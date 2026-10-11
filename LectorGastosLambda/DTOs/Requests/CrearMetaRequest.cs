using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LectorGastosLambda.DTOs.Requests
{
    public class CrearMetaRequest
    {
        public string UserId { get; set; }
        public string Nombre { get; set; }
        public string Categoria { get; set; }
        public decimal MontoObjetivo { get; set; }
        public string Icono { get; set; }
    }
}
