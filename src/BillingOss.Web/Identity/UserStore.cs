using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Identity;

/// <summary>
/// Almacén de Identity sobre las tablas propias de usuarios (D-10). El correo es el nombre de usuario. La semilla de
/// TOTP y los códigos de recuperación se cifran por campo con la llave maestra (D-09): sin llave no se puede activar
/// el segundo factor, y como el staff lo tiene obligatorio, tampoco puede entrar.
/// </summary>
public sealed class UserStore<TUser>(BillingDbContext db, IServiceProvider services) :
    IUserPasswordStore<TUser>,
    IUserEmailStore<TUser>,
    IUserSecurityStampStore<TUser>,
    IUserLockoutStore<TUser>,
    IUserTwoFactorStore<TUser>,
    IUserAuthenticatorKeyStore<TUser>,
    IUserTwoFactorRecoveryCodeStore<TUser>
    where TUser : LoginUser
{
    private DbSet<TUser> Users => db.Set<TUser>();

    private string Table => typeof(TUser) == typeof(StaffUser) ? "identity.staff_users" : "identity.portal_users";

    public async Task<IdentityResult> CreateAsync(TUser user, CancellationToken cancellationToken)
    {
        Users.Add(user);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(TUser user, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(TUser user, CancellationToken cancellationToken)
    {
        Users.Remove(user);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return IdentityResult.Success;
    }

    public async Task<TUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        long.TryParse(userId, out var id) ? await Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken).ConfigureAwait(false) : null;

    public Task<TUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => FindByEmailAsync(normalizedUserName, cancellationToken);

    public Task<TUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

    public Task<string> GetUserIdAsync(TUser user, CancellationToken cancellationToken) => Task.FromResult(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public Task<string?> GetUserNameAsync(TUser user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.Email);

    public Task SetUserNameAsync(TUser user, string? userName, CancellationToken cancellationToken) => SetEmailAsync(user, userName, cancellationToken);

    public Task<string?> GetNormalizedUserNameAsync(TUser user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.NormalizedEmail);

    public Task SetNormalizedUserNameAsync(TUser user, string? normalizedName, CancellationToken cancellationToken) => SetNormalizedEmailAsync(user, normalizedName, cancellationToken);

    public Task SetPasswordHashAsync(TUser user, string? passwordHash, CancellationToken cancellationToken)
    {
        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(TUser user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash);

    public Task<bool> HasPasswordAsync(TUser user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash is not null);

    public Task SetEmailAsync(TUser user, string? email, CancellationToken cancellationToken)
    {
        user.Email = email ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task<string?> GetEmailAsync(TUser user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.Email);

    public Task<bool> GetEmailConfirmedAsync(TUser user, CancellationToken cancellationToken) => Task.FromResult(user.EmailConfirmed);

    public Task SetEmailConfirmedAsync(TUser user, bool confirmed, CancellationToken cancellationToken)
    {
        user.EmailConfirmed = confirmed;
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedEmailAsync(TUser user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.NormalizedEmail);

    public Task SetNormalizedEmailAsync(TUser user, string? normalizedEmail, CancellationToken cancellationToken)
    {
        user.NormalizedEmail = normalizedEmail ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task SetSecurityStampAsync(TUser user, string stamp, CancellationToken cancellationToken)
    {
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(TUser user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.SecurityStamp);

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(TUser user, CancellationToken cancellationToken) => Task.FromResult(user.LockoutEnd);

    public Task SetLockoutEndDateAsync(TUser user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        user.LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(TUser user, CancellationToken cancellationToken) => Task.FromResult(++user.AccessFailedCount);

    public Task ResetAccessFailedCountAsync(TUser user, CancellationToken cancellationToken)
    {
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(TUser user, CancellationToken cancellationToken) => Task.FromResult(user.AccessFailedCount);

    /// <summary>El bloqueo por intentos vale para todos los usuarios, sin excepciones.</summary>
    public Task<bool> GetLockoutEnabledAsync(TUser user, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task SetLockoutEnabledAsync(TUser user, bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetTwoFactorEnabledAsync(TUser user, bool enabled, CancellationToken cancellationToken)
    {
        user.TwoFactorEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task<bool> GetTwoFactorEnabledAsync(TUser user, CancellationToken cancellationToken) => Task.FromResult(user.TwoFactorEnabled);

    public Task SetAuthenticatorKeyAsync(TUser user, string key, CancellationToken cancellationToken)
    {
        user.AuthenticatorKey = Seal(key, user, "authenticator_key");
        return Task.CompletedTask;
    }

    public Task<string?> GetAuthenticatorKeyAsync(TUser user, CancellationToken cancellationToken) =>
        Task.FromResult(user.AuthenticatorKey is null ? null : Open(user.AuthenticatorKey, user, "authenticator_key"));

    public Task ReplaceCodesAsync(TUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
    {
        user.RecoveryCodes = Seal(string.Join(';', recoveryCodes), user, "recovery_codes");
        return Task.CompletedTask;
    }

    public async Task<bool> RedeemCodeAsync(TUser user, string code, CancellationToken cancellationToken)
    {
        var codes = Codes(user);
        if (!codes.Remove(code))
        {
            return false;
        }

        await ReplaceCodesAsync(user, codes, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<int> CountCodesAsync(TUser user, CancellationToken cancellationToken) => Task.FromResult(Codes(user).Count);

    public void Dispose()
    {
    }

    private List<string> Codes(TUser user) =>
        user.RecoveryCodes is null ? [] : [.. Open(user.RecoveryCodes, user, "recovery_codes").Split(';', StringSplitOptions.RemoveEmptyEntries)];

    // El usuario ya tiene ID cuando se enrola un segundo factor: el cifrado queda atado a esa fila.
    private byte[] Seal(string value, TUser user, string column) => Box.Encrypt(value, Table, column, user.Id);

    private string Open(byte[] blob, TUser user, string column) => Box.Decrypt(blob, Table, column, user.Id);

    private SecretBox Box => services.GetService<SecretBox>()
        ?? throw new InvalidOperationException("Falta la llave de secretos (Secrets:KeyFile): sin ella no se guarda ni se lee el segundo factor.");
}
