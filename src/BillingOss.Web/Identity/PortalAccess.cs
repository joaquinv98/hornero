using System.Security.Claims;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;

namespace BillingOss.Web.Identity;

/// <summary>
/// El único lugar donde se decide qué cuentas ve una sesión del portal (sección 13 del modelo de dominio). Toda consulta
/// del portal se filtra con <see cref="AccountIds"/>, así que lo ajeno no aparece y da 404, igual que lo que no existe.
/// </summary>
public sealed class PortalAccess(BillingDbContext db)
{
    /// <summary>Cuentas que la sesión puede ver con esa capacidad, como subconsulta para componer.</summary>
    public IQueryable<long> AccountIds(ClaimsPrincipal user, string capability)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.ActingAccount() is { } acting)
        {
            return db.Accounts.Where(a => a.Id == acting).Select(a => a.Id);
        }

        var userId = user.UserId() ?? -1;
        return db.Set<AccountUser>()
            .Where(m => m.UserId == userId && (m.IsOwner || m.Capabilities.Contains(capability)))
            .Select(m => m.AccountId);
    }
}
