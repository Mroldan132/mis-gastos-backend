using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LectorGastosLambda.DTOs.Requests
{
    public class CrearIngresoRequest
    {
        public string UserId { get; set; }
        public decimal Monto { get; set; }
        public string Fecha { get; set; } // Opcional, si viene vacío usamos la fecha actual
        public string Descripcion { get; set; }
    }
}