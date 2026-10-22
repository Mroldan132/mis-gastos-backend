using Amazon.DynamoDBv2.DataModel;

namespace LectorGastosLambda.Models.DataBase
{
    [DynamoDBTable("GastosFamiliares")]
    public class Gasto
    {
        [DynamoDBHashKey]
        public string UsuarioID { get; set; }

        [DynamoDBRangeKey]
        public string FechaOperacion { get; set; } 

        public decimal Monto { get; set; }
        public string Moneda { get; set; }
        public decimal? TipoCambio { get; set; }
        public decimal MontoPEN { get; set; }
        public string Comercio { get; set; }
        public string Categoria { get; set; }
        public string Banco { get; set; }

        public string GastoId { get; set; }
        public bool EsPagoDeCuota { get; set; }
        public string CuotaIdRelacionada { get; set; }

        public bool EsGastoReferencia { get; set; }
        public string Estado { get; set; } = "Pendiente";
        public string FechaActualizacion { get; set; }

        // "Correo" (leído por la IA), "Manual", "Texto" (registro rápido), "Fijo" (pago fijo)
        public string Origen { get; set; }
        // Los gastos leídos por la IA quedan sin revisar hasta que el usuario los confirma o corrige
        public bool Revisado { get; set; }
        // null = se decide por las categorías compartidas del hogar; true/false = elegido a mano
        public bool? EsCompartido { get; set; }
    }
}