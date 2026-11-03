using System.Globalization;
using System.Net;
using System.Security.Claims;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Identity;

/// <summary>Esquemas de autenticación (D-10): portal y staff con cookies separadas, más sus pasos intermedios.</summary>
public static class Schemes
{
    /// <summary>Clientes. SameSite=Lax: viaja en la vuelta de la pasarela por GET.</summary>
    public const string Portal = "portal";

    /// <summary>Contraseña correcta, falta el segundo factor. Dura minutos.</summary>
    public const string PortalPending = "portal.2fa";

    /// <summary>
    /// La ida a un proveedor externo: state, nonce y el verificador de PKCE hasta la vuelta (diseno/ingreso-clientes.md,
    /// sección 4). SameSite=Lax, porque la vuelta llega desde el sitio del proveedor.
    /// </summary>
    public const string PortalChallenge = "portal.oidc";

    /// <summary>Lo que devolvió el proveedor, ya validado, mientras se decide si entra, se vincula o se registra.</summary>
    public const string PortalExternal = "portal.external";

    /// <summary>Staff, solo bajo /admin. SameSite=Strict.</summary>
    public const string Staff = "staff";

    public const string StaffPending = "staff.2fa";

    /// <summary>Lo que devuelve el proveedor OIDC antes de vincularlo con un usuario del staff.</summary>
    public const string StaffExternal = "staff.external";

    public const string StaffOidc = "staff.oidc";
}

public static class Claims
{
    public const string Stamp = "bo:stamp";

    public const string Permission = "bo:perm";

    /// <summary>"Ver como cliente": la cuenta que mira el staff.</summary>
    public const string ActAccount = "bo:act_account";

    /// <summary>"Ver como cliente": quién del staff está mirando.</summary>
    public const string ActStaff = "bo:act_staff";

    public const string ActStaffStamp = "bo:act_staff_stamp";

    public static long? UserId(this ClaimsPrincipal principal) =>
        long.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), CultureInfo.InvariantCulture, out var id) ? id : null;

    public static long? ActingAccount(this ClaimsPrincipal principal) =>
        long.TryParse(principal?.FindFirstValue(ActAccount), CultureInfo.InvariantCulture, out var id) ? id : null;
}

public enum LoginOutcome
{
    Failed,
    LockedOut,

    /// <summary>La IP está en la lista de bloqueo o bloqueada por intentos (diseno/fraude-y-acceso.md, sección 6.2).</summary>
    IpBlocked,
    Success,
}

/// <summary>
/// Un intento de ingreso con lo que hace falta para anotarlo en el historial: a qué lado entra, desde qué IP y con qué
/// navegador.
/// </summary>
public sealed record LoginAttempt(AccessGuard Guard, LoginAudience Audience, IPAddress? Ip, string? UserAgent)
{
    public static LoginAttempt From(HttpContext http, AccessGuard guard, LoginAudience audience)
    {
        ArgumentNullException.ThrowIfNull(http);
        return new(guard, audience, http.Connection.RemoteIpAddress, http.Request.Headers.UserAgent.ToString());
    }

    public Task Record(LoginUser? user, LoginResult result, LoginMethod method) => Guard.Record(Audience, user, Ip, UserAgent, result, method);
}

