using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using LectorGastosLambda.DTOs.Requests;
using LectorGastosLambda.Models.DataBase;
using LectorGastosLambda.Security;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace LectorGastosLambda.Functions
{
    public class MetasFunction
    {
        private static readonly AmazonDynamoDBClient _dynamoClient = new AmazonDynamoDBClient(Amazon.RegionEndpoint.USEast2);
        private static readonly DynamoDBContext _dbContext = new DynamoDBContext(_dynamoClient);

        public async Task<APIGatewayHttpApiV2ProxyResponse> MetasMasterHandler(APIGatewayHttpApiV2ProxyRequest apiRequest, ILambdaContext context)
        {
            var httpMethod = apiRequest.RequestContext.Http.Method.ToUpper();
            var path = apiRequest.RequestContext.Http.Path.ToLower(); 

            context.Logger.LogLine($"Recibida petición {httpMethod} en la ruta {path}");

            if (httpMethod == "OPTIONS") return JwtAuth.RespuestaPreflight();

            // El usuario sale del token, nunca de la URL ni del cuerpo
            var userId = JwtAuth.ObtenerUserId(apiRequest, context.Logger);
            if (userId == null)
                return CrearRespuesta(401, new { Success = false, Message = "No autorizado. Inicia sesión de nuevo." });

            try
            {
                switch (httpMethod)
                {
                    case "GET":
                        return await ObtenerMetas(userId, context);

                    case "POST":
                    case "PUT":
                        if (path.EndsWith("/aportar"))
                        {
                            return await AportarAMeta(apiRequest, userId, context);
                        }
                        return await CrearMeta(apiRequest, userId, context);

                    default:
                        return CrearRespuesta(405, new { Success = false, Message = $"Método {httpMethod} no permitido." });
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"Error general en MetasFunction: {ex.Message}");
                return CrearRespuesta(500, new { Success = false, Message = "Error interno del servidor." });
            }
        }
        private async Task<APIGatewayHttpApiV2ProxyResponse> ObtenerMetas(string userId, ILambdaContext context)
        {
            try
            {
                var metas = await _dbContext.QueryAsync<Meta>(userId).GetRemainingAsync();
                return CrearRespuesta(200, new { Success = true, Metas = metas });
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"Error al obtener metas: {ex.Message}");
                return CrearRespuesta(500, new { Success = false, Message = "Error interno" });
            }
        }

        private async Task<APIGatewayHttpApiV2ProxyResponse> CrearMeta(APIGatewayHttpApiV2ProxyRequest apiRequest, string userId, ILambdaContext context)
        {
            if (string.IsNullOrEmpty(apiRequest.Body))
                return CrearRespuesta(400, new { Success = false, Message = "El cuerpo está vacío." });

            var peticion = JsonSerializer.Deserialize<CrearMetaRequest>(apiRequest.Body);

            if (peticion == null || peticion.MontoObjetivo <= 0 || string.IsNullOrEmpty(peticion.Nombre))
            {
                return CrearRespuesta(400, new { Success = false, Message = "Nombre y Monto Objetivo son obligatorios." });
            }

            var nuevaMeta = new Meta
            {
                UsuarioID = userId,
                MetaId = Guid.NewGuid().ToString(),
                Nombre = peticion.Nombre,
                Categoria = string.IsNullOrEmpty(peticion.Categoria) ? "General" : peticion.Categoria,
                MontoObjetivo = peticion.MontoObjetivo,
                MontoActual = 0,
                Icono = string.IsNullOrEmpty(peticion.Icono) ? "🎯" : peticion.Icono,
                FechaCreacion = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"),
                Estado = "Activa"
            };

            await _dbContext.SaveAsync(nuevaMeta);
            context.Logger.LogLine($"Meta creada exitosamente. MetaId: {nuevaMeta.MetaId}");

            return CrearRespuesta(201, new { Success = true, Message = "Meta creada", MetaId = nuevaMeta.MetaId });
        }

        private async Task<APIGatewayHttpApiV2ProxyResponse> AportarAMeta(APIGatewayHttpApiV2ProxyRequest apiRequest, string userId, ILambdaContext context)
        {
            if (string.IsNullOrEmpty(apiRequest.Body))
                return CrearRespuesta(400, new { Success = false, Message = "El cuerpo está vacío." });

            var peticion = JsonSerializer.Deserialize<AporteMetaRequest>(apiRequest.Body);

            if (peticion == null || peticion.MontoAporte <= 0)
            {
                return CrearRespuesta(400, new { Success = false, Message = "El monto de aporte debe ser mayor a 0." });
            }

            var meta = await _dbContext.LoadAsync<Meta>(userId, peticion.MetaId);
            if (meta == null)
            {
                return CrearRespuesta(404, new { Success = false, Message = "Meta no encontrada." });
            }

            meta.MontoActual += peticion.MontoAporte;
            if (meta.MontoActual >= meta.MontoObjetivo)
            {
                meta.Estado = "Completada"; // ¡Felicidades!
            }
            await _dbContext.SaveAsync(meta);

            var nuevoGasto = new Gasto
            {
                UsuarioID = userId,
                GastoId = Guid.NewGuid().ToString(),
                FechaOperacion = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"),
                Monto = peticion.MontoAporte,
                Moneda = "PEN",
                TipoCambio = 1m,
                MontoPEN = peticion.MontoAporte, 
                Categoria = "Ahorro",
                Comercio = $"Aporte a Meta: {meta.Nombre}",
                Banco = "Transferencia Interna",
                EsPagoDeCuota = false
            };

            await _dbContext.SaveAsync(nuevoGasto);

            context.Logger.LogLine($"Aporte de S/ {peticion.MontoAporte} registrado en la meta {meta.Nombre}.");

            return CrearRespuesta(200, new { Success = true, Message = "Aporte registrado con éxito" });
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
