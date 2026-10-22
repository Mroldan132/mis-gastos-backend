using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Text;

namespace LectorGastosLambda.Security
{
    // Firma y valida los JWT de la app. El secreto se lee de la variable de entorno JWT_SECRET,
    // que debe tener el mismo valor en todas las funciones GastosHogar-*.
    public static class JwtAuth
    {
        public const string Issuer = "gastos-hogar-api";
        public const string Audience = "gastos-hogar-app";

        private static readonly JwtSecurityTokenHandler _handler = new JwtSecurityTokenHandler { MapInboundClaims = false };

        public static SymmetricSecurityKey? ObtenerClave()
        {
            var secreto = Environment.GetEnvironmentVariable("JWT_SECRET");
            // HMAC-SHA256 necesita al menos 32 bytes de clave
            if (string.IsNullOrWhiteSpace(secreto) || Encoding.UTF8.GetByteCount(secreto) < 32) return null;
            return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secreto));
        }

        // Devuelve el UserId del token "Authorization: Bearer ..." o null si falta, es inválido o expiró.
        public static string? ObtenerUserId(APIGatewayHttpApiV2ProxyRequest apiRequest, ILambdaLogger logger)
        {
            var clave = ObtenerClave();
            if (clave == null)
            {
                logger.LogError("JWT_SECRET no está configurado (o tiene menos de 32 caracteres). Se rechazan todas las peticiones.");
                return null;
            }

            var header = apiRequest.Headers?
                .FirstOrDefault(h => string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase)).Value;

            if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                var principal = _handler.ValidateToken(header.Substring("Bearer ".Length).Trim(), new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = Issuer,
                    ValidateAudience = true,
                    ValidAudience = Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = clave,
                    ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
                    ClockSkew = TimeSpan.FromMinutes(1)
                }, out _);

                var userId = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
                return string.IsNullOrEmpty(userId) ? null : userId;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Token rechazado: {ex.Message}");
                return null;
            }
        }

        public static APIGatewayHttpApiV2ProxyResponse RespuestaPreflight() => new APIGatewayHttpApiV2ProxyResponse
        {
            StatusCode = 204,
            Headers = new Dictionary<string, string>
            {
                { "Access-Control-Allow-Origin", "*" },
                { "Access-Control-Allow-Headers", "Content-Type, Authorization" },
                { "Access-Control-Allow-Methods", "GET, POST, PUT, OPTIONS" }
            }
        };
    }
}
