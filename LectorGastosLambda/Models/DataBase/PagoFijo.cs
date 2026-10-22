using Amazon.DynamoDBv2.DataModel;

namespace LectorGastosLambda.Models.DataBase
{
    // Pago que se repite cada mes (alquiler, luz, internet, suscripciones…)
    [DynamoDBTable("PagosFijosHogar")]
    public class PagoFijo
    {
        [DynamoDBHashKey]
        public string UsuarioID { get; set; }

        [DynamoDBRangeKey]
        public string FijoId { get; set; }

        public string Nombre { get; set; }
        public decimal Monto { get; set; }
        public string Moneda { get; set; } = "PEN";
        public string Categoria { get; set; }
        public int DiaVencimiento { get; set; } // 1 a 31; si el mes es más corto vence el último día
        public bool Activo { get; set; } = true;

        // Último mes pagado en formato "YYYY-MM"
        public string UltimoMesPagado { get; set; }
        public string FechaCreacion { get; set; }
    }
}
