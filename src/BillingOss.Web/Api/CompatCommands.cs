using System.Globalization;
using System.Text.Json.Nodes;
using BillingOss.App.Notifications;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Api;

/// <summary>
/// Comandos de la API compatible agregados después de v0 (sección 3.1 de diseno/api-compatible.md). Cada uno traduce
/// a un caso de uso propio. Las listas nuevas paginan con limitstart y limitnum, de 1 a 250.
/// </summary>
internal static partial class CompatApi
{
    private const int MaxPage = 250;

    private static readonly Dictionary<string, Func<IServiceProvider, IFormCollection, CancellationToken, Task<IResult>>> AddedCommands = new(StringComparer.Ordinal)
    {
        ["GetClients"] = GetClients,
        ["GetProducts"] = GetProducts,
        ["GetInvoices"] = GetInvoices,
        ["DeleteClient"] = DeleteClient,
        ["UpdateClientProduct"] = UpdateClientProduct,
        ["ModuleCreate"] = (sp, form, ct) => OnService(form, id => sp.GetRequiredService<ServiceAdmin>().Provision(id, ct)),
        ["ModuleTerminate"] = (sp, form, ct) => OnService(form, id => sp.GetRequiredService<ServiceAdmin>().Terminate(id, ct: ct)),
        // La suspensión del staff queda con su motivo (suspendreason, o el de una suspensión manual): un pago no la levanta.
        ["ModuleSuspend"] = (sp, form, ct) => OnService(form, id => sp.GetRequiredService<ServiceAdmin>().Suspend(id, Optional(form, "suspendreason"), ct: ct)),
        ["ModuleUnsuspend"] = (sp, form, ct) => OnService(form, id => sp.GetRequiredService<ServiceAdmin>().Unsuspend(id, ct: ct)),
        ["CancelOrder"] = CancelOrder,
        ["GetEmailTemplates"] = GetEmailTemplates,
    };

    /// <summary>Todos los comandos, en orden: cada uno es un permiso compat: que se elige al emitir una llave.</summary>
    public static IEnumerable<string> CommandNames => Commands.Concat(AddedCommands.Keys).Order(StringComparer.Ordinal);

    /// <summary>Los cinco textos de estado de factura de la sección 3 de diseno/api-compatible.md.</summary>
    private static readonly string[] InvoiceStatuses = ["Draft", "Unpaid", "Paid", "Cancelled", "Refunded"];

    /// <summary>Una factura anulada sale Refunded si sus notas devolvieron algo ya pagado, y Cancelled si no.</summary>
    private static string InvoiceStatusText(Document d) => d.Status switch
    {
        InvoiceStatus.Draft => "Draft",
        InvoiceStatus.Paid => "Paid",
        InvoiceStatus.Cancelled => d.Refunded > 0 || d.SourceStatus == "Refunded" ? "Refunded" : "Cancelled",
        _ => "Unpaid",
    };

    private static async Task<IResult> GetClients(IServiceProvider sp, IFormCollection form, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BillingDbContext>();
        var query = db.Accounts.AsNoTracking();
        if (Optional(form, "status") is { } status)
        {
            var parsed = Enum.GetNames<AccountStatus>().Contains(status)
                ? Enum.Parse<AccountStatus>(status)
                : throw new ValidationException($"Estado de cuenta desconocido: {status}. Los válidos son Active, Inactive y Closed.");
            query = query.Where(a => a.Status == parsed);
        }

        if (Optional(form, "search")?.Trim() is { Length: > 0 } search)
        {
            // Con el escape explícito: sin él, Npgsql manda ESCAPE '' y la barra del patrón se busca como texto.
            var pattern = Like(search);
            query = query.Where(a => EF.Functions.ILike(a.Name, pattern, "\\") || EF.Functions.ILike(a.Email, pattern, "\\")
                || (a.CompanyName != null && EF.Functions.ILike(a.CompanyName, pattern, "\\")));
        }

        var (start, limit) = Page(form);
        var total = await query.CountAsync(ct);
        var page = await query.OrderBy(a => a.Id).Skip(start).Take(limit).ToListAsync(ct);
        return List(total, start, page.Count, "clients", "client", page.Select(a =>
        {
            var (first, last) = SplitName(a.Name);
            return new JsonObject
            {
                ["id"] = a.Id,
                ["firstname"] = first,
                ["lastname"] = last,
                ["companyname"] = a.CompanyName ?? string.Empty,
                ["email"] = a.Email,
                ["status"] = a.Status.ToString(),
            };
        }));
    }

