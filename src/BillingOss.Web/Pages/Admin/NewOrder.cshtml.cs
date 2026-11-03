using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin;

/// <summary>
/// Agregar pedido (menú Pedidos de la sección 10 de la decisión de interfaz): el staff pide por una cuenta cualquier
/// producto, también los que la tienda no muestra, con todas sus opciones y el cupón, y puede aceptarlo para que se
/// aprovisione sin esperar el pago. Se calcula y se confirma con los mismos casos de uso que el portal y la API, y la
/// referencia del formulario hace que un doble envío no pida dos veces, en cualquier nodo.
/// </summary>
[Authorize(Policy = StaffPermissions.AccountsRead)]
[Authorize(Policy = StaffPermissions.OrdersWrite)]
public sealed class NewOrderModel(BillingDbContext db, OrderService orders, BillingCore core, AuditLog audit) : PageModel
{
    public sealed class Form
    {
        public long AccountId { get; set; }

        public long ProductId { get; set; }

        public long PlanId { get; set; }

        public int Quantity { get; set; } = 1;

        public long? ParentServiceId { get; set; }

        public string? Label { get; set; }

        public string? Coupon { get; set; }

        /// <summary>Aceptar el pedido y aprovisionar sin esperar el pago de la factura.</summary>
        public bool Accept { get; set; }

        public string? Reference { get; set; }

        /// <summary>El total del último cálculo, con punto decimal: si al confirmar da otro, se muestra el nuevo.</summary>
        public string? Total { get; set; }

        public List<ChoiceRow> Choices { get; set; } = [];
    }

    [BindProperty]
    public Form Input { get; set; } = new();

    public Account? Account { get; private set; }

    /// <summary>Todos los productos por grupo, también los ocultos, para elegir qué se pide.</summary>
    public IReadOnlyList<(long Id, string Name, bool Visible)> Products { get; private set; } = [];

    public Product? Product { get; private set; }

    /// <summary>Los planes activos del producto con su precio vigente en la moneda de la cuenta.</summary>
    public IReadOnlyList<(Plan Plan, PlanPrice Price)> Plans { get; private set; } = [];

    public OptionCatalog? Options { get; private set; }

    /// <summary>Los servicios vivos de la cuenta de los que puede colgar el complemento.</summary>
    public IReadOnlyList<(long Id, string Name)> Parents { get; private set; } = [];

    public OrderQuote? Quote { get; private set; }

    /// <summary>El pedido recién hecho, con su factura si dejó algo para pagar.</summary>
    public Order? Done { get; private set; }

    public Document? DoneInvoice { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Al confirmar, el pedido ya no daba el total que se calculó: se muestra el nuevo para confirmar otra vez.</summary>
    public bool Changed { get; private set; }

    public BillingCycle Cycle => Plans.FirstOrDefault(p => p.Plan.Id == Input.PlanId).Plan?.Cycle ?? BillingCycle.Monthly;

    public static string Link(long accountId) => string.Create(CultureInfo.InvariantCulture, $"/admin/pedidos/nuevo?cuenta={accountId}");

    public async Task<IActionResult> OnGet(long? cuenta, long? producto, long? hecho)
    {
        await Load(cuenta, producto);
        if (Account is not null && hecho is { } orderId)
        {
            Done = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId && o.AccountId == Account.Id);
            DoneInvoice = Done?.InvoiceId is { } invoice ? await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == invoice) : null;
        }

