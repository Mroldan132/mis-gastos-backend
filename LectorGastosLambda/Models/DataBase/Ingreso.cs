using Amazon.DynamoDBv2.DataModel;

namespace LectorGastosLambda.Models.DataBase
{
    [DynamoDBTable("IngresosFamiliares")]
    public class Ingreso
    {
        [DynamoDBHashKey]
        public string UsuarioID { get; set; }

        [DynamoDBRangeKey]
        public string FechaOperacion { get; set; } // Formato "YYYY-MM-DDTHH:mm:ss"

        public string IngresoId { get; set; }
        public decimal Monto { get; set; }
        public string Descripcion { get; set; } // Ej: "Sueldo", "Venta de laptop"
        public string Moneda { get; set; }
    }
}