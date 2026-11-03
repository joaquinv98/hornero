using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Services;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Portal;

/// <summary>
/// El cliente cambia el plan o las opciones de su servicio (diseno/catalogo.md, sección 5): a los productos que permite el
/// suyo y las opciones visibles si el producto lo deja, siempre con prorrateo y en el mismo ciclo. Primero ve el cálculo y
/// después confirma, con el mismo caso de uso que el staff y la API. Hace falta ver los servicios y las facturas de la cuenta.
/// </summary>
public sealed class ServiceChangeModel(BillingDbContext db, PortalAccess access, ServiceChangeService changes) : PageModel
{
    public sealed class Form
    {
        public long ProductId { get; set; }

        public List<ChoiceRow> Choices { get; set; } = [];
    }

    [BindProperty]
    public Form Input { get; set; } = new();

    public Service? Service { get; private set; }

    public string ProductName { get; private set; } = string.Empty;

    /// <summary>El producto actual y los que permite, del mismo módulo y del mismo tipo.</summary>
    public IReadOnlyList<(long Id, string Name)> Products { get; private set; } = [];

    public OptionCatalog? Options { get; private set; }

    /// <summary>Las opciones que el cliente elige: las visibles, si el producto deja cambiarlas o si pasa a otro producto.</summary>
    public IReadOnlyList<Option> Editable { get; private set; } = [];

    public ChangeQuote? Quote { get; private set; }

    public ServiceChange? Pending { get; private set; }

    public ServiceChange? Done { get; private set; }

    public string? Error { get; private set; }

    public bool CanChange => Products.Count > 1 || Editable.Count > 0;

    private Product? current;

    public async Task<IActionResult> OnGet(long id, long? producto, long? hecho)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        Input.ProductId = producto ?? Service!.ProductId;
        Input.Choices = [.. (await CatalogQueries.Chosen(db, id, HttpContext.RequestAborted)).Select(c => new ChoiceRow { OptionId = c.Option.Id, ValueId = c.Value.Id, Quantity = c.Quantity })];
        Done = hecho is { } changeId ? await db.ServiceChanges.AsNoTracking().SingleOrDefaultAsync(c => c.Id == changeId && c.ServiceId == id) : null;
        await LoadProduct();
        return Page();
    }

    public async Task<IActionResult> OnPostPreview(long id)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        await LoadProduct();
        try
        {
            Quote = await changes.Preview(id, ToRequest(), byCustomer: true, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            Error = ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostConfirm(long id)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        await LoadProduct();
        try
        {
            var result = await changes.Request(id, ToRequest(), byCustomer: true, HttpContext.RequestAborted);
            return Redirect(string.Create(CultureInfo.InvariantCulture, $"/servicios/{id}/cambio?hecho={result.ChangeId}"));
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            Error = ex.Message;
        }

        return Page();
    }

    private ChangeRequest ToRequest() =>
        new(Input.ProductId, null, ChoiceRow.ToChoices(Input.Choices.Where(c => Editable.Any(o => o.Id == c.OptionId)), Options), Prorate: true);

    /// <summary>
    /// El servicio, si la sesión ve los servicios y las facturas de su cuenta (lo ajeno da 404), sus productos posibles y su
    /// cambio pendiente.
    /// </summary>
    private async Task<bool> Load(long id)
    {
        var services = access.AccountIds(User, AccountCapabilities.Services);
        var invoices = access.AccountIds(User, AccountCapabilities.Invoices);
        Service = await db.Services.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && services.Contains(s.AccountId) && invoices.Contains(s.AccountId));
        if (Service is null)
        {
            return false;
        }

        current = await db.Products.AsNoTracking().SingleAsync(p => p.Id == Service.ProductId);
        ProductName = current.Name;
        var allowed = current.UpgradeProductIds;
        var instance = current.ModuleInstanceId;
        var addon = current.Addon;
        Products = [(current.Id, current.Name), .. (await db.Products.AsNoTracking()
            .Where(p => allowed.Contains(p.Id) && p.Id != current.Id && p.ModuleInstanceId == instance && p.Addon == addon)
            .OrderBy(p => p.GroupId).ThenBy(p => p.SortOrder).ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.Name }).ToListAsync()).Select(p => (p.Id, p.Name))];
        Pending = await db.ServiceChanges.AsNoTracking().SingleOrDefaultAsync(c => c.ServiceId == id && c.Status == ServiceChangeStatus.Pending);
        return true;
    }

    /// <summary>Las opciones del producto elegido que el cliente puede tocar, completadas con lo que tiene o el defecto.</summary>
    private async Task LoadProduct()
    {
        if (Products.All(p => p.Id != Input.ProductId))
        {
            Input.ProductId = Service!.ProductId;
        }

        Options = await CatalogQueries.Options(db, Input.ProductId, HttpContext.RequestAborted);
        Editable = Input.ProductId != Service!.ProductId || current!.OptionChanges ? [.. Options.Options.Where(o => !o.Hidden)] : [];
        Input.Choices = ChoiceRow.Complete(Input.Choices, Options, Editable);
    }
}
