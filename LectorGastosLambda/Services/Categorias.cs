using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace LectorGastosLambda.Services
{
    // Catálogo único de categorías de gasto. La IA solo puede elegir de esta lista y cualquier texto
    // (categorías antiguas, errores de tipeo, sinónimos) se traduce a una de ellas.
    // Si cambias la lista, actualiza también CATEGORIAS en el frontend (src/lib/format.js).
    public static class Categorias
    {
        public const string Otros = "Otros";

        // Nombre y descripción: la descripción le dice a la IA qué va en cada una
        public static readonly (string Nombre, string Descripcion)[] Catalogo =
        {
            ("Comida", "restaurantes, delivery, cafeterías, menú, comida al paso"),
            ("Supermercado", "supermercados, mercado, bodega, abarrotes, compras de despensa"),
            ("Transporte", "taxi, Uber, Cabify, combustible, peajes, estacionamiento, pasajes"),
            ("Servicios", "luz, agua, gas, internet, telefonía, cable"),
            ("Hogar", "alquiler, mantenimiento, muebles, limpieza, ferretería, reparaciones"),
            ("Salud", "farmacia, médico, clínica, dentista, laboratorio, óptica, seguro de salud"),
            ("Educación", "colegio, universidad, cursos, libros, útiles"),
            ("Entretenimiento", "cine, salidas, bares, eventos, conciertos, juegos, hobbies"),
            ("Suscripciones", "Netflix, Spotify, Disney+, Prime, iCloud, apps y membresías mensuales"),
            ("Ropa", "ropa, calzado y accesorios de vestir"),
            ("Cuidado personal", "peluquería, barbería, cosméticos, gimnasio, spa, higiene"),
            ("Tecnología", "equipos electrónicos, computadoras, celulares (el equipo), accesorios, software"),
            ("Mascotas", "veterinaria, alimento y cuidado de mascotas"),
            ("Viajes", "vuelos, hoteles, hospedaje, paquetes turísticos"),
            ("Regalos", "regalos, donaciones, celebraciones"),
            (Otros, "lo que no encaje claramente en ninguna otra"),
        };

        // Categorías que el usuario puede elegir (Ahorro la asignan solo las metas)
        public static readonly string[] Nombres = Catalogo.Select(c => c.Nombre).ToArray();

        private static readonly Dictionary<string, string> PorClave = Nombres
            .Append(ReglasGastos.CategoriaAhorro)
            .ToDictionary(Clave, n => n);

        // Palabras sueltas que delatan una categoría (sin tildes y en minúsculas)
        private static readonly Dictionary<string, string> Sinonimos = new Dictionary<string, string>
        {
            ["Comida"] = "restaurante restaurantes alimentacion alimentos comidas delivery cafe cafeteria almuerzo cena desayuno menu rappi pedidosya polleria chifa cevicheria pizzeria fastfood",
            ["Supermercado"] = "supermercados mercado bodega abarrotes minimarket despensa tottus wong metro plazavea vivanda makro tambo oxxo mass",
            ["Transporte"] = "taxi uber cabify didi indriver gasolina combustible grifo peaje peajes estacionamiento movilidad pasaje pasajes bus metropolitano vehiculo auto",
            ["Servicios"] = "luz agua gas internet telefono telefonia cable enel sedapal calidda movistar claro entel bitel",
            ["Hogar"] = "alquiler renta mantenimiento muebles decoracion limpieza ferreteria sodimac promart casa vivienda",
            ["Salud"] = "farmacia farmacias botica boticas inkafarma mifarma medico medicos medicina medicinas medicamentos clinica hospital dentista odontologia laboratorio consulta eps optica",
            ["Educación"] = "colegio universidad curso cursos libros libreria utiles academia matricula pension estudios",
            ["Entretenimiento"] = "cine ocio diversion salidas eventos concierto conciertos juegos videojuegos bar bares discoteca hobbies entradas",
            ["Suscripciones"] = "suscripcion netflix spotify disney hbo prime youtube icloud membresia membresias streaming",
            ["Ropa"] = "vestimenta calzado zapatos zapatillas moda",
            ["Cuidado personal"] = "peluqueria barberia cosmeticos belleza spa gimnasio gym higiene cuidado",
            ["Tecnología"] = "electronica computadora laptop gadgets software",
            ["Mascotas"] = "mascota veterinaria veterinario perro gato",
            ["Viajes"] = "viaje vuelo vuelos hotel hoteles hospedaje aerolinea turismo airbnb booking",
            ["Regalos"] = "regalo donacion donaciones celebracion cumpleanos",
            [Otros] = "varios general miscelaneos",
        }
        .SelectMany(kv => kv.Value.Split(' ').Select(palabra => (palabra, kv.Key)))
        .ToDictionary(x => x.palabra, x => x.Key);

        // ¿Es exactamente una categoría del catálogo? (sin importar mayúsculas ni tildes)
        public static bool Existe(string? texto) => !string.IsNullOrWhiteSpace(texto) && Nombres.Any(n => Clave(n) == Clave(texto));

        // Traduce cualquier texto a una categoría válida; lo que no se reconoce queda en "Otros"
        public static string Normalizar(string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return Otros;
            var clave = Clave(texto);
            if (PorClave.TryGetValue(clave, out var exacta)) return exacta;
            if (Sinonimos.TryGetValue(clave, out var sinonimo)) return sinonimo;

            foreach (var palabra in clave.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (PorClave.TryGetValue(palabra, out var porPalabra)) return porPalabra;
                if (Sinonimos.TryGetValue(palabra, out var porSinonimo)) return porSinonimo;
            }
            return Otros;
        }

        // Lista para el prompt de la IA: "- Salud: farmacia, médico, ..."
        public static string ParaIA() =>
            string.Join("\n", Catalogo.Select(c => $"- {c.Nombre}: {c.Descripcion}"));

        private static string Clave(string texto)
        {
            var sinTildes = new string(texto.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
            return string.Join(' ', sinTildes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
