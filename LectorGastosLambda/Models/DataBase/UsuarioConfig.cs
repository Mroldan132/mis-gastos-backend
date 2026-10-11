using Amazon.DynamoDBv2.DataModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LectorGastosLambda.Models.DataBase
{
    [DynamoDBTable("ConfiguracionUsuarios")]
    public class UsuarioConfig
    {
        [DynamoDBHashKey]
        public string Email { get; set; }

        [DynamoDBProperty]
        public string Nombre { get; set; }

        [DynamoDBProperty]
        public string UserId { get; set; }

        [DynamoDBProperty]
        public string PasswordHash { get; set; }

        [DynamoDBProperty]
        public string AppPassword { get; set; }

        [DynamoDBProperty]
        public string UltimaRevision { get; set; }
    }
}
