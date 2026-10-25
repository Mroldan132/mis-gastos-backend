using Amazon.DynamoDBv2.DataModel;
using System.Collections.Generic;

namespace LectorGastosLambda.Models.DataBase
{
    // Metas de ahorro. Las personales se guardan bajo el UserId; las del hogar bajo DuenoHogar
    [DynamoDBTable("MetasHogar")]
    public class Meta
    {
        public const string DuenoHogar = "hogar";
        public const string EstadoActiva = "Activa";
        public const string EstadoCompletada = "Completada";

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
        public string Estado { get; set; } = EstadoActiva; // Activa o Completada

        // Solo metas del hogar: cuánto ha puesto cada UserId (la suma es MontoActual)
        public Dictionary<string, decimal> Aportes { get; set; } = new();
        // UserId de quien creó la meta (solo metas del hogar)
        public string? CreadaPor { get; set; }
        public string? FechaActualizacion { get; set; }

        [DynamoDBIgnore]
        public bool EsCompartida => UsuarioID == DuenoHogar;
    }
}
