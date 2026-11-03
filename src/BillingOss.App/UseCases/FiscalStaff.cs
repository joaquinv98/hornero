using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Fiscal;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.App.UseCases;

/// <summary>La emisión de un documento como la ve el staff, con lo que el fisco tiene con el número propuesto.</summary>
public sealed record EmissionView(
    long DocumentId,
    EmissionStatus Status,
    int PointOfSale,
    int TypeCode,
    long? ProposedNumber,
    DateOnly? Date,
    int Attempts,
    string? LastError,
    JsonNode? Codes,
    long SeriesLastNumber,
    bool JobAlive,
    FoundVoucher? Found);

/// <summary>
/// Un comprobante que el fisco tiene con el número propuesto. Matches dice si coincide con el documento en todo lo que el
/// fisco informa, y Taken si ya está registrado para otro documento.
/// </summary>
public sealed record FoundVoucher(long Number, string? AuthorizationCode, bool Matches, bool Taken, JsonNode Document);

public sealed record SeriesView(long Id, int PointOfSale, int TypeCode, long LastNumber, DateOnly? LastDate, DateTimeOffset? SyncedAt, bool Emitting);

/// <summary>
/// Operaciones del staff sobre la emisión fiscal (sección 7.1 del modelo de dominio), por la API propia con el permiso
/// fiscal. Adoptar: el fisco ya tiene un comprobante con el número propuesto y el staff confirma, con su código de
/// autorización, que es el de este documento. Reintentar: una emisión rechazada o en conflicto vuelve a pendiente y
/// se encola. Ajustar la serie: el último número registrado, con motivo. Cada una queda en la auditoría, ninguna toca
/// una serie con una emisión en curso y ninguna llama a una instancia de producción con la traba puesta.
/// </summary>
public sealed class FiscalStaffService(BillingDbContext db, ModuleRegistry registry, ModuleClient client, JobQueue jobs, AuditLog audit, TimeProvider time, IOptions<FiscalOptions> fiscal)
{
    public async Task<EmissionView> Show(long documentId, CancellationToken ct = default)
    {
        var (emission, series, issuer, doc) = await Load(documentId, ct).ConfigureAwait(false);
        FoundVoucher? found = null;
        if (emission.Status != EmissionStatus.Authorized && emission.ProposedNumber is { } number)
        {
            // Con la traba de producción puesta se muestra la emisión sin consultar al fisco.
            var module = await Instance(issuer, ct).ConfigureAwait(false);
            if (!Locked(module) && await Find(module, series, number, ct).ConfigureAwait(false) is { } voucher)
            {
                var (payload, _) = await Payload(doc, issuer, series, emission, number, voucher, ct).ConfigureAwait(false);
                found = new FoundVoucher(number, voucher["authorization_code"]?.ToString(), FiscalPayload.Matches(voucher, payload), await Taken(series, number, ct).ConfigureAwait(false), voucher);
            }
        }

        return new EmissionView(
            documentId,
            emission.Status,
            emission.PointOfSale,
            emission.TypeCode,
            emission.ProposedNumber,
            emission.Date,
            emission.Attempts,
            emission.LastError,
            emission.Codes is null ? null : JsonNode.Parse(emission.Codes),
            series.LastNumber,
            await LiveJob(documentId, ct).ConfigureAwait(false),
            found);
    }

