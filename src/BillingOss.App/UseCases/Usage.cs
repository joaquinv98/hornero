using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Notifications;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using BillingOss.Domain.Usage;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BillingOss.App.UseCases;

/// <summary>Trabajos del uso medido (sección 3 de diseno/uso-medido.md).</summary>
public static class UsageJobs
{
    public const string Sweep = "usage.sweep";
    public const string Read = "usage.read";
    public const string Close = "usage.close";

    /// <summary>Los cierres del mes van de a uno entre todos los nodos.</summary>
    public const string CloseEntity = "usage-close";

    /// <summary>
    /// El cierre del mes espera esto desde el primer intervalo del mes, para que lleguen las lecturas del último día.
    /// ponytail: espera fija, si un módulo tarda más su último día va a la ventana siguiente (no se pierde).
    /// </summary>
    public static TimeSpan CloseDelay { get; } = TimeSpan.FromHours(6);

    /// <summary>Lo encola el planificador: la lectura del día que terminó y el cierre del mes que empezó.</summary>
    public static async Task Schedule(BillingDbContext db, JobQueue jobs, DateOnly today, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(jobs);
        if (!await db.Set<UsageMetric>().AnyAsync(m => m.Active, ct).ConfigureAwait(false))
        {
            return;
        }

        var measured = today.AddDays(-1);
        await jobs.Enqueue(Sweep, new { date = measured }, dedupKey: $"{Sweep}:{measured:yyyy-MM-dd}", ct: ct).ConfigureAwait(false);
        var month = new DateOnly(today.Year, today.Month, 1);
        await jobs.Enqueue(Close, new { cut = month }, CloseEntity, $"{Close}:{month:yyyy-MM}", now + CloseDelay, ct: ct).ConfigureAwait(false);
    }
}

public sealed record UsageMetricInput(string? Name, string? Unit, UsageAggregation? Aggregation, decimal Included, decimal Step, UsageCycle Cycle, IReadOnlyList<int>? AlertPercents, bool Active = true);

/// <summary>
/// Métricas de uso de un producto y sus precios (sección 1 de diseno/uso-medido.md). Una métrica tiene que estar en el
/// manifiesto del módulo del producto, con el mismo tipo. Los cambios quedan en la auditoría.
/// </summary>
public sealed class UsageCatalog(BillingDbContext db, AuditLog audit, TimeProvider time)
{
    public async Task<UsageMetric> Save(long productId, string code, UsageMetricInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var declared = await Declared(productId, ct).ConfigureAwait(false);
        if (!declared.TryGetValue(code, out var declaration))
        {
            throw new ValidationException($"El módulo del producto no declara la métrica {code}.");
        }

        var kind = declaration.Kind == UsageProtocol.Counter ? UsageKind.Counter : UsageKind.Gauge;
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var metric = await db.Set<UsageMetric>().SingleOrDefaultAsync(m => m.ProductId == productId && m.Code == code, ct).ConfigureAwait(false);
        if (metric is null)
        {
            metric = new UsageMetric(productId, code, kind, now);
            db.Add(metric);
        }
        else if (metric.Kind != kind)
        {
            throw new ValidationException($"La métrica {code} es de otro tipo en el módulo: hace falta una métrica nueva.");
        }

        try
        {
            var name = string.IsNullOrWhiteSpace(input.Name) ? declaration.Label.GetValueOrDefault("es") ?? code : input.Name;
            metric.Configure(name, input.Unit ?? declaration.Unit, input.Aggregation ?? metric.Aggregation, input.Included, input.Step, input.Cycle, input.AlertPercents ?? metric.AlertPercents, input.Active, now);
        }
        catch (ArgumentException ex)
        {
            throw new ValidationException(ex.Message);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("usage.metric.save", string.Create(CultureInfo.InvariantCulture, $"product:{productId}"),
            new { metric_id = metric.Id, code, included = metric.Included, step = metric.Step, cycle = metric.Cycle.ToString(), aggregation = metric.Aggregation.ToString(), active = metric.Active }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return metric;
    }

    /// <summary>Agrega un precio con vigencia. El mismo día y moneda repetidos dan <see cref="InvalidOperationException"/>.</summary>
    public async Task<UsageMetricPrice> AddPrice(long productId, string code, Currency currency, decimal unitPrice, DateOnly validFrom, CancellationToken ct = default)
    {
        var metric = await db.Set<UsageMetric>().AsNoTracking().SingleOrDefaultAsync(m => m.ProductId == productId && m.Code == code, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"El producto {productId} no tiene la métrica {code}.");
        UsageMetricPrice price;
        try
        {
            price = new UsageMetricPrice(metric.Id, currency, unitPrice, validFrom);
        }
        catch (ArgumentException ex)
        {
            throw new ValidationException(ex.Message);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.Add(price);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"La métrica {code} ya tiene precio en {currency} desde el {validFrom:dd/MM/yyyy}."));
        }

        await audit.Record("usage.price.add", string.Create(CultureInfo.InvariantCulture, $"product:{productId}"),
            new { metric_id = metric.Id, code, currency = currency.Code, unit_price = unitPrice, valid_from = validFrom }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return price;
    }

    /// <summary>Las métricas que declara el módulo del producto. Sin módulo o sin la capacidad, no se puede medir.</summary>
    public async Task<IReadOnlyDictionary<string, UsageMetricDeclaration>> Declared(long productId, CancellationToken ct = default)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el producto {productId}.");
        var manifest = product.ModuleInstanceId is not { } instanceId ? null : await db.ModuleInstances.AsNoTracking()
            .Where(i => i.Id == instanceId)
            .Join(db.ModuleInstallations, i => i.InstallationId, m => m.Id, (i, m) => m.Manifest)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        var parsed = manifest is null ? null : JsonNode.Parse(manifest) as JsonObject;
        return parsed is not null && UsageProtocol.Declares(parsed)
            ? UsageProtocol.Declared(parsed)
            : throw new ValidationException($"El producto {productId} no tiene un módulo que lea el uso ({UsageProtocol.Capability}).");
    }
}

