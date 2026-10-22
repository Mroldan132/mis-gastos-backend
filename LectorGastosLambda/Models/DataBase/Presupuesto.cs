using Amazon.DynamoDBv2.DataModel;

namespace LectorGastosLambda.Models.DataBase
{
    // Tope mensual de gasto por categoría (se repite cada mes)
    [DynamoDBTable("PresupuestosHogar")]
    public class Presupuesto
    {
        [DynamoDBHashKey]
        public string UsuarioID { get; set; }

        [DynamoDBRangeKey]
        public string Categoria { get; set; }

        public decimal Monto { get; set; }
        public string FechaActualizacion { get; set; }
    }
}
