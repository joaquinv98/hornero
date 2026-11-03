using BillingOss.App.Identity;
using Microsoft.AspNetCore.Identity;

namespace BillingOss.Web.Identity;

/// <summary>Lo que se muestra para enrolar una app de TOTP: la semilla en grupos de cuatro y el enlace otpauth.</summary>
public sealed record TotpSetup(string Key, string Uri)
{
    public string Grouped => string.Join(' ', Key.Chunk(4).Select(c => new string(c)));

    /// <summary>
    /// Crea la semilla si todavía no tiene. Se guarda directo en el almacén para no cambiar el sello, que cortaría la
    /// sesión en curso.
    /// </summary>
    public static async Task<TotpSetup> For<TUser>(UserManager<TUser> users, IUserStore<TUser> store, TUser user, string issuer)
        where TUser : LoginUser
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(user);
        var keys = (IUserAuthenticatorKeyStore<TUser>)store;
        var key = await users.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        if (key is null)
        {
            key = users.GenerateNewAuthenticatorKey();
            await keys.SetAuthenticatorKeyAsync(user, key, CancellationToken.None).ConfigureAwait(false);
            await store.UpdateAsync(user, CancellationToken.None).ConfigureAwait(false);
        }

        var label = System.Uri.EscapeDataString($"{issuer}:{user.Email}");
        return new TotpSetup(key, $"otpauth://totp/{label}?secret={key}&issuer={System.Uri.EscapeDataString(issuer)}&digits=6");
    }

    /// <summary>Activa el segundo factor si el código vale y devuelve los códigos de recuperación, que se muestran una vez.</summary>
    public static async Task<IReadOnlyList<string>?> Enable<TUser>(UserManager<TUser> users, TUser user, string provider, string? code)
        where TUser : LoginUser
    {
        ArgumentNullException.ThrowIfNull(users);
        var clean = new string((code ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (clean.Length != 6 || !await users.VerifyTwoFactorTokenAsync(user, provider, clean).ConfigureAwait(false))
        {
            return null;
        }

        await users.SetTwoFactorEnabledAsync(user, true).ConfigureAwait(false);
        return [.. await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount).ConfigureAwait(false) ?? []];
    }

    public const int RecoveryCodeCount = 8;

    /// <summary>
    /// Confirma una acción sensible (desactivar el segundo factor, generar códigos nuevos) con un código de la app. Un
    /// error cuenta para el bloqueo por intentos de los ajustes, así una sesión robada no sirve para adivinarlo. No es un
    /// ingreso: no va al historial.
    /// </summary>
    public static async Task<bool> Confirm<TUser>(UserManager<TUser> users, TUser user, string provider, string? code, SecuritySettings settings)
        where TUser : LoginUser
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(settings);
        Sessions.Tune(users, settings);
        var clean = new string((code ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (await users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            return false;
        }

        if (clean.Length == 6 && await users.VerifyTwoFactorTokenAsync(user, provider, clean).ConfigureAwait(false))
        {
            await users.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
            return true;
        }

        await users.AccessFailedAsync(user).ConfigureAwait(false);
        return false;
    }

    /// <summary>Ocho códigos de recuperación nuevos si el código de la app vale. Los anteriores dejan de valer.</summary>
    public static async Task<IReadOnlyList<string>?> NewRecoveryCodes<TUser>(UserManager<TUser> users, TUser user, string provider, string? code, SecuritySettings settings)
        where TUser : LoginUser
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(user);
        return user.TwoFactorEnabled && await Confirm(users, user, provider, code, settings).ConfigureAwait(false)
            ? [.. await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount).ConfigureAwait(false) ?? []]
            : null;
    }
}
