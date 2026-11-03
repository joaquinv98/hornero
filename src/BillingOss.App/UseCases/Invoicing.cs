using System.Globalization;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.UseCases;

/// <summary>
/// Renglón propio: de una factura manual, de un presupuesto o de un ítem facturable. Gravado va con la categoría de
/// servicios digitales, como un producto gravado, y el motor de impuestos resuelve su alícuota para el receptor. El
/// importe es cantidad por precio, redondeado a dos decimales.
/// </summary>
public sealed record LineInput(string Description, decimal Quantity, decimal UnitPrice, TaxTreatment Treatment = TaxTreatment.Taxed, long? ServiceId = null)
{
    /// <summary>
    /// La categoría del motor cuando no sale del tratamiento: la del producto de un plan cotizado, que es la misma con que
    /// lo factura el pedido. Solo la pone el núcleo: ni la API ni los formularios la reciben.
    /// </summary>
    internal string? Category { get; init; }
}

/// <summary>Sin moneda, la de la cuenta. Sin vencimiento, el plazo de siempre. Con Draft queda en borrador sin número.</summary>
public sealed record ManualInvoiceInput(long AccountId, IReadOnlyList<LineInput> Lines, string? Currency = null, DateOnly? DueDate = null, bool Draft = false);

public sealed record AppliedCredit(long DocumentId, string Currency, decimal Amount);

/// <summary>Motivo de las facturas que no salen de un pedido: marca de dónde vienen y quién las puede tocar.</summary>
public static class DocumentReasons
{
    public const string Renewal = "renewal";
    public const string Manual = "manual";
}

/// <summary>
/// Facturas con renglones propios, saldo a favor aplicado a pedido y ajustes de saldo del staff. Las facturas se emiten
/// con el mismo cierre que las demás y consumen el saldo a favor al emitirse (sección 6.3 del modelo de dominio).
/// </summary>
public sealed class InvoiceService(BillingDbContext db, BillingCore core, CreditService credit, AuditLog audit, TimeProvider time)
{
    public const int MaxLines = 200;

    public async Task<long> CreateManual(ManualInvoiceInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input.Lines);
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == input.AccountId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la cuenta {input.AccountId}.");
        var today = core.Today();
        var due = input.DueDate ?? today.AddDays(core.Options.InvoiceDueDays);
        if (due < today)
        {
            throw new ValidationException("El vencimiento no puede ser anterior a hoy.");
        }

        // Los precios se cargan en el modo de carga por defecto de la moneda, como los del catálogo.
        var currency = input.Currency is null ? account.Currency : ParseCurrency(input.Currency);
        var draft = new Document(DocumentKind.Invoice, account.Id, currency, (await core.Settings(ct).ConfigureAwait(false)).DefaultPriceMode(currency), due, time.GetUtcNow(), reason: DocumentReasons.Manual);
        await AddLines(draft, input.Lines, LineKind.Manual, ct).ConfigureAwait(false);
        db.Documents.Add(draft);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (!input.Draft)
        {
            await IssueAndApplyCredit(draft, ct).ConfigureAwait(false);
        }

