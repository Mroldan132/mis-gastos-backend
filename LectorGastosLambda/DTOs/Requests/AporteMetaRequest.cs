using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LectorGastosLambda.DTOs.Requests
{
    public class AporteMetaRequest
    {
        public string UserId { get; set; }
        public string MetaId { get; set; }
        public decimal MontoAporte { get; set; }
    }
}