/// <summary>Ingreso, segundo factor y validación de sesiones, iguales para clientes y staff.</summary>
public static class Sessions
{
    public static TimeSpan PendingLifetime { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Verifica la contraseña con bloqueo por intentos (D-10) y la IP contra la lista. Un hash bcrypt importado se
    /// reemplaza acá mismo por el de Identity. Con un correo que no existe igual se calcula un hash, para no revelar qué
    /// correos tienen usuario. Todo intento queda en el historial. El ingreso exitoso lo anota quien lo termina, con
    /// <see cref="Succeeded"/>, porque puede faltar el segundo factor.
    /// </summary>
    public static async Task<(LoginOutcome Outcome, TUser? User)> CheckPassword<TUser>(UserManager<TUser> users, LoginAttempt attempt, string email, string password)
        where TUser : LoginUser
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(attempt);
        Tune(users, await attempt.Guard.Settings().ConfigureAwait(false));
        if (await attempt.Guard.IsBlocked(attempt.Ip).ConfigureAwait(false))
        {
            users.PasswordHasher.VerifyHashedPassword(null!, DummyHash.Value, password ?? string.Empty);
            await attempt.Record(null, LoginResult.BlockedIp, LoginMethod.Password).ConfigureAwait(false);
            return (LoginOutcome.IpBlocked, null);
        }

        var user = string.IsNullOrWhiteSpace(email) ? null : await users.FindByEmailAsync(email.Trim()).ConfigureAwait(false);
        if (user is null || user.DisabledAt is not null || user.PasswordHash is null)
        {
            users.PasswordHasher.VerifyHashedPassword(null!, DummyHash.Value, password ?? string.Empty);
            var result = user is null ? LoginResult.UnknownUser : user.DisabledAt is not null ? LoginResult.Disabled : LoginResult.BadPassword;
            await attempt.Record(user, result, LoginMethod.Password).ConfigureAwait(false);
            return (LoginOutcome.Failed, null);
        }

        if (await users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            await attempt.Record(user, LoginResult.Locked, LoginMethod.Password).ConfigureAwait(false);
            return (LoginOutcome.LockedOut, null);
        }

        if (await users.CheckPasswordAsync(user, password ?? string.Empty).ConfigureAwait(false))
        {
            return (LoginOutcome.Success, user);
        }

        await users.AccessFailedAsync(user).ConfigureAwait(false);
        await attempt.Record(user, LoginResult.BadPassword, LoginMethod.Password).ConfigureAwait(false);
        return (await users.IsLockedOutAsync(user).ConfigureAwait(false) ? LoginOutcome.LockedOut : LoginOutcome.Failed, null);
    }

    /// <summary>
    /// Verifica un código TOTP o, si no tiene forma de código, uno de recuperación. Un error cuenta como intento fallido.
    /// El ingreso exitoso queda en el historial con el método que usó.
    /// </summary>
    public static async Task<LoginOutcome> CheckSecondFactor<TUser>(UserManager<TUser> users, LoginAttempt attempt, TUser user, string provider, string? code)
        where TUser : LoginUser
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(user);
        Tune(users, await attempt.Guard.Settings().ConfigureAwait(false));
        var clean = new string((code ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());
        var method = clean.Length == 6 && clean.All(char.IsAsciiDigit) ? LoginMethod.Totp : LoginMethod.RecoveryCode;
        if (await attempt.Guard.IsBlocked(attempt.Ip).ConfigureAwait(false))
        {
            await attempt.Record(user, LoginResult.BlockedIp, method).ConfigureAwait(false);
            return LoginOutcome.IpBlocked;
        }

        if (await users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            await attempt.Record(user, LoginResult.Locked, method).ConfigureAwait(false);
            return LoginOutcome.LockedOut;
        }

        var ok = method == LoginMethod.Totp
            ? await users.VerifyTwoFactorTokenAsync(user, provider, clean).ConfigureAwait(false)
            : clean.Length > 0 && (await users.RedeemTwoFactorRecoveryCodeAsync(user, clean.ToUpperInvariant()).ConfigureAwait(false)).Succeeded;
        if (ok)
        {
            await users.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
            await Succeeded(attempt, user, method).ConfigureAwait(false);
            return LoginOutcome.Success;
        }

        await users.AccessFailedAsync(user).ConfigureAwait(false);
        await attempt.Record(user, LoginResult.BadCode, method).ConfigureAwait(false);
        return await users.IsLockedOutAsync(user).ConfigureAwait(false) ? LoginOutcome.LockedOut : LoginOutcome.Failed;
    }