/// <summary>
/// Corta las ventanas de uso y cobra el excedente (secciones 4 y 5 de diseno/uso-medido.md). Corre en la transacción
/// del que llama, con el lock de facturación de la cuenta ya tomado: la renovación o el cierre del mes.
/// </summary>
public sealed class UsageBilling(BillingDbContext db, BillingCore core, TimeProvider time, ILogger<UsageBilling> logger)
{
    /// <summary>Motivo de las facturas del cierre del mes.</summary>
    public const string InvoiceReason = "usage";

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    /// <summary>
    /// Cierra en <paramref name="cut"/> las ventanas de las métricas activas del servicio que elige
    /// <paramref name="include"/>: guarda un cargo por métrica, marca sus muestras y, si hay excedente con importe, le
    /// agrega un renglón al borrador que da <paramref name="draft"/>. Una métrica con excedente y sin precio en la moneda
    /// del servicio queda abierta. El que llama guarda. Devuelve cuántos renglones agregó.
    /// </summary>
    public async Task<int> Close(Service service, Func<UsageMetric, bool> include, DateOnly cut, Func<Task<Document>> draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(include);
        ArgumentNullException.ThrowIfNull(draft);
        var metrics = (await db.Set<UsageMetric>().AsNoTracking().Where(m => m.ProductId == service.ProductId && m.Active).ToListAsync(ct).ConfigureAwait(false))
            .Where(include).ToList();
        var codes = metrics.Select(m => m.Code).ToList();
        var samples = codes.Count == 0 ? [] : await db.Set<UsageSample>()
            .Where(s => s.ServiceId == service.Id && codes.Contains(s.Code) && s.ChargeId == null && s.Date < cut)
            .ToListAsync(ct).ConfigureAwait(false);
        if (samples.Count == 0)
        {
            return 0;
        }

        var ids = metrics.Select(m => m.Id).ToList();
        var prices = await db.Set<UsageMetricPrice>().AsNoTracking().Where(p => ids.Contains(p.MetricId) && p.Currency == service.Currency).ToListAsync(ct).ConfigureAwait(false);
        var ends = await db.Set<UsageCharge>().Where(c => c.ServiceId == service.Id && codes.Contains(c.Code))
            .GroupBy(c => c.Code).Select(g => new { Code = g.Key, End = g.Max(c => c.End) })
            .ToDictionaryAsync(x => x.Code, x => x.End, ct).ConfigureAwait(false);
        (string Product, string Category, LineTax Tax)? billing = null;
        var lines = 0;
        foreach (var metric in metrics)
        {
            var window = samples.Where(s => s.Code == metric.Code).ToList();
            var previousEnd = ends.TryGetValue(metric.Code, out var end) ? end : (DateOnly?)null;

            // Ya se cortó en este día: lo que llegó después espera al próximo corte, así no choca con aquel cargo.
            if (window.Count == 0 || previousEnd >= cut)
            {
                continue;
            }

            // Las ventanas van seguidas, desde el corte anterior o desde la primera muestra. Una muestra que llegó tarde,
            // con fecha de la ventana ya cortada, entra en esta.
            var start = previousEnd ?? window.Min(s => s.Date);
            var included = metric.IncludedFor(service.Cycle, start, cut);
            var measured = UsageMath.Measure(metric.Aggregation, [.. window.Select(s => s.Value)]);
            var excess = UsageMath.Excess(measured, included, metric.Step);
            decimal? unitPrice = null;
            if (excess > 0m)
            {
                if (UsageMetricPrice.On(prices, metric.Id, service.Currency, start) is not { } price)
                {
                    logger.LogWarning("El servicio {Service} tiene excedente de {Metric} sin precio en {Currency}: la ventana queda abierta.", service.Id, metric.Code, service.Currency);
                    continue;
                }

                unitPrice = price.UnitPrice;
            }

            var charge = new UsageCharge(service.Id, metric.Code, metric.Id, start, cut, measured, included, excess, unitPrice, service.Currency, time.GetUtcNow());
            db.Add(charge);
            window.ForEach(s => s.BilledBy(charge));
            if (unitPrice is { } unit && Rounding.Commercial(excess * unit) > 0m)
            {
                billing ??= await Billing(service, ct).ConfigureAwait(false);
                var (product, category, tax) = billing.Value;
                var document = await draft().ConfigureAwait(false);
                var line = document.AddLine(LineKind.Usage, Describe(metric, product, start, cut, measured, included), excess, unit, tax.Treatment, tax.Rate, service.Id,
                    string.Create(CultureInfo.InvariantCulture, $"usage:{service.Id}:{metric.Code}:{cut:yyyy-MM-dd}"), category: category, reason: tax.Reason, legend: tax.Legend);
                charge.BillOn(line);
                lines++;
            }
        }

        return lines;
    }

