using Microsoft.AspNetCore.Identity;

namespace BillingOss.Web.Identity;

/// <summary>
/// Las contraseñas importadas conservan su hash de origen (D-10). Si es bcrypt ($2a$, $2b$ o $2y$), se verifica y se
/// pide el rehash: Identity guarda su propio hash en ese mismo ingreso. Cualquier otro formato ajeno no verifica, y
/// esa persona entra restableciendo la contraseña por correo.
/// </summary>
public sealed class ImportedPasswordHasher<TUser> : IPasswordHasher<TUser>
    where TUser : class
{
    private readonly PasswordHasher<TUser> inner = new();

    public string HashPassword(TUser user, string password) => inner.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(TUser user, string hashedPassword, string providedPassword)
    {
        ArgumentNullException.ThrowIfNull(hashedPassword);
        try
        {
            if (IsBcrypt(hashedPassword))
            {
                return BCrypt.Net.BCrypt.Verify(providedPassword, hashedPassword) ? PasswordVerificationResult.SuccessRehashNeeded : PasswordVerificationResult.Failed;
            }

            return hashedPassword.StartsWith('$') ? PasswordVerificationResult.Failed : inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
        catch (Exception ex) when (ex is FormatException or BCrypt.Net.SaltParseException)
        {
            return PasswordVerificationResult.Failed;
        }
    }

    private static bool IsBcrypt(string hash) =>
        hash.Length == 60 && (hash.StartsWith("$2a$", StringComparison.Ordinal) || hash.StartsWith("$2b$", StringComparison.Ordinal) || hash.StartsWith("$2y$", StringComparison.Ordinal));
}
