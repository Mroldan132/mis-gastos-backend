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
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace LectorGastosLambda.Functions
{
    // Presupuestos por categoría, pagos fijos del mes y el hogar compartido
    public class PlanFunction
    {
        private static readonly AmazonDynamoDBClient _dynamoClient = new AmazonDynamoDBClient(Amazon.RegionEndpoint.USEast2);
        private static readonly DynamoDBContext _dbContext = new DynamoDBContext(_dynamoClient);

        public async Task<APIGatewayHttpApiV2ProxyResponse> PlanHandler(APIGatewayHttpApiV2ProxyRequest apiRequest, ILambdaContext context)
        {
            var metodo = apiRequest.RequestContext.Http.Method.ToUpper();
            var ruta = (apiRequest.RawPath ?? "").ToLower().TrimEnd('/');
            context.Logger.LogLine($"Recibida petición {metodo} {ruta} en Plan");

            if (metodo == "OPTIONS") return JwtAuth.RespuestaPreflight();

            var userId = JwtAuth.ObtenerUserId(apiRequest, context.Logger);
            if (userId == null) return Respuestas.Error(401, "No autorizado. Inicia sesión de nuevo.");

            try
            {
                return (metodo, ruta) switch
                {
                    ("GET", "/presupuestos") => await ObtenerPresupuestos(apiRequest, userId),
                    ("PUT", "/presupuestos") => await GuardarPresupuestos(apiRequest, userId),

                    ("GET", "/fijos") => await ObtenerFijos(apiRequest, userId),
                    ("POST", "/fijos") => await GuardarFijo(apiRequest, userId, esNuevo: true),
                    ("PUT", "/fijos") => await GuardarFijo(apiRequest, userId, esNuevo: false),
                    ("DELETE", "/fijos") => await EliminarFijo(apiRequest, userId),
                    ("POST", "/fijos/pagar") => await PagarFijo(apiRequest, userId, context),

                    ("GET", "/hogar") => await ObtenerHogar(apiRequest, userId),
                    ("PUT", "/hogar") => await GuardarHogar(apiRequest),

                    _ => Respuestas.Error(404, $"Ruta {metodo} {ruta} no encontrada.")
                };
            }
            catch (JsonException)
            {
                return Respuestas.Error(400, "El cuerpo de la petición no es un JSON válido.");
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Error en PlanFunction: {ex}");
                return Respuestas.Error(500, "Error interno del servidor.");
            }
        }

        private static string MesSolicitado(APIGatewayHttpApiV2ProxyRequest req)
        {
            var mes = Respuestas.Query(req, "mes");
            return ReglasGastos.EsMesValido(mes) ? mes! : ReglasGastos.MesActualLima();
        }

        private static T? Leer<T>(APIGatewayHttpApiV2ProxyRequest req) where T : class =>
            string.IsNullOrEmpty(req.Body) ? null : JsonSerializer.Deserialize<T>(req.Body);

        private static Task<List<Gasto>> GastosDe(string userId) =>
            _dbContext.QueryAsync<Gasto>(userId, new QueryConfig()).GetRemainingAsync();

        // ====================================================
        // PRESUPUESTOS
        // ====================================================

        private async Task<APIGatewayHttpApiV2ProxyResponse> ObtenerPresupuestos(APIGatewayHttpApiV2ProxyRequest req, string userId)
        {
            var mes = MesSolicitado(req);
            var presupuestosTask = _dbContext.QueryAsync<Presupuesto>(userId, new QueryConfig()).GetRemainingAsync();
            var gastosTask = GastosDe(userId);
            await Task.WhenAll(presupuestosTask, gastosTask);

            var gastado = ReglasGastos.TotalesPorCategoria(gastosTask.Result.Where(g => ReglasGastos.CuentaEnMes(g, mes)));

            var items = presupuestosTask.Result
                .Select(p =>
                {
                    var usado = gastado.GetValueOrDefault(p.Categoria);
                    return new
                    {
                        p.Categoria,
                        p.Monto,
                        Gastado = usado,
                        Restante = p.Monto - usado,
                        Porcentaje = p.Monto > 0 ? Math.Round(usado / p.Monto * 100, 1) : 0
                    };
                })
                .OrderByDescending(p => p.Porcentaje)
                .ToList();

            // Categorías con gasto este mes que todavía no tienen presupuesto (sugerencias)
            var sinPresupuesto = gastado
                .Where(kv => kv.Key != ReglasGastos.CategoriaAhorro && items.All(i => i.Categoria != kv.Key))
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new { Categoria = kv.Key, Gastado = kv.Value })
                .ToList();

            return Respuestas.Json(200, new
            {
                Success = true,
                Mes = mes,
                Presupuestos = items,
                SinPresupuesto = sinPresupuesto,
                TotalPresupuesto = items.Sum(i => i.Monto),
                TotalGastado = items.Sum(i => i.Gastado)
            });
        }

        private async Task<APIGatewayHttpApiV2ProxyResponse> GuardarPresupuestos(APIGatewayHttpApiV2ProxyRequest req, string userId)
        {
            var peticion = Leer<GuardarPresupuestosRequest>(req);
            if (peticion == null) return Respuestas.Error(400, "El cuerpo de la petición está vacío.");

            var nuevos = peticion.Presupuestos
                .Where(p => !string.IsNullOrWhiteSpace(p.Categoria) && p.Monto > 0)
                .GroupBy(p => p.Categoria.Trim())
                .ToDictionary(g => g.Key, g => g.Last().Monto);

            var actuales = await _dbContext.QueryAsync<Presupuesto>(userId, new QueryConfig()).GetRemainingAsync();
            var ahora = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");

            var borrar = _dbContext.CreateBatchWrite<Presupuesto>();
            foreach (var p in actuales.Where(p => !nuevos.ContainsKey(p.Categoria)))
                borrar.AddDeleteItem(p);

            var guardar = _dbContext.CreateBatchWrite<Presupuesto>();
            guardar.AddPutItems(nuevos.Select(kv => new Presupuesto { UsuarioID = userId, Categoria = kv.Key, Monto = kv.Value, FechaActualizacion = ahora }));

            await _dbContext.CreateMultiTableBatchWrite(borrar, guardar).ExecuteAsync();
            return Respuestas.Json(200, new { Success = true, Message = "Presupuestos guardados", Total = nuevos.Count });
        }

        // ====================================================
        // PAGOS FIJOS
        // ====================================================

        private async Task<APIGatewayHttpApiV2ProxyResponse> ObtenerFijos(APIGatewayHttpApiV2ProxyRequest req, string userId)
        {
            var mes = MesSolicitado(req);
            var hoy = ReglasGastos.AhoraLima().Date;
            var fijos = await _dbContext.QueryAsync<PagoFijo>(userId, new QueryConfig()).GetRemainingAsync();

            var items = fijos
                .Select(f =>
                {
                    var vence = ReglasGastos.VencimientoEnMes(f, mes);
                    var pagado = ReglasGastos.FijoPagadoEnMes(f, mes);
                    return new
                    {
                        f.FijoId, f.Nombre, f.Monto, f.Moneda, f.Categoria, f.DiaVencimiento, f.Activo, f.UltimoMesPagado,
                        Vencimiento = vence.ToString("yyyy-MM-dd"),
                        Pagado = pagado,
                        DiasRestantes = (int)(vence - hoy).TotalDays,
                        Vencido = !pagado && f.Activo && vence < hoy
                    };
                })
                .OrderBy(f => !f.Activo).ThenBy(f => f.Pagado).ThenBy(f => f.Vencimiento)
                .ToList();

            var pendientes = items.Where(f => f.Activo && !f.Pagado).ToList();
            var tc = pendientes.Any(f => f.Moneda == "USD") ? await Monedas.ObtenerUsdPenAsync(NullLogger.Instance) : 1m;

            return Respuestas.Json(200, new
            {
                Success = true,
                Mes = mes,
                Fijos = items,
                TotalMensual = items.Where(f => f.Activo).Sum(f => f.Moneda == "USD" ? f.Monto * tc : f.Monto),
                TotalPendiente = pendientes.Sum(f => f.Moneda == "USD" ? f.Monto * tc : f.Monto),
                CantidadPendiente = pendientes.Count
            });
        }

        private async Task<APIGatewayHttpApiV2ProxyResponse> GuardarFijo(APIGatewayHttpApiV2ProxyRequest req, string userId, bool esNuevo)
        {
            var p = Leer<PagoFijoRequest>(req);
            if (p == null) return Respuestas.Error(400, "El cuerpo de la petición está vacío.");

            PagoFijo? fijo;
            if (esNuevo)
            {
                if (string.IsNullOrWhiteSpace(p.Nombre) || p.Monto is not > 0 || p.DiaVencimiento is not (>= 1 and <= 31))
                    return Respuestas.Error(400, "Nombre, monto (> 0) y día de vencimiento (1 a 31) son obligatorios.");

                fijo = new PagoFijo
                {
                    UsuarioID = userId,
                    FijoId = Guid.NewGuid().ToString(),
                    FechaCreacion = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"),
                    Categoria = "Servicios"
                };
            }
            else
            {
                if (string.IsNullOrEmpty(p.FijoId)) return Respuestas.Error(400, "FijoId es obligatorio.");
                fijo = await _dbContext.LoadAsync<PagoFijo>(userId, p.FijoId);
                if (fijo == null) return Respuestas.Error(404, "Pago fijo no encontrado.");
            }

            if (!string.IsNullOrWhiteSpace(p.Nombre)) fijo.Nombre = p.Nombre.Trim();
            if (p.Monto is > 0) fijo.Monto = p.Monto.Value;
            if (!string.IsNullOrEmpty(p.Moneda)) fijo.Moneda = p.Moneda.ToUpper() == "USD" ? "USD" : "PEN";
            if (!string.IsNullOrWhiteSpace(p.Categoria)) fijo.Categoria = p.Categoria.Trim();
            if (p.DiaVencimiento is >= 1 and <= 31) fijo.DiaVencimiento = p.DiaVencimiento.Value;
            if (p.Activo.HasValue) fijo.Activo = p.Activo.Value;

            await _dbContext.SaveAsync(fijo);
            return Respuestas.Json(esNuevo ? 201 : 200, new { Success = true, Message = esNuevo ? "Pago fijo creado" : "Pago fijo actualizado", fijo.FijoId });
        }

        private async Task<APIGatewayHttpApiV2ProxyResponse> EliminarFijo(APIGatewayHttpApiV2ProxyRequest req, string userId)
        {
            var fijoId = Respuestas.Query(req, "fijoId");
            if (string.IsNullOrEmpty(fijoId)) return Respuestas.Error(400, "El parámetro 'fijoId' es obligatorio.");

            var fijo = await _dbContext.LoadAsync<PagoFijo>(userId, fijoId);
            if (fijo == null) return Respuestas.Error(404, "Pago fijo no encontrado.");

            await _dbContext.DeleteAsync(fijo);
            return Respuestas.Json(200, new { Success = true, Message = "Pago fijo eliminado" });
        }

        // Registra el gasto del mes y marca el pago fijo como pagado
        private async Task<APIGatewayHttpApiV2ProxyResponse> PagarFijo(APIGatewayHttpApiV2ProxyRequest req, string userId, ILambdaContext context)
        {
            var p = Leer<PagarFijoRequest>(req);
            if (p == null || string.IsNullOrEmpty(p.FijoId)) return Respuestas.Error(400, "FijoId es obligatorio.");

            var mes = ReglasGastos.EsMesValido(p.Mes) ? p.Mes! : ReglasGastos.MesActualLima();
            var fijo = await _dbContext.LoadAsync<PagoFijo>(userId, p.FijoId);
            if (fijo == null) return Respuestas.Error(404, "Pago fijo no encontrado.");
            if (ReglasGastos.FijoPagadoEnMes(fijo, mes)) return Respuestas.Error(409, "Este pago ya está registrado para ese mes.");

            // Si se paga el mes actual se usa la fecha de hoy; si es otro mes, su fecha de vencimiento
            var ahoraLima = ReglasGastos.AhoraLima();
            var fecha = mes == ReglasGastos.MesActualLima() ? ahoraLima : ReglasGastos.VencimientoEnMes(fijo, mes).Add(ahoraLima.TimeOfDay);

            var gasto = new Gasto
            {
                UsuarioID = userId,
                FechaOperacion = fecha.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                GastoId = Guid.NewGuid().ToString(),
                Comercio = fijo.Nombre,
                Categoria = fijo.Categoria,
                Banco = "Pago fijo",
                Monto = p.Monto is > 0 ? p.Monto.Value : fijo.Monto,
                Moneda = fijo.Moneda,
                CuotaIdRelacionada = "",
                Origen = "Fijo",
                Revisado = true
            };
            await Monedas.NormalizarAsync(gasto, context.Logger);

            fijo.UltimoMesPagado = mes;
            await Task.WhenAll(_dbContext.SaveAsync(gasto), _dbContext.SaveAsync(fijo));

            return Respuestas.Json(200, new { Success = true, Message = $"{fijo.Nombre} pagado", gasto.GastoId });
        }

        // ====================================================
        // HOGAR COMPARTIDO
        // ====================================================

        private async Task<APIGatewayHttpApiV2ProxyResponse> ObtenerHogar(APIGatewayHttpApiV2ProxyRequest req, string userId)
        {
            var mes = MesSolicitado(req);
            var configTask = Hogar.CargarConfig(_dbContext);
            var usuariosTask = Hogar.CargarUsuarios(_dbContext);
            await Task.WhenAll(configTask, usuariosTask);
            var config = configTask.Result;
            var miembros = usuariosTask.Result;

            // Gastos e ingresos del mes de cada miembro, en paralelo
            var datos = await Task.WhenAll(miembros.Select(async m =>
            {
                var gastos = (await GastosDe(m.UserId)).Where(g => ReglasGastos.CuentaEnMes(g, mes) && !ReglasGastos.EsAhorro(g)).ToList();
                var ingresos = await Hogar.IngresosDelMes(_dbContext, m.UserId, mes);
                return (Miembro: m, Gastos: gastos, Ingresos: ingresos);
            }));

            var compartidosPorMiembro = datos.ToDictionary(d => d.Miembro.UserId, d => d.Gastos.Where(g => ReglasGastos.EsCompartido(g, config)).ToList());
            var totalCompartido = compartidosPorMiembro.Values.Sum(l => l.Sum(ReglasGastos.MontoEnSoles));
            var proporciones = Hogar.Proporciones(config, datos.Select(d => new MiembroHogar(d.Miembro.UserId, d.Miembro.Nombre, d.Ingresos)).ToList());

            var resumenMiembros = datos.Select(d =>
            {
                var pagado = compartidosPorMiembro[d.Miembro.UserId].Sum(ReglasGastos.MontoEnSoles);
                var leCorresponde = Math.Round(totalCompartido * proporciones[d.Miembro.UserId], 2);
                return new
                {
                    d.Miembro.UserId,
                    d.Miembro.Nombre,
                    EsYo = d.Miembro.UserId == userId,
                    Ingresos = d.Ingresos,
                    GastosTotales = d.Gastos.Sum(ReglasGastos.MontoEnSoles),
                    PagadoCompartido = pagado,
                    Porcentaje = Math.Round(proporciones[d.Miembro.UserId] * 100, 1),
                    LeCorresponde = leCorresponde,
                    Saldo = Math.Round(pagado - leCorresponde, 2) // > 0: le deben; < 0: debe
                };
            }).ToList();

            // Transferencias sugeridas: los que deben le pagan a los que pusieron de más
            var deudores = resumenMiembros.Where(m => m.Saldo < 0).Select(m => (m.Nombre, Monto: -m.Saldo)).ToList();
            var acreedores = resumenMiembros.Where(m => m.Saldo > 0).Select(m => (m.Nombre, Monto: m.Saldo)).ToList();
            var transferencias = new List<object>();
            for (int i = 0, j = 0; i < deudores.Count && j < acreedores.Count;)
            {
                var monto = Math.Min(deudores[i].Monto, acreedores[j].Monto);
                if (monto >= 0.01m) transferencias.Add(new { De = deudores[i].Nombre, Para = acreedores[j].Nombre, Monto = Math.Round(monto, 2) });
                deudores[i] = (deudores[i].Nombre, deudores[i].Monto - monto);
                acreedores[j] = (acreedores[j].Nombre, acreedores[j].Monto - monto);
                if (deudores[i].Monto < 0.01m) i++;
                if (acreedores[j].Monto < 0.01m) j++;
            }

            var nombres = miembros.ToDictionary(m => m.UserId, m => m.Nombre);
            var movimientos = compartidosPorMiembro
                .SelectMany(kv => kv.Value.Select(g => new
                {
                    PagadoPor = nombres[kv.Key],
                    EsMio = kv.Key == userId,
                    g.Comercio, g.Categoria, g.Monto, g.Moneda,
                    MontoPEN = ReglasGastos.MontoEnSoles(g),
                    Fecha = ReglasGastos.FechaEfectiva(g)
                }))
                .OrderByDescending(g => g.Fecha)
                .Take(100)
                .ToList();

            var porCategoria = compartidosPorMiembro.Values.SelectMany(l => l)
                .GroupBy(g => g.Categoria ?? "Otros")
                .Select(grp => new { Categoria = grp.Key, Total = grp.Sum(ReglasGastos.MontoEnSoles) })
                .OrderByDescending(c => c.Total)
                .ToList();

            return Respuestas.Json(200, new
            {
                Success = true,
                Mes = mes,
                Configuracion = new { config.CategoriasCompartidas, config.ModoReparto, config.Porcentajes },
                // Categorías que usaron los miembros este mes (la IA crea categorías libres)
                CategoriasDelMes = datos.SelectMany(d => d.Gastos).Select(g => g.Categoria).Where(c => !string.IsNullOrEmpty(c)).Distinct().OrderBy(c => c).ToList(),
                TotalCompartido = totalCompartido,
                Miembros = resumenMiembros,
                Transferencias = transferencias,
                PorCategoria = porCategoria,
                Movimientos = movimientos
            });
        }

        private async Task<APIGatewayHttpApiV2ProxyResponse> GuardarHogar(APIGatewayHttpApiV2ProxyRequest req)
        {
            var p = Leer<ConfigHogarRequest>(req);
            if (p == null) return Respuestas.Error(400, "El cuerpo de la petición está vacío.");

            var config = await Hogar.CargarConfig(_dbContext);

            if (p.CategoriasCompartidas != null)
                config.CategoriasCompartidas = p.CategoriasCompartidas.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct().ToList();

            if (p.ModoReparto != null)
            {
                if (p.ModoReparto is not ("mitad" or "ingresos" or "fijo"))
                    return Respuestas.Error(400, "ModoReparto debe ser 'mitad', 'ingresos' o 'fijo'.");
                config.ModoReparto = p.ModoReparto;
            }

            if (p.Porcentajes != null)
            {
                if (p.Porcentajes.Values.Any(v => v < 0) || Math.Abs(p.Porcentajes.Values.Sum() - 100) > 0.5m)
                    return Respuestas.Error(400, "Los porcentajes deben ser positivos y sumar 100.");
                config.Porcentajes = p.Porcentajes;
            }

            config.FechaActualizacion = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
            await _dbContext.SaveAsync(config);
            return Respuestas.Json(200, new { Success = true, Message = "Configuración del hogar guardada" });
        }

        // Logger vacío para operaciones donde no hace falta registrar nada
        private sealed class NullLogger : ILambdaLogger
        {
            public static readonly NullLogger Instance = new();
            public void Log(string message) { }
            public void LogLine(string message) { }
        }
    }
}