    private static async Task<IResult> GetInvoices(IServiceProvider sp, IFormCollection form, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BillingDbContext>();
        var query = db.Documents.AsNoTracking().Where(d => d.Kind == DocumentKind.Invoice);
        if ((Optional(form, "userid") ?? Optional(form, "clientid")) is { } account)
        {
            var accountId = long.Parse(account, CultureInfo.InvariantCulture);
            query = query.Where(d => d.AccountId == accountId);
        }

        if (Optional(form, "status") is { } status)
        {
            query = WhereStatus(query, status);
        }

        var (start, limit) = Page(form);
        var total = await query.CountAsync(ct);
        var page = await query.OrderBy(d => d.Id).Skip(start).Take(limit).ToListAsync(ct);
        var paidIds = page.Where(d => d.Status == InvoiceStatus.Paid).Select(d => d.Id).ToList();
        var paidOn = await db.Allocations.AsNoTracking().Where(a => paidIds.Contains(a.DocumentId))
            .GroupBy(a => a.DocumentId).Select(g => new { g.Key, At = g.Max(a => a.CreatedAt) })
            .ToDictionaryAsync(x => x.Key, x => x.At, ct);
        return List(total, start, page.Count, "invoices", "invoice", page.Select(d => new JsonObject
        {
            ["id"] = d.Id,
            ["invoicenum"] = d.Number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["userid"] = d.AccountId,
            ["date"] = d.IssueDate is { } issue ? Date(issue) : string.Empty,
            ["duedate"] = Date(d.DueDate),
            ["datepaid"] = paidOn.TryGetValue(d.Id, out var at) ? at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty,
            ["subtotal"] = Money(d.NetTaxed + d.Exempt + d.NotTaxed),
            ["tax"] = Money(d.Tax),
            ["total"] = Money(d.Total),
            ["status"] = InvoiceStatusText(d),
        }));
    }

    /// <summary>
    /// El estado de <see cref="InvoiceStatusText"/> traducido a una condición sobre los importes, para filtrar en la
    /// base. La prueba de contrato compara cada filtro con el estado que devuelve GetInvoice.
    /// </summary>
    private static IQueryable<Document> WhereStatus(IQueryable<Document> query, string status) => status switch
    {
        "Draft" => query.Where(d => d.IssueDate == null),
        "Unpaid" => query.Where(d => d.IssueDate != null && d.Total != 0m && d.Credited < d.Total
            && (d.Allocated <= 0m || d.Total - d.Credited - d.Allocated > 0m)),
        "Paid" => query.Where(d => d.IssueDate != null
            && ((d.Total == 0m && !(d.Imported && (d.SourceStatus == "Cancelled" || d.SourceStatus == "Refunded")))
                || (d.Total != 0m && d.Credited < d.Total && d.Allocated > 0m && d.Total - d.Credited - d.Allocated <= 0m))),
        "Cancelled" => query.Where(d => d.IssueDate != null
            && ((d.Total == 0m && d.Imported && d.SourceStatus == "Cancelled")
                || (d.Total != 0m && d.Credited >= d.Total && d.Refunded <= 0m && d.SourceStatus != "Refunded"))),
        "Refunded" => query.Where(d => d.IssueDate != null
            && ((d.Total == 0m && d.Imported && d.SourceStatus == "Refunded")
                || (d.Total != 0m && d.Credited >= d.Total && (d.Refunded > 0m || d.SourceStatus == "Refunded")))),
        _ => throw new ValidationException($"Estado de factura desconocido: {status}. Los válidos son {string.Join(", ", InvoiceStatuses)}."),
    };

