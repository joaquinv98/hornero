using System.Globalization;
using System.Net;
using System.Net.Mail;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BillingOss.Web.Identity;

/// <summary>Lo que escribe quien se registra. Sin contraseña cuando viene de un proveedor externo.</summary>
public sealed record RegistrationInput(string Name, string? CompanyName, string? Phone, string Email, string? Password, string Currency, string Language);

/// <summary>El usuario creado, o la clave del error y lo que rechazó la política de contraseñas.</summary>
public sealed record RegistrationResult(PortalUser? User, long AccountId, string? Error, IReadOnlyList<string> Problems)
{
    public static RegistrationResult Fail(string error, IReadOnlyList<string>? problems = null) => new(null, 0, error, problems ?? []);
}

/// <summary>
/// Alta de un cliente desde el portal (diseno/ingreso-clientes.md, sección 2): la cuenta, su usuario titular y, si vino
/// de un proveedor externo, el vínculo, en una sola transacción. Pasa por el ajuste que la apaga, las listas de bloqueo de
/// IPs y de correos y el tope de altas por IP, que vale entre nodos.
/// </summary>
public sealed class PortalRegistration(
    BillingDbContext db,
    UserManager<PortalUser> users,
    AccountService accounts,
    AccessGuard guard,
    ExternalLoginStore external,
    AuditLog audit,
    IAccountMail mail,
    IOptions<BillingOptions> options,
    TimeProvider time,
    ILogger<PortalRegistration> logger)
{
    public async Task<RegistrationResult> Register(RegistrationInput input, IPAddress? ip, ExternalIdentity? identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var settings = await guard.Settings(ct);
        if (!settings.RegistrationEnabled)
        {
            return RegistrationResult.Fail("Register.Closed");
        }

        if (await guard.IsBlocked(ip, ct))
        {
            return RegistrationResult.Fail("Register.IpBlocked");
        }

        // Con un proveedor, el correo es el que verificó él: no se puede cambiar en el formulario.
        var email = (identity?.Email ?? input.Email ?? string.Empty).Trim();
        if (email.Length > 320 || !MailAddress.TryCreate(email, out var address) || address.Address != email)
        {
            return RegistrationResult.Fail("Register.BadEmail");
        }

        if (identity is { EmailVerified: false })
        {
            return RegistrationResult.Fail("External.EmailNotVerified");
        }

        if (await guard.IsBlocked(email, ct))
        {
            return RegistrationResult.Fail("Register.EmailBlocked");
        }

        var name = (input.Name ?? string.Empty).Trim();
        var company = Blank(input.CompanyName);
        var phone = Blank(input.Phone);
        if (name.Length is 0 or > 200 || company?.Length > 200 || phone?.Length > 50)
        {
            return RegistrationResult.Fail("Register.BadName");
        }

        if (input.Currency is not { Length: 3 } code || !code.All(char.IsAsciiLetterUpper) || !(await Currencies(settings, ct)).Contains(code))
        {
            return RegistrationResult.Fail("Register.BadCurrency");
        }

        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await guard.RegistrationAllowed(ip, ct))
        {
            return RegistrationResult.Fail("Register.TooMany");
        }

        // El correo verificado por el proveedor queda verificado. El de la contraseña, cuando llegue el enlace.
        var user = new PortalUser { Email = email, Language = input.Language, CreatedAt = now, EmailConfirmed = identity is not null };
        IdentityResult created;
        long accountId;
        try
        {
            created = identity is null ? await users.CreateAsync(user, input.Password ?? string.Empty) : await users.CreateAsync(user);
            if (!created.Succeeded)
            {
                return created.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName")
                    ? RegistrationResult.Fail("Register.EmailTaken")
                    : RegistrationResult.Fail("Pwd.Rejected", [.. created.Errors.Select(e => e.Code).Where(c => c.StartsWith("Password", StringComparison.Ordinal)).Distinct()]);
            }

            accountId = await accounts.Create(new AccountInput(name, email, new Currency(code), input.Language, phone, company), ct);
        }
        catch (ValidationException)
        {
            // Otra cuenta ya tiene ese correo, aunque no tenga usuario: la da de alta el staff.
            return RegistrationResult.Fail("Register.EmailTaken");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return RegistrationResult.Fail("Register.EmailTaken");
        }

        db.Set<AccountUser>().Add(new AccountUser { AccountId = accountId, UserId = user.Id, IsOwner = true, CreatedAt = now });
        await db.SaveChangesAsync(ct);
        if (identity is not null && !await external.Add(identity.ProviderId, identity.Subject, user.Id, identity.Email, "registration", ct))
        {
            return RegistrationResult.Fail("External.LinkedElsewhere");
        }

        await audit.Record(AccessGuard.RegistrationAction, string.Create(CultureInfo.InvariantCulture, $"user:{user.Id}"), new { account_id = accountId, method = identity?.Provider ?? "password" }, ct);
        await tx.CommitAsync(ct);
        if (identity is null)
        {
            await SendVerification(user, ct);
        }

        return new(user, accountId, null, []);
    }

    /// <summary>
    /// Las monedas que se ofrecen en el alta: la de los ajustes y las de los precios del catálogo. La primera es la de los
    /// ajustes, que viene elegida.
    /// </summary>
    public async Task<IReadOnlyList<string>> Currencies(SecuritySettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var priced = await db.PlanPrices.AsNoTracking().Select(p => p.Currency).Distinct().ToListAsync(ct);
        return [settings.RegistrationCurrency, .. priced.Select(c => c.Code).Where(c => c != settings.RegistrationCurrency).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// El enlace para verificar el correo, con la plantilla de verificación (diseno/fraude-y-acceso.md, sección 5). Se
    /// arma con Billing:PublicUrl, nunca con el Host del pedido. Si falla, el alta sigue: se pide otro desde la seguridad.
    /// </summary>
    private async Task SendVerification(PortalUser user, CancellationToken ct)
    {
        if (options.Value.PublicUrl is not { Length: > 0 } publicUrl)
        {
            logger.LogError("Falta Billing:PublicUrl: no se puede armar el enlace para verificar el correo del usuario {UserId}.", user.Id);
            return;
        }

        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        var link = new Uri(string.Create(CultureInfo.InvariantCulture, $"{publicUrl.TrimEnd('/')}/cuenta/verificar?U={user.Id}&T={Uri.EscapeDataString(token)}"));
        await mail.SendEmailVerification(user.Email, user.Language, link, ct);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
