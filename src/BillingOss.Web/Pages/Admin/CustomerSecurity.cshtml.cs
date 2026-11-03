using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Orders;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin;

/// <summary>
/// Seguridad de los usuarios de una cuenta y evaluaciones de fraude de sus pedidos (diseno/fraude-y-acceso.md, sección
/// 11). Con security.manage se le quita el segundo factor a quien perdió el teléfono y los códigos.
/// </summary>
[Authorize(Policy = StaffPermissions.AccountsRead)]
public sealed class CustomerSecurityModel(BillingDbContext db, UserManager<PortalUser> users, AuditLog audit) : PageModel
{
    public sealed record UserRow(long Id, string Email, bool IsOwner, string[] Capabilities, bool EmailConfirmed, bool TwoFactor, bool Required, bool LockedOut, DateTimeOffset? LastLogin);

    public sealed record LoginRow(LoginEvent Event, string Email);

    public Account? Account { get; private set; }

    public IReadOnlyList<UserRow> Users { get; private set; } = [];

    public IReadOnlyList<LoginRow> Logins { get; private set; } = [];

    public IReadOnlyList<FraudCheck> Checks { get; private set; } = [];

    /// <summary>Los proveedores externos vinculados de cada usuario (diseno/ingreso-clientes.md, sección 5).</summary>
    public ILookup<long, (long Id, string Provider, string? Email)> Links { get; private set; } =
        Array.Empty<(long User, long Id, string Provider, string? Email)>().ToLookup(x => x.User, x => (x.Id, x.Provider, x.Email));

    public bool CanManage => User.HasClaim(Claims.Permission, StaffPermissions.SecurityManage);

    public bool CanRead => User.HasClaim(Claims.Permission, StaffPermissions.SecurityRead);

    public bool Reset { get; private set; }

    /// <summary>Clave del aviso de la última acción sobre el segundo factor obligatorio.</summary>
    public string? Notice { get; private set; }

    public async Task<IActionResult> OnGet(long id)
    {
        Account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id);
        if (Account is null)
        {
            return NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        var members = await (from m in db.Set<AccountUser>().AsNoTracking()
                             join u in db.Set<PortalUser>() on m.UserId equals u.Id
                             where m.AccountId == id
                             orderby m.IsOwner descending, u.Email
                             select new { u.Id, u.Email, m.IsOwner, m.Capabilities, u.EmailConfirmed, u.TwoFactorEnabled, u.TwoFactorRequired, u.LockoutEnd }).ToListAsync();
        var ids = members.Select(m => m.Id).ToList();
        var last = await db.Set<LoginEvent>().AsNoTracking()
            .Where(e => e.Audience == LoginAudience.Portal && e.Result == LoginResult.Success && ids.Contains(e.UserId!.Value))
            .GroupBy(e => e.UserId).Select(g => new { UserId = g.Key!.Value, At = g.Max(e => e.CreatedAt) }).ToDictionaryAsync(x => x.UserId, x => x.At);
        Users = [.. members.Select(m => new UserRow(m.Id, m.Email, m.IsOwner, m.Capabilities, m.EmailConfirmed, m.TwoFactorEnabled, m.TwoFactorRequired, m.LockoutEnd > now, last.TryGetValue(m.Id, out var at) ? at : null))];
        Links = (await (from l in db.Set<ExternalLogin>().AsNoTracking()
                        join p in db.Set<ExternalProvider>().AsNoTracking() on l.ProviderId equals p.Id
                        where ids.Contains(l.UserId)
                        orderby l.Id
                        select new { l.UserId, l.Id, p.Name, l.Email }).ToListAsync()).ToLookup(x => x.UserId, x => (x.Id, x.Name, (string?)x.Email));
        if (CanRead)
        {
            var emails = members.ToDictionary(m => m.Id, m => m.Email);
            Logins = [.. (await db.Set<LoginEvent>().AsNoTracking()
                    .Where(e => e.Audience == LoginAudience.Portal && ids.Contains(e.UserId!.Value))
                    .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Take(50).ToListAsync())
                .Select(e => new LoginRow(e, emails[e.UserId!.Value]))];
        }

        Checks = await db.Set<FraudCheck>().AsNoTracking().Where(f => f.AccountId == id).OrderByDescending(f => f.Id).Take(50).ToListAsync();
        return Page();
    }

    /// <summary>
    /// Le quita el segundo factor a un usuario de la cuenta: borra la semilla y los códigos y renueva el sello, así corta
    /// sus sesiones. Si lo tiene obligatorio, lo vuelve a enrolar en el próximo ingreso.
    /// </summary>
    public async Task<IActionResult> OnPostResetTwoFactor(long id, long userId)
    {
        if (!CanManage)
        {
            // Con el esquema del staff: sin esquema por defecto, Forbid() a secas da un 500.
            return Forbid(Schemes.Staff);
        }

        if (!await db.Set<AccountUser>().AnyAsync(m => m.AccountId == id && m.UserId == userId)
            || await users.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture)) is not { } user)
        {
            return NotFound();
        }

        user.AuthenticatorKey = null;
        user.RecoveryCodes = null;
        await users.SetTwoFactorEnabledAsync(user, false);
        await audit.Record("security.2fa_reset", string.Create(CultureInfo.InvariantCulture, $"user:{userId}"), new { account = id });
        Reset = true;
        return await OnGet(id);
    }

    /// <summary>
    /// Le saca a un usuario de la cuenta un vínculo con un proveedor externo, por ejemplo si perdió esa cuenta o alguien
    /// la usa sin su permiso. Sigue entrando con su contraseña o con el enlace para elegir una.
    /// </summary>
    public async Task<IActionResult> OnPostUnlink(long id, long userId, long linkId, [FromServices] ExternalLoginStore external)
    {
        ArgumentNullException.ThrowIfNull(external);
        if (!CanManage)
        {
            return Forbid(Schemes.Staff);
        }

        if (!await db.Set<AccountUser>().AnyAsync(m => m.AccountId == id && m.UserId == userId) || !await external.Remove(linkId, userId, "security.external_unlinked"))
        {
            return NotFound();
        }

        Notice = "External.Unlinked";
        return await OnGet(id);
    }

    /// <summary>
    /// Le exige el segundo factor a un usuario de la cuenta, o le deja de exigir el que le puso el staff o el sistema
    /// anterior. Exigido, no ve otra página del portal que la de seguridad hasta activarlo (sección 8). Los roles de los
    /// ajustes valen igual.
    /// </summary>
    public async Task<IActionResult> OnPostRequireTwoFactor(long id, long userId, bool required)
    {
        if (!CanManage)
        {
            return Forbid(Schemes.Staff);
        }

        if (!await db.Set<AccountUser>().AnyAsync(m => m.AccountId == id && m.UserId == userId)
            || await users.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture)) is not { } user)
        {
            return NotFound();
        }

        user.TwoFactorRequired = required;
        await users.UpdateAsync(user);
        await audit.Record("security.2fa_required", string.Create(CultureInfo.InvariantCulture, $"user:{userId}"), new { account = id, required });
        Notice = required ? "Customer.RequiredOn" : "Customer.RequiredOff";
        return await OnGet(id);
    }
}
