namespace LectorGastosLambda.DTOs.Requests
{
    // Solo se cambian los campos que vienen con valor
    public class EditarGastoRequest
    {
        public string FechaOperacion { get; set; } // Llave para encontrar el gasto (no se puede cambiar)
        public string? Comercio { get; set; }
        public string? Categoria { get; set; }
        public decimal? Monto { get; set; }
        public string? Moneda { get; set; }
        public decimal? TipoCambio { get; set; }
        public string? Compartido { get; set; } // "auto" (según la categoría), "si" o "no"
        public bool? Revisado { get; set; }
    }

    public class TextoGastoRequest
    {
        public string Texto { get; set; }
    }
}
