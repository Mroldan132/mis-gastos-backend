using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using LectorGastosLambda.Models.DataBase;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LectorGastosLambda.Services
{
    public record MiembroHogar(string UserId, string Nombre, decimal Ingresos);

    // Quiénes forman el hogar y cómo se reparten los gastos y metas entre ellos
    public static class Hogar
    {
        public static async Task<ConfiguracionHogar> CargarConfig(IDynamoDBContext db) =>
            await db.LoadAsync<ConfiguracionHogar>(ConfiguracionHogar.IdPrincipal) ?? new ConfiguracionHogar();

        public static async Task<List<UsuarioConfig>> CargarUsuarios(IDynamoDBContext db) =>
            (await db.ScanAsync<UsuarioConfig>(new List<ScanCondition>()).GetRemainingAsync())
                .Where(u => !string.IsNullOrEmpty(u.UserId))
                .OrderBy(u => u.UserId)
                .ToList();

        public static async Task<decimal> IngresosDelMes(IDynamoDBContext db, string userId, string mes)
        {
            var ingresos = await db.QueryAsync<Ingreso>(userId, new QueryConfig
            {
                QueryFilter = new List<ScanCondition> { new ScanCondition("FechaOperacion", ScanOperator.BeginsWith, mes) }
            }).GetRemainingAsync();
            return ingresos.Sum(i => i.Monto);
        }

        // Miembros del hogar con sus ingresos del mes (solo se consultan si hacen falta para el reparto)
        public static async Task<List<MiembroHogar>> CargarMiembros(IDynamoDBContext db, IEnumerable<UsuarioConfig> usuarios, string mes, bool conIngresos)
        {
            var miembros = await Task.WhenAll(usuarios
                .Where(u => !string.IsNullOrEmpty(u.UserId))
                .OrderBy(u => u.UserId)
                .Select(async u => new MiembroHogar(u.UserId, u.Nombre ?? u.Email, conIngresos ? await IngresosDelMes(db, u.UserId, mes) : 0m)));
            return miembros.ToList();
        }

        public static async Task<List<MiembroHogar>> CargarMiembros(IDynamoDBContext db, ConfiguracionHogar config, string mes) =>
            await CargarMiembros(db, await CargarUsuarios(db), mes, conIngresos: config.ModoReparto == "ingresos");

        // Proporción (0 a 1) que le toca a cada miembro según el modo de reparto del hogar
        public static Dictionary<string, decimal> Proporciones(ConfiguracionHogar config, IReadOnlyCollection<MiembroHogar> miembros)
        {
            var iguales = miembros.ToDictionary(m => m.UserId, _ => miembros.Count > 0 ? 1m / miembros.Count : 0m);

            if (config.ModoReparto == "ingresos")
            {
                var total = miembros.Sum(m => m.Ingresos);
                return total > 0 ? miembros.ToDictionary(m => m.UserId, m => m.Ingresos / total) : iguales;
            }

            if (config.ModoReparto == "fijo" && config.Porcentajes != null)
            {
                var suma = miembros.Sum(m => config.Porcentajes.GetValueOrDefault(m.UserId));
                if (suma > 0) return miembros.ToDictionary(m => m.UserId, m => config.Porcentajes.GetValueOrDefault(m.UserId) / suma);
            }

            return iguales;
        }
    }
}
