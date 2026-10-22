using Amazon.Lambda.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.Model;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using LectorGastosLambda.Models.DataBase;
using LectorGastosLambda.Services;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
namespace LectorGastosLambda.Functions
{
    public class ProcessMailFunction
    {
        private static readonly AmazonDynamoDBClient dynamoClient = new AmazonDynamoDBClient(Amazon.RegionEndpoint.USEast2);
        private static readonly DynamoDBContext dbContext = new DynamoDBContext(dynamoClient);

        // Máximo de llamadas simultáneas a Gemini, para no chocar con el límite de peticiones (429)
        private static readonly SemaphoreSlim limiteGemini = new SemaphoreSlim(3);

        // Se activa cuando Gemini pide esperar más de un minuto (cuota diaria agotada): no tiene sentido seguir llamando
        private static volatile bool cuotaAgotada;
        private const string Carpeta = "GastosApp";

        public async Task<string> FunctionHandler(object input, ILambdaContext context)
        {
            var geminiApiKey = GeminiClient.ApiKey;
            var geminiModel = GeminiClient.Modelo;

            if (string.IsNullOrWhiteSpace(geminiApiKey))
            {
                context.Logger.LogError("Falta la variable de entorno GEMINI_API_KEY. No se procesará ningún correo.");
                return "Error: falta GEMINI_API_KEY";
            }

            cuotaAgotada = false;

            try
            {
                context.Logger.LogLine("Consultando tabla de usuarios...");

                var scanResponse = await dynamoClient.ScanAsync(new ScanRequest { TableName = "ConfiguracionUsuarios" });

                if (scanResponse.Items.Count == 0)
                {
                    context.Logger.LogLine("No se encontraron usuarios en la tabla ConfiguracionUsuarios.");
                    return "Sin usuarios";
                }

                int totalGuardados = 0, totalFallidos = 0;

                foreach (var item in scanResponse.Items)
                {
                    if (!item.ContainsKey("Email") || !item.ContainsKey("AppPassword"))
                    {
                        context.Logger.LogWarning("Usuario sin Email o AppPassword en ConfiguracionUsuarios, se omite.");
                        continue;
                    }

                    var userEmail = item["Email"].S;
                    var userPass = item["AppPassword"].S;
                    var userNombre = item.ContainsKey("Nombre") ? item["Nombre"].S : userEmail;
                    var userId = item.ContainsKey("UserId") ? item["UserId"].S : userEmail;

                    context.Logger.LogLine($"--- Procesando cuenta: {userNombre} ({userEmail}) ---");

                    try
                    {
                        var (guardados, fallidos) = await ProcesarCuentaAsync(userEmail, userPass, userNombre, userId, geminiApiKey, geminiModel, context.Logger);
                        totalGuardados += guardados;
                        totalFallidos += fallidos;
                    }
                    catch (AuthenticationException ex)
                    {
                        context.Logger.LogError($"Gmail rechazó el login de {userEmail} (¿contraseña de aplicación revocada?): {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        context.Logger.LogError($"Error procesando cuenta {userEmail}: {ex}");
                    }
                }

                context.Logger.LogLine($"Resumen: {totalGuardados} gastos guardados, {totalFallidos} correos fallidos.");
                return $"Proceso completado. Guardados: {totalGuardados}, fallidos: {totalFallidos}.";
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Error crítico: {ex}");
                return "Error";
            }
        }

        private async Task<(int guardados, int fallidos)> ProcesarCuentaAsync(string userEmail, string userPass, string userNombre, string userId, string apiKey, string model, ILambdaLogger logger)
        {
            using var client = new ImapClient();
            await client.ConnectAsync("imap.gmail.com", 993, true);
            await client.AuthenticateAsync(userEmail, userPass);

            IMailFolder carpeta;
            try
            {
                carpeta = await client.GetFolderAsync(Carpeta);
            }
            catch (FolderNotFoundException)
            {
                logger.LogWarning($"Etiqueta '{Carpeta}' no encontrada para {userNombre}");
                await client.DisconnectAsync(true);
                return (0, 0);
            }

            await carpeta.OpenAsync(FolderAccess.ReadWrite);
            var uids = await carpeta.SearchAsync(SearchQuery.NotSeen);
            logger.LogLine($"Correos nuevos para {userNombre}: {uids.Count}");

            // Una conexión IMAP no admite operaciones en paralelo: los correos se descargan en serie
            var textos = new List<(UniqueId uid, string texto)>();
            foreach (var uid in uids)
            {
                var message = await carpeta.GetMessageAsync(uid);
                textos.Add((uid, LimpiarContenido(message.HtmlBody ?? message.TextBody)));
            }

            var tareasIA = textos.ToDictionary(t => t.uid, t => ProcesarTicketConIAAsync(t.texto, userId, apiKey, model, logger));
            await Task.WhenAll(tareasIA.Values);

            int guardados = 0, fallidos = 0;
            var clavesUsadas = new HashSet<string>();

            foreach (var (uid, tarea) in tareasIA)
            {
                var record = await tarea;
                if (record == null)
                {
                    // Se deja como no leído para reintentarlo en la próxima ejecución
                    fallidos++;
                    logger.LogWarning($"Correo {uid} de {userNombre} no se pudo procesar; queda como no leído.");
                    continue;
                }

                try
                {
                    record.FechaOperacion = await GenerarFechaUnicaAsync(record.UsuarioID, record.FechaOperacion, clavesUsadas);
                    await dbContext.SaveAsync(record);
                    await carpeta.AddFlagsAsync(uid, MessageFlags.Seen, true);
                    guardados++;
                    logger.LogLine($"Gasto guardado: {record.Comercio} ({record.Moneda} {record.Monto}) para {userNombre}");
                }
                catch (Exception ex)
                {
                    fallidos++;
                    logger.LogError($"Error guardando gasto del correo {uid} de {userNombre}: {ex.Message}");
                }
            }

            await client.DisconnectAsync(true);
            return (guardados, fallidos);
        }

        // FechaOperacion es la sort key: si dos tickets caen en el mismo segundo, el segundo sobrescribiría al primero.
        // Se avanza un segundo hasta encontrar una clave libre (en esta ejecución y en DynamoDB).
        private async Task<string> GenerarFechaUnicaAsync(string userId, string fechaTicket, HashSet<string> clavesUsadas)
        {
            var ahora = DateTime.UtcNow;
            var fecha = DateTime.ParseExact(fechaTicket, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                .Add(new TimeSpan(ahora.Hour, ahora.Minute, ahora.Second));

            while (true)
            {
                var clave = fecha.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                if (clavesUsadas.Add(clave) && await dbContext.LoadAsync<Gasto>(userId, clave) == null)
                    return clave;
                fecha = fecha.AddSeconds(1);
            }
        }

        private async Task<Gasto?> ProcesarTicketConIAAsync(string textoLimpio, string userId, string apiKey, string model, ILambdaLogger logger)
        {
            if (string.IsNullOrWhiteSpace(textoLimpio))
            {
                logger.LogWarning("Correo sin contenido legible, se omite.");
                return null;
            }

            await limiteGemini.WaitAsync();
            try
            {
                if (cuotaAgotada) return null;

                var resultado = await GeminiClient.ExtraerGastoAsync("Extrae los datos de este ticket de gasto:", textoLimpio, apiKey, model, logger);
                if (resultado.CuotaAgotada)
                {
                    cuotaAgotada = true;
                    logger.LogWarning("Cuota de Gemini agotada; los correos restantes quedan como no leídos para la próxima ejecución.");
                    return null;
                }
                if (resultado.Json == null) return null;

                var record = JsonSerializer.Deserialize<Gasto>(resultado.Json);
                if (record == null)
                {
                    logger.LogWarning($"Gemini devolvió un JSON vacío: {resultado.Json}");
                    return null;
                }

                // --- ESTANDARIZACIÓN PARA LA APP WEB ---
                record.UsuarioID = userId;

                // Si Gemini no devuelve una fecha válida, se usa la de hoy
                if (!DateTime.TryParseExact(record.FechaOperacion, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    logger.LogWarning($"Fecha inválida de Gemini '{record.FechaOperacion}', se usa la fecha actual.");
                    record.FechaOperacion = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }

                record.GastoId = Guid.NewGuid().ToString();
                record.EsPagoDeCuota = false;
                record.CuotaIdRelacionada = "";
                record.Origen = "Correo";
                record.Revisado = false; // El usuario lo confirma o corrige desde la app

                // Congela el tipo de cambio del día para los gastos en dólares
                await Monedas.NormalizarAsync(record, logger);

                return record;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error IA: {ex.Message}");
                return null;
            }
            finally
            {
                limiteGemini.Release();
            }
        }

        private string LimpiarContenido(string? html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            var text = Regex.Replace(html, @"<(style|script)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]*>", " ");
            text = WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length > 1000 ? text.Substring(0, 1000) : text;
        }
    }
}
