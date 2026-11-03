using System.Globalization;
using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Services;
using BillingOss.Domain.Usage;
using BillingOss.Web.Content;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Portal;

public sealed class ServicesModel(BillingDbContext db, PortalAccess access, KnownIssues knownIssues) : PageModel
{
    /// <summary>Un servicio con su producto, su cuenta y si el cliente le puede pedir un cambio de plan o de opciones.</summary>
    public sealed record Row(Service Service, string Product, string Account, bool CanChange);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public bool ManyAccounts { get; private set; }

    /// <summary>Las cuentas cuyos servicios se pueden consultar con soporte.</summary>
    public IReadOnlySet<long> SupportAccounts { get; private set; } = new HashSet<long>();

    /// <summary>Productos que miden uso: sus servicios llevan el enlace a la página de uso.</summary>
    public IReadOnlySet<long> Metered { get; private set; } = new HashSet<long>();

    /// <summary>Los incidentes y mantenimientos de la red que tocan los productos de sus servicios.</summary>
    public IReadOnlyList<EventView> Issues { get; private set; } = [];

    /// <summary>El pedido recién hecho desde Contratar que no dejó nada para pagar, si es de una cuenta de la sesión.</summary>
    public long? Placed { get; private set; }

    /// <summary>El pedido recién hecho espera que lo acepte el staff: su producto no se activa solo.</summary>
    public bool PlacedPending { get; private set; }

    public async Task OnGet(long? pedido)
    {
        var accounts = access.AccountIds(User, AccountCapabilities.Services);
        var placed = pedido is { } orderId ? await db.Orders.Where(o => o.Id == orderId && accounts.Contains(o.AccountId)).Select(o => (OrderStatus?)o.Status).SingleOrDefaultAsync() : null;
        Placed = placed is null ? null : pedido;
        PlacedPending = placed == OrderStatus.Pending;
        var billing = access.AccountIds(User, AccountCapabilities.Invoices);
        Rows = await (from s in db.Services.AsNoTracking()
                      join p in db.Products on s.ProductId equals p.Id
                      join a in db.Accounts on s.AccountId equals a.Id
                      where accounts.Contains(s.AccountId)
                      orderby s.AccountId, s.Id
                      select new Row(s, p.Name, a.Name, s.Status == ServiceStatus.Active && (p.OptionChanges || p.UpgradeProductIds.Length > 0) && billing.Contains(s.AccountId))).ToListAsync();
        ManyAccounts = Rows.Select(r => r.Service.AccountId).Distinct().Count() > 1;
        SupportAccounts = (await access.AccountIds(User, AccountCapabilities.Support).ToListAsync()).ToHashSet();
        Metered = (await db.Set<UsageMetric>().Where(m => m.Active).Select(m => m.ProductId).Distinct().ToListAsync()).ToHashSet();
        Issues = await knownIssues.For(User, CultureInfo.CurrentUICulture.Name, HttpContext.RequestAborted);
    }
}
