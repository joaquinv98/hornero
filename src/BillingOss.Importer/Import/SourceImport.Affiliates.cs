using System.Globalization;
using BillingOss.App.UseCases;
using BillingOss.Domain.Affiliates;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Importer.Import;

/// <summary>
/// Programa de afiliados del origen (sección 11 de diseno/afiliados.md). El mapeo sale de la estructura de la copia:
/// afiliados con su ID como código, referidos por servicio, comisiones pagadas y en espera, retiros, clics y los ajustes.
/// </summary>
public sealed partial class SourceImport
{
    /// <summary>Tablas de afiliados que se comparan contra sus filas del origen en la conciliación.</summary>
    public static IReadOnlySet<string> AffiliateRows { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "tblaffiliates", "tblaffiliatesaccounts", "tblaffiliateshistory", "tblaffiliatespending", "tblaffiliateswithdrawals",
    };

    private static readonly HashSet<string> AffiliateTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "tblaffiliates", "tblaffiliatesaccounts", "tblaffiliateshistory", "tblaffiliatespending", "tblaffiliateswithdrawals",
        "tblaffiliates_hits", "tblaffiliates_referrers",
    };

    private async Task Affiliates(Dictionary<long, Currency> currencies, CancellationToken ct)
    {
        var at = now;
        await AffiliateProgram(currencies, ct).ConfigureAwait(false);

        // Afiliados: activos, con su ID y el ID como código, así el enlace viejo se redirige a /r/{ID}.
        var affiliates = new Dictionary<long, (long Account, Currency Currency)>();
        foreach (var r in Rows("tblaffiliates"))
        {
            var id = Long(r, "id");
            var account = Long(r, "clientid");
            if (!accountCurrency.TryGetValue(account, out var currency))
            {
                Incidents.Add($"Afiliado {id}: la cuenta {account} no existe, no se importa.");
                continue;
            }

            db.Add(Affiliate.FromImport(id, account, id.ToString(CultureInfo.InvariantCulture), Instant(Date(r, "date")) ?? at));
            affiliates[id] = (account, currency);
            Count("affiliate_rows", "tblaffiliates");
            if (Rule(Str(r, "paytype"), Dec(r, "payamount"), Long(r, "onetime") == 1, $"Afiliado {id}") is { } rule)
            {
                db.Add(new CommissionRule(null, id, rule.Kind, rule.Value, null, rule.Recurring));
            }
        }

        await Flush(ct).ConfigureAwait(false);

        // Referidos: el servicio, su cuenta y la última comisión que pagó el origen.
        var serviceAccounts = Rows("tblhosting").Where(r => services.Contains(Long(r, "id"))).ToDictionary(r => Long(r, "id"), r => (Account: Long(r, "userid"), Created: Instant(Date(r, "regdate"))));
        var referrals = new Dictionary<long, (long Affiliate, long Service)>();
        foreach (var r in Rows("tblaffiliatesaccounts"))
        {
            var id = Long(r, "id");
            var affiliateId = Long(r, "affiliateid");
            var serviceId = Long(r, "relid");
            if (!affiliates.TryGetValue(affiliateId, out var affiliate) || !serviceAccounts.TryGetValue(serviceId, out var service))
            {
                Incidents.Add($"Referido {id}: el afiliado {affiliateId} o el servicio {serviceId} no se importó, queda afuera.");
                continue;
            }

            if (service.Account == affiliate.Account)
            {
                Incidents.Add($"Referido {id}: el servicio {serviceId} es de la cuenta del propio afiliado {affiliateId}, queda afuera.");
                continue;
            }

            var referral = new AffiliateReferral(affiliateId, service.Account, serviceId, null, ReferralSource.Import, service.Created ?? at, Date(r, "lastpaid"));
            referral.SetImportedId(id);
            db.Add(referral);
            referrals[id] = (affiliateId, serviceId);
            Count("affiliate_rows", "tblaffiliatesaccounts");
        }

        await Flush(ct).ConfigureAwait(false);

        // Comisiones pagadas (liberadas) y en espera, cada una en la moneda de la cuenta del afiliado.
        var released = new List<AffiliateCommission>();
        foreach (var r in Rows("tblaffiliateshistory"))
        {
            if (Commission(r, referrals, affiliates, Date(r, "date"), held: false, "tblaffiliateshistory") is { } c)
            {
                released.Add(c);
            }
        }

        foreach (var r in Rows("tblaffiliatespending"))
        {
            Commission(r, referrals, affiliates, Date(r, "clearingdate"), held: true, "tblaffiliatespending");
        }

        await Flush(ct).ConfigureAwait(false);
        foreach (var c in released)
        {
            db.Add(new AffiliateLedgerEntry(c.AffiliateId, c.Currency, c.Amount, LedgerKind.Import, at, c.Id));
        }

        // Retiros: pagados en su fecha.
        var withdrawals = new List<AffiliatePayout>();
        foreach (var r in Rows("tblaffiliateswithdrawals"))
        {
            var affiliateId = Long(r, "affiliateid");
            var amount = Dec(r, "amount");
            if (!affiliates.TryGetValue(affiliateId, out var affiliate) || amount <= 0m)
            {
                Incidents.Add($"Retiro {Long(r, "id")}: el afiliado {affiliateId} no se importó o el importe no es positivo, queda afuera.");
                continue;
            }

            var payout = AffiliatePayout.FromImport(affiliateId, affiliate.Currency, amount, Instant(Date(r, "date")) ?? at);
            db.Add(payout);
            withdrawals.Add(payout);
            Count("affiliate_rows", "tblaffiliateswithdrawals");
        }

        await Flush(ct).ConfigureAwait(false);
        foreach (var p in withdrawals)
        {
            db.Add(new AffiliateLedgerEntry(p.AffiliateId, p.Currency, -p.Amount, LedgerKind.Import, at, payoutId: p.Id));
        }

        await Flush(ct).ConfigureAwait(false);
        await AffiliateBalances(affiliates, ct).ConfigureAwait(false);
        await AffiliateClicks(affiliates, ct).ConfigureAwait(false);
        await log.WriteLineAsync($"Afiliados: {affiliates.Count}, referidos {referrals.Count}, comisiones {released.Count}").ConfigureAwait(false);
    }

    /// <summary>Ajustes del programa: prendido, regla en porcentaje y retiro mínimo en la moneda por defecto.</summary>
    private async Task AffiliateProgram(Dictionary<long, Currency> currencies, CancellationToken ct)
    {
        var settings = Rows("tblconfiguration").GroupBy(r => Str(r, "setting") ?? string.Empty).ToDictionary(g => g.Key, g => Str(g.Last(), "value") ?? string.Empty, StringComparer.Ordinal);
        var program = await AffiliateData.Program(db, TimeProvider.System, ct).ConfigureAwait(false);
        program.Enabled = settings.GetValueOrDefault("AffiliateEnabled") == "on";
        program.UpdatedAt = now;
        var defaultCurrency = Rows("tblcurrencies").Where(r => Long(r, "default") == 1).Select(r => currencies.GetValueOrDefault(Long(r, "id"))).FirstOrDefault(c => c.Code is not null);
        if (decimal.TryParse(settings.GetValueOrDefault("AffiliatePayout"), NumberStyles.Number, CultureInfo.InvariantCulture, out var minimum) && minimum > 0m && defaultCurrency.Code is not null)
        {
            program.SetMinimumPayouts(new Dictionary<string, decimal> { [defaultCurrency.Code] = minimum });
        }

        if (decimal.TryParse(settings.GetValueOrDefault("AffiliateEarningPercent"), NumberStyles.Number, CultureInfo.InvariantCulture, out var percent) && percent > 0m)
        {
            db.Add(new CommissionRule(null, null, CommissionKind.Percent, percent, null, recurring: true));
        }

        // La espera de las comisiones, en días. La ventana del enlace no tiene ajuste en el origen y queda la de fábrica.
        if (int.TryParse(settings.GetValueOrDefault("AffiliatesDelayCommission"), NumberStyles.None, CultureInfo.InvariantCulture, out var delay))
        {
            if (delay <= 365)
            {
                program.SetDays(program.CookieDays, delay);
            }
            else
            {
                Incidents.Add($"Afiliados: la espera de {delay} días del origen pasa el tope de 365, queda la de {program.HoldDays}.");
            }
        }

        if (decimal.TryParse(settings.GetValueOrDefault("AffiliateBonusDeposit"), NumberStyles.Number, CultureInfo.InvariantCulture, out var bonus) && bonus > 0m)
        {
            if (defaultCurrency.Code is not null && Rounding.HasAtMostDecimals(bonus))
            {
                program.SetSignupBonuses(new Dictionary<string, decimal> { [defaultCurrency.Code] = bonus });
            }
            else
            {
                Incidents.Add($"Afiliados: el bono de alta de {bonus.ToString(CultureInfo.InvariantCulture)} del origen no tiene moneda por defecto o pasa los centavos, no se importa.");
            }
        }

        program.MonthlyReport = settings.GetValueOrDefault("SendAffiliateReportMonthly") == "on";
        if (Blank(settings.GetValueOrDefault("AffiliateLinks")) is not null)
        {
            Incidents.Add("Afiliados: el origen tenía material para compartir con su propia marca del enlace. Revisalo y cargalo en el programa con {enlace}.");
        }

        // El origen abría un pedido de soporte por cada retiro. Acá el aviso va por correo a la casilla del programa.
        if (Blank(settings.GetValueOrDefault("AffiliateDepartment")) is { } department && department != "0")
        {
            Incidents.Add($"Afiliados: el origen mandaba los retiros pedidos al departamento {department}. Cargá en el programa la casilla que tiene que recibir el aviso.");
        }

        // Reglas por producto: sin comisión, o un porcentaje propio.
        foreach (var (id, r) in productRows.Where(p => products.Contains(p.Key)))
        {
            if (Rule(Str(r, "affiliatepaytype"), Dec(r, "affiliatepayamount"), Long(r, "affiliateonetime") == 1, $"Producto {id}") is { } rule)
            {
                db.Add(new CommissionRule(id, null, rule.Kind, rule.Value, null, rule.Recurring));
            }
        }

        await Flush(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// La regla propia de un afiliado o de un producto del origen. Vacía usa la del programa. "none" es sin comisión y
    /// "percentage" un porcentaje. Otro tipo sale como incidencia y queda con la regla del programa.
    /// </summary>
    private (CommissionKind Kind, decimal Value, bool Recurring)? Rule(string? type, decimal amount, bool once, string who)
    {
        switch (Blank(type)?.ToLowerInvariant())
        {
            case null:
                return null;
            case "none":
                return (CommissionKind.None, 0m, false);
            case "percentage" when amount is > 0m and <= 100m:
                return (CommissionKind.Percent, amount, !once);
            default:
                Incidents.Add($"{who}: comisión de tipo '{type}' con {amount.ToString(CultureInfo.InvariantCulture)}, que no se traduce: queda con la regla del programa.");
                return null;
        }
    }

    private AffiliateCommission? Commission(DumpRow r, Dictionary<long, (long Affiliate, long Service)> referrals, Dictionary<long, (long Account, Currency Currency)> affiliates, DateOnly? date, bool held, string table)
    {
        var referralId = Long(r, "affaccid");
        var amount = Dec(r, "amount");
        if (amount == 0m)
        {
            // Una comisión en cero no tiene nada que importar: cuenta como fila vista.
            Incidents.Add($"Comisión {Long(r, "id")} de {table}: importe cero, no se importa.");
            Count("affiliate_rows", table);
            return null;
        }

        if (!referrals.TryGetValue(referralId, out var referral) || amount < 0m || !Rounding.HasAtMostDecimals(amount))
        {
            Incidents.Add($"Comisión {Long(r, "id")} de {table}: el referido {referralId} no se importó o el importe {amount.ToString(CultureInfo.InvariantCulture)} no sirve, queda afuera (el saldo se ajusta aparte).");
            return null;
        }

        var affiliateId = referral.Affiliate;
        var invoiceId = Long(r, "invoice_id");
        var commission = AffiliateCommission.FromImport(affiliateId, referralId, referral.Service, invoices.ContainsKey(invoiceId) ? invoiceId : null, affiliates[affiliateId].Currency, amount, date ?? AsOf, held, Instant(date) ?? now);
        db.Add(commission);
        Count("affiliate_rows", table);
        return commission;
    }

    /// <summary>
    /// El saldo del libro de cada afiliado tiene que dar el del origen. Si no, un movimiento cubre la diferencia. Lo
    /// cobrado sale de los retiros importados: si no da con el total retirado del origen, es una incidencia.
    /// </summary>
    private async Task AffiliateBalances(Dictionary<long, (long Account, Currency Currency)> affiliates, CancellationToken ct)
    {
        var ledger = await db.Set<AffiliateLedgerEntry>().GroupBy(e => e.AffiliateId).Select(g => new { g.Key, Sum = g.Sum(e => e.Amount) }).ToDictionaryAsync(g => g.Key, g => g.Sum, ct).ConfigureAwait(false);
        var paid = await db.Set<AffiliatePayout>().GroupBy(p => p.AffiliateId).Select(g => new { g.Key, Sum = g.Sum(p => p.Amount) }).ToDictionaryAsync(g => g.Key, g => g.Sum, ct).ConfigureAwait(false);
        foreach (var r in Rows("tblaffiliates"))
        {
            var id = Long(r, "id");
            if (!affiliates.TryGetValue(id, out var affiliate))
            {
                continue;
            }

            var withdrawn = Dec(r, "withdrawn");
            if (withdrawn != paid.GetValueOrDefault(id))
            {
                Incidents.Add($"Afiliado {id}: el origen dice que retiró {withdrawn.ToString(CultureInfo.InvariantCulture)} y sus retiros suman {paid.GetValueOrDefault(id).ToString(CultureInfo.InvariantCulture)}. Lo cobrado muestra los retiros.");
            }

            var balance = Dec(r, "balance");
            var difference = balance - ledger.GetValueOrDefault(id);
            Count("affiliate_balance_by_currency", affiliate.Currency.Code, balance);
            if (difference != 0m)
            {
                db.Add(new AffiliateLedgerEntry(id, affiliate.Currency, difference, LedgerKind.Import, now));
                Incidents.Add($"Afiliado {id}: el saldo del origen ({balance.ToString(CultureInfo.InvariantCulture)}) no da con sus comisiones y retiros, un movimiento de importación cubre {difference.ToString(CultureInfo.InvariantCulture)}.");
            }
        }

        await Flush(ct).ConfigureAwait(false);
    }

    /// <summary>Clics con su fecha y solo el host del sitio de origen.</summary>
    private async Task AffiliateClicks(Dictionary<long, (long Account, Currency Currency)> affiliates, CancellationToken ct)
    {
        var hosts = Rows("tblaffiliates_referrers").ToDictionary(r => Long(r, "id"), r => Uri.TryCreate(Str(r, "referrer"), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.Host.ToLowerInvariant() : null);
        var orphans = 0;
        var pending = 0;
        foreach (var r in Rows("tblaffiliates_hits"))
        {
            var affiliateId = Long(r, "affiliate_id");
            if (!affiliates.ContainsKey(affiliateId))
            {
                orphans++;
                continue;
            }

            db.Add(new AffiliateClick(affiliateId, Instant(DateTimeValue(r, "created_at")) ?? now, hosts.GetValueOrDefault(Long(r, "referrer_id"))));
            if (++pending >= 5000)
            {
                await Flush(ct).ConfigureAwait(false);
                pending = 0;
            }
        }

        await Flush(ct).ConfigureAwait(false);
        if (orphans > 0)
        {
            Incidents.Add($"Afiliados: {orphans} clics de afiliados que ya no están en el origen quedan afuera.");
        }
    }
}