    private static async Task<IResult> DeleteClient(IServiceProvider sp, IFormCollection form, CancellationToken ct)
    {
        var id = long.Parse(form["clientid"].ToString(), CultureInfo.InvariantCulture);
        await sp.GetRequiredService<AccountService>().Delete(id, ct);
        return Success(new JsonObject { ["clientid"] = id });
    }

    /// <summary>
    /// Solo cambia el nombre visible (domain). Cualquier otro campo da error: ignorarlo diría que se cambió algo que
    /// quedó igual.
    /// </summary>
    private static async Task<IResult> UpdateClientProduct(IServiceProvider sp, IFormCollection form, CancellationToken ct)
    {
        string[] envelope = ["action", "identifier", "secret", "responsetype", "serviceid", "accountid", "domain"];
        if (form.Keys.Where(k => !envelope.Contains(k)).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } unknown)
        {
            throw new ValidationException($"UpdateClientProduct solo cambia domain. No se soporta: {string.Join(", ", unknown)}.");
        }

        if (!form.ContainsKey("domain"))
        {
            throw new ValidationException("Falta domain, que es lo que cambia UpdateClientProduct.");
        }

        var id = ServiceId(form);
        await sp.GetRequiredService<ServiceAdmin>().Rename(id, form["domain"].ToString(), ct);
        return Success(new JsonObject { ["serviceid"] = id });
    }

    private static async Task<IResult> OnService(IFormCollection form, Func<long, Task> work)
    {
        var id = ServiceId(form);
        await work(id);
        return Success(new JsonObject { ["serviceid"] = id });
    }

    private static async Task<IResult> CancelOrder(IServiceProvider sp, IFormCollection form, CancellationToken ct)
    {
        var id = long.Parse(form["orderid"].ToString(), CultureInfo.InvariantCulture);
        await sp.GetRequiredService<OrderService>().Cancel(id, ct);
        return Success(new JsonObject { ["orderid"] = id });
    }

    /// <summary>Las plantillas de los avisos en cada idioma, con la edición del proveedor si la hay. Sin el diseño común.</summary>
    private static async Task<IResult> GetEmailTemplates(IServiceProvider sp, IFormCollection form, CancellationToken ct)
    {
        var custom = await sp.GetRequiredService<BillingDbContext>().Set<Template>().AsNoTracking().ToListAsync(ct);
        var templates = TemplateRenderer.Keys.Where(k => k != TemplateRenderer.Layout)
            .SelectMany(key => TemplateRenderer.Cultures.Select(culture =>
            {
                var edited = custom.Find(t => t.Key == key && t.Culture == culture);
                return new JsonObject
                {
                    ["name"] = key,
                    ["language"] = culture,
                    ["subject"] = edited?.Subject ?? TemplateRenderer.Default(key, culture).Subject,
                    ["custom"] = edited is not null,
                };
            }))
            .ToList();
        return List(templates.Count, 0, templates.Count, "emailtemplates", "emailtemplate", templates, withPaging: false);
    }

    private static JsonHttpResult<JsonObject> List(int total, int start, int returned, string plural, string singular, IEnumerable<JsonObject> items, bool withPaging = true)
    {
        var body = new JsonObject { ["totalresults"] = total };
        if (withPaging)
        {
            body["startnumber"] = start;
            body["numreturned"] = returned;
        }

        body[plural] = new JsonObject { [singular] = new JsonArray([.. items]) };
        return Success(body);
    }

    private static (int Start, int Limit) Page(IFormCollection form) =>
        (Int(form, "limitstart", 0), Math.Clamp(Int(form, "limitnum", 25), 1, MaxPage));

    /// <summary>Patrón de ILIKE que busca el texto tal cual, con sus comodines escapados.</summary>
    private static string Like(string text) =>
        "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
}
