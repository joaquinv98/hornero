using System.Globalization;
using System.Text;
using System.Text.Json;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.Domain.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin.Payments;

/// <summary>
/// Utilidades, Registro de módulos y de pasarelas (sección 10.2 del diseño de interfaz): los avisos que mandó cada
/// pasarela, tal como llegaron, con lo que dijo el módulo al verificarlos y los eventos que dejaron. Sirve para saber si
/// un aviso llegó y por qué no se tomó, por ejemplo con el secreto de los avisos equivocado.
/// </summary>
[Authorize(Policy = StaffPermissions.PaymentsManage)]
public sealed class GatewayLogModel(BillingDbContext db, IOptions<BillingOptions> options) : PageModel
{
    public const string Route = "/admin/registro-modulos";

    /// <summary>Lo que se muestra del cuerpo de un aviso: alcanza para leerlo sin traer todo a la página.</summary>
    public const int MaxBody = 64 * 1024;

    private const int PageSize = 50;

    /// <summary>Estados del filtro, con la clave del texto de cada uno.</summary>
    public static IReadOnlyList<(string Value, string Text)> Filters { get; } =
    [
        ("", "GatewayLog.FilterAll"), ("recibidos", "GatewayLog.Received"), ("verificados", "GatewayLog.Verified"), ("invalidos", "GatewayLog.Invalid"),
    ];

    public sealed record Row(long Id, string At, long InstanceId, string Instance, ReceiptStatus Status, int Bytes, IReadOnlyList<GatewayEvent> Events);

    /// <param name="Cut">El cuerpo era más largo que <see cref="MaxBody"/> y se muestra el principio.</param>
    public sealed record Detail(Row Row, string Headers, string Query, string Body, bool Cut);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    /// <summary>Las instancias de cobros, para el filtro.</summary>
    public IReadOnlyList<(long Id, string Name)> Instances { get; private set; } = [];

    public Detail? Shown { get; private set; }

    public long? Instance { get; private set; }

    public string Filter { get; private set; } = string.Empty;

    public int PageNumber { get; private set; } = 1;

    public bool HasMore { get; private set; }

    public static (string Text, string Tone) StateOf(ReceiptStatus status) => status switch
    {
        ReceiptStatus.Verified => ("GatewayLog.Verified", "ok"),
        ReceiptStatus.Invalid => ("GatewayLog.Invalid", "danger"),
        _ => ("GatewayLog.Received", "info"),
    };

    public async Task<IActionResult> OnGet(long? instancia, string? estado, long? aviso, int pagina = 1)
    {
        var ct = HttpContext.RequestAborted;
        Filter = Filters.Any(f => f.Value == estado) ? estado! : string.Empty;
        PageNumber = Math.Clamp(pagina, 1, 100_000);
        var paymentInstances = await db.Database.SqlQuery<long>($"""
            SELECT i.id AS "Value" FROM platform.module_instances i
            JOIN platform.module_installations m ON m.id = i.installation_id
            WHERE m.manifest -> 'kinds' @> '["payment"]'::jsonb
            ORDER BY i.id
            """).ToListAsync(ct);
        var names = await db.ModuleInstances.AsNoTracking().Where(i => paymentInstances.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name, ct);
        foreach (var (id, portalName) in await db.GatewaySetups.AsNoTracking().Where(s => paymentInstances.Contains(s.ModuleInstanceId))
            .ToDictionaryAsync(s => s.ModuleInstanceId, s => s.DisplayName, ct))
        {
            // La instancia (con su ambiente) y el nombre que ve el cliente en el portal.
            if (names.TryGetValue(id, out var instanceName) && instanceName != portalName)
            {
                names[id] = $"{instanceName} · {portalName}";
            }
        }

        Instances = [.. paymentInstances.Select(id => (id, names.GetValueOrDefault(id, "#" + id)))];
        Instance = instancia is { } wanted && paymentInstances.Contains(wanted) ? wanted : null;

        var query = db.WebhookReceipts.AsNoTracking().Where(r => paymentInstances.Contains(r.ModuleInstanceId));
        if (Instance is { } instance)
        {
            query = query.Where(r => r.ModuleInstanceId == instance);
        }

        query = Filter switch
        {
            "recibidos" => query.Where(r => r.Status == ReceiptStatus.Received),
            "verificados" => query.Where(r => r.Status == ReceiptStatus.Verified),
            "invalidos" => query.Where(r => r.Status == ReceiptStatus.Invalid),
            _ => query,
        };

        var receipts = await query.OrderByDescending(r => r.Id).Skip((PageNumber - 1) * PageSize).Take(PageSize + 1)
            .Select(r => new Receipt(r.Id, r.ModuleInstanceId, r.Status, r.ReceivedAt, r.Body.Length)).ToListAsync(ct);
        HasMore = receipts.Count > PageSize;
        Rows = await Build([.. receipts.Take(PageSize)], names, ct);

        if (aviso is { } shown)
        {
            var receipt = await db.WebhookReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.Id == shown && paymentInstances.Contains(r.ModuleInstanceId), ct);
            if (receipt is null)
            {
                return NotFound();
            }

            var row = (await Build([new Receipt(receipt.Id, receipt.ModuleInstanceId, receipt.Status, receipt.ReceivedAt, receipt.Body.Length)], names, ct)).Single();
            var cut = receipt.Body.Length > MaxBody;
            Shown = new Detail(row, Pretty(receipt.Headers), Pretty(receipt.Query), Pretty(Encoding.UTF8.GetString(receipt.Body, 0, Math.Min(receipt.Body.Length, MaxBody))), cut);
        }

        return Page();
    }

    /// <summary>El enlace a otra página o a un aviso, con los mismos filtros.</summary>
    public string Link(int page, long? receipt = null)
    {
        var q = new List<string>();
        if (Instance is { } instance)
        {
            q.Add(string.Create(CultureInfo.InvariantCulture, $"instancia={instance}"));
        }

        if (Filter.Length > 0)
        {
            q.Add("estado=" + Uri.EscapeDataString(Filter));
        }

        if (page > 1)
        {
            q.Add(string.Create(CultureInfo.InvariantCulture, $"pagina={page}"));
        }

        if (receipt is { } id)
        {
            q.Add(string.Create(CultureInfo.InvariantCulture, $"aviso={id}"));
        }

        return Route + (q.Count > 0 ? "?" + string.Join('&', q) : string.Empty);
    }

    private async Task<IReadOnlyList<Row>> Build(IReadOnlyList<Receipt> receipts, IReadOnlyDictionary<long, string> names, CancellationToken ct)
    {
        var ids = receipts.Select(r => r.Id).ToList();
        var events = (await db.GatewayEvents.AsNoTracking().Where(e => ids.Contains(e.ReceiptId)).OrderBy(e => e.Id).ToListAsync(ct)).ToLookup(e => e.ReceiptId);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
        return [.. receipts.Select(r => new Row(
            r.Id,
            TimeZoneInfo.ConvertTime(r.ReceivedAt, zone).ToString("G", CultureInfo.CurrentCulture),
            r.InstanceId,
            names.GetValueOrDefault(r.InstanceId, "#" + r.InstanceId),
            r.Status,
            r.Bytes,
            [.. events[r.Id]]))];
    }

    /// <summary>Un JSON con sangría para leerlo, o el texto tal cual si no es JSON.</summary>
    private static string Pretty(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(json.RootElement, Indented);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record Receipt(long Id, long InstanceId, ReceiptStatus Status, DateTimeOffset ReceivedAt, int Bytes);
}
