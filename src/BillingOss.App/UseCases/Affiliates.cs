using System.Globalization;
using System.Text.Json;
using BillingOss.App.Notifications;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Affiliates;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BillingOss.App.UseCases;

public static class AffiliateJobs
{
    public const string Order = "affiliates.order";
    public const string Paid = "affiliates.paid";
    public const string Review = "affiliates.review";
    public const string Release = "affiliates.release";
    public const string Monthly = "affiliates.monthly";
}

/// <summary>Saldo de un afiliado en una moneda: lo liberado disponible, lo que sigue en espera y lo ya cobrado.</summary>
public sealed record AffiliateBalance(Currency Currency, decimal Available, decimal Held, decimal Withdrawn = 0m);

/// <summary>
/// Programa de afiliados (diseno/afiliados.md): ajustes, reglas, altas, decisiones del staff, clics, atribuciones,
/// cobros y avisos. Lo usan el portal, el admin y la API propia.
/// </summary>
public sealed class AffiliateService(BillingDbContext db, AuditLog audit, TimeProvider time, Notifier notifier, BillingCore core)
{
    public Task<AffiliateProgram> Program(CancellationToken ct = default) => AffiliateData.Program(db, time, ct);

    /// <summary>
    /// Guarda los ajustes del programa. Los que se omiten quedan vacíos o apagados: el admin manda siempre todos.
    /// </summary>
    public async Task UpdateProgram(bool enabled, bool requiresApproval, int cookieDays, int holdDays, IReadOnlyDictionary<string, decimal> minimums,
        IReadOnlyDictionary<string, decimal>? bonuses = null, string? payoutNotifyEmail = null, string? links = null, bool monthlyReport = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(minimums);
        var program = await Program(ct).ConfigureAwait(false);
        Validated(() =>
        {
            program.SetDays(cookieDays, holdDays);
            program.SetMinimumPayouts(minimums);
            program.SetSignupBonuses(bonuses ?? new Dictionary<string, decimal>());
            program.SetPayoutNotifyEmail(payoutNotifyEmail);
            program.SetLinks(links);
        });
        program.Enabled = enabled;
        program.RequiresApproval = requiresApproval;
        program.MonthlyReport = monthlyReport;
        program.UpdatedAt = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("affiliates.program", "affiliates:program", new
        {
            enabled,
            requires_approval = requiresApproval,
            cookie_days = cookieDays,
            hold_days = holdDays,
            minimums,
            bonuses = program.Bonuses(),
            payout_notify = program.PayoutNotifyEmail is not null,
            links = program.Links is not null,
            monthly_report = monthlyReport,
        }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fija una regla de comisión: sin producto ni afiliado es la del programa. Reemplaza a las de su nivel que chocan
    /// con ella (sección 4 de diseno/afiliados.md).
    /// </summary>
    public async Task<long> SetRule(long? productId, long? affiliateId, CommissionKind kind, decimal value, Currency? currency, bool recurring, CancellationToken ct = default)
    {
        var rule = Validated(() => new CommissionRule(productId, affiliateId, kind, value, currency, recurring));
        if (productId is { } p && !await db.Products.AnyAsync(x => x.Id == p, ct).ConfigureAwait(false))
        {
            throw new ValidationException($"No existe el producto {p}.");
        }

        if (affiliateId is { } a && !await db.Set<Affiliate>().AnyAsync(x => x.Id == a, ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"No existe el afiliado {a}.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var level = await db.Set<CommissionRule>().Where(r => r.ProductId == productId && r.AffiliateId == affiliateId).ToListAsync(ct).ConfigureAwait(false);
        db.RemoveRange(CommissionRules.Replaced(level, productId, affiliateId, rule.Currency));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.Add(rule);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("affiliates.rule", string.Create(CultureInfo.InvariantCulture, $"affiliates:rule:{rule.Id}"),
            new { product_id = productId, affiliate_id = affiliateId, kind = kind.ToString(), value = rule.Value, currency = rule.Currency?.Code, recurring = rule.Recurring }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return rule.Id;
    }

    public async Task DeleteRule(long ruleId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var rule = await db.Set<CommissionRule>().SingleOrDefaultAsync(r => r.Id == ruleId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la regla {ruleId}.");
        db.Remove(rule);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("affiliates.rule_deleted", string.Create(CultureInfo.InvariantCulture, $"affiliates:rule:{ruleId}"), ct: ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>La cuenta se suma al programa. Si ya es afiliada, devuelve el afiliado que tiene.</summary>
    public async Task<Affiliate> Join(long accountId, CancellationToken ct = default)
    {
        if (await db.Set<Affiliate>().AsNoTracking().SingleOrDefaultAsync(a => a.AccountId == accountId, ct).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        var program = await Program(ct).ConfigureAwait(false);
        if (!program.Enabled)
        {
            throw new ValidationException("El programa de afiliados no está abierto.");
        }

        if (!await db.Accounts.AnyAsync(a => a.Id == accountId, ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"No existe la cuenta {accountId}.");
        }

        for (var attempt = 0; ; attempt++)
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var affiliate = new Affiliate(accountId, Affiliate.NewCode(), program.RequiresApproval, time.GetUtcNow());
            db.Add(affiliate);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                if (unique.ConstraintName == "ix_affiliates_code" && attempt < 5)
                {
                    continue;
                }

                // Otro pedido la sumó a la vez.
                db.ChangeTracker.Clear();
                return await db.Set<Affiliate>().AsNoTracking().SingleAsync(a => a.AccountId == accountId, ct).ConfigureAwait(false);
            }

            var bonus = affiliate.IsActive ? await GrantBonus(affiliate, program, ct).ConfigureAwait(false) : 0m;
            await audit.Record("affiliates.join", Entity(affiliate.Id), new { account_id = accountId, status = affiliate.Status.ToString(), bonus }, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return affiliate;
        }
    }

    /// <summary>Aprobar, rechazar, suspender o reactivar (sección 5 de diseno/afiliados.md).</summary>
    public async Task Decide(long affiliateId, AffiliateStatus status, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var affiliate = await Locked(affiliateId, ct).ConfigureAwait(false);
        var from = affiliate.Status;
        if (from == status)
        {
            return;
        }

        try
        {
            affiliate.MoveTo(status, time.GetUtcNow());
        }
        catch (InvalidOperationException)
        {
            throw new ValidationException($"El afiliado {affiliateId} está {from}: no puede pasar a {status}.");
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Pendiente y rechazado son los únicos estados de quien nunca estuvo activo: el bono va en la primera aprobación.
        var bonus = from is AffiliateStatus.Pending or AffiliateStatus.Rejected && affiliate.IsActive
            ? await GrantBonus(affiliate, await Program(ct).ConfigureAwait(false), ct).ConfigureAwait(false)
            : 0m;
        await audit.Record("affiliates.status", Entity(affiliateId), new { from = from.ToString(), to = status.ToString(), bonus }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Un clic en el enlace de un afiliado activo con el programa prendido: devuelve el afiliado y los días de la
    /// ventana para la cookie. Si el navegador ya traía la cookie del mismo afiliado (<paramref name="cookieAffiliate"/>)
    /// no suma otro clic. Un código que no existe o de un afiliado que no está activo da null.
    /// </summary>
    public async Task<(long AffiliateId, int CookieDays)?> Click(string code, string? referrerHost, long? cookieAffiliate, CancellationToken ct = default)
    {
        var program = await Program(ct).ConfigureAwait(false);
        var normalized = (code ?? string.Empty).Trim().ToLowerInvariant();
        if (!program.Enabled || normalized.Length is 0 or > 32)
        {
            return null;
        }

        var affiliate = await db.Set<Affiliate>().AsNoTracking().SingleOrDefaultAsync(a => a.Code == normalized, ct).ConfigureAwait(false);
        if (affiliate is not { IsActive: true })
        {
            return null;
        }

        if (affiliate.Id != cookieAffiliate)
        {
            db.Add(new AffiliateClick(affiliate.Id, time.GetUtcNow(), referrerHost));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return (affiliate.Id, program.CookieDays);
    }

    /// <summary>
    /// Atribuye la cuenta al afiliado desde la hora del clic. Gana el primer clic dentro de la ventana, el mismo
    /// afiliado la renueva y la propia cuenta del afiliado nunca se atribuye. Devuelve si quedó atribuida a él.
    /// </summary>
    public async Task<bool> Attribute(long accountId, long affiliateId, DateTimeOffset clickedAt, AttributionSource source, CancellationToken ct = default)
    {
        var program = await Program(ct).ConfigureAwait(false);
        if (!program.Enabled)
        {
            return false;
        }

        // Sin limpiar el contexto: lo llama también la página del pedido, que puede tener lo suyo en curso.
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var affiliate = await db.Set<Affiliate>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == affiliateId, ct).ConfigureAwait(false);
        if (affiliate is not { IsActive: true } || affiliate.AccountId == accountId || !await db.Accounts.AnyAsync(a => a.Id == accountId, ct).ConfigureAwait(false))
        {
            return false;
        }

        // Con la cuenta tomada, dos atribuciones de la misma cuenta a la vez no se pisan.
        await Sql.LockAccount(db, accountId, ct).ConfigureAwait(false);
        var current = await db.Set<AffiliateAttribution>().SingleOrDefaultAsync(a => a.AccountId == accountId, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (current is null)
        {
            db.Add(new AffiliateAttribution(accountId, affiliateId, clickedAt, source, now));
        }
        else if (current.TakesClick(affiliateId, clickedAt, program.CookieDays))
        {
            current.Set(affiliateId, clickedAt, source, now);
        }
        else
        {
            return current.AffiliateId == affiliateId;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Atribución que informa una integración por la API, con la hora del pedido como clic.</summary>
    public async Task<bool> AttributeByCode(long accountId, string code, CancellationToken ct = default)
    {
        var normalized = (code ?? string.Empty).Trim().ToLowerInvariant();
        var affiliate = await db.Set<Affiliate>().AsNoTracking().SingleOrDefaultAsync(a => a.Code == normalized, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el código de afiliado {code}.");
        return await Attribute(accountId, affiliate.Id, time.GetUtcNow(), AttributionSource.Api, ct).ConfigureAwait(false);
    }

    /// <summary>El staff asigna a mano un servicio a un afiliado.</summary>
    public async Task<long> AssignReferral(long affiliateId, long serviceId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var affiliate = await Locked(affiliateId, ct).ConfigureAwait(false);
        var service = await db.Services.AsNoTracking().SingleOrDefaultAsync(s => s.Id == serviceId, ct).ConfigureAwait(false)
            ?? throw new ValidationException($"No existe el servicio {serviceId}.");
        if (service.AccountId == affiliate.AccountId)
        {
            throw new ValidationException("Un servicio de la cuenta del afiliado no puede ser su referido.");
        }

        var orderId = service.OrderLineId is { } line
            ? await db.Set<OrderLine>().Where(l => l.Id == line).Select(l => (long?)l.OrderId).SingleOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        var referral = new AffiliateReferral(affiliateId, service.AccountId, serviceId, orderId, ReferralSource.Staff, time.GetUtcNow());
        db.Add(referral);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ValidationException($"El servicio {serviceId} ya es referido de un afiliado.");
        }

        await audit.Record("affiliates.referral", Entity(affiliateId), new { service_id = serviceId }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return referral.Id;
    }

    public async Task<IReadOnlyList<AffiliateBalance>> Balances(long affiliateId, CancellationToken ct = default) =>
        (await AffiliateData.Balances(db, [affiliateId], ct).ConfigureAwait(false)).GetValueOrDefault(affiliateId) ?? [];

    /// <summary>
    /// Pasa saldo del afiliado al saldo a favor de su cuenta, en la misma moneda y en el momento. Toma el lock del
    /// afiliado y después el del saldo de la cuenta, como cualquier movimiento de saldo (sección 13 del modelo).
    /// </summary>
    public async Task<long> ConvertToCredit(long affiliateId, Currency currency, decimal amount, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (affiliate, payout) = await Withdraw(affiliateId, PayoutMethod.Credit, currency, amount, null, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        await Sql.LockCreditBalance(db, affiliate.AccountId, currency.Code, ct).ConfigureAwait(false);
        var balance = await db.CreditBalances.SingleAsync(b => b.AccountId == affiliate.AccountId && b.Currency == currency, ct).ConfigureAwait(false);
        await db.Entry(balance).ReloadAsync(ct).ConfigureAwait(false);
        balance.Balance += amount;
        var entry = new CreditEntry(affiliate.AccountId, currency, amount, "Comisiones de afiliado", now);
        db.CreditEntries.Add(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        payout.CreditEntryId = entry.Id;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("affiliates.payout_credit", Entity(affiliateId), new { payout_id = payout.Id, currency = currency.Code, amount }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return payout.Id;
    }

    /// <summary>Pide un retiro: sale del saldo ahora y el staff lo marca pagado o lo rechaza.</summary>
    public async Task<long> RequestWithdrawal(long affiliateId, Currency currency, decimal amount, string details, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            throw new ValidationException("Faltan los datos para hacerte el pago.");
        }

        var program = await Program(ct).ConfigureAwait(false);
        var minimum = program.MinimumPayout(currency);
        if (amount < minimum)
        {
            throw new ValidationException(string.Create(CultureInfo.InvariantCulture, $"El retiro mínimo es de {minimum:0.00} {currency.Code}."));
        }

        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (affiliate, payout) = await Withdraw(affiliateId, PayoutMethod.Withdrawal, currency, amount, details.Trim(), ct).ConfigureAwait(false);
        await audit.Record("affiliates.payout_requested", Entity(affiliateId), new { payout_id = payout.Id, currency = currency.Code, amount }, ct).ConfigureAwait(false);

        // El aviso al equipo sale en la misma transacción, sin los datos de la transferencia: esos se ven en el admin.
        if (program.PayoutNotifyEmail is { } to)
        {
            var account = await db.Accounts.AsNoTracking().Where(a => a.Id == affiliate.AccountId).Select(a => a.Name).SingleAsync(ct).ConfigureAwait(false);
            await notifier.Queue(TemplateKeys.AffiliatePayoutRequested, to, null, new Dictionary<string, object?>
            {
                ["payout_id"] = payout.Id,
                ["affiliate_id"] = affiliateId,
                ["account_name"] = account,
                ["amount"] = Notifier.Money(amount, currency, TemplateRenderer.CultureFor(null)),
            }, $"{TemplateKeys.AffiliatePayoutRequested}:{payout.Id}", ct: ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return payout.Id;
    }

    public async Task MarkPayoutPaid(long payoutId, string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new ValidationException("Falta la referencia del pago.");
        }

        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "affiliates.payouts", [payoutId], ct).ConfigureAwait(false);
        var payout = await LockedPayout(payoutId, ct).ConfigureAwait(false);
        if (!Validated(() => payout.MarkPaid(reference.Trim(), time.GetUtcNow())))
        {
            return;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("affiliates.payout_paid", Entity(payout.AffiliateId), new { payout_id = payoutId }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Rechaza un retiro pedido: el importe vuelve al saldo, una sola vez.</summary>
    public async Task RejectPayout(long payoutId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ValidationException("Falta el motivo del rechazo.");
        }

        db.ChangeTracker.Clear();
        var affiliateId = await db.Set<AffiliatePayout>().Where(p => p.Id == payoutId).Select(p => (long?)p.AffiliateId).SingleOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el pago {payoutId}.");
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Locked(affiliateId, ct).ConfigureAwait(false);
        var payout = await LockedPayout(payoutId, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (!Validated(() => payout.Reject(reason.Trim(), now)))
        {
            return;
        }

        db.Add(new AffiliateLedgerEntry(affiliateId, payout.Currency, payout.Amount, LedgerKind.PayoutReturned, now, payoutId: payoutId));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("affiliates.payout_rejected", Entity(affiliateId), new { payout_id = payoutId }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Con el resumen mensual prendido, deja en la cola el del mes anterior para cada afiliado activo: clics, referidos
    /// nuevos, comisiones del mes y saldo. Lo importado no cuenta como del mes aunque no tenga fecha en el origen. La
    /// clave del aviso es por afiliado y mes: repetirlo no manda otro.
    /// </summary>
    public async Task<int> SendMonthlyReports(CancellationToken ct = default)
    {
        var program = await Program(ct).ConfigureAwait(false);
        if (!program.MonthlyReport)
        {
            return 0;
        }

        var today = core.Today();
        var end = new DateOnly(today.Year, today.Month, 1);
        var start = end.AddMonths(-1);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(core.Options.TimeZone);
        var (since, until) = (Instant(start), Instant(end));
        var affiliates = await (from a in db.Set<Affiliate>().AsNoTracking()
                                join c in db.Accounts on a.AccountId equals c.Id
                                where a.Status == AffiliateStatus.Active
                                orderby a.Id
                                select new { a.Id, a.AccountId, c.Name, c.Email, c.Language }).ToListAsync(ct).ConfigureAwait(false);
        var ids = affiliates.Select(a => a.Id).ToList();
        var clicks = await db.Set<AffiliateClick>().Where(c => ids.Contains(c.AffiliateId) && c.At >= since && c.At < until)
            .GroupBy(c => c.AffiliateId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct).ConfigureAwait(false);
        var signups = await db.Set<AffiliateReferral>().Where(r => ids.Contains(r.AffiliateId) && r.Source != ReferralSource.Import && r.CreatedAt >= since && r.CreatedAt < until)
            .GroupBy(r => r.AffiliateId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct).ConfigureAwait(false);
        var earned = await db.Set<AffiliateCommission>().Where(c => ids.Contains(c.AffiliateId) && !c.Imported && c.CreatedAt >= since && c.CreatedAt < until)
            .GroupBy(c => new { c.AffiliateId, c.Currency }).Select(g => new { g.Key.AffiliateId, g.Key.Currency, Amount = g.Sum(c => c.Amount - c.Reversed) })
            .ToListAsync(ct).ConfigureAwait(false);
        var balances = await AffiliateData.Balances(db, ids, ct).ConfigureAwait(false);
        var month = start.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var queued = 0;
        foreach (var a in affiliates)
        {
            var culture = TemplateRenderer.CultureFor(a.Language);
            var model = new Dictionary<string, object?>
            {
                ["account_name"] = a.Name,
                ["month"] = start.ToString(culture == "en" ? "MMMM yyyy" : "MMMM 'de' yyyy", CultureInfo.GetCultureInfo(culture)),
                ["clicks"] = clicks.GetValueOrDefault(a.Id),
                ["signups"] = signups.GetValueOrDefault(a.Id),
                ["earned"] = earned.Where(e => e.AffiliateId == a.Id && e.Amount > 0m).OrderBy(e => e.Currency.Code, StringComparer.Ordinal).Select(e => Notifier.Money(e.Amount, e.Currency, culture)).ToList(),
                ["balances"] = (balances.GetValueOrDefault(a.Id) ?? []).Select(b => new Dictionary<string, object?>
                {
                    ["available"] = Notifier.Money(b.Available, b.Currency, culture),
                    ["held"] = b.Held > 0m ? Notifier.Money(b.Held, b.Currency, culture) : null,
                }).ToList(),
            };
            if (await notifier.Queue(TemplateKeys.AffiliateMonthly, a.Email, a.Language, model, $"{TemplateKeys.AffiliateMonthly}:{a.Id}:{month}", a.AccountId, ct).ConfigureAwait(false))
            {
                queued++;
            }
        }

        return queued;

        DateTimeOffset Instant(DateOnly date) => TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), zone);
    }

    private static string Entity(long affiliateId) => string.Create(CultureInfo.InvariantCulture, $"affiliates:{affiliateId}");

    /// <summary>
    /// El bono de alta del programa en la moneda de la cuenta del afiliado, al saldo disponible. Lo llama la primera
    /// activación, con el afiliado tomado. El índice único del bono impide un segundo. Devuelve el importe acreditado.
    /// </summary>
    private async Task<decimal> GrantBonus(Affiliate affiliate, AffiliateProgram program, CancellationToken ct)
    {
        var currency = await db.Accounts.Where(a => a.Id == affiliate.AccountId).Select(a => a.Currency).SingleAsync(ct).ConfigureAwait(false);
        var bonus = program.SignupBonus(currency);
        if (bonus > 0m)
        {
            db.Add(new AffiliateLedgerEntry(affiliate.Id, currency, bonus, LedgerKind.Bonus, time.GetUtcNow()));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return bonus;
    }

    /// <summary>Lo común a los dos cobros, con el afiliado tomado: activo, importe válido y saldo suficiente.</summary>
    private async Task<(Affiliate Affiliate, AffiliatePayout Payout)> Withdraw(long affiliateId, PayoutMethod method, Currency currency, decimal amount, string? details, CancellationToken ct)
    {
        if (amount <= 0m || !Rounding.HasAtMostDecimals(amount))
        {
            throw new ValidationException("El importe tiene que ser positivo, con dos decimales como máximo.");
        }

        var affiliate = await Locked(affiliateId, ct).ConfigureAwait(false);
        if (!affiliate.IsActive)
        {
            throw new ValidationException("Solo un afiliado activo puede cobrar su saldo.");
        }

        var available = await db.Set<AffiliateLedgerEntry>().Where(e => e.AffiliateId == affiliateId && e.Currency == currency).SumAsync(e => e.Amount, ct).ConfigureAwait(false);
        if (amount > available)
        {
            throw new ValidationException(string.Create(CultureInfo.InvariantCulture, $"El saldo disponible es de {available:0.00} {currency.Code}."));
        }

        var now = time.GetUtcNow();
        var payout = new AffiliatePayout(affiliateId, method, currency, amount, details, now);
        db.Add(payout);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.Add(new AffiliateLedgerEntry(affiliateId, currency, -amount, LedgerKind.Payout, now, payoutId: payout.Id));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (affiliate, payout);
    }

    private async Task<Affiliate> Locked(long affiliateId, CancellationToken ct)
    {
        await Sql.LockRows(db, "affiliates.affiliates", [affiliateId], ct).ConfigureAwait(false);
        return await db.Set<Affiliate>().SingleOrDefaultAsync(a => a.Id == affiliateId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el afiliado {affiliateId}.");
    }

    private async Task<AffiliatePayout> LockedPayout(long payoutId, CancellationToken ct)
    {
        await Sql.LockRows(db, "affiliates.payouts", [payoutId], ct).ConfigureAwait(false);
        return await db.Set<AffiliatePayout>().SingleOrDefaultAsync(p => p.Id == payoutId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el pago {payoutId}.");
    }

    /// <summary>Las validaciones del dominio llegan al portal, al admin y a la API como errores de validación.</summary>
    private static T Validated<T>(Func<T> work)
    {
        try
        {
            return work();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new ValidationException(ex.Message.Split(" (Parameter", 2)[0]);
        }
    }

    private static void Validated(Action work) => Validated(() =>
    {
        work();
        return true;
    });
}

/// <summary>
/// Referidos y comisiones (secciones 3 y 7 de diseno/afiliados.md). Todo lo que toca las comisiones de una factura
/// toma primero el lock de la factura: el pago, la nota y la liberación de una misma factura van de a uno.
/// </summary>
public sealed class AffiliateCommissions(BillingDbContext db, BillingCore core, TimeProvider time, ILogger<AffiliateCommissions> logger)
{
    /// <summary>Se confirmó un pedido: sus servicios quedan referidos si la cuenta vino por un afiliado dentro de la ventana.</summary>
    public async Task OrderPlaced(long orderId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Refer([orderId], ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Una factura quedó paga: por cada servicio referido, con el afiliado activo y una regla que pague, deja una
    /// comisión en espera. Repetirlo no duplica (índice único de la comisión vigente por factura y servicio).
    /// </summary>
    public async Task<int> InvoicePaid(long documentId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "billing.documents", [documentId], ct).ConfigureAwait(false);
        var doc = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == documentId, ct).ConfigureAwait(false);
        var program = await AffiliateData.Program(db, time, ct).ConfigureAwait(false);

        // Si antes de correr se revirtió el pago, la factura ya no está paga: la comisión nace cuando se pague de nuevo.
        if (doc is not { Kind: DocumentKind.Invoice, IsIssued: true, Pending: 0m } || doc.Total - doc.Credited <= 0m || !program.Enabled)
        {
            return 0;
        }

        var lines = await db.DocumentLines.AsNoTracking()
            .Where(l => l.DocumentId == documentId && l.ServiceId != null)
            .Select(l => new { ServiceId = l.ServiceId!.Value, l.Kind, l.Net })
            .ToListAsync(ct).ConfigureAwait(false);
        var bases = CommissionRules.Bases(lines.Select(l => (l.ServiceId, l.Kind, l.Net)));
        if (bases.Count == 0)
        {
            return 0;
        }

        // El pago pudo entrar antes que el aviso del pedido: los referidos que falten salen de sus pedidos.
        var serviceIds = bases.Keys.ToList();
        var orders = await (from s in db.Services
                            join l in db.Set<OrderLine>() on s.OrderLineId equals l.Id
                            where serviceIds.Contains(s.Id)
                            select l.OrderId).Distinct().ToListAsync(ct).ConfigureAwait(false);
        await Refer(orders, ct).ConfigureAwait(false);

        var referrals = await db.Set<AffiliateReferral>().AsNoTracking().Where(r => serviceIds.Contains(r.ServiceId)).ToListAsync(ct).ConfigureAwait(false);
        if (referrals.Count == 0)
        {
            return 0;
        }

        var affiliateIds = referrals.Select(r => r.AffiliateId).Distinct().ToList();
        var active = await db.Set<Affiliate>().Where(a => affiliateIds.Contains(a.Id) && a.Status == AffiliateStatus.Active).Select(a => a.Id).ToListAsync(ct).ConfigureAwait(false);
        var products = await db.Services.Where(s => serviceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.ProductId, ct).ConfigureAwait(false);
        var rules = await db.Set<CommissionRule>().AsNoTracking()
            .Where(r => r.AffiliateId == null || affiliateIds.Contains(r.AffiliateId.Value)).ToListAsync(ct).ConfigureAwait(false);
        var referralIds = referrals.Select(r => r.Id).ToList();
        var live = await db.Set<AffiliateCommission>().AsNoTracking()
            .Where(c => referralIds.Contains(c.ReferralId) && c.Status != CommissionStatus.Reversed)
            .Select(c => new { c.ReferralId, c.DocumentId }).ToListAsync(ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var releaseOn = core.Today().AddDays(program.HoldDays);
        var created = 0;
        foreach (var referral in referrals.Where(r => active.Contains(r.AffiliateId)))
        {
            // Una regla de una sola vez paga si el referido no cobró antes, ni acá ni en el sistema anterior.
            var rule = CommissionRules.Resolve(rules, referral.AffiliateId, products[referral.ServiceId], doc.Currency);
            var once = rule is { Recurring: false } && (referral.PaidBefore is not null || live.Any(c => c.ReferralId == referral.Id));
            if (rule is null || once || live.Any(c => c.ReferralId == referral.Id && c.DocumentId == documentId)
                || rule.Amount(bases[referral.ServiceId], doc.Currency) is not { } amount || amount <= 0m)
            {
                continue;
            }

            // Una nota parcial anterior al pago, o la que lo completó, ya acreditó parte de la factura: se debe la
            // proporción, igual que si la nota llegara después (lo que hace la revisión).
            var due = AffiliateCommission.Target(amount, doc.Total, doc.Credited, doc.Pending);
            if (due <= 0m)
            {
                continue;
            }

            var commission = new AffiliateCommission(referral.AffiliateId, referral.Id, referral.ServiceId, documentId, doc.Currency, bases[referral.ServiceId], rule, amount, releaseOn, now);
            commission.ReduceTo(due, now);
            db.Add(commission);
            created++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return created;
    }

    /// <summary>Una nota de crédito: revisa las comisiones de la factura que corrige.</summary>
    public async Task CreditNoteIssued(long noteId, CancellationToken ct = default)
    {
        var invoiceId = await db.Documents.Where(d => d.Id == noteId && d.Kind == DocumentKind.CreditNote).Select(d => d.RelatedDocumentId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (invoiceId is { } id)
        {
            await Review(id, release: false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lleva cada comisión vigente de la factura a lo que se debe (sección 3 de diseno/afiliados.md). Lo revertido de
    /// una liberada sale del saldo. Con <paramref name="release"/>, libera después las que llegaron a su fecha.
    /// </summary>
    public async Task Review(long documentId, bool release, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "billing.documents", [documentId], ct).ConfigureAwait(false);
        var doc = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == documentId, ct).ConfigureAwait(false);
        var commissions = await db.Set<AffiliateCommission>().Where(c => c.DocumentId == documentId && c.Status != CommissionStatus.Reversed).ToListAsync(ct).ConfigureAwait(false);
        if (doc is null || commissions.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        var today = core.Today();
        foreach (var c in commissions)
        {
            var wasReleased = c.Status == CommissionStatus.Released;
            var reversed = c.ReduceTo(AffiliateCommission.Target(c.Amount, doc.Total, doc.Credited, doc.Pending), now);
            if (reversed > 0m)
            {
                logger.LogInformation("Comisión {Commission} del afiliado {Affiliate}: se revierten {Amount} por la factura {Document}", c.Id, c.AffiliateId, reversed, documentId);
            }

            if (reversed > 0m && wasReleased)
            {
                db.Add(new AffiliateLedgerEntry(c.AffiliateId, c.Currency, -reversed, LedgerKind.Reversal, now, c.Id));
            }

            if (release)
            {
                Release(c, today, now);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Libera las comisiones vencidas, factura por factura. Una que falla no frena a las demás.</summary>
    public async Task<int> ReleaseDue(CancellationToken ct = default)
    {
        var today = core.Today();
        var due = await db.Set<AffiliateCommission>().AsNoTracking()
            .Where(c => c.Status == CommissionStatus.Held && c.ReleaseOn <= today)
            .Select(c => new { c.Id, c.DocumentId }).ToListAsync(ct).ConfigureAwait(false);
        var documents = due.Where(c => c.DocumentId != null).Select(c => c.DocumentId!.Value).Distinct().Order().ToList();
        var errors = await Batch.Each(documents, id => Review(id, release: true, ct), logger, "la liberación de las comisiones de la factura").ConfigureAwait(false);

        // Las importadas sin factura no tienen nada que revisar: se liberan tal cual.
        var orphans = due.Where(c => c.DocumentId == null).Select(c => c.Id).Order().ToList();
        errors.AddRange(await Batch.Each(orphans, async id =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await Sql.LockRows(db, "affiliates.commissions", [id], ct).ConfigureAwait(false);
            var c = await db.Set<AffiliateCommission>().SingleAsync(x => x.Id == id, ct).ConfigureAwait(false);
            Release(c, today, time.GetUtcNow());
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }, logger, "la liberación de la comisión").ConfigureAwait(false));
        if (errors.Count > 0)
        {
            throw new AggregateException("La liberación de comisiones terminó con fallas.", errors);
        }

        return due.Count;
    }

    private void Release(AffiliateCommission c, DateOnly today, DateTimeOffset now)
    {
        if (c.Status == CommissionStatus.Held && c.ReleaseOn <= today && c.Release(now) is > 0m and var amount)
        {
            db.Add(new AffiliateLedgerEntry(c.AffiliateId, c.Currency, amount, LedgerKind.Release, now, c.Id));
        }
    }

    /// <summary>
    /// Deja referidos los servicios de los pedidos hechos dentro de la ventana de la atribución de su cuenta, con el
    /// afiliado activo. Un servicio que ya tiene referido lo conserva.
    /// </summary>
    private async Task Refer(IReadOnlyCollection<long> orderIds, CancellationToken ct)
    {
        if (orderIds.Count == 0)
        {
            return;
        }

        var program = await AffiliateData.Program(db, time, ct).ConfigureAwait(false);
        if (!program.Enabled)
        {
            return;
        }

        var orders = await db.Orders.AsNoTracking().Include(o => o.Lines).Where(o => orderIds.Contains(o.Id)).ToListAsync(ct).ConfigureAwait(false);
        var accounts = orders.Select(o => o.AccountId).Distinct().ToList();
        var attributions = await db.Set<AffiliateAttribution>().AsNoTracking().Where(a => accounts.Contains(a.AccountId)).ToDictionaryAsync(a => a.AccountId, ct).ConfigureAwait(false);
        var affiliateIds = attributions.Values.Select(a => a.AffiliateId).Distinct().ToList();
        var affiliates = await db.Set<Affiliate>().AsNoTracking().Where(a => affiliateIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        foreach (var order in orders)
        {
            if (!attributions.TryGetValue(order.AccountId, out var attribution) || !attribution.Covers(order.PlacedAt, program.CookieDays)
                || affiliates.GetValueOrDefault(attribution.AffiliateId) is not { IsActive: true } affiliate || affiliate.AccountId == order.AccountId)
            {
                continue;
            }

            var services = order.Lines.Where(l => l.ServiceId != null).Select(l => l.ServiceId!.Value).ToArray();
            var source = nameof(ReferralSource.Order);

            // El pedido y el pago pueden llegar a la vez en nodos distintos: el índice único del servicio decide.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO affiliates.referrals (affiliate_id, account_id, service_id, order_id, source, created_at)
                SELECT {affiliate.Id}, {order.AccountId}, s, {order.Id}, {source}, {now} FROM unnest({services}) AS s
                ON CONFLICT (service_id) DO NOTHING
                """, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Lecturas compartidas del programa, el portal, el admin y la API.</summary>
public static class AffiliateData
{
    /// <summary>La fila del programa. La primera vez se crea con los valores por defecto: apagado.</summary>
    public static async Task<AffiliateProgram> Program(BillingDbContext db, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        if (await db.Set<AffiliateProgram>().SingleOrDefaultAsync(ct).ConfigureAwait(false) is { } program)
        {
            return program;
        }

        var now = time.GetUtcNow();
        var defaults = new AffiliateProgram { UpdatedAt = now };
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO affiliates.program (id, enabled, requires_approval, cookie_days, hold_days, minimum_payouts, signup_bonuses, monthly_report, updated_at)
            VALUES ({AffiliateProgram.SingletonId}, {defaults.Enabled}, {defaults.RequiresApproval}, {defaults.CookieDays}, {defaults.HoldDays}, {defaults.MinimumPayouts}::jsonb,
                    {defaults.SignupBonuses}::jsonb, {defaults.MonthlyReport}, {now})
            ON CONFLICT (id) DO NOTHING
            """, ct).ConfigureAwait(false);
        return await db.Set<AffiliateProgram>().SingleAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Saldo disponible (el libro), en espera (las comisiones sin liberar) y cobrado (los pagos hechos, a saldo a favor o
    /// por retiro), por afiliado y moneda.
    /// </summary>
    public static async Task<Dictionary<long, IReadOnlyList<AffiliateBalance>>> Balances(BillingDbContext db, IReadOnlyCollection<long> affiliateIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var available = await db.Set<AffiliateLedgerEntry>().Where(e => affiliateIds.Contains(e.AffiliateId))
            .GroupBy(e => new { e.AffiliateId, e.Currency }).Select(g => new { g.Key.AffiliateId, g.Key.Currency, Amount = g.Sum(e => e.Amount) })
            .ToListAsync(ct).ConfigureAwait(false);
        var held = await db.Set<AffiliateCommission>().Where(c => affiliateIds.Contains(c.AffiliateId) && c.Status == CommissionStatus.Held)
            .GroupBy(c => new { c.AffiliateId, c.Currency }).Select(g => new { g.Key.AffiliateId, g.Key.Currency, Amount = g.Sum(c => c.Amount - c.Reversed) })
            .ToListAsync(ct).ConfigureAwait(false);
        var withdrawn = await db.Set<AffiliatePayout>().Where(p => affiliateIds.Contains(p.AffiliateId) && p.Status == PayoutStatus.Paid)
            .GroupBy(p => new { p.AffiliateId, p.Currency }).Select(g => new { g.Key.AffiliateId, g.Key.Currency, Amount = g.Sum(p => p.Amount) })
            .ToListAsync(ct).ConfigureAwait(false);
        return available.Concat(held).Concat(withdrawn).Select(a => (a.AffiliateId, a.Currency)).Distinct()
            .GroupBy(k => k.AffiliateId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AffiliateBalance>)[.. g.OrderBy(k => k.Currency.Code, StringComparer.Ordinal).Select(k => new AffiliateBalance(
                k.Currency,
                available.Where(a => a.AffiliateId == k.AffiliateId && a.Currency == k.Currency).Sum(a => a.Amount),
                held.Where(h => h.AffiliateId == k.AffiliateId && h.Currency == k.Currency).Sum(h => h.Amount),
                withdrawn.Where(w => w.AffiliateId == k.AffiliateId && w.Currency == k.Currency).Sum(w => w.Amount)))]);
    }
}

public sealed class AffiliateOrderHandler(AffiliateCommissions commissions) : IJobHandler
{
    public string Kind => AffiliateJobs.Order;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        await commissions.OrderPlaced(JsonDocument.Parse(job.Payload).RootElement.GetProperty("order_id").GetInt64(), ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

public sealed class AffiliatePaidHandler(AffiliateCommissions commissions) : IJobHandler
{
    public string Kind => AffiliateJobs.Paid;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        await commissions.InvoicePaid(JsonDocument.Parse(job.Payload).RootElement.GetProperty("document_id").GetInt64(), ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

/// <summary>Una nota de crédito o una imputación revertida: revisa las comisiones de la factura.</summary>
public sealed class AffiliateReviewHandler(AffiliateCommissions commissions) : IJobHandler
{
    public string Kind => AffiliateJobs.Review;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var payload = JsonDocument.Parse(job.Payload).RootElement;
        var documentId = payload.GetProperty("document_id").GetInt64();
        if (payload.TryGetProperty("event", out var name) && name.GetString() == Events.CreditNoteIssued)
        {
            await commissions.CreditNoteIssued(documentId, ct).ConfigureAwait(false);
        }
        else
        {
            await commissions.Review(documentId, release: false, ct).ConfigureAwait(false);
        }

        return JobResult.Done;
    }
}

public sealed class AffiliateReleaseHandler(AffiliateCommissions commissions) : IJobHandler
{
    public string Kind => AffiliateJobs.Release;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        await commissions.ReleaseDue(ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

/// <summary>El resumen del mes anterior a cada afiliado. El planificador lo encola el día 1.</summary>
public sealed class AffiliateMonthlyHandler(AffiliateService affiliates) : IJobHandler
{
    public string Kind => AffiliateJobs.Monthly;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        await affiliates.SendMonthlyReports(ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}
