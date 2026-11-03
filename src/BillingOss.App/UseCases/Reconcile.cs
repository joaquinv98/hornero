using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Fiscal;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BillingOss.App.UseCases;

/// <param name="Since">Desde cuándo pedirles a las pasarelas lo cobrado. En una restauración, el punto al que se volvió.</param>
/// <param name="CounterGap">Cuánto saltar cada contador de numeración comercial, para no repetir números que ya viajaron por correo.</param>
/// <param name="DryRun">Solo informa: no adopta, no salta contadores, no guarda hallazgos ni pagos sin casar.</param>
public sealed record ReconcileRequest(DateTimeOffset Since, long CounterGap = 0, bool DryRun = false);

public sealed record ReconcileReport(long Timeline, DateTimeOffset Since, bool DryRun, IReadOnlyList<ReconcileFinding> Findings, int Adopted, int UnmatchedPayments);

/// <summary>
/// Conciliación con el mundo exterior después de una promoción o de una restauración (D-14): el fisco, las pasarelas
/// y los sistemas aprovisionados siguieron adelante y la base no. Por cada serie fiscal trae los comprobantes que el
/// fisco tiene de más, adopta los que coinciden con un solo documento local sin comprobante y registra como
/// hallazgo los huérfanos y los ambiguos, y deja la serie al día para que la emisión siga. Salta la numeración
/// comercial si se le pide. Les pide a las pasarelas los pagos desde el punto y deja en payments.unmatched los que no
/// tienen intento propio. Les pide a los módulos de aprovisionamiento que saben listar lo que lleva marca del núcleo y
/// registra lo que no tiene servicio. Todo queda en la auditoría. Corre como comando (reconcile) o como trabajo cuando
/// el planificador ve una línea de tiempo nueva.
/// </summary>
public sealed class ReconcileService(
    BillingDbContext db,
    ModuleRegistry registry,
    ModuleClient client,
    GatewayPayments gateways,
    AuditLog audit,
    TimeProvider time,
    IOptions<FiscalOptions> fiscal,
    ILogger<ReconcileService> logger)
{
    public const string AuditAction = "reconcile";

    /// <summary>
    /// La línea de tiempo actual de PostgreSQL: la del WAL que se está escribiendo, que cambia en el momento de cada
    /// promoción y de cada restauración. No la del último checkpoint (pg_control_checkpoint), que después de una
    /// promoción rápida queda vieja hasta que ese checkpoint termina, minutos en los que el planificador seguiría
    /// marcando la hora y el "desde" de la conciliación se correría más allá de lo perdido.
    /// </summary>
    public static Task<long> CurrentTimeline(BillingDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.SqlQuery<long>($"SELECT ('x' || substr(pg_walfile_name(pg_current_wal_lsn()), 1, 8))::bit(32)::bigint AS \"Value\"").SingleAsync(ct);
    }

    /// <summary>Deja marcada la línea de tiempo vista, con la hora.</summary>
    public static Task MarkTimeline(BillingDbContext db, long timeline, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform.timeline (id, timeline_id, seen_at) VALUES (1, {timeline}, {now})
            ON CONFLICT (id) DO UPDATE SET timeline_id = EXCLUDED.timeline_id, seen_at = EXCLUDED.seen_at
            """, ct);
    }

    /// <summary>
    /// Si la línea de tiempo cambió desde la última marca, desde cuándo conciliar: la hora de esa marca, que en una
    /// base restaurada es anterior al punto de restauración. Null si no cambió.
    /// </summary>
    public async Task<DateTimeOffset?> SinceIfTimelineChanged(CancellationToken ct)
    {
        var current = await CurrentTimeline(db, ct).ConfigureAwait(false);
        var mark = await db.Timeline.AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return mark is not null && mark.TimelineId != current ? mark.SeenAt : null;
    }

    public async Task<ReconcileReport> Run(ReconcileRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = time.GetUtcNow();
        var timeline = await CurrentTimeline(db, ct).ConfigureAwait(false);
        var findings = new List<ReconcileFinding>();
        var adopted = await Fiscal(request, findings, now, ct).ConfigureAwait(false);
        await Counters(request, findings, now, ct).ConfigureAwait(false);
        var unmatched = await Payments(request, now, ct).ConfigureAwait(false);
        await Provisioning(request, findings, now, ct).ConfigureAwait(false);

        if (!request.DryRun)
        {
            foreach (var f in findings)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO platform.reconcile_findings (kind, subject, details, created_at)
                    VALUES ({f.Kind}, {f.Subject}, {f.Details}::jsonb, {f.CreatedAt})
                    ON CONFLICT (kind, subject) WHERE resolved_at IS NULL DO NOTHING
                    """, ct).ConfigureAwait(false);
            }

            await MarkTimeline(db, timeline, now, ct).ConfigureAwait(false);
        }

        await audit.Record(
            AuditAction,
            $"timeline:{timeline}",
            new { since = request.Since, counter_gap = request.CounterGap, dry_run = request.DryRun, findings = findings.Count, adopted, unmatched_payments = unmatched },
            ct).ConfigureAwait(false);
        return new ReconcileReport(timeline, request.Since, request.DryRun, findings, adopted, unmatched);
    }

    // ---- Fisco ----

    private async Task<int> Fiscal(ReconcileRequest request, List<ReconcileFinding> findings, DateTimeOffset now, CancellationToken ct)
    {
        var issuer = await db.IssuerProfiles.SingleOrDefaultAsync(i => i.Active, ct).ConfigureAwait(false);
        if (issuer?.ModuleInstanceId is null)
        {
            return 0;
        }

        var module = await registry.Resolve(issuer.ModuleInstanceId.Value, ct).ConfigureAwait(false);
        if (FiscalEmissionHandler.IsProduction(module) && !fiscal.Value.ProductionEnabled)
        {
            // Con la traba de producción puesta no se le pregunta nada al fisco real.
            logger.LogWarning("La conciliación no consulta al fisco real mientras Fiscal:ProductionEnabled esté apagado.");
            return 0;
        }

        // Todas las series que el emisor puede usar, no solo las que la base conoce: restaurada a antes de la primera
        // emisión de una serie, la fila no existe y el fisco igual tiene comprobantes. La que el fisco tiene y acá
        // falta se crea al avanzarla (en modo de prueba no se guarda nada).
        var adopted = 0;
        var known = await db.FiscalSeries.Where(s => s.IssuerId == issuer.Id).OrderBy(s => s.Id).ToListAsync(ct).ConfigureAwait(false);
        var unknown = FiscalTypes.Series(issuer)
            .Where(s => !known.Any(k => k.PointOfSale == s.PointOfSale && k.TypeCode == s.TypeCode))
            .Select(s => new FiscalSeries(issuer.Id, s.PointOfSale, s.TypeCode));
        foreach (var series in known.Concat(unknown))
        {
            var last = await Call(module, "fiscal", "last_number", new JsonObject { ["point_of_sale"] = series.PointOfSale, ["type_code"] = series.TypeCode }, ct).ConfigureAwait(false);
            var atFisco = last.Result["number"]?.GetValue<long>() ?? throw new PermanentFailureException("El módulo fiscal no informó el último número.");
            module = last.Module;
            if (EmissionRules.CheckSeries(atFisco, series.LastNumber) != SeriesCheck.MustReconcile)
            {
                continue;
            }

            DateOnly? lastDate = null;
            for (var number = series.LastNumber + 1; number <= atFisco; number++)
            {
                var subject = string.Create(CultureInfo.InvariantCulture, $"{series.PointOfSale}-{series.TypeCode}-{number}");
                var input = new JsonObject { ["point_of_sale"] = series.PointOfSale, ["type_code"] = series.TypeCode, ["number"] = number };
                var find = await client.Call(module.Endpoint, "/v0/fiscal/find", new ModuleRequest(module.Data, [], input), null, module.Timeout("find"), ct).ConfigureAwait(false);
                module = await SaveState(module, find, ct).ConfigureAwait(false);
                if (find.Error is { Code: ErrorCodes.NotFound })
                {
                    findings.Add(Finding(ReconcileFinding.FiscalOrphan, subject, new { missing = true }, now));
                    continue;
                }

                var found = Unwrap(find, "find");
                var document = found["document"] ?? found;
                var date = DateOnly.Parse(document["date"]!.GetValue<string>(), CultureInfo.InvariantCulture);
                lastDate = date;
                var candidates = await Candidates(issuer, series, number, date, found, ct).ConfigureAwait(false);
                if (candidates.Count == 1)
                {
                    adopted++;
                    if (!request.DryRun)
                    {
                        await Adopt(issuer, series, number, date, found, candidates[0], now, ct).ConfigureAwait(false);
                    }

                    continue;
                }

                var summary = new
                {
                    date = document["date"]?.ToString(),
                    total = document["amounts"]?["total"]?.ToString(),
                    currency = document["currency"]?.ToString(),
                    authorization_code = found["authorization_code"]?.ToString(),
                    candidates = candidates.Select(c => c.Doc.Id).ToArray(),
                };
                findings.Add(Finding(candidates.Count == 0 ? ReconcileFinding.FiscalOrphan : ReconcileFinding.FiscalAmbiguous, subject, summary, now));
            }

            if (!request.DryRun)
            {
                // Los que faltaban ya están adoptados o registrados: nunca se vuelven a pedir, y la emisión sigue.
                if (series.Id == 0)
                {
                    db.FiscalSeries.Add(series);
                }

                series.LastNumber = atFisco;
                series.LastDate = lastDate ?? series.LastDate;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        if (!request.DryRun)
        {
            // Las emisiones frenadas por la serie vuelven a pendientes: el barrido diario las reencola con la serie al día.
            foreach (var emission in await db.FiscalEmissions
                .Where(e => e.IssuerId == issuer.Id && e.Status == EmissionStatus.Conflict && !db.FiscalDocuments.Any(f => f.DocumentId == e.DocumentId))
                .ToListAsync(ct).ConfigureAwait(false))
            {
                emission.MoveTo(EmissionStatus.Pending, now);
                emission.LastError = "Serie conciliada: vuelve a pendiente.";
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return adopted;
    }

    /// <summary>
    /// Documentos locales sin comprobante que coinciden con lo que el fisco tiene bajo ese número, con la comparación
    /// estricta de la emisión (FiscalPayload.Matches). Uno solo se adopta. Dos o más, nadie: va al staff.
    /// </summary>
    private async Task<List<(Document Doc, JsonObject Payload, FiscalEmission? Emission)>> Candidates(IssuerProfile issuer, FiscalSeries series, long number, DateOnly date, JsonNode found, CancellationToken ct)
    {
        var f = found["document"] ?? found;
        if (!decimal.TryParse(f["amounts"]?["total"]?.ToString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var total))
        {
            return [];
        }

        var candidates = new List<(Document, JsonObject, FiscalEmission?)>();
        var docs = await db.Documents.Include(d => d.Lines)
            .Where(d => d.IssueDate != null && !d.Imported && d.Receiver != null && d.Total == total
                && !db.FiscalDocuments.Any(x => x.DocumentId == d.Id)
                && !db.Accounts.Any(a => a.Id == d.AccountId && a.NoFiscalDocument))
            .OrderBy(d => d.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var doc in docs)
        {
            var receiver = JsonSerializer.Deserialize<ReceiverSnapshot>(doc.Receiver!)!;
            var original = await FiscalPayload.Original(db, doc, issuer, ct).ConfigureAwait(false);
            (int PointOfSale, int TypeCode) expected;
            try
            {
                expected = FiscalTypes.For(issuer, doc, receiver, original?.TypeCode);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (expected != (series.PointOfSale, series.TypeCode) || (doc.Kind != DocumentKind.Invoice && original is null))
            {
                continue;
            }

            var emission = await db.FiscalEmissions.SingleOrDefaultAsync(e => e.DocumentId == doc.Id, ct).ConfigureAwait(false);
            var payload = await FiscalPayload.Build(db, doc, receiver, issuer, series, number, date, ct).ConfigureAwait(false);
            if (emission?.RequestId is { } requestId)
            {
                payload["wsfex_id"] = requestId;
            }

            if (f["exchange_rate"] is JsonNode rate)
            {
                payload["document"]!["exchange_rate"] = rate.DeepClone();
            }

            if (FiscalPayload.Matches(found, payload))
            {
                candidates.Add((doc, payload, emission));
            }
        }

        return candidates;
    }

    private async Task Adopt(IssuerProfile issuer, FiscalSeries series, long number, DateOnly date, JsonNode found, (Document Doc, JsonObject Payload, FiscalEmission? Emission) candidate, DateTimeOffset now, CancellationToken ct)
    {
        var (doc, payload, emission) = candidate;
        if (emission is not null && emission.Status != EmissionStatus.Authorized)
        {
            if (emission.Status == EmissionStatus.Rejected)
            {
                emission.MoveTo(EmissionStatus.Pending, now);
            }

            emission.MoveTo(EmissionStatus.Authorized, now);
            emission.LastError = null;
        }

        // Sin aviso al cliente: el comprobante salió antes de la restauración y el aviso de entonces ya viajó.
        db.FiscalDocuments.Add(new FiscalDocument
        {
            DocumentId = doc.Id,
            EmissionId = emission?.Id,
            IssuerId = issuer.Id,
            PointOfSale = series.PointOfSale,
            TypeCode = series.TypeCode,
            Number = number,
            Date = date,
            AuthorizationCode = found["authorization_code"]?.GetValue<string>() ?? string.Empty,
            AuthorizationDue = found["authorization_due"] is JsonNode due ? DateOnly.Parse(due.GetValue<string>(), CultureInfo.InvariantCulture) : null,
            Payload = payload.ToJsonString(),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Conciliación: el comprobante {PointOfSale}-{TypeCode}-{Number} se adoptó para el documento {DocumentId}.", series.PointOfSale, series.TypeCode, number, doc.Id);
    }

    // ---- Numeración comercial ----

    private async Task Counters(ReconcileRequest request, List<ReconcileFinding> findings, DateTimeOffset now, CancellationToken ct)
    {
        if (request.CounterGap <= 0)
        {
            return;
        }

        var counters = new List<(string Kind, long Next)>();
        await using (var cmd = await Sql.Command(db, "SELECT kind, next FROM billing.counters ORDER BY kind", ct).ConfigureAwait(false))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                counters.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        foreach (var (kind, next) in counters)
        {
            findings.Add(Finding(ReconcileFinding.CounterGap, kind, new { from = next, to = next + request.CounterGap, gap = request.CounterGap }, now));
            if (!request.DryRun)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE billing.counters SET next = next + {request.CounterGap} WHERE kind = {kind}", ct).ConfigureAwait(false);
            }
        }
    }

    // ---- Pasarelas ----

    private async Task<int> Payments(ReconcileRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var instances = await db.Database.SqlQueryRaw<long>("""
            SELECT i.id AS "Value" FROM platform.module_instances i
            JOIN platform.module_installations m ON m.id = i.installation_id
            WHERE m.manifest -> 'kinds' @> '["payment"]'::jsonb AND m.manifest -> 'capabilities' @> '["payment.reconcile"]'::jsonb
            ORDER BY i.id
            """).ToListAsync(ct).ConfigureAwait(false);
        var unmatched = 0;
        foreach (var instance in instances)
        {
            unmatched += await gateways.Reconcile(instance, request.Since, now, request.DryRun, ct).ConfigureAwait(false);
        }

        return unmatched;
    }

    // ---- Aprovisionamiento ----

    private async Task Provisioning(ReconcileRequest request, List<ReconcileFinding> findings, DateTimeOffset now, CancellationToken ct)
    {
        var instances = new List<(long Id, string Name, bool Listed)>();
        await using (var cmd = await Sql.Command(db, """
            SELECT i.id, i.name, coalesce(m.manifest -> 'capabilities' @> '["provisioning.list"]'::jsonb, false)
            FROM platform.module_instances i
            JOIN platform.module_installations m ON m.id = i.installation_id
            WHERE m.manifest -> 'kinds' @> '["provisioning"]'::jsonb AND m.base_url <> @manual
            ORDER BY i.id
            """, ct, ("manual", ManualModule.Address)).ConfigureAwait(false))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                instances.Add((reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2)));
            }
        }

        foreach (var (id, name, listed) in instances)
        {
            var subject = string.Create(CultureInfo.InvariantCulture, $"instance:{id}");
            if (!listed)
            {
                // El módulo no sabe listar lo que lleva marca: lo que se creó después del punto se revisa en su panel.
                findings.Add(Finding(ReconcileFinding.ProvisioningUnlisted, subject, new { instance = name }, now));
                continue;
            }

            var module = await registry.Resolve(id, ct).ConfigureAwait(false);
            int? offset = 0;
            while (offset is { } current)
            {
                var page = await Call(module, "provisioning", "list", new JsonObject { ["offset"] = current }, ct).ConfigureAwait(false);
                module = page.Module;
                foreach (var resource in page.Result["resources"]?.AsArray() ?? [])
                {
                    var serviceId = resource?["service_id"] is JsonValue v && v.TryGetValue<long>(out var sid) ? sid : (long?)null;
                    var known = serviceId is { } s && await db.Services.AnyAsync(x => x.Id == s && x.ModuleInstanceId == id, ct).ConfigureAwait(false);
                    if (!known)
                    {
                        var external = resource?["external_ids"]?.ToJsonString() ?? "{}";
                        var key = serviceId?.ToString(CultureInfo.InvariantCulture) ?? external;
                        findings.Add(Finding(
                            ReconcileFinding.ProvisioningOrphan,
                            Cut($"{subject}:{key}", 200),
                            new { instance = name, service_id = serviceId, external_ids = resource?["external_ids"]?.DeepClone(), label = resource?["label"]?.ToString() },
                            now));
                    }
                }

                offset = page.Result["next_offset"]?.GetValue<int>();
            }
        }
    }

    // ---- Llamadas ----

    /// <summary>Llama sin clave de idempotencia, guarda el estado que vuelva (también en error) y falla con el error del módulo.</summary>
    private async Task<(JsonObject Result, ResolvedInstance Module)> Call(ResolvedInstance module, string kind, string operation, JsonObject input, CancellationToken ct)
    {
        var result = await client.Call(module.Endpoint, $"/v0/{kind}/{operation}", new ModuleRequest(module.Data, [], input), null, module.Timeout(operation), ct).ConfigureAwait(false);
        module = await SaveState(module, result, ct).ConfigureAwait(false);
        return (Unwrap(result, operation) as JsonObject ?? [], module);
    }

    private async Task<ResolvedInstance> SaveState(ResolvedInstance module, ModuleCallResult result, CancellationToken ct) =>
        result.InstanceState is { } state ? await registry.SaveState(module, state, ct).ConfigureAwait(false) : module;

    private static JsonNode Unwrap(ModuleCallResult result, string operation)
    {
        if (result.Error is { } error)
        {
            throw ProtocolServiceModules.Retryable(error)
                ? new RetryLaterException($"{operation}: {error.Code}: {error.Message}", error.RetryAfter is { } s ? TimeSpan.FromSeconds(s) : null)
                : new PermanentFailureException($"{operation}: {error.Code}: {error.Message}", error.Code);
        }

        return result.Response?.Result ?? new JsonObject();
    }

    private static ReconcileFinding Finding(string kind, string subject, object details, DateTimeOffset now) =>
        new() { Kind = kind, Subject = subject, Details = JsonSerializer.Serialize(details), CreatedAt = now };

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>La conciliación como trabajo: la encola el planificador cuando ve una línea de tiempo nueva.</summary>
/// <remarks>
/// No se cruza con los trabajos fiscales. Cada serie tiene su clave de entidad y la conciliación lleva la clave fiscal
/// suelta, así que la cola ya no los pone en fila: mientras haya una conciliación pendiente o corriendo, los trabajos
/// fiscales esperan (<see cref="Pending"/>), y la conciliación espera a que terminen los que ya corrían.
/// </remarks>
public sealed class ReconcileHandler(ReconcileService service, BillingDbContext db, ILogger<ReconcileHandler> logger) : IJobHandler
{
    /// <summary>Cada cuánto vuelve a mirar el que espera, sin gastar un intento.</summary>
    public static TimeSpan Wait { get; } = TimeSpan.FromSeconds(15);

    private static readonly string[] FiscalKinds = [JobKinds.FiscalEmit, JobKinds.FiscalCheck, JobKinds.FiscalRate];

    public string Kind => JobKinds.Reconcile;

    /// <summary>Hay una conciliación por línea de tiempo pendiente o corriendo: los trabajos fiscales esperan.</summary>
    public static Task<bool> Pending(BillingDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Jobs.AnyAsync(j => j.Kind == JobKinds.Reconcile && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
    }

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Los trabajos fiscales nuevos ya esperan a esta. Los que corrían desde antes terminan primero.
        if (await db.Jobs.AnyAsync(j => FiscalKinds.Contains(j.Kind) && j.Status == JobStatus.Running, ct).ConfigureAwait(false))
        {
            return JobResult.PollLater(Wait);
        }

        var since = JsonDocument.Parse(job.Payload).RootElement.GetProperty("since").GetDateTimeOffset();
        var report = await service.Run(new ReconcileRequest(since), ct).ConfigureAwait(false);
        logger.LogWarning(
            "Conciliación por cambio de línea de tiempo ({Timeline}) desde {Since:O}: {Findings} hallazgos, {Adopted} comprobantes adoptados, {Unmatched} pagos sin casar.",
            report.Timeline, since, report.Findings.Count, report.Adopted, report.UnmatchedPayments);
        return JobResult.Done;
    }
}
