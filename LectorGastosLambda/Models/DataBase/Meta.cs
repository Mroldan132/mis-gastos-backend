using Amazon.DynamoDBv2.DataModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LectorGastosLambda.Models.DataBase
{
    [DynamoDBTable("MetasHogar")] 
    public class Meta
    {
        [DynamoDBHashKey]
        public string UsuarioID { get; set; }

        [DynamoDBRangeKey]
        public string MetaId { get; set; }

        public string Nombre { get; set; }
        public string Categoria { get; set; }
        public decimal MontoObjetivo { get; set; }
        public decimal MontoActual { get; set; }
        public string Icono { get; set; }
        public string FechaCreacion { get; set; }
        public string Estado { get; set; } = "Activa"; // Activa o Completada
    }
}
