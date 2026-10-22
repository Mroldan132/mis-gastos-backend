using LectorGastosLambda.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LectorGastosLambda.Services
{
    // Reglas de negocio compartidas por Dashboard, Plan y el resumen semanal
    public static class ReglasGastos
    {
        public const string CategoriaAhorro = "Ahorro";

        public static decimal MontoEnSoles(Gasto g) => g.MontoPEN > 0 ? g.MontoPEN : g.Monto;

        // Fecha en la que el gasto pesa en el bolsillo: las cuotas cuentan cuando se pagan
        public static string FechaEfectiva(Gasto g) => g.EsPagoDeCuota ? g.FechaActualizacion : g.FechaOperacion;

        // Gastos que cuentan en un mes "YYYY-MM": normales del mes + cuotas pagadas ese mes, sin los gastos fraccionados
        public static bool CuentaEnMes(Gasto g, string mes)
        {
            if (g.EsGastoReferencia) return false;
            if (g.EsPagoDeCuota) return g.Estado == "Pagado" && (g.FechaActualizacion ?? "").StartsWith(mes);
            return (g.FechaOperacion ?? "").StartsWith(mes);
        }

        // Igual que CuentaEnMes pero para un rango de fechas [desde, hasta)
        public static bool CuentaEnRango(Gasto g, DateTime desde, DateTime hasta)
        {
            if (g.EsGastoReferencia) return false;
            if (g.EsPagoDeCuota && g.Estado != "Pagado") return false;
            if (!DateTime.TryParse(FechaEfectiva(g), CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha)) return false;
            return fecha >= desde && fecha < hasta;
        }

        public static bool EsAhorro(Gasto g) => g.Categoria == CategoriaAhorro;

        public static bool EsCompartido(Gasto g, ConfiguracionHogar config) =>
            g.EsCompartido ?? (config.CategoriasCompartidas?.Contains(g.Categoria ?? "") ?? false);

        public static string MesAnterior(string mes, int meses = 1)
        {
            var fecha = DateTime.ParseExact(mes + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).AddMonths(-meses);
            return fecha.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        }

        public static bool EsMesValido(string? mes) =>
            !string.IsNullOrEmpty(mes) && DateTime.TryParseExact(mes + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

        // Fecha de vencimiento de un pago fijo en un mes dado (si el día no existe, el último día del mes)
        public static DateTime VencimientoEnMes(PagoFijo fijo, string mes)
        {
            var inicio = DateTime.ParseExact(mes + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var dia = Math.Clamp(fijo.DiaVencimiento, 1, DateTime.DaysInMonth(inicio.Year, inicio.Month));
            return new DateTime(inicio.Year, inicio.Month, dia);
        }

        public static bool FijoPagadoEnMes(PagoFijo fijo, string mes) =>
            !string.IsNullOrEmpty(fijo.UltimoMesPagado) && string.CompareOrdinal(fijo.UltimoMesPagado, mes) >= 0;

        // Hora actual de Lima (UTC-5, sin horario de verano)
        public static DateTime AhoraLima() => DateTime.UtcNow.AddHours(-5);

        public static string MesActualLima() => AhoraLima().ToString("yyyy-MM", CultureInfo.InvariantCulture);

        public static Dictionary<string, decimal> TotalesPorCategoria(IEnumerable<Gasto> gastos) =>
            gastos.GroupBy(g => g.Categoria ?? "Otros").ToDictionary(grp => grp.Key, grp => grp.Sum(MontoEnSoles));
    }
}
