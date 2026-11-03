using System.Globalization;
using System.Text;
using System.Text.Json;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BillingOss.App.UseCases;

/// <summary>
/// Columnas del extracto de un banco, guardadas en la cuenta para transferir. Cada dato es el nombre de la columna en
/// el encabezado (sin distinguir mayúsculas) o su número, desde 0.
/// </summary>
public sealed record StatementMap
{
    public string Delimiter { get; init; } = ",";

    public bool HasHeader { get; init; } = true;

    public string Date { get; init; } = "fecha";

    public string DateFormat { get; init; } = "dd/MM/yyyy";

    /// <summary>Importe con signo: los débitos (negativos) no se importan.</summary>
    public string Amount { get; init; } = "importe";

    /// <summary>Separador decimal del importe: "," en los extractos argentinos, "." en otros.</summary>
    public string DecimalSeparator { get; init; } = ",";

    /// <summary>La referencia unívoca de la transferencia (BCRA, punto 3.2.1.2): con la cuenta, la clave del movimiento.</summary>
    public string Reference { get; init; } = "referencia";

    public string? PayerCuit { get; init; } = "cuit";

    public string? PayerName { get; init; } = "ordenante";

    public string? Concept { get; init; } = "concepto";

    public static StatementMap Parse(string? json)
    {
        try
        {
            return json is null or "{}" ? new StatementMap() : JsonSerializer.Deserialize<StatementMap>(json, Json) ?? new StatementMap();
        }
        catch (JsonException ex)
        {
            throw new ValidationException($"El mapa de columnas no es un JSON válido: {ex.Message}");
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}

public sealed record TransferAccountInput(string Bank, string Holder, string Cuit, string Cbu, string? Alias, string Currency, bool Active = true, string? CsvMap = null);

/// <summary>El comprobante de una transferencia que adjunta el cliente. El tipo sale del contenido, no del nombre.</summary>
public sealed record NoticeFile(string Name, byte[] Content)
{
    public const int MaxBytes = 5 * 1024 * 1024;

    /// <summary>application/pdf, image/png o image/jpeg según los primeros bytes, o null si no es ninguno.</summary>
    public static string? TypeOf(ReadOnlySpan<byte> content) =>
        content.StartsWith("%PDF-"u8) ? "application/pdf"
        : content.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ? "image/png"
        : content.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? "image/jpeg"
        : null;
}

/// <summary>
/// Resultado de importar un extracto. <paramref name="Existing"/> son las filas que ya estaban en la base, de una
/// importación anterior, con la misma fecha, importe y ordenante. Una referencia que se repite en el mismo archivo, o que
/// ya está en la base con otros datos, vuelve en <paramref name="Errors"/> con su línea: ese crédito no entra y el staff
/// tiene que mirarlo.
/// </summary>
public sealed record StatementImport(int Rows, int Created, int Applied, int Unmatched, int Existing, IReadOnlyList<string> Errors);

/// <summary>
/// Transferencias a CBU, CVU o alias, sin módulo (sección 4.6 del diseño de cobros y 9.3 del protocolo). El extracto
/// del banco se importa y cada crédito entra como pago manual de transferencia por el mismo camino que
/// POST /api/v1/payments, con la referencia unívoca del banco como ID externo: reimportar no duplica. El CUIT del
/// ordenante identifica la cuenta y el importe, las facturas.
/// </summary>
public sealed class TransferService(BillingDbContext db, PaymentService payments, AuditLog audit, IOptions<PaymentsOptions> options, TimeProvider time, ILogger<TransferService> logger)
{
    /// <summary>Canal de los pagos por transferencia: el mismo de la carga manual por la API con method "transferencia".</summary>
    public const string Channel = "manual:transferencia";

    /// <summary>Avisos de "Ya transferí" abiertos por cuenta: con el comprobante de hasta 5 MB, un cliente no llena la base.</summary>
    public const int MaxOpenNotices = 10;

    private const int MaxErrors = 50;

    /// <summary>ID externo del pago de un movimiento: la cuenta para transferir y la referencia del banco.</summary>
    public static string ExternalId(BankMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);
        return string.Create(CultureInfo.InvariantCulture, $"bank-{movement.TransferAccountId}-{movement.Reference}");
    }

    /// <summary>Alta o cambio de una cuenta para transferir, con el CBU o CVU validado por sus dígitos verificadores.</summary>
    public async Task<long> SaveAccount(long? id, TransferAccountInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var cuit = Digits(input.Cuit);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Bank) || string.IsNullOrWhiteSpace(input.Holder))
        {
            errors.Add("Faltan el banco o el titular.");
        }

