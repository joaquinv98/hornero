using System.Security.Claims;
using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.Web.Identity;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Content;

/// <summary>
/// Los incidentes y mantenimientos conocidos que tocan los productos que un cliente tiene contratados (sección 6.6 de
/// diseno/contenidos.md). El portal los muestra en la lista de servicios y en el inicio. Cuenta lo mismo que la audiencia: los
/// productos de los servicios vivos de las cuentas que ve la sesión.
/// </summary>
public sealed class KnownIssues(PortalAccess access, ContentViewers viewers, StatusService status)
{
    public async Task<IReadOnlyList<EventView>> For(ClaimsPrincipal user, string? language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var accounts = await access.AccountIds(user, AccountCapabilities.Services).ToListAsync(ct).ConfigureAwait(false);
        if (accounts.Count == 0)
        {
            return [];
        }

        var viewer = await viewers.ForAccounts(accounts, ct).ConfigureAwait(false);
        return await status.IssuesForProducts(viewer.ProductIds, language, ct).ConfigureAwait(false);
    }
}
