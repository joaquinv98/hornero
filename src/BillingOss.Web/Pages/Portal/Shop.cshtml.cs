using System.Globalization;
using System.Security.Claims;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Shared;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Portal;

/// <summary>Una cuenta que puede contratar, con su moneda: los precios de la tienda salen en ella.</summary>
public sealed record ShopAccount(long Id, string Name, Currency Currency);

/// <summary>
/// Contratar (sección 10.3 de la decisión de interfaz y diseno/catalogo.md): los productos visibles por grupo, los
/// complementos y los paquetes, con su precio en la moneda de la cuenta. Cada uno lleva a su pedido. Contrata quien ve
/// los servicios y las facturas de una cuenta activa, porque el pedido crea los dos.
/// </summary>
public sealed class ShopModel(BillingDbContext db, PortalAccess access, BillingCore core) : PageModel
{
    public IReadOnlyList<ShopAccount> Accounts { get; private set; } = [];

    public ShopAccount? Account { get; private set; }

    public IReadOnlyList<(ProductGroup? Group, IReadOnlyList<ProductView> Products)> Groups { get; private set; } = [];

    public IReadOnlyList<ProductView> Addons { get; private set; } = [];

    public IReadOnlyList<BundleView> Bundles { get; private set; } = [];

    /// <summary>
    /// El "desde" de cada producto: el ciclo más corto que se puede pedir en la moneda de la cuenta, con el precio del plan
    /// más lo menos que cobran sus opciones. Un producto que cobra por sus opciones no sale en cero.
    /// </summary>
    public IReadOnlyDictionary<long, (string Cycle, decimal Amount)> From { get; private set; } = new Dictionary<long, (string, decimal)>();

    /// <summary>Las cuentas activas donde la sesión ve los servicios y las facturas.</summary>
    public static async Task<List<ShopAccount>> Orderable(BillingDbContext db, PortalAccess access, ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(access);
        var services = access.AccountIds(user, AccountCapabilities.Services);
        var invoices = access.AccountIds(user, AccountCapabilities.Invoices);
        return [.. (await db.Accounts.AsNoTracking()
            .Where(a => a.Status == AccountStatus.Active && services.Contains(a.Id) && invoices.Contains(a.Id))
            .OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.Name, a.Currency })
            .ToListAsync(ct)).Select(a => new ShopAccount(a.Id, a.Name, a.Currency))];
    }

    /// <summary>
    /// Adónde va el cliente con un pedido hecho desde la tienda: a la factura para pagarla o, si no dejó nada para pagar, a
    /// sus servicios. Con <paramref name="reference"/> busca un pedido ya hecho con esa referencia, o nulo si no hay.
    /// </summary>
    public static async Task<string?> AfterOrder(BillingDbContext db, long accountId, string reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var key = $"portal:{reference}";
        var order = await db.Orders.AsNoTracking().Where(o => o.AccountId == accountId && o.ClientReference == key)
            .Select(o => new { o.Id, o.InvoiceId }).SingleOrDefaultAsync(ct);
        return order is null ? null : AfterOrder(order.Id, order.InvoiceId);
    }

    public static string AfterOrder(long orderId, long? invoiceId) => invoiceId is { } invoice
        ? string.Create(CultureInfo.InvariantCulture, $"/facturas/{invoice}")
        : string.Create(CultureInfo.InvariantCulture, $"/servicios?pedido={orderId}");

    public async Task OnGet(long? cuenta)
    {
        var ct = HttpContext.RequestAborted;
        Accounts = await Orderable(db, access, User, ct);
        Account = Accounts.FirstOrDefault(a => a.Id == cuenta) ?? Accounts.FirstOrDefault();
        if (Account is null)
        {
            return;
        }

        // Se ofrece lo visible de los grupos visibles que tiene precio en la moneda de la cuenta.
        var today = core.Today();
        var currency = Account.Currency.Code;
        var groups = await db.ProductGroups.AsNoTracking().OrderBy(g => g.SortOrder).ThenBy(g => g.Id).ToListAsync(ct);
        var hidden = groups.Where(g => !g.Visible).Select(g => (long?)g.Id).ToHashSet();
        var products = (await CatalogQueries.Products(db, today, null, ct))
            .Where(p => p.Visible && !hidden.Contains(p.GroupId) && p.Prices.Any(x => x.Currency == currency))
            .ToList();
        var options = await CatalogQueries.Options(db, [.. products.Select(p => p.Id)], ct);
        var from = new Dictionary<long, (string Cycle, decimal Amount)>();
        foreach (var p in products)
        {
            var orderable = p.Prices.Where(x => x.Currency == currency)
                .Select(x => (x.Cycle, x.Amount, Options: options[p.Id].Cheapest(Account.Currency, BillingCycle.Parse(x.Cycle))))
                .Where(x => x.Options is not null)
                .OrderBy(x => x.Cycle == "free" ? 0 : x.Cycle == "onetime" ? 1 : 2).ThenBy(x => BillingCycle.Parse(x.Cycle).Months)
                .FirstOrDefault();
            if (orderable.Options is { } cheapest)
            {
                from[p.Id] = (orderable.Cycle, orderable.Amount + cheapest.Amount);
            }
        }

        From = from;
        products = [.. products.Where(p => from.ContainsKey(p.Id))];
        Groups = [.. groups.Where(g => g.Visible)
            .Select(g => ((ProductGroup?)g, (IReadOnlyList<ProductView>)[.. products.Where(p => !p.Addon && p.GroupId == g.Id)]))
            .Append((null, [.. products.Where(p => !p.Addon && p.GroupId is null)]))
            .Where(x => x.Item2.Count > 0)];
        Addons = [.. products.Where(p => p.Addon)];
        Bundles = [.. (await CatalogQueries.Bundles(db, today, null, ct)).Where(b => b.Active && b.Prices.Any(p => p.Currency == currency))];
    }
}
