using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LectorGastosLambda.DTOs.Responses
{
    public class LoginResponse
    {
        public bool Success { get; set; }
        public string Nombre { get; set; }
        public string UserId { get; set; }
        public string Token { get; set; }
        public string Message { get; set; }
    }
}
