using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.Domain.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin.Billing;

/// <summary>Facturas y notas de todas las cuentas, con filtros por estado, tipo, cuenta y número.</summary>
[Authorize(Policy = StaffPermissions.BillingRead)]
public sealed class InvoicesModel(BillingDbContext db, IOptions<BillingOptions> options, TimeProvider time) : PageModel
{
    public const int Limit = 200;

    public sealed record Row(Document Document, string Account);

    /// <summary>Los mismos valores que usan el menú y la línea de pendientes (docs/interfaz/menu.md).</summary>
    public static IReadOnlyList<string> States { get; } = ["", "borradores", "por-pagar", "vencidas", "pagadas", "anuladas"];

    public static IReadOnlyList<(string Value, DocumentKind Kind)> Kinds { get; } =
        [("factura", DocumentKind.Invoice), ("nota-de-credito", DocumentKind.CreditNote), ("nota-de-debito", DocumentKind.DebitNote)];

    [BindProperty(SupportsGet = true, Name = "estado")]
    public string? State { get; set; }

    [BindProperty(SupportsGet = true, Name = "tipo")]
    public string? Kind { get; set; }

    [BindProperty(SupportsGet = true, Name = "cuenta")]
    public long? AccountId { get; set; }

    [BindProperty(SupportsGet = true, Name = "numero")]
    public long? Number { get; set; }

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public async Task OnGet()
    {
        var today = options.Value.Today(time);
        var query = db.Documents.AsNoTracking();
        query = State switch
        {
            "borradores" => query.Where(d => d.IssueDate == null),
            "por-pagar" => query.Where(d => d.IssueDate != null && d.Kind != DocumentKind.CreditNote && d.Total - d.Credited - d.Allocated > 0),
            "vencidas" => query.Where(d => d.IssueDate != null && d.Kind != DocumentKind.CreditNote && d.Total - d.Credited - d.Allocated > 0 && d.DueDate < today),
            "pagadas" => query.Where(d => d.IssueDate != null && d.Kind != DocumentKind.CreditNote && d.Total - d.Credited - d.Allocated == 0 && d.Credited < d.Total),
            "anuladas" => query.Where(d => d.IssueDate != null && d.Total > 0 && d.Credited >= d.Total),
            _ => query,
        };
        if (Kinds.FirstOrDefault(k => k.Value == Kind) is { Value: not null } kind)
        {
            query = query.Where(d => d.Kind == kind.Kind);
        }

        if (AccountId is { } account)
        {
            query = query.Where(d => d.AccountId == account);
        }

        if (Number is { } number)
        {
            query = query.Where(d => d.Number == number || d.Id == number);
        }

        Rows = [.. (await query
            .Join(db.Accounts, d => d.AccountId, a => a.Id, (d, a) => new { Document = d, a.Name })
            .OrderByDescending(x => x.Document.Id)
            .Take(Limit)
            .ToListAsync()).Select(x => new Row(x.Document, x.Name))];
    }
}
