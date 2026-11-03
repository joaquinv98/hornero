using System.Globalization;
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
/// El pedido de un producto desde Contratar: el ciclo, las opciones visibles, la etiqueta, el servicio del que cuelga si
/// es un complemento, los complementos que se suman en el mismo pedido y el cupón. Primero se calcula con la cotización
/// y después se confirma con el pedido, los mismos casos de uso que la API. La referencia del formulario hace que un
/// doble envío no pida dos veces, en cualquier nodo.
/// </summary>
public sealed class OrderModel(BillingDbContext db, PortalAccess access, OrderService orders, BillingCore core) : PageModel
{
    public sealed class Form
    {
        public long AccountId { get; set; }

        public long PlanId { get; set; }

        public long? ParentServiceId { get; set; }

        public string? Label { get; set; }

        public string? Coupon { get; set; }

        public string? Reference { get; set; }

        /// <summary>
        /// El total del último cálculo, con punto decimal en cualquier idioma: si al confirmar da otro, se muestra el nuevo en
        /// vez de pedir.
        /// </summary>
        public string? Total { get; set; }

        public List<ChoiceRow> Choices { get; set; } = [];

        /// <summary>Los planes de complementos que se suman al pedido, colgados de la línea del producto.</summary>
        public List<long> AddonPlanIds { get; set; } = [];
    }

    [BindProperty]
    public Form Input { get; set; } = new();

    public IReadOnlyList<ShopAccount> Accounts { get; private set; } = [];

    public ShopAccount? Account { get; private set; }

    public Product? Product { get; private set; }

    /// <summary>
    /// Los planes activos que se pueden pedir en la moneda de la cuenta, del ciclo más corto al más largo, con lo menos que
    /// cobran por período y de alta con sus opciones.
    /// </summary>
    public IReadOnlyList<(Plan Plan, decimal From, decimal SetupFee)> Plans { get; private set; } = [];

    public OptionCatalog? Options { get; private set; }

    public IReadOnlyList<Option> Shown { get; private set; } = [];

    /// <summary>Los servicios activos de la cuenta de los que puede colgar el complemento.</summary>
    public IReadOnlyList<(long Id, string Name)> Parents { get; private set; } = [];

    /// <summary>
    /// Los complementos visibles que admite el producto, plan por plan con lo que cobran por período y de alta en la
    /// moneda de la cuenta con sus opciones por defecto, que son las que llevan.
    /// </summary>
    public IReadOnlyList<(Product Addon, Plan Plan, decimal Amount, decimal SetupFee)> Addons { get; private set; } = [];

    public OrderQuote? Quote { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Al confirmar, el pedido ya no daba el total que se calculó: se muestra el nuevo para confirmar otra vez.</summary>
    public bool Changed { get; private set; }

    public BillingCycle Cycle => Plans.FirstOrDefault(p => p.Plan.Id == Input.PlanId).Plan?.Cycle ?? BillingCycle.Monthly;

    public bool OutOfStock => Product?.Stock is <= 0;

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
            Quote = await orders.Quote(Account!.Id, Items(), Coupon(), ct: HttpContext.RequestAborted, byCustomer: true);
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

        // Una referencia que no vino del formulario no protege de nada: se genera otra.
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
            var quote = await orders.Quote(Account!.Id, Items(), Coupon(), ct: HttpContext.RequestAborted, byCustomer: true);
            if (!decimal.TryParse(Input.Total, NumberStyles.Number, CultureInfo.InvariantCulture, out var seen) || seen != quote.Total)
            {
                Quote = quote;
                Input.Total = quote.Total.ToString(CultureInfo.InvariantCulture);
                Changed = true;
                return Page();
            }

            var placed = await orders.PlaceOrder(Account!.Id, Items(), HttpContext.RequestAborted, $"portal:{reference}", couponCode: Coupon(), byCustomer: true);
            return Redirect(ShopModel.AfterOrder(placed.OrderId, placed.InvoiceId));
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException or ArgumentException)
        {
            Error = ex.Message;
        }

