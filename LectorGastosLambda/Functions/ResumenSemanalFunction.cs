using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.Lambda.Core;
using LectorGastosLambda.Models.DataBase;
using LectorGastosLambda.Services;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace LectorGastosLambda.Functions
{
    public class OpcionesResumen
    {
        public bool DryRun { get; set; }          // true: arma el correo y lo registra en el log sin enviarlo
        public string? SoloUsuario { get; set; }  // UserId para enviar solo a una persona
    }

    // Cada domingo por la noche envía a cada usuario un resumen de su semana desde su propio Gmail
    public class ResumenSemanalFunction
    {
        private static readonly AmazonDynamoDBClient _dynamoClient = new AmazonDynamoDBClient(Amazon.RegionEndpoint.USEast2);
        private static readonly DynamoDBContext _dbContext = new DynamoDBContext(_dynamoClient);
        private static readonly CultureInfo Peru = CultureInfo.GetCultureInfo("es-PE");

        public async Task<string> ResumenHandler(JsonElement input, ILambdaContext context)
        {
            var opciones = new OpcionesResumen();
            try { opciones = input.Deserialize<OpcionesResumen>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? opciones; }
            catch (JsonException) { /* Evento de EventBridge: se usan las opciones por defecto */ }

            var usuarios = await _dbContext.ScanAsync<UsuarioConfig>(new List<ScanCondition>()).GetRemainingAsync();
            var hogar = await CargarContextoHogar(usuarios, context.Logger);
            int enviados = 0;

            foreach (var usuario in usuarios.Where(u => !string.IsNullOrEmpty(u.UserId) && !string.IsNullOrEmpty(u.Email)))
            {
                if (opciones.SoloUsuario != null && usuario.UserId != opciones.SoloUsuario) continue;

                try
                {
                    var (asunto, html) = await ArmarResumen(usuario, hogar);

                    if (opciones.DryRun)
                    {
                        context.Logger.LogLine($"[Prueba] Resumen para {usuario.Nombre}: {asunto}\n{html}");
                        continue;
                    }

                    await EnviarCorreo(usuario, asunto, html);
                    enviados++;
                    context.Logger.LogLine($"Resumen semanal enviado a {usuario.Nombre}.");
                }
                catch (AuthenticationException ex)
                {
                    context.Logger.LogError($"Gmail rechazó el envío de {usuario.Email} (¿contraseña de aplicación revocada?): {ex.Message}");
                }
                catch (Exception ex)
                {
                    context.Logger.LogError($"Error armando o enviando el resumen de {usuario.Email}: {ex}");
                }
            }

            return opciones.DryRun ? "Prueba completada (sin envíos)." : $"Resúmenes enviados: {enviados}.";
        }

        // Metas activas del hogar y cómo se reparten entre los miembros (es igual para todos, se carga una sola vez)
        private sealed record ContextoHogar(List<Meta> MetasActivas, Dictionary<string, decimal> Proporciones);

        private static async Task<ContextoHogar> CargarContextoHogar(List<UsuarioConfig> usuarios, ILambdaLogger logger)
        {
            try
            {
                var config = await Hogar.CargarConfig(_dbContext);
                var miembros = await Hogar.CargarMiembros(_dbContext, usuarios, ReglasGastos.MesActualLima(), conIngresos: config.ModoReparto == "ingresos");
                var metas = await _dbContext.QueryAsync<Meta>(Meta.DuenoHogar, new QueryConfig()).GetRemainingAsync();
                return new ContextoHogar(
                    metas.Where(m => m.Estado == Meta.EstadoActiva).OrderBy(m => m.FechaCreacion).Take(4).ToList(),
                    Hogar.Proporciones(config, miembros));
            }
            catch (Exception ex)
            {
                // Si falla, el resumen sale igual pero sin la sección de metas del hogar
                logger.LogError($"No se pudieron cargar las metas del hogar: {ex.Message}");
                return new ContextoHogar(new List<Meta>(), new Dictionary<string, decimal>());
            }
        }

        private static async Task<(string Asunto, string Html)> ArmarResumen(UsuarioConfig usuario, ContextoHogar hogar)
        {
            var hoy = ReglasGastos.AhoraLima().Date;
            var inicioSemana = hoy.AddDays(-6);         // Los últimos 7 días, incluido hoy
            var finSemana = hoy.AddDays(1);
            var inicioAnterior = inicioSemana.AddDays(-7);
            var mes = ReglasGastos.MesActualLima();

            var gastosTask = _dbContext.QueryAsync<Gasto>(usuario.UserId, new QueryConfig()).GetRemainingAsync();
            var presupuestosTask = _dbContext.QueryAsync<Presupuesto>(usuario.UserId, new QueryConfig()).GetRemainingAsync();
            var fijosTask = _dbContext.QueryAsync<PagoFijo>(usuario.UserId, new QueryConfig()).GetRemainingAsync();
            await Task.WhenAll(gastosTask, presupuestosTask, fijosTask);
            var gastos = gastosTask.Result;

            var semana = gastos.Where(g => ReglasGastos.CuentaEnRango(g, inicioSemana, finSemana) && !ReglasGastos.EsAhorro(g)).ToList();
            var anterior = gastos.Where(g => ReglasGastos.CuentaEnRango(g, inicioAnterior, inicioSemana) && !ReglasGastos.EsAhorro(g)).ToList();
            var totalSemana = semana.Sum(ReglasGastos.MontoEnSoles);
            var totalAnterior = anterior.Sum(ReglasGastos.MontoEnSoles);

            var porCategoria = ReglasGastos.TotalesPorCategoria(semana).OrderByDescending(kv => kv.Value).Take(5).ToList();
            var anteriorPorCategoria = ReglasGastos.TotalesPorCategoria(anterior);
            var mayorAlza = ReglasGastos.TotalesPorCategoria(semana)
                .Select(kv => (Categoria: kv.Key, Diferencia: kv.Value - anteriorPorCategoria.GetValueOrDefault(kv.Key)))
                .Where(x => x.Diferencia > 0)
                .OrderByDescending(x => x.Diferencia)
                .FirstOrDefault();

            // Presupuestos del mes al 80 % o más
            var gastadoMes = ReglasGastos.TotalesPorCategoria(gastos.Where(g => ReglasGastos.CuentaEnMes(g, mes)));
            var alertas = presupuestosTask.Result
                .Select(p => (p.Categoria, p.Monto, Gastado: gastadoMes.GetValueOrDefault(p.Categoria)))
                .Where(p => p.Monto > 0 && p.Gastado / p.Monto >= 0.8m)
                .OrderByDescending(p => p.Gastado / p.Monto)
                .ToList();

            // Lo que vence en los próximos 7 días: pagos fijos sin pagar y cuotas pendientes
            var limite = hoy.AddDays(7);
            var vencimientos = new List<(DateTime Fecha, string Nombre, decimal Monto, string Moneda)>();
            foreach (var f in fijosTask.Result.Where(f => f.Activo))
            {
                foreach (var m in new[] { mes, hoy.AddMonths(1).ToString("yyyy-MM") }.Distinct())
                {
                    var vence = ReglasGastos.VencimientoEnMes(f, m);
                    if (!ReglasGastos.FijoPagadoEnMes(f, m) && vence <= limite && (vence >= hoy || m == mes))
                        vencimientos.Add((vence, f.Nombre, f.Monto, f.Moneda));
                }
            }
            foreach (var c in gastos.Where(g => g.EsPagoDeCuota && g.Estado != "Pagado"))
            {
                if (DateTime.TryParse(c.FechaOperacion, CultureInfo.InvariantCulture, DateTimeStyles.None, out var vence) && vence.Date <= limite)
                    vencimientos.Add((vence.Date, c.Comercio, c.Monto, c.Moneda ?? "PEN"));
            }
            vencimientos = vencimientos.OrderBy(v => v.Fecha).Take(8).ToList();

            var porRevisar = gastos.Count(g => g.Origen == "Correo" && !g.Revisado);

            // Metas del hogar: avance total y cuánto le falta a esta persona para cubrir su parte
            var miProporcion = hogar.Proporciones.GetValueOrDefault(usuario.UserId);
            var metasHogar = hogar.MetasActivas.Select(m =>
            {
                var miAporte = m.Aportes?.GetValueOrDefault(usuario.UserId) ?? 0;
                var miParte = Math.Round(m.MontoObjetivo * miProporcion, 2);
                return (Nombre: $"{m.Icono} {m.Nombre}".Trim(), Actual: m.MontoActual, Objetivo: m.MontoObjetivo, MeFalta: Math.Max(0, miParte - miAporte));
            }).ToList();

            var variacion = totalAnterior > 0 ? (totalSemana - totalAnterior) / totalAnterior * 100 : (decimal?)null;
            var asunto = $"Tu semana: {Monto(totalSemana)} en gastos" +
                         (variacion.HasValue ? $" ({(variacion >= 0 ? "+" : "")}{variacion:0}% vs. la anterior)" : "");

            var html = Plantilla(usuario.Nombre, inicioSemana, hoy, totalSemana, variacion, porCategoria, mayorAlza, alertas, vencimientos, metasHogar, porRevisar, semana.Count);
            return (asunto, html);
        }

        private static async Task EnviarCorreo(UsuarioConfig usuario, string asunto, string html)
        {
            var mensaje = new MimeMessage();
            mensaje.From.Add(new MailboxAddress("HogarApp", usuario.Email));
            mensaje.To.Add(new MailboxAddress(usuario.Nombre ?? usuario.Email, usuario.Email));
            mensaje.Subject = asunto;
            mensaje.Body = new BodyBuilder { HtmlBody = html }.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync("smtp.gmail.com", 465, SecureSocketOptions.SslOnConnect);
            await smtp.AuthenticateAsync(usuario.Email, usuario.AppPassword);
            await smtp.SendAsync(mensaje);
            await smtp.DisconnectAsync(true);
        }

        private static string Monto(decimal valor, string moneda = "PEN") =>
            (moneda == "USD" ? "$ " : "S/ ") + valor.ToString("N2", Peru);

        private static string E(string? texto) => WebUtility.HtmlEncode(texto ?? "");

        // Correo en HTML con estilos en línea (los clientes de correo ignoran las hojas de estilo)
        private static string Plantilla(string? nombre, DateTime desde, DateTime hasta, decimal total, decimal? variacion,
            List<KeyValuePair<string, decimal>> categorias, (string Categoria, decimal Diferencia) mayorAlza,
            List<(string Categoria, decimal Monto, decimal Gastado)> alertas,
            List<(DateTime Fecha, string Nombre, decimal Monto, string Moneda)> vencimientos,
            List<(string Nombre, decimal Actual, decimal Objetivo, decimal MeFalta)> metasHogar, int porRevisar, int cantidad)
        {
            const string tinta = "#F4F4F5", suave = "#A1A1AA", tenue = "#71717A", borde = "#26262B", superficie = "#141417", acento = "#D4F75A";
            var sb = new StringBuilder();
            string Seccion(string titulo) => $"<p style=\"margin:28px 0 10px;font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:{tenue}\">{titulo}</p>";
            string Fila(string izq, string der, string colorDer = tinta) =>
                $"<tr><td style=\"padding:10px 0;border-top:1px solid {borde};color:{suave};font-size:14px\">{izq}</td>" +
                $"<td style=\"padding:10px 0;border-top:1px solid {borde};color:{colorDer};font-size:14px;text-align:right;white-space:nowrap\">{der}</td></tr>";

            var primerNombre = E((nombre ?? "").Split(' ', '(').FirstOrDefault());
            sb.Append($"<div style=\"background:#09090B;padding:32px 16px;font-family:-apple-system,'Segoe UI',Helvetica,Arial,sans-serif\">");
            sb.Append($"<div style=\"max-width:520px;margin:0 auto;background:{superficie};border:1px solid {borde};border-radius:20px;padding:28px\">");
            sb.Append($"<p style=\"margin:0;font-size:13px;color:{tenue}\">HogarApp · {desde.ToString("d MMM", Peru)} – {hasta.ToString("d MMM", Peru)}</p>");
            sb.Append($"<h1 style=\"margin:8px 0 0;font-family:Georgia,serif;font-weight:normal;font-size:30px;color:{tinta}\">Hola {primerNombre}, así fue tu semana</h1>");

            sb.Append($"<div style=\"margin-top:24px;padding:20px;border-radius:14px;background:#09090B;border:1px solid {borde}\">");
            sb.Append($"<p style=\"margin:0;font-size:12px;color:{tenue}\">Gastaste en {cantidad} movimientos</p>");
            sb.Append($"<p style=\"margin:6px 0 0;font-size:34px;font-weight:600;color:{tinta}\">{Monto(total)}</p>");
            if (variacion.HasValue)
            {
                var sube = variacion > 0;
                sb.Append($"<p style=\"margin:6px 0 0;font-size:14px;color:{(sube ? "#FB7185" : "#34D399")}\">{(sube ? "▲" : "▼")} {Math.Abs(variacion.Value):0}% {(sube ? "más" : "menos")} que la semana anterior</p>");
            }
            if (mayorAlza.Categoria != null)
                sb.Append($"<p style=\"margin:10px 0 0;font-size:13px;color:{suave}\">Lo que más subió: <b style=\"color:{tinta}\">{E(mayorAlza.Categoria)}</b> (+{Monto(mayorAlza.Diferencia)})</p>");
            sb.Append("</div>");

            if (categorias.Count > 0)
            {
                sb.Append(Seccion("En qué se fue"));
                sb.Append("<table width=\"100%\" cellpadding=\"0\" cellspacing=\"0\">");
                foreach (var c in categorias) sb.Append(Fila(E(c.Key), Monto(c.Value)));
                sb.Append("</table>");
            }

            if (vencimientos.Count > 0)
            {
                sb.Append(Seccion("Vence en los próximos 7 días"));
                sb.Append("<table width=\"100%\" cellpadding=\"0\" cellspacing=\"0\">");
                foreach (var v in vencimientos)
                {
                    var vencido = v.Fecha < ReglasGastos.AhoraLima().Date;
                    sb.Append(Fila($"{E(v.Nombre)} <span style=\"color:{tenue}\">· {(vencido ? "venció el " : "")}{v.Fecha.ToString("ddd d", Peru)}</span>", Monto(v.Monto, v.Moneda), vencido ? "#FB7185" : tinta));
                }
                sb.Append("</table>");
            }

            if (alertas.Count > 0)
            {
                sb.Append(Seccion("Presupuestos del mes"));
                sb.Append("<table width=\"100%\" cellpadding=\"0\" cellspacing=\"0\">");
                foreach (var a in alertas)
                {
                    var pct = a.Gastado / a.Monto * 100;
                    sb.Append(Fila($"{E(a.Categoria)} <span style=\"color:{tenue}\">· {Monto(a.Gastado)} de {Monto(a.Monto)}</span>", $"{pct:0}%", pct >= 100 ? "#FB7185" : "#FBBF24"));
                }
                sb.Append("</table>");
            }

            if (metasHogar.Count > 0)
            {
                sb.Append(Seccion("Metas del hogar"));
                sb.Append("<table width=\"100%\" cellpadding=\"0\" cellspacing=\"0\">");
                foreach (var m in metasHogar)
                {
                    var pct = m.Objetivo > 0 ? Math.Min(m.Actual / m.Objetivo, 1) * 100 : 0;
                    var detalle = m.MeFalta > 0 ? $"te faltan {Monto(m.MeFalta)} de tu parte" : "tu parte está completa";
                    sb.Append(Fila($"{E(m.Nombre)} <span style=\"color:{tenue}\">· {Monto(m.Actual)} de {Monto(m.Objetivo)} · {detalle}</span>", $"{pct:0}%", pct >= 100 ? "#34D399" : acento));
                }
                sb.Append("</table>");
            }

            if (porRevisar > 0)
                sb.Append($"<p style=\"margin:24px 0 0;padding:14px 16px;border-radius:12px;background:rgba(212,247,90,.08);border:1px solid rgba(212,247,90,.2);font-size:14px;color:{tinta}\">Tienes <b>{porRevisar}</b> {(porRevisar == 1 ? "gasto leído" : "gastos leídos")} por la IA sin revisar.</p>");

            var appUrl = Environment.GetEnvironmentVariable("APP_URL");
            if (!string.IsNullOrEmpty(appUrl))
                sb.Append($"<p style=\"margin:28px 0 0\"><a href=\"{E(appUrl)}\" style=\"display:inline-block;padding:12px 20px;border-radius:12px;background:{acento};color:#09090B;font-weight:600;font-size:14px;text-decoration:none\">Abrir HogarApp</a></p>");

            sb.Append($"<p style=\"margin:28px 0 0;font-size:12px;color:{tenue}\">Este resumen se envía cada domingo desde tu propia cuenta de Gmail.</p>");
            sb.Append("</div></div>");
            return sb.ToString();
        }
    }
}
