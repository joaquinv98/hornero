using BillingOss.Web.Identity;
using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin.Billing;

/// <summary>
/// Ítems facturables sueltos: se cargan para una cuenta, se cancelan mientras están pendientes y se facturan con la
/// corrida de su día o en el momento.
/// </summary>
[Authorize(Policy = StaffPermissions.BillingRead)]
public sealed class ItemsModel(BillingDbContext db, BillableItemService items) : PageModel
{
    public sealed record Row(BillableItem Item, string Account);

    [BindProperty(SupportsGet = true, Name = "cuenta")]
    public long? AccountId { get; set; }

    [BindProperty(SupportsGet = true, Name = "estado")]
    public BillableItemStatus? State { get; set; }

    [BindProperty]
    public string? Description { get; set; }

    [BindProperty]
    public string? Quantity { get; set; }

    [BindProperty]
    public string? UnitPrice { get; set; }

    [BindProperty]
    public TaxTreatment Treatment { get; set; } = TaxTreatment.Taxed;

    [BindProperty]
    public string? Currency { get; set; }

    [BindProperty]
    public DateOnly? InvoiceOn { get; set; }

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public string? Error { get; private set; }

    public bool CanWrite => this.Can(StaffPermissions.BillingWrite);

    public async Task OnGet()
    {
        State ??= BillableItemStatus.Pending;
        await Load();
    }

    public Task<IActionResult> OnPostAdd() => Act(async () =>
    {
        if (AccountId is not { } accountId)
        {
            throw new ValidationException("Elegí la cuenta.");
        }

        var line = BillingForm.Lines([new LineRow { Description = Description, Quantity = Quantity, UnitPrice = UnitPrice, Treatment = Treatment }]).FirstOrDefault()
            ?? throw new ValidationException("Cargá la descripción y el precio.");
        await items.Add(accountId, new BillableItemInput(line.Description, line.Quantity, line.UnitPrice, line.Treatment, Currency: string.IsNullOrWhiteSpace(Currency) ? null : Currency, InvoiceOn: InvoiceOn), HttpContext.RequestAborted);
    });

    public Task<IActionResult> OnPostCancel(long item) => Act(() => items.Cancel(item, HttpContext.RequestAborted));

    public Task<IActionResult> OnPostInvoice() => Act(async () =>
    {
        if (AccountId is not { } accountId)
        {
            throw new ValidationException("Elegí la cuenta.");
        }

        if ((await items.InvoiceNow(accountId, ct: HttpContext.RequestAborted)).Count == 0)
        {
            throw new ValidationException("La cuenta no tiene ítems pendientes.");
        }
    });

    private async Task<IActionResult> Act(Func<Task> work)
    {
        if (!CanWrite)
        {
            return Forbid(Schemes.Staff);
        }

        try
        {
            await work();
            return Redirect(AccountId is { } id ? string.Create(CultureInfo.InvariantCulture, $"/admin/items?cuenta={id}") : "/admin/items");
        }
        catch (Exception ex) when (ex is ValidationException or KeyNotFoundException)
        {
            Error = ex.Message;
            State = BillableItemStatus.Pending;
            await Load();
            return Page();
        }
    }

    private async Task Load()
    {
        var query = db.BillableItems.AsNoTracking();
        if (State is { } state)
        {
            query = query.Where(i => i.Status == state);
        }

        if (AccountId is { } account)
        {
            query = query.Where(i => i.AccountId == account);
        }

        Rows = [.. (await query
            .Join(db.Accounts, i => i.AccountId, a => a.Id, (i, a) => new { Item = i, a.Name })
            .OrderBy(x => x.Item.InvoiceOn).ThenBy(x => x.Item.Id)
            .Take(200)
            .ToListAsync()).Select(x => new Row(x.Item, x.Name))];
    }
}
