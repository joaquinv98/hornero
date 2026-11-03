using System.Globalization;

namespace BillingOss.Web.Content;

/// <summary>
/// Las direcciones del sistema anterior que el proveedor ya difundió en su sitio, en sus correos y en los favoritos de los clientes,
/// y a dónde van ahora (sección 12 de diseno/contenidos.md). Cada una responde un 301 a la dirección nueva y no consulta la base:
/// el artículo o la categoría de origen conservan su número, y las páginas nuevas aceptan ese número y redirigen a la dirección
/// corta. Lo que no es de contenidos da 404 hasta que se sume en <see cref="Target"/>.
/// </summary>
public static class LegacyUrls
{
    /// <summary>La pantalla de abrir un ticket del portal. El departamento y el asunto del formulario viejo no se arrastran.</summary>
    public const string NewTicket = "/soporte/nuevo";

    public static void MapLegacyUrls(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        string[] read = [HttpMethods.Get, HttpMethods.Head];

        // La portada vieja llevaba la ruta en el parámetro rp: index.php?rp=/knowledgebase/12/
        app.MapMethods("/index.php", read, (string? rp) => Target(rp) is { } to ? Moved(to) : Results.NotFound());

        // La base de conocimiento anterior: knowledgebase.php?action=displayarticle&catid=6&id=25
        app.MapMethods("/knowledgebase.php", read, (long? id, long? catid) =>
            Moved(id is > 0 ? $"/conocimiento/{id}" : catid is > 0 ? $"/conocimiento/c/{catid}" : "/conocimiento"));

        // El estado de la red y el formulario de tickets, que antes eran páginas sueltas.
        app.MapMethods("/serverstatus.php", read, () => Moved("/estado-red"));
        app.MapMethods("/networkissues.php", read, () => Moved("/estado-red"));
        app.MapMethods("/submitticket.php", read, () => Moved(NewTicket));
    }

    /// <summary>
    /// La dirección nueva de una ruta vieja (el valor de <c>rp</c>), o nulo si no es de contenidos. Sin ruta, la portada. Un
    /// número con nombre atrás es una categoría y un número solo es un artículo, que es como las armaba el sistema anterior.
    /// </summary>
    public static string? Target(string? rp)
    {
        var parts = (rp ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return "/";
        }

        var rest = parts[1..];
        return parts[0].ToLowerInvariant() switch
        {
            "knowledgebase" => Knowledge(rest),
            "announcements" => rest.Length > 0 && Number(rest[0]) is { } id ? $"/anuncios/{id}" : "/anuncios",
            "download" or "downloads" => "/descargas",
            _ => null,
        };
    }

    private static string Knowledge(string[] rest)
    {
        if (rest.Length == 0)
        {
            return "/conocimiento";
        }

        if (rest[0].Equals("tag", StringComparison.OrdinalIgnoreCase))
        {
            return rest.Length > 1 ? "/conocimiento?q=" + Uri.EscapeDataString(rest[1]) : "/conocimiento";
        }

        // La búsqueda viajaba por POST: el término no llega, así que va a la página que la ofrece.
        if (Number(rest[0]) is not { } id)
        {
            return "/conocimiento";
        }

        return rest.Length == 1 ? $"/conocimiento/{id}" : $"/conocimiento/c/{id}";
    }

    private static long? Number(string text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;

    private static IResult Moved(string to) => Results.Redirect(to, permanent: true);
}
