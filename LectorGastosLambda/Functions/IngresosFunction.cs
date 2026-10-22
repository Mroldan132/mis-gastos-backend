using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using LectorGastosLambda.DTOs.Requests;
using LectorGastosLambda.Models.DataBase;
using LectorGastosLambda.Security;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace LectorGastosLambda.Functions
{
    public class IngresosFunction
    {
        private static readonly AmazonDynamoDBClient _dynamoClient = new AmazonDynamoDBClient(Amazon.RegionEndpoint.USEast2);
        private static readonly DynamoDBContext _dbContext = new DynamoDBContext(_dynamoClient);

        public async Task<APIGatewayHttpApiV2ProxyResponse> IngresosMasterHandler(APIGatewayHttpApiV2ProxyRequest apiRequest, ILambdaContext context)
        {
            var httpMethod = apiRequest.RequestContext.Http.Method.ToUpper();
            context.Logger.LogLine($"Recibida petición {httpMethod} en Ingresos");

            if (httpMethod == "OPTIONS") return JwtAuth.RespuestaPreflight();

            // El usuario sale del token, nunca de la URL ni del cuerpo
            var userId = JwtAuth.ObtenerUserId(apiRequest, context.Logger);
            if (userId == null)
                return CrearRespuesta(401, new { Success = false, Message = "No autorizado. Inicia sesión de nuevo." });

            try
            {
                switch (httpMethod)
                {
                    case "POST":
                        return await CrearIngreso(apiRequest, userId, context);
                    case "GET":
                        return await ObtenerIngresos(apiRequest, userId, context);
                    default:
                        return CrearRespuesta(405, new { Success = false, Message = $"Método {httpMethod} no permitido." });
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"Error general en IngresosFunction: {ex.Message}");
                return CrearRespuesta(500, new { Success = false, Message = "Error interno del servidor." });
            }
        }

        private async Task<APIGatewayHttpApiV2ProxyResponse> CrearIngreso(APIGatewayHttpApiV2ProxyRequest apiRequest, string userId, ILambdaContext context)
        {
            if (string.IsNullOrEmpty(apiRequest.Body))
                return CrearRespuesta(400, new { Success = false, Message = "El cuerpo está vacío." });

            var peticion = JsonSerializer.Deserialize<CrearIngresoRequest>(apiRequest.Body);

            if (peticion == null || peticion.Monto <= 0 || string.IsNullOrEmpty(peticion.Descripcion))
                return CrearRespuesta(400, new { Success = false, Message = "Monto y Descripción son obligatorios." });

            var nuevoIngreso = new Ingreso
            {
                UsuarioID = userId,
                FechaOperacion = string.IsNullOrEmpty(peticion.Fecha) ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss") : peticion.Fecha,
                IngresoId = Guid.NewGuid().ToString(),
                Monto = peticion.Monto,
                Descripcion = peticion.Descripcion,
                Moneda = "PEN"
            };

            await _dbContext.SaveAsync(nuevoIngreso);
            return CrearRespuesta(201, new { Success = true, Message = "Ingreso registrado", IngresoId = nuevoIngreso.IngresoId });
        }

        private async Task<APIGatewayHttpApiV2ProxyResponse> ObtenerIngresos(APIGatewayHttpApiV2ProxyRequest apiRequest, string userId, ILambdaContext context)
        {
            string? mes = null;
            apiRequest.QueryStringParameters?.TryGetValue("mes", out mes);

            if (string.IsNullOrEmpty(mes))
                return CrearRespuesta(400, new { Success = false, Message = "El parámetro 'mes' es obligatorio." });

            var config = new DynamoDBOperationConfig
            {
                QueryFilter = new List<ScanCondition>
                {
                    new ScanCondition("FechaOperacion", ScanOperator.BeginsWith, mes)
                }
            };

            var busqueda = _dbContext.QueryAsync<Ingreso>(userId, config);
            var listaIngresos = await busqueda.GetRemainingAsync();

            return CrearRespuesta(200, new { Success = true, TotalResultados = listaIngresos.Count, Ingresos = listaIngresos });
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