using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Access;

/// <summary>
/// Lo que sigue a la vuelta de un proveedor (diseno/ingreso-clientes.md, sección 4.3): entra si la persona ya está
/// vinculada, pide confirmar el vínculo con la contraseña o con un enlace al correo si ya hay un usuario con ese correo,
/// o completa el alta si no lo hay y el registro está abierto. Nunca vincula solo, y el segundo factor sigue valiendo.
/// </summary>
public sealed class ExternalModel(
    UserManager<PortalUser> users,
    AccessGuard guard,
    ExternalLoginStore external,
    PortalRegistration registration,
    IAccountMail mail,
    IDataProtectionProvider protection,
    IOptions<BillingOptions> options,
    ILogger<ExternalModel> logger) : PageModel
{
    /// <summary>El enlace del correo que confirma un vínculo vence en una hora, como el de la contraseña.</summary>
    public static TimeSpan MailLinkLifetime { get; } = TimeSpan.FromHours(1);

    public const string MailLinkPurpose = "BillingOss.Portal.ExternalLink";

    public enum Step
    {
        Error,

        /// <summary>Ya hay un usuario con ese correo, o vincula el de la sesión: confirma con la contraseña o con el correo.</summary>
        Confirm,

        /// <summary>No hay usuario con ese correo: completa el alta.</summary>
        Register,

        MailSent,

        /// <summary>Esa persona del proveedor ya estaba vinculada a este usuario.</summary>
        AlreadyLinked,
    }

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string Name { get; set; } = string.Empty;

    [BindProperty]
    public string? Company { get; set; }

    [BindProperty]
    public string? Phone { get; set; }

    [BindProperty]
    public string Currency { get; set; } = string.Empty;

    public Step Show { get; private set; }

    public ExternalIdentity? Identity { get; private set; }

    public bool Linking { get; private set; }

    public bool HasPassword { get; private set; }

    public IReadOnlyList<string> Currencies { get; private set; } = [];

    public string? Error { get; private set; }

    public IReadOnlyList<string> Problems { get; private set; } = [];

    /// <summary>Los minutos del bloqueo por intentos, para el mensaje.</summary>
    public int LockMinutes { get; private set; }

    public string Back => Linking ? "/cuenta/seguridad" : "/ingresar";

    public async Task<IActionResult> OnGet()
    {
        if (await Read() is not { } read)
        {
            return Redirect("/ingresar");
        }

        return await Decide(read.Identity, read.Trip);
    }

    /// <summary>Confirma el vínculo con la contraseña del usuario. Una contraseña mala cuenta como intento fallido.</summary>
    public async Task<IActionResult> OnPostPassword()
    {
        if (await Read() is not { } read)
        {
            return Redirect("/ingresar");
        }

        var (identity, trip) = read;
        if (await Target(identity, trip) is not { } target)
        {
            return await Decide(identity, trip);
        }

        var attempt = LoginAttempt.From(HttpContext, guard, LoginAudience.Portal);
        var (outcome, user) = await Sessions.CheckPassword(users, attempt, target.Email, Password);
        if (user is null || user.Id != target.Id)
        {
            LockMinutes = (await guard.Settings()).AccountMinutes;
            Error = outcome switch
            {
                LoginOutcome.LockedOut => "Access.LockedOut",
                LoginOutcome.IpBlocked => "Access.IpBlocked",
                _ => "External.WrongPassword",
            };
            return await Confirm(target);
        }

        if (!await external.Add(identity.ProviderId, identity.Subject, user.Id, identity.Email, "password"))
        {
            return await Fail("External.LinkedElsewhere");
        }

        if (Linking)
        {
            await HttpContext.SignOutAsync(Schemes.PortalExternal);
            return Redirect("/cuenta/seguridad?vinculado=1");
        }

        return await SignIn(user, identity, trip);
    }

    /// <summary>Manda al correo del usuario el enlace que confirma el vínculo. No entra: después ingresa con el proveedor.</summary>
    public async Task<IActionResult> OnPostMail()
    {
        if (await Read() is not { } read)
        {
            return Redirect("/ingresar");
        }

        var (identity, trip) = read;
        if (await Target(identity, trip) is not { } target)
        {
            return await Decide(identity, trip);
        }

        if (options.Value.PublicUrl is not { Length: > 0 } publicUrl)
        {
            logger.LogError("Falta Billing:PublicUrl: no se puede armar el enlace para confirmar el vínculo del usuario {UserId}.", target.Id);
            return await Fail("Mail.NoPublicUrl");
        }

        var token = protection.CreateProtector(MailLinkPurpose).ToTimeLimitedDataProtector().Protect(
            JsonSerializer.Serialize(new MailLink(target.Id, identity.ProviderId, identity.Subject, identity.Email, target.SecurityStamp)), MailLinkLifetime);
        var link = new Uri($"{publicUrl.TrimEnd('/')}/ingresar/externo/confirmar?t={Uri.EscapeDataString(token)}");
        await mail.SendExternalLink(target.Email, target.Language, identity.ProviderName, link, HttpContext.RequestAborted);
        await HttpContext.SignOutAsync(Schemes.PortalExternal);
        Identity = identity;
        Show = Step.MailSent;
        return Page();
    }

    /// <summary>Completa el alta con el correo que verificó el proveedor, sin contraseña.</summary>
    public async Task<IActionResult> OnPostRegister()
    {
        if (await Read() is not { } read)
        {
            return Redirect("/ingresar");
        }

        var (identity, trip) = read;
        Identity = identity;
        if (trip.Purpose != ExternalPurpose.Login || !await Active(identity))
        {
            return await Decide(identity, trip);
        }

        var input = new RegistrationInput(Name, Company, Phone, identity.Email ?? string.Empty, null, Currency, RegisterModel.Language());
        var result = await registration.Register(input, HttpContext.Connection.RemoteIpAddress, identity, HttpContext.RequestAborted);
        if (result.User is not { } user)
        {
            Error = result.Error;
            return await RegisterForm();
        }

        await HttpContext.SignOutAsync(Schemes.PortalExternal);
        await Sessions.Succeeded(LoginAttempt.From(HttpContext, guard, LoginAudience.Portal), user, LoginMethod.External);
        await Sessions.SignInPortal(HttpContext, user, persistent: false);
        return LocalRedirect(RegisterModel.NextStep(trip.ReturnUrl));
    }

    /// <summary>Qué le toca a quien volvió del proveedor.</summary>
    private async Task<IActionResult> Decide(ExternalIdentity identity, ExternalTrip trip)
    {
        Identity = identity;
        Linking = trip.Purpose == ExternalPurpose.Link;
        if (await guard.IsBlocked(HttpContext.Connection.RemoteIpAddress))
        {
            await LoginAttempt.From(HttpContext, guard, LoginAudience.Portal).Record(null, LoginResult.BlockedIp, LoginMethod.External);
            return await Fail("Access.IpBlocked");
        }

        if (!await Active(identity))
        {
            return await Fail("External.Unavailable");
        }

        var link = await external.Link(identity.ProviderId, identity.Subject);
        if (Linking)
        {
            if (await SessionUser(trip) is not { } current)
            {
                return await Fail("External.Expired");
            }

            if (link is not null)
            {
                await HttpContext.SignOutAsync(Schemes.PortalExternal);
                Show = link.UserId == current.Id ? Step.AlreadyLinked : Step.Error;
                Error = link.UserId == current.Id ? null : "External.LinkedElsewhere";
                return Page();
            }

            return identity.EmailVerified ? await Confirm(current) : await Fail("External.EmailNotVerified");
        }

        if (link is not null)
        {
            var linked = await users.FindByIdAsync(link.UserId.ToString(CultureInfo.InvariantCulture));
            return linked is null ? await Fail("External.Failed") : await SignIn(linked, identity, trip);
        }

        if (!identity.EmailVerified || identity.Email is null)
        {
            return await Fail("External.EmailNotVerified");
        }

        if (await guard.IsBlocked(identity.Email))
        {
            return await Fail("Register.EmailBlocked");
        }

        if (await users.FindByEmailAsync(identity.Email) is { } existing)
        {
            return existing.DisabledAt is null ? await Confirm(existing) : await Fail("External.Disabled");
        }

        return (await guard.Settings()).RegistrationEnabled ? await RegisterForm() : await Fail("External.NoUser");
    }

    /// <summary>
    /// Entra con el vínculo. Con el segundo factor activo pasa al paso del código, como con la contraseña. Un usuario
    /// deshabilitado no entra.
    /// </summary>
    private async Task<IActionResult> SignIn(PortalUser user, ExternalIdentity identity, ExternalTrip trip)
    {
        var attempt = LoginAttempt.From(HttpContext, guard, LoginAudience.Portal);
        if (user.DisabledAt is not null)
        {
            await attempt.Record(user, LoginResult.Disabled, LoginMethod.External);
            return await Fail("External.Disabled");
        }

        if (await external.Link(identity.ProviderId, identity.Subject) is { } link)
        {
            await external.Touch(link.Id);
        }

        await HttpContext.SignOutAsync(Schemes.PortalExternal);
        if (user.TwoFactorEnabled)
        {
            await Sessions.BeginSecondFactor(HttpContext, Schemes.PortalPending, user, persistent: false);
            return Redirect("/ingresar/codigo?ReturnUrl=" + Uri.EscapeDataString(trip.ReturnUrl));
        }

        await users.ResetAccessFailedCountAsync(user);
        await Sessions.Succeeded(attempt, user, LoginMethod.External);
        await Sessions.SignInPortal(HttpContext, user, persistent: false);
        return LocalRedirect(await guard.TwoFactorPending(user.Id, HttpContext.RequestAborted) ? TwoFactorEnrolled.SecurityPath : trip.ReturnUrl);
    }

    /// <summary>
    /// A quién se vincularía: el usuario de la sesión que pidió vincular, o el que tiene el correo verificado por el
    /// proveedor. Null si ya no corresponde confirmar nada (vinculado mientras tanto, correo sin verificar, otra sesión).
    /// </summary>
    private async Task<PortalUser?> Target(ExternalIdentity identity, ExternalTrip trip)
    {
        Identity = identity;
        Linking = trip.Purpose == ExternalPurpose.Link;
        if (!identity.EmailVerified || !await Active(identity) || await external.Link(identity.ProviderId, identity.Subject) is not null
            || await guard.IsBlocked(HttpContext.Connection.RemoteIpAddress))
        {
            return null;
        }

        if (Linking)
        {
            return await SessionUser(trip);
        }

        return identity.Email is { } email && await users.FindByEmailAsync(email) is { DisabledAt: null } user ? user : null;
    }

    /// <summary>El proveedor de la vuelta sigue prendido: el staff lo pudo apagar mientras la persona decidía.</summary>
    private async Task<bool> Active(ExternalIdentity identity) =>
        await external.Find(identity.Provider) is { Enabled: true } provider && provider.Id == identity.ProviderId;

    /// <summary>El usuario de la sesión del portal, si es el mismo que pidió vincular. "Ver como cliente" no vincula.</summary>
    private async Task<PortalUser?> SessionUser(ExternalTrip trip)
    {
        var session = await HttpContext.AuthenticateAsync(Schemes.Portal);
        return session.Succeeded && session.Principal.ActingAccount() is null && session.Principal.UserId() is { } id && id == trip.UserId
            ? await users.FindByIdAsync(id.ToString(CultureInfo.InvariantCulture)) is { DisabledAt: null } user ? user : null
            : null;
    }

    private Task<IActionResult> Confirm(PortalUser target)
    {
        HasPassword = target.PasswordHash is not null;
        Show = Step.Confirm;
        return Task.FromResult<IActionResult>(Page());
    }

    private async Task<IActionResult> RegisterForm()
    {
        var settings = await guard.Settings();
        Currencies = await registration.Currencies(settings, HttpContext.RequestAborted);
        if (Currency.Length == 0)
        {
            Currency = Currencies[0];
        }

        if (Name.Length == 0)
        {
            Name = Identity?.Name ?? string.Empty;
        }

        Show = Step.Register;
        return Page();
    }

    private async Task<IActionResult> Fail(string error)
    {
        await HttpContext.SignOutAsync(Schemes.PortalExternal);
        Error = error;
        Show = Step.Error;
        return Page();
    }

    private async Task<(ExternalIdentity Identity, ExternalTrip Trip)?> Read()
    {
        var result = await HttpContext.AuthenticateAsync(Schemes.PortalExternal);
        if (!result.Succeeded)
        {
            return null;
        }

        var principal = result.Principal;
        var items = result.Properties;
        if (principal.FindFirstValue("provider") is not { } provider || !long.TryParse(principal.FindFirstValue("provider_id"), CultureInfo.InvariantCulture, out var providerId)
            || principal.FindFirstValue("sub") is not { } subject)
        {
            return null;
        }

        var identity = new ExternalIdentity(provider, providerId, principal.FindFirstValue("provider_name") ?? provider, subject, principal.FindFirstValue("email"),
            principal.FindFirstValue("email_verified") == "true", principal.FindFirstValue("name"));
        var trip = new ExternalTrip(items.GetString("purpose") ?? ExternalPurpose.Login, Ui.SafeReturn(items.GetString("return")),
            long.TryParse(items.GetString("user"), CultureInfo.InvariantCulture, out var user) ? user : null);
        return (identity, trip);
    }

    /// <summary>Lo que lleva el enlace del correo, protegido y con vencimiento. El sello lo invalida si cambia la contraseña.</summary>
    public sealed record MailLink(long User, long Provider, string Subject, string? Email, string Stamp);
}
