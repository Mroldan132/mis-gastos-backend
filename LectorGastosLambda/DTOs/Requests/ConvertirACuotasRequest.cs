namespace LectorGastosLambda.DTOs.Requests
{
    public class ConvertirACuotasRequest
    {
        public string UserId { get; set; }
        public string FechaOperacionOriginal { get; set; }
        public int CantidadCuotas { get; set; }
    }
}