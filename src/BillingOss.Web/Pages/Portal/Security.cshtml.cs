using System.Globalization;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Portal;

/// <summary>
/// Seguridad del cliente: correo verificado, contraseña, segundo factor con sus códigos de recuperación y últimos
/// ingresos (diseno/fraude-y-acceso.md, secciones 5 a 8), y los proveedores externos vinculados
/// (diseno/ingreso-clientes.md, sección 5). El segundo factor es opcional, salvo que lo tenga obligatorio por su rol,
/// porque lo tenía en el sistema anterior o porque se lo exigió el staff. Obligatorio, hasta activarlo la sesión no ve otra
/// cosa (lo corta la política del portal) y después no se puede desactivar, ni con un pedido armado a mano.
/// </summary>
public sealed class SecurityModel(
    UserManager<PortalUser> users,
    IUserStore<PortalUser> store,
    AccessGuard guard,
    IAccountMail mail,
    AuditLog audit,
    IConfiguration config,
    IOptions<BillingOptions> options,
    ExternalLoginStore external,
    ExternalSignIn signIn) : PageModel
{
    /// <summary>Con 2 o menos códigos de recuperación se avisa que conviene generar otros.</summary>
    public const int FewCodes = 2;

    [BindProperty]
    public string Code { get; set; } = string.Empty;

    [BindProperty]
    public string CurrentPassword { get; set; } = string.Empty;

    [BindProperty]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    public bool Enabled { get; private set; }

    public bool Required { get; private set; }

    /// <summary>Obligatorio porque lo tenía en el sistema anterior, no por su rol: cambia el texto que lo explica.</summary>
    public bool Imported { get; private set; }

    public TotpSetup? Setup { get; private set; }

    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    public int CodesLeft { get; private set; }

    public string Email { get; private set; } = string.Empty;

    public bool EmailConfirmed { get; private set; }

    public int MinLength { get; private set; }

    public IReadOnlyList<string> Problems { get; private set; } = [];

    public IReadOnlyList<LoginEvent> Logins { get; private set; } = [];

    /// <summary>Los proveedores externos vinculados (diseno/ingreso-clientes.md, sección 5).</summary>
    public IReadOnlyList<(ExternalLogin Link, string Provider)> Links { get; private set; } = [];

    /// <summary>Los proveedores prendidos, para vincular otro.</summary>
    public IReadOnlyList<ExternalProvider> Providers { get; private set; } = [];

    /// <summary>La zona del proveedor, para las fechas.</summary>
    public string TimeZone => options.Value.TimeZone;

    /// <summary>Quien se registró con un proveedor no tiene contraseña: la elige con el enlace de recuperar.</summary>
    public bool HasPassword { get; private set; }

    public string? Error { get; private set; }

    public string? Notice { get; private set; }

    public async Task<IActionResult> OnGet()
    {
        var user = await Current();
        if (Request.Query.ContainsKey("vinculado"))
        {
            Notice = "External.LinkDone";
        }

        return user is null ? NotFound() : await Show(user);
    }

    /// <summary>Va al proveedor para vincularlo a este usuario. A la vuelta se confirma con la contraseña o el correo.</summary>
    public async Task<IActionResult> OnPostLink(string provider)
    {
        var user = await Current();
        if (user is null)
        {
            return NotFound();
        }

        if (await signIn.Challenge(HttpContext, provider ?? string.Empty, ExternalPurpose.Link, TwoFactorEnrolled.SecurityPath, user.Id) is { } url)
        {
            return Redirect(url);
        }

        Error = "External.Unavailable";
        return await Show(user);
    }

    /// <summary>Saca un vínculo propio. Sin contraseña no se saca el último: sería quedarse sin forma de entrar.</summary>
    public async Task<IActionResult> OnPostUnlink(long id)
    {
        var user = await Current();
        if (user is null)
        {
            return NotFound();
        }

        var links = await external.Links(user.Id);
        if (user.PasswordHash is null && links.Count <= 1 && links.Any(l => l.Link.Id == id))
        {
            Error = "External.LastMethod";
            return await Show(user);
        }

        Notice = await external.Remove(id, user.Id, "portal.external_unlinked") ? "External.Unlinked" : null;
        return await Show(user);
    }

    public async Task<IActionResult> OnPostEnable()
    {
        var user = await Current();
        if (user is null)
        {
            return NotFound();
        }

        RecoveryCodes = await TotpSetup.Enable(users, user, Ui.PortalTotp, Code);
        if (RecoveryCodes is null)
        {
            Error = "Code.Failed";
            return await Show(user);
        }

        // Activarlo cambia el sello: se renueva esta sesión y se cortan las demás.
        await audit.Record("portal.2fa_enabled");
        await Sessions.SignInPortal(HttpContext, user, await Persistent());
        return await Show(user);
    }

    public async Task<IActionResult> OnPostDisable()
    {
        var user = await Current();
        if (user is null)
        {
            return NotFound();
        }

        if (await guard.TwoFactorRequired(user.Id))
        {
            Error = user.TwoFactorRequired ? "Security.CannotDisable" : "TwoFa.CannotDisable";
            return await Show(user);
        }

        if (!await TotpSetup.Confirm(users, user, Ui.PortalTotp, Code, await guard.Settings()))
        {
            Error = "Code.Failed";
            return await Show(user);
        }

        user.AuthenticatorKey = null;
        user.RecoveryCodes = null;
        await users.SetTwoFactorEnabledAsync(user, false);
        await audit.Record("portal.2fa_disabled");
        await Sessions.SignInPortal(HttpContext, user, await Persistent());
        return await Show(user);
    }

    /// <summary>Ocho códigos de recuperación nuevos, con un código de la app: los anteriores dejan de valer.</summary>
    public async Task<IActionResult> OnPostCodes()
    {
        var user = await Current();
        if (user is null)
        {
            return NotFound();
        }

        RecoveryCodes = await TotpSetup.NewRecoveryCodes(users, user, Ui.PortalTotp, Code, await guard.Settings());
        if (RecoveryCodes is null)
        {
            Error = "Code.Failed";
        }
        else
        {
            await audit.Record("portal.recovery_codes");
        }

        return await Show(user);
    }

    /// <summary>Cambio de contraseña con la actual. Renueva el sello: corta las otras sesiones y renueva esta.</summary>
    public async Task<IActionResult> OnPostPassword()
    {
        var user = await Current();
        if (user is null)
        {
            return NotFound();
        }

        if (NewPassword != ConfirmPassword)
        {
            Error = "Reset.Mismatch";
            return await Show(user);
        }

        var result = await users.ChangePasswordAsync(user, CurrentPassword, NewPassword);
        if (!result.Succeeded)
        {
            Problems = [.. result.Errors.Select(e => e.Code).Where(c => c != "PasswordMismatch" && c.StartsWith("Password", StringComparison.Ordinal)).Distinct()];
            Error = result.Errors.Any(e => e.Code == "PasswordMismatch") ? "Pwd.WrongCurrent" : "Pwd.Rejected";
            return await Show(user);
        }

        await audit.Record("portal.password_changed");
        await Sessions.SignInPortal(HttpContext, user, await Persistent());
        Notice = "Pwd.Changed";
        return await Show(user);
    }

    /// <summary>Manda el enlace para verificar el correo. Se arma con Billing:PublicUrl, nunca con el Host del pedido.</summary>
    public async Task<IActionResult> OnPostVerify()
    {
        var user = await Current();
        if (user is null)
        {
            return NotFound();
        }

        if (!user.EmailConfirmed)
        {
            if (options.Value.PublicUrl is not { Length: > 0 } publicUrl)
            {
                Error = "Mail.NoPublicUrl";
                return await Show(user);
            }

            var token = await users.GenerateEmailConfirmationTokenAsync(user);
            var link = new Uri(string.Create(CultureInfo.InvariantCulture, $"{publicUrl.TrimEnd('/')}/cuenta/verificar?U={user.Id}&T={Uri.EscapeDataString(token)}"));
            await mail.SendEmailVerification(user.Email, user.Language, link, HttpContext.RequestAborted);
            Notice = "Mail.Sent";
        }

        return await Show(user);
    }

    /// <summary>Solo el propio usuario: en "ver como cliente" esta página no existe.</summary>
    private async Task<PortalUser?> Current() =>
        User.ActingAccount() is null && User.UserId() is { } id ? await users.FindByIdAsync(id.ToString(CultureInfo.InvariantCulture)) : null;

    private async Task<IActionResult> Show(PortalUser user)
    {
        Enabled = user.TwoFactorEnabled;
        Required = await guard.TwoFactorRequired(user.Id);
        Imported = user.TwoFactorRequired;
        Email = user.Email;
        EmailConfirmed = user.EmailConfirmed;
        MinLength = (await guard.Settings()).MinLength;
        Logins = await guard.History(LoginAudience.Portal, user.Id, 10);
        Links = await external.Links(user.Id);
        Providers = await external.Enabled();
        HasPassword = user.PasswordHash is not null;
        if (Enabled)
        {
            CodesLeft = await users.CountRecoveryCodesAsync(user);
        }
        else
        {
            Setup = await TotpSetup.For(users, store, user, config["Ui:Brand"] ?? "billing-oss");
        }

        return Page();
    }

    private async Task<bool> Persistent() => (await HttpContext.AuthenticateAsync(Schemes.Portal)).Properties?.IsPersistent == true;
}