    /// <summary>
    /// Adopta el comprobante que el fisco tiene con el número propuesto. La confirmación es el código de autorización
    /// que informa el fisco: el staff lo vio en <see cref="Show"/> y lo repite. No exige que los datos coincidan, para
    /// eso está el staff, pero sí que el número no esté registrado para otro documento.
    /// </summary>
    public async Task<FiscalDocument> Adopt(long documentId, string authorizationCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authorizationCode))
        {
            throw new ValidationException("Hace falta el código de autorización del comprobante que se adopta, como lo informa el fisco.");
        }

        var (emission, series, issuer, doc) = await Load(documentId, ct).ConfigureAwait(false);
        var module = await Module(issuer, ct).ConfigureAwait(false);
        await Adoptable(emission, documentId, ct).ConfigureAwait(false);
        var number = emission.ProposedNumber!.Value;

        // El fisco se consulta fuera de la transacción (sección 7 del modelo): lo que tiene no cambia.
        var found = await Find(module, series, number, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"El fisco no tiene el comprobante {series.PointOfSale}-{series.TypeCode}-{number}: no hay nada que adoptar, reintentá la emisión.");
        var code = found["authorization_code"]?.ToString();
        if (code != authorizationCode.Trim())
        {
            throw new ValidationException($"El código de autorización no es el que informa el fisco para el comprobante {number}.");
        }

        var (payload, date) = await Payload(doc, issuer, series, emission, number, found, ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "fiscal.emissions", [emission.Id], ct).ConfigureAwait(false);
        await Sql.LockRows(db, "fiscal.series", [series.Id], ct).ConfigureAwait(false);
        await db.Entry(emission).ReloadAsync(ct).ConfigureAwait(false);
        await db.Entry(series).ReloadAsync(ct).ConfigureAwait(false);
        await Adoptable(emission, documentId, ct).ConfigureAwait(false);
        if (await Taken(series, number, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"El comprobante {number} ya está registrado para otro documento.");
        }

        // Algo salió de la vía normal: la serie deja de darse por comprobada, porque el fisco puede tener más
        // comprobantes, y la próxima emisión consulta el último número.
        series.SyncedAt = null;
        var now = time.GetUtcNow();
        await FiscalEmissionHandler.Authorize(db, jobs, now, emission, series, documentId, number, date, found, payload, ct).ConfigureAwait(false);
        await audit.Record("fiscal.adopt", $"document:{documentId}", new { point_of_sale = series.PointOfSale, type_code = series.TypeCode, number, authorization_code = code }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return await db.FiscalDocuments.SingleAsync(f => f.DocumentId == documentId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Vuelve a encolar la emisión. Una rechazada o en conflicto pasa a pendiente. Una pendiente o incierta cuyo
    /// trabajo murió solo se encola: la incierta se resuelve consultando, nunca emitiendo a ciegas.
    /// </summary>
    public async Task<EmissionStatus> Retry(long documentId, CancellationToken ct = default)
    {
        var (emission, _, issuer, doc) = await Load(documentId, ct).ConfigureAwait(false);
        await Module(issuer, ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "fiscal.emissions", [emission.Id], ct).ConfigureAwait(false);
        await db.Entry(emission).ReloadAsync(ct).ConfigureAwait(false);
        if (emission.Status == EmissionStatus.Authorized || await db.FiscalDocuments.AnyAsync(f => f.DocumentId == documentId, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"El documento {documentId} ya tiene comprobante fiscal.");
        }

        if (doc.Kind == DocumentKind.Invoice && doc.Status == InvoiceStatus.Cancelled)
        {
            throw new InvalidOperationException(FiscalEmissionHandler.CancelledUnpaid);
        }

        if (await LiveJob(documentId, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"La emisión del documento {documentId} ya tiene un trabajo en curso: esperá a que termine o muera.");
        }

        var from = emission.Status;
        if (from is EmissionStatus.Rejected or EmissionStatus.Conflict)
        {
            emission.MoveTo(EmissionStatus.Pending, time.GetUtcNow());
        }

        var key = await FiscalEmissionHandler.EntityKey(db, doc, ct).ConfigureAwait(false);
        await jobs.Enqueue(JobKinds.FiscalEmit, new { document_id = documentId, sweep = true }, key, ct: ct).ConfigureAwait(false);
        await audit.Record("fiscal.retry", $"document:{documentId}", new { from = from.ToString(), to = emission.Status.ToString() }, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return emission.Status;
    }

    public async Task<IReadOnlyList<SeriesView>> Series(CancellationToken ct = default)
    {
        var issuer = await Issuer(ct).ConfigureAwait(false);
        var views = new List<SeriesView>();
        foreach (var series in await db.FiscalSeries.AsNoTracking().Where(s => s.IssuerId == issuer.Id).OrderBy(s => s.PointOfSale).ThenBy(s => s.TypeCode).ToListAsync(ct).ConfigureAwait(false))
        {
            views.Add(await View(issuer, series, ct).ConfigureAwait(false));
        }

        return views;
    }

    /// <summary>
    /// Fija el último número registrado de la serie, con motivo. La serie deja de darse por sincronizada: la próxima
    /// emisión consulta el último número al fisco y frena si no coincide. No se ajusta una serie con emisiones en curso
    /// ni con una emisión incierta, que primero se adopta o se reintenta.
    /// </summary>
    public async Task<SeriesView> AdjustSeries(int pointOfSale, int typeCode, long lastNumber, DateOnly? lastDate, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ValidationException("Hace falta el motivo del ajuste.");
        }

        if (lastNumber < 0)
        {
            throw new ValidationException("El último número no puede ser negativo.");
        }

        var issuer = await Issuer(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var series = await db.FiscalSeries.SingleOrDefaultAsync(s => s.IssuerId == issuer.Id && s.PointOfSale == pointOfSale && s.TypeCode == typeCode, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la serie {pointOfSale}-{typeCode}.");
        await Sql.LockRows(db, "fiscal.series", [series.Id], ct).ConfigureAwait(false);
        await db.Entry(series).ReloadAsync(ct).ConfigureAwait(false);
        if (await Emitting(issuer, series, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"La serie {pointOfSale}-{typeCode} tiene emisiones en curso: esperá a que terminen o mueran antes de ajustarla.");
        }

        if (await db.FiscalEmissions.AnyAsync(e => e.IssuerId == issuer.Id && e.PointOfSale == pointOfSale && e.TypeCode == typeCode && (e.Status == EmissionStatus.Sending || e.Status == EmissionStatus.Unknown), ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"La serie {pointOfSale}-{typeCode} tiene una emisión con resultado incierto: adoptala o reintentala antes de ajustar.");
        }

        var before = series.LastNumber;
        series.LastNumber = lastNumber;
        series.LastDate = lastDate ?? series.LastDate;
        series.SyncedAt = null;
        await audit.Record("fiscal.series.adjust", $"series:{series.Id}", new { point_of_sale = pointOfSale, type_code = typeCode, from = before, to = lastNumber, last_date = series.LastDate, reason }, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return await View(issuer, series, ct).ConfigureAwait(false);
    }

    private async Task<(FiscalEmission Emission, FiscalSeries Series, IssuerProfile Issuer, Document Doc)> Load(long documentId, CancellationToken ct)
    {
        var doc = await db.Documents.Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == documentId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el documento {documentId}.");
        var emission = await db.FiscalEmissions.SingleOrDefaultAsync(e => e.DocumentId == documentId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"El documento {documentId} no tiene emisión fiscal.");
        var issuer = await db.IssuerProfiles.SingleAsync(i => i.Id == emission.IssuerId, ct).ConfigureAwait(false);
        var series = await db.FiscalSeries.SingleOrDefaultAsync(s => s.IssuerId == emission.IssuerId && s.PointOfSale == emission.PointOfSale && s.TypeCode == emission.TypeCode, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la serie {emission.PointOfSale}-{emission.TypeCode} de la emisión.");
        return (emission, series, issuer, doc);
    }

    private async Task<IssuerProfile> Issuer(CancellationToken ct) =>
        await db.IssuerProfiles.SingleOrDefaultAsync(i => i.Active, ct).ConfigureAwait(false)
        ?? throw new KeyNotFoundException("No hay un emisor activo.");

    /// <summary>Solo se adopta sobre una emisión frenada con número propuesto: pendiente, incierta o en conflicto, sin trabajo vivo.</summary>
    private async Task Adoptable(FiscalEmission emission, long documentId, CancellationToken ct)
    {
        if (emission.Status == EmissionStatus.Authorized || await db.FiscalDocuments.AnyAsync(f => f.DocumentId == documentId, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"El documento {documentId} ya tiene comprobante fiscal.");
        }

        if (emission.Status is not (EmissionStatus.Pending or EmissionStatus.Unknown or EmissionStatus.Conflict))
        {
            throw new InvalidOperationException($"La emisión está {emission.Status}: solo se adopta una pendiente, incierta o en conflicto.");
        }

        if (emission.ProposedNumber is null)
        {
            throw new InvalidOperationException("La emisión nunca propuso un número: no hay nada que adoptar, reintentá la emisión.");
        }

        if (await LiveJob(documentId, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"La emisión del documento {documentId} tiene un trabajo en curso: esperá a que termine o muera.");
        }
    }

    private Task<ResolvedInstance> Instance(IssuerProfile issuer, CancellationToken ct) =>
        registry.Resolve(issuer.ModuleInstanceId ?? throw new InvalidOperationException("El emisor no tiene instancia fiscal configurada."), ct);

    /// <summary>La traba de producción: una instancia de producción sin Fiscal:ProductionEnabled no recibe nada.</summary>
    private bool Locked(ResolvedInstance module) => FiscalEmissionHandler.IsProduction(module) && !fiscal.Value.ProductionEnabled;

    /// <summary>La instancia fiscal del emisor para una operación que la llama o le encola trabajo: con la traba puesta no sale.</summary>
    private async Task<ResolvedInstance> Module(IssuerProfile issuer, CancellationToken ct)
    {
        var module = await Instance(issuer, ct).ConfigureAwait(false);
        return Locked(module) ? throw new InvalidOperationException(FiscalEmissionHandler.ProductionDisabled) : module;
    }

    /// <summary>El comprobante que el fisco tiene con ese número, o null si no existe.</summary>
    private async Task<JsonNode?> Find(ResolvedInstance module, FiscalSeries series, long number, CancellationToken ct)
    {
        var input = new JsonObject { ["point_of_sale"] = series.PointOfSale, ["type_code"] = series.TypeCode, ["number"] = number };
        var result = await client.Call(module.Endpoint, "/v0/fiscal/find", new ModuleRequest(module.Data, [], input), null, module.Timeout("find"), ct).ConfigureAwait(false);
        if (result.InstanceState is { } state)
        {
            await registry.SaveState(module, state, ct).ConfigureAwait(false);
        }

        return result.Error switch
        {
            { Code: ErrorCodes.NotFound } => null,
            { } error when ErrorCodes.IsRetryable(error.Code) => throw new RetryLaterException($"{error.Code}: {error.Message}", error.RetryAfter is { } s ? TimeSpan.FromSeconds(s) : null),
            { } error => throw new PermanentFailureException($"{error.Code}: {error.Message}"),
            null => result.Response?.Result,
        };
    }

    /// <summary>El documento como se le habría mandado al fisco con ese número, con la fecha y la cotización que el fisco registró.</summary>
    private async Task<(JsonObject Payload, DateOnly Date)> Payload(Document doc, IssuerProfile issuer, FiscalSeries series, FiscalEmission emission, long number, JsonNode found, CancellationToken ct)
    {
        var receiver = JsonSerializer.Deserialize<ReceiverSnapshot>(doc.Receiver!)!;
        var date = found["document"]?["date"] is JsonNode d && DateOnly.TryParse(d.ToString(), CultureInfo.InvariantCulture, out var parsed) ? parsed : emission.Date ?? DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var payload = await FiscalPayload.Build(db, doc, receiver, issuer, series, number, date, ct).ConfigureAwait(false);
        if (emission.RequestId is { } requestId)
        {
            payload["wsfex_id"] = requestId;
        }

        if (found["document"]?["exchange_rate"] is JsonNode rate)
        {
            payload["document"]!["exchange_rate"] = rate.DeepClone();
        }

        return (payload, date);
    }

    private Task<bool> Taken(FiscalSeries series, long number, CancellationToken ct) =>
        db.FiscalDocuments.AnyAsync(f => f.IssuerId == series.IssuerId && f.PointOfSale == series.PointOfSale && f.TypeCode == series.TypeCode && f.Number == number, ct);

    /// <summary>Hay un trabajo de emisión pendiente o corriendo para el documento.</summary>
    private async Task<bool> LiveJob(long documentId, CancellationToken ct) =>
        (await db.Jobs.Where(j => j.Kind == JobKinds.FiscalEmit && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
            .Select(j => j.Payload).ToListAsync(ct).ConfigureAwait(false))
        .Any(p => JsonDocument.Parse(p).RootElement.GetProperty("document_id").GetInt64() == documentId);

    /// <summary>Hay un trabajo de emisión pendiente o corriendo con la clave de la serie.</summary>
    private Task<bool> Emitting(IssuerProfile issuer, FiscalSeries series, CancellationToken ct)
    {
        var key = Subscriptions.FiscalSeries(issuer.Id, series.PointOfSale, series.TypeCode);
        return db.Jobs.AnyAsync(j => j.Kind == JobKinds.FiscalEmit && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running) && j.EntityKey == key, ct);
    }

    private async Task<SeriesView> View(IssuerProfile issuer, FiscalSeries series, CancellationToken ct) =>
        new(series.Id, series.PointOfSale, series.TypeCode, series.LastNumber, series.LastDate, series.SyncedAt, await Emitting(issuer, series, ct).ConfigureAwait(false));
}
