using LectorGastosLambda.DTOs.Gemini;
using LectorGastosLambda.Functions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LectorGastosLambda.DTOs.Responses
{
    public class GeminiResponse { public Candidate[] candidates { get; set; } }
}