        await audit.Record("invoice.manual", Entity(draft.Id), new { account_id = account.Id, draft = input.Draft, lines = input.Lines.Count }, ct).ConfigureAwait(false);
        await core.Commit(tx, ct).ConfigureAwait(false);
        return draft.Id;
    }

    /// <summary>Emite un borrador manual. Repetirlo sobre uno ya emitido no hace nada.</summary>
    public async Task Issue(long draftId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var draft = await LockManual(draftId, ct).ConfigureAwait(false);
        if (draft.IsIssued)
        {
            return;
        }

        if (draft.DueDate < core.Today())
        {
            throw new ValidationException("El vencimiento del borrador ya pasó: descartalo y armá otro.");
        }

        await IssueAndApplyCredit(draft, ct).ConfigureAwait(false);
        await audit.Record("invoice.issue", Entity(draftId), ct: ct).ConfigureAwait(false);
        await core.Commit(tx, ct).ConfigureAwait(false);
    }

    /// <summary>Descarta un borrador manual. Uno emitido no se borra: se anula con una nota de crédito.</summary>
    public async Task Discard(long draftId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var draft = await LockManual(draftId, ct).ConfigureAwait(false);
        if (draft.IsIssued)
        {
            throw new ValidationException("Una factura emitida no se borra: se anula con una nota de crédito.");
        }

        db.Documents.Remove(draft);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("invoice.discard", Entity(draftId), ct: ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Paga con el saldo a favor de la cuenta las facturas y notas de débito pedidas, de la que vence antes a la que
    /// vence después, cada una con el saldo de su moneda. Sirve para el saldo que entró después de emitirlas, por
    /// ejemplo el que dejó una nota de crédito. Los documentos se bloquean antes que el saldo (sección 13 del modelo).
    /// </summary>
    public async Task<IReadOnlyList<AppliedCredit>> ApplyCredit(long accountId, IReadOnlyList<long> documentIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        var ids = documentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new ValidationException("Elegí al menos una factura.");
        }

        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "billing.documents", ids, ct).ConfigureAwait(false);
        var docs = await db.Documents.Where(d => ids.Contains(d.Id)).ToListAsync(ct).ConfigureAwait(false);
        if (docs.Count != ids.Count || docs.Any(d => d.AccountId != accountId || d.Kind == DocumentKind.CreditNote || !d.IsIssued))
        {
            throw new ValidationException("Hay documentos que no son de la cuenta, no están emitidos o no se pagan.");
        }

        var applied = new List<AppliedCredit>();
        foreach (var doc in docs.OrderBy(d => d.DueDate).ThenBy(d => d.Id))
        {
            var before = doc.Pending;
            if (before > 0m)
            {
                await credit.ApplyTo(doc, ct).ConfigureAwait(false);
            }

            if (before - doc.Pending is var amount and > 0m)
            {
                applied.Add(new AppliedCredit(doc.Id, doc.Currency.Code, amount));
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("credit.apply", AccountEntity(accountId), new { documents = applied.Select(a => new { document_id = a.DocumentId, amount = a.Amount }) }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return applied;
    }

    /// <summary>
    /// Ajuste de saldo a favor del staff: suma con un importe positivo y resta con uno negativo, nunca por debajo de
    /// cero. Queda un movimiento con el motivo y quién lo hizo, y una entrada en la auditoría sin el texto del motivo,
    /// que puede tener datos personales. Un movimiento nunca se edita: un error se corrige con otro ajuste.
    /// </summary>
    public async Task<long> AdjustCredit(long accountId, string currency, decimal amount, string reason, CancellationToken ct = default)
    {
        var code = ParseCurrency(currency);
        if (amount == 0m || !Rounding.HasAtMostDecimals(amount))
        {
            throw new ValidationException("El ajuste tiene que ser distinto de cero y con dos decimales como máximo.");
        }

        reason = reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > 200)
        {
            throw new ValidationException("El ajuste necesita un motivo de hasta 200 caracteres.");
        }

        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (!await db.Accounts.AnyAsync(a => a.Id == accountId, ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"No existe la cuenta {accountId}.");
        }

        await Sql.LockCreditBalance(db, accountId, code.Code, ct).ConfigureAwait(false);
        var balance = await db.CreditBalances.SingleAsync(b => b.AccountId == accountId && b.Currency == code, ct).ConfigureAwait(false);
        if (balance.Balance + amount < 0m)
        {
            throw new ValidationException(string.Create(CultureInfo.InvariantCulture, $"El saldo a favor en {code} es {balance.Balance:0.00}: no alcanza para restar {-amount:0.00}."));
        }

        balance.Balance += amount;
        var entry = new CreditEntry(accountId, code, amount, reason, time.GetUtcNow(), actor: Actor.Current.Id);
        db.CreditEntries.Add(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("credit.adjust", AccountEntity(accountId), new { entry_id = entry.Id, currency = code.Code, amount, balance = balance.Balance }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return entry.Id;
    }

    /// <summary>Exime a la cuenta del recargo por mora o de los recordatorios de vencimiento. Queda en la auditoría.</summary>
    public async Task SetExemptions(long accountId, bool noLateFees, bool noReminders, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la cuenta {accountId}.");
        account.NoLateFees = noLateFees;
        account.NoReminders = noReminders;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("account.billing-exemptions", AccountEntity(accountId), new { no_late_fees = noLateFees, no_reminders = noReminders }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Agrega renglones propios a un borrador, con el impuesto que corresponde al receptor de hoy. Valida que los
    /// servicios sean de la cuenta y que el total dé positivo.
    /// </summary>
    internal async Task AddLines(Document draft, IReadOnlyList<LineInput> lines, LineKind kind, CancellationToken ct, Func<int, string?>? dedupKey = null)
    {
        var serviceIds = lines.Where(l => l.ServiceId is not null).Select(l => l.ServiceId!.Value).Distinct().ToList();
        if (serviceIds.Count > 0 && await db.Services.CountAsync(s => serviceIds.Contains(s.Id) && s.AccountId == draft.AccountId, ct).ConfigureAwait(false) != serviceIds.Count)
        {
            throw new ValidationException("Hay servicios que no son de la cuenta.");
        }

        var receiver = await core.Receiver(draft.AccountId, ct).ConfigureAwait(false);
        AddTaxed(draft, lines, kind, await core.Taxes(ct).ConfigureAwait(false), receiver, core.Today(), dedupKey);
        if (Total(draft) <= 0m)
        {
            throw new ValidationException("El total tiene que dar más que cero.");
        }
    }

    /// <summary>
    /// La categoría de impuesto de un renglón propio según su tratamiento: gravado va como servicio digital, igual que un
    /// producto gravado, y exento y no gravado con las suyas. Las reglas de la cuenta y del receptor siguen valiendo.
    /// </summary>
    public static string Category(TaxTreatment treatment) => treatment switch
    {
        TaxTreatment.Exempt => TaxCategories.Exempt,
        TaxTreatment.NotTaxed => TaxCategories.Untaxed,
        _ => TaxCategories.Digital,
    };

    /// <summary>Los renglones con el impuesto que resuelve el motor para su categoría, el receptor y la fecha fiscal.</summary>
    internal static void AddTaxed(Document draft, IReadOnlyList<LineInput> lines, LineKind kind, TaxContext taxes, ReceiverSnapshot receiver, DateOnly on, Func<int, string?>? dedupKey = null)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var category = l.Category ?? Category(l.Treatment);
            var tax = taxes.Line(category, receiver, draft.AccountId, on);
            draft.AddLine(kind, l.Description.Trim(), l.Quantity, l.UnitPrice, tax.Treatment, tax.Rate, l.ServiceId, dedupKey?.Invoke(i), category, tax.Reason, tax.Legend);
        }
    }

    /// <summary>Lo que daría el documento con sus líneas. Un error del cálculo es un error del pedido.</summary>
    internal static decimal Total(Document doc)
    {
        try
        {
            return doc.Compute().Total;
        }
        catch (ArgumentException ex)
        {
            throw new ValidationException(ex.Message);
        }
    }

    /// <summary>
    /// El total con que se emitiría hoy: sus líneas más los tributos de comprobante que el motor le sumaría al emitir,
    /// como una percepción. Es lo que muestra un presupuesto.
    /// </summary>
    internal static decimal Total(Document doc, TaxContext taxes, ReceiverSnapshot receiver, DateOnly on) =>
        Total(doc) + taxes.DocumentTaxes(doc, receiver, on).Where(t => t.AddsToTotal).Sum(t => t.Amount);

    internal static void Validate(IReadOnlyList<LineInput>? lines)
    {
        if (lines is null || lines.Count == 0 || lines.Count > MaxLines)
        {
            throw new ValidationException($"Hace falta entre 1 y {MaxLines} renglones.");
        }

        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l.Description) || l.Description.Trim().Length > 500)
            {
                throw new ValidationException("Cada renglón necesita una descripción de hasta 500 caracteres.");
            }

            if (l.Quantity <= 0m || l.Quantity >= 1_000_000m || !Rounding.HasAtMostDecimals(l.Quantity, 4))
            {
                throw new ValidationException("La cantidad de cada renglón va de más de 0 a menos de un millón, con cuatro decimales como máximo.");
            }

            if (Math.Abs(l.UnitPrice) >= 1_000_000_000_000m || !Rounding.HasAtMostDecimals(l.UnitPrice, 6))
            {
                throw new ValidationException("El precio de cada renglón tiene seis decimales como máximo.");
            }

            if (!Enum.IsDefined(l.Treatment))
            {
                throw new ValidationException("Tratamiento de impuesto desconocido.");
            }
        }
    }

    public static Currency ParseCurrency(string code)
    {
        try
        {
            return new Currency(code?.Trim().ToUpperInvariant() ?? string.Empty);
        }
        catch (ArgumentException)
        {
            throw new ValidationException($"Moneda inválida: '{code}'.");
        }
    }

    internal async Task IssueAndApplyCredit(Document draft, CancellationToken ct)
    {
        await core.IssueDraft(draft, await core.Receiver(draft.AccountId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await credit.ApplyTo(draft, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static string Entity(long documentId) => string.Create(CultureInfo.InvariantCulture, $"document:{documentId}");

    private static string AccountEntity(long accountId) => string.Create(CultureInfo.InvariantCulture, $"account:{accountId}");

    private async Task<Document> LockManual(long draftId, CancellationToken ct)
    {
        await Sql.LockRows(db, "billing.documents", [draftId], ct).ConfigureAwait(false);
        var draft = await db.Documents.Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == draftId && d.Kind == DocumentKind.Invoice, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la factura {draftId}.");
        return draft.Reason == DocumentReasons.Manual || draft.IsIssued
            ? draft
            : throw new ValidationException("Solo se emiten o descartan a mano los borradores de facturas manuales.");
    }
}
