using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using LectorGastosLambda.Models.DataBase;
using LectorGastosLambda.Security;
using LectorGastosLambda.Services;
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
            if (httpMethod == "OPTIONS") return JwtAuth.RespuestaPreflight();
            if (httpMethod != "GET")
            {
                return CrearRespuesta(405, new { Success = false, Message = "Método no permitido. Solo GET." });
            }

            // El usuario sale del token, nunca de la URL
            var userId = JwtAuth.ObtenerUserId(apiRequest, context.Logger);
            if (userId == null)
                return CrearRespuesta(401, new { Success = false, Message = "No autorizado. Inicia sesión de nuevo." });

            try
            {
                string? mes = null; // Formato esperado: "2026-05"
                apiRequest.QueryStringParameters?.TryGetValue("mes", out mes);

                if (!ReglasGastos.EsMesValido(mes))
                    return CrearRespuesta(400, new { Success = false, Message = "El parámetro 'mes' es obligatorio." });

                context.Logger.LogLine($"Calculando Dashboard para usuario {userId} en {mes}");

                // 1. Traemos todos los ingresos y gastos del usuario AL MISMO TIEMPO (se filtran en memoria
                //    porque las cuotas cuentan en el mes en que se pagan y hace falta el historial para la tendencia)
                var busquedaIngresos = _dbContext.QueryAsync<Ingreso>(userId, new QueryConfig()).GetRemainingAsync();
                var busquedaGastos = _dbContext.QueryAsync<Gasto>(userId, new QueryConfig()).GetRemainingAsync();
                await Task.WhenAll(busquedaIngresos, busquedaGastos);

                var todosLosIngresos = busquedaIngresos.Result;
                var listaGastosTotales = busquedaGastos.Result;

                decimal IngresosDe(string m) => todosLosIngresos.Where(i => (i.FechaOperacion ?? "").StartsWith(m)).Sum(i => i.Monto);
                List<Gasto> GastosDe(string m) => listaGastosTotales.Where(g => ReglasGastos.CuentaEnMes(g, m)).ToList();

                // 2. Mes solicitado (normales del mes + cuotas pagadas en el mes, sin los gastos fraccionados)
                var gastosDelMes = GastosDe(mes);
                decimal totalIngresos = IngresosDe(mes);
                decimal totalGastos = gastosDelMes.Sum(ReglasGastos.MontoEnSoles);
                decimal balanceFinal = totalIngresos - totalGastos;

                var gastosPorCategoria = gastosDelMes
                    .GroupBy(g => g.Categoria)
                    .Select(g => new { Categoria = g.Key, Total = g.Sum(ReglasGastos.MontoEnSoles) })
                    .OrderByDescending(g => g.Total)
                    .ToList();

                // 3. Comparativa por categoría contra el mes anterior
                var mesAnterior = ReglasGastos.MesAnterior(mes);
                var anteriorPorCategoria = ReglasGastos.TotalesPorCategoria(GastosDe(mesAnterior));
                var comparativa = gastosPorCategoria
                    .Select(c => c.Categoria ?? "Otros")
                    .Union(anteriorPorCategoria.Keys)
                    .Where(c => c != ReglasGastos.CategoriaAhorro)
                    .Select(c =>
                    {
                        var actual = gastosPorCategoria.FirstOrDefault(x => x.Categoria == c)?.Total ?? 0;
                        var anterior = anteriorPorCategoria.GetValueOrDefault(c);
                        return new
                        {
                            Categoria = c,
                            Actual = actual,
                            Anterior = anterior,
                            Variacion = anterior > 0 ? Math.Round((actual - anterior) / anterior * 100, 1) : (decimal?)null
                        };
                    })
                    .OrderByDescending(c => c.Actual)
                    .ToList();

                // 4. Tendencia de los últimos 6 meses (incluido el solicitado)
                var tendencia = Enumerable.Range(0, 6).Reverse()
                    .Select(i => ReglasGastos.MesAnterior(mes, i))
                    .Select(m =>
                    {
                        var gastos = GastosDe(m);
                        var ahorro = gastos.Where(ReglasGastos.EsAhorro).Sum(ReglasGastos.MontoEnSoles);
                        return new { Mes = m, Ingresos = IngresosDe(m), Gastos = gastos.Sum(ReglasGastos.MontoEnSoles) - ahorro, Ahorro = ahorro };
                    })
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
                    DesgloseCategorias = gastosPorCategoria,
                    Comparativa = new
                    {
                        MesAnterior = mesAnterior,
                        GastosMesAnterior = GastosDe(mesAnterior).Where(g => !ReglasGastos.EsAhorro(g)).Sum(ReglasGastos.MontoEnSoles),
                        Categorias = comparativa
                    },
                    Tendencia = tendencia,
                    // Gastos leídos por la IA que el usuario todavía no confirmó o corrigió
                    PorRevisar = listaGastosTotales.Count(g => g.Origen == "Correo" && !g.Revisado)
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