    /// <summary>Anota el ingreso exitoso. Si es desde una red nueva para ese usuario, le avisa por correo.</summary>
    public static Task Succeeded(LoginAttempt attempt, LoginUser user, LoginMethod method)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return attempt.Record(user, LoginResult.Success, method);
    }

    /// <summary>
    /// Enrola el TOTP en el primer ingreso del staff, que ya pasó la contraseña. Es un paso del ingreso: pasa por la
    /// lista, por el bloqueo de la cuenta y por el historial como un código, y un código malo cuenta como intento fallido.
    /// Devuelve los códigos de recuperación, que se muestran una vez.
    /// </summary>
    public static async Task<(LoginOutcome Outcome, IReadOnlyList<string>? Codes)> EnrollSecondFactor<TUser>(UserManager<TUser> users, LoginAttempt attempt, TUser user, string provider, string? code)
        where TUser : LoginUser
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(user);
        Tune(users, await attempt.Guard.Settings().ConfigureAwait(false));
        if (await attempt.Guard.IsBlocked(attempt.Ip).ConfigureAwait(false))
        {
            await attempt.Record(user, LoginResult.BlockedIp, LoginMethod.Totp).ConfigureAwait(false);
            return (LoginOutcome.IpBlocked, null);
        }

        if (await users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            await attempt.Record(user, LoginResult.Locked, LoginMethod.Totp).ConfigureAwait(false);
            return (LoginOutcome.LockedOut, null);
        }

        if (await TotpSetup.Enable(users, user, provider, code).ConfigureAwait(false) is { } codes)
        {
            await users.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
            await Succeeded(attempt, user, LoginMethod.Totp).ConfigureAwait(false);
            return (LoginOutcome.Success, codes);
        }

        await users.AccessFailedAsync(user).ConfigureAwait(false);
        await attempt.Record(user, LoginResult.BadCode, LoginMethod.Totp).ConfigureAwait(false);
        return (await users.IsLockedOutAsync(user).ConfigureAwait(false) ? LoginOutcome.LockedOut : LoginOutcome.Failed, null);
    }

    /// <summary>
    /// El bloqueo por cuenta con los ajustes del panel, con el umbral del staff para el staff. El UserManager es de este
    /// pedido: se le cambian sus opciones sin tocar las compartidas.
    /// </summary>
    internal static void Tune<TUser>(UserManager<TUser> users, SecuritySettings settings)
        where TUser : class
    {
        var o = users.Options;
        var staff = typeof(TUser) == typeof(StaffUser);
        users.Options = new IdentityOptions
        {
            ClaimsIdentity = o.ClaimsIdentity,
            User = o.User,
            Password = o.Password,
            SignIn = o.SignIn,
            Tokens = o.Tokens,
            Stores = o.Stores,
            Lockout = new LockoutOptions
            {
                AllowedForNewUsers = true,
                MaxFailedAccessAttempts = staff ? settings.StaffAccountAttempts : settings.AccountAttempts,
                DefaultLockoutTimeSpan = TimeSpan.FromMinutes(staff ? settings.StaffAccountMinutes : settings.AccountMinutes),
            },
        };
    }

    /// <summary>Guarda en una cookie corta que la contraseña ya se verificó y falta el segundo factor.</summary>
    public static Task BeginSecondFactor(HttpContext http, string scheme, LoginUser user, bool persistent)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(user);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Id(user))], scheme);
        return http.SignInAsync(scheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = false,
            Items = { ["persistent"] = persistent ? "1" : "0" },
        });
    }

    /// <summary>El usuario que ya pasó la contraseña, o null si la cookie intermedia venció.</summary>
    public static async Task<(TUser? User, bool Persistent)> PendingUser<TUser>(HttpContext http, string scheme, UserManager<TUser> users)
        where TUser : LoginUser
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(users);
        var result = await http.AuthenticateAsync(scheme).ConfigureAwait(false);
        if (!result.Succeeded || result.Principal.UserId() is not { } id)
        {
            return (null, false);
        }

        var user = await users.FindByIdAsync(id.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        var persistent = result.Properties?.Items.TryGetValue("persistent", out var p) == true && p == "1";
        return (user is { DisabledAt: null } ? user : null, persistent);
    }

    public static async Task SignInPortal(HttpContext http, PortalUser user, bool persistent)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(user);
        await http.SignOutAsync(Schemes.PortalPending).ConfigureAwait(false);
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Id(user)), new Claim(ClaimTypes.Name, user.Email), new Claim(Claims.Stamp, user.SecurityStamp)],
            Schemes.Portal);
        await http.SignInAsync(Schemes.Portal, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = persistent }).ConfigureAwait(false);
    }

    public static async Task SignInStaff(HttpContext http, StaffUser user)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(user);
        await http.SignOutAsync(Schemes.StaffPending).ConfigureAwait(false);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Id(user)),
                new Claim(ClaimTypes.Name, user.Email),
                new Claim(Claims.Stamp, user.SecurityStamp),
                .. user.Permissions.Select(p => new Claim(Claims.Permission, p)),
            ],
            Schemes.Staff);
        await http.SignInAsync(Schemes.Staff, new ClaimsPrincipal(identity)).ConfigureAwait(false);
    }

    /// <summary>"Ver como cliente": una sesión del portal atada a la cuenta, no a un usuario, que lleva quién del staff mira.</summary>
    public static Task SignInAsAccount(HttpContext http, long accountId, StaffUser staff)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(staff);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, staff.Email),
                new Claim(Claims.ActAccount, accountId.ToString(CultureInfo.InvariantCulture)),
                new Claim(Claims.ActStaff, Id(staff)),
                new Claim(Claims.ActStaffStamp, staff.SecurityStamp),
            ],
            Schemes.Portal);
        // Una hora que se renueva con el uso, y solo mientras el staff siga habilitado y con el permiso.
        return http.SignInAsync(Schemes.Portal, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) });
    }

    /// <summary>
    /// Cada pedido confirma que el usuario sigue habilitado y que su sello no cambió: cambiar la contraseña o el
    /// segundo factor corta las otras sesiones en todos los nodos. Es una lectura por clave primaria. El bloqueo por
    /// intentos no corta sesiones abiertas: si no, cualquiera podría echar a un usuario fallando cinco veces.
    /// </summary>
    public static async Task ValidatePortal(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var principal = context.Principal!;
        var db = context.HttpContext.RequestServices.GetRequiredService<BillingDbContext>();
        bool valid;
        if (principal.ActingAccount() is not null)
        {
            var staffId = long.Parse(principal.FindFirstValue(Claims.ActStaff)!, CultureInfo.InvariantCulture);
            var stamp = principal.FindFirstValue(Claims.ActStaffStamp);
            valid = await db.Set<StaffUser>().AsNoTracking()
                .AnyAsync(u => u.Id == staffId && u.DisabledAt == null && u.SecurityStamp == stamp && u.Permissions.Contains(StaffPermissions.Impersonate)).ConfigureAwait(false);
        }
        else
        {
            valid = await IsCurrent<PortalUser>(db, principal).ConfigureAwait(false);
        }

        if (!valid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(Schemes.Portal).ConfigureAwait(false);
        }
    }

    public static async Task ValidateStaff(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var db = context.HttpContext.RequestServices.GetRequiredService<BillingDbContext>();
        if (!await IsCurrent<StaffUser>(db, context.Principal!).ConfigureAwait(false))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(Schemes.Staff).ConfigureAwait(false);
        }
    }

    private static async Task<bool> IsCurrent<TUser>(BillingDbContext db, ClaimsPrincipal principal)
        where TUser : LoginUser
    {
        var id = principal.UserId();
        var stamp = principal.FindFirstValue(Claims.Stamp);
        return id is not null && await db.Set<TUser>().AsNoTracking()
            .AnyAsync(u => u.Id == id && u.DisabledAt == null && u.SecurityStamp == stamp).ConfigureAwait(false);
    }

    private static string Id(LoginUser user) => user.Id.ToString(CultureInfo.InvariantCulture);

    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<object>().HashPassword(new object(), "sin-usuario"));
}
