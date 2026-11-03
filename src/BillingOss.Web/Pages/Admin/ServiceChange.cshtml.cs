using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin;

/// <summary>
/// El staff cambia el producto, el ciclo o las opciones de un servicio (diseno/catalogo.md, sección 5): primero ve el
/// cálculo y después lo confirma, con el mismo caso de uso que la API. También cancela un cambio que espera el pago.
/// </summary>
[Authorize(Policy = StaffPermissions.ServicesChange)]
public sealed class ServiceChangeModel(BillingDbContext db, ServiceChangeService changes) : PageModel
{
    public sealed class Form
    {
        public long ProductId { get; set; }

        public string? Cycle { get; set; }

        public bool Prorate { get; set; } = true;

        public List<ChoiceRow> Choices { get; set; } = [];
    }

    [BindProperty]
    public Form Input { get; set; } = new();

    public Service? Service { get; private set; }

    public string AccountName { get; private set; } = string.Empty;

    public string ProductName { get; private set; } = string.Empty;

    public IReadOnlyList<(long Id, string Name)> Products { get; private set; } = [];

    public IReadOnlyList<string> Cycles { get; private set; } = [];

    public OptionCatalog? Options { get; private set; }

    public ChangeQuote? Quote { get; private set; }

    public ServiceChange? Pending { get; private set; }

    public ServiceChange? Done { get; private set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGet(long id, long? producto, long? hecho)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        Input.ProductId = producto ?? Service!.ProductId;
        Input.Cycle = Service!.Cycle.Code;
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

        try
        {
            Quote = await changes.Preview(id, ToRequest(), ct: HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            Error = ex.Message;
        }

        await LoadProduct();
        return Page();
    }

    public async Task<IActionResult> OnPostConfirm(long id)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        try
        {
            var result = await changes.Request(id, ToRequest(), ct: HttpContext.RequestAborted);
            return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/servicios/{id}/cambio?hecho={result.ChangeId}"));
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
        {
            Error = ex.Message;
        }

        await LoadProduct();
        return Page();
    }

    public async Task<IActionResult> OnPostCancel(long id, long change)
    {
        if (!await Load(id) || Pending?.Id != change)
        {
            return NotFound();
        }

        try
        {
            await changes.Cancel(change, HttpContext.RequestAborted);
            return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/servicios/{id}/cambio?hecho={change}"));
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
        }

        Input.ProductId = Service!.ProductId;
        Input.Cycle = Service.Cycle.Code;
        await LoadProduct();
        return Page();
    }

    private ChangeRequest ToRequest() => new(Input.ProductId, Input.Cycle, ChoiceRow.ToChoices(Input.Choices, Options), Input.Prorate);

    /// <summary>El servicio, su cuenta, los productos a los que puede pasar (misma instancia de módulo) y su cambio pendiente.</summary>
    private async Task<bool> Load(long id)
    {
        Service = await db.Services.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id);
        if (Service is null)
        {
            return false;
        }

        var current = await db.Products.AsNoTracking().SingleAsync(p => p.Id == Service.ProductId);
        ProductName = current.Name;
        AccountName = await db.Accounts.Where(a => a.Id == Service.AccountId).Select(a => a.Name).SingleAsync();
        Products = [.. (await db.Products.AsNoTracking()
            .Where(p => p.Id == current.Id || (p.ModuleInstanceId == current.ModuleInstanceId && p.Addon == current.Addon))
            .OrderBy(p => p.GroupId).ThenBy(p => p.SortOrder).ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.Name }).ToListAsync()).Select(p => (p.Id, p.Name))];
        Pending = await db.ServiceChanges.AsNoTracking().SingleOrDefaultAsync(c => c.ServiceId == id && c.Status == ServiceChangeStatus.Pending);

        // Las opciones del formulario son las del producto elegido, así el pedido se interpreta con ellas.
        var productId = Input.ProductId > 0 ? Input.ProductId : Service.ProductId;
        Options = await CatalogQueries.Options(db, productId, HttpContext.RequestAborted);
        return true;
    }

    /// <summary>Los ciclos que vende el producto elegido, y lo elegido completado con los defectos de sus opciones.</summary>
    private async Task LoadProduct()
    {
        if (Products.All(p => p.Id != Input.ProductId))
        {
            Input.ProductId = Service!.ProductId;
        }

        Options = await CatalogQueries.Options(db, Input.ProductId, HttpContext.RequestAborted);
        Cycles = [.. (await db.Plans.AsNoTracking().Where(p => p.ProductId == Input.ProductId && p.Active).ToListAsync())
            .Select(p => p.Cycle).Where(c => c.IsRecurring == Service!.Cycle.IsRecurring).Select(c => c.Code)];
        Input.Choices = ChoiceRow.Complete(Input.Choices, Options, Options.Options);
    }
}
