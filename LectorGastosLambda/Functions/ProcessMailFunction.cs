using Amazon.Lambda.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.Model;
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
        private static readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };

        public async Task<string> FunctionHandler(object input, ILambdaContext context)
        {
            var geminiApiKey = "AIzaSyC2GjI713vSINfdo--nq_a4T3L71r_cnX8";

            try
            {
                context.Logger.LogLine("Consultando tabla de usuarios...");

                var scanRequest = new ScanRequest { TableName = "ConfiguracionUsuarios" };
                var scanResponse = await dynamoClient.ScanAsync(scanRequest);

                if (scanResponse.Items.Count == 0)
                {
                    context.Logger.LogLine("No se encontraron usuarios en la tabla ConfiguracionUsuarios.");
                    return "Sin usuarios";
                }

                foreach (var item in scanResponse.Items)
                {
                    var userEmail = item["Email"].S;
                    var userPass = item["AppPassword"].S;
                    var userNombre = item.ContainsKey("Nombre") ? item["Nombre"].S : userEmail;
                    var userId = item.ContainsKey("UserId") ? item["UserId"].S : userEmail;

                    context.Logger.LogLine($"--- Procesando cuenta: {userNombre} ({userEmail}) ---");

                    try
                    {
                        using (var client = new ImapClient())
                        {
                            await client.ConnectAsync("imap.gmail.com", 993, true);
                            await client.AuthenticateAsync(userEmail, userPass);

                            var carpeta = await client.GetFolderAsync("GastosApp");
                            if (carpeta == null)
                            {
                                context.Logger.LogLine($"Etiqueta 'GastosApp' no encontrada para {userNombre}");
                                continue;
                            }

                            await carpeta.OpenAsync(FolderAccess.ReadWrite);
                            var uids = await carpeta.SearchAsync(SearchQuery.NotSeen);
                            context.Logger.LogLine($"Correos nuevos para {userNombre}: {uids.Count}");

                            var tareasIA = new Dictionary<UniqueId, Task<Gasto>>();

                            foreach (var uid in uids)
                            {
                                var message = await carpeta.GetMessageAsync(uid);
                                var textoLimpio = LimpiarContenido(message.HtmlBody ?? message.TextBody);

                                // Pasamos el 'userNombre' y el 'userId' ("1" o "2")
                                var tarea = ProcesarTicketConIAAsync(textoLimpio, userNombre, userId, geminiApiKey, context.Logger);
                                tareasIA.Add(uid, tarea);
                            }

                            await Task.WhenAll(tareasIA.Values);

                            foreach (var kvp in tareasIA)
                            {
                                var uid = kvp.Key;
                                var record = await kvp.Value;

                                if (record != null)
                                {
                                    await dbContext.SaveAsync(record);
                                    await carpeta.AddFlagsAsync(uid, MessageFlags.Seen, true);
                                    context.Logger.LogLine($"Gasto Guardado: {record.Comercio} para {userNombre}");
                                }
                            }
                            await client.DisconnectAsync(true);
                        }
                    }
                    catch (Exception ex)
                    {
                        context.Logger.LogLine($"Error procesando cuenta {userEmail}: {ex.Message}");
                    }
                }
                return "Proceso completado.";
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"Error Crítico: {ex.Message}");
                return "Error";
            }
        }

        private async Task<Gasto> ProcesarTicketConIAAsync(string textoLimpio, string nombreUsuario, string userId, string apiKey, ILambdaLogger logger)
        {
            try
            {
                var model = "gemini-flash-latest";
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

                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                request.Headers.Add("x-goog-api-key", apiKey);

                var response = await httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var resString = await response.Content.ReadAsStringAsync();
                    var aiData = JsonSerializer.Deserialize<GeminiResponse>(resString);

                    if (aiData?.candidates != null && aiData.candidates.Length > 0)
                    {
                        var jsonLimpio = aiData.candidates[0].content.parts[0].text;
                        var record = JsonSerializer.Deserialize<Gasto>(jsonLimpio);

                        // --- ESTANDARIZACIÓN PARA LA APP WEB ---
                        record.UsuarioID = userId;

                        // Añade la hora UTC actual a la fecha del ticket para cumplir formato y evitar colisiones
                        var horaExacta = DateTime.UtcNow.ToString("HH:mm:ss");
                        record.FechaOperacion = $"{record.FechaOperacion}T{horaExacta}";

                        // Asigna los campos nuevos que requiere la aplicación de React
                        record.GastoId = Guid.NewGuid().ToString();
                        record.EsPagoDeCuota = false;
                        record.CuotaIdRelacionada = "";

                        // ====================================================
                        // LÓGICA MULTIMONEDA: CONGELAR TIPO DE CAMBIO
                        // ====================================================
                        if (record.Moneda != null && record.Moneda.ToUpper() == "USD")
                        {
                            try
                            {
                                // Reusamos tu httpClient para traer el TC actual
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
                                logger.LogLine($"Error obteniendo TC en vivo, usando respaldo. Error: {ex.Message}");
                                record.TipoCambio = 3.75m;
                                record.MontoPEN = Math.Round(record.Monto * 3.75m, 2);
                            }
                        }
                        else
                        {
                            // Si es PEN, el monto original es el mismo que el MontoPEN
                            record.Moneda = "PEN"; // Aseguramos que siempre diga PEN
                            record.TipoCambio = 1m;
                            record.MontoPEN = record.Monto;
                        }

                        return record;
                    }
                }
            }
            catch (Exception ex) { logger.LogLine($"Error IA: {ex.Message}"); }
            return null;
        }

        private string LimpiarContenido(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            var text = Regex.Replace(html, @"<(style|script)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]*>", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length > 1000 ? text.Substring(0, 1000) : text;
        }
    }
}