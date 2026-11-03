using System.Globalization;
using System.Threading.RateLimiting;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Affiliates;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web;

/// <summary>
/// La cookie del enlace de afiliado (sección 3 de diseno/afiliados.md): el afiliado y la hora del clic, protegidos con
/// Data Protection, que vence con la ventana del programa.
/// </summary>
public static class AffiliateCookie
{
    public const string Name = "bo_ref";

    private const string Purpose = "BillingOss.Affiliates.Referral";

    public static void Write(HttpContext http, long affiliateId, DateTimeOffset clickedAt, int days)
    {
        ArgumentNullException.ThrowIfNull(http);
        var value = string.Create(CultureInfo.InvariantCulture, $"{affiliateId}:{clickedAt.ToUnixTimeSeconds()}");
        http.Response.Cookies.Append(Name, Protector(http).Protect(value), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromDays(days),
            IsEssential = true,
        });
    }

    /// <summary>El afiliado y la hora del clic, o null si no hay cookie o no se puede leer.</summary>
    public static (long AffiliateId, DateTimeOffset ClickedAt)? Read(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.Request.Cookies[Name] is not { Length: > 0 } raw)
        {
            return null;
        }

        try
        {
            var parts = Protector(http).Unprotect(raw).Split(':');
            return parts.Length == 2 && long.TryParse(parts[0], CultureInfo.InvariantCulture, out var id) && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var seconds)
                ? (id, DateTimeOffset.FromUnixTimeSeconds(seconds))
                : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public static void Delete(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Delete(Name, new CookieOptions { Path = "/", Secure = true, SameSite = SameSiteMode.Lax });
    }

    /// <summary>
    /// Atribuye una cuenta con la cookie del pedido. Lo llama el pedido de una página cuando crea la cuenta y confirma en
    /// el mismo paso, antes de confirmar. Devuelve si la cuenta quedó atribuida.
    /// </summary>
    public static async Task<bool> Attribute(HttpContext http, long accountId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        return Read(http) is { } referral
            && await http.RequestServices.GetRequiredService<AffiliateService>().Attribute(accountId, referral.AffiliateId, referral.ClickedAt, AttributionSource.Link, ct).ConfigureAwait(false);
    }

    private static IDataProtector Protector(HttpContext http) =>
        http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
}

/// <summary>El enlace /r/{código}, su tope por IP y la atribución de las cuentas del cliente que entra con la cookie.</summary>
public static partial class AffiliateWeb
{
    public const string LinkLimiter = "affiliate-link";

    /// <summary>
    /// Un importe o un porcentaje escrito a mano: coma o punto decimal y sin separador de miles, porque "1.500" sería
    /// ambiguo entre mil quinientos y uno y medio. Null si no tiene esa forma o pasa los decimales.
    /// </summary>
    public static decimal? ParseDecimal(string? text, int decimals)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        return DecimalPattern().Match(trimmed) is { Success: true } m && m.Groups[2].Length <= decimals
            ? decimal.Parse(trimmed.Replace(',', '.'), CultureInfo.InvariantCulture)
            : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{1,12}([.,](\d{1,4}))?$")]
    private static partial System.Text.RegularExpressions.Regex DecimalPattern();

    public static void AddAffiliates(this IServiceCollection services) =>
        services.AddRateLimiter(o => o.AddPolicy(LinkLimiter, http => RateLimitPartition.GetFixedWindowLimiter(
            "ip:" + http.Connection.RemoteIpAddress,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) })));

    public static void MapAffiliates(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // El cliente que entra al portal con la cookie: sus cuentas de titular quedan atribuidas y la cookie se borra.
        // "Ver como cliente" no atribuye nada.
        app.Use(async (http, next) =>
        {
            if (http.Request.Cookies.ContainsKey(AffiliateCookie.Name) && !http.Request.Path.StartsWithSegments("/r"))
            {
                await AttributeOwnedAccounts(http).ConfigureAwait(false);
            }

            await next(http).ConfigureAwait(false);
        });

        // Haya o no afiliado, la misma redirección: un código inventado no se distingue de uno real.
        app.MapGet("/r/{code}", async (string code, string? ir, HttpContext http, AffiliateService affiliates, IConfiguration config, TimeProvider time, CancellationToken ct) =>
        {
            var current = AffiliateCookie.Read(http);
            var hit = await affiliates.Click(code, ReferrerHost(http), current?.AffiliateId, ct).ConfigureAwait(false);
            if (hit is { } h)
            {
                AffiliateCookie.Write(http, h.AffiliateId, time.GetUtcNow(), h.CookieDays);
            }

            http.Response.Headers.CacheControl = "no-store";
            return Results.Redirect(ir is { Length: > 0 } ? Ui.SafeReturn(ir) : config["Affiliates:LandingUrl"] is { Length: > 0 } landing ? landing : "/");
        }).AllowAnonymous().RequireRateLimiting(LinkLimiter);
    }

    /// <summary>
    /// El ítem Afiliados del portal (10.3 de interfaz-y-temas.md): con el programa prendido, o si alguna cuenta de la
    /// persona ya es afiliada y tiene saldo que mirar o cobrar. Una sola consulta.
    /// </summary>
    public static Task<bool> InPortalMenu(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var db = http.RequestServices.GetRequiredService<BillingDbContext>();
        var accounts = http.RequestServices.GetRequiredService<PortalAccess>().AccountIds(http.User, AccountCapabilities.Affiliate);
        return accounts.AnyAsync(id => db.Set<AffiliateProgram>().Any(p => p.Enabled) || db.Set<Affiliate>().Any(a => a.AccountId == id), http.RequestAborted);
    }

    private static async Task AttributeOwnedAccounts(HttpContext http)
    {
        var auth = await http.AuthenticateAsync(Schemes.Portal).ConfigureAwait(false);
        if (!auth.Succeeded || auth.Principal.ActingAccount() is not null || auth.Principal.UserId() is not { } userId)
        {
            return;
        }

        if (AffiliateCookie.Read(http) is { } referral)
        {
            var db = http.RequestServices.GetRequiredService<BillingDbContext>();
            var owned = await db.Set<AccountUser>().Where(m => m.UserId == userId && m.IsOwner).Select(m => m.AccountId).ToListAsync(http.RequestAborted).ConfigureAwait(false);
            var affiliates = http.RequestServices.GetRequiredService<AffiliateService>();
            foreach (var account in owned)
            {
                await affiliates.Attribute(account, referral.AffiliateId, referral.ClickedAt, AttributionSource.Link, http.RequestAborted).ConfigureAwait(false);
            }
        }

        AffiliateCookie.Delete(http);
    }

    /// <summary>Solo el host del sitio de origen, sin ruta ni consulta: alcanza para ver de dónde vienen los clics.</summary>
    private static string? ReferrerHost(HttpContext http) =>
        Uri.TryCreate(http.Request.Headers.Referer.ToString(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.Host.ToLowerInvariant() : null;
}
