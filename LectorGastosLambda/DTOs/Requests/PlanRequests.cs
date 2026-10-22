using System.Collections.Generic;

namespace LectorGastosLambda.DTOs.Requests
{
    public class PresupuestoItem
    {
        public string Categoria { get; set; }
        public decimal Monto { get; set; }
    }

    public class GuardarPresupuestosRequest
    {
        // Reemplaza todos los presupuestos del usuario; una categoría ausente o con monto 0 se elimina
        public List<PresupuestoItem> Presupuestos { get; set; } = new();
    }

    public class PagoFijoRequest
    {
        public string? FijoId { get; set; } // Solo para editar
        public string? Nombre { get; set; }
        public decimal? Monto { get; set; }
        public string? Moneda { get; set; }
        public string? Categoria { get; set; }
        public int? DiaVencimiento { get; set; }
        public bool? Activo { get; set; }
    }

    public class PagarFijoRequest
    {
        public string FijoId { get; set; }
        public string? Mes { get; set; } // "YYYY-MM"; por defecto el mes actual
        public decimal? Monto { get; set; } // Si este mes vino distinto (ej. la luz)
    }

    public class ConfigHogarRequest
    {
        public List<string>? CategoriasCompartidas { get; set; }
        public string? ModoReparto { get; set; } // "mitad", "ingresos" o "fijo"
        public Dictionary<string, decimal>? Porcentajes { get; set; }
    }
}
