using Amazon.Lambda.Core;
using LectorGastosLambda.DTOs.Responses;
using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LectorGastosLambda.Services
{
    public record ResultadoGemini(string? Json, bool CuotaAgotada);

    // Extrae los datos de un gasto (Banco, Monto, Moneda, FechaOperacion, Comercio, Categoria) a partir de texto libre
    public static class GeminiClient
    {
        public const string ModeloPorDefecto = "gemini-flash-latest";
        private const int MaxReintentos = 3;
        private static readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        public static string? ApiKey => Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        public static string Modelo => Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? ModeloPorDefecto;

        // Reintenta con espera creciente ante 429 y 5xx. Si Gemini pide esperar más de un minuto, la cuota diaria se agotó.
        public static async Task<ResultadoGemini> ExtraerGastoAsync(string instruccion, string texto, string apiKey, string model, ILambdaLogger logger)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

            var requestBody = new
            {
                contents = new[] { new { parts = new[] { new { text = instruccion + "\n\n" + texto } } } },
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
                    var json = aiData?.candidates?.FirstOrDefault()?.content?.parts?.FirstOrDefault()?.text;
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        logger.LogWarning($"Gemini respondió sin contenido: {Recortar(resString)}");
                        return new ResultadoGemini(null, false);
                    }
                    return new ResultadoGemini(json, false);
                }

                logger.LogError($"Gemini respondió {(int)response.StatusCode} (intento {intento}/{MaxReintentos}): {Recortar(resString)}");

                if (response.StatusCode == HttpStatusCode.TooManyRequests && SegundosDeEspera(resString) > 60)
                    return new ResultadoGemini(null, true);

                var reintentable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (!reintentable || intento == MaxReintentos) return new ResultadoGemini(null, false);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, intento)));
            }
            return new ResultadoGemini(null, false);
        }

        // Lee el "retryDelay": "20000s" que Gemini incluye en sus errores 429
        private static double SegundosDeEspera(string respuesta)
        {
            var match = Regex.Match(respuesta, @"""retryDelay""\s*:\s*""(\d+(?:\.\d+)?)s""");
            return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        private static string Recortar(string texto) => texto.Length > 500 ? texto.Substring(0, 500) + "..." : texto;
    }
}
