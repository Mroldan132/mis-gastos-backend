using Amazon.Lambda.APIGatewayEvents;
using System.Collections.Generic;
using System.Text.Json;

namespace LectorGastosLambda.Services
{
    public static class Respuestas
    {
        public static APIGatewayHttpApiV2ProxyResponse Json(int statusCode, object body) => new APIGatewayHttpApiV2ProxyResponse
        {
            StatusCode = statusCode,
            Body = JsonSerializer.Serialize(body),
            Headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/json" },
                { "Access-Control-Allow-Origin", "*" }
            }
        };

        public static APIGatewayHttpApiV2ProxyResponse Error(int statusCode, string mensaje) => Json(statusCode, new { Success = false, Message = mensaje });

        public static string? Query(APIGatewayHttpApiV2ProxyRequest req, string nombre)
        {
            string? valor = null;
            req.QueryStringParameters?.TryGetValue(nombre, out valor);
            return valor;
        }
    }
}