    /// <summary>"Uso de Transferencia de VPS 2 GB (vps-01) del 07/11/2026 al 06/12/2026: 120 GB medidos, 100 GB incluidos".</summary>
    public static string Describe(UsageMetric metric, string product, DateOnly start, DateOnly end, decimal measured, decimal included)
    {
        ArgumentNullException.ThrowIfNull(metric);
        var text = string.Create(Spanish, $"Uso de {metric.Name} de {product} del {start:dd/MM/yyyy} al {end.AddDays(-1):dd/MM/yyyy}: {Amount(measured, metric.Unit, Spanish)} medidos, {Amount(included, metric.Unit, Spanish)} incluidos");
        return text.Length > 500 ? text[..500] : text;
    }

    /// <summary>Precio por unidad con su moneda, con 2 a 6 decimales: "ARS 1.234,50" o "USD 0,0125".</summary>
    public static string Price(decimal unitPrice, Currency currency, IFormatProvider culture) =>
        $"{currency.Code} {unitPrice.ToString("#,##0.00####", culture)}";

    /// <summary>"120 GB", "3,5 GB" o "12 casillas", con la cultura del que lee.</summary>
    public static string Amount(decimal value, string unit, IFormatProvider culture) =>
        string.IsNullOrEmpty(unit) ? value.ToString("#,##0.######", culture) : $"{value.ToString("#,##0.######", culture)} {unit}";

