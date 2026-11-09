using LectorGastosLambda.Models.DataBase;
using MimeKit;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LectorGastosLambda.Services
{
    // Cuenta de Gmail dedicada a la app: los usuarios le reenvían las notificaciones de su banco
    // y desde ella se envía el resumen semanal. Así no se guarda la contraseña del correo personal de nadie.
    //   BUZON_EMAIL         -> ej. misgastos.app@gmail.com
    //   BUZON_APP_PASSWORD  -> contraseña de aplicación de esa cuenta
    public static class Buzon
    {
        public static string? Email => Environment.GetEnvironmentVariable("BUZON_EMAIL");
        public static string? Password => Environment.GetEnvironmentVariable("BUZON_APP_PASSWORD");
        public static bool Configurado => !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password);

        // Alias de un usuario en el buzón: misgastos.app+<alias>@gmail.com
        // Es la parte local de su correo, en minúsculas y solo letras y números (el frontend calcula lo mismo).
        public static string Alias(string? email) =>
            new string((email ?? "").Split('@')[0].ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        // Correo de Gmail con el código para confirmar un reenvío: se deja sin leer para que el dueño lo vea
        public static bool EsConfirmacionDeReenvio(MimeMessage m) =>
            m.From.Mailboxes.Any(d => d.Address.Equals("forwarding-noreply@google.com", StringComparison.OrdinalIgnoreCase));

        // A quién pertenece un correo que llegó al buzón:
        //   1. alias en la dirección de destino (misgastos.app+juan@gmail.com)
        //   2. cuenta que lo reenvió con un filtro de Gmail (cabecera X-Forwarded-For)
        //   3. remitente, si lo reenviaron a mano ("Fwd:")
        public static UsuarioConfig? IdentificarUsuario(MimeMessage m, IReadOnlyList<UsuarioConfig> usuarios)
        {
            var (localBuzon, dominioBuzon) = Partes(Email);

            var destinos = new[] { "Delivered-To", "X-Forwarded-To", "To", "Cc" }
                .SelectMany(cabecera => Direcciones(m, cabecera));
            foreach (var destino in destinos)
            {
                var (local, dominio) = Partes(destino);
                var mas = local.IndexOf('+');
                if (mas < 0 || dominio != dominioBuzon || SinPuntos(local[..mas]) != SinPuntos(localBuzon)) continue;

                var alias = Alias(local[(mas + 1)..]);
                var usuario = usuarios.FirstOrDefault(u => Alias(u.Email) == alias);
                if (usuario != null) return usuario;
            }

            var reenviadoPor = Direcciones(m, "X-Forwarded-For").Concat(m.From.Mailboxes.Select(d => d.Address));
            foreach (var direccion in reenviadoPor)
            {
                var usuario = usuarios.FirstOrDefault(u => string.Equals(u.Email, direccion, StringComparison.OrdinalIgnoreCase));
                if (usuario != null) return usuario;
            }
            return null;
        }

        private static IEnumerable<string> Direcciones(MimeMessage m, string cabecera) =>
            m.Headers.Where(h => h.Field.Equals(cabecera, StringComparison.OrdinalIgnoreCase))
                .SelectMany(h => InternetAddressList.TryParse(h.Value, out var lista)
                    ? lista.Mailboxes.Select(d => d.Address)
                    : h.Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Where(x => x.Contains('@')));

        private static (string Local, string Dominio) Partes(string? direccion)
        {
            var partes = (direccion ?? "").Trim().ToLowerInvariant().Split('@');
            return partes.Length == 2 ? (partes[0], partes[1]) : ("", "");
        }

        // Gmail ignora los puntos de la parte local (misgastos.app = misgastosapp)
        private static string SinPuntos(string local) => local.Replace(".", "");
    }
}
