using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BillingOss.App;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static BillingOss.Importer.Import.SourceImport;

namespace BillingOss.Importer.Import;

/// <summary>Clase de una diferencia del día.</summary>
public enum DifferenceClass
{
    /// <summary>Esperada por diseño: una entrada del día o una regla distinta a propósito.</summary>
    Expected,

    /// <summary>Esperada, pero alguien la tiene que mirar antes del corte: el día del corte billing-oss la va a hacer.</summary>
    Review,

    /// <summary>Defecto: billing-oss hizo otra cosa que el origen sin una regla que lo explique.</summary>
    Defect,
}

/// <summary>
/// Una diferencia del día entre el origen y billing-oss. Lleva solo IDs, fechas, importes y estados, nunca datos
/// personales. Cada una tiene su clase y su motivo.
/// </summary>
public sealed record Difference(DifferenceClass Class, string Kind, long? ServiceId, long? SourceInvoiceId, long? OwnDocumentId, string Field, string Source, string Own, string Reason)
{
    public bool Error => Class == DifferenceClass.Defect;

    /// <summary>Nombre de la clase en el informe.</summary>
    public string ClassName => Class switch { DifferenceClass.Defect => "defecto", DifferenceClass.Review => "a revisar", _ => "esperada" };
}

/// <summary>
/// Un día del mes en paralelo. Clona la base importada, corre la facturación y la mora del día con los
/// ajustes del origen, repite los cobros con pasarela del día y compara contra un volcado posterior del origen:
/// renovaciones de servicios y dominios emparejadas por período, anulaciones, suspensiones, bajas, estado de los
/// dominios y cobros. Nunca emite ante el fisco ni llama a módulos ni pasarelas: corre sin llaves de secretos, así que
/// el núcleo no tiene cómo, y cada operación de un servicio se da por confirmada (<see cref="SimulatedModules"/>).
/// </summary>
public sealed partial class ParallelRun(string adminConnection, TextWriter log)
{
    private static readonly string[] Tables =
    [
        "tblinvoices", "tblinvoiceitems", "tblhosting", "tblaccounts", "tblorders", "tblconfiguration", "tblcancelrequests",
        "tblcurrencies", "tblclients", "tbldomains",
    ];

    private readonly List<Difference> diffs = [];
    private Dictionary<string, List<DumpRow>> post = [];

