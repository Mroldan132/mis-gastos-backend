using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using LectorGastosLambda.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace LectorGastosLambda.Functions
{
    public class DashboardFunction
    {
        private static readonly AmazonDynamoDBClient _dynamoClient = new AmazonDynamoDBClient(Amazon.RegionEndpoint.USEast2);
        private static readonly DynamoDBContext _dbContext = new DynamoDBContext(_dynamoClient);

        public async Task<APIGatewayHttpApiV2ProxyResponse> DashboardHandler(APIGatewayHttpApiV2ProxyRequest apiRequest, ILambdaContext context)
        {
            var httpMethod = apiRequest.RequestContext.Http.Method.ToUpper();
            if (httpMethod != "GET")
            {
                return CrearRespuesta(405, new { Success = false, Message = "Método no permitido. Solo GET." });
            }

            try
            {
                apiRequest.QueryStringParameters.TryGetValue("userId", out string userId);
                apiRequest.QueryStringParameters.TryGetValue("mes", out string mes); // Formato esperado: "2026-05"

                if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(mes))
                    return CrearRespuesta(400, new { Success = false, Message = "Los parámetros 'userId' y 'mes' son obligatorios." });

                context.Logger.LogLine($"Calculando Dashboard para usuario {userId} en {mes}");

                // 1. Configuración de búsquedas (El truco está aquí)

                // Para INGRESOS: Filtramos directamente en DynamoDB porque no tienen la complejidad de las cuotas.
                var configIngresos = new DynamoDBOperationConfig
                {
                    QueryFilter = new List<ScanCondition>
            {
                new ScanCondition("FechaOperacion", ScanOperator.BeginsWith, mes)
            }
                };

                // Para GASTOS: Traemos todos (sin filtro) para poder procesar la lógica de fechas en memoria.
                var configGastos = new DynamoDBOperationConfig();

                // 2. Disparamos las dos búsquedas AL MISMO TIEMPO
                var busquedaIngresos = _dbContext.QueryAsync<Ingreso>(userId, configIngresos).GetRemainingAsync();
                var busquedaGastos = _dbContext.QueryAsync<Gasto>(userId, configGastos).GetRemainingAsync();

                // Esperamos a que ambas terminen
                await Task.WhenAll(busquedaIngresos, busquedaGastos);

                var listaIngresos = busquedaIngresos.Result;
                var listaGastosTotales = busquedaGastos.Result;

                // 3. Procesamos los gastos en memoria (LINQ)
                var gastosDelMes = listaGastosTotales.Where(g =>
                    !g.EsGastoReferencia &&
                    (
                        // Gastos normales del mes
                        (!g.EsPagoDeCuota && !string.IsNullOrEmpty(g.FechaOperacion) && g.FechaOperacion.StartsWith(mes))
                        ||
                        // Cuotas pagadas en este mes
                        (g.EsPagoDeCuota && g.Estado == "Pagado" && !string.IsNullOrEmpty(g.FechaActualizacion) && g.FechaActualizacion.StartsWith(mes))
                    )
                ).ToList();

                // 4. Cálculos finales
                decimal totalIngresos = listaIngresos.Sum(i => i.Monto);

                // Ahora usamos "gastosDelMes" en lugar de la lista total, así evitamos sumar cuotas no pagadas
                decimal totalGastos = gastosDelMes.Sum(g => g.MontoPEN > 0 ? g.MontoPEN : g.Monto);

                decimal balanceFinal = totalIngresos - totalGastos;

                var gastosPorCategoria = gastosDelMes
                    .GroupBy(g => g.Categoria)
                    .Select(g => new {
                        Categoria = g.Key,
                        Total = g.Sum(x => x.MontoPEN > 0 ? x.MontoPEN : x.Monto)
                    })
                    .OrderByDescending(g => g.Total)
                    .ToList();

                // 5. Devolvemos el reporte completo
                return CrearRespuesta(200, new
                {
                    Success = true,
                    Mes = mes,
                    Resumen = new
                    {
                        Ingresos = totalIngresos,
                        Gastos = totalGastos,
                        Balance = balanceFinal
                    },
                    DesgloseCategorias = gastosPorCategoria
                });
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"Error en Dashboard: {ex.Message}");
                return CrearRespuesta(500, new { Success = false, Message = "Error interno calculando el dashboard." });
            }
        }
        private APIGatewayHttpApiV2ProxyResponse CrearRespuesta(int statusCode, object bodyResponse)
        {
            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Body = JsonSerializer.Serialize(bodyResponse),
                Headers = new Dictionary<string, string>
                {
                    { "Content-Type", "application/json" },
                    { "Access-Control-Allow-Origin", "*" }
                }
            };
        }
    }
}