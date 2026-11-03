using System.Globalization;
using BillingOss.App;
using BillingOss.App.Persistence;
using BillingOss.App.Support;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web;

/// <summary>Lo que comparten las pantallas de soporte del portal y del admin.</summary>
public static class SupportUi
{
    /// <summary>
    /// Tope de un pedido con adjuntos: cinco de 10 MB y el texto. Kestrel corta en 1 MiB todo lo demás. Si se suben los
    /// topes de Support, este va con ellos (docs/soporte.md).
    /// </summary>
    public const long MaxRequestBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Punto, palabra y forma del estado (D-19, sección 4.4), según quién mira: lo que espera al staff le pide acción al
    /// staff, y lo que el staff ya respondió le pide acción al cliente.
    /// </summary>
    public static (string Tone, string? Shape) Status(TicketStatus status, bool staff) => (status, staff) switch
    {
        (TicketStatus.Open, true) => ("warn", "ring"),
        (TicketStatus.Open, false) or (TicketStatus.InProgress, _) => ("info", "hollow"),
        (TicketStatus.AwaitingCustomer, false) => ("warn", "ring"),
        (TicketStatus.AwaitingCustomer, true) or (TicketStatus.OnHold, _) => ("neutral", "hollow"),
        _ => ("neutral", null),
    };

    /// <summary>Del 9 al 10 bien, del 7 al 8 neutro, lo demás pide atención.</summary>
    public static string RatingTone(int rating) => rating >= 9 ? "ok" : rating >= 7 ? "neutral" : "warn";

    public static string PriorityTone(TicketPriority priority) => priority switch
    {
        TicketPriority.Urgent => "danger",
        TicketPriority.High => "warn",
        _ => "neutral",
    };

    /// <summary>
    /// Los archivos del formulario con los topes de Support. Devuelve la clave del texto del primer problema y el
    /// nombre del archivo, o los adjuntos listos.
    /// </summary>
    public static async Task<(List<NewAttachment> Files, string? Problem, string? FileName)> Read(IEnumerable<IFormFile>? files, SupportOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = new List<NewAttachment>();

        // Un selector de archivos vacío igual manda una parte sin nombre.
        foreach (var file in (files ?? []).Where(f => f.FileName.Length > 0))
        {
            var problem = TicketService.Check(options, file.FileName, file.Length, result.Count);
            if (problem != AttachmentProblem.None)
            {
                return ([], "Support.File" + problem, TicketService.SafeName(file.FileName));
            }

            using var content = new MemoryStream((int)file.Length);
            await file.CopyToAsync(content, ct).ConfigureAwait(false);
            result.Add(new NewAttachment(file.FileName, content.ToArray()));
        }

        return (result, null, null);
    }

    /// <summary>"pdf, png, jpg" para la ayuda del formulario.</summary>
    public static string Extensions(SupportOptions options) => string.Join(", ", (options ?? throw new ArgumentNullException(nameof(options))).Extensions.Select(e => e.TrimStart('.')));

    /// <summary>Lo que acepta el selector de archivos del navegador, para no ofrecer lo que después se rechaza.</summary>
    public static string Accept(SupportOptions options) => string.Join(",", (options ?? throw new ArgumentNullException(nameof(options))).Extensions);

    /// <summary>
    /// La misma lista con algunos parámetros cambiados. Cambiar un filtro o el orden vuelve a la primera página, y lo que
    /// vale para un solo pedido no viaja: el formato, el paso de una acción y su aviso de vuelta.
    /// </summary>
    public static string Link(HttpRequest request, params (string Key, string? Value)[] changes)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(changes);
        var values = request.Query.ToDictionary(q => q.Key, q => (string?)q.Value.ToString(), StringComparer.Ordinal);
        if (!changes.Any(c => c.Key == "pagina"))
        {
            values.Remove("pagina");
        }

        foreach (var once in (string[])["formato", "handler", "hechos"])
        {
            values.Remove(once);
        }

        foreach (var (key, value) in changes)
        {
            values[key] = value;
        }

        var query = string.Join("&", values.Where(v => v.Value is { Length: > 0 }).OrderBy(v => v.Key, StringComparer.Ordinal)
            .Select(v => Uri.EscapeDataString(v.Key) + "=" + Uri.EscapeDataString(v.Value!)));
        return request.Path + (query.Length > 0 ? "?" + query : string.Empty);
    }

    public static string Megabytes(long bytes) => (bytes / (1024 * 1024)).ToString(CultureInfo.CurrentCulture);

    /// <summary>Un instante en la hora del proveedor ("28/10/2026 10:41"). El valor para máquinas va en time datetime.</summary>
    public static string When(DateTimeOffset at, BillingOptions options) =>
        TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById((options ?? throw new ArgumentNullException(nameof(options))).TimeZone))
            .ToString("g", CultureInfo.CurrentCulture);

    public static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>"820 KB" o "1,5 MB".</summary>
    public static string Size(long bytes) => bytes < 1024 * 1024
        ? string.Create(CultureInfo.CurrentCulture, $"{Math.Max(1, bytes / 1024)} KB")
        : string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024d * 1024):0.#} MB");

    /// <summary>Un mensaje del hilo con sus adjuntos.</summary>
    public sealed record ThreadEntry(TicketMessage Message, IReadOnlyList<TicketAttachment> Files);

    /// <summary>
    /// El hilo para el parcial _TicketThread. El staff ve las notas internas, de dónde llegó cada mensaje y la casilla
    /// de quien escribió. FileUrl termina en el ID del adjunto.
    /// </summary>
    public sealed record ThreadView(IReadOnlyList<ThreadEntry> Entries, bool Staff, string FileUrl, BillingOptions Billing);

    /// <summary>Los mensajes de un ticket con sus adjuntos, en orden. Sin <paramref name="internals"/>, sin las notas del staff.</summary>
    public static async Task<IReadOnlyList<ThreadEntry>> Thread(BillingDbContext db, long ticketId, bool internals, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var messages = await db.Set<TicketMessage>().AsNoTracking().Where(m => m.TicketId == ticketId && (internals || !m.Internal))
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToListAsync(ct).ConfigureAwait(false);
        var ids = messages.Select(m => m.Id).ToList();
        var files = await db.Set<TicketAttachment>().AsNoTracking().Where(a => ids.Contains(a.MessageId)).OrderBy(a => a.Id).ToListAsync(ct).ConfigureAwait(false);
        return messages.Select(m => new ThreadEntry(m, files.Where(f => f.MessageId == m.Id).ToList())).ToList();
    }

    /// <summary>El contenido de un adjunto del hilo que se ve, o null. Los importados sin archivo no se bajan.</summary>
    public static async Task<(TicketAttachment File, byte[] Content)?> File(BillingDbContext db, IReadOnlyList<ThreadEntry> thread, long fileId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(thread);
        if (thread.SelectMany(e => e.Files).FirstOrDefault(f => f.Id == fileId && !f.Missing) is not { } file)
        {
            return null;
        }

        var content = await db.Set<AttachmentContent>().AsNoTracking().Where(c => c.AttachmentId == file.Id).Select(c => c.Content).SingleAsync(ct).ConfigureAwait(false);
        return (file, content);
    }
}
