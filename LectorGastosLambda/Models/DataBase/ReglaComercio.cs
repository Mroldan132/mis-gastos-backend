using Amazon.DynamoDBv2.DataModel;

namespace LectorGastosLambda.Models.DataBase
{
    // Lo que la app aprendió de las correcciones del usuario para un comercio
    // (tal como lo escribe la IA al leer el correo del banco)
    [DynamoDBTable("ReglasComercio")]
    public class ReglaComercio
    {
        [DynamoDBHashKey]
        public string UsuarioID { get; set; } = "";

        // Nombre del comercio normalizado (ver Aprendizaje.Clave)
        [DynamoDBRangeKey]
        public string Comercio { get; set; } = "";

        // Cómo lo dejó el usuario la última vez
        public string NombreComercio { get; set; } = "";
        public string Categoria { get; set; } = "";

        // Veces seguidas que el usuario confirmó esta categoría para el comercio
        public int Confirmaciones { get; set; }

        public string FechaActualizacion { get; set; } = "";
    }
}
