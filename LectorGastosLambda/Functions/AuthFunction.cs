using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using System;
using System.Threading.Tasks;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using LectorGastosLambda.Models.DataBase;
using LectorGastosLambda.DTOs.Responses;
using LectorGastosLambda.DTOs.Requests;
using LectorGastosLambda.Security;

namespace LectorGastosLambda.Functions
{
    public class AuthFunction
    {
        private static readonly AmazonDynamoDBClient _dynamoClient = new AmazonDynamoDBClient(Amazon.RegionEndpoint.USEast2);
        private static readonly DynamoDBContext _dbContext = new DynamoDBContext(_dynamoClient);

        public async Task<APIGatewayHttpApiV2ProxyResponse> LoginHandler(APIGatewayHttpApiV2ProxyRequest apiRequest, ILambdaContext context)
        {
            try
            {
                // La clave para firmar el JWT viene de la variable de entorno JWT_SECRET
                var clave = JwtAuth.ObtenerClave();
                if (clave == null)
                {
                    context.Logger.LogError("JWT_SECRET no está configurado (o tiene menos de 32 caracteres). No se puede iniciar sesión.");
                    return CrearRespuesta(500, new LoginResponse { Success = false, Message = "Error interno del servidor." });
                }

                // 1. Validar que la petición tenga un cuerpo (Body)
                if (string.IsNullOrEmpty(apiRequest.Body))
                {
                    return CrearRespuesta(400, new LoginResponse { Success = false, Message = "El cuerpo de la petición está vacío." });
                }

                // 2. Extraer el JSON del envoltorio de API Gateway
                var loginData = JsonSerializer.Deserialize<LoginRequest>(apiRequest.Body);

                if (loginData == null || string.IsNullOrEmpty(loginData.Email) || string.IsNullOrEmpty(loginData.Password))
                {
                    return CrearRespuesta(400, new LoginResponse { Success = false, Message = "Email y contraseña son obligatorios." });
                }

                context.Logger.LogLine($"Intento de login para: {loginData.Email}");

                // 3. Buscar al usuario en la tabla ConfiguracionUsuarios
                var usuario = await _dbContext.LoadAsync<UsuarioConfig>(loginData.Email);

                if(usuario == null)
                    return CrearRespuesta(401, new LoginResponse { Success = false, Message = "Usuario no encontrado." });



                if (usuario != null && usuario.PasswordHash == loginData.Password)
                {
                    context.Logger.LogLine("Login exitoso. Generando Token...");

                    var tokenString = GenerarTokenJWT(usuario, clave);

                    return CrearRespuesta(200, new LoginResponse
                    {
                        Success = true,
                        Nombre = usuario.Nombre,
                        UserId = usuario.UserId,
                        Token = tokenString
                    });
                }

                context.Logger.LogLine("Credenciales incorrectas.");
                return CrearRespuesta(401, new LoginResponse { Success = false, Message = "Email o contraseña incorrectos." });
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"Error crítico en login: {ex.Message}");
                return CrearRespuesta(500, new LoginResponse { Success = false, Message = "Error interno del servidor." });
            }
        }

        private string GenerarTokenJWT(UsuarioConfig usuario, SymmetricSecurityKey securityKey)
        {
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, usuario.UserId ?? string.Empty),
                new Claim(JwtRegisteredClaimNames.Email, usuario.Email),
                new Claim("nombre", usuario.Nombre ?? string.Empty)
            };

            var token = new JwtSecurityToken(
                issuer: JwtAuth.Issuer,
                audience: JwtAuth.Audience,
                claims: claims,
                expires: DateTime.UtcNow.AddDays(7),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        // Método de ayuda para empaquetar la respuesta para API Gateway
        private APIGatewayHttpApiV2ProxyResponse CrearRespuesta(int statusCode, object bodyResponse)
        {
            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Body = JsonSerializer.Serialize(bodyResponse),
                Headers = new Dictionary<string, string>
                {
                    { "Content-Type", "application/json" },
                    { "Access-Control-Allow-Origin", "*" } // Ayuda extra para evitar bloqueos de CORS en React
                }
            };
        }
    }




    
}