        Input.Reference = Guid.NewGuid().ToString("N");
        return Page();
    }

    public async Task<IActionResult> OnPostQuote()
    {
        if (!await Load(Input.AccountId, Input.ProductId))
        {
            return NotFound();
        }

        try
        {
            Quote = await orders.Quote(Account!.Id, [Item()], Coupon(), ct: HttpContext.RequestAborted);
            Input.Total = Quote.Total.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException or ArgumentException)
        {
            Error = ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostConfirm()
    {
        if (!await Load(Input.AccountId, Input.ProductId))
        {
            return NotFound();
        }

        // Una referencia que no vino del formulario no protege de nada: se genera otra.
        if (Input.Reference is not { Length: 32 } reference || !reference.All(char.IsAsciiHexDigit))
        {
            Input.Reference = reference = Guid.NewGuid().ToString("N");
        }

        var ct = HttpContext.RequestAborted;
        var key = $"admin:{reference}";
        if (await db.Orders.AsNoTracking().Where(o => o.AccountId == Account!.Id && o.ClientReference == key).Select(o => (long?)o.Id).SingleOrDefaultAsync(ct) is { } existing)
        {
            return Redirect(DoneLink(existing));
        }

        try
        {
            // Se confirma lo que se vio: si el total cambió, primero se muestra.
            var quote = await orders.Quote(Account!.Id, [Item()], Coupon(), ct: ct);
            if (!decimal.TryParse(Input.Total, NumberStyles.Number, CultureInfo.InvariantCulture, out var seen) || seen != quote.Total)
            {
                Quote = quote;
                Input.Total = quote.Total.ToString(CultureInfo.InvariantCulture);
                Changed = true;
                return Page();
            }

            var placed = await orders.PlaceOrder(Account.Id, [Item()], ct, key, couponCode: Coupon());
            if (Input.Accept)
            {
                await orders.Accept(placed.OrderId, provision: true, ct);
            }

            await audit.TryRecord("order.placed_by_staff", $"order:{placed.OrderId}", new { account_id = Account.Id, accepted = Input.Accept });
            return Redirect(DoneLink(placed.OrderId));
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException or ArgumentException)
        {
            Error = ex.Message;
        }

        return Page();
    }

    private string DoneLink(long orderId) => string.Create(CultureInfo.InvariantCulture, $"{Link(Account!.Id)}&hecho={orderId}");

    private string? Coupon() => string.IsNullOrWhiteSpace(Input.Coupon) ? null : Input.Coupon.Trim();

    private OrderItem Item() => new(
        Input.PlanId,
        Quantity: Product!.AllowQuantity ? Input.Quantity : 1,
        Label: string.IsNullOrWhiteSpace(Input.Label) ? null : Input.Label.Trim(),
        Options: ChoiceRow.ToChoices(Input.Choices, Options),
        ParentServiceId: Product.Addon ? Input.ParentServiceId : null);

    /// <summary>
    /// La cuenta, los productos para elegir y, si ya se eligió uno, sus planes con precio en la moneda de la cuenta, todas
    /// sus opciones con lo elegido o el defecto, y los servicios de los que puede colgar si es un complemento.
    /// </summary>
    private async Task<bool> Load(long? accountId, long? productId)
    {
        var ct = HttpContext.RequestAborted;
        Account = accountId is { } id ? await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) : null;
        if (Account is null)
        {
            return false;
        }

        Input.AccountId = Account.Id;
        var groups = await db.ProductGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.SortOrder, ct);
        Products = [.. (await db.Products.AsNoTracking().Select(p => new { p.Id, p.Name, p.Visible, p.GroupId, p.SortOrder }).ToListAsync(ct))
            .OrderBy(p => p.GroupId is { } g ? groups.GetValueOrDefault(g) : int.MaxValue).ThenBy(p => p.GroupId).ThenBy(p => p.SortOrder).ThenBy(p => p.Id)
            .Select(p => (p.Id, p.Name, p.Visible))];
        Product = productId is { } wanted ? await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == wanted, ct) : null;
        if (Product is null)
        {
            return false;
        }

        Input.ProductId = Product.Id;
        var today = core.Today();
        var priced = new List<(Plan Plan, PlanPrice Price)>();
        foreach (var plan in (await db.Plans.AsNoTracking().Where(p => p.ProductId == Product.Id && p.Active).ToListAsync(ct))
            .OrderBy(p => p.CycleUnit == CycleUnit.Free ? 0 : p.Cycle.Months == 0 ? 1 : 2).ThenBy(p => p.Cycle.Months))
        {
            if (await CatalogQueries.Price(db, plan.Id, Account.Currency, today, ct) is { } price)
            {
                priced.Add((plan, price));
            }
        }

        Plans = priced;
        if (Plans.All(p => p.Plan.Id != Input.PlanId) && Plans.Count > 0)
        {
            Input.PlanId = Plans[0].Plan.Id;
        }

        Options = await CatalogQueries.Options(db, Product.Id, ct);
        Input.Choices = ChoiceRow.Complete(Input.Choices, Options, Options.Options);
        if (Product.Addon)
        {
            var parents = Product.ParentProductIds;
            var account = Account.Id;
            Parents = [.. (await (from s in db.Services.AsNoTracking()
                                  join p in db.Products on s.ProductId equals p.Id
                                  where s.AccountId == account && !p.Addon && (parents.Length == 0 || parents.Contains(p.Id))
                                      && (s.Status == ServiceStatus.Pending || s.Status == ServiceStatus.Provisioning || s.Status == ServiceStatus.Active || s.Status == ServiceStatus.Suspended)
                                  orderby s.Id
                                  select new { s.Id, p.Name, s.Label }).ToListAsync(ct))
                .Select(s => (s.Id, string.Create(CultureInfo.InvariantCulture, $"#{s.Id} {s.Name}") + (s.Label is { Length: > 0 } label ? $" · {label}" : string.Empty)))];
        }

        return Plans.Count > 0;
    }
}