    /// <summary>Corre el día y devuelve las diferencias. La base de trabajo queda para revisarla. Una instancia por día.</summary>
    public async Task<IReadOnlyList<Difference>> Run(string postDump, string baseDb, string workDb, DateOnly day, int? cancelUnpaidAfterDays, CancellationToken ct)
    {
        if (!OwnDatabase().IsMatch(baseDb) || !OwnDatabase().IsMatch(workDb) || baseDb == workDb)
        {
            throw new ArgumentException("La base importada y la de trabajo tienen que ser distintas y empezar con billing_oss_.");
        }

        post = Tables.ToDictionary(t => t, _ => new List<DumpRow>(), StringComparer.OrdinalIgnoreCase);
        await using (var dump = DumpReader.Open(postDump))
        {
            await foreach (var row in dump.ReadRowsAsync(ct).ConfigureAwait(false))
            {
                if (post.TryGetValue(row.Table.Name, out var list))
                {
                    list.Add(row);
                }
            }
        }

        var settings = BillingSettings(post["tblconfiguration"]);
        await log.WriteLineAsync($"Ajustes del origen: {settings.ToJsonString()}. Anulación de impagas: {(cancelUnpaidAfterDays is { } c ? $"{c} días" : "no")}.").ConfigureAwait(false);

        await Execute(adminConnection, $"DROP DATABASE IF EXISTS {workDb} WITH (FORCE)", ct).ConfigureAwait(false);
        await Execute(adminConnection, $"CREATE DATABASE {workDb} TEMPLATE {baseDb}", ct).ConfigureAwait(false);
        var target = new NpgsqlConnectionStringBuilder(adminConnection) { Database = workDb }.ConnectionString;

        // A la hora del ciclo diario del origen, en la zona horaria del proveedor.
        var options = new BillingOptions();
        Apply(settings, cancelUnpaidAfterDays, options);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        var local = day.ToDateTime(new TimeOnly(0, 5));
        var clock = new FixedClock(new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime());
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(clock)
            .AddScoped<IServiceModules, SimulatedModules>()
            .AddBillingCore(target, o => Apply(settings, cancelUnpaidAfterDays, o))
            .BuildServiceProvider();
        try
        {
            var before = await Read(sp, db => Snapshot.Take(db, ct)).ConfigureAwait(false);
            var renewals = await Use<BillingRunService, int>(sp, s => s.Run(ct)).ConfigureAwait(false);
            await Use<DunningService, int>(sp, s => s.Run(ct)).ConfigureAwait(false);
            var jobs = sp.GetRequiredService<JobRunner>();
            await jobs.RunUntilIdle(ct).ConfigureAwait(false);

            var pairs = await Read(sp, db => CompareRenewals(db, before, day, ct)).ConfigureAwait(false);
            var replayed = await ReplayPayments(sp, before, pairs, ct).ConfigureAwait(false);
            await jobs.RunUntilIdle(ct).ConfigureAwait(false);

            await Read(sp, db => ComparePaidInvoices(db, replayed, ct)).ConfigureAwait(false);
            await Read(sp, db => CompareCancellations(db, before, day, options.CancelUnpaidAfterDays, ct)).ConfigureAwait(false);
            await Read(sp, db => CompareServices(db, before, day, options, ct)).ConfigureAwait(false);
            await Read(sp, db => CompareDomains(db, before, day, options, ct)).ConfigureAwait(false);
            var failed = await Read(sp, db => db.Jobs.AsNoTracking().Where(j => j.LastError != null).Select(j => j.Kind).ToListAsync(ct)).ConfigureAwait(false);
            foreach (var kind in failed)
            {
                diffs.Add(new Difference(DifferenceClass.Defect, "trabajo con falla", null, null, null, "trabajo", string.Empty, kind, "un trabajo del núcleo falló durante la simulación"));
            }

            await log.WriteLineAsync($"Facturas de renovación de billing-oss: {renewals}. Cobros repetidos: {replayed.Count}.").ConfigureAwait(false);
            return diffs;
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    /// <summary>Escribe las diferencias en TSV: primero los defectos, después las que hay que revisar y al final las esperadas.</summary>
    public static async Task WriteTsv(IEnumerable<Difference> differences, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        await output.WriteLineAsync("clase\ttipo\tservicio\tfactura_origen\tdocumento_billing_oss\tcampo\torigen\tbilling_oss\tmotivo").ConfigureAwait(false);
        foreach (var d in differences.OrderByDescending(d => d.Class).ThenBy(d => d.Kind, StringComparer.Ordinal).ThenBy(d => d.ServiceId).ThenBy(d => d.SourceInvoiceId))
        {
            await output.WriteLineAsync(string.Join('\t', d.ClassName, d.Kind, Id(d.ServiceId), Id(d.SourceInvoiceId), Id(d.OwnDocumentId), d.Field, d.Source, d.Own, d.Reason)).ConfigureAwait(false);
        }

        static string Id(long? id) => id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>Los ajustes que imprime el importador, más la anulación de impagas que el origen hacía con código propio.</summary>
    private static void Apply(JsonObject settings, int? cancelUnpaidAfterDays, BillingOptions o)
    {
        o.RenewalLeadDays = (int?)settings["RenewalLeadDays"] ?? o.RenewalLeadDays;
        o.RenewalLeadDaysByMonths = (settings["RenewalLeadDaysByMonths"] as JsonObject ?? [])
            .ToDictionary(p => int.Parse(p.Key, CultureInfo.InvariantCulture), p => (int)p.Value!);
        o.ContinuousInvoicing = (bool?)settings["ContinuousInvoicing"] ?? false;
        o.InvoiceDueDays = (int?)settings["InvoiceDueDays"] ?? o.InvoiceDueDays;
        o.GraceDays = (int?)settings["GraceDays"] ?? o.GraceDays;
        o.TerminationDays = (int?)settings["TerminationDays"];
        o.DomainRenewalLeadDays = (int?)settings["DomainRenewalLeadDays"] ?? o.DomainRenewalLeadDays;
        o.CancelUnpaidAfterDays = cancelUnpaidAfterDays;
    }

    /// <summary>
    /// Renovaciones: el origen las da como renglones de servicio de las facturas nuevas sin pedido, y billing-oss
    /// como períodos facturados. Se emparejan por servicio e inicio del período, nunca por número, y después se
    /// compara cada factura entera. Devuelve qué documento propio corresponde a cada factura nueva del origen.
    /// </summary>
    private async Task<Dictionary<long, long>> CompareRenewals(BillingDbContext db, Snapshot before, DateOnly day, CancellationToken ct)
    {
        var orderInvoices = post["tblorders"].Select(r => Long(r, "invoiceid")).ToHashSet();
        var items = post["tblinvoiceitems"].ToLookup(r => Long(r, "invoiceid"));
        var invoices = post["tblinvoices"].Where(r => !before.ImportedInvoices.Contains(Long(r, "id")) && Str(r, "status") != "Draft").ToDictionary(r => Long(r, "id"));
        var source = new List<RenewalLine>();
        foreach (var (id, invoice) in invoices)
        {
            var lines = items[id].ToList();
            if (orderInvoices.Contains(id))
            {
                diffs.Add(new Difference(DifferenceClass.Expected, "factura de pedido", null, id, null, "factura", Str(invoice, "total") ?? string.Empty, string.Empty, "entrada del día: la factura de un pedido no sale de la corrida diaria"));
                continue;
            }

            // Renovaciones de servicios y de dominios. En los dominios el período empieza en su vencimiento.
            var renewals = lines.Where(l => Str(l, "type") is "Hosting" or "Domain" && Date(l, "duedate") is not null).ToList();
            if (renewals.Count == 0)
            {
                diffs.Add(new Difference(DifferenceClass.Expected, "otra factura del día", null, id, null, "factura", Str(invoice, "total") ?? string.Empty, string.Empty, "entrada del día (cambio de plan, carga de saldo o factura manual): no sale de la corrida"));
            }

            // Una promoción recurrente va en un renglón aparte del mismo servicio (PromoHosting, negativo) y billing-oss
            // la factura en el mismo renglón, con el precio neto: el renglón del servicio se compara neto de su promoción.
            var promo = lines.Where(l => Str(l, "type") == "PromoHosting").GroupBy(l => Long(l, "relid")).ToDictionary(g => g.Key, g => g.Sum(l => Dec(l, "amount")));
            foreach (var l in renewals)
            {
                var domain = Str(l, "type") == "Domain";
                var discount = !domain && promo.Remove(Long(l, "relid"), out var p) ? p : 0m;
                source.Add(new RenewalLine(Long(l, "relid"), Date(l, "duedate")!.Value, PeriodEnd(Str(l, "description")), Dec(l, "amount") + discount, id, domain));
            }
        }

        var own = await (from p in db.BilledPeriods
                         join l in db.DocumentLines on p.DocumentLineId equals l.Id
                         join d in db.Documents on l.DocumentId equals d.Id
                         where !d.Imported && d.Kind == DocumentKind.Invoice && !p.Released
                         select new RenewalLine(p.ServiceId, p.Start, p.End.AddDays(-1), l.Amount, d.Id, false))
            .AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        // Los dominios no tienen períodos facturados: el inicio va en la clave del renglón (domain:{id}:{aaaa-mm-dd}, con un
        // contador detrás si la reemisión lo volvió a facturar).
        var domainLines = await (from l in db.DocumentLines
                                 join d in db.Documents on l.DocumentId equals d.Id
                                 join dm in db.Domains on l.DomainId equals (long?)dm.Id
                                 where !d.Imported && d.Kind == DocumentKind.Invoice && l.Kind == LineKind.Renewal && l.DedupKey != null
                                 select new { dm.Id, dm.Years, l.DedupKey, l.Amount, DocumentId = d.Id })
            .AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        foreach (var l in domainLines)
        {
            var start = DomainStart(l.DedupKey!);
            own.Add(new RenewalLine(l.Id, start, start.AddYears(l.Years).AddDays(-1), l.Amount, l.DocumentId, true));
        }

        var registered = post["tblhosting"].ToDictionary(r => Long(r, "id"), r => Date(r, "regdate"));

        // Claves por servicio o dominio e inicio: puede haber repetidas, así que se emparejan como multiconjunto.
        var paired = new List<(RenewalLine Source, RenewalLine Own)>();
        var sourceByKey = source.ToLookup(l => (l.Domain, l.ServiceId, l.Start));
        var ownByKey = own.ToLookup(l => (l.Domain, l.ServiceId, l.Start));
        foreach (var key in sourceByKey.Select(g => g.Key).Union(ownByKey.Select(g => g.Key)))
        {
            var s = sourceByKey[key].ToList();
            var o = ownByKey[key].ToList();
            for (var i = 0; i < Math.Min(s.Count, o.Count); i++)
            {
                paired.Add((s[i], o[i]));
                if (s[i].Amount != o[i].Amount)
                {
                    diffs.Add(Renewal(DifferenceClass.Defect, s[i], o[i], "importe", Money(s[i].Amount), Money(o[i].Amount), "importe del renglón distinto"));
                }

                if (s[i].End is { } end && end != o[i].End)
                {
                    diffs.Add(Renewal(DifferenceClass.Defect, s[i], o[i], "fin del período", Day(end), Day(o[i].End!.Value), "el período termina otro día"));
                }
            }

            foreach (var missing in s.Skip(o.Count))
            {
                diffs.Add((missing.Domain ? before.Domains.ContainsKey(missing.ServiceId) : before.Services.ContainsKey(missing.ServiceId))
                    ? Renewal(DifferenceClass.Defect, missing, null, "renglón", Day(missing.Start), string.Empty, "el origen renovó y billing-oss no")
                    : Renewal(DifferenceClass.Expected, missing, null, "renglón", Day(missing.Start), string.Empty, missing.Domain ? "dominio que no estaba en la base importada" : "servicio que no estaba en la base importada"));
            }

            foreach (var extra in o.Skip(s.Count))
            {
                // Un servicio dado de alta ese día no existía en el ciclo del origen, que corre a las cero horas: solo
                // pasa si la base importada salió de un volcado posterior al ciclo.
                var late = !extra.Domain && registered.GetValueOrDefault(extra.ServiceId) >= day;
                diffs.Add(Renewal(late ? DifferenceClass.Expected : DifferenceClass.Defect, null, extra, "renglón", string.Empty, Day(extra.Start),
                    late ? "servicio dado de alta el mismo día, después del ciclo del origen: lo renueva el ciclo siguiente" : "billing-oss renovó y el origen no"));
            }
        }

        // Factura por factura: si las dos agrupan las mismas renovaciones se compara la cabecera.
        var docs = await db.Documents.AsNoTracking().Where(d => !d.Imported && d.Kind == DocumentKind.Invoice)
            .Select(d => new { d.Id, d.AccountId, d.IssueDate, d.DueDate, d.Total, d.Tax, d.Currency })
            .ToDictionaryAsync(d => d.Id, ct).ConfigureAwait(false);
        var credit = await db.Allocations.AsNoTracking().Where(a => a.CreditEntryId != null).GroupBy(a => a.DocumentId)
            .Select(g => new { g.Key, Amount = g.Sum(a => a.Amount) }).ToDictionaryAsync(g => g.Key, g => g.Amount, ct).ConfigureAwait(false);
        var sourceCredit = post["tblaccounts"].Where(r => Str(r, "type") == "invoice_credit_applied")
            .GroupBy(r => Long(r, "invoiceid")).ToDictionary(g => g.Key, g => g.Sum(r => Dec(r, "amountin")));
        var result = new Dictionary<long, long>();
        foreach (var group in paired.GroupBy(p => p.Own.DocumentId))
        {
            var sourceIds = group.Select(p => p.Source.DocumentId).Distinct().ToList();
            var ownKeys = own.Where(l => l.DocumentId == group.Key).Select(l => (l.Domain, l.ServiceId, l.Start)).ToHashSet();
            var sourceKeys = source.Where(l => sourceIds.Contains(l.DocumentId)).Select(l => (l.Domain, l.ServiceId, l.Start)).ToHashSet();

            // La factura se informa por su primer servicio. Una solo de dominios va sin servicio.
            var serviceId = group.Select(p => p.Own).FirstOrDefault(l => !l.Domain)?.ServiceId;
            if (sourceIds.Count != 1 || !ownKeys.SetEquals(sourceKeys))
            {
                diffs.Add(new Difference(DifferenceClass.Defect, "agrupación", serviceId, sourceIds[0], group.Key, "facturas", string.Join(' ', sourceIds), group.Key.ToString(CultureInfo.InvariantCulture), "las renovaciones se agrupan en facturas distintas"));
                continue;
            }

            var sourceId = sourceIds[0];
            result[sourceId] = group.Key;
            var s = invoices[sourceId];
            var o = docs[group.Key];
            void Check(string field, string sourceValue, string ownValue, string reason)
            {
                if (sourceValue != ownValue)
                {
                    diffs.Add(new Difference(DifferenceClass.Defect, "factura de renovación", serviceId, sourceId, group.Key, field, sourceValue, ownValue, reason));
                }
            }

            // Como en la importación: el total es el bruto, y el saldo a favor aplicado es una imputación.
            var sourceTotal = Dec(s, "subtotal") + Dec(s, "tax") + Dec(s, "tax2");
            Check("total", Money(sourceTotal), Money(o.Total), "total distinto");
            Check("cuenta", Long(s, "userid").ToString(CultureInfo.InvariantCulture), o.AccountId.ToString(CultureInfo.InvariantCulture), "otra cuenta");
            Check("vencimiento", Date(s, "duedate") is { } due ? Day(due) : string.Empty, Day(o.DueDate), "vence otro día");
            Check("emisión", Date(s, "date") is { } date ? Day(date) : string.Empty, o.IssueDate is { } issued ? Day(issued) : string.Empty, "se emite otro día");
            var sourceApplied = sourceCredit.GetValueOrDefault(sourceId, Dec(s, "credit"));
            Check("saldo a favor aplicado", Money(sourceApplied), Money(credit.GetValueOrDefault(group.Key)), "se aplicó otro saldo a favor");
            var sourceTax = Dec(s, "tax") + Dec(s, "tax2");
            if (sourceTax != o.Tax)
            {
                // Mismo total y hasta un centavo de IVA: billing-oss redondea por factura y alícuota (D-04).
                var rounding = sourceTotal == o.Total && Math.Abs(sourceTax - o.Tax) <= 0.01m;
                diffs.Add(new Difference(rounding ? DifferenceClass.Expected : DifferenceClass.Defect, "factura de renovación", serviceId, sourceId, group.Key, "IVA", Money(sourceTax), Money(o.Tax),
                    rounding ? "redondeo del IVA por factura y alícuota, con el mismo total" : "IVA distinto"));
            }
        }

        await log.WriteLineAsync($"Renovaciones: origen {source.Count}, billing-oss {own.Count}, emparejadas {paired.Count}, facturas emparejadas {result.Count}. Día {Day(day)}.").ConfigureAwait(false);

        // Lo facturado en renovaciones por cada lado, por moneda: las diferencias lo explican renglón por renglón.
        var accountCurrency = post["tblclients"].ToDictionary(r => Long(r, "id"), r => Long(r, "currency"));
        var codes = post["tblcurrencies"].ToDictionary(r => Long(r, "id"), r => Str(r, "code")?.ToUpperInvariant() ?? string.Empty);
        var sourceTotals = source.Select(l => l.DocumentId).Distinct().Select(d => invoices[d])
            .GroupBy(i => codes.GetValueOrDefault(accountCurrency.GetValueOrDefault(Long(i, "userid")), "?"))
            .Select(g => $"{g.Key} {Money(g.Sum(i => Dec(i, "subtotal") + Dec(i, "tax") + Dec(i, "tax2")))}");
        var ownTotals = docs.Values.Where(d => own.Any(l => l.DocumentId == d.Id)).GroupBy(d => d.Currency).Select(g => $"{g.Key.Code} {Money(g.Sum(d => d.Total))}");
        await log.WriteLineAsync($"Facturado en renovaciones: origen {string.Join(", ", sourceTotals.Order(StringComparer.Ordinal))}; billing-oss {string.Join(", ", ownTotals.Order(StringComparer.Ordinal))}.").ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Repite sobre la copia los cobros con pasarela que el origen registró en la ventana, imputados a la misma
    /// factura importada o a la propia que corresponde a la factura nueva del origen.
    /// </summary>
    private async Task<List<(long SourceInvoice, long Document)>> ReplayPayments(ServiceProvider sp, Snapshot before, Dictionary<long, long> pairs, CancellationToken ct)
    {
        var currencies = post["tblcurrencies"].ToDictionary(r => Long(r, "id"), r => new Currency(Str(r, "code")!.ToUpperInvariant()));
        var accountCurrency = post["tblclients"].ToDictionary(r => Long(r, "id"), r => Long(r, "currency"));
        var gross = post["tblinvoices"].ToDictionary(r => Long(r, "id"), r => Dec(r, "subtotal") + Dec(r, "tax") + Dec(r, "tax2"));
        var replayed = new List<(long, long)>();
        foreach (var r in post["tblaccounts"].Where(r => Dec(r, "amountin") > 0m && Blank(Str(r, "gateway")) is not null))
        {
            var channel = $"import:{Blank(Str(r, "gateway"))}";
            var external = Blank(Str(r, "transid")) ?? FormattableString.Invariant($"tx-{Long(r, "id")}");
            if (before.Payments.Contains((channel, external)) || before.Payments.Contains((channel, FormattableString.Invariant($"{external}#{Long(r, "id")}"))))
            {
                continue;
            }

            var invoiceId = Long(r, "invoiceid");
            var documentId = before.ImportedInvoices.Contains(invoiceId) ? invoiceId : pairs.GetValueOrDefault(invoiceId);
            if (documentId == 0)
            {
                diffs.Add(new Difference(DifferenceClass.Expected, "cobro", null, invoiceId, null, "factura", Money(Dec(r, "amountin")), string.Empty, "cobro de una factura que billing-oss no tiene (pedido del día u otra entrada)"));
                continue;
            }

            // La moneda del cobro sale con la misma regla que en la importación. En otra moneda que la factura se imputa
            // lo que debía, en su moneda, como lo hace el importador: billing-oss todavía no convierte un cobro (sección
            // 8 del modelo de dominio), así que se repite en la moneda de la factura por ese importe y sin la comisión.
            var accountId = Long(r, "userid");
            var currency = MoveCurrency(r, currencies.GetValueOrDefault(accountCurrency.GetValueOrDefault(accountId), Currency.Ars), currencies, gross.GetValueOrDefault(invoiceId));
            var (amount, fee) = (Dec(r, "amountin"), Dec(r, "fees"));
            var owed = await Read(sp, db => db.Documents.AsNoTracking().Where(d => d.Id == documentId)
                .Select(d => new { d.Currency, Pending = d.Total - d.Credited - d.Allocated }).SingleAsync(ct)).ConfigureAwait(false);
            if (owed.Currency != currency && owed.Pending > 0m)
            {
                (currency, amount, fee) = (owed.Currency, owed.Pending, 0m);
            }

            try
            {
                await Use<PaymentService, long>(sp, s => s.Record(new PaymentInput(accountId, channel, external, currency, amount, PaymentStatus.Approved, [documentId], fee), ct)).ConfigureAwait(false);
                replayed.Add((invoiceId, documentId));
            }
            catch (InvalidOperationException e)
            {
                diffs.Add(new Difference(DifferenceClass.Defect, "cobro", null, invoiceId, documentId, "registro", Money(Dec(r, "amountin")), string.Empty, $"billing-oss no pudo registrar el cobro: {e.Message}"));
            }
        }

        return replayed;
    }

    /// <summary>Después de los cobros del día, cada factura cobrada tiene que quedar en el mismo estado de los dos lados.</summary>
    private async Task ComparePaidInvoices(BillingDbContext db, List<(long SourceInvoice, long Document)> replayed, CancellationToken ct)
    {
        var status = post["tblinvoices"].ToDictionary(r => Long(r, "id"), r => Str(r, "status"));
        var ids = replayed.Select(p => p.Document).ToList();
        var docs = await db.Documents.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct).ConfigureAwait(false);
        foreach (var (sourceId, documentId) in replayed.Distinct())
        {
            var sourcePaid = status.GetValueOrDefault(sourceId) == "Paid";
            var ownPaid = docs[documentId].Status == InvoiceStatus.Paid;
            if (sourcePaid != ownPaid)
            {
                diffs.Add(new Difference(DifferenceClass.Defect, "cobro", null, sourceId, documentId, "estado", status.GetValueOrDefault(sourceId) ?? string.Empty, docs[documentId].Status.ToString(), "la factura cobrada quedó en otro estado"));
            }
        }
    }

    /// <summary>Anulaciones de impagas: facturas abiertas de la base que el origen anuló y notas de crédito propias del día.</summary>
    private async Task CompareCancellations(BillingDbContext db, Snapshot before, DateOnly day, int? cancelDays, CancellationToken ct)
    {
        var status = post["tblinvoices"].ToDictionary(r => Long(r, "id"), r => Str(r, "status"));
        var sourceCancelled = before.OpenInvoices.Keys.Where(id => status.GetValueOrDefault(id) == "Cancelled").ToHashSet();
        var ownCancelled = (await db.Documents.AsNoTracking().Where(d => !d.Imported && d.Kind == DocumentKind.CreditNote && d.RelatedDocumentId != null)
            .Select(d => d.RelatedDocumentId!.Value).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        var requested = NewEndRequests(before);
        var services = post["tblinvoiceitems"].Where(r => Str(r, "type") == "Hosting").ToLookup(r => Long(r, "invoiceid"), r => (long?)Long(r, "relid"));
        foreach (var id in sourceCancelled.Except(ownCancelled))
        {
            var byRequest = services[id].Any(s => requested.Contains(s!.Value));
            diffs.Add(new Difference(byRequest ? DifferenceClass.Expected : DifferenceClass.Defect, "anulación", services[id].FirstOrDefault(), id, null, "factura", "anulada", "abierta",
                byRequest ? "entrada del día: el pedido de baja anuló la factura en el origen" : "el origen anuló y billing-oss no"));
        }

        foreach (var id in ownCancelled.Except(sourceCancelled))
        {
            var late = cancelDays is { } days && before.OpenInvoices.TryGetValue(id, out var due) && due.AddDays(days) < day;
            var collections = status.GetValueOrDefault(id) == "Collections" ? " (la factura está en gestión de cobro)" : string.Empty;
            diffs.Add(new Difference(late ? DifferenceClass.Review : DifferenceClass.Defect, "anulación", services[id].FirstOrDefault(), id, null, "factura", status.GetValueOrDefault(id) ?? string.Empty, "anulada",
                late ? $"anulación vencida antes del día que el origen no hizo{collections}: billing-oss la pone al día" : "billing-oss anuló y el origen no"));
        }
    }

    /// <summary>
    /// Suspensiones, reactivaciones y bajas de los servicios vivos de la base, y hasta dónde quedaron pagos. Una acción
    /// que billing-oss hace y que vencía antes del día simulado es esperada: el origen la tenía pendiente.
    /// </summary>
    private async Task CompareServices(BillingDbContext db, Snapshot before, DateOnly day, BillingOptions options, CancellationToken ct)
    {
        var after = await db.Services.AsNoTracking().Select(s => new { s.Id, s.Status, s.PaidThrough }).ToDictionaryAsync(s => s.Id, ct).ConfigureAwait(false);
        var termination = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.TerminationDays ?? options.TerminationDays, ct).ConfigureAwait(false);
        var source = post["tblhosting"].ToDictionary(r => Long(r, "id"));
        var requested = NewEndRequests(before);
        foreach (var b in before.Services.Values.Where(s => s.Status is ServiceStatus.Active or ServiceStatus.Suspended))
        {
            if (!source.TryGetValue(b.Id, out var s))
            {
                continue;
            }

            var initial = State(b.Status);
            var own = State(after[b.Id].Status);
            var theirs = Str(s, "domainstatus") switch
            {
                "Active" => "activo",
                "Suspended" => "suspendido",
                "Terminated" or "Cancelled" => "baja",
                var other => other ?? string.Empty,
            };
            if (own != theirs)
            {
                var actionDue = own == "baja" ? b.CancelAt ?? (termination.GetValueOrDefault(b.ProductId) is { } days ? b.PaidThrough?.AddDays(days) : null)
                    : own == "suspendido" ? b.PaidThrough?.AddDays(options.GraceDays)
                    : null;
                var late = own != initial && theirs == initial && actionDue < day;
                var byRequest = theirs == "baja" && requested.Contains(b.Id);

                // Una baja retenida en una revisión del staff de billing-oss que el origen hizo en el día.
                var released = theirs == "baja" && own == initial && before.Held.Contains(b.Id);
                var kind = late ? DifferenceClass.Review : byRequest || released ? DifferenceClass.Expected : DifferenceClass.Defect;
                diffs.Add(new Difference(kind, own != initial ? own : theirs, b.Id, null, null, "estado", theirs, own,
                    late ? $"vencía el {Day(actionDue!.Value)} y el origen no lo hizo: billing-oss lo pone al día"
                    : byRequest ? "entrada del día: pedido de baja en el origen"
                    : released ? "la baja está retenida en una revisión del staff de billing-oss y el origen la hizo"
                    : "el servicio quedó en otro estado"));
                continue;
            }

            var sourcePaid = Date(s, "nextduedate");
            var ownPaid = after[b.Id].PaidThrough;
            if (b.PaidThrough is not null && own != "baja" && sourcePaid != ownPaid && (sourcePaid != b.PaidThrough || ownPaid != b.PaidThrough))
            {
                diffs.Add(new Difference(DifferenceClass.Defect, "pagado hasta", b.Id, null, null, "pagado hasta", sourcePaid is { } sp ? Day(sp) : string.Empty, ownPaid is { } op ? Day(op) : string.Empty, "lo pagado avanzó distinto"));
            }
        }

        static string State(ServiceStatus status) => status switch
        {
            ServiceStatus.Active => "activo",
            ServiceStatus.Suspended => "suspendido",
            ServiceStatus.Terminated or ServiceStatus.Cancelled => "baja",
            var other => other.ToString(),
        };
    }

    /// <summary>
    /// Estado de los dominios vivos de la base y hasta dónde quedaron pagos. billing-oss los pasa a gracia el día después
    /// de lo pago y a vencidos pasada la gracia, porque ningún registrar sincroniza todavía, y el origen los cambia por
    /// su cuenta. Lo que billing-oss hizo por esa regla el mismo día es esperado, lo que vencía antes es para revisar y
    /// lo que el origen cambió solo es defecto.
    /// </summary>
    private async Task CompareDomains(BillingDbContext db, Snapshot before, DateOnly day, BillingOptions options, CancellationToken ct)
    {
        var after = await db.Domains.AsNoTracking().Select(d => new { d.Id, d.Status, d.PaidThrough }).ToDictionaryAsync(d => d.Id, ct).ConfigureAwait(false);
        var source = post["tbldomains"].ToDictionary(r => Long(r, "id"));
        foreach (var (id, b) in before.Domains.Where(d => d.Value.Status is DomainStatus.Active or DomainStatus.Grace))
        {
            if (!source.TryGetValue(id, out var s) || !after.TryGetValue(id, out var a))
            {
                continue;
            }

            var theirs = DomainState(Str(s, "status"));
            if (a.Status != theirs)
            {
                var actionDue = a.Status == DomainStatus.Expired ? b.PaidThrough?.AddDays(options.DomainGraceDays)
                    : a.Status == DomainStatus.Grace ? b.PaidThrough?.AddDays(1)
                    : null;
                var own = a.Status != b.Status && theirs == b.Status && actionDue is not null;
                var kind = !own ? DifferenceClass.Defect : actionDue < day ? DifferenceClass.Review : DifferenceClass.Expected;
                diffs.Add(new Difference(kind, "dominio", id, null, null, "estado", Label(theirs), Label(a.Status),
                    kind == DifferenceClass.Review ? $"vencía el {Day(actionDue!.Value)} y el origen no lo hizo: billing-oss lo pone al día"
                    : kind == DifferenceClass.Expected ? "regla propia: un dominio impago entra en gracia el día después de lo pago y vence pasada la gracia"
                    : "el dominio quedó en otro estado"));
                continue;
            }

            var sourcePaid = Date(s, "nextduedate");
            if (b.PaidThrough is not null && sourcePaid != a.PaidThrough && (sourcePaid != b.PaidThrough || a.PaidThrough != b.PaidThrough))
            {
                diffs.Add(new Difference(DifferenceClass.Defect, "pagado hasta de dominio", id, null, null, "pagado hasta", sourcePaid is { } sp ? Day(sp) : string.Empty, a.PaidThrough is { } op ? Day(op) : string.Empty, "lo pagado del dominio avanzó distinto"));
            }
        }

        static string Label(DomainStatus status) => status switch
        {
            DomainStatus.Active => "activo",
            DomainStatus.Grace => "en gracia",
            DomainStatus.Expired => "vencido",
            DomainStatus.Pending => "pendiente",
            _ => "cancelado",
        };
    }

    /// <summary>Servicios con un pedido de baja en el volcado posterior que la base no tenía.</summary>
    private HashSet<long> NewEndRequests(Snapshot before) =>
        post["tblcancelrequests"].Select(r => Long(r, "relid")).Where(id => before.Services.TryGetValue(id, out var s) && s.CancelAt is null).ToHashSet();

    private static Difference Renewal(DifferenceClass kind, RenewalLine? source, RenewalLine? own, string field, string sourceValue, string ownValue, string reason) =>
        new(kind, (source ?? own)!.Domain ? "renovación de dominio" : "renovación", source?.ServiceId ?? own?.ServiceId, source?.DocumentId, own?.DocumentId, field, sourceValue, ownValue, reason);

    /// <summary>El origen escribe el período en el renglón como "(dd/mm/aaaa - dd/mm/aaaa)", con el último día incluido.</summary>
    public static DateOnly? PeriodEnd(string? description) =>
        description is not null && PeriodText().Match(description) is { Success: true } m
        && DateOnly.TryParseExact(m.Groups["to"].Value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) ? end : null;

    /// <summary>Inicio del período de un renglón de dominio, leído de su clave domain:{id}:{aaaa-mm-dd}, tenga o no el contador de la reemisión.</summary>
    public static DateOnly DomainStart(string dedupKey)
    {
        ArgumentNullException.ThrowIfNull(dedupKey);
        return DateOnly.ParseExact(dedupKey.Split(':')[2], "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<T> Use<TService, T>(ServiceProvider sp, Func<TService, Task<T>> work)
        where TService : notnull
    {
        await using var scope = sp.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<TService>()).ConfigureAwait(false);
    }

    private static Task<T> Read<T>(ServiceProvider sp, Func<BillingDbContext, Task<T>> work) => Use(sp, work);

    private static Task Read(ServiceProvider sp, Func<BillingDbContext, Task> work) => Use<BillingDbContext, bool>(sp, async db =>
    {
        await work(db).ConfigureAwait(false);
        return true;
    });

    private static async Task Execute(string connectionString, string sql, CancellationToken ct)
    {
        // Copiar la base importada o borrar la de trabajo fuerza un checkpoint: con el servidor cargado pasa los 30 s.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    [GeneratedRegex("^billing_oss_[a-z0-9_]{1,50}$")]
    private static partial Regex OwnDatabase();

    [GeneratedRegex(@"\((?<from>\d{2}/\d{2}/\d{4}) - (?<to>\d{2}/\d{2}/\d{4})\)")]
    private static partial Regex PeriodText();

    /// <summary>Un renglón de renovación. Con <paramref name="Domain"/>, <paramref name="ServiceId"/> es el ID del dominio.</summary>
    private sealed record RenewalLine(long ServiceId, DateOnly Start, DateOnly? End, decimal Amount, long DocumentId, bool Domain);

    private sealed record ServiceBefore(long Id, ServiceStatus Status, DateOnly? PaidThrough, DateOnly? CancelAt, long ProductId);

    private sealed record DomainBefore(DomainStatus Status, DateOnly? PaidThrough);

    /// <summary>Lo que había en la base antes de correr el día.</summary>
    private sealed record Snapshot(Dictionary<long, ServiceBefore> Services, Dictionary<long, DomainBefore> Domains, HashSet<long> ImportedInvoices, Dictionary<long, DateOnly> OpenInvoices, HashSet<(string, string?)> Payments, HashSet<long> Held)
    {
        public static async Task<Snapshot> Take(BillingDbContext db, CancellationToken ct)
        {
            var services = await db.Services.AsNoTracking().Select(s => new ServiceBefore(s.Id, s.Status, s.PaidThrough, s.CancelAt, s.ProductId))
                .ToDictionaryAsync(s => s.Id, ct).ConfigureAwait(false);
            var imported = (await db.Documents.AsNoTracking().Where(d => d.Imported && d.Kind == DocumentKind.Invoice).Select(d => d.Id).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
            var open = await db.Documents.AsNoTracking()
                .Where(d => d.Imported && d.Kind == DocumentKind.Invoice && d.IssueDate != null && d.Total - d.Credited - d.Allocated > 0)
                .ToDictionaryAsync(d => d.Id, d => d.DueDate, ct).ConfigureAwait(false);
            var payments = (await db.Payments.AsNoTracking().Select(p => new { p.Channel, p.ExternalId }).ToListAsync(ct).ConfigureAwait(false))
                .Select(p => (p.Channel, p.ExternalId)).ToHashSet();
            var domains = await db.Domains.AsNoTracking().Select(d => new { d.Id, d.Status, d.PaidThrough })
                .ToDictionaryAsync(d => d.Id, d => new DomainBefore(d.Status, d.PaidThrough), ct).ConfigureAwait(false);

            // Bajas retenidas en una revisión del staff: billing-oss no las hace solo.
            return new Snapshot(services, domains, imported, open, payments, await DunningService.Held(db, ct).ConfigureAwait(false));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// Los módulos del paralelo: cada operación queda confirmada en el acto, así el estado del servicio avanza como si
    /// el módulo (o el staff, en los manuales) respondiera. Un servicio de un producto con módulo y sin instancia sigue
    /// fallando a la vista, como en producción: el mapa de módulos de la importación tiene que estar completo.
    /// </summary>
    private sealed class SimulatedModules : IServiceModules
    {
        public Task<ModuleOutcome> Execute(Service service, ServiceOperation op, CancellationToken ct) => Task.FromResult(new ModuleOutcome(true));
    }
}
