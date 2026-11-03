using System.Net.Mail;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BillingOss.App.UseCases;

public sealed record TaxIdentityInput(
    TaxDocumentType DocumentType,
    string DocumentNumber,
    string LegalName,
    int VatCondition,
    string Country,
    string? Province = null,
    string? City = null,
    string? Street = null,
    string? PostalCode = null,
    string? VatNumber = null);

public sealed record AccountInput(string Name, string Email, Currency Currency, string Language = "es", string? Phone = null, string? CompanyName = null, TaxIdentityInput? Tax = null);

public sealed class ValidationException(string message) : Exception(message);

/// <summary>Alta y cambios de cuentas (sección 2 del modelo de dominio).</summary>
public sealed class AccountService(BillingDbContext db, TimeProvider time, IVatNumberValidator vatNumbers, TaxSetupService taxes)
{
    /// <summary>Países cuyo número de IVA se valida: la Unión Europea en VIES y el Reino Unido en HMRC.</summary>
    public static IReadOnlySet<string> VatNumberCountries { get; } = new HashSet<string>(TaxSetupService.EuVatRates.Keys.Append("GB"), StringComparer.Ordinal);

    public async Task<long> Create(AccountInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        input = Validate(input);
        await EnsureConditionAllowed(input.Tax?.VatCondition, null, ct).ConfigureAwait(false);
        await EnsureEmailFree(input.Email, null, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        await using var tx = await Sql.OwnTransaction(db, ct).ConfigureAwait(false);
        var account = new Account(input.Name, input.Email, input.Currency, now, input.Language) { Phone = input.Phone, CompanyName = input.CompanyName };
        db.Accounts.Add(account);
        await Save(input.Email, ct).ConfigureAwait(false);
        if (input.Tax is { } tax)
        {
            db.TaxIdentities.Add(NewIdentity(account.Id, tax, now));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await Sql.Commit(tx, ct).ConfigureAwait(false);
        return account.Id;
    }

    public async Task Update(long accountId, AccountInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        input = Validate(input);
        await EnsureEmailFree(input.Email, accountId, ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la cuenta {accountId}.");
        account.Name = input.Name;
        account.Email = input.Email;
        account.Phone = input.Phone;
        account.CompanyName = input.CompanyName;
        account.Language = input.Language;
        if (input.Tax is { } tax)
        {
            // La identidad fiscal tiene vigencia: un cambio cierra la vigente y abre una nueva. Se compara con los
            // valores ya normalizados, así escribir lo mismo en otra caja no abre una versión.
            var now = time.GetUtcNow();
            var current = await db.TaxIdentities.SingleOrDefaultAsync(t => t.AccountId == accountId && t.ValidTo == null, ct).ConfigureAwait(false);
            await EnsureConditionAllowed(tax.VatCondition, current?.VatCondition, ct).ConfigureAwait(false);

            // Las fichas del admin y del portal no editan el domicilio: si no viene, sigue el de la versión vigente, que
            // va en el receptor del comprobante y en el domicilio de la factura E.
            tax = tax with { Street = tax.Street ?? current?.Street, City = tax.City ?? current?.City, PostalCode = tax.PostalCode ?? current?.PostalCode };
            if (current is null || current.DocumentType != tax.DocumentType || current.DocumentNumber != tax.DocumentNumber || current.VatCondition != tax.VatCondition
                || current.LegalName != tax.LegalName || current.Country != tax.Country || current.Province != tax.Province || current.VatNumber != tax.VatNumber
                || current.Street != tax.Street || current.City != tax.City || current.PostalCode != tax.PostalCode)
            {
                var next = NewIdentity(accountId, tax, now);
                if (current is not null && current.Country == tax.Country && current.VatNumber == tax.VatNumber)
                {
                    // La constancia de VIES o HMRC es del número y del país: si no cambian, la versión nueva la hereda.
                    next.VatNumberValidatedAt = current.VatNumberValidatedAt;
                    next.VatValidationRef = current.VatValidationRef;
                }

                current?.Close(now);
                await Save(input.Email, ct).ConfigureAwait(false);
                db.TaxIdentities.Add(next);
            }
        }

        await Save(input.Email, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Borra una cuenta sin historia, por ejemplo una cargada por error. Una con documentos, servicios, pedidos,
    /// pagos o saldo no se borra nunca: se cierra (sección 2 del modelo). Lo que la base todavía referencie también
    /// cuenta como historia.
    /// </summary>
    public async Task Delete(long accountId, CancellationToken ct = default)
    {
        const string HasHistory = "La cuenta tiene historia (documentos, servicios, pedidos o pagos): no se borra, se cierra desde el admin.";
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "accounts.accounts", [accountId], ct).ConfigureAwait(false);
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la cuenta {accountId}.");
        if (await db.Documents.AnyAsync(d => d.AccountId == accountId, ct).ConfigureAwait(false)
            || await db.Services.AnyAsync(s => s.AccountId == accountId, ct).ConfigureAwait(false)
            || await db.Orders.AnyAsync(o => o.AccountId == accountId, ct).ConfigureAwait(false)
            || await db.Payments.AnyAsync(p => p.AccountId == accountId, ct).ConfigureAwait(false)
            || await db.CreditEntries.AnyAsync(c => c.AccountId == accountId, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(HasHistory);
        }

        await db.Set<AccountUser>().Where(m => m.AccountId == accountId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.TaxIdentities.Where(t => t.AccountId == accountId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        db.Accounts.Remove(account);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            throw new InvalidOperationException(HasHistory);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// Valida el número de IVA de la identidad vigente contra VIES o HMRC y guarda la constancia en esa versión: es
    /// evidencia, no un cambio de ficha. Con el número validado, las reglas de empresa del exterior aplican la
    /// inversión del sujeto pasivo.
    /// </summary>
    public async Task<VatNumberCheck> ValidateVatNumber(long accountId, CancellationToken ct = default)
    {
        var identity = await db.TaxIdentities.SingleOrDefaultAsync(t => t.AccountId == accountId && t.ValidTo == null, ct).ConfigureAwait(false)
            ?? throw new ValidationException("La cuenta no tiene datos fiscales.");
        if (!VatNumberCountries.Contains(identity.Country))
        {
            throw new ValidationException($"El país {identity.Country} no tiene validación de número de IVA.");
        }

        if (string.IsNullOrWhiteSpace(identity.VatNumber))
        {
            throw new ValidationException("Falta el número de IVA.");
        }

        var check = await vatNumbers.Check(identity.Country, identity.VatNumber, ct).ConfigureAwait(false);
        if (!check.Valid)
        {
            identity.VatNumberValidatedAt = null;
            identity.VatValidationRef = null;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            throw new ValidationException($"El número de IVA {identity.Country}{identity.VatNumber} no es válido según el registro.");
        }

        identity.VatNumberValidatedAt = check.CheckedAt;
        identity.VatValidationRef = check.RequestId;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return check;
    }

    /// <summary>
    /// Completa la ficha fiscal con lo que informó el padrón: una versión nueva de la identidad con su fuente y su
    /// fecha. La condición es una sugerencia que confirma quien carga: si el padrón no la deduce, queda la actual.
    /// </summary>
    public async Task FillFromRegistry(long accountId, TaxpayerInfo info, int? confirmedCondition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var current = await db.TaxIdentities.SingleOrDefaultAsync(t => t.AccountId == accountId && t.ValidTo == null, ct).ConfigureAwait(false);
        var condition = confirmedCondition ?? info.SuggestedVatCondition ?? current?.VatCondition
            ?? throw new ValidationException("El padrón no deduce la condición frente al IVA: elegila a mano.");
        if (!VatConditions.IsKnown(condition))
        {
            throw new ValidationException($"La condición frente al IVA {condition} no está en la tabla de ARCA.");
        }

        await EnsureConditionAllowed(condition, current?.VatCondition, ct).ConfigureAwait(false);
        current?.Close(now);
        db.TaxIdentities.Add(new TaxIdentity(accountId, TaxDocumentType.Cuit, info.TaxId, info.Name, condition, "AR", now)
        {
            Street = info.Street ?? current?.Street,
            City = info.City ?? current?.City,
            Province = ArgentinaProvinces.Code(info.Province) ?? current?.Province,
            PostalCode = info.PostalCode ?? current?.PostalCode,
            Source = "arca",
            VerifiedAt = now,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Marca de "empresa grande según ARCA", que carga el staff mirando el listado: una factura por el mínimo de la
    /// factura de crédito electrónica MiPyME o más tiene que ser FCE, y hasta que exista se frena antes de emitir.
    /// </summary>
    public async Task SetLargeCompany(long accountId, bool largeCompany, CancellationToken ct = default)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la cuenta {accountId}.");
        account.LargeCompany = largeCompany;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>El correo identifica a la cuenta, sin distinguir mayúsculas. Otra cuenta no puede tenerlo.</summary>
    private async Task EnsureEmailFree(string email, long? ownId, CancellationToken ct)
    {
        if (await db.Accounts.AnyAsync(a => (ownId == null || a.Id != ownId) && a.Email.ToLower() == email.ToLower(), ct).ConfigureAwait(false))
        {
            throw new ValidationException($"Ya hay una cuenta con el correo {email}.");
        }
    }

    /// <summary>Dos altas o cambios a la vez con el mismo correo: el índice único decide y el que pierde falla como validación.</summary>
    private async Task Save(string email, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ix_accounts_email" })
        {
            throw new ValidationException($"Ya hay una cuenta con el correo {email}.");
        }
    }

    private static TaxIdentity NewIdentity(long accountId, TaxIdentityInput tax, DateTimeOffset now) =>
        new(accountId, tax.DocumentType, tax.DocumentNumber, tax.LegalName, tax.VatCondition, tax.Country, now)
        {
            Province = tax.Province,
            City = tax.City,
            Street = tax.Street,
            PostalCode = tax.PostalCode,
            VatNumber = tax.VatNumber,
            Source = "manual",
        };

    /// <summary>
    /// La condición 7 (sujeto no categorizado) obliga a percibir IVA por la RG 2126: sin la percepción configurada la
    /// factura se frenaría antes de ir a ARCA, así que la ficha no la acepta (sección 3.8 del diseño fiscal). Solo
    /// frena el pase a la 7: una cuenta que ya la tenía cuando se cerró la percepción se sigue editando, y el aviso
    /// de Impuestos la cuenta hasta que la corrijan.
    /// </summary>
    private async Task EnsureConditionAllowed(int? condition, int? current, CancellationToken ct)
    {
        if (condition == VatConditions.NoCategorizado && current != VatConditions.NoCategorizado && !await taxes.Rg2126Active(ct).ConfigureAwait(false))
        {
            throw new ValidationException("La condición 7 (sujeto no categorizado) exige la percepción de IVA de la RG 2126 configurada en Impuestos, Percepciones: sin ella la factura se frenaría antes de ir a ARCA. Si el cliente acreditó su condición, cargá esa.");
        }
    }

    /// <summary>Valida y devuelve la entrada ya normalizada: país y número de IVA en mayúsculas y la provincia argentina como código ISO 3166-2.</summary>
    private static AccountInput Validate(AccountInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ValidationException("Falta el nombre.");
        }

        if (!MailAddress.TryCreate(input.Email, out _))
        {
            throw new ValidationException($"Correo inválido: {input.Email}.");
        }

        if (input.Tax is not { } tax)
        {
            return input;
        }

        if (tax.DocumentType is TaxDocumentType.Cuit or TaxDocumentType.Cuil && !TaxIdentity.IsValidCuit(tax.DocumentNumber))
        {
            throw new ValidationException($"CUIT o CUIL inválido: {tax.DocumentNumber}.");
        }

        // El fisco toma el DNI como número de 7 u 8 dígitos y no tiene condición frente al IVA 0: sin esto, la factura
        // recién falla al emitir el comprobante.
        if (tax.DocumentType is TaxDocumentType.Dni && !(tax.DocumentNumber.Length is 7 or 8 && tax.DocumentNumber.All(char.IsAsciiDigit)))
        {
            throw new ValidationException($"DNI inválido, va con 7 u 8 dígitos y sin puntos: {tax.DocumentNumber}.");
        }

        if (tax.VatCondition <= 0)
        {
            throw new ValidationException("Falta la condición frente al IVA.");
        }

        // Solo los códigos de la tabla de ARCA: la RG 5616/2024 exige la condición en todo comprobante electrónico.
        if (!VatConditions.IsKnown(tax.VatCondition))
        {
            throw new ValidationException($"La condición frente al IVA {tax.VatCondition} no está en la tabla de ARCA.");
        }

        if (tax.Country is not { Length: 2 })
        {
            throw new ValidationException("El país va en ISO 3166-1 de dos letras.");
        }

        var country = tax.Country.ToUpperInvariant();
        var province = string.IsNullOrWhiteSpace(tax.Province) ? null : tax.Province.Trim();
        if (country == "AR" && province is not null)
        {
            // Las reglas por subdivisión y los regímenes de ingresos brutos nombran la provincia por su código ISO 3166-2.
            province = ArgentinaProvinces.Code(province)
                ?? throw new ValidationException($"La provincia \"{province}\" no está en la tabla ISO 3166-2 de Argentina: elegí una de la lista, por ejemplo AR-B para Buenos Aires o AR-C para la Ciudad.");
        }

        return input with { Tax = tax with { Country = country, Province = province, VatNumber = string.IsNullOrWhiteSpace(tax.VatNumber) ? null : tax.VatNumber.Trim().ToUpperInvariant() } };
    }
}
