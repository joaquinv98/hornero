using System.Security.Cryptography;
using System.Text;
using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.Domain.Content;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Content;

/// <summary>
/// Quién mira las páginas públicas de contenidos. Las páginas no piden sesión, pero si hay una del portal el cliente ve además
/// lo que le corresponde por sus productos y grupos (sección 2.2 de diseno/contenidos.md). La regla de audiencia es una sola
/// (<see cref="ContentViewer"/>) y la usan las páginas, la API y la búsqueda.
/// </summary>
public sealed class ContentAccess(PortalAccess access, ContentViewers viewers)
{
    /// <summary>Cookie propia de una visita sin sesión, para que su voto cuente una vez por artículo.</summary>
    public const string VisitCookie = "bo_visit";

    /// <summary>
    /// Mira la sesión del portal sin exigirla. Si la hay, deja la identidad en <c>HttpContext.User</c> para que el menú la
    /// muestre. Una visita sin sesión, o con una vencida, es anónima.
    /// </summary>
    public async Task<ContentViewer> Viewer(HttpContext http, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        var result = await http.AuthenticateAsync(Schemes.Portal).ConfigureAwait(false);
        if (result is not { Succeeded: true, Principal: { } principal })
        {
            return ContentViewer.Anonymous;
        }

        http.User = principal;
        var accounts = await access.AccountIds(principal, AccountCapabilities.Services)
            .Union(access.AccountIds(principal, AccountCapabilities.Invoices))
            .ToListAsync(ct).ConfigureAwait(false);
        return await viewers.ForAccounts(accounts, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// El votante: "user:{id}" para un cliente con su sesión y "visit:{hash}" para una visita con su cookie, que se crea al votar.
    /// El staff mirando como cliente vota como visita: su voto no es el de la persona a la que está mirando.
    /// </summary>
    public static string? Voter(HttpContext http, bool create)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.User.Identity is { IsAuthenticated: true, AuthenticationType: Schemes.Portal } && http.User.ActingAccount() is null && http.User.UserId() is { } id)
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"user:{id}");
        }

        var cookie = http.Request.Cookies[VisitCookie];
        if (string.IsNullOrEmpty(cookie) || cookie.Length is < 16 or > 64)
        {
            if (!create)
            {
                return null;
            }

            cookie = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
            http.Response.Cookies.Append(VisitCookie, cookie, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(365), IsEssential = true });
        }

        return "visit:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cookie)))[..32];
    }
}
