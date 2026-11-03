using System.Globalization;
using System.Security.Claims;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Access;

/// <summary>
/// La vuelta del proveedor (diseno/ingreso-clientes.md, sección 4.2). Valida todo y deja lo que devolvió en la cookie
/// externa del portal. No decide nada: sigue a /ingresar/externo con una navegación desde el propio sitio, porque esta
/// llega desde el proveedor y las cookies Strict del paso del segundo factor no viajarían en esa cadena.
/// </summary>
public sealed class ExternalCallbackModel(ExternalSignIn signIn) : PageModel
{
    public string? Error { get; private set; }

    public string Back { get; private set; } = "/ingresar";

    public async Task<IActionResult> OnGet(string provider, string? code, string? state, string? error)
    {
        var (identity, trip, problem) = await signIn.Callback(HttpContext, provider, code, state, error);
        if (trip is { Purpose: ExternalPurpose.Link })
        {
            Back = "/cuenta/seguridad";
        }

        if (identity is null || trip is null)
        {
            Error = problem ?? "External.Failed";
            return Page();
        }

        var claims = new List<Claim>
        {
            new("provider", identity.Provider),
            new("provider_id", identity.ProviderId.ToString(CultureInfo.InvariantCulture)),
            new("provider_name", identity.ProviderName),
            new("sub", identity.Subject),
            new("email_verified", identity.EmailVerified ? "true" : "false"),
        };
        if (identity.Email is { } email)
        {
            claims.Add(new("email", email));
        }

        if (identity.Name is { Length: > 0 } name)
        {
            claims.Add(new("name", name));
        }

        var properties = new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow + ExternalSignIn.TripLifetime };
        properties.Items["purpose"] = trip.Purpose;
        properties.Items["return"] = trip.ReturnUrl;
        if (trip.UserId is { } user)
        {
            properties.Items["user"] = user.ToString(CultureInfo.InvariantCulture);
        }

        await HttpContext.SignInAsync(Schemes.PortalExternal, new ClaimsPrincipal(new ClaimsIdentity(claims, Schemes.PortalExternal)), properties);
        Response.Headers["Refresh"] = "0;url=/ingresar/externo";
        return Page();
    }
}
