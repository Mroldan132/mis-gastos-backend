using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LectorGastosLambda.DTOs.Requests
{
    public class CrearGastoRequest
    {
        // El UserId lo sacaremos después del Token, pero por ahora lo enviamos para probar
        public string UserId { get; set; }
        public decimal Monto { get; set; }
        public string Fecha { get; set; } // Formato "YYYY-MM-DD"
        public string Categoria { get; set; }
        public string Comercio { get; set; }
        public bool EsPagoDeCuota { get; set; }
        public string CuotaIdRelacionada { get; set; }
        public string Moneda { get; set; }
        public decimal? TipoCambio { get; set; }
        
        
    }
}
