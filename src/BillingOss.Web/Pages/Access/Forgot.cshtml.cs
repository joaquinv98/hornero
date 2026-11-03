using System.Globalization;
using BillingOss.App;
using BillingOss.App.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Access;

/// <summary>
/// Restablecer la contraseña por correo. La respuesta es la misma exista o no el correo, y aunque esté bloqueado. El enlace se arma con
/// Billing:PublicUrl y nunca con el Host del pedido, que lo puede elegir cualquiera.
/// </summary>
public sealed class ForgotModel(UserManager<PortalUser> users, IAccountMail mail, AccessGuard guard, IOptions<BillingOptions> options, ILogger<ForgotModel> logger, Identity.ChallengeGuard challenge) : PageModel
{
    [BindProperty]
    public string Email { get; set; } = string.Empty;

    public bool Sent { get; private set; }

    public string? Error { get; private set; }

    /// <summary>El desafío anti robots, si recuperar la contraseña lo pide (diseno/ingreso-clientes.md, sección 7).</summary>
    public Identity.ChallengeView? AntiBot { get; private set; }

    public async Task OnGet() => AntiBot = await challenge.For(HttpContext, "forgot");

    public async Task<IActionResult> OnPost()
    {
        if (!await challenge.Verify(HttpContext, "forgot"))
        {
            await OnGet();
            Error = "Challenge.Failed";
            return Page();
        }

        Sent = true;
        var user = string.IsNullOrWhiteSpace(Email) ? null : await users.FindByEmailAsync(Email.Trim());
        // Un correo o un dominio de la lista de bloqueo no recibe nada, con la misma respuesta.
        if (user is null || user.DisabledAt is not null || await guard.IsBlocked(user.Email, HttpContext.RequestAborted))
        {
            return Page();
        }

        if (options.Value.PublicUrl is not { Length: > 0 } publicUrl)
        {
            logger.LogError("Falta Billing:PublicUrl: no se puede armar el enlace para restablecer la contraseña.");
            return Page();
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var link = new Uri(string.Create(CultureInfo.InvariantCulture, $"{publicUrl.TrimEnd('/')}/recuperar/nueva?u={user.Id}&t={Uri.EscapeDataString(token)}"));
        await mail.SendPasswordReset(user.Email, user.Language, link, HttpContext.RequestAborted);
        return Page();
    }
}
