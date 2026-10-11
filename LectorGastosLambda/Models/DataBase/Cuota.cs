using Amazon.DynamoDBv2.DataModel;

namespace LectorGastosLambda.Models.DataBase
{
    [DynamoDBTable("CuotasFamiliares")]
    public class Cuota
    {
        [DynamoDBHashKey]
        public string CuotaId { get; set; } // El ID único de esta deuda

        public string UsuarioID { get; set; } // "1" o "2"
        public string GastoPadreId { get; set; } // El ID del gasto grande de 1200
        public string Descripcion { get; set; } // Ej. "Refrigeradora Electrolux"
        public decimal MontoTotal { get; set; } // 1200
        public int CantidadCuotas { get; set; } // 6
        public string FechaCreacion { get; set; }
    }
}