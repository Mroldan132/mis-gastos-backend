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
                var usuarios = await dbContext.ScanAsync<UsuarioConfig>(new List<ScanCondition>()).GetRemainingAsync();
                if (usuarios.Count == 0)
                {
                    context.Logger.LogLine("No se encontraron usuarios en la tabla ConfiguracionUsuarios.");
                    return "Sin usuarios";
                }

                int totalGuardados = 0, totalFallidos = 0;

                // 1. Buzón dedicado: todos los usuarios le reenvían sus notificaciones
                if (Buzon.Configurado)
                {
                    context.Logger.LogLine($"--- Procesando buzón {Buzon.Email} ---");
                    try
                    {
                        var (guardados, fallidos) = await ProcesarCuentaAsync(Buzon.Email!, Buzon.Password!, "INBOX", "buzón",
                            m => Buzon.IdentificarUsuario(m, usuarios)?.UserId, geminiApiKey, geminiModel, context.Logger);
                        totalGuardados += guardados;
                        totalFallidos += fallidos;
                    }
                    catch (AuthenticationException ex)
                    {
                        context.Logger.LogError($"Gmail rechazó el login del buzón {Buzon.Email} (¿contraseña de aplicación revocada?): {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        context.Logger.LogError($"Error procesando el buzón {Buzon.Email}: {ex}");
                    }
                }
                else
                {
                    context.Logger.LogWarning("BUZON_EMAIL / BUZON_APP_PASSWORD no configurados: solo se leen las cuentas personales.");
                }

                // 2. Sistema anterior: cuentas personales con contraseña de aplicación y etiqueta GastosApp.
                //    Al borrar el AppPassword de un usuario en la tabla, deja de leerse su correo personal.
                foreach (var usuario in usuarios.Where(u => !string.IsNullOrEmpty(u.Email) && !string.IsNullOrEmpty(u.AppPassword)))
                {
                    var userNombre = usuario.Nombre ?? usuario.Email;
                    var userId = usuario.UserId ?? usuario.Email;
                    context.Logger.LogLine($"--- Procesando cuenta personal: {userNombre} ({usuario.Email}) ---");

                    try
                    {
                        var (guardados, fallidos) = await ProcesarCuentaAsync(usuario.Email, usuario.AppPassword, Carpeta, userNombre,
                            _ => userId, geminiApiKey, geminiModel, context.Logger);
                        totalGuardados += guardados;
                        totalFallidos += fallidos;
                    }
                    catch (AuthenticationException ex)
                    {
                        context.Logger.LogError($"Gmail rechazó el login de {usuario.Email} (¿contraseña de aplicación revocada?): {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        context.Logger.LogError($"Error procesando cuenta {usuario.Email}: {ex}");
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

        // Lee los correos no leídos de una carpeta. resolverUsuario dice a quién pertenece cada correo (null = de nadie conocido).
        private async Task<(int guardados, int fallidos)> ProcesarCuentaAsync(string email, string password, string nombreCarpeta, string etiqueta,
            Func<MimeMessage, string?> resolverUsuario, string apiKey, string model, ILambdaLogger logger)
        {
            using var client = new ImapClient();
            await client.ConnectAsync("imap.gmail.com", 993, true);
            await client.AuthenticateAsync(email, password);

            IMailFolder carpeta;
            try
            {
                carpeta = nombreCarpeta == "INBOX" ? client.Inbox : await client.GetFolderAsync(nombreCarpeta);
            }
            catch (FolderNotFoundException)
            {
                logger.LogWarning($"Etiqueta '{nombreCarpeta}' no encontrada para {etiqueta}");
                await client.DisconnectAsync(true);
                return (0, 0);
            }

            await carpeta.OpenAsync(FolderAccess.ReadWrite);
            var uids = await carpeta.SearchAsync(SearchQuery.NotSeen);
            logger.LogLine($"Correos nuevos en {etiqueta}: {uids.Count}");

            // Una conexión IMAP no admite operaciones en paralelo: los correos se descargan en serie
            var textos = new List<(UniqueId uid, string userId, string texto)>();
            foreach (var uid in uids)
            {
                var message = await carpeta.GetMessageAsync(uid);
                if (Buzon.EsConfirmacionDeReenvio(message))
                {
                    logger.LogLine($"Correo {uid}: confirmación de reenvío de Gmail, se deja sin leer para copiar el código.");
                    continue;
                }

                var userId = resolverUsuario(message);
                if (userId == null)
                {
                    // Queda sin leer: si el usuario se registra después, se procesa en la siguiente ejecución
                    logger.LogWarning($"Correo {uid} en {etiqueta} de '{message.From}' (asunto '{message.Subject}'): no se reconoce a qué usuario pertenece.");
                    continue;
                }
                textos.Add((uid, userId, LimpiarContenido(message.HtmlBody ?? message.TextBody)));
            }

            var tareasIA = textos.ToDictionary(t => t.uid, t => ProcesarTicketConIAAsync(t.texto, t.userId, apiKey, model, logger));
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
                    logger.LogWarning($"Correo {uid} de {etiqueta} no se pudo procesar; queda como no leído.");
                    continue;
                }

                try
                {
                    record.FechaOperacion = await GenerarFechaUnicaAsync(record.UsuarioID, record.FechaOperacion, clavesUsadas);
                    await dbContext.SaveAsync(record);
                    await carpeta.AddFlagsAsync(uid, MessageFlags.Seen, true);
                    guardados++;
                    logger.LogLine($"Gasto guardado: {record.Comercio} ({record.Moneda} {record.Monto}) para {record.UsuarioID} desde {etiqueta}");
                }
                catch (Exception ex)
                {
                    fallidos++;
                    logger.LogError($"Error guardando gasto del correo {uid} de {etiqueta}: {ex.Message}");
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

                var instruccion =
                    "Extrae los datos de este ticket de gasto de un banco peruano. " +
                    "Categoria: exactamente una de esta lista, según el comercio y lo que se compró " +
                    "(si el comercio no deja claro qué es, usa Otros):\n" + Categorias.ParaIA() + "\nTicket:";
                var resultado = await GeminiClient.ExtraerGastoAsync(instruccion, textoLimpio, apiKey, model, logger);
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
