using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web.Pages.Admin.Catalog;

/// <summary>Alta y edición de un cupón: descuento, dónde vale, cuándo, cuántas veces y para quién.</summary>
[Authorize(Policy = StaffPermissions.CatalogRead)]
public sealed class CouponModel(BillingDbContext db, CatalogService catalog, IStringLocalizer<Texts> l) : PageModel
{
    public sealed class Form
    {
        public string? Code { get; set; }

        public DiscountKind Kind { get; set; }

        public string? Value { get; set; }

        public string? Currency { get; set; }

        public bool Recurring { get; set; }

        public int? RecurringCycles { get; set; }

        public List<long> ProductIds { get; set; } = [];

        public List<string> Cycles { get; set; } = [];

        public DateOnly? StartsOn { get; set; }

        public DateOnly? ExpiresOn { get; set; }

        public int? MaxUses { get; set; }

        public bool OncePerAccount { get; set; }

        public bool NewAccountsOnly { get; set; }

        public bool ExistingAccountsOnly { get; set; }

        public bool OncePerOrder { get; set; }

        public bool Active { get; set; } = true;

        public string? Notes { get; set; }
    }

    [BindProperty]
    public Form Input { get; set; } = new();

    public long? CouponId { get; private set; }

    public int Uses { get; private set; }

    public string? Error { get; private set; }

    public bool Saved { get; private set; }

    public bool CanWrite => this.HasCatalogWrite();

    public IReadOnlyList<ProductChoice> Products { get; private set; } = [];

    public async Task<IActionResult> OnGet(string id, bool guardado = false)
    {
        if (!Ids.TryParse(id, out var couponId))
        {
            return NotFound();
        }

        if (couponId is { } existing)
        {
            if (await db.Coupons.AsNoTracking().SingleOrDefaultAsync(c => c.Id == existing) is not { } c)
            {
                return NotFound();
            }

            Uses = c.Uses;
            Input = new Form
            {
                Code = c.Code,
                Kind = c.Kind,
                Value = CatalogForm.Format(c.Value),
                Currency = c.Currency?.Code,
                Recurring = c.Recurring,
                RecurringCycles = c.RecurringCycles,
                ProductIds = [.. c.ProductIds],
                Cycles = [.. c.Cycles],
                StartsOn = c.StartsOn,
                ExpiresOn = c.ExpiresOn,
                MaxUses = c.MaxUses,
                OncePerAccount = c.OncePerAccount,
                NewAccountsOnly = c.NewAccountsOnly,
                ExistingAccountsOnly = c.ExistingAccountsOnly,
                OncePerOrder = c.OncePerOrder,
                Active = c.Active,
                Notes = c.Notes,
            };
        }

        CouponId = couponId;
        Saved = guardado;
        await Load();
        return Page();
    }

    public async Task<IActionResult> OnPost(string id)
    {
        if (!Ids.TryParse(id, out var couponId))
        {
            return NotFound();
        }

        if (!CanWrite)
        {
            return Forbid();
        }

        CouponId = couponId;
        if (ModelState.IsValid)
        {
            try
            {
                var f = Input;
                var saved = await catalog.SaveCoupon(couponId, new CouponInput(
                    f.Code ?? string.Empty,
                    f.Kind,
                    CatalogForm.Amount(f.Value, l["Catalog.Discount"]),
                    f.Currency,
                    f.Recurring,
                    f.RecurringCycles,
                    f.ProductIds,
                    f.Cycles,
                    f.StartsOn,
                    f.ExpiresOn,
                    f.MaxUses,
                    f.OncePerAccount,
                    f.NewAccountsOnly,
                    f.ExistingAccountsOnly,
                    f.OncePerOrder,
                    f.Active,
                    f.Notes), HttpContext.RequestAborted);
                return Redirect(string.Create(CultureInfo.InvariantCulture, $"/admin/catalogo/cupones/{saved}?guardado=true"));
            }
            catch (ValidationException ex)
            {
                Error = ex.Message;
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
        }
        else
        {
            Error = l["Catalog.InvalidFields"];
        }

        if (couponId is { } existing)
        {
            Uses = await db.Coupons.Where(c => c.Id == existing).Select(c => c.Uses).SingleOrDefaultAsync();
        }

        await Load();
        return Page();
    }

    private async Task Load()
    {
        var groups = await db.ProductGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name);
        Products = [.. (await db.Products.AsNoTracking().OrderBy(p => p.GroupId).ThenBy(p => p.SortOrder).ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.Name, p.GroupId }).ToListAsync())
            .Select(p => new ProductChoice(p.Id, p.Name, p.GroupId is { } g ? groups.GetValueOrDefault(g) : null))];
    }
}
