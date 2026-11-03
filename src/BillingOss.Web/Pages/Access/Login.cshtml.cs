using BillingOss.App.Identity;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Access;

public sealed class LoginModel(UserManager<PortalUser> users, AccessGuard guard, ExternalLoginStore external, ExternalSignIn signIn, ChallengeGuard challenge) : PageModel
{
    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public bool Remember { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    /// <summary>Los minutos del bloqueo por intentos, para el mensaje.</summary>
    public int LockMinutes { get; private set; }

    /// <summary>Los proveedores externos prendidos, con su botón (diseno/ingreso-clientes.md, sección 4).</summary>
    public IReadOnlyList<ExternalProvider> Providers { get; private set; } = [];

    public bool RegistrationOpen { get; private set; }

    /// <summary>El desafío anti robots, si el ingreso lo pide (diseno/ingreso-clientes.md, sección 7).</summary>
    public ChallengeView? AntiBot { get; private set; }

    public async Task OnGet()
    {
        Providers = await external.Enabled();
        RegistrationOpen = (await guard.Settings()).RegistrationEnabled;
        AntiBot = await challenge.For(HttpContext, "login");
    }

    public async Task<IActionResult> OnPost()
    {
        if (!await challenge.Verify(HttpContext, "login"))
        {
            await OnGet();
            Error = "Challenge.Failed";
            return Page();
        }

        var attempt = LoginAttempt.From(HttpContext, guard, LoginAudience.Portal);
        var (outcome, user) = await Sessions.CheckPassword(users, attempt, Email, Password);
        if (user is null)
        {
            await OnGet();
            LockMinutes = (await guard.Settings()).AccountMinutes;
            Error = outcome switch
            {
                LoginOutcome.LockedOut => "Access.LockedOut",
                LoginOutcome.IpBlocked => "Access.IpBlocked",
                _ => "Login.Failed",
            };
            return Page();
        }

        if (user.TwoFactorEnabled)
        {
            await Sessions.BeginSecondFactor(HttpContext, Schemes.PortalPending, user, Remember);
            return Redirect("/ingresar/codigo?ReturnUrl=" + Uri.EscapeDataString(Ui.SafeReturn(ReturnUrl)));
        }

        await users.ResetAccessFailedCountAsync(user);
        await Sessions.Succeeded(attempt, user, LoginMethod.Password);
        await Sessions.SignInPortal(HttpContext, user, Remember);
        return LocalRedirect(await guard.TwoFactorPending(user.Id, HttpContext.RequestAborted) ? TwoFactorEnrolled.SecurityPath : Ui.SafeReturn(ReturnUrl));
    }

    /// <summary>Ir al proveedor externo. La IP bloqueada no sale: el mismo freno que el ingreso con contraseña.</summary>
    public async Task<IActionResult> OnPostExternal(string provider)
    {
        if (await guard.IsBlocked(HttpContext.Connection.RemoteIpAddress, HttpContext.RequestAborted))
        {
            await OnGet();
            Error = "Access.IpBlocked";
            return Page();
        }

        if (await signIn.Challenge(HttpContext, provider ?? string.Empty, ExternalPurpose.Login, ReturnUrl, null) is { } url)
        {
            return Redirect(url);
        }

        await OnGet();
        Error = "External.Unavailable";
        return Page();
    }
}
