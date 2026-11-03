using System.Globalization;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.UseCases;

/// <summary>Sin moneda, la de la cuenta. Sin fecha, la corrida de hoy lo factura.</summary>
public sealed record BillableItemInput(string Description, decimal Quantity, decimal UnitPrice, TaxTreatment Treatment = TaxTreatment.Taxed, long? ServiceId = null, string? Currency = null, DateOnly? InvoiceOn = null);

/// <summary>
/// Ítems facturables sueltos (sección 6 del modelo de dominio): se cargan, se cancelan mientras están pendientes y se
/// facturan una sola vez, con la corrida de su día (que los suma al borrador de la cuenta en su moneda) o en el momento.
/// </summary>
public sealed class BillableItemService(BillingDbContext db, BillingCore core, InvoiceService invoices, AuditLog audit, TimeProvider time)
{
    public async Task<long> Add(long accountId, BillableItemInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        InvoiceService.Validate([new LineInput(input.Description, input.Quantity, input.UnitPrice, input.Treatment, input.ServiceId)]);
        if (Domain.Shared.Rounding.Commercial(input.Quantity * input.UnitPrice) <= 0m)
        {
            throw new ValidationException("Un ítem facturable tiene que sumar más que cero: un descuento va en una factura manual o en una nota de crédito.");
        }

        var today = core.Today();
        if (input.InvoiceOn is { } on && on < today)
        {
            throw new ValidationException("La fecha de facturación no puede ser anterior a hoy.");
        }

        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la cuenta {accountId}.");
        if (input.ServiceId is { } serviceId && !await db.Services.AnyAsync(s => s.Id == serviceId && s.AccountId == accountId, ct).ConfigureAwait(false))
        {
            throw new ValidationException("El servicio no es de la cuenta.");
        }

        var currency = input.Currency is null ? account.Currency : InvoiceService.ParseCurrency(input.Currency);
        var item = new BillableItem(accountId, input.Description.Trim(), input.Quantity, input.UnitPrice, currency, input.Treatment, input.InvoiceOn ?? today, time.GetUtcNow(), input.ServiceId, Actor.Current.Id);
        db.BillableItems.Add(item);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("billable-item.add", Entity(item.Id), new { account_id = accountId }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return item.Id;
    }

    public async Task Cancel(long itemId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "billing.billable_items", [itemId], ct).ConfigureAwait(false);
        var item = await db.BillableItems.SingleOrDefaultAsync(i => i.Id == itemId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el ítem {itemId}.");
        if (item.Status != BillableItemStatus.Pending)
        {
            throw new ValidationException("El ítem ya no está pendiente: uno facturado se corrige con una nota de crédito.");
        }

        item.Cancel(time.GetUtcNow());
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("billable-item.cancel", Entity(itemId), ct: ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Factura ya los ítems pendientes pedidos, o todos los de la cuenta, aunque su fecha sea posterior: una factura por
    /// moneda, emitida enseguida y con el saldo a favor aplicado. Con la cuenta tomada, como la corrida.
    /// </summary>
    public async Task<IReadOnlyList<long>> InvoiceNow(long accountId, IReadOnlyList<long>? itemIds = null, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockAccount(db, accountId, ct).ConfigureAwait(false);
        var ids = itemIds?.Distinct().ToList()
            ?? await db.BillableItems.Where(i => i.AccountId == accountId && i.Status == BillableItemStatus.Pending).Select(i => i.Id).ToListAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "billing.billable_items", ids, ct).ConfigureAwait(false);
        var items = await db.BillableItems.Where(i => ids.Contains(i.Id) && i.AccountId == accountId && i.Status == BillableItemStatus.Pending).OrderBy(i => i.Id).ToListAsync(ct).ConfigureAwait(false);
        if (items.Count != ids.Count)
        {
            throw new ValidationException("Hay ítems que no son de la cuenta o ya no están pendientes.");
        }

        if (items.Count == 0)
        {
            return [];
        }

        var today = core.Today();
        var receiver = await core.Receiver(accountId, ct).ConfigureAwait(false);
        var taxes = await core.Taxes(ct).ConfigureAwait(false);
        var created = new List<long>();
        foreach (var group in items.GroupBy(i => i.Currency))
        {
            var draft = new Document(DocumentKind.Invoice, accountId, group.Key, taxes.Settings.DefaultPriceMode(group.Key), today.AddDays(core.Options.InvoiceDueDays), time.GetUtcNow(), reason: DocumentReasons.Manual);
            AddTo(draft, group, receiver, taxes, today);
            db.Documents.Add(draft);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await invoices.IssueAndApplyCredit(draft, ct).ConfigureAwait(false);
            group.ToList().ForEach(i => i.MarkInvoiced(draft.Id, time.GetUtcNow()));
            created.Add(draft.Id);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("billable-item.invoice", string.Create(CultureInfo.InvariantCulture, $"account:{accountId}"), new { items = ids, documents = created }, ct).ConfigureAwait(false);
        await core.Commit(tx, ct).ConfigureAwait(false);
        return created;
    }

    /// <summary>Las líneas de los ítems, con el impuesto del receptor del día y la clave que impide facturarlos dos veces.</summary>
    internal static void AddTo(Document draft, IEnumerable<BillableItem> items, ReceiverSnapshot receiver, TaxContext taxes, DateOnly on)
    {
        foreach (var item in items)
        {
            var category = InvoiceService.Category(item.Treatment);
            var tax = taxes.Line(category, receiver, draft.AccountId, on);
            draft.AddLine(LineKind.Item, item.Description, item.Quantity, item.UnitPrice, tax.Treatment, tax.Rate, item.ServiceId, item.DedupKey, category, tax.Reason, tax.Legend);
        }
    }

    private static string Entity(long itemId) => string.Create(CultureInfo.InvariantCulture, $"billable-item:{itemId}");
}
