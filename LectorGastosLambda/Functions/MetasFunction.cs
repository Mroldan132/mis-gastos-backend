using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using LectorGastosLambda.DTOs.Requests;
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
    // Metas de ahorro personales y del hogar (compartidas entre todos los miembros)
    public class MetasFunction
    {
        private static readonly AmazonDynamoDBClient _dynamoClient = new AmazonDynamoDBClient(Amazon.RegionEndpoint.USEast2);
        private static readonly DynamoDBContext _dbContext = new DynamoDBContext(_dynamoClient);
        private static readonly JsonSerializerOptions JsonFlexible = new() { PropertyNameCaseInsensitive = true };

        public async Task<APIGatewayHttpApiV2ProxyResponse> MetasMasterHandler(APIGatewayHttpApiV2ProxyRequest apiRequest, ILambdaContext context)
        {
            var metodo = apiRequest.RequestContext.Http.Method.ToUpper();
            var ruta = (apiRequest.RequestContext.Http.Path ?? "").ToLower().TrimEnd('/');
            context.Logger.LogLine($"Recibida petición {metodo} en la ruta {ruta}");

            if (metodo == "OPTIONS") return JwtAuth.RespuestaPreflight();

            // El usuario sale del token, nunca de la URL ni del cuerpo
            var userId = JwtAuth.ObtenerUserId(apiRequest, context.Logger);
            if (userId == null) return Respuestas.Error(401, "No autorizado. Inicia sesión de nuevo.");

            try
            {
                if (ruta.EndsWith("/aportar") && metodo is "POST" or "PUT")
                    return await AportarAMeta(apiRequest, userId, context);

                return metodo switch
                {
                    "GET" => await ObtenerMetas(userId),
                    "POST" => await CrearMeta(apiRequest, userId, context),
                    // PUT con MetaId edita; sin MetaId crea (compatibilidad con la app)
                    "PUT" => TieneMetaId(apiRequest) ? await EditarMeta(apiRequest, userId) : await CrearMeta(apiRequest, userId, context),
                    "DELETE" => await EliminarMeta(apiRequest, userId, context),
                    _ => Respuestas.Error(405, $"Método {metodo} no permitido.")
                };
            }
            catch (JsonException)
            {
                return Respuestas.Error(400, "El cuerpo de la petición no es un JSON válido.");
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Error general en MetasFunction: {ex}");
                return Respuestas.Error(500, "Error interno del servidor.");
            }
        }

        private static T? Leer<T>(APIGatewayHttpApiV2ProxyRequest req) where T : class =>
            string.IsNullOrEmpty(req.Body) ? null : JsonSerializer.Deserialize<T>(req.Body, JsonFlexible);

        private static bool TieneMetaId(APIGatewayHttpApiV2ProxyRequest req)
        {
            if (string.IsNullOrEmpty(req.Body)) return false;
            using var doc = JsonDocument.Parse(req.Body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.EnumerateObject().Any(p =>
                    p.Name.Equals("MetaId", StringComparison.OrdinalIgnoreCase)
                    && p.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(p.Value.GetString()));
        }

        // Busca la meta entre las personales del usuario y, si no está, entre las del hogar
        private static async Task<Meta?> BuscarMeta(string userId, string metaId) =>
            await _dbContext.LoadAsync<Meta>(userId, metaId) ?? await _dbContext.LoadAsync<Meta>(Meta.DuenoHogar, metaId);

        private static string Ahora() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");

        // ====================================================
        // LISTAR
        // ====================================================

        private async Task<APIGatewayHttpApiV2ProxyResponse> ObtenerMetas(string userId)
        {
            var personalesTask = _dbContext.QueryAsync<Meta>(userId).GetRemainingAsync();
            var hogarTask = _dbContext.QueryAsync<Meta>(Meta.DuenoHogar).GetRemainingAsync();
            var configTask = Hogar.CargarConfig(_dbContext);
            await Task.WhenAll(personalesTask, hogarTask, configTask);

            var config = configTask.Result;
            var miembros = await Hogar.CargarMiembros(_dbContext, config, ReglasGastos.MesActualLima());
            var proporciones = Hogar.Proporciones(config, miembros);

            var metasHogar = hogarTask.Result
                .OrderBy(m => m.Estado == Meta.EstadoCompletada)
                .ThenByDescending(m => m.FechaCreacion)
                .Select(m => ResumenCompartida(m, miembros, proporciones, userId))
                .ToList();

            return Respuestas.Json(200, new
            {
                Success = true,
                Metas = personalesTask.Result,
                MetasHogar = metasHogar,
                ModoReparto = config.ModoReparto
            });
        }

        // Cómo va una meta del hogar: avance total y cuánto puso / le falta a cada miembro
        private static object ResumenCompartida(Meta m, List<MiembroHogar> miembros, Dictionary<string, decimal> proporciones, string userId)
        {
            var nombres = miembros.ToDictionary(x => x.UserId, x => x.Nombre);
            return new
            {
                m.MetaId, m.Nombre, m.Categoria, m.Icono, m.MontoObjetivo, m.MontoActual, m.Estado, m.FechaCreacion, m.FechaActualizacion,
                EsCompartida = true,
                CreadaPor = nombres.GetValueOrDefault(m.CreadaPor ?? ""),
                Avance = m.MontoObjetivo > 0 ? Math.Round(Math.Min(m.MontoActual / m.MontoObjetivo, 1) * 100, 1) : 0,
                Falta = Math.Max(0, m.MontoObjetivo - m.MontoActual),
                Miembros = miembros.Select(mb =>
                {
                    var aportado = m.Aportes?.GetValueOrDefault(mb.UserId) ?? 0;
                    var proporcion = proporciones.GetValueOrDefault(mb.UserId);
                    var leCorresponde = Math.Round(m.MontoObjetivo * proporcion, 2);
                    return new
                    {
                        mb.UserId, mb.Nombre,
                        EsYo = mb.UserId == userId,
                        Aportado = aportado,
                        Porcentaje = Math.Round(proporcion * 100, 1),
                        LeCorresponde = leCorresponde,
                        Falta = Math.Max(0, leCorresponde - aportado)
                    };
                }).ToList()
            };
        }

        // ====================================================
        // CREAR / EDITAR / ELIMINAR
        // ====================================================

        private async Task<APIGatewayHttpApiV2ProxyResponse> CrearMeta(APIGatewayHttpApiV2ProxyRequest req, string userId, ILambdaContext context)
        {
            var p = Leer<CrearMetaRequest>(req);
            if (p == null) return Respuestas.Error(400, "El cuerpo de la petición está vacío.");
            if (p.MontoObjetivo <= 0 || string.IsNullOrWhiteSpace(p.Nombre))
                return Respuestas.Error(400, "Nombre y Monto Objetivo son obligatorios.");

            var meta = new Meta
            {
                UsuarioID = p.Compartida ? Meta.DuenoHogar : userId,
                MetaId = Guid.NewGuid().ToString(),
                Nombre = p.Nombre.Trim(),
                Categoria = string.IsNullOrWhiteSpace(p.Categoria) ? "General" : p.Categoria.Trim(),
                MontoObjetivo = p.MontoObjetivo,
                MontoActual = 0,
                Icono = string.IsNullOrWhiteSpace(p.Icono) ? "\U0001F3AF" : p.Icono,
                FechaCreacion = Ahora(),
                Estado = Meta.EstadoActiva,
                CreadaPor = p.Compartida ? userId : null
            };

            await _dbContext.SaveAsync(meta);
            context.Logger.LogLine($"Meta {(p.Compartida ? "del hogar" : "personal")} creada. MetaId: {meta.MetaId}");

            return Respuestas.Json(201, new { Success = true, Message = p.Compartida ? "Meta del hogar creada" : "Meta creada", meta.MetaId, meta.EsCompartida });
        }

        private async Task<APIGatewayHttpApiV2ProxyResponse> EditarMeta(APIGatewayHttpApiV2ProxyRequest req, string userId)
        {
            var p = Leer<EditarMetaRequest>(req);
            if (p == null || string.IsNullOrEmpty(p.MetaId)) return Respuestas.Error(400, "Falta el MetaId.");

            var meta = await BuscarMeta(userId, p.MetaId);
            if (meta == null) return Respuestas.Error(404, "Meta no encontrada.");

            if (!string.IsNullOrWhiteSpace(p.Nombre)) meta.Nombre = p.Nombre.Trim();
            if (!string.IsNullOrWhiteSpace(p.Categoria)) meta.Categoria = p.Categoria.Trim();
            if (!string.IsNullOrWhiteSpace(p.Icono)) meta.Icono = p.Icono;
            if (p.MontoObjetivo.HasValue)
            {
                if (p.MontoObjetivo <= 0) return Respuestas.Error(400, "El monto objetivo debe ser mayor a 0.");
                meta.MontoObjetivo = p.MontoObjetivo.Value;
            }

            if (p.Estado != null)
            {
                if (p.Estado is not (Meta.EstadoActiva or Meta.EstadoCompletada))
                    return Respuestas.Error(400, "Estado debe ser 'Activa' o 'Completada'.");
                meta.Estado = p.Estado;
            }
            else
            {
                // Sin estado explícito, el estado sigue a los números (si subió el objetivo, vuelve a estar activa)
                meta.Estado = meta.MontoActual >= meta.MontoObjetivo ? Meta.EstadoCompletada : Meta.EstadoActiva;
            }

            meta.FechaActualizacion = Ahora();
            await _dbContext.SaveAsync(meta);
            return Respuestas.Json(200, new { Success = true, Message = "Meta actualizada", meta.MetaId, meta.Estado, meta.EsCompartida });
        }

        private async Task<APIGatewayHttpApiV2ProxyResponse> EliminarMeta(APIGatewayHttpApiV2ProxyRequest req, string userId, ILambdaContext context)
        {
            var metaId = Respuestas.Query(req, "metaId");
            if (string.IsNullOrEmpty(metaId) && TieneMetaId(req)) metaId = Leer<EditarMetaRequest>(req)?.MetaId;
            if (string.IsNullOrEmpty(metaId)) return Respuestas.Error(400, "Falta el metaId (en la URL ?metaId= o en el cuerpo).");

            // Las metas del hogar las puede eliminar cualquier miembro, igual que su configuración
            var meta = await BuscarMeta(userId, metaId);
            if (meta == null) return Respuestas.Error(404, "Meta no encontrada.");

            await _dbContext.DeleteAsync(meta);
            context.Logger.LogLine($"Meta {metaId} ({(meta.EsCompartida ? "del hogar" : "personal")}) eliminada por {userId}.");
            return Respuestas.Json(200, new { Success = true, Message = "Meta eliminada" });
        }

        // ====================================================
        // APORTAR
        // ====================================================

        private async Task<APIGatewayHttpApiV2ProxyResponse> AportarAMeta(APIGatewayHttpApiV2ProxyRequest req, string userId, ILambdaContext context)
        {
            var p = Leer<AporteMetaRequest>(req);
            if (p == null) return Respuestas.Error(400, "El cuerpo de la petición está vacío.");
            if (p.MontoAporte <= 0) return Respuestas.Error(400, "El monto de aporte debe ser mayor a 0.");
            if (string.IsNullOrEmpty(p.MetaId)) return Respuestas.Error(400, "Falta el MetaId.");

            var meta = await BuscarMeta(userId, p.MetaId);
            if (meta == null) return Respuestas.Error(404, "Meta no encontrada.");

            var ahora = Ahora();
            meta.MontoActual += p.MontoAporte;
            if (meta.EsCompartida)
            {
                meta.Aportes ??= new Dictionary<string, decimal>();
                meta.Aportes[userId] = meta.Aportes.GetValueOrDefault(userId) + p.MontoAporte;
            }
            var recienCompletada = meta.Estado != Meta.EstadoCompletada && meta.MontoActual >= meta.MontoObjetivo;
            if (recienCompletada) meta.Estado = Meta.EstadoCompletada;
            meta.FechaActualizacion = ahora;
            await _dbContext.SaveAsync(meta);

            // El aporte sale del bolsillo de quien lo hace: queda como su gasto de Ahorro
            await _dbContext.SaveAsync(new Gasto
            {
                UsuarioID = userId,
                GastoId = Guid.NewGuid().ToString(),
                FechaOperacion = ahora,
                Monto = p.MontoAporte,
                Moneda = "PEN",
                TipoCambio = 1m,
                MontoPEN = p.MontoAporte,
                Categoria = ReglasGastos.CategoriaAhorro,
                Comercio = meta.EsCompartida ? $"Aporte a meta del hogar: {meta.Nombre}" : $"Aporte a Meta: {meta.Nombre}",
                Banco = "Transferencia Interna",
                EsPagoDeCuota = false,
                Origen = "Manual",
                Revisado = true,
                EsCompartido = false
            });

            context.Logger.LogLine($"Aporte de S/ {p.MontoAporte} de {userId} a la meta {meta.Nombre}{(meta.EsCompartida ? " (hogar)" : "")}.");

            return Respuestas.Json(200, new
            {
                Success = true,
                Message = recienCompletada ? "¡Meta completada!" : "Aporte registrado con éxito",
                meta.MetaId, meta.MontoActual, meta.MontoObjetivo, meta.Estado, meta.EsCompartida,
                Completada = recienCompletada,
                MiAporte = meta.EsCompartida ? meta.Aportes.GetValueOrDefault(userId) : meta.MontoActual
            });
        }
    }
}
