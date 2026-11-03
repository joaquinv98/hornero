using System.Globalization;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Catalog;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Portal;

/// <summary>
/// El pedido de un paquete desde Contratar (diseno/catalogo.md, sección 3): una línea por unidad de cada plan, cada una
/// con sus opciones visibles y su etiqueta, y los complementos colgados de la primera línea que los admite. Las líneas
/// llevan el descuento del paquete y no toman cupón. Se calcula y se confirma con los mismos casos de uso que la API.
/// </summary>
public sealed class BundleOrderModel(BillingDbContext db, PortalAccess access, OrderService orders, BillingCore core) : PageModel
{
    public sealed class LineForm
    {
        public string? Label { get; set; }

        public List<ChoiceRow> Choices { get; set; } = [];
    }

    public sealed class Form
    {
        public long AccountId { get; set; }

        public string? Reference { get; set; }

        /// <summary>
        /// El total del último cálculo, con punto decimal en cualquier idioma: si al confirmar da otro, se muestra el nuevo en
        /// vez de pedir.
        /// </summary>
        public string? Total { get; set; }

        public List<LineForm> Lines { get; set; } = [];
    }

    /// <summary>Una unidad del paquete: su plan, su producto, sus opciones visibles y la línea de la que cuelga si es un complemento.</summary>
    public sealed record Line(Plan Plan, Product Product, OptionCatalog Options, IReadOnlyList<Option> Shown, int? ParentItem);

    [BindProperty]
    public Form Input { get; set; } = new();

    public IReadOnlyList<ShopAccount> Accounts { get; private set; } = [];

    public ShopAccount? Account { get; private set; }

    public BundleView? Bundle { get; private set; }

    public IReadOnlyList<Line> Lines { get; private set; } = [];

    public OrderQuote? Quote { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Al confirmar, el pedido ya no daba el total que se calculó: se muestra el nuevo para confirmar otra vez.</summary>
    public bool Changed { get; private set; }

    public async Task<IActionResult> OnGet(long id, long? cuenta)
    {
        if (!await Load(id, cuenta))
        {
            return NotFound();
        }

        Input.Reference = Guid.NewGuid().ToString("N");
        return Page();
    }

    public async Task<IActionResult> OnPostQuote(long id)
    {
        if (!await Load(id, Input.AccountId))
        {
            return NotFound();
        }

        try
        {
            Quote = await orders.Quote(Account!.Id, Items(), bundleId: id, ct: HttpContext.RequestAborted, byCustomer: true);
            Input.Total = Quote.Total.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException or ArgumentException)
        {
            Error = ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostConfirm(long id)
    {
        if (!await Load(id, Input.AccountId))
        {
            return NotFound();
        }

        if (Input.Reference is not { Length: 32 } reference || !reference.All(char.IsAsciiHexDigit))
        {
            Input.Reference = reference = Guid.NewGuid().ToString("N");
        }

        // Un segundo envío del mismo formulario va al pedido que ya se hizo, aunque el cupón o el stock se hayan agotado con él.
        if (await ShopModel.AfterOrder(db, Account!.Id, reference, HttpContext.RequestAborted) is { } done)
        {
            return Redirect(done);
        }

        try
        {
            // Se confirma lo que se vio: si el total cambió (otras opciones, un precio nuevo, el cupón agotado), primero se muestra.
            var quote = await orders.Quote(Account!.Id, Items(), bundleId: id, ct: HttpContext.RequestAborted, byCustomer: true);
            if (!decimal.TryParse(Input.Total, NumberStyles.Number, CultureInfo.InvariantCulture, out var seen) || seen != quote.Total)
            {
                Quote = quote;
                Input.Total = quote.Total.ToString(CultureInfo.InvariantCulture);
                Changed = true;
                return Page();
            }

            var placed = await orders.PlaceOrder(Account!.Id, Items(), HttpContext.RequestAborted, $"portal:{reference}", bundleId: id, byCustomer: true);
            return Redirect(ShopModel.AfterOrder(placed.OrderId, placed.InvoiceId));
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException or ArgumentException)
        {
            Error = ex.Message;
        }

        return Page();
    }

    private List<OrderItem> Items() => [.. Lines.Select((line, i) => new OrderItem(
        line.Plan.Id,
        Label: string.IsNullOrWhiteSpace(Input.Lines[i].Label) || line.Product.Addon ? null : Input.Lines[i].Label!.Trim(),
        Options: ChoiceRow.ToChoices(Input.Lines[i].Choices.Where(c => line.Shown.Any(o => o.Id == c.OptionId)), line.Options),
        ParentItem: line.ParentItem,
        Bundled: true))];

    /// <summary>
    /// El paquete, si está activo y se vende en la moneda de la cuenta, con una línea por unidad de cada plan en el orden
    /// del paquete. Un complemento cuelga de la primera línea de un producto que lo admite.
    /// </summary>
    private async Task<bool> Load(long id, long? accountId)
    {
        var ct = HttpContext.RequestAborted;
        Accounts = await ShopModel.Orderable(db, access, User, ct);
        Account = Accounts.FirstOrDefault(a => a.Id == accountId) ?? Accounts.FirstOrDefault();
        if (Account is null)
        {
            return false;
        }

        var currency = Account.Currency.Code;
        Bundle = (await CatalogQueries.Bundles(db, core.Today(), [id], ct)).SingleOrDefault(b => b.Active && b.Prices.Any(p => p.Currency == currency));
        if (Bundle is null)
        {
            return false;
        }

        var planIds = Bundle.Items.Select(i => i.PlanId).ToList();
        var plans = await db.Plans.AsNoTracking().Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var productIds = Bundle.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var catalogs = await CatalogQueries.Options(db, productIds, ct);
        var units = Bundle.Items.SelectMany(i => Enumerable.Repeat(i, i.Quantity)).Select(i => (Plan: plans[i.PlanId], Product: products[i.ProductId])).ToList();
        Lines = [.. units.Select(u =>
        {
            var parent = u.Product.Addon
                ? units.FindIndex(l => !l.Product.Addon && (u.Product.ParentProductIds.Length == 0 || u.Product.ParentProductIds.Contains(l.Product.Id)))
                : -1;
            var options = catalogs[u.Product.Id];
            return new Line(u.Plan, u.Product, options, [.. options.Options.Where(o => !o.Hidden)], parent >= 0 ? parent : null);
        })];
        Input.AccountId = Account.Id;
        while (Input.Lines.Count < Lines.Count)
        {
            Input.Lines.Add(new LineForm());
        }

        Input.Lines = [.. Input.Lines.Take(Lines.Count)];
        for (var i = 0; i < Lines.Count; i++)
        {
            Input.Lines[i].Choices = ChoiceRow.Complete(Input.Lines[i].Choices, Lines[i].Options, Lines[i].Shown);
        }

        return true;
    }
}
