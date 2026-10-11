using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using LectorGastosLambda.DTOs.Requests;
using LectorGastosLambda.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace LectorGastosLambda.Functions
{
    public class GastosFunction
    {
        private static readonly AmazonDynamoDBClient _dynamoClient = new AmazonDynamoDBClient(Amazon.RegionEndpoint.USEast2);
        private static readonly DynamoDBContext _dbContext = new DynamoDBContext(_dynamoClient);

        public async Task<APIGatewayHttpApiV2ProxyResponse> GastosMasterHandler(APIGatewayHttpApiV2ProxyRequest apiRequest, ILambdaContext context)
        {
            var httpMethod = apiRequest.RequestContext.Http.Method.ToUpper();

            context.Logger.LogLine($"Recibida petición {httpMethod} en la ruta de Gastos");

            try
            {
                switch (httpMethod)
                {
                    case "POST":
                        return await CrearGasto(apiRequest, context);
                    case "GET":
                        return await ObtenerGastos(apiRequest, context);
                    default:
                        return CrearRespuesta(405, new { Success = false, Message = $"Método {httpMethod} no permitido." });
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"Error general en GastosFunction: {ex.Message}");
                return CrearRespuesta(500, new { Success = false, Message = "Error interno del servidor." });
            }
        }

        private async Task<APIGatewayHttpApiV2ProxyResponse> CrearGasto(APIGatewayHttpApiV2ProxyRequest apiRequest, ILambdaContext context)
        {
            if (string.IsNullOrEmpty(apiRequest.Body))
            {
                return CrearRespuesta(400, new { Success = false, Message = "El cuerpo de la petición está vacío." });
            }

            var peticion = JsonSerializer.Deserialize<CrearGastoRequest>(apiRequest.Body);

            if (peticion == null || peticion.Monto <= 0 || string.IsNullOrEmpty(peticion.Categoria))
            {
                return CrearRespuesta(400, new { Success = false, Message = "Monto y Categoría son obligatorios." });
            }

            string monedaFinal = string.IsNullOrEmpty(peticion.Moneda) ? "PEN" : peticion.Moneda.ToUpper();
            decimal tipoCambioFinal = peticion.TipoCambio ?? 1m;
            decimal montoPenFinal;

            if (monedaFinal == "USD")
            {
                montoPenFinal = Math.Round(peticion.Monto * tipoCambioFinal, 2);
            }
            else
            {
                monedaFinal = "PEN";
                tipoCambioFinal = 1m;
                montoPenFinal = peticion.Monto;
            }

            var nuevoGasto = new Gasto
            {
                UsuarioID = peticion.UserId,
                FechaOperacion = string.IsNullOrEmpty(peticion.Fecha) ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss") : peticion.Fecha,
                GastoId = Guid.NewGuid().ToString(),
                Categoria = peticion.Categoria,
                EsPagoDeCuota = peticion.EsPagoDeCuota,
                CuotaIdRelacionada = peticion.CuotaIdRelacionada,
                Comercio = peticion.Comercio,
                Banco = "Efectivo/Otro",
                Monto = peticion.Monto,       
                Moneda = monedaFinal,         
                TipoCambio = tipoCambioFinal, 
                MontoPEN = montoPenFinal      
            };

            await _dbContext.SaveAsync(nuevoGasto);
            context.Logger.LogLine($"Gasto guardado exitosamente. Hash: {nuevoGasto.UsuarioID}. Moneda: {monedaFinal}. MontoPEN: {montoPenFinal}");

            return CrearRespuesta(201, new { Success = true, Message = "Gasto registrado", GastoId = nuevoGasto.GastoId });
        }

        private async Task<APIGatewayHttpApiV2ProxyResponse> ObtenerGastos(APIGatewayHttpApiV2ProxyRequest apiRequest, ILambdaContext context)
        {
            apiRequest.QueryStringParameters.TryGetValue("userId", out string userId);
            apiRequest.QueryStringParameters.TryGetValue("mes", out string mes); // Ejemplo: "2026-08"

            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(mes))
            {
                return CrearRespuesta(400, new { Success = false, Message = "Los parámetros 'userId' y 'mes' son obligatorios." });
            }

            try
            {
                // 1. Traemos TODOS los gastos del usuario (Un solo viaje a la base de datos)
                var config = new DynamoDBOperationConfig();
                var busqueda = _dbContext.QueryAsync<Gasto>(userId, config);
                var listaGastosTotales = await busqueda.GetRemainingAsync();

                // 2. Filtramos exactamente con la misma regla segura que usamos en el Dashboard
                var gastosDelMes = listaGastosTotales.Where(g =>
                    
                    (
                        // Caso 1: Gastos normales (NO cuota) del mes
                        (!g.EsPagoDeCuota && !string.IsNullOrEmpty(g.FechaOperacion) && g.FechaOperacion.StartsWith(mes))

                        ||

                        // Caso 2: Cuotas efectivamente pagadas en este mes
                        (g.EsPagoDeCuota && g.Estado == "Pagado" && !string.IsNullOrEmpty(g.FechaActualizacion) && g.FechaActualizacion.StartsWith(mes))
                    )
                ).ToList();

                // 3. Retornamos la lista final filtrada y sin duplicados
                return CrearRespuesta(200, new
                {
                    Success = true,
                    TotalResultados = gastosDelMes.Count,
                    Gastos = gastosDelMes // Lista ya filtrada y validada
                });
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"Error al obtener gastos: {ex.Message}");
                return CrearRespuesta(500, new { Success = false, Message = "Error interno al obtener los gastos." });
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