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
    }
}