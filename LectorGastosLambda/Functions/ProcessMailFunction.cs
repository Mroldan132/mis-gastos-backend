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
using LectorGastosLambda.DTOs.Responses;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
namespace LectorGastosLambda.Functions
{
    public class ProcessMailFunction
    {
        private static readonly AmazonDynamoDBClient dynamoClient = new AmazonDynamoDBClient(Amazon.RegionEndpoint.USEast2);
        private static readonly DynamoDBContext dbContext = new DynamoDBContext(dynamoClient);
        private static readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Máximo de llamadas simultáneas a Gemini, para no chocar con el límite de peticiones (429)
        private static readonly SemaphoreSlim limiteGemini = new SemaphoreSlim(3);
        private const int MaxReintentos = 3;

        // Se activa cuando Gemini pide esperar más de un minuto (cuota diaria agotada): no tiene sentido seguir llamando
        private static volatile bool cuotaAgotada;
        private const string Carpeta = "GastosApp";

        public async Task<string> FunctionHandler(object input, ILambdaContext context)
        {
            var geminiApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            var geminiModel = Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-flash-latest";

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

                var jsonGasto = await LlamarGeminiAsync(textoLimpio, apiKey, model, logger);
                if (jsonGasto == null) return null;

                var record = JsonSerializer.Deserialize<Gasto>(jsonGasto);
                if (record == null)
                {
                    logger.LogWarning($"Gemini devolvió un JSON vacío: {jsonGasto}");
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

                // ====================================================
                // LÓGICA MULTIMONEDA: CONGELAR TIPO DE CAMBIO
                // ====================================================
                if (string.Equals(record.Moneda, "USD", StringComparison.OrdinalIgnoreCase))
                {
                    record.Moneda = "USD";
                    try
                    {
                        var responseTc = await httpClient.GetStringAsync("https://open.er-api.com/v6/latest/USD");
                        using var document = JsonDocument.Parse(responseTc);
                        var rate = document.RootElement.GetProperty("rates").GetProperty("PEN").GetDecimal();

                        record.TipoCambio = rate;
                        record.MontoPEN = Math.Round(record.Monto * rate, 2);
                        logger.LogLine($"Gasto en USD detectado. TC aplicado: {rate}. MontoPEN: {record.MontoPEN}");
                    }
                    catch (Exception ex)
                    {
                        // Si la API de moneda cae, usamos un TC seguro de respaldo
                        logger.LogWarning($"Error obteniendo TC en vivo, usando respaldo. Error: {ex.Message}");
                        record.TipoCambio = 3.75m;
                        record.MontoPEN = Math.Round(record.Monto * 3.75m, 2);
                    }
                }
                else
                {
                    record.Moneda = "PEN";
                    record.TipoCambio = 1m;
                    record.MontoPEN = record.Monto;
                }

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

        // Devuelve el JSON del gasto o null. Reintenta con espera creciente ante 429 (cuota) y 5xx (Gemini caído).
        private async Task<string?> LlamarGeminiAsync(string textoLimpio, string apiKey, string model, ILambdaLogger logger)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

            var requestBody = new
            {
                contents = new[] { new { parts = new[] { new { text = "Extrae los datos de este ticket de gasto: " + textoLimpio } } } },
                generationConfig = new
                {
                    temperature = 0.0,
                    responseMimeType = "application/json",
                    responseSchema = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            Banco = new { type = "STRING" },
                            Monto = new { type = "NUMBER" },
                            Moneda = new { type = "STRING", description = "PEN o USD" },
                            FechaOperacion = new { type = "STRING", description = "YYYY-MM-DD" },
                            Comercio = new { type = "STRING" },
                            Categoria = new { type = "STRING" }
                        },
                        required = new[] { "Banco", "Monto", "Moneda", "FechaOperacion", "Comercio", "Categoria" }
                    }
                }
            };
            var cuerpo = JsonSerializer.Serialize(requestBody);

            for (int intento = 1; intento <= MaxReintentos; intento++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(cuerpo, Encoding.UTF8, "application/json");
                request.Headers.Add("x-goog-api-key", apiKey);

                using var response = await httpClient.SendAsync(request);
                var resString = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var aiData = JsonSerializer.Deserialize<GeminiResponse>(resString);
                    var texto = aiData?.candidates?.FirstOrDefault()?.content?.parts?.FirstOrDefault()?.text;
                    if (string.IsNullOrWhiteSpace(texto))
                    {
                        logger.LogWarning($"Gemini respondió sin contenido: {Recortar(resString)}");
                        return null;
                    }
                    return texto;
                }

                var reintentable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                logger.LogError($"Gemini respondió {(int)response.StatusCode} (intento {intento}/{MaxReintentos}): {Recortar(resString)}");

                if (response.StatusCode == HttpStatusCode.TooManyRequests && SegundosDeEspera(resString) > 60)
                {
                    cuotaAgotada = true;
                    logger.LogWarning("Cuota de Gemini agotada; los correos restantes quedan como no leídos para la próxima ejecución.");
                    return null;
                }

                if (!reintentable || intento == MaxReintentos) return null;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, intento)));
            }
            return null;
        }

        // Lee el "retryDelay": "20000s" que Gemini incluye en sus errores 429
        private static double SegundosDeEspera(string respuesta)
        {
            var match = Regex.Match(respuesta, @"""retryDelay""\s*:\s*""(\d+(?:\.\d+)?)s""");
            return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        private static string Recortar(string texto) => texto.Length > 500 ? texto.Substring(0, 500) + "..." : texto;

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
