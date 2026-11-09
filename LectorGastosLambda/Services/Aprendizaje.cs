using Amazon.DynamoDBv2.DataModel;
using LectorGastosLambda.Models.DataBase;
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LectorGastosLambda.Services
{
    // La app aprende de las revisiones del usuario:
    //   - Si corrige la categoría o el nombre de un comercio, los siguientes gastos de ese comercio llegan así.
    //   - Si confirma la misma categoría varias veces seguidas, los siguientes se confirman solos.
    public static class Aprendizaje
    {
        public const int ConfirmacionesParaAutoConfirmar = 3;

        // Palabras que no distinguen a un comercio ("TAMBO+ SAC 0123" = "Tambo")
        private static readonly string[] Ruido = { "sac", "sa", "saa", "eirl", "srl", "sas", "peru", "lima", "tienda", "local" };

        // Clave estable para un comercio: minúsculas, sin tildes, sin números ni signos, sin palabras de relleno
        public static string? Clave(string? comercio)
        {
            if (string.IsNullOrWhiteSpace(comercio)) return null;
            var texto = new string(comercio.ToLowerInvariant().Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                .Select(c => char.IsLetter(c) ? c : ' ').ToArray());
            var palabras = texto.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => !Ruido.Contains(p)).ToArray();
            // Sin espacios: "Plaza Vea" y "PLAZAVEA" son el mismo comercio
            return palabras.Length == 0 ? null : string.Concat(palabras);
        }

        public static async Task<ReglaComercio?> BuscarAsync(IDynamoDBContext db, string userId, string? comercio)
        {
            var clave = Clave(comercio);
            return clave == null ? null : await db.LoadAsync<ReglaComercio>(userId, clave);
        }

        // Aplica lo aprendido a un gasto recién leído de un correo. Devuelve true si se confirmó solo.
        public static async Task<bool> AplicarAsync(IDynamoDBContext db, Gasto gasto)
        {
            var regla = await BuscarAsync(db, gasto.UsuarioID, gasto.ComercioOriginal ?? gasto.Comercio);
            if (regla == null) return false;

            if (!string.IsNullOrWhiteSpace(regla.NombreComercio)) gasto.Comercio = regla.NombreComercio;
            gasto.Categoria = regla.Categoria;

            if (regla.Confirmaciones < ConfirmacionesParaAutoConfirmar) return false;
            gasto.Revisado = true;
            gasto.AutoConfirmado = true;
            return true;
        }

        // Registra una revisión del usuario: misma categoría suma una confirmación; otra categoría empieza de nuevo
        public static async Task RegistrarAsync(IDynamoDBContext db, Gasto gasto)
        {
            var clave = Clave(gasto.ComercioOriginal ?? gasto.Comercio);
            if (clave == null) return;

            var regla = await db.LoadAsync<ReglaComercio>(gasto.UsuarioID, clave)
                ?? new ReglaComercio { UsuarioID = gasto.UsuarioID, Comercio = clave };

            regla.Confirmaciones = regla.Categoria == gasto.Categoria ? regla.Confirmaciones + 1 : 1;
            regla.Categoria = gasto.Categoria;
            regla.NombreComercio = gasto.Comercio;
            regla.FechaActualizacion = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            await db.SaveAsync(regla);
        }
    }
}
