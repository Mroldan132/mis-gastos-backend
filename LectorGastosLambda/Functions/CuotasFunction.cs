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
    public class CuotasFunction
    {
        private static readonly AmazonDynamoDBClient _dynamoClient = new AmazonDynamoDBClient(Amazon.RegionEndpoint.USEast2);
        private static readonly DynamoDBContext _dbContext = new DynamoDBContext(_dynamoClient);

        public async Task<APIGatewayHttpApiV2ProxyResponse> CuotasMasterHandler(APIGatewayHttpApiV2ProxyRequest apiRequest, ILambdaContext context)
        {
            var httpMethod = apiRequest.RequestContext.Http.Method.ToUpper();
            context.Logger.LogLine($"Recibida petición {httpMethod} en Cuotas");

            try
            {
                if (httpMethod == "POST") return await ConvertirACuotas(apiRequest, context);
                if (httpMethod == "GET") return await ListarCuotas(apiRequest, context);
                if (httpMethod == "PUT") return await ActualizarCuota(apiRequest, context);

                return CrearRespuesta(405, new { Success = false, Message = $"Método {httpMethod} no permitido." });
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"Error general en CuotasFunction: {ex.Message}");
                return CrearRespuesta(500, new { Success = false, Message = "Error interno del servidor." });
            }
        }

        // --- 1. LISTAR CUOTAS (GET) ---
        private async Task<APIGatewayHttpApiV2ProxyResponse> ListarCuotas(APIGatewayHttpApiV2ProxyRequest apiRequest, ILambdaContext context)
        {
            apiRequest.QueryStringParameters.TryGetValue("userId", out string userId);

            if (string.IsNullOrEmpty(userId))
                return CrearRespuesta(400, new { Success = false, Message = "Falta el userId." });

            // Buscamos en la tabla de Gastos todos los que sean "EsPagoDeCuota"
            var config = new DynamoDBOperationConfig
            {
                QueryFilter = new List<ScanCondition> { new ScanCondition("EsPagoDeCuota", ScanOperator.Equal, true) }
            };
            var search = _dbContext.QueryAsync<Gasto>(userId, config);
            var cuotas = await search.GetRemainingAsync();

            // Ordenamos por fecha de cobro y mapeamos para el frontend
            var resultado = cuotas.OrderBy(c => c.FechaOperacion).Select(c => new {
                Id = c.GastoId,
                FechaOperacion = c.FechaOperacion, // Es vital devolver la fecha para poder hacer el PUT luego
                Comercio = c.Comercio,
                Monto = c.Monto,
                Estado = string.IsNullOrEmpty(c.Estado) ? "Pendiente" : c.Estado
            });

            return CrearRespuesta(200, new { Success = true, Cuotas = resultado });
        }

        // --- 2. EDITAR O PAGAR CUOTA (PUT) ---
        private async Task<APIGatewayHttpApiV2ProxyResponse> ActualizarCuota(APIGatewayHttpApiV2ProxyRequest apiRequest, ILambdaContext context)
        {
            if (string.IsNullOrEmpty(apiRequest.Body))
                return CrearRespuesta(400, new { Success = false, Message = "El cuerpo está vacío." });

            var peticion = JsonSerializer.Deserialize<EditarCuotaRequest>(apiRequest.Body);

            // DynamoDB necesita el PartitionKey (UserId) y el SortKey (FechaOperacion) para encontrar el registro
            var gastoCuota = await _dbContext.LoadAsync<Gasto>(peticion.UserId, peticion.FechaOperacion);

            if (gastoCuota == null)
                return CrearRespuesta(404, new { Success = false, Message = "Cuota no encontrada." });

            // Actualizamos solo lo que nos mandan
            if (peticion.Monto.HasValue) gastoCuota.Monto = peticion.Monto.Value;

            if (!string.IsNullOrEmpty(peticion.Estado)) 
            {
                gastoCuota.Estado = peticion.Estado;
                gastoCuota.FechaActualizacion = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
            }

            await _dbContext.SaveAsync(gastoCuota);

            return CrearRespuesta(200, new { Success = true, Message = "Cuota actualizada correctamente." });
        }

        // --- 3. CONVERTIR A CUOTAS (POST - Tu código original intacto) ---
        private async Task<APIGatewayHttpApiV2ProxyResponse> ConvertirACuotas(APIGatewayHttpApiV2ProxyRequest apiRequest, ILambdaContext context)
        {
            // ... (AQUÍ VA TU CÓDIGO EXACTO DE ConvertirACuotas QUE ME PASASTE, NO LE CAMBIÉ NADA) ...
            var peticion = JsonSerializer.Deserialize<ConvertirACuotasRequest>(apiRequest.Body);
            var gastoOriginal = await _dbContext.LoadAsync<Gasto>(peticion.UserId, peticion.FechaOperacionOriginal);

            var nuevaCuotaId = Guid.NewGuid().ToString();

            decimal montoMensual = Math.Round(gastoOriginal.Monto / peticion.CantidadCuotas, 2);
            DateTime fechaBase = DateTime.Parse(gastoOriginal.FechaOperacion);

            var tareasGuardado = new List<Task>();
            for (int i = 1; i <= peticion.CantidadCuotas; i++)
            {
                var gastoHijo = new Gasto
                {
                    UsuarioID = gastoOriginal.UsuarioID,
                    FechaOperacion = fechaBase.AddMonths(i).ToString("yyyy-MM-ddTHH:mm:ss"),
                    GastoId = Guid.NewGuid().ToString(),
                    Monto = montoMensual,
                    Moneda = gastoOriginal.Moneda,
                    Comercio = $"{gastoOriginal.Comercio} (Cuota {i}/{peticion.CantidadCuotas})",
                    Categoria = gastoOriginal.Categoria,
                    Banco = gastoOriginal.Banco,
                    EsPagoDeCuota = true,
                    CuotaIdRelacionada = nuevaCuotaId,
                    EsGastoReferencia = false
                };
                tareasGuardado.Add(_dbContext.SaveAsync(gastoHijo));
            }
            gastoOriginal.EsGastoReferencia = true;
            tareasGuardado.Add(_dbContext.SaveAsync(gastoOriginal));
            await Task.WhenAll(tareasGuardado);

            return CrearRespuesta(200, new { Success = true, Message = "Gasto convertido a cuotas." });
        }

        private APIGatewayHttpApiV2ProxyResponse CrearRespuesta(int statusCode, object bodyResponse)
        {
            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Body = JsonSerializer.Serialize(bodyResponse),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" }, { "Access-Control-Allow-Origin", "*" } }
            };
        }
    }
}