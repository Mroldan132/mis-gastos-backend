using Amazon.DynamoDBv2.DataModel;
using System.Collections.Generic;

namespace LectorGastosLambda.Models.DataBase
{
    // Reglas del hogar compartido. Hay un solo hogar con todos los usuarios de ConfiguracionUsuarios.
    [DynamoDBTable("ConfiguracionHogar")]
    public class ConfiguracionHogar
    {
        public const string IdPrincipal = "principal";

        [DynamoDBHashKey]
        public string HogarId { get; set; } = IdPrincipal;

        // Los gastos de estas categorías cuentan como compartidos (salvo que se marquen a mano)
        public List<string> CategoriasCompartidas { get; set; } = new() { "Comida", "Hogar", "Servicios" };

        // "mitad", "ingresos" o "fijo"
        public string ModoReparto { get; set; } = "mitad";

        // Solo para el modo "fijo": porcentaje que aporta cada UserId (deben sumar 100)
        public Dictionary<string, decimal> Porcentajes { get; set; } = new();

        public string FechaActualizacion { get; set; }
    }
}
