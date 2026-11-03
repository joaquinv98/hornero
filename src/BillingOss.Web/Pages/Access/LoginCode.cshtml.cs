using BillingOss.App.Identity;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Access;

public sealed class LoginCodeModel(UserManager<PortalUser> users, AccessGuard guard) : PageModel
{
    [BindProperty]
    public string Code { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    /// <summary>Los minutos del bloqueo por intentos, para el mensaje.</summary>
    public int LockMinutes { get; private set; }

    public async Task<IActionResult> OnGet() =>
        (await Sessions.PendingUser(HttpContext, Schemes.PortalPending, users)).User is null ? Redirect("/ingresar") : Page();

    public async Task<IActionResult> OnPost()
    {
        var (user, persistent) = await Sessions.PendingUser(HttpContext, Schemes.PortalPending, users);
        if (user is null)
        {
            return Redirect("/ingresar");
        }

        switch (await Sessions.CheckSecondFactor(users, LoginAttempt.From(HttpContext, guard, LoginAudience.Portal), user, Ui.PortalTotp, Code))
        {
            case LoginOutcome.Success:
                await Sessions.SignInPortal(HttpContext, user, persistent);
                return LocalRedirect(Ui.SafeReturn(ReturnUrl));
            case LoginOutcome.LockedOut:
                LockMinutes = (await guard.Settings()).AccountMinutes;
                Error = "Access.LockedOut";
                return Page();
            case LoginOutcome.IpBlocked:
                Error = "Access.IpBlocked";
                return Page();
            default:
                Error = "Code.Failed";
                return Page();
        }
    }
}