        if (cuit is not { Length: 11 })
        {
            errors.Add("Escribí el CUIT con 11 números.");
        }

        if (!TransferAccount.IsValidCbu(Digits(input.Cbu)))
        {
            errors.Add("El CBU o CVU tiene que tener 22 números y sus dígitos verificadores.");
        }

        if (input.Alias is { Length: > 0 } alias && (alias.Length is < 6 or > 20 || !alias.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')))
        {
            errors.Add("El alias tiene entre 6 y 20 letras, números, puntos o guiones.");
        }

        if (input.Currency is not { Length: 3 } || !input.Currency.All(char.IsAsciiLetterUpper))
        {
            errors.Add("La moneda es un código ISO de tres letras, por ejemplo ARS.");
        }

        var map = StatementMap.Parse(input.CsvMap);
        if (errors.Count > 0)
        {
            throw new ValidationException(string.Join(' ', errors));
        }

        var account = id is { } existing
            ? await db.TransferAccounts.SingleOrDefaultAsync(a => a.Id == existing, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No existe la cuenta para transferir {existing}.")
            : db.TransferAccounts.Add(new TransferAccount { CreatedAt = time.GetUtcNow() }).Entity;
        account.Bank = input.Bank.Trim();
        account.Holder = input.Holder.Trim();
        account.Cuit = cuit!;
        account.Cbu = Digits(input.Cbu)!;
        account.Alias = string.IsNullOrWhiteSpace(input.Alias) ? null : input.Alias.Trim();
        account.Currency = new Currency(input.Currency);
        account.Active = input.Active;
        account.CsvMap = map.ToJson();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("payments.transfer_account_saved", string.Create(CultureInfo.InvariantCulture, $"transfer_account:{account.Id}"), null, ct).ConfigureAwait(false);
        return account.Id;
    }

    /// <summary>
    /// Importa el extracto: guarda cada crédito una sola vez y lo concilia. Las filas que no se entienden vuelven como
    /// errores con su número de línea, sin frenar las demás. La referencia identifica el crédito. Si ya está en la base
    /// con la misma fecha, importe y ordenante es el mismo movimiento reimportado, y se cuenta. Si se repite en el mismo
    /// archivo (un extracto no lista dos veces un movimiento) o está en la base con otros datos, es otro crédito con una
    /// referencia que no es unívoca, casi siempre una columna mal mapeada: vuelve como error para que no se pierda en
    /// silencio.
    /// </summary>
    public async Task<StatementImport> Import(long transferAccountId, string csv, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var account = await db.TransferAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == transferAccountId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la cuenta para transferir {transferAccountId}.");
        var map = StatementMap.Parse(account.CsvMap);
        if (map.Delimiter is not { Length: 1 })
        {
            throw new ValidationException("El separador de columnas del mapa tiene que ser un solo carácter.");
        }

        var rows = Csv(csv, map.Delimiter[0]);
        var errors = new List<string>();
        var columns = Columns(map, map.HasHeader && rows.Count > 0 ? rows[0] : null);
        var created = new List<long>();
        var existing = 0;
        var failed = 0;
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var first = map.HasHeader ? 1 : 0;
        var now = time.GetUtcNow();
        void Error(int index, string text)
        {
            if (++failed <= MaxErrors)
            {
                errors.Add(string.Create(CultureInfo.InvariantCulture, $"Línea {index + 1}: {text}"));
            }
        }

        for (var i = first; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var movement = Read(row, columns, map, out var error);
            if (movement is null)
            {
                if (error is not null)
                {
                    Error(i, error);
                }

                continue;
            }

            if (seen.TryGetValue(movement.Reference, out var earlier))
            {
                Error(i, $"la referencia {movement.Reference} ya está en la línea {earlier} del mismo archivo: cada crédito necesita una referencia unívoca (revisá el mapa de columnas). Este no entró.");
                continue;
            }

            seen[movement.Reference] = i + 1;
            await using var cmd = await Sql.Command(db, """
                INSERT INTO payments.bank_movements (transfer_account_id, reference, date, amount, currency, payer_cuit, payer_name, concept, status, suggested_document_ids, created_at, updated_at)
                VALUES (@account, @reference, @date, @amount, @currency, @cuit, @name, @concept, 'Unmatched', '{}', @now, @now)
                ON CONFLICT (transfer_account_id, reference) DO NOTHING
                RETURNING id
                """, ct, ("account", transferAccountId), ("reference", movement.Reference), ("date", movement.Date), ("amount", movement.Amount),
                ("currency", account.Currency.Code), ("cuit", movement.PayerCuit), ("name", movement.PayerName), ("concept", movement.Concept), ("now", now)).ConfigureAwait(false);
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is long id)
            {
                created.Add(id);
                continue;
            }

            var stored = await db.BankMovements.AsNoTracking()
                .Where(m => m.TransferAccountId == transferAccountId && m.Reference == movement.Reference)
                .Select(m => new { m.Date, m.Amount, m.PayerCuit }).SingleAsync(ct).ConfigureAwait(false);
            if (stored.Date == movement.Date && stored.Amount == movement.Amount && stored.PayerCuit == movement.PayerCuit)
            {
                existing++;
            }
            else
            {
                Error(i, $"la referencia {movement.Reference} ya está en la base con otra fecha, otro importe u otro ordenante ({stored.Date:dd/MM/yyyy}, {stored.Amount}): cada crédito necesita una referencia unívoca (revisá el mapa de columnas). Este no entró.");
            }
        }

        if (failed > errors.Count)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"Y {failed - errors.Count} líneas más con error."));
        }

        var applied = 0;
        foreach (var id in created)
        {
            if (await Match(id, ct).ConfigureAwait(false))
            {
                applied++;
            }
        }

        await audit.Record("payments.statement_imported", string.Create(CultureInfo.InvariantCulture, $"transfer_account:{transferAccountId}"), new { rows = rows.Count - first, created = created.Count, applied, existing, errors = failed }, ct).ConfigureAwait(false);
        return new StatementImport(rows.Count - first, created.Count, applied, created.Count - applied, existing, errors);
    }

    /// <summary>
    /// Concilia un movimiento nuevo: identifica la cuenta por el CUIT o CUIL del ordenante y propone las facturas cuyo
    /// saldo suma el importe. Con Payments:TransfersAutoApply, la coincidencia exacta se imputa sola y la cuenta
    /// identificada sin factura queda como saldo a favor. Lo demás queda en la bandeja. Devuelve si se aplicó.
    /// </summary>
    public async Task<bool> Match(long movementId, CancellationToken ct = default)
    {
        var movement = await db.BankMovements.SingleAsync(m => m.Id == movementId, ct).ConfigureAwait(false);
        if (movement.Status != MovementStatus.Unmatched)
        {
            return false;
        }

        if (movement.PayerCuit is { } cuit)
        {
            var accounts = await AccountsByCuit(cuit, ct).ConfigureAwait(false);
            movement.AccountId = accounts.Count == 1 ? accounts[0] : null;
        }

        movement.SuggestedDocumentIds = movement.AccountId is { } accountId ? await Suggest(accountId, movement.Currency, movement.Amount, ct).ConfigureAwait(false) : [];
        movement.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (!options.Value.TransfersAutoApply || movement.AccountId is not { } account)
        {
            return false;
        }

        await Apply(movement.Id, account, movement.SuggestedDocumentIds, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Entra el movimiento como pago de la cuenta: a esas facturas, a las impagas más viejas (null) o todo a saldo a
    /// favor (vacío). Repetirlo no duplica: el pago es único por canal e ID externo.
    /// </summary>
    public async Task<long> Apply(long movementId, long accountId, IReadOnlyList<long>? documentIds, CancellationToken ct = default)
    {
        var movement = await db.BankMovements.SingleOrDefaultAsync(m => m.Id == movementId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el movimiento {movementId}.");
        if (movement.Status == MovementStatus.Ignored)
        {
            throw new ValidationException("El movimiento está marcado como que no es un cobro.");
        }

        if (movement.Status == MovementStatus.Applied)
        {
            return movement.PaymentId!.Value;
        }

        if (!await db.Accounts.AnyAsync(a => a.Id == accountId, ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"No existe la cuenta {accountId}.");
        }

        var paymentId = await payments.Record(new PaymentInput(accountId, Channel, ExternalId(movement), movement.Currency, movement.Amount, PaymentStatus.Approved, documentIds), ct).ConfigureAwait(false);

        // Record pudo limpiar el contexto: el movimiento se vuelve a leer.
        movement = await db.BankMovements.SingleAsync(m => m.Id == movementId, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        movement.Status = MovementStatus.Applied;
        movement.AccountId = accountId;
        movement.PaymentId = paymentId;
        movement.UpdatedAt = now;
        var notice = await db.TransferNotices
            .Where(n => n.AccountId == accountId && n.Status == NoticeStatus.Open && n.Amount == movement.Amount && n.Currency == movement.Currency)
            .OrderBy(n => n.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (notice is not null)
        {
            notice.Status = NoticeStatus.Resolved;
            notice.MovementId = movement.Id;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("payments.transfer_applied", string.Create(CultureInfo.InvariantCulture, $"account:{accountId}"), new { movement = movement.Id, payment = paymentId }, ct).ConfigureAwait(false);
        return paymentId;
    }

    /// <summary>No es un cobro de un cliente, por ejemplo una transferencia entre cuentas propias.</summary>
    public async Task Ignore(long movementId, CancellationToken ct = default)
    {
        var movement = await db.BankMovements.SingleOrDefaultAsync(m => m.Id == movementId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el movimiento {movementId}.");
        if (movement.Status == MovementStatus.Applied)
        {
            throw new ValidationException("El movimiento ya entró como pago.");
        }

        movement.Status = MovementStatus.Ignored;
        movement.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("payments.transfer_ignored", string.Create(CultureInfo.InvariantCulture, $"bank_movement:{movement.Id}"), null, ct).ConfigureAwait(false);
    }

    /// <summary>La cuenta ya tiene MaxOpenNotices avisos esperando al staff.</summary>
    public async Task<bool> NoticesFull(long accountId, CancellationToken ct = default) =>
        await db.TransferNotices.CountAsync(n => n.AccountId == accountId && n.Status == NoticeStatus.Open, ct).ConfigureAwait(false) >= MaxOpenNotices;

    /// <summary>"Ya transferí": un aviso para el staff, con el saldo de las facturas y el comprobante si lo hay. No es un pago.</summary>
    public async Task<long> Notify(long accountId, IReadOnlyList<long> documentIds, DateOnly transferredOn, string? reference, NoticeFile? receipt = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        if (await NoticesFull(accountId, ct).ConfigureAwait(false))
        {
            throw new ValidationException("La cuenta ya tiene varios avisos de transferencia esperando que el staff los revise.");
        }

        if (receipt is not null && (receipt.Content.Length is 0 or > NoticeFile.MaxBytes || NoticeFile.TypeOf(receipt.Content) is null))
        {
            throw new ValidationException("El comprobante tiene que ser un PDF, PNG o JPG de hasta 5 MB.");
        }

        var ids = documentIds.Distinct().ToList();
        var docs = await db.Documents.AsNoTracking().Where(d => ids.Contains(d.Id) && d.AccountId == accountId).ToListAsync(ct).ConfigureAwait(false);
        if (ids.Count == 0 || docs.Count != ids.Count || docs.Any(d => !d.IsIssued || d.Kind == DocumentKind.CreditNote || d.Pending <= 0m) || docs.Select(d => d.Currency).Distinct().Count() != 1)
        {
            throw new ValidationException("Las facturas tienen que ser de la cuenta, estar por pagar y en la misma moneda.");
        }

        var notice = new TransferNotice
        {
            AccountId = accountId,
            DocumentIds = [.. ids],
            Amount = docs.Sum(d => d.Pending),
            Currency = docs[0].Currency,
            TransferredOn = transferredOn,
            Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim() is { Length: > 200 } r ? r[..200] : reference.Trim(),
            Receipt = receipt?.Content,
            ReceiptName = receipt is null ? null : Path.GetFileName(receipt.Name) is { Length: > 0 } n ? n.Length > 200 ? n[^200..] : n : "comprobante",
            ReceiptType = receipt is null ? null : NoticeFile.TypeOf(receipt.Content),
            CreatedAt = time.GetUtcNow(),
        };
        db.TransferNotices.Add(notice);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("payments.transfer_notice", string.Create(CultureInfo.InvariantCulture, $"account:{accountId}"), new { notice = notice.Id }, ct).ConfigureAwait(false);
        logger.LogInformation("La cuenta {AccountId} avisó una transferencia por {Amount} {Currency}.", accountId, notice.Amount, notice.Currency.Code);
        return notice.Id;
    }

    public async Task ResolveNotice(long noticeId, CancellationToken ct = default)
    {
        var notice = await db.TransferNotices.SingleOrDefaultAsync(n => n.Id == noticeId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el aviso {noticeId}.");
        notice.Status = NoticeStatus.Resolved;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Cuentas con ese CUIT o CUIL en su identidad fiscal vigente, con o sin guiones.</summary>
    private async Task<List<long>> AccountsByCuit(string cuit, CancellationToken ct)
    {
        var accounts = new List<long>();
        await using var cmd = await Sql.Command(db, """
            SELECT DISTINCT account_id FROM accounts.tax_identities
            WHERE valid_to IS NULL AND document_type IN ('Cuit', 'Cuil') AND regexp_replace(document_number, '\D', '', 'g') = @cuit
            """, ct, ("cuit", cuit)).ConfigureAwait(false);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            accounts.Add(reader.GetInt64(0));
        }

        return accounts;
    }

    /// <summary>
    /// Facturas impagas de la cuenta en la moneda cuyo saldo suma exactamente el importe: las más viejas en orden, como
    /// imputa un pago, o si no una sola con ese saldo. Sin coincidencia exacta, ninguna.
    /// </summary>
    private async Task<long[]> Suggest(long accountId, Currency currency, decimal amount, CancellationToken ct)
    {
        var unpaid = await db.Documents.AsNoTracking()
            .Where(d => d.AccountId == accountId && d.Currency == currency && d.Kind != DocumentKind.CreditNote && d.IssueDate != null && d.Total - d.Credited - d.Allocated > 0)
            .OrderBy(d => d.DueDate).ThenBy(d => d.Id)
            .Select(d => new { d.Id, Pending = d.Total - d.Credited - d.Allocated })
            .ToListAsync(ct).ConfigureAwait(false);
        var sum = 0m;
        var prefix = new List<long>();
        foreach (var d in unpaid)
        {
            sum += d.Pending;
            prefix.Add(d.Id);
            if (sum == amount)
            {
                return [.. prefix];
            }

            if (sum > amount)
            {
                break;
            }
        }

        return unpaid.FirstOrDefault(d => d.Pending == amount) is { } single ? [single.Id] : [];
    }

    private static Dictionary<string, int> Columns(StatementMap map, IReadOnlyList<string>? header)
    {
        var names = new Dictionary<string, string?>
        {
            ["date"] = map.Date,
            ["amount"] = map.Amount,
            ["reference"] = map.Reference,
            ["cuit"] = map.PayerCuit,
            ["name"] = map.PayerName,
            ["concept"] = map.Concept,
        };
        var columns = new Dictionary<string, int>();
        foreach (var (field, name) in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var index = int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n
                : header?.Select((h, i) => (h, i)).Where(x => x.h.Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)).Select(x => (int?)x.i).FirstOrDefault() ?? -1;
            if (index < 0 && field is "date" or "amount" or "reference")
            {
                throw new ValidationException($"El extracto no tiene la columna «{name}» del mapa de columnas.");
            }

            if (index >= 0)
            {
                columns[field] = index;
            }
        }

        return columns;
    }

    private static BankMovement? Read(IReadOnlyList<string> row, Dictionary<string, int> columns, StatementMap map, out string? error)
    {
        string? Cell(string field) => columns.TryGetValue(field, out var i) && i < row.Count && !string.IsNullOrWhiteSpace(row[i]) ? row[i].Trim() : null;

        error = null;
        var amountText = Cell("amount");
        var reference = Cell("reference");
        if (amountText is null || reference is null || Cell("date") is not { } dateText)
        {
            error = "faltan la fecha, el importe o la referencia.";
            return null;
        }

        var normalized = map.DecimalSeparator == ","
            ? amountText.Replace(".", string.Empty, StringComparison.Ordinal).Replace(",", ".", StringComparison.Ordinal)
            : amountText.Replace(",", string.Empty, StringComparison.Ordinal);
        normalized = new string([.. normalized.Where(c => !char.IsWhiteSpace(c) && c != '$')]);
        if (!decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || decimal.Round(amount, 2) != amount)
        {
            error = $"importe que no se entiende ({amountText}).";
            return null;
        }

        if (amount <= 0m)
        {
            // Un débito de la cuenta no es un cobro.
            return null;
        }

        if (!DateOnly.TryParseExact(dateText, map.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            error = $"fecha que no tiene el formato {map.DateFormat} ({dateText}).";
            return null;
        }

        return new BankMovement
        {
            Reference = reference.Length > 200 ? reference[..200] : reference,
            Date = date,
            Amount = amount,
            PayerCuit = Digits(Cell("cuit")) is { Length: 11 } cuit ? cuit : null,
            PayerName = Cell("name") is { } name ? name.Length > 200 ? name[..200] : name : null,
            Concept = Cell("concept") is { } concept ? concept.Length > 500 ? concept[..500] : concept : null,
        };
    }

    private static string? Digits(string? text) => text is null ? null : new string([.. text.Where(char.IsAsciiDigit)]);

    /// <summary>CSV con comillas dobles (RFC 4180): un campo entre comillas puede tener el separador, comillas dobladas y saltos.</summary>
    public static List<List<string>> Csv(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else if (c != '﻿')
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
