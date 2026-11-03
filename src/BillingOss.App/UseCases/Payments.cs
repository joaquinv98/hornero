using System.Diagnostics;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BillingOss.App.UseCases;

/// <summary>
/// Un pago informado. Refunded es lo devuelto acumulado que informa la pasarela, si lo informa. Rate es la cotización
/// para imputar documentos de otra moneda (unidades de la moneda del pago por una del documento): la del intento, o
/// sin ella la vigente.
/// </summary>
public sealed record PaymentInput(
    long AccountId,
    string Channel,
    string? ExternalId,
    Currency Currency,
    decimal Amount,
    PaymentStatus Status,
    IReadOnlyList<long>? DocumentIds = null,
    decimal Fee = 0m,
    decimal? Refunded = null,
    decimal? Rate = null);

/// <summary>
/// Registrar un pago e imputarlo (sección 8 del modelo de dominio). Es idempotente por canal e ID externo, que es
/// obligatorio: el mismo pago informado dos veces entra una sola vez. Lo que sobra va a saldo a favor en la moneda
/// del pago. También saca de las cuentas lo que vuelve al pagador: reembolsos y contracargos.
/// </summary>
public sealed class PaymentService(BillingDbContext db, JobQueue jobs, BillingCore core, CreditService credit, ExchangeRateService rates, TimeProvider time, ILogger<PaymentService> logger)
{
    public async Task<long> Record(PaymentInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Sin ID externo no hay idempotencia: dos pagos distintos sin ID chocarían como si fueran el mismo.
        if (string.IsNullOrWhiteSpace(input.ExternalId))
        {
            throw new ValidationException("El pago necesita su ID externo.");
        }

        // Una cotización que no sirve no se imputa en silencio: cero o negativa mandaba la plata entera a saldo. Ocho
        // decimales, los que guardan el intento y la imputación.
        if (input.Rate is { } given && (given <= 0m || !Rounding.HasAtMostDecimals(given, 8)))
        {
            throw new ValidationException("La cotización tiene que ser positiva y con ocho decimales como máximo.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.Channel == input.Channel && p.ExternalId == input.ExternalId, ct).ConfigureAwait(false);

        if (payment is null)
        {
            payment = new Payment(input.AccountId, input.Channel, input.ExternalId, input.Currency, input.Amount, PaymentStatus.Pending, time.GetUtcNow(), input.Fee);
            db.Payments.Add(payment);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Otro proceso registró el mismo pago a la vez: se reintenta y se toma el que quedó.
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                return await Record(input, ct).ConfigureAwait(false);
            }
        }
        else if (payment.Amount != input.Amount || payment.Currency != input.Currency || payment.AccountId != input.AccountId)
        {
            throw new InvalidOperationException($"El pago {input.Channel}/{input.ExternalId} ya existe con otro monto, moneda o cuenta.");
        }

        await Sql.LockRows(db, "payments.payments", [payment.Id], ct).ConfigureAwait(false);
        await db.Entry(payment).ReloadAsync(ct).ConfigureAwait(false);
        var wasChargedBack = payment.Status == PaymentStatus.ChargedBack;

        // Un pago que el núcleo ve por primera vez ya en disputa, devuelto o contracargado pasó por aprobado: se imputa
        // igual, y después se saca lo que volvió al pagador.
        if (!payment.Collected && input.Status is not (PaymentStatus.Pending or PaymentStatus.Rejected) && payment.TryMoveTo(PaymentStatus.Approved))
        {
            await Allocate(payment, input.DocumentIds, input.Rate, ct).ConfigureAwait(false);
            await jobs.Publish(Events.PaymentReceived, new { payment_id = payment.Id }, ct: ct).ConfigureAwait(false);
        }

        payment.TryMoveTo(input.Status);
        if (!wasChargedBack && payment.Status == PaymentStatus.ChargedBack && payment.Kept > 0m)
        {
            await Return(payment, payment.Kept, RefundOrigin.Chargeback, ct).ConfigureAwait(false);
        }
        else if (input.Refunded is { } refunded && payment.Collected)
        {
            await GatewayRefunded(payment, refunded, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return payment.Id;
    }

    /// <summary>
    /// Reembolso del staff (sección 12.4 del modelo de dominio): en una transacción emite la nota de crédito, que deja
    /// lo ya pagado como saldo a favor, y encola el pedido de la plata a la pasarela. Si el pago no entró por una
    /// pasarela, el reembolso queda fallido y el importe sigue como saldo a favor para que lo resuelva el staff.
    /// </summary>
    public async Task<long> RequestRefund(long paymentId, long invoiceId, IReadOnlyList<CreditLine> lines, string reason, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "payments.payments", [paymentId], ct).ConfigureAwait(false);
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.Id == paymentId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el pago {paymentId}.");
        if (payment.Status is not (PaymentStatus.Approved or PaymentStatus.PartiallyRefunded))
        {
            throw new ValidationException($"El pago {paymentId} está {payment.Status}: solo se reembolsa uno aprobado.");
        }

        var invoice = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == invoiceId && d.Kind == DocumentKind.Invoice, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la factura {invoiceId}.");
        if (invoice.AccountId != payment.AccountId || invoice.Currency != payment.Currency)
        {
            throw new ValidationException("La factura no es de la cuenta o de la moneda del pago.");
        }

        var available = payment.Kept - await InFlight(payment.Id, ct).ConfigureAwait(false);
        var (note, returned) = await credit.Credit(invoiceId, lines, reason, ct).ConfigureAwait(false) ?? throw new UnreachableException();
        var amount = Math.Min(returned, available);
        if (amount <= 0m)
        {
            // La transacción no se confirma: tampoco queda la nota.
            throw new ValidationException("No hay nada pagado por este pago para devolver: para bajar una deuda alcanza con una nota de crédito.");
        }

        var now = time.GetUtcNow();
        var refund = new Refund(payment.Id, RefundOrigin.Staff, amount, now, note.Id);
        db.Refunds.Add(refund);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (GatewayPayments.InstanceOf(payment.Channel) is { } instanceId)
        {
            await jobs.Enqueue(
                JobKinds.PaymentRefund,
                new { refund_id = refund.Id },
                GatewayPayments.PaymentEntity(instanceId, payment.ExternalId!),
                $"{JobKinds.PaymentRefund}:{refund.Id}",
                ct: ct).ConfigureAwait(false);
        }
        else
        {
            refund.Fail("El pago no entró por una pasarela: la plata la devuelve el staff y mientras tanto queda como saldo a favor.", now);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await core.Commit(tx, ct).ConfigureAwait(false);
        return refund.Id;
    }

    /// <summary>La pasarela devolvió la plata de un reembolso del staff: sale del saldo a favor que dejó la nota.</summary>
    public async Task SettleRefund(long refundId, string? externalId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (refund, payment) = await LockRefund(refundId, ct).ConfigureAwait(false);
        if (refund.Status == RefundStatus.Requested)
        {
            if (refund.Amount > payment.Kept)
            {
                refund.Fail("El pago ya había vuelto al pagador por otra vía.", time.GetUtcNow());
            }
            else
            {
                await Settle(payment, refund, externalId, ct).ConfigureAwait(false);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>La pasarela no devolvió la plata: el importe sigue como saldo a favor y lo resuelve el staff.</summary>
    public async Task FailRefund(long refundId, string error, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (refund, _) = await LockRefund(refundId, ct).ConfigureAwait(false);
        if (refund.Status == RefundStatus.Requested)
        {
            logger.LogWarning("El reembolso {RefundId} falló y queda como saldo a favor: {Error}", refundId, error);
            refund.Fail(error, time.GetUtcNow());
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task<(Refund Refund, Payment Payment)> LockRefund(long refundId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var paymentId = await db.Refunds.Where(r => r.Id == refundId).Select(r => r.PaymentId).SingleAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "payments.payments", [paymentId], ct).ConfigureAwait(false);
        return (await db.Refunds.SingleAsync(r => r.Id == refundId, ct).ConfigureAwait(false),
            await db.Payments.SingleAsync(p => p.Id == paymentId, ct).ConfigureAwait(false));
    }

    /// <summary>Reembolsos del staff ya pedidos a la pasarela y sin respuesta: cuentan como devueltos para no repetirlos.</summary>
    private Task<decimal> InFlight(long paymentId, CancellationToken ct) =>
        db.Refunds.Where(r => r.PaymentId == paymentId && r.Status == RefundStatus.Requested).SumAsync(r => r.Amount, ct);

    /// <summary>
    /// La pasarela informa cuánto lleva devuelto el pago. Lo nuevo es primero un reembolso del staff que había fallado
    /// (por ejemplo, la plata se devolvió desde el panel de la pasarela) y el resto, un reembolso que el núcleo no pidió.
    /// </summary>
    private async Task GatewayRefunded(Payment payment, decimal reported, CancellationToken ct)
    {
        var delta = Math.Min(reported, payment.Amount) - payment.Refunded - await InFlight(payment.Id, ct).ConfigureAwait(false);
        if (delta <= 0m)
        {
            return;
        }

        var failed = await db.Refunds.Where(r => r.PaymentId == payment.Id && r.Status == RefundStatus.Failed).OrderBy(r => r.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var refund in failed)
        {
            if (refund.Amount > delta)
            {
                continue;
            }

            await Settle(payment, refund, null, ct).ConfigureAwait(false);
            delta -= refund.Amount;
        }

        if (delta > 0m)
        {
            await Return(payment, delta, RefundOrigin.Gateway, ct).ConfigureAwait(false);
        }
    }

    private async Task Settle(Payment payment, Refund refund, string? externalId, CancellationToken ct)
    {
        var uncovered = await Reverse(payment, refund.Amount, fromAllocations: false, ct).ConfigureAwait(false);
        payment.AddRefunded(refund.Amount);
        payment.TryMoveTo(payment.Kept == 0m ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded);
        refund.Complete(uncovered, time.GetUtcNow(), externalId);
        Warn(refund, uncovered);
    }

    /// <summary>Plata que volvió al pagador sin nota de crédito previa: reembolso desde la pasarela o contracargo.</summary>
    private async Task Return(Payment payment, decimal amount, RefundOrigin origin, CancellationToken ct)
    {
        var refund = new Refund(payment.Id, origin, amount, time.GetUtcNow());
        db.Refunds.Add(refund);
        var uncovered = await Reverse(payment, amount, fromAllocations: true, ct).ConfigureAwait(false);
        payment.AddRefunded(amount);
        refund.Complete(uncovered, time.GetUtcNow());
        Warn(refund, uncovered);
    }

    private void Warn(Refund refund, decimal uncovered)
    {
        if (uncovered > 0m)
        {
            logger.LogWarning("Del reembolso del pago {PaymentId} quedan {Uncovered} sin cubrir: el saldo ya se había consumido. Lo resuelve el staff.", refund.PaymentId, uncovered);
        }
    }

    /// <summary>
    /// Saca de las cuentas lo que volvió al pagador y devuelve lo que no pudo cubrir. Con imputaciones, en este orden:
    /// el saldo a favor que dejó el mismo pago (también lo que le devolvió una nota de crédito), sus imputaciones
    /// vigentes de la más nueva a la más vieja (la factura vuelve a deber) y el resto del saldo a favor de la cuenta.
    /// Sin imputaciones, solo el saldo a favor. Toma los locks en el orden de la sección 13: facturas y después saldo.
    /// </summary>
    private async Task<decimal> Reverse(Payment payment, decimal amount, bool fromAllocations, CancellationToken ct)
    {
        // Lo que esta transacción ya cambió se guarda antes de leer y recargar: si no, la recarga lo pisaría.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var allocations = fromAllocations
            ? await db.Allocations.Where(a => a.PaymentId == payment.Id && a.ReversedAt == null).OrderByDescending(a => a.Id).ToListAsync(ct).ConfigureAwait(false)
            : [];
        await Sql.LockRows(db, "billing.documents", allocations.Select(a => a.DocumentId), ct).ConfigureAwait(false);
        await Sql.LockCreditBalance(db, payment.AccountId, payment.Currency.Code, ct).ConfigureAwait(false);
        var balance = await db.CreditBalances.SingleAsync(b => b.AccountId == payment.AccountId && b.Currency == payment.Currency, ct).ConfigureAwait(false);
        await db.Entry(balance).ReloadAsync(ct).ConfigureAwait(false);
        var left = amount;

        if (fromAllocations)
        {
            // Solo el saldo en la moneda del pago: lo que una nota devolvió de una factura en otra moneda quedó en la suya.
            var own = await db.CreditEntries.Where(e => e.PaymentId == payment.Id && e.Currency == payment.Currency).SumAsync(e => e.Amount, ct).ConfigureAwait(false);
            Debit(Math.Min(own, balance.Balance));
        }

        foreach (var allocation in allocations)
        {
            var doc = await db.Documents.SingleAsync(d => d.Id == allocation.DocumentId, ct).ConfigureAwait(false);
            await db.Entry(doc).ReloadAsync(ct).ConfigureAwait(false);

            // Un importado puede tener imputaciones que no suman lo imputado: nunca se revierte más de lo que tiene. En
            // otra moneda, lo que vuelve del pago y lo que vuelve a deber el documento van en proporción.
            var share = allocation.PaymentShare;
            var take = Math.Min(allocation.Amount, doc.Allocated);
            var paid = take == allocation.Amount ? share : Rounding.Commercial(share * take / allocation.Amount);
            if (paid > left)
            {
                (paid, take) = (left, Rounding.Commercial(allocation.Amount * left / share));
            }

            if (take <= 0m || paid <= 0m)
            {
                continue;
            }

            var wasPaid = doc.Pending == 0m;
            if (allocation.ReverseUpTo(take, now, paid) is { } rest)
            {
                db.Allocations.Add(rest);
            }

            doc.ReverseAllocation(take);
            payment.Unapply(paid);
            await jobs.Publish(Events.AllocationReversed, new { document_id = doc.Id, payment_id = payment.Id }, $"document:{doc.Id}", ct).ConfigureAwait(false);

            left -= paid;
            if (wasPaid)
            {
                await RenewalsUnpaid(doc.Id, ct).ConfigureAwait(false);
            }
        }

        Debit(balance.Balance);
        return left;

        void Debit(decimal available)
        {
            var take = Math.Min(left, available);
            if (take <= 0m)
            {
                return;
            }

            balance.Balance -= take;
            db.CreditEntries.Add(new CreditEntry(payment.AccountId, payment.Currency, -take, "Devuelto al pagador", now, paymentId: payment.Id));
            payment.Unapply(take);
            left -= take;
        }
    }

    /// <summary>
    /// Una factura que estaba pagada vuelve a deber: lo pagado de sus servicios vuelve al inicio de cada período, y la
    /// mora sigue desde ahí (sección 5.2 del modelo de dominio).
    /// </summary>
    private async Task RenewalsUnpaid(long documentId, CancellationToken ct)
    {
        // ponytail: las facturas importadas no traen sus períodos y no mueven lo pagado. Si hace falta, retroceder un ciclo por línea de renovación.
        var periods = await db.BilledPeriods.Where(p => p.Line!.DocumentId == documentId && !p.Released).ToListAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "services.services", periods.Select(p => p.ServiceId), ct).ConfigureAwait(false);
        foreach (var period in periods)
        {
            var service = await db.Services.SingleAsync(s => s.Id == period.ServiceId, ct).ConfigureAwait(false);
            await db.Entry(service).ReloadAsync(ct).ConfigureAwait(false);
            service.RenewalReversed(period.Start);
        }
    }

    /// <summary>
    /// Imputa a los documentos pedidos, o a las facturas impagas más viejas de la moneda del pago. Un documento en otra
    /// moneda se imputa con <paramref name="rate"/>, o sin ella con la cotización vigente, y si no hay ninguna el pago no
    /// se registra y el pedido falla con el motivo. Lo que sobra va a saldo en la moneda del pago.
    /// </summary>
    private async Task Allocate(Payment payment, IReadOnlyList<long>? documentIds, decimal? rate, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var ids = documentIds ?? await db.Documents
            .Where(d => d.AccountId == payment.AccountId && d.Currency == payment.Currency && d.Kind != DocumentKind.CreditNote
                        && d.IssueDate != null && d.Total - d.Credited - d.Allocated > 0)
            .OrderBy(d => d.DueDate).ThenBy(d => d.Id)
            .Select(d => d.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        await Sql.LockRows(db, "billing.documents", ids, ct).ConfigureAwait(false);
        var paid = new List<Document>();

        // Lo imputado en cada moneda convertida, para redondear de forma acumulada: así el pago que cubre varias facturas
        // al cambio consume exactamente lo que cobró la pasarela, sin centavos sueltos.
        var converted = new Dictionary<Currency, decimal>();
        foreach (var id in ids)
        {
            if (payment.Unapplied <= 0m)
            {
                break;
            }

            var doc = await db.Documents.SingleAsync(d => d.Id == id, ct).ConfigureAwait(false);
            await db.Entry(doc).ReloadAsync(ct).ConfigureAwait(false);
            // Una nota de crédito no se paga: aunque venga su ID, lo que no va a una factura o nota de débito queda como saldo.
            if (doc.Kind == DocumentKind.CreditNote || doc.AccountId != payment.AccountId || !doc.IsIssued || doc.Pending <= 0m)
            {
                continue;
            }

            if (doc.Currency == payment.Currency)
            {
                var amount = Math.Min(doc.Pending, payment.Unapplied);
                doc.Allocate(amount);
                payment.Apply(amount);
                db.Allocations.Add(new Allocation(doc.Id, amount, 1m, now, paymentId: payment.Id));
            }
            else if ((rate ?? (await rates.Current(doc.Currency, payment.Currency, ct).ConfigureAwait(false))?.Factor) is { } factor)
            {
                // Primero el documento entero, con la misma cuenta acumulada que armó el intento: si alcanza, queda saldado
                // aunque la cuenta inversa no dé exacta, como pasa al cobrar pesos en dólares. Si no alcanza, lo que se pueda.
                var before = converted.GetValueOrDefault(doc.Currency);
                var amount = doc.Pending;
                var consumed = Consumed(amount);
                if (consumed > payment.Unapplied)
                {
                    amount = Math.Min(doc.Pending, Rounding.Commercial(payment.Unapplied / factor));
                    consumed = Consumed(amount);
                }

                while (amount > 0m && consumed > payment.Unapplied)
                {
                    amount -= 0.01m;
                    consumed = Consumed(amount);
                }

                if (amount <= 0m || consumed <= 0m)
                {
                    continue;
                }

                converted[doc.Currency] = before + amount;
                doc.Allocate(amount);
                payment.Apply(consumed);
                db.Allocations.Add(new Allocation(doc.Id, amount, Math.Round(amount / consumed, 8, MidpointRounding.AwayFromZero), now, paymentId: payment.Id, paymentAmount: consumed));

                decimal Consumed(decimal a) => Rounding.Commercial((before + a) * factor) - Rounding.Commercial(before * factor);
            }
            else
            {
                // Solo llega con documentos pedidos: el pago pidió imputar uno en otra moneda y no hay con qué convertir.
                throw new ValidationException($"No hay cotización entre {doc.Currency} y {payment.Currency} para imputar el documento {doc.Number}: mandá la cotización o cargá una vigente.");
            }

            await jobs.Publish(Events.PaymentAllocated, new { document_id = doc.Id, payment_id = payment.Id }, await FiscalEmissionHandler.EntityKey(db, doc, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            if (doc.Pending == 0m)
            {
                paid.Add(doc);
            }
        }

        if (payment.Unapplied > 0m)
        {
            await Sql.LockCreditBalance(db, payment.AccountId, payment.Currency.Code, ct).ConfigureAwait(false);
            var balance = await db.CreditBalances.SingleAsync(b => b.AccountId == payment.AccountId && b.Currency == payment.Currency, ct).ConfigureAwait(false);
            await db.Entry(balance).ReloadAsync(ct).ConfigureAwait(false);
            balance.Balance += payment.Unapplied;
            db.CreditEntries.Add(new CreditEntry(payment.AccountId, payment.Currency, payment.Unapplied, "Pago sin imputar", now, paymentId: payment.Id));
            payment.Apply(payment.Unapplied);
            await jobs.Enqueue(JobKinds.ReissueReleased, new { account_id = payment.AccountId }, ct: ct).ConfigureAwait(false);
        }

        // Las pagas al final, después del saldo: es el orden de locks de la sección 13 del modelo.
        foreach (var doc in paid)
        {
            await core.InvoicePaid(doc, ct).ConfigureAwait(false);
        }
    }
}
