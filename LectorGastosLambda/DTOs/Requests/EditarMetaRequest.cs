namespace LectorGastosLambda.DTOs.Requests
{
    // Solo se cambian los campos que vienen con valor
    public class EditarMetaRequest
    {
        public string MetaId { get; set; }
        public string? Nombre { get; set; }
        public string? Categoria { get; set; }
        public decimal? MontoObjetivo { get; set; }
        public string? Icono { get; set; }
        public string? Estado { get; set; } // "Activa" o "Completada"
    }
}
