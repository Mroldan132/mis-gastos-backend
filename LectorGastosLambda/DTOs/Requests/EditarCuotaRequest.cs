using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LectorGastosLambda.DTOs.Requests
{
    public class EditarCuotaRequest
    {
        public string UserId { get; set; }
        public string FechaOperacion { get; set; }
        public decimal? Monto { get; set; }
        public string Estado { get; set; }
    }
}
