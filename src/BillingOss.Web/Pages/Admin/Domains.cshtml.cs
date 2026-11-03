using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Domains;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web.Pages.Admin;

/// <summary>
/// Los dominios por trabajo pendiente (D-19): arriba los contadores de lo que pide acción, que llevan a la lista
/// filtrada, y abajo la búsqueda por nombre o por cuenta.
/// </summary>
[Authorize(Policy = StaffPermissions.DomainsRead)]
public sealed class DomainsModel(BillingDbContext db, BillingCore core, DomainService domains, IStringLocalizer<Texts> texts) : PageModel
{
    /// <summary>Lo que se muestra en la pantalla. La exportación CSV lleva todo.</summary>
    public const int PageRows = 200;

    public sealed record Row(RegisteredDomain Domain, string Account, string Registrar);

    /// <summary>Las vistas de trabajo pendiente, con su cantidad.</summary>
    public sealed record View(string Id, string Label, int Count);

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true, Name = "vista")]
    public string? Filter { get; set; }

    public IReadOnlyList<View> Views { get; private set; } = [];

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public bool CanManage => User.HasClaim(Claims.Permission, StaffPermissions.DomainsManage);

    /// <summary>Resultado de una acción masiva: a cuántos se aplicó y a cuántos no.</summary>
    public (int Done, int Skipped)? Bulk { get; private set; }

    public string? Error { get; private set; }

    public Task<IActionResult> OnGet() => Load(PageRows);

    /// <summary>La misma lista, entera, en CSV para abrir en una planilla, con el encabezado y el sí y el no en el idioma del staff.</summary>
    public async Task<IActionResult> OnGetCsv()
    {
        await Load(int.MaxValue);
        var csv = new System.Text.StringBuilder(texts["AdminDomains.CsvHeader"].Value).Append('\n');
        string yes = texts["Common.Yes"], no = texts["Common.No"];
        foreach (var r in Rows)
        {
            var d = r.Domain;
            csv.AppendLine(string.Join(';', new object?[] { d.Id, d.Name, r.Account, d.AccountId, d.Status, d.RegistryStatus, Ui.Iso(d.ExpiresOn), Ui.Iso(d.PaidThrough), r.Registrar, d.AutoRenew ? yes : no }
                .Select(v => Cell(Convert.ToString(v, CultureInfo.InvariantCulture)))));
        }

        return File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv; charset=utf-8", "dominios.csv");
    }

    /// <summary>
    /// Una acción sobre los dominios marcados, con la casilla de confirmación: sincronizar, o prender o apagar la
    /// renovación automática. Cada uno va por su caso de uso, y el que no se puede (por ejemplo uno vencido) se cuenta aparte.
    /// </summary>
    public async Task<IActionResult> OnPostBulk(List<long> ids, string? action, bool confirm)
    {
        if (!CanManage)
        {
            return Forbid(Schemes.Staff);
        }

        if (!confirm || ids.Count == 0 || action is not ("sync" or "renew-on" or "renew-off"))
        {
            Error = "AdminDomains.BulkConfirm";
            return await Load(PageRows);
        }

        var (done, skipped) = (0, 0);
        foreach (var id in ids.Distinct().Take(PageRows))
        {
            try
            {
                await (action == "sync" ? domains.SyncNow(id, HttpContext.RequestAborted) : domains.SetAutoRenew(id, action == "renew-on", HttpContext.RequestAborted));
                done++;
            }
            catch (Exception ex) when (ex is ValidationException or KeyNotFoundException)
            {
                skipped++;
            }
        }

        Bulk = (done, skipped);
        return await Load(PageRows);
    }

    /// <summary>Una celda de CSV con punto y coma: entre comillas si hace falta, y sin fórmulas que una planilla ejecute.</summary>
    private static string Cell(string? text)
    {
        var value = text ?? string.Empty;
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }

    private async Task<IActionResult> Load(int take)
    {
        var today = core.Today();
        var soon = today.AddDays(30);

        // Una operación fallida que no se volvió a pedir (ninguna posterior de su tipo, como DomainJobs.LastFailed) queda 30
        // días. Una renovación fallida queda mientras lo pago pase el vencimiento en el registro, porque el cliente pagó y el
        // registro no renovó: sale al reintentarla, con la nota de crédito que la anula o cuando la sincronización trae el
        // vencimiento nuevo. Las opciones no van al registrar.
        var failedSince = core.Now().AddDays(-30);
        var failed = db.DomainOperations.Where(o => o.Status == DomainOperationStatus.Failed && o.Operation != DomainJobs.AddOption
            && !db.DomainOperations.Any(p => p.DomainId == o.DomainId && p.Operation == o.Operation && p.Id > o.Id));
        var views = new Dictionary<string, IQueryable<RegisteredDomain>>(StringComparer.Ordinal)
        {
            ["fallidas"] = db.Domains.Where(d => d.Status != DomainStatus.Cancelled && failed.Any(o => o.DomainId == d.Id
                && (o.FinishedAt >= failedSince || (o.Operation == "renew" && (d.ExpiresOn == null || d.PaidThrough > d.ExpiresOn))))),
            ["pendientes"] = db.Domains.Where(d => d.Status == DomainStatus.Pending),
            ["gracia"] = db.Domains.Where(d => d.Status == DomainStatus.Grace),
            ["vencen"] = db.Domains.Where(d => d.Status == DomainStatus.Active && (d.ExpiresOn ?? d.PaidThrough) <= soon),
            ["vencidos"] = db.Domains.Where(d => d.Status == DomainStatus.Expired),
            ["sin-renovacion"] = db.Domains.Where(d => d.Status == DomainStatus.Active && !d.AutoRenew),
        };
        var list = new List<View>();
        foreach (var (id, query) in views)
        {
            list.Add(new View(id, Ui.Key("AdminDomainsView", id.Replace('-', '_')), await query.CountAsync()));
        }

        Views = list;
        IQueryable<RegisteredDomain> chosen;
        if (Filter is { } filter && views.TryGetValue(filter, out var selected))
        {
            chosen = selected;
        }
        else if (Q?.Trim() is { Length: > 0 } q)
        {
            Filter = null;
            var pattern = "%" + q.ToLowerInvariant().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            var account = long.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;
            chosen = db.Domains.Where(d => d.AccountId == account || d.Id == account || EF.Functions.Like(d.Name, pattern));
        }
        else
        {
            return Page();
        }

        Rows = await (from d in chosen.AsNoTracking()
                      join a in db.Accounts on d.AccountId equals a.Id
                      join i in db.ModuleInstances on d.RegistrarInstanceId equals i.Id
                      orderby d.ExpiresOn ?? d.PaidThrough, d.Name
                      select new Row(d, a.Name, i.Name)).Take(take).ToListAsync();
        return Page();
    }
}
