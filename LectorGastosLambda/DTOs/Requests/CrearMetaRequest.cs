namespace LectorGastosLambda.DTOs.Requests
{
    public class CrearMetaRequest
    {
        public string UserId { get; set; }
        public string Nombre { get; set; }
        public string Categoria { get; set; }
        public decimal MontoObjetivo { get; set; }
        public string Icono { get; set; }
        // true: la meta es de todo el hogar y cualquier miembro puede aportar
        public bool Compartida { get; set; }
    }
}