    /// <summary>
    /// El nombre y el impuesto del renglón, con la categoría del producto como la renovación y la misma fecha fiscal
    /// única: el día de la corrida, que es el de emisión.
    /// </summary>
    private async Task<(string Product, string Category, LineTax Tax)> Billing(Service service, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == service.ProductId, ct).ConfigureAwait(false);
        var receiver = await core.Receiver(service.AccountId, ct).ConfigureAwait(false);
        var tax = (await core.Taxes(ct).ConfigureAwait(false)).Line(product, receiver, service.AccountId, core.Today());
        var name = string.IsNullOrEmpty(service.Label) ? product.Name : $"{product.Name} ({service.Label})";
        return (name, product.TaxCategory, tax);
    }
}

/// <summary>Encola la lectura del día de cada servicio que mide uso.</summary>
public sealed class UsageSweepHandler(BillingDbContext db, JobQueue jobs) : IJobHandler
{
    public string Kind => UsageJobs.Sweep;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var date = JsonDocument.Parse(job.Payload).RootElement.GetProperty("date").Deserialize<DateOnly>();
        var products = db.Set<UsageMetric>().Where(m => m.Active).Select(m => m.ProductId);
        var ids = await db.Services.AsNoTracking()
            .Where(s => (s.Status == ServiceStatus.Active || s.Status == ServiceStatus.Suspended) && s.ModuleInstanceId != null && products.Contains(s.ProductId))
            .OrderBy(s => s.Id).Select(s => s.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        // En una transacción, así pasa por la cerca del trabajo (D-07).
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var id in ids)
        {
            await jobs.Enqueue(UsageJobs.Read, new { service_id = id, date }, string.Create(CultureInfo.InvariantCulture, $"usage:{id}"),
                string.Create(CultureInfo.InvariantCulture, $"{UsageJobs.Read}:{id}:{date:yyyy-MM-dd}"), ct: ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

/// <summary>
/// La lectura diaria de un servicio (sección 3 de diseno/uso-medido.md): pide <c>usage</c> al módulo, guarda una
/// muestra por métrica activa y avisa al cliente si pasó un umbral. Las de un servicio corren de a una por su clave de
/// entidad, y la muestra es única por día: repetirla no guarda otra ni corre la base de un contador.
/// </summary>
public sealed class UsageReadHandler(BillingDbContext db, ModuleRegistry registry, ModuleClient client, UsageAlerts alerts, TimeProvider time) : IJobHandler
{
    public string Kind => UsageJobs.Read;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var payload = JsonDocument.Parse(job.Payload);
        var serviceId = payload.RootElement.GetProperty("service_id").GetInt64();
        var date = payload.RootElement.GetProperty("date").Deserialize<DateOnly>();
        var service = await db.Services.AsNoTracking().SingleAsync(s => s.Id == serviceId, ct).ConfigureAwait(false);
        if (service.Status is not (ServiceStatus.Active or ServiceStatus.Suspended) || service.ModuleInstanceId is not { } instanceId)
        {
            return JobResult.Done;
        }

        var done = await db.Set<UsageSample>().Where(s => s.ServiceId == serviceId && s.Date == date).Select(s => s.Code).ToListAsync(ct).ConfigureAwait(false);
        var metrics = (await db.Set<UsageMetric>().AsNoTracking().Where(m => m.ProductId == service.ProductId && m.Active).OrderBy(m => m.Id).ToListAsync(ct).ConfigureAwait(false))
            .Where(m => !done.Contains(m.Code)).ToList();
        if (metrics.Count == 0)
        {
            return JobResult.Done;
        }

        var readings = await Read(service, instanceId, metrics, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var read = new List<UsageMetric>();
        foreach (var metric in metrics.Where(m => readings.ContainsKey(m.Code)))
        {
            // El contador se encadena con la lectura anterior, pero no con una de antes de que la métrica volviera a estar
            // activa: lo que subió mientras no se medía no se cobra.
            var reading = readings[metric.Code];
            var previous = metric.Kind == UsageKind.Counter
                ? await db.Set<UsageSample>().Where(s => s.ServiceId == serviceId && s.Code == metric.Code && s.ReadAt >= metric.ActiveSince)
                    .OrderByDescending(s => s.ReadAt).ThenByDescending(s => s.Id).Select(s => (decimal?)s.Reading)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false)
                : null;
            var value = metric.Kind == UsageKind.Counter ? UsageMath.CounterUse(previous, reading) : reading;
            db.Add(new UsageSample(serviceId, metric.Code, date, reading, value, now));
            read.Add(metric);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await alerts.Check(service, read, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return JobResult.Done;
    }

    /// <summary>Llama a <c>usage</c> y valida la respuesta: un valor fuera de formato deja el trabajo muerto y visible.</summary>
    private async Task<Dictionary<string, decimal>> Read(Service service, long instanceId, List<UsageMetric> metrics, CancellationToken ct)
    {
        var module = await registry.Resolve(instanceId, ct).ConfigureAwait(false);
        if (module.InCore || !UsageProtocol.Declares(module.Manifest))
        {
            throw new PermanentFailureException($"El módulo del servicio {service.Id} no declara {UsageProtocol.Capability}.");
        }

        var context = await ProtocolServiceModules.ServiceContext(db, service, module.Manifest, ct).ConfigureAwait(false);
        var input = new JsonObject { ["metrics"] = new JsonArray([.. metrics.Select(m => JsonValue.Create(m.Code))]) };
        var result = await client.Call(module.Endpoint, UsageProtocol.Path, new ModuleRequest(module.Data, context, input), null, module.Timeout(UsageProtocol.Operation), ct).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            throw ErrorCodes.IsRetryable(error.Code)
                ? new RetryLaterException($"{error.Code}: {error.Message}", error.RetryAfter is { } s ? TimeSpan.FromSeconds(s) : null)
                : new PermanentFailureException($"{error.Code}: {error.Message}");
        }

        var response = result.Response!;
        if (response.InstanceState is { } state)
        {
            await registry.SaveState(module, state, ct).ConfigureAwait(false);
        }

        if (response.Status != ModuleResponse.CompletedStatus || response.Result?["metrics"] is not JsonObject values)
        {
            throw new PermanentFailureException($"El módulo del servicio {service.Id} respondió usage sin métricas.");
        }

        var readings = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var metric in metrics.Where(m => values.ContainsKey(m.Code)))
        {
            var text = values[metric.Code] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            readings[metric.Code] = UsageProtocol.TryParse(text, out var value)
                ? value
                : throw new PermanentFailureException($"El módulo del servicio {service.Id} devolvió una lectura inválida de {metric.Code}: tiene que ser un decimal sin signo, con 12 enteros y 6 decimales como máximo.");
        }

        return readings;
    }
}

/// <summary>
/// Avisos al pasar un umbral de lo incluido (sección 6 de diseno/uso-medido.md): uno por servicio, métrica, ventana y
/// umbral, por la clave del correo. Va en la transacción de la lectura.
/// </summary>
public sealed class UsageAlerts(BillingDbContext db, Notifier notifier, BillingCore core)
{
    public async Task Check(Service service, IReadOnlyList<UsageMetric> metrics, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(metrics);
        foreach (var metric in metrics.Where(m => m.Included > 0m && m.AlertPercents.Length > 0))
        {
            var samples = await db.Set<UsageSample>().Where(s => s.ServiceId == service.Id && s.Code == metric.Code && s.ChargeId == null)
                .Select(s => new { s.Value, s.Date }).ToListAsync(ct).ConfigureAwait(false);
            if (samples.Count == 0)
            {
                continue;
            }

            // La ventana abierta, con las mismas cuentas que el corte: desde el corte anterior o la primera muestra.
            var window = await db.Set<UsageCharge>().Where(c => c.ServiceId == service.Id && c.Code == metric.Code)
                .MaxAsync(c => (DateOnly?)c.End, ct).ConfigureAwait(false);
            var since = window ?? samples.Min(s => s.Date);
            var used = UsageMath.Measure(metric.Aggregation, [.. samples.Select(s => s.Value)]);
            var included = metric.IncludedFor(service.Cycle, since, samples.Max(s => s.Date).AddDays(1));
            if (UsageMath.Threshold(used, included, metric.AlertPercents) is not { } percent)
            {
                continue;
            }

            await Send(service, metric, used, included, percent, window, since, ct).ConfigureAwait(false);
        }
    }

    private async Task Send(Service service, UsageMetric metric, decimal used, decimal included, int percent, DateOnly? window, DateOnly since, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == service.AccountId, ct).ConfigureAwait(false);
        var product = await db.Products.AsNoTracking().Where(p => p.Id == service.ProductId).Select(p => p.Name).SingleAsync(ct).ConfigureAwait(false);

        // El precio que va a cobrar el corte: el vigente al empezar la ventana.
        var price = UsageMetricPrice.On(
            await db.Set<UsageMetricPrice>().AsNoTracking().Where(p => p.MetricId == metric.Id).ToListAsync(ct).ConfigureAwait(false),
            metric.Id, service.Currency, since);
        var language = TemplateRenderer.CultureFor(account.Language);
        var culture = CultureInfo.GetCultureInfo(language);
        var model = new Dictionary<string, object?>
        {
            ["account_name"] = account.Name,
            ["service"] = string.IsNullOrEmpty(service.Label) ? product : $"{product} ({service.Label})",
            ["metric"] = metric.Name,
            ["unit"] = metric.Unit,
            ["used"] = UsageBilling.Amount(used, metric.Unit, culture),
            ["included"] = UsageBilling.Amount(included, metric.Unit, culture),
            ["percent"] = percent,
            ["over"] = percent >= 100,
            ["price"] = price is null ? null : UsageBilling.Price(price.UnitPrice, service.Currency, culture),
            ["usage_url"] = core.Options.PublicUrl is { Length: > 0 } url ? string.Create(CultureInfo.InvariantCulture, $"{url.TrimEnd('/')}/servicios/{service.Id}/uso") : null,
        };
        var key = string.Create(CultureInfo.InvariantCulture, $"{TemplateKeys.UsageThreshold}:{service.Id}:{metric.Code}:{window?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "0"}:{percent}");
        await notifier.Queue(TemplateKeys.UsageThreshold, account.Email, account.Language, model, key, account.Id, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Cierre del mes calendario (sección 4.1 de diseno/uso-medido.md): por cuenta, en su transacción y con su lock de
/// facturación, corta las métricas mensuales y las de los servicios que ya no renuevan, y emite una factura de uso por
/// moneda y modo de carga. Una cuenta que falla no frena a las demás. Repetirlo no factura dos veces: los cargos son
/// únicos por corte.
/// </summary>
public sealed class UsageCloseHandler(BillingDbContext db, UsageBilling usage, BillingCore core, CreditService credit, AuditLog audit, TimeProvider time, ILogger<UsageCloseHandler> logger) : IJobHandler
{
    public string Kind => UsageJobs.Close;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var cut = JsonDocument.Parse(job.Payload).RootElement.GetProperty("cut").Deserialize<DateOnly>();
        var started = Stopwatch.GetTimestamp();
        // Los servicios con muestras sin facturar que este cierre corta: de métricas mensuales, o de cualquiera si el
        // servicio ya no renueva. La misma regla que aplica CloseAccount.
        var pending = await db.Services.AsNoTracking()
            .Where(s => db.Set<UsageSample>().Any(x => x.ServiceId == s.Id && x.ChargeId == null && x.Date < cut
                && db.Set<UsageMetric>().Any(m => m.ProductId == s.ProductId && m.Code == x.Code && m.Active
                    && (m.Cycle == UsageCycle.CalendarMonth || s.NextDueDate == null || (s.Status != ServiceStatus.Active && s.Status != ServiceStatus.Suspended)))))
            .Select(s => new { s.Id, s.AccountId })
            .ToListAsync(ct).ConfigureAwait(false);
        var byAccount = pending.ToLookup(s => s.AccountId, s => s.Id);
        var invoices = 0;
        var errors = await Batch.Each(byAccount.Select(g => g.Key).Order(), async accountId =>
            invoices += await CloseAccount(accountId, [.. byAccount[accountId]], cut, ct).ConfigureAwait(false), logger, "el cierre de uso de la cuenta").ConfigureAwait(false);
        await audit.Record(UsageJobs.Close, string.Create(CultureInfo.InvariantCulture, $"job:{job.Id}"),
            new { cut, seconds = Stopwatch.GetElapsedTime(started).TotalSeconds, invoices }, ct).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            throw new AggregateException("El cierre de uso terminó con fallas.", errors);
        }

        return JobResult.Done;
    }

    /// <summary>Devuelve cuántas facturas emitió.</summary>
    public async Task<int> CloseAccount(long accountId, IReadOnlyList<long> serviceIds, DateOnly cut, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockAccount(db, accountId, ct).ConfigureAwait(false);
        var drafts = new Dictionary<(Currency Currency, PriceMode Mode), Document>();
        foreach (var service in await db.Services.Where(s => serviceIds.Contains(s.Id)).OrderBy(s => s.Id).ToListAsync(ct).ConfigureAwait(false))
        {
            // Lo que se cobra con la renovación también se cierra acá si el servicio ya no renueva: si no, nadie lo cobraría.
            var renews = service.NextDueDate is not null && service.Status is ServiceStatus.Active or ServiceStatus.Suspended;
            await usage.Close(service, m => m.Cycle == UsageCycle.CalendarMonth || !renews, cut, () => Task.FromResult(Draft(drafts, accountId, service.Currency, service.PriceMode)), ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (drafts.Count > 0)
        {
            var receiver = await core.Receiver(accountId, ct).ConfigureAwait(false);
            foreach (var draft in drafts.Values)
            {
                await core.IssueDraft(draft, receiver, ct).ConfigureAwait(false);
                await credit.ApplyTo(draft, ct).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Numera las facturas de uso justo antes de confirmar, como toda emisión (NumberingGuard).
        await core.Commit(tx, ct).ConfigureAwait(false);
        return drafts.Count;
    }

    /// <summary>Una factura de uso por moneda y modo de carga: el precio del excedente se lee en el modo del servicio, como su renovación.</summary>
    private Document Draft(Dictionary<(Currency Currency, PriceMode Mode), Document> drafts, long accountId, Currency currency, PriceMode mode)
    {
        if (!drafts.TryGetValue((currency, mode), out var draft))
        {
            draft = new Document(DocumentKind.Invoice, accountId, currency, mode, core.Today().AddDays(core.Options.InvoiceDueDays), time.GetUtcNow(), reason: UsageBilling.InvoiceReason);
            db.Documents.Add(draft);
            drafts[(currency, mode)] = draft;
        }

        return draft;
    }
}

/// <summary>Una métrica en la ventana abierta: lo usado hasta ahora, lo incluido y el excedente estimado.</summary>
public sealed record UsageMetricView(
    string Code,
    string Name,
    string Unit,
    UsageAggregation Aggregation,
    UsageCycle Cycle,
    decimal Included,
    decimal Used,
    DateOnly? Since,
    DateOnly? LastSample,
    decimal Excess,
    decimal? UnitPrice,
    decimal? Estimated)
{
    /// <summary>Porcentaje de lo incluido, para la barra. Sin incluido no hay porcentaje.</summary>
    public int? Percent => Included > 0m ? (int)Math.Min(999m, Math.Floor(Used * 100m / Included)) : null;
}

/// <summary>Una ventana ya cortada, con lo que se facturó.</summary>
public sealed record UsageChargeView(string Code, string Name, string Unit, DateOnly Start, DateOnly End, decimal Measured, decimal Included, decimal Excess, decimal? UnitPrice, decimal? Amount, long? DocumentId, long? DocumentNumber);

public sealed record UsageView(long ServiceId, long AccountId, long ProductId, string Product, string? Label, Currency Currency, IReadOnlyList<UsageMetricView> Metrics, IReadOnlyList<UsageChargeView> Charges);

/// <summary>El uso de un servicio como lo ven el portal, el panel y la API. Mismas cuentas que el corte.</summary>
public sealed class UsageQueries(BillingDbContext db)
{
    public async Task<UsageView?> ForService(long serviceId, CancellationToken ct = default)
    {
        var row = await db.Services.AsNoTracking().Where(s => s.Id == serviceId)
            .Join(db.Products, s => s.ProductId, p => p.Id, (s, p) => new { Service = s, Product = p.Name })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var service = row.Service;
        var metrics = await db.Set<UsageMetric>().AsNoTracking().Where(m => m.ProductId == service.ProductId && m.Active).OrderBy(m => m.Id).ToListAsync(ct).ConfigureAwait(false);
        var ids = metrics.Select(m => m.Id).ToList();
        var prices = await db.Set<UsageMetricPrice>().AsNoTracking().Where(p => ids.Contains(p.MetricId)).ToListAsync(ct).ConfigureAwait(false);
        var open = await db.Set<UsageSample>().AsNoTracking().Where(s => s.ServiceId == serviceId && s.ChargeId == null).ToListAsync(ct).ConfigureAwait(false);
        var charges = await (from c in db.Set<UsageCharge>().AsNoTracking()
                             where c.ServiceId == serviceId
                             join l in db.DocumentLines on c.DocumentLineId equals l.Id into ls
                             from l in ls.DefaultIfEmpty()
                             join d in db.Documents on l.DocumentId equals d.Id into ds
                             from d in ds.DefaultIfEmpty()
                             orderby c.End descending, c.Id descending
                             select new { Charge = c, Amount = (decimal?)l.Amount, DocumentId = (long?)d.Id, d.Number })
            .Take(48).ToListAsync(ct).ConfigureAwait(false);

        // El último corte de cada métrica, de todos los cargos y no solo de los que se muestran.
        var ends = await db.Set<UsageCharge>().AsNoTracking().Where(c => c.ServiceId == serviceId)
            .GroupBy(c => c.Code).Select(g => new { Code = g.Key, End = g.Max(c => c.End) })
            .ToDictionaryAsync(x => x.Code, x => x.End, ct).ConfigureAwait(false);

        var views = metrics.Select(m =>
        {
            var window = open.Where(s => s.Code == m.Code).ToList();
            var used = UsageMath.Measure(m.Aggregation, [.. window.Select(s => s.Value)]);

            // Como en el corte: desde el corte anterior o desde la primera muestra, hasta la última lectura.
            DateOnly? since = ends.TryGetValue(m.Code, out var end) ? end : window.Count == 0 ? null : window.Min(s => s.Date);
            var start = since ?? DateOnly.MinValue;
            var included = m.IncludedFor(service.Cycle, start, window.Count == 0 ? start : window.Max(s => s.Date).AddDays(1));
            var excess = UsageMath.Excess(used, included, m.Step);
            var price = since is null ? null : UsageMetricPrice.On(prices, m.Id, service.Currency, start);
            return new UsageMetricView(m.Code, m.Name, m.Unit, m.Aggregation, m.Cycle, included, used, since, window.Count == 0 ? null : window.Max(s => s.Date),
                excess, price?.UnitPrice, price is null ? null : Rounding.Commercial(excess * price.UnitPrice));
        }).ToList();
        var names = metrics.ToDictionary(m => m.Code, m => (m.Name, m.Unit));
        var history = charges.Select(c =>
        {
            var (name, unit) = names.TryGetValue(c.Charge.Code, out var n) ? n : (c.Charge.Code, string.Empty);
            return new UsageChargeView(c.Charge.Code, name, unit, c.Charge.Start, c.Charge.End, c.Charge.Measured, c.Charge.Included, c.Charge.Excess, c.Charge.UnitPrice, c.Amount, c.DocumentId, c.Number);
        }).ToList();
        return new UsageView(service.Id, service.AccountId, service.ProductId, row.Product, service.Label, service.Currency, views, history);
    }

    /// <summary>Las lecturas diarias más nuevas de un servicio, para el panel del staff.</summary>
    public Task<List<UsageSample>> Samples(long serviceId, int take = 62, CancellationToken ct = default) =>
        db.Set<UsageSample>().AsNoTracking().Where(s => s.ServiceId == serviceId)
            .OrderByDescending(s => s.Date).ThenBy(s => s.Code).Take(take).ToListAsync(ct);
}
