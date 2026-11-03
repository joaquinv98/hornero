using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Access;

/// <summary>
/// "Crear cuenta" (diseno/ingreso-clientes.md, sección 2): con correo y contraseña o con un proveedor externo. Crea la
/// cuenta y su usuario titular, manda el enlace para verificar el correo, entra y sigue a los datos fiscales y de ahí al
/// pedido que la trajo.
/// </summary>
public sealed class RegisterModel(PortalRegistration registration, AccessGuard guard, ExternalLoginStore external, ExternalSignIn signIn, ChallengeGuard challenge) : PageModel
{
    [BindProperty]
    public string Name { get; set; } = string.Empty;

    [BindProperty]
    public string? Company { get; set; }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string? Phone { get; set; }

    [BindProperty]
    public string Currency { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string Confirm { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public bool Open { get; private set; }

    public int MinLength { get; private set; }

    public IReadOnlyList<string> Currencies { get; private set; } = [];

    public IReadOnlyList<ExternalProvider> Providers { get; private set; } = [];

    public string? Error { get; private set; }

    public IReadOnlyList<string> Problems { get; private set; } = [];

    /// <summary>El desafío anti robots, si el registro lo pide (diseno/ingreso-clientes.md, sección 7).</summary>
    public ChallengeView? AntiBot { get; private set; }

    public async Task<IActionResult> OnGet()
    {
        // Las páginas de acceso no leen la sesión solas: con una abierta, el registro no tiene nada que hacer.
        if ((await HttpContext.AuthenticateAsync(Schemes.Portal)).Succeeded)
        {
            return LocalRedirect(Ui.SafeReturn(ReturnUrl));
        }

        await Load();
        return Page();
    }

    public async Task<IActionResult> OnPost()
    {
        await Load();
        if (!await challenge.Verify(HttpContext, "register"))
        {
            Error = "Challenge.Failed";
            return Page();
        }

        if (Password != Confirm)
        {
            Error = "Reset.Mismatch";
            return Page();
        }

        var ip = HttpContext.Connection.RemoteIpAddress;
        var input = new RegistrationInput(Name, Company, Phone, Email, Password, Currency, Language());
        var result = await registration.Register(input, ip, null, HttpContext.RequestAborted);
        if (result.User is not { } user)
        {
            Error = result.Error;
            Problems = result.Problems;
            return Page();
        }

        // El alta es el primer ingreso: queda en el historial y no avisa de un lugar nuevo.
        await Sessions.Succeeded(LoginAttempt.From(HttpContext, guard, LoginAudience.Portal), user, LoginMethod.Password);
        await Sessions.SignInPortal(HttpContext, user, persistent: false);
        return LocalRedirect(NextStep(ReturnUrl));
    }

    public async Task<IActionResult> OnPostExternal(string provider)
    {
        await Load();
        if (!Open)
        {
            return Page();
        }

        if (await guard.IsBlocked(HttpContext.Connection.RemoteIpAddress, HttpContext.RequestAborted))
        {
            Error = "Register.IpBlocked";
            return Page();
        }

        if (await signIn.Challenge(HttpContext, provider ?? string.Empty, ExternalPurpose.Login, ReturnUrl, null) is { } url)
        {
            return Redirect(url);
        }

        Error = "External.Unavailable";
        return Page();
    }

    /// <summary>Después del alta: los datos fiscales de la cuenta nueva, y de ahí al pedido.</summary>
    public static string NextStep(string? returnUrl) => "/cuenta/datos-fiscales?nuevo=1&volver=" + Uri.EscapeDataString(Ui.SafeReturn(returnUrl));

    /// <summary>El idioma de la interfaz en la que se registró: "es" o "en".</summary>
    public static string Language() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? "en" : "es";

    private async Task Load()
    {
        var settings = await guard.Settings();
        Open = settings.RegistrationEnabled;
        MinLength = settings.MinLength;
        Currencies = await registration.Currencies(settings, HttpContext.RequestAborted);
        if (Currency.Length == 0)
        {
            Currency = Currencies[0];
        }

        Providers = Open ? await external.Enabled() : [];
        AntiBot = Open ? await challenge.For(HttpContext, "register") : null;
    }
}
