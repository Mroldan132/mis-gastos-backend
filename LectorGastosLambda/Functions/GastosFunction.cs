using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using LectorGastosLambda.DTOs.Requests;
using LectorGastosLambda.Models.DataBase;
using LectorGastosLambda.Security;
using LectorGastosLambda.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace LectorGastosLambda.Functions
{
    public class GastosFunction
    {
        private static readonly AmazonDynamoDBClient _dynamoClient = new AmazonDynamoDBClient(Amazon.RegionEndpoint.USEast2);
        private static readonly DynamoDBContext _dbContext = new DynamoDBContext(_dynamoClient);

        private static readonly string[] Categorias = { "Comida", "Transporte", "Servicios", "Hogar", "Entretenimiento", "Salud", "Tecnología", "Educación", "Ropa", "Otros" };

        public async Task<APIGatewayHttpApiV2ProxyResponse> GastosMasterHandler(APIGatewayHttpApiV2ProxyRequest apiRequest, ILambdaContext context)
        {
            var httpMethod = apiRequest.RequestContext.Http.Method.ToUpper();
            var path = (apiRequest.RawPath ?? "").ToLower().TrimEnd('/');

            context.Logger.LogLine($"Recibida petición {httpMethod} {path} en la ruta de Gastos");

            if (httpMethod == "OPTIONS") return JwtAuth.RespuestaPreflight();

            // El usuario sale del token, nunca de la URL ni del cuerpo
            var userId = JwtAuth.ObtenerUserId(apiRequest, context.Logger);
            if (userId == null)
                return CrearRespuesta(401, new { Success = false, Message = "No autorizado. Inicia sesión de nuevo." });

            try
            {
                if (httpMethod == "POST" && path.EndsWith("/gastos/texto"))
                    return await InterpretarTexto(apiRequest, context);

                switch (httpMethod)
                {
                    case "POST":
                        return await CrearGasto(apiRequest, userId, context);
                    case "GET":
                        return await ObtenerGastos(apiRequest, userId, context);
                    case "PUT":
                        return await EditarGasto(apiRequest, userId, context);
                    case "DELETE":
                        return await EliminarGasto(apiRequest, userId, context);
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

        private static bool? CompartidoDesdeTexto(string? valor) => valor?.ToLower() switch
        {
            "si" => true,
            "no" => false,
            _ => null, // "auto": se decide por las categorías compartidas del hogar
        };

        private async Task<APIGatewayHttpApiV2ProxyResponse> CrearGasto(APIGatewayHttpApiV2ProxyRequest apiRequest, string userId, ILambdaContext context)
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

            // La fecha puede venir sola ("YYYY-MM-DD"); se completa con la hora para que la llave sea única
            var fecha = string.IsNullOrEmpty(peticion.Fecha)
                ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss")
                : peticion.Fecha.Length == 10 ? $"{peticion.Fecha}T{DateTime.UtcNow:HH:mm:ss}" : peticion.Fecha;

            var nuevoGasto = new Gasto
            {
                UsuarioID = userId,
                FechaOperacion = fecha,
                GastoId = Guid.NewGuid().ToString(),
                Categoria = peticion.Categoria,
                EsPagoDeCuota = peticion.EsPagoDeCuota,
                CuotaIdRelacionada = peticion.CuotaIdRelacionada,
                Comercio = peticion.Comercio,
                Banco = "Efectivo/Otro",
                Monto = peticion.Monto,
                Moneda = monedaFinal,
                TipoCambio = tipoCambioFinal,
                MontoPEN = montoPenFinal,
                Origen = peticion.Origen == "Texto" ? "Texto" : "Manual",
                Revisado = true,
                EsCompartido = CompartidoDesdeTexto(peticion.Compartido)
            };

            await _dbContext.SaveAsync(nuevoGasto);
            context.Logger.LogLine($"Gasto guardado exitosamente. Hash: {nuevoGasto.UsuarioID}. Moneda: {monedaFinal}. MontoPEN: {montoPenFinal}");

            return CrearRespuesta(201, new { Success = true, Message = "Gasto registrado", GastoId = nuevoGasto.GastoId });
        }

        private async Task<APIGatewayHttpApiV2ProxyResponse> ObtenerGastos(APIGatewayHttpApiV2ProxyRequest apiRequest, string userId, ILambdaContext context)
        {
            string? mes = null; // Ejemplo: "2026-08"
            string? buscar = null;
            apiRequest.QueryStringParameters?.TryGetValue("mes", out mes);
            apiRequest.QueryStringParameters?.TryGetValue("buscar", out buscar);

            if (string.IsNullOrEmpty(mes) && string.IsNullOrWhiteSpace(buscar))
            {
                return CrearRespuesta(400, new { Success = false, Message = "El parámetro 'mes' o 'buscar' es obligatorio." });
            }

            try
            {
                // 1. Traemos TODOS los gastos del usuario (Un solo viaje a la base de datos)
                var busqueda = _dbContext.QueryAsync<Gasto>(userId, new QueryConfig());
                var listaGastosTotales = await busqueda.GetRemainingAsync();

                // 2a. Búsqueda en todo el historial (comercio, categoría o banco), los más recientes primero
                if (!string.IsNullOrWhiteSpace(buscar))
                {
                    var texto = Normalizar(buscar);
                    var encontrados = listaGastosTotales
                        .Where(g => Normalizar(g.Comercio).Contains(texto) || Normalizar(g.Categoria).Contains(texto) || Normalizar(g.Banco).Contains(texto))
                        .OrderByDescending(g => g.FechaOperacion)
                        .Take(200)
                        .ToList();

                    return CrearRespuesta(200, new { Success = true, TotalResultados = encontrados.Count, Gastos = encontrados });
                }

                // 2b. Filtramos exactamente con la misma regla segura que usamos en el Dashboard
                var gastosDelMes = listaGastosTotales.Where(g =>
                    (
                        // Caso 1: Gastos normales (NO cuota) del mes
                        (!g.EsPagoDeCuota && !string.IsNullOrEmpty(g.FechaOperacion) && g.FechaOperacion.StartsWith(mes!))

                        ||

                        // Caso 2: Cuotas efectivamente pagadas en este mes
                        (g.EsPagoDeCuota && g.Estado == "Pagado" && !string.IsNullOrEmpty(g.FechaActualizacion) && g.FechaActualizacion.StartsWith(mes!))
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

        // --- EDITAR (corregir lo que leyó la IA, marcar revisado o compartido) ---
        private async Task<APIGatewayHttpApiV2ProxyResponse> EditarGasto(APIGatewayHttpApiV2ProxyRequest apiRequest, string userId, ILambdaContext context)
        {
            if (string.IsNullOrEmpty(apiRequest.Body))
                return CrearRespuesta(400, new { Success = false, Message = "El cuerpo de la petición está vacío." });

            var peticion = JsonSerializer.Deserialize<EditarGastoRequest>(apiRequest.Body);
            if (peticion == null || string.IsNullOrEmpty(peticion.FechaOperacion))
                return CrearRespuesta(400, new { Success = false, Message = "FechaOperacion es obligatoria." });

            var gasto = await _dbContext.LoadAsync<Gasto>(userId, peticion.FechaOperacion);
            if (gasto == null)
                return CrearRespuesta(404, new { Success = false, Message = "Gasto no encontrado." });

            var cambiaMonto = peticion.Monto.HasValue || !string.IsNullOrEmpty(peticion.Moneda) || peticion.TipoCambio.HasValue;
            if (cambiaMonto && (gasto.EsGastoReferencia || gasto.EsPagoDeCuota))
                return CrearRespuesta(409, new { Success = false, Message = "El monto de un gasto fraccionado o de una cuota se ajusta desde Cuotas." });

            if (!string.IsNullOrWhiteSpace(peticion.Comercio)) gasto.Comercio = peticion.Comercio.Trim();
            if (!string.IsNullOrWhiteSpace(peticion.Categoria)) gasto.Categoria = peticion.Categoria.Trim();
            if (peticion.Compartido != null) gasto.EsCompartido = CompartidoDesdeTexto(peticion.Compartido);

            if (cambiaMonto)
            {
                if (peticion.Monto is <= 0)
                    return CrearRespuesta(400, new { Success = false, Message = "El monto debe ser mayor a 0." });

                if (peticion.Monto.HasValue) gasto.Monto = peticion.Monto.Value;
                if (!string.IsNullOrEmpty(peticion.Moneda)) gasto.Moneda = peticion.Moneda.ToUpper() == "USD" ? "USD" : "PEN";

                if (gasto.Moneda == "USD")
                {
                    gasto.TipoCambio = peticion.TipoCambio ?? gasto.TipoCambio ?? await Monedas.ObtenerUsdPenAsync(context.Logger);
                    gasto.MontoPEN = Math.Round(gasto.Monto * gasto.TipoCambio.Value, 2);
                }
                else
                {
                    gasto.TipoCambio = 1m;
                    gasto.MontoPEN = gasto.Monto;
                }
            }

            // Cualquier corrección cuenta como revisión, salvo que se pida explícitamente lo contrario
            gasto.Revisado = peticion.Revisado ?? true;

            await _dbContext.SaveAsync(gasto);
            context.Logger.LogLine($"Gasto {gasto.GastoId} actualizado.");
            return CrearRespuesta(200, new { Success = true, Message = "Gasto actualizado", Gasto = gasto });
        }

        // --- ELIMINAR ---
        private async Task<APIGatewayHttpApiV2ProxyResponse> EliminarGasto(APIGatewayHttpApiV2ProxyRequest apiRequest, string userId, ILambdaContext context)
        {
            string? fecha = null;
            apiRequest.QueryStringParameters?.TryGetValue("fechaOperacion", out fecha);
            if (string.IsNullOrEmpty(fecha))
                return CrearRespuesta(400, new { Success = false, Message = "El parámetro 'fechaOperacion' es obligatorio." });

            var gasto = await _dbContext.LoadAsync<Gasto>(userId, fecha);
            if (gasto == null)
                return CrearRespuesta(404, new { Success = false, Message = "Gasto no encontrado." });

            if (gasto.EsGastoReferencia || gasto.EsPagoDeCuota)
                return CrearRespuesta(409, new { Success = false, Message = "Los gastos fraccionados y sus cuotas no se pueden eliminar desde aquí." });

            await _dbContext.DeleteAsync<Gasto>(userId, fecha);
            context.Logger.LogLine($"Gasto {gasto.GastoId} ({gasto.Comercio}) eliminado.");
            return CrearRespuesta(200, new { Success = true, Message = "Gasto eliminado" });
        }

        // --- REGISTRO RÁPIDO: "almuerzo 25 yape" -> gasto sugerido (no se guarda hasta que el usuario confirme) ---
        private async Task<APIGatewayHttpApiV2ProxyResponse> InterpretarTexto(APIGatewayHttpApiV2ProxyRequest apiRequest, ILambdaContext context)
        {
            var peticion = string.IsNullOrEmpty(apiRequest.Body) ? null : JsonSerializer.Deserialize<TextoGastoRequest>(apiRequest.Body);
            var texto = peticion?.Texto?.Trim();
            if (string.IsNullOrEmpty(texto) || texto.Length > 300)
                return CrearRespuesta(400, new { Success = false, Message = "Escribe una frase corta, por ejemplo: \"almuerzo 25 yape\"." });

            var apiKey = GeminiClient.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                context.Logger.LogError("Falta la variable de entorno GEMINI_API_KEY en esta función.");
                return CrearRespuesta(503, new { Success = false, Message = "El registro rápido no está disponible en este momento." });
            }

            var hoy = ReglasGastos.AhoraLima().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var instruccion =
                $"Hoy es {hoy} en Lima, Perú. Convierte esta frase de un usuario peruano en un gasto. " +
                $"Moneda PEN salvo que mencione dólares o $. Si no dice la fecha, usa hoy; entiende \"ayer\". " +
                $"Banco: el medio de pago que mencione (Yape, Plin, efectivo, tarjeta, banco) o \"Efectivo/Otro\". " +
                $"Comercio: un nombre corto y claro con mayúscula inicial. " +
                $"Categoria: exactamente una de {string.Join(", ", Categorias)}. Frase:";

            var resultado = await GeminiClient.ExtraerGastoAsync(instruccion, texto, apiKey, GeminiClient.Modelo, context.Logger);
            if (resultado.CuotaAgotada)
                return CrearRespuesta(429, new { Success = false, Message = "Se agotó la cuota diaria de la IA. Regístralo de forma manual." });
            if (resultado.Json == null)
                return CrearRespuesta(502, new { Success = false, Message = "No pudimos interpretar la frase. Intenta escribirla de otra forma." });

            var sugerido = JsonSerializer.Deserialize<Gasto>(resultado.Json);
            if (sugerido == null || sugerido.Monto <= 0)
                return CrearRespuesta(422, new { Success = false, Message = "No encontramos un monto en la frase." });

            if (!DateTime.TryParseExact(sugerido.FechaOperacion, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                sugerido.FechaOperacion = hoy;
            if (!Categorias.Contains(sugerido.Categoria)) sugerido.Categoria = "Otros";
            sugerido.Moneda = sugerido.Moneda?.ToUpper() == "USD" ? "USD" : "PEN";

            return CrearRespuesta(200, new
            {
                Success = true,
                Sugerencia = new { sugerido.Comercio, sugerido.Monto, sugerido.Moneda, sugerido.Categoria, sugerido.Banco, Fecha = sugerido.FechaOperacion }
            });
        }

        // Minúsculas y sin tildes, para que "tecnologia" encuentre "Tecnología"
        private static string Normalizar(string? texto)
        {
            if (string.IsNullOrEmpty(texto)) return "";
            var sinTildes = new string(texto.Normalize(System.Text.NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
            return sinTildes.ToLowerInvariant().Trim();
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