        return Page();
    }

    private string? Coupon() => string.IsNullOrWhiteSpace(Input.Coupon) ? null : Input.Coupon.Trim();

    /// <summary>El producto en la primera línea y cada complemento elegido en la suya, colgado de ella.</summary>
    private List<OrderItem> Items() => [Item(), .. Input.AddonPlanIds.Distinct().Where(id => Addons.Any(a => a.Plan.Id == id)).Select(id => new OrderItem(id, ParentItem: 0))];

    private OrderItem Item() => new(
        Input.PlanId,
        Label: string.IsNullOrWhiteSpace(Input.Label) ? null : Input.Label.Trim(),
        Options: ChoiceRow.ToChoices(Input.Choices.Where(c => Shown.Any(o => o.Id == c.OptionId)), Options),
        ParentServiceId: Product!.Addon ? Input.ParentServiceId : null);

    /// <summary>
    /// El producto, si se vende en la tienda (visible, en un grupo visible y con precio en la moneda de la cuenta), la
    /// cuenta con la que se pide, sus planes, sus opciones visibles completadas con lo elegido o el defecto, y los padres
    /// posibles de un complemento. Lo que no se vende da 404, igual que lo que no existe.
    /// </summary>
    private async Task<bool> Load(long id, long? accountId)
    {
        var ct = HttpContext.RequestAborted;
        Accounts = await ShopModel.Orderable(db, access, User, ct);
        Account = Accounts.FirstOrDefault(a => a.Id == accountId) ?? Accounts.FirstOrDefault();
        Product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.Visible && (p.GroupId == null || db.ProductGroups.Any(g => g.Id == p.GroupId && g.Visible)), ct);
        if (Account is null || Product is null)
        {
            return false;
        }

        var today = core.Today();
        Options = await CatalogQueries.Options(db, id, ct);
        var plans = await db.Plans.AsNoTracking().Where(p => p.ProductId == id && p.Active).ToListAsync(ct);
        var priced = new List<(Plan Plan, decimal From, decimal SetupFee)>();
        foreach (var plan in plans.OrderBy(p => p.CycleUnit == CycleUnit.Free ? 0 : p.Cycle.Months == 0 ? 1 : 2).ThenBy(p => p.Cycle.Months))
        {
            if (await CatalogQueries.Price(db, plan.Id, Account.Currency, today, ct) is { } price && Options.Cheapest(Account.Currency, plan.Cycle) is { } cheapest)
            {
                priced.Add((plan, price.Amount + cheapest.Amount, price.SetupFee + cheapest.SetupFee));
            }
        }

        Plans = priced;
        if (Plans.Count == 0)
        {
            return false;
        }

        Input.AccountId = Account.Id;
        if (Plans.All(p => p.Plan.Id != Input.PlanId))
        {
            Input.PlanId = Plans[0].Plan.Id;
        }

        Shown = [.. Options.Options.Where(o => !o.Hidden)];
        Input.Choices = ChoiceRow.Complete(Input.Choices, Options, Shown);
        if (!Product.Addon)
        {
            // Los complementos visibles de este producto (o de cualquiera), con los planes que tienen precio en la moneda.
            var addons = await db.Products.AsNoTracking()
                .Where(p => p.Addon && p.Visible && (p.ParentProductIds.Length == 0 || p.ParentProductIds.Contains(id)))
                .OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync(ct);
            var addonIds = addons.Select(a => a.Id).ToList();
            var addonPlans = await db.Plans.AsNoTracking().Where(p => addonIds.Contains(p.ProductId) && p.Active).ToListAsync(ct);
            var addonOptions = await CatalogQueries.Options(db, addonIds, ct);
            var offered = new List<(Product Addon, Plan Plan, decimal Amount, decimal SetupFee)>();
            foreach (var addon in addons)
            {
                foreach (var plan in addonPlans.Where(p => p.ProductId == addon.Id).OrderBy(p => p.Cycle.Months))
                {
                    if (await CatalogQueries.Price(db, plan.Id, Account.Currency, today, ct) is not { } price)
                    {
                        continue;
                    }

                    try
                    {
                        var defaults = addonOptions[addon.Id].Resolve(null, Account.Currency, plan.Cycle);
                        offered.Add((addon, plan, price.Amount + defaults.Sum(c => c.Amount), price.SetupFee + defaults.Sum(c => c.SetupFee)));
                    }
                    catch (ArgumentException)
                    {
                        // Sus opciones por defecto no se ofrecen en esa moneda o ese ciclo: ese plan no se suma.
                    }
                }
            }

            Addons = offered;
        }
        else
        {
            var parents = Product.ParentProductIds;
            var account = Account.Id;
            Parents = [.. (await (from s in db.Services.AsNoTracking()
                                  join p in db.Products on s.ProductId equals p.Id
                                  where s.AccountId == account && s.Status == ServiceStatus.Active && !p.Addon && (parents.Length == 0 || parents.Contains(p.Id))
                                  orderby s.Id
                                  select new { s.Id, p.Name, s.Label }).ToListAsync(ct))
                .Select(s => (s.Id, s.Label is { Length: > 0 } label ? $"{s.Name} · {label}" : s.Name))];
        }

        return true;
    }
}
