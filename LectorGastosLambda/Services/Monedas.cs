using Amazon.Lambda.Core;
using LectorGastosLambda.Models.DataBase;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace LectorGastosLambda.Services
{
    public static class Monedas
    {
        // Si la API de tipo de cambio cae, se usa este valor de respaldo
        public const decimal TipoCambioRespaldo = 3.75m;
        private static readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        public static async Task<decimal> ObtenerUsdPenAsync(ILambdaLogger logger)
        {
            try
            {
                var respuesta = await httpClient.GetStringAsync("https://open.er-api.com/v6/latest/USD");
                using var document = JsonDocument.Parse(respuesta);
                return document.RootElement.GetProperty("rates").GetProperty("PEN").GetDecimal();
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Error obteniendo tipo de cambio, se usa el respaldo {TipoCambioRespaldo}: {ex.Message}");
                return TipoCambioRespaldo;
            }
        }

        // Congela el tipo de cambio del día: deja Moneda, TipoCambio y MontoPEN consistentes
        public static async Task NormalizarAsync(Gasto gasto, ILambdaLogger logger)
        {
            if (string.Equals(gasto.Moneda, "USD", StringComparison.OrdinalIgnoreCase))
            {
                gasto.Moneda = "USD";
                gasto.TipoCambio = await ObtenerUsdPenAsync(logger);
                gasto.MontoPEN = Math.Round(gasto.Monto * gasto.TipoCambio.Value, 2);
            }
            else
            {
                gasto.Moneda = "PEN";
                gasto.TipoCambio = 1m;
                gasto.MontoPEN = gasto.Monto;
            }
        }
    }
}
