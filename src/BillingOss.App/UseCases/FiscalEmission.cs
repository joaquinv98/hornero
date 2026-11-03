using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Fiscal;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.App.UseCases;

/// <summary>
/// Emisión fiscal de un documento (secciones 7 y 7.1 del modelo de dominio y 9.4 del protocolo). Corre como trabajo
/// con la clave de entidad de su serie (<see cref="EntityKey"/>): las emisiones de una serie van de a una y en orden,
/// y las de series distintas no se esperan. Cada corrida avanza un paso de la máquina de estados y guarda antes de
/// llamar al fisco.
/// </summary>
public sealed class FiscalEmissionHandler(BillingDbContext db, ModuleRegistry registry, ModuleClient client, BillingCore core, JobQueue jobs, TimeProvider time, IOptions<FiscalOptions> fiscal) : IJobHandler
{
    /// <summary>Espera antes de dar por inexistente un comprobante incierto, por si el pedido sigue en proceso.</summary>
    public static TimeSpan UncertainWait { get; } = TimeSpan.FromSeconds(60);

    /// <summary>Espera cuando el fisco todavía no publicó la cotización (un feriado largo): la emisión queda pendiente.</summary>
    public static TimeSpan NoRateWait { get; } = TimeSpan.FromHours(4);

    /// <summary>Motivo que queda en la emisión cuando la traba de producción frena el envío.</summary>
    public const string ProductionDisabled = "La emisión contra el fisco real está apagada: se habilita con Fiscal:ProductionEnabled.";

    /// <summary>Motivo que queda en la emisión de una factura anulada sin cobro antes de tener comprobante.</summary>
    public const string CancelledUnpaid = "La factura se anuló sin cobro antes de tener comprobante fiscal: ni ella ni sus notas llevan uno.";

    /// <summary>Motivo que queda en la emisión de una nota mientras su original, emitible, sigue sin comprobante.</summary>
    public const string NoteWaiting = "La nota espera el comprobante fiscal del original: sale cuando lo tenga.";

    /// <summary>
    /// Intentos de una emisión antes de frenarla cuando el resultado nunca queda claro. Así una emisión trabada no
    /// frena para siempre la cola fiscal: queda pendiente y su trabajo, muerto y visible. Cada espera del ciclo
    /// incierto termina en una propuesta nueva, así que este tope también acota las reprogramaciones.
    /// </summary>
    public const int MaxAttempts = 5;

    public string Kind => JobKinds.FiscalEmit;

    /// <summary>
    /// Una instancia fiscal apunta al fisco real cuando su configuración dice environment = production (sección 9.4
    /// del protocolo). Lo que no lo diga se toma como homologación. La clave y el valor se comparan sin distinguir
    /// mayúsculas, como los lee el módulo, y basta con que una clave lo diga: con la clave repetida en distinta caja
    /// el módulo se queda con la última y la base guarda las claves en su propio orden, así que mirar solo la primera
    /// dejaría pasar a producción una instancia que el núcleo toma por homologación.
    /// </summary>
    public static bool IsProduction(ResolvedInstance module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return module.Data.Config.Any(p => string.Equals(p.Key, "environment", StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Value?.ToString(), "production", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Clave de entidad de la emisión de un documento: la de su serie (emisor, punto de venta y tipo). Sin emisor
    /// activo o sin tipo de comprobante para el documento, la clave genérica: la emisión igual se descarta o se
    /// rechaza con el motivo. Se calcula al publicar el evento, con el receptor congelado en el documento.
    /// </summary>
    public static async Task<string> EntityKey(BillingDbContext db, Document doc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(doc);
        var issuer = await db.IssuerProfiles.AsNoTracking().SingleOrDefaultAsync(i => i.Active, ct).ConfigureAwait(false);
        if (issuer is null || doc.Receiver is null)
        {
            return Subscriptions.FiscalEntity;
        }

        try
        {
            var receiver = JsonSerializer.Deserialize<ReceiverSnapshot>(doc.Receiver)!;
            var original = await FiscalPayload.Original(db, doc, issuer, ct).ConfigureAwait(false);
            var (pointOfSale, typeCode) = FiscalTypes.For(issuer, doc, receiver, original?.TypeCode);
            return Subscriptions.FiscalSeries(issuer.Id, pointOfSale, typeCode);
        }
        catch (InvalidOperationException)
        {
            return Subscriptions.FiscalEntity;
        }
    }

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Con una conciliación por línea de tiempo viva no se emite: puede estar adoptando o avanzando esta serie.
        if (await ReconcileHandler.Pending(db, ct).ConfigureAwait(false))
        {
            return JobResult.PollLater(ReconcileHandler.Wait);
        }

        var payload = JsonDocument.Parse(job.Payload).RootElement;
        var documentId = payload.GetProperty("document_id").GetInt64();
        try
        {
            return await Run(documentId, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (RetryLaterException or PermanentFailureException or OperationCanceledException) && !Failover.IsTransient(ex))
        {
            // Una falla imprevista muere visible en el primer intento y libera la cola fiscal, en vez de dar vueltas
            // dos horas con esperas crecientes. El motivo se escribe por fuera del contexto, que puede quedar a medias,
            // en una transacción propia para que pase por la cerca del trabajo: si el lease ya es de otro nodo, no se
            // escribe y el trabajo vuelve a la cola sin cerrarse. Una conmutación de la base no es una falla del
            // trabajo: la deja pasar y el corredor lo reprograma.
            await using (var tx = await db.Database.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false))
            {
                await db.FiscalEmissions.Where(e => e.DocumentId == documentId)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.LastError, ex.Message), CancellationToken.None).ConfigureAwait(false);
                await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw new PermanentFailureException($"Falla imprevista en la emisión fiscal: {ex.Message}");
        }
    }

    private async Task<JobResult> Run(long documentId, JsonElement payload, CancellationToken ct)
    {
        var trigger = payload.TryGetProperty("event", out var e) && e.GetString() == Events.PaymentAllocated ? "payment" : "issued";

        // El barrido diario reencola lo que sigue sin comprobante: al primer cobro, las facturas solo el último día del mes.
        var sweep = payload.TryGetProperty("sweep", out var s) && s.GetBoolean();

        // Un documento con comprobante ya está emitido, aunque un escritor atrasado haya pisado su emisión.
        if (await db.FiscalDocuments.AnyAsync(f => f.DocumentId == documentId, ct).ConfigureAwait(false))
        {
            return JobResult.Done;
        }

        var doc = await db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == documentId, ct).ConfigureAwait(false);
        var issuer = await db.IssuerProfiles.SingleOrDefaultAsync(i => i.Active, ct).ConfigureAwait(false);
        var account = await db.Accounts.SingleAsync(a => a.Id == doc.AccountId, ct).ConfigureAwait(false);

        // Lo emitido antes del pase a producción es del emisor de homologación: no sale con el de producción.
        if (issuer?.ModuleInstanceId is null || doc.Imported || account.NoFiscalDocument || !doc.IsIssued || doc.IssuedAt < issuer.ActiveSince
            || (doc.Kind == DocumentKind.Invoice && !sweep && (issuer.Moment == EmissionMoment.AtIssue) != (trigger == "issued")))
        {
            return JobResult.Done;
        }

        if (doc.Kind == DocumentKind.Invoice && doc.Status == InvoiceStatus.Cancelled)
        {
            // Anulada sin un peso cobrado antes de tener comprobante: ni ella ni sus notas llevan uno (D-05). Un CAE por
            // un servicio que no se cobró ni se prestó no tendría nota fiscal que lo anule.
            await db.FiscalEmissions.Where(e => e.DocumentId == documentId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.LastError, CancelledUnpaid), ct).ConfigureAwait(false);
            return JobResult.Done;
        }

        var receiver = JsonSerializer.Deserialize<ReceiverSnapshot>(doc.Receiver!)!;
        var original = await FiscalPayload.Original(db, doc, issuer, ct).ConfigureAwait(false);
        var (pointOfSale, typeCode) = FiscalTypes.For(issuer, doc, receiver, original?.TypeCode);
        var emission = await db.FiscalEmissions.SingleOrDefaultAsync(e => e.DocumentId == documentId, ct).ConfigureAwait(false);
        if (emission is null)
        {
            emission = new FiscalEmission(documentId, issuer.Id, pointOfSale, typeCode, time.GetUtcNow());
            db.FiscalEmissions.Add(emission);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Frenos antes de llamar al fisco (secciones 3.6 y 3.8 del diseño fiscal): el motivo queda en la emisión y
        // el staff corrige la ficha o la configuración. Nada sale hacia el módulo.
        var settings = await core.Settings(ct).ConfigureAwait(false);
        if (emission.Status == EmissionStatus.Pending && EmissionBrakes.Check(doc, receiver, account, settings, core.Today()) is { } brake)
        {
            return await Stop(emission, brake, EmissionStatus.Rejected, ct).ConfigureAwait(false);
        }

        var series = await db.FiscalSeries.SingleOrDefaultAsync(s => s.IssuerId == issuer.Id && s.PointOfSale == pointOfSale && s.TypeCode == typeCode, ct).ConfigureAwait(false);
        if (series is null)
        {
            series = new FiscalSeries(issuer.Id, pointOfSale, typeCode);
            db.FiscalSeries.Add(series);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var module = await registry.Resolve(issuer.ModuleInstanceId!.Value, ct).ConfigureAwait(false);
        var step = new Ctx(module, doc, receiver, issuer, emission, series, account, settings);

        if (emission.Status == EmissionStatus.Sending)
        {
            // El proceso cayó con la emisión en vuelo: el resultado es incierto.
            emission.MoveTo(EmissionStatus.Unknown, time.GetUtcNow());
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        if (emission.Status == EmissionStatus.Pending && doc.Kind != DocumentKind.Invoice && original is null)
        {
            // El fisco exige el comprobante asociado en una nota: sin comprobante del original no se manda nada (D-05).
            // Si el original es emitible y todavía no lo tiene (al primer cobro, o con su emisión frenada) la nota
            // espera: la autorización del original la encola (y si no, el barrido diario) y sale cuando tenga el suyo,
            // como la nota de débito del recargo por mora, que es plata que se cobra. Si nunca lo va a tener (importado sin comprobante, anulado
            // sin cobro o emitido antes del pase a producción, que es del emisor de homologación) queda rechazada.
            var invoice = doc.RelatedDocumentId is { } relatedId ? await db.Documents.SingleOrDefaultAsync(d => d.Id == relatedId, ct).ConfigureAwait(false) : null;
            if (invoice is { Imported: false, Status: not InvoiceStatus.Cancelled } && !(invoice.IssuedAt < issuer.ActiveSince))
            {
                emission.LastError = NoteWaiting;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return JobResult.Done;
            }

            return await Stop(step, "La nota no se emite: el original no tiene comprobante fiscal.", EmissionStatus.Rejected, ct).ConfigureAwait(false);
        }

        return emission.Status switch
        {
            EmissionStatus.Unknown => await Resolve(step, ct).ConfigureAwait(false),
            EmissionStatus.Pending => await Send(step, ct).ConfigureAwait(false),
            _ => JobResult.Done,
        };
    }

    private sealed record Ctx(ResolvedInstance Module, Document Doc, ReceiverSnapshot Receiver, IssuerProfile Issuer, FiscalEmission Emission, FiscalSeries Series, Account Account, TaxSettings Settings)
    {
        /// <summary>Cambia cuando el módulo devuelve estado nuevo: la llamada siguiente de la corrida lo manda.</summary>
        public ResolvedInstance Module { get; set; } = Module;
    }

    private async Task<JobResult> Send(Ctx c, CancellationToken ct)
    {
        var (doc, receiver, issuer, emission, series) = (c.Doc, c.Receiver, c.Issuer, c.Emission, c.Series);
        if (IsProduction(c.Module) && !fiscal.Value.ProductionEnabled)
        {
            // Traba de producción: sin Fiscal:ProductionEnabled no sale ni la consulta del número hacia el fisco real.
            // La emisión queda pendiente con el motivo y el trabajo muere visible, sin llamar al módulo.
            emission.LastError = ProductionDisabled;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            throw new PermanentFailureException(ProductionDisabled);
        }

        var now = time.GetUtcNow();
        var export = FiscalTypes.IsExportType(issuer, series.TypeCode);
        long lastNumber;
        DateOnly? lastDate;
        long? lastRequest = null;
        if (!export && EmissionRules.IsSynced(series.SyncedAt, now))
        {
            // La serie se comprobó contra el fisco hace menos de un día: se propone el siguiente sin consultar. Si otro
            // sistema usó ese número, el fisco lo rechaza por fuera de secuencia, eso vuelve como incierto y la consulta
            // ve otro comprobante con ese número: conflicto, y la serie deja de darse por sincronizada. La exportación
            // consulta siempre, porque el ID de pedido es por CUIT y lo puede haber usado cualquier otro sistema del emisor.
            lastNumber = series.LastNumber;
            lastDate = series.LastDate;
        }
        else
        {
            var last = await Call(c, "last_number", new JsonObject { ["point_of_sale"] = series.PointOfSale, ["type_code"] = series.TypeCode }, null, ct).ConfigureAwait(false);
            lastNumber = last["number"]!.GetValue<long>();
            var check = EmissionRules.CheckSeries(lastNumber, series.LastNumber);
            if (check != SeriesCheck.InSync)
            {
                // El fisco va adelante (o atrás): se frena la serie hasta conciliar (sección 9.4 del protocolo, paso 2).
                return await Stop(c, $"Serie fuera de sincronía: el fisco tiene {lastNumber} y el núcleo {series.LastNumber}.", EmissionStatus.Conflict, ct).ConfigureAwait(false);
            }

            lastDate = last["date"] is JsonNode d ? DateOnly.Parse(d.GetValue<string>(), CultureInfo.InvariantCulture) : null;
            lastRequest = last["last_request_id"]?.GetValue<long>();
            series.SyncedAt = now;
        }

        var date = EmissionRules.DocumentDate(core.Today(), lastDate);
        var number = lastNumber + 1;
        var payload = await FiscalPayload.Build(db, doc, receiver, issuer, series, number, date, ct).ConfigureAwait(false);
        var document = payload["document"]!;

        // La cotización se resuelve antes de pasar a Enviando, así una falla acá deja la emisión pendiente. Con
        // cancelación en la misma moneda (RG 5616, validación 10038 de WSFEv1) el comprobante va sin cotización y el
        // fisco registra la del día hábil anterior. Una nota de exportación lleva la de su factura (ARCA lo exige, error
        // 2053 de WSFEX): si no la tiene guardada, la que el fisco registró para ella. Las demás piden la de su fecha.
        // Qué fecha pedir con cancelación en pesos (la del comprobante o la del día hábil anterior) se confirma en
        // homologación.
        var currency = document["currency"]!.GetValue<string>();
        var sameCurrency = document["same_currency_payment"]?.GetValue<bool>() == true;
        var omitRate = sameCurrency && (!export || doc.Kind == DocumentKind.Invoice);
        var quoted = false;
        if (currency != "PES" && document["exchange_rate"] is null && !omitRate)
        {
            if (export && document["associated"]?.AsArray().FirstOrDefault() is { } original)
            {
                document["exchange_rate"] = await RegisteredRate(c, original, ct).ConfigureAwait(false)
                    ?? throw new RetryLaterException("El fisco no informó la cotización del comprobante asociado.", UncertainWait);
            }
            else
            {
                document["exchange_rate"] = (await Quote(c, currency, date, ct).ConfigureAwait(false))["rate"]!.DeepClone();
                quoted = true;
            }
        }

        // Una cotización que no es la que el fisco publica para esta fecha (la copiada de la factura en una nota de
        // exportación, que puede venir de un comprobante importado) se comprueba contra la orientativa antes de
        // llamar: fuera del 2 % al 400 % el fisco la rechaza (validación 10119), y así el motivo queda claro y a mano.
        if (currency != "PES" && !quoted && document["exchange_rate"] is JsonNode sent)
        {
            var reference = (await Quote(c, currency, date, ct).ConfigureAwait(false))["rate"];
            if (!(FiscalPayload.Num(sent) is { } r && FiscalPayload.Num(reference) is { } q && EmissionRules.RateInRange(r, q)))
            {
                // Como un error definitivo del módulo: la emisión queda rechazada y el trabajo, muerto y visible para el staff.
                emission.Codes = new JsonObject { ["upstream"] = new JsonArray(new JsonObject { ["code"] = 10119, ["message"] = "Cotización fuera del rango de la orientativa" }) }.ToJsonString();
                var reason = $"La cotización {sent} está fuera del rango que admite el fisco, del 2 % al 400 % de la orientativa {reference} (validación 10119): corregí la del comprobante asociado y reintentá.";
                await Stop(c, reason, EmissionStatus.Rejected, ct).ConfigureAwait(false);
                throw new PermanentFailureException(reason);
            }
        }

        // El freno de la FCE MiPyME en otra moneda recién se decide con la cotización: el mínimo es en pesos.
        if (currency != "PES" && emission.Status == EmissionStatus.Pending)
        {
            decimal? exchangeRate = document["exchange_rate"] is { } node && decimal.TryParse(node.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            if (EmissionBrakes.Fce(doc, c.Account, c.Settings, core.Today(), exchangeRate) is { } fce)
            {
                return await Stop(c, fce, EmissionStatus.Rejected, ct).ConfigureAwait(false);
            }
        }

        emission.Propose(number, date, now);
        if (export)
        {
            // El ID de pedido sigue al mayor entre el último que usó el fisco y el último propio, y queda fijo para los reintentos.
            var fiscoLast = lastRequest
                ?? throw new PermanentFailureException("El módulo fiscal no informó el último ID de pedido de exportación.");
            var ownLast = await db.FiscalEmissions.Where(e => e.IssuerId == issuer.Id).MaxAsync(e => e.RequestId, ct).ConfigureAwait(false) ?? 0;
            payload["wsfex_id"] = emission.ReserveRequestId(Math.Max(fiscoLast, ownLast) + 1);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Cada intento es una intención nueva (otra fecha u otra cotización), así no choca con la huella de uno anterior.
        // Nunca se repite a ciegas: un intento nuevo sale solo después de un error definitivo o de que find confirme que
        // el número no existe. La clave lleva la instancia: la SDK solo compara el input, y dos instancias del mismo
        // proceso del módulo (homologación y producción en el mes en paralelo, con la base recreada en el corte) no
        // se pueden devolver una a la otra la respuesta guardada.
        var key = string.Create(CultureInfo.InvariantCulture, $"emision-{c.Module.Data.Id}-{emission.Id}-{number}-{emission.Attempts}");
        var result = await Invoke(c, "authorize", payload, key, ct).ConfigureAwait(false);
        if (result.Response is { Status: ModuleResponse.CompletedStatus } response)
        {
            if (document["exchange_rate"] is null)
            {
                // Sin cotización el fisco registra la oficial: se guarda para el QR y para las notas.
                document["exchange_rate"] = await RegisteredRate(c, document, ct).ConfigureAwait(false);
            }

            // El fisco acaba de autorizar el número que sigue al último: la serie queda comprobada.
            series.SyncedAt = time.GetUtcNow();
            await Authorize(db, jobs, series.SyncedAt.Value, emission, series, doc.Id, number, date, response.Result!, payload, ct).ConfigureAwait(false);
            return JobResult.Done;
        }

        // Un error definitivo dice que no se autorizó nada: lo rechazó el fisco o el módulo lo cortó antes de mandarlo
        // (datos, configuración o firma inválidos). idempotency_conflict no, porque el pedido anterior con esa clave
        // pudo haber llegado.
        if (result.Error is { } error && !ErrorCodes.IsRetryable(error.Code) && error.Code != ErrorCodes.IdempotencyConflict)
        {
            emission.Codes = error.Details?.ToJsonString();
            if (error.Details?["upstream"] is JsonArray upstream && upstream.Any(u => u is JsonObject code && code["message"]?.ToString() == "wsfex_id_in_use"))
            {
                // El fisco ya usó ese ID de pedido en otro comprobante: el reintento tiene que reservar otro.
                emission.ReleaseRequestId();
            }

            var stopped = await Stop(c, error.Message, EmissionStatus.Rejected, ct).ConfigureAwait(false);

            // El rechazo del fisco sigue el circuito de la nota de crédito. Un error del módulo pide que el staff corrija
            // los datos o la configuración y reintente: el trabajo queda muerto y visible.
            return error.Code == ErrorCodes.Rejected ? stopped : throw new PermanentFailureException($"{error.Code}: {error.Message}");
        }

        // Cualquier otra cosa puede haber llegado al fisco: es incierto y se consulta antes de reintentar.
        emission.LastError = result.Error?.Message;
        emission.MoveTo(EmissionStatus.Unknown, time.GetUtcNow());
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return JobResult.PollLater(UncertainWait);
    }

    private async Task<JobResult> Resolve(Ctx c, CancellationToken ct)
    {
        var (doc, receiver, issuer, emission, series) = (c.Doc, c.Receiver, c.Issuer, c.Emission, c.Series);
        var number = emission.ProposedNumber!.Value;
        var call = await Invoke(c, "find", new JsonObject { ["point_of_sale"] = series.PointOfSale, ["type_code"] = series.TypeCode, ["number"] = number }, null, ct).ConfigureAwait(false);

        if (call.Error is { Code: ErrorCodes.NotFound })
        {
            if (time.GetUtcNow() - emission.UpdatedAt < UncertainWait)
            {
                return JobResult.PollLater(UncertainWait);
            }

            emission.MoveTo(EmissionStatus.Pending, time.GetUtcNow());
            if (emission.Attempts >= MaxAttempts)
            {
                emission.LastError = $"Frenada después de {emission.Attempts} intentos sin resultado cierto. Último error: {emission.LastError}";
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                throw new PermanentFailureException(emission.LastError);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return await Send(c, ct).ConfigureAwait(false);
        }

        if (call.Error is { } error)
        {
            // Como en el envío: un error definitivo de la consulta (certificado vencido, traba del módulo) muere
            // visible en el primer intento y libera la serie, en vez de dar vueltas dos horas con la clave tomada. La
            // emisión queda incierta con el motivo, y el barrido diario la vuelve a encolar cuando el staff corrige.
            var message = $"{error.Code}: {error.Message}";
            emission.LastError = message;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            throw ErrorCodes.IsRetryable(error.Code)
                ? new RetryLaterException(message, error.RetryAfter is { } s ? TimeSpan.FromSeconds(s) : null)
                : new PermanentFailureException(message);
        }

        var found = call.Response!.Result!;
        var payload = await FiscalPayload.Build(db, doc, receiver, issuer, series, number, emission.Date!.Value, ct).ConfigureAwait(false);
        if (emission.RequestId is { } requestId)
        {
            payload["wsfex_id"] = requestId;
        }

        // La cotización que se usó es la que guarda el fisco.
        if (found["document"]?["exchange_rate"] is JsonNode rate)
        {
            payload["document"]!["exchange_rate"] = rate.DeepClone();
        }

        if (!FiscalPayload.Matches(found, payload))
        {
            return await Stop(c, $"El comprobante {number} existe en el fisco con otros datos.", EmissionStatus.Conflict, ct).ConfigureAwait(false);
        }

        if (await Contested(emission, number, ct).ConfigureAwait(false))
        {
            return await Stop(c, $"El comprobante {number} coincide, pero otra emisión de la serie también propuso ese número.", EmissionStatus.Conflict, ct).ConfigureAwait(false);
        }

        // Es el nuestro, con el número que seguía al último: la serie queda comprobada.
        series.SyncedAt = time.GetUtcNow();
        await Authorize(db, jobs, series.SyncedAt.Value, emission, series, doc.Id, number, emission.Date!.Value, found, payload, ct).ConfigureAwait(false);
        return JobResult.Done;
    }

    /// <summary>Cotización oficial de la moneda para la fecha, con la fuente del tipo de comprobante. Sin cotización publicada espera unas horas.</summary>
    private Task<JsonObject> Quote(Ctx c, string currency, DateOnly date, CancellationToken ct) =>
        Call(c, "exchange_rate", new JsonObject { ["currency"] = currency, ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["type_code"] = c.Series.TypeCode }, null, ct, NoRateWait);

    /// <summary>
    /// Otra emisión de la serie propuso el mismo número, o ya hay un comprobante con él: el encontrado puede ser de
    /// cualquiera de las dos y nadie lo adopta. Una rechazada no cuenta, porque nunca se autorizó.
    /// </summary>
    private async Task<bool> Contested(FiscalEmission emission, long number, CancellationToken ct) =>
        await db.FiscalEmissions.AnyAsync(
            e => e.Id != emission.Id && e.IssuerId == emission.IssuerId && e.PointOfSale == emission.PointOfSale && e.TypeCode == emission.TypeCode
                && e.ProposedNumber == number && e.Status != EmissionStatus.Rejected,
            ct).ConfigureAwait(false)
        || await db.FiscalDocuments.AnyAsync(
            f => f.IssuerId == emission.IssuerId && f.PointOfSale == emission.PointOfSale && f.TypeCode == emission.TypeCode && f.Number == number,
            ct).ConfigureAwait(false);

    /// <summary>Cotización que el fisco registró para un comprobante. Null si no la informa o la consulta falla.</summary>
    private async Task<JsonNode?> RegisteredRate(Ctx c, JsonNode voucher, CancellationToken ct)
    {
        var input = new JsonObject
        {
            ["point_of_sale"] = voucher["point_of_sale"]!.DeepClone(),
            ["type_code"] = voucher["type_code"]!.DeepClone(),
            ["number"] = voucher["number"]!.DeepClone(),
        };
        var found = await Invoke(c, "find", input, null, ct).ConfigureAwait(false);
        return found.Response?.Result?["document"]?["exchange_rate"]?.DeepClone();
    }

    /// <summary>Llama al módulo y guarda el estado que devuelva, también en un error, antes de seguir (sección 6 del protocolo).</summary>
    private async Task<ModuleCallResult> Invoke(Ctx c, string operation, JsonObject input, string? key, CancellationToken ct)
    {
        var result = await client.Call(c.Module.Endpoint, $"/v0/fiscal/{operation}", new ModuleRequest(c.Module.Data, [], input), key, c.Module.Timeout(operation), ct).ConfigureAwait(false);
        if (result.InstanceState is { } state)
        {
            c.Module = await registry.SaveState(c.Module, state, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Llama y devuelve el resultado, o falla con reintento o en forma definitiva según el error. Con
    /// <paramref name="retryNotFoundAfter"/>, un not_found espera ese tiempo en vez de morir: sirve para la cotización
    /// que el fisco todavía no publicó.
    /// </summary>
    private async Task<JsonObject> Call(Ctx c, string operation, JsonObject input, string? key, CancellationToken ct, TimeSpan? retryNotFoundAfter = null)
    {
        var result = await Invoke(c, operation, input, key, ct).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            var message = $"{error.Code}: {error.Message}";
            if (error.Code == ErrorCodes.NotFound && retryNotFoundAfter is { } wait)
            {
                throw new RetryLaterException(message, wait);
            }

            throw ErrorCodes.IsRetryable(error.Code)
                ? new RetryLaterException(message, error.RetryAfter is { } s ? TimeSpan.FromSeconds(s) : null)
                : new PermanentFailureException(message);
        }

        return result.Response?.Result as JsonObject ?? [];
    }

    /// <summary>
    /// Registra el comprobante que el fisco autorizó (o que el staff adopta): la emisión queda autorizada, la serie
    /// avanza si el número es mayor que su último y el evento sale en la misma transacción. Lo comparte la emisión
    /// con la adopción a mano (<see cref="FiscalStaffService"/>).
    /// </summary>
    internal static async Task Authorize(BillingDbContext db, JobQueue jobs, DateTimeOffset now, FiscalEmission emission, FiscalSeries series, long documentId, long number, DateOnly date, JsonNode result, JsonObject payload, CancellationToken ct)
    {
        emission.MoveTo(EmissionStatus.Authorized, now);
        if (number > series.LastNumber)
        {
            series.LastNumber = number;
            series.LastDate = date;
        }

        var voucher = new FiscalDocument
        {
            DocumentId = documentId,
            EmissionId = emission.Id,
            IssuerId = emission.IssuerId,
            PointOfSale = series.PointOfSale,
            TypeCode = series.TypeCode,
            Number = number,
            Date = date,
            AuthorizationCode = result["authorization_code"]?.GetValue<string>() ?? string.Empty,
            AuthorizationDue = result["authorization_due"] is JsonNode due ? DateOnly.Parse(due.GetValue<string>(), CultureInfo.InvariantCulture) : null,
            Payload = payload.ToJsonString(),
            CreatedAt = now,
        };
        db.FiscalDocuments.Add(voucher);

        // Dentro de la transacción del que llama si ya hay una (la adopción), o en una propia.
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (payload["document"]?["currency"]?.ToString() != "PES" && payload["document"]?["exchange_rate"] is null)
        {
            // Autorizado sin la cotización que el fisco registró (la consulta de después falló): el CAE se guarda igual
            // y la cotización, que el QR exige, la completa un trabajo propio en la misma transacción.
            await jobs.Enqueue(JobKinds.FiscalRate, new { fiscal_document_id = voucher.Id }, Subscriptions.FiscalSeries(emission.IssuerId, series.PointOfSale, series.TypeCode), FiscalRateHandler.DedupKey(voucher.Id), ct: ct).ConfigureAwait(false);
        }

        await jobs.Publish(Events.FiscalDocumentAuthorized, new { document_id = documentId }, ct: ct).ConfigureAwait(false);

        // Las notas que esperaban este comprobante salen ya, cada una en la cola de su serie, sin esperar al barrido: la
        // factura y su nota van por colas distintas, y la nota de débito del recargo cobrada antes que su factura tiene
        // que tener su comprobante cuando la factura tiene el suyo.
        // ponytail: una nota que se pone a esperar a la vez que esta autorización (otro nodo, sin confirmar todavía) no
        // se ve acá y sale con el barrido diario. Si ese día de demora importa, bloquear la fila del original en los dos lados.
        var waiting = await db.Documents.AsNoTracking()
            .Where(d => d.RelatedDocumentId == documentId && db.FiscalEmissions.Any(e => e.DocumentId == d.Id && e.Status == EmissionStatus.Pending && e.LastError == NoteWaiting))
            .OrderBy(d => d.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var note in waiting)
        {
            await jobs.Enqueue(JobKinds.FiscalEmit, new { document_id = note.Id }, await EntityKey(db, note, ct).ConfigureAwait(false), ct: ct).ConfigureAwait(false);
        }

        if (tx is not null)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    private Task<JobResult> Stop(Ctx c, string reason, EmissionStatus status, CancellationToken ct)
    {
        if (status == EmissionStatus.Conflict)
        {
            // La serie deja de darse por sincronizada: la próxima emisión vuelve a consultar el último número.
            c.Series.SyncedAt = null;
        }

        return Stop(c.Emission, reason, status, ct);
    }

    /// <summary>Frena la emisión con el motivo, también antes de tener la serie (los frenos de la ficha y la configuración).</summary>
    private async Task<JobResult> Stop(FiscalEmission emission, string reason, EmissionStatus status, CancellationToken ct)
    {
        emission.LastError = reason;
        if (emission.Status == EmissionStatus.Pending && status == EmissionStatus.Conflict)
        {
            // Pendiente no pasa a conflicto por el diagrama: queda pendiente con el motivo, y la serie no avanza.
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            throw new PermanentFailureException(reason);
        }

        emission.MoveTo(status, time.GetUtcNow());
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

/// <summary>
/// Lo que el núcleo rechaza sin llamar al fisco. Devuelve el motivo, o null si la emisión puede seguir.
/// </summary>
public static class EmissionBrakes
{
    public static string? Check(Document doc, ReceiverSnapshot receiver, Account account, TaxSettings settings, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(settings);
        if (!VatConditions.IsKnown(receiver.VatCondition))
        {
            return $"La condición frente al IVA del receptor ({receiver.VatCondition}) no está en la tabla de ARCA: corregí la ficha y volvé a emitir.";
        }

        // Una B a un no categorizado tiene que llevar la percepción de la RG 2126 (validación 10067 de WSFEv1).
        if (receiver.VatCondition == VatConditions.NoCategorizado && doc.Kind == DocumentKind.Invoice && doc.Tax > 0m
            && !doc.IssuedTaxes().Any(t => t.Code == Tax.VatPerceptionRg2126 && t.Amount > 0m))
        {
            return "El receptor es un sujeto no categorizado y la factura no lleva la percepción de IVA de la RG 2126: configurala en Impuestos, Percepciones, o pedile al cliente que acredite su condición. Esta factura ya emitida se anula con nota de crédito y se vuelve a emitir con la percepción o la ficha corregidas.";
        }

        // El mínimo de la FCE es en pesos: un documento en otra moneda se compara recién con la cotización que informa el fisco.
        return doc.Currency == Currency.Ars ? Fce(doc, account, settings, today, 1m) : null;
    }

    /// <summary>
    /// Freno de la factura de crédito electrónica MiPyME (Ley 27.440 y RG 4367): a una cuenta marcada como empresa
    /// grande, por el mínimo o más en pesos. Un documento en otra moneda se convierte con la cotización del fisco, y
    /// sin cotización (cancelación en la misma moneda) no se puede comparar y también se frena, hasta que exista la FCE.
    /// </summary>
    public static string? Fce(Document doc, Account account, TaxSettings settings, DateOnly today, decimal? exchangeRate)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(settings);
        if (!account.LargeCompany || doc.Kind != DocumentKind.Invoice || today < settings.FceMinimumFrom)
        {
            return null;
        }

        if (exchangeRate is not { } rate)
        {
            return string.Create(CultureInfo.InvariantCulture, $"La cuenta figura como empresa grande en ARCA y la factura está en {doc.Currency.Code} sin cotización: no se puede comparar con el mínimo de la factura de crédito electrónica MiPyME ({settings.FceMinimum:0.00} pesos), que este sistema todavía no emite. Hasta entonces, a esta cuenta se le factura en pesos o con cotización.");
        }

        var pesos = Rounding.Commercial(doc.Total * rate);
        return pesos < settings.FceMinimum ? null
            : string.Create(CultureInfo.InvariantCulture, $"La cuenta figura como empresa grande en ARCA y el importe ({doc.Total:0.00} {doc.Currency.Code}, {pesos:0.00} pesos) llega al mínimo de la factura de crédito electrónica MiPyME ({settings.FceMinimum:0.00}): el comprobante tiene que ser FCE, que este sistema todavía no emite.");
    }
}

/// <summary>Elige punto de venta y tipo de comprobante según el emisor, el documento y el receptor.</summary>
public static class FiscalTypes
{
    /// <summary>Condición frente al IVA "Cliente del Exterior" en la tabla de ARCA.</summary>
    public const int ForeignClient = 9;

    /// <summary>
    /// La clase que corresponde (sección 3.6 del diseño fiscal): E a la exportación de servicios, C si el emisor es
    /// monotributista o exento, A al receptor con condición de clase A y B al resto.
    /// </summary>
    public static string LetterFor(int issuerVatCondition, string classAConditions, string receiverCountry, int receiverVatCondition, bool hasTax)
    {
        ArgumentNullException.ThrowIfNull(classAConditions);
        if ((receiverCountry != "AR" || receiverVatCondition == ForeignClient) && !hasTax)
        {
            return "E";
        }

        if (VatConditions.IssuesClassC(issuerVatCondition))
        {
            return "C";
        }

        var classA = JsonSerializer.Deserialize<int[]>(classAConditions) ?? [];
        return classA.Contains(receiverVatCondition) ? "A" : "B";
    }

    /// <summary>
    /// Exportación: receptor de otro país o con condición de cliente del exterior, siempre que el documento no lleve
    /// IVA. Con una regla que lo grava (el servicio se usa en el país) va como comprobante del mercado interno con la
    /// condición de cliente del exterior, porque una factura E no informa IVA.
    /// </summary>
    public static bool IsExport(ReceiverSnapshot receiver, Document doc)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(doc);
        return (receiver.Country != "AR" || receiver.VatCondition == ForeignClient) && doc.Tax == 0m;
    }

    /// <summary>El tipo de comprobante es de la clase E del emisor.</summary>
    public static bool IsExportType(IssuerProfile issuer, int typeCode)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        return Letter(issuer, typeCode) == "E";
    }

    /// <summary>La clase (A, B o E) a la que pertenece un tipo de comprobante en el mapa del emisor, si está.</summary>
    public static string? Letter(IssuerProfile issuer, int typeCode)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        var map = JsonNode.Parse(issuer.TypeMap)?.AsObject() ?? [];
        return map.FirstOrDefault(l => l.Value is JsonObject kinds && kinds.Any(k => k.Value?.GetValue<int>() == typeCode)).Key;
    }

    /// <summary>Todas las series (punto de venta y tipo) que el emisor puede usar según su mapa, existan o no en la base.</summary>
    public static IEnumerable<(int PointOfSale, int TypeCode)> Series(IssuerProfile issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        var map = JsonNode.Parse(issuer.TypeMap)?.AsObject() ?? [];
        return map
            .SelectMany(l => (l.Value as JsonObject ?? []).Select(k => (l.Key == "E" ? issuer.ExportPointOfSale ?? issuer.PointOfSale : issuer.PointOfSale, k.Value!.GetValue<int>())))
            .Distinct();
    }

    /// <summary>
    /// Punto de venta y tipo del documento. Una nota sigue la clase del comprobante de su original cuando lo tiene,
    /// aunque la ficha del receptor haya cambiado desde la factura.
    /// </summary>
    public static (int PointOfSale, int TypeCode) For(IssuerProfile issuer, Document doc, ReceiverSnapshot receiver, int? originalTypeCode = null)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(receiver);
        var letter = (originalTypeCode is { } original ? Letter(issuer, original) : null)
            ?? LetterFor(issuer.VatCondition, issuer.ClassAConditions, receiver.Country, receiver.VatCondition, doc.Tax > 0m);
        var map = JsonNode.Parse(issuer.TypeMap)?[letter]?[doc.Kind.ToString()]
            ?? throw new InvalidOperationException($"El emisor no tiene tipo de comprobante para {letter} {doc.Kind}.");
        var pointOfSale = letter == "E" ? issuer.ExportPointOfSale ?? issuer.PointOfSale : issuer.PointOfSale;
        return (pointOfSale, map.GetValue<int>());
    }
}

/// <summary>Arma el documento de authorize con la forma de la sección 9.4 del protocolo.</summary>
public static class FiscalPayload
{
    private static readonly Dictionary<decimal, int> VatIds = new() { [0m] = 3, [0.105m] = 4, [0.21m] = 5, [0.27m] = 6, [0.05m] = 8, [0.025m] = 9 };

    public static async Task<JsonObject> Build(BillingDbContext db, Document doc, ReceiverSnapshot receiver, IssuerProfile issuer, FiscalSeries series, long number, DateOnly date, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(series);
        var lineIds = doc.Lines.Select(l => l.Id).ToList();
        // El período de servicio va del inicio más temprano al fin más tardío de los renglones: períodos facturados y
        // ventanas de uso medido.
        var periods = (await db.BilledPeriods.Where(p => lineIds.Contains(p.DocumentLineId)).Select(p => new { p.Start, p.End }).ToListAsync(ct).ConfigureAwait(false))
            .Concat(await db.Set<Domain.Usage.UsageCharge>().Where(c => c.DocumentLineId != null && lineIds.Contains(c.DocumentLineId.Value)).Select(c => new { c.Start, c.End }).ToListAsync(ct).ConfigureAwait(false))
            .ToList();
        var from = periods.Count > 0 ? periods.Min(p => p.Start) : date;
        var to = periods.Count > 0 ? periods.Max(p => p.End).AddDays(-1) : date;
        var aliquots = JsonSerializer.Deserialize<List<AliquotTotal>>(doc.Aliquots!)!;
        var settings = IssuerSettings.Parse(issuer.Settings);
        var export = FiscalTypes.IsExportType(issuer, series.TypeCode);

        // En clase C el neto es el subtotal, sin IVA ni exento ni no gravado (validaciones 10043 a 10048 de WSFEv1).
        var classC = FiscalTypes.Letter(issuer, series.TypeCode) == "C";

        // Los tributos van con el código de la tabla del fisco. Uno que suma al total y no tiene código no puede salir.
        var taxes = doc.IssuedTaxes().Where(t => t.AddsToTotal && t.Amount > 0m).ToList();
        if (taxes.Find(t => t.FiscalCode is null) is { } untyped)
        {
            throw new PermanentFailureException($"El tributo {untyped.Name} no tiene código de la tabla del fisco: cargalo en Impuestos.");
        }

        var currency = doc.Currency.Code switch { "ARS" => "PES", "USD" => "DOL", var c => c };

        // Una nota sigue a su original. El importado guarda los datos sin el nivel "document".
        var fiscal = await Original(db, doc, issuer, ct).ConfigureAwait(false);
        var sent = fiscal is null ? null : JsonNode.Parse(fiscal.Payload);
        var originalDoc = sent?["document"] ?? sent;
        var originalSame = originalDoc?["same_currency_payment"]?.GetValue<bool>();

        // En exportación solo la factura informa si se cancela en la misma moneda. En pesos no aplica.
        bool? sameCurrency = currency == "PES" ? false
            : export && doc.Kind != DocumentKind.Invoice ? null
            : originalSame ?? settings.SameCurrency(await PaymentMethod(db, doc, ct).ConfigureAwait(false));

        // La nota de exportación usa la cotización de su original (si no la tiene guardada, Send la consulta al fisco).
        // En el mercado interno cada comprobante lleva la de su fecha, o ninguna si se cancela en la misma moneda.
        JsonNode? exchangeRate = currency == "PES" ? "1" : export ? originalDoc?["exchange_rate"]?.DeepClone() : null;

        var associated = new JsonArray();
        if (fiscal is not null)
        {
            associated.Add(new JsonObject
            {
                ["type_code"] = fiscal.TypeCode,
                ["point_of_sale"] = fiscal.PointOfSale,
                ["number"] = fiscal.Number,
                ["date"] = Iso(fiscal.Date),
                ["tax_id"] = issuer.TaxId,
            });
        }

        var document = new JsonObject
        {
            ["point_of_sale"] = series.PointOfSale,
            ["type_code"] = series.TypeCode,
            ["number"] = number,
            ["date"] = Iso(date),
            ["concept"] = "services",
            ["service_period"] = new JsonObject { ["from"] = Iso(from), ["to"] = Iso(to) },
            ["payment_due"] = Iso(EmissionRules.PaymentDue(date, doc.DueDate)),
            ["receiver"] = new JsonObject
            {
                ["doc_type"] = DocType(receiver.DocumentType),
                ["doc_number"] = receiver.DocumentType == TaxDocumentType.None ? "0" : receiver.DocumentNumber,
                ["vat_condition"] = receiver.VatCondition,
                ["name"] = receiver.LegalName,
            },
            ["currency"] = currency,
            ["exchange_rate"] = exchangeRate,
            ["same_currency_payment"] = sameCurrency,
            ["amounts"] = new JsonObject
            {
                ["net_taxed"] = Money(classC ? doc.NetTaxed + doc.Exempt + doc.NotTaxed : doc.NetTaxed),
                ["net_untaxed"] = Money(classC ? 0m : doc.NotTaxed),
                ["exempt"] = Money(classC ? 0m : doc.Exempt),
                ["vat"] = new JsonArray([.. (classC ? [] : aliquots).Select(a => (JsonNode)new JsonObject
                {
                    ["rate_id"] = VatIds.TryGetValue(a.Rate, out var id) ? id : throw new InvalidOperationException($"Alícuota sin código: {a.Rate}."),
                    ["base"] = Money(a.Net),
                    ["amount"] = Money(a.Tax),
                })]),
                ["other_taxes"] = Money(doc.OtherTaxes),
                ["taxes"] = new JsonArray([.. taxes.Select(t => (JsonNode)new JsonObject
                {
                    ["id"] = t.FiscalCode!.Value,
                    ["description"] = t.Name,
                    ["base"] = Money(t.Base),
                    ["rate"] = (t.Rate * 100m).ToString("0.##", CultureInfo.InvariantCulture),
                    ["amount"] = Money(t.Amount),
                })]),
                ["total"] = Money(doc.Total),
            },
            ["associated"] = associated,
            ["items"] = new JsonArray([.. doc.Lines.Select(l => (JsonNode)new JsonObject
            {
                ["description"] = l.Description,
                ["quantity"] = l.Quantity.ToString("0.####", CultureInfo.InvariantCulture),
                ["unit_price"] = l.UnitPrice.ToString("0.######", CultureInfo.InvariantCulture),
                ["total"] = Money(l.Amount),
            })]),
            ["optional"] = new JsonArray(),
        };

        if (taxes.Count > 0 && doc.Kind != DocumentKind.Invoice)
        {
            // Una nota que revierte percepciones informa un período asociado del mismo mes (manual 4.7).
            document["period_assoc"] = new JsonObject { ["from"] = Iso(new DateOnly(date.Year, date.Month, 1)), ["to"] = Iso(new DateOnly(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month))) };
        }

        if (export)
        {
            // Datos del cliente del exterior: el país va con el código y la CUIT genérica que publica ARCA.
            var country = settings.ExportCountries?.GetValueOrDefault(receiver.Country) ?? settings.ExportDefaultCountry
                ?? throw new PermanentFailureException($"El emisor no tiene código de país de exportación para {receiver.Country}.");
            var r = document["receiver"]!.AsObject();
            r["name"] = Cut(receiver.LegalName, 200);
            r["address"] = Cut(receiver.Address ?? "Exterior", 300);
            r["country_code"] = country.Code;
            r["country_tax_id"] = country.TaxId;
            if (receiver.DocumentNumber.Length > 0)
            {
                r["tax_id"] = receiver.DocumentNumber;
            }

            document["payment_terms"] = settings.ExportPaymentTerms;
            document["language"] = settings.ExportLanguage ?? "es";
            foreach (var item in document["items"]!.AsArray())
            {
                item!["unit_code"] = settings.ExportUnitCode;
            }
        }

        return new JsonObject { ["document"] = document };
    }

    /// <summary>
    /// El comprobante del original de una nota. Una factura importada puede tener varios (D-17): se asocia la última
    /// factura, por fecha y por ID, entre los tipos de factura del emisor, y si ninguno es de esos tipos, el último.
    /// Solo cuentan los de este emisor y los importados del sistema anterior: uno de homologación no se asocia en
    /// producción.
    /// </summary>
    public static async Task<FiscalDocument?> Original(BillingDbContext db, Document doc, IssuerProfile issuer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(issuer);
        if (doc.RelatedDocumentId is not { } original)
        {
            return null;
        }

        var invoiceTypes = (JsonNode.Parse(issuer.TypeMap)?.AsObject() ?? []).Select(l => l.Value?["Invoice"]?.GetValue<int>()).OfType<int>().ToList();
        var candidates = await db.FiscalDocuments.Where(f => f.DocumentId == original && (f.IssuerId == issuer.Id || f.Imported))
            .OrderByDescending(f => f.Date).ThenByDescending(f => f.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        return candidates.Find(f => invoiceTypes.Contains(f.TypeCode)) ?? candidates.FirstOrDefault();
    }

    /// <summary>Medio de pago de los servicios facturados. En una nota, también los de la factura que corrige.</summary>
    private static Task<string?> PaymentMethod(BillingDbContext db, Document doc, CancellationToken ct)
    {
        var related = doc.RelatedDocumentId;
        var services = db.DocumentLines.Where(l => (l.DocumentId == doc.Id || l.DocumentId == related) && l.ServiceId != null).Select(l => l.ServiceId!.Value);
        return db.Services.Where(s => services.Contains(s.Id) && s.PaymentMethod != null).OrderBy(s => s.Id).Select(s => s.PaymentMethod).FirstOrDefaultAsync(ct);
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>
    /// Un comprobante encontrado coincide si tiene la misma fecha y el mismo total, y además el mismo receptor,
    /// concepto, período de servicio, vencimiento de pago, moneda, desglose de IVA e ID de pedido de exportación
    /// cuando el fisco los informa. Así dos facturas del mismo cliente con la misma fecha y el mismo total no se
    /// confunden. Los números se comparan como números: el fisco devuelve el documento sin ceros adelante.
    /// </summary>
    public static bool Matches(JsonNode found, JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(payload);
        var doc = payload["document"]!;
        var f = found["document"] ?? found;
        return f["date"] is not null && f["amounts"]?["total"] is not null
            && Same(f["date"], doc["date"])
            && Same(f["amounts"]?["total"], doc["amounts"]?["total"])
            && Same(f["receiver"]?["doc_number"], doc["receiver"]?["doc_number"])
            && Same(f["concept"], doc["concept"])
            && Same(f["service_period"]?["from"], doc["service_period"]?["from"])
            && Same(f["service_period"]?["to"], doc["service_period"]?["to"])
            && Same(f["payment_due"], doc["payment_due"])
            && Same(f["currency"], doc["currency"])
            && Same(found["wsfex_id"], payload["wsfex_id"])
            && (f["amounts"]?["vat"] is not JsonArray vat || Vat(vat).SequenceEqual(Vat(doc["amounts"]?["vat"] as JsonArray)));
    }

    /// <summary>Lo que el fisco no informa no cuenta. Si los dos son números se comparan como tales, si no como texto.</summary>
    private static bool Same(JsonNode? found, JsonNode? sent) =>
        found is null || (Num(found) is { } a && Num(sent) is { } b ? a == b : found.ToString() == sent?.ToString());

    private static IEnumerable<(decimal? Rate, decimal? Base, decimal? Amount)> Vat(JsonArray? vat) =>
        (vat ?? []).Select(v => (Num(v?["rate_id"]), Num(v?["base"]), Num(v?["amount"]))).Order();

    /// <summary>El valor numérico de un nodo, venga como número o como texto. Null si no es un número.</summary>
    public static decimal? Num(JsonNode? node) =>
        decimal.TryParse(node?.ToString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static int DocType(TaxDocumentType type) => type switch
    {
        TaxDocumentType.Cuit => 80,
        TaxDocumentType.Cuil => 86,
        TaxDocumentType.Dni => 96,
        TaxDocumentType.Passport => 94,
        _ => 99,
    };

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Money(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>
/// Ajustes del emisor (IssuerProfile.Settings). Los países van por código ISO de dos letras. SameCurrencyByMethod
/// dice, por medio de pago, si el comprobante en moneda extranjera se cancela en esa misma moneda.
/// </summary>
public sealed record IssuerSettings(
    Dictionary<string, ExportCountry>? ExportCountries = null,
    ExportCountry? ExportDefaultCountry = null,
    string? ExportPaymentTerms = null,
    string? ExportLanguage = null,
    int? ExportUnitCode = null,
    Dictionary<string, bool>? SameCurrencyByMethod = null,
    bool SameCurrencyDefault = false)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static IssuerSettings Parse(string json) => JsonSerializer.Deserialize<IssuerSettings>(json, Json) ?? new();

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public bool SameCurrency(string? method) =>
        method is not null && SameCurrencyByMethod?.TryGetValue(method, out var same) == true ? same : SameCurrencyDefault;
}

/// <summary>País de destino con el código y la CUIT genérica que publica ARCA.</summary>
public sealed record ExportCountry(int Code, long TaxId);

/// <summary>
/// Barrido fiscal diario (secciones 7.1 y 7.2 del modelo de dominio). Vuelve a encolar la emisión de todo documento
/// emitido que sigue sin comprobante y sin trabajo vivo (al primer cobro, las facturas solo el último día del mes, las
/// notas siempre), cada una con la clave de su serie, y encola el control diario de cada serie
/// (<see cref="FiscalCheckHandler"/>).
/// </summary>
public sealed class FiscalSweepHandler(BillingDbContext db, JobQueue jobs, BillingCore core) : IJobHandler
{
    public string Kind => JobKinds.FiscalSweep;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var issuer = await db.IssuerProfiles.SingleOrDefaultAsync(i => i.Active, ct).ConfigureAwait(false);
        if (issuer?.ModuleInstanceId is null)
        {
            return JobResult.Done;
        }

        // El barrido es el de su día, aunque el corredor lo tome al día siguiente (cola ocupada, nodo caído). Y lo
        // impago de un mes ya cerrado sale en el primer barrido que corra, no en el último día del mes siguiente.
        var today = JsonDocument.Parse(job.Payload).RootElement.TryGetProperty("date", out var d) && DateOnly.TryParse(d.GetString(), CultureInfo.InvariantCulture, out var date)
            ? date
            : core.Today();
        var monthEnd = today.AddDays(1).Month != today.Month;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var atIssue = issuer.Moment == EmissionMoment.AtIssue;
        var live = (await db.Jobs.Where(j => j.Kind == JobKinds.FiscalEmit && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
            .Select(j => j.Payload).ToListAsync(ct).ConfigureAwait(false))
            .Select(p => JsonDocument.Parse(p).RootElement.GetProperty("document_id").GetInt64())
            .ToHashSet();

        // Una factura anulada sin un peso cobrado no lleva comprobante (D-05): no se encola ni el último día del mes.
        var since = issuer.ActiveSince;
        var pending = await db.Documents
            .Where(d => d.IssueDate != null && !d.Imported && (since == null || d.IssuedAt >= since)
                && (d.Kind != DocumentKind.Invoice || atIssue || monthEnd || d.IssueDate < monthStart)
                && !(d.Kind == DocumentKind.Invoice && d.Allocated == 0m && d.Credited >= d.Total)
                && !db.FiscalDocuments.Any(f => f.DocumentId == d.Id)
                && !db.Accounts.Any(a => a.Id == d.AccountId && a.NoFiscalDocument))
            .OrderBy(d => d.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var doc in pending.Where(d => !live.Contains(d.Id)))
        {
            var key = await FiscalEmissionHandler.EntityKey(db, doc, ct).ConfigureAwait(false);
            await jobs.Enqueue(JobKinds.FiscalEmit, new { document_id = doc.Id, sweep = true }, key, ct: ct).ConfigureAwait(false);
        }

        // El control de cada serie es un trabajo con la clave de la serie: corre de a uno con sus emisiones y no las frena entre sí.
        foreach (var series in await db.FiscalSeries.Where(s => s.IssuerId == issuer.Id).OrderBy(s => s.Id).ToListAsync(ct).ConfigureAwait(false))
        {
            var key = Subscriptions.FiscalSeries(issuer.Id, series.PointOfSale, series.TypeCode);
            await jobs.Enqueue(JobKinds.FiscalCheck, new { series_id = series.Id }, key, $"{JobKinds.FiscalCheck}:{series.Id}:{today:yyyy-MM-dd}", ct: ct).ConfigureAwait(false);
        }

        // Un comprobante en moneda extranjera que sigue sin la cotización que el fisco registró: su trabajo murió y
        // se revive (misma clave de deduplicación), así ningún comprobante queda sin QR para siempre.
        var unrated = await db.Database.SqlQuery<long>($"""
            SELECT id AS "Value" FROM fiscal.documents
            WHERE issuer_id = {issuer.Id} AND NOT imported
              AND payload -> 'document' ->> 'currency' <> 'PES' AND payload -> 'document' ->> 'exchange_rate' IS NULL
            """).ToListAsync(ct).ConfigureAwait(false);
        foreach (var voucher in await db.FiscalDocuments.AsNoTracking().Where(f => unrated.Contains(f.Id)).OrderBy(f => f.Id).ToListAsync(ct).ConfigureAwait(false))
        {
            var key = Subscriptions.FiscalSeries(issuer.Id, voucher.PointOfSale, voucher.TypeCode);
            await jobs.Enqueue(JobKinds.FiscalRate, new { fiscal_document_id = voucher.Id }, key, FiscalRateHandler.DedupKey(voucher.Id), ct: ct).ConfigureAwait(false);
        }

        return JobResult.Done;
    }
}

/// <summary>
/// Control diario de una serie (sección 7.1 del modelo de dominio): compara el último número del fisco con el
/// registrado. En sincronía renueva la marca con la que la emisión propone el siguiente sin consultar. Si el fisco va
/// adelante (o atrás), la borra y muere visible en vez de callar.
/// </summary>
public sealed class FiscalCheckHandler(BillingDbContext db, ModuleRegistry registry, ModuleClient client, TimeProvider time, IOptions<FiscalOptions> fiscal) : IJobHandler
{
    public string Kind => JobKinds.FiscalCheck;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (await ReconcileHandler.Pending(db, ct).ConfigureAwait(false))
        {
            return JobResult.PollLater(ReconcileHandler.Wait);
        }

        var seriesId = JsonDocument.Parse(job.Payload).RootElement.GetProperty("series_id").GetInt64();
        var series = await db.FiscalSeries.SingleOrDefaultAsync(s => s.Id == seriesId, ct).ConfigureAwait(false);
        var issuer = series is null ? null : await db.IssuerProfiles.SingleOrDefaultAsync(i => i.Id == series.IssuerId && i.Active, ct).ConfigureAwait(false);
        if (series is null || issuer?.ModuleInstanceId is null)
        {
            return JobResult.Done;
        }

        var module = await registry.Resolve(issuer.ModuleInstanceId.Value, ct).ConfigureAwait(false);
        if (FiscalEmissionHandler.IsProduction(module) && !fiscal.Value.ProductionEnabled)
        {
            // Con la traba de producción puesta no se le pregunta nada al fisco real.
            return JobResult.Done;
        }

        var input = new JsonObject { ["point_of_sale"] = series.PointOfSale, ["type_code"] = series.TypeCode };
        var result = await FiscalQuery.Call(registry, client, module, "last_number", input, ct).ConfigureAwait(false);
        var last = result["number"]?.GetValue<long>() ?? throw new PermanentFailureException("El módulo fiscal no informó el último número.");
        var inSync = EmissionRules.CheckSeries(last, series.LastNumber) == SeriesCheck.InSync;
        series.SyncedAt = inSync ? time.GetUtcNow() : null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return inSync ? JobResult.Done : throw new PermanentFailureException($"Serie fuera de sincronía: el fisco tiene {last} y el núcleo {series.LastNumber} ({series.PointOfSale}-{series.TypeCode}).");
    }
}

/// <summary>
/// Completa la cotización que el fisco registró para un comprobante en moneda extranjera que se autorizó sin ella
/// (cancelación en la misma moneda) porque la consulta de después de autorizar falló. El QR la exige (RG 4892). Corre
/// con la clave de la serie, se reintenta mientras el fisco no responda y el barrido diario revive el que murió.
/// </summary>
public sealed class FiscalRateHandler(BillingDbContext db, ModuleRegistry registry, ModuleClient client, IOptions<FiscalOptions> fiscal) : IJobHandler
{
    public string Kind => JobKinds.FiscalRate;

    public static string DedupKey(long fiscalDocumentId) => string.Create(CultureInfo.InvariantCulture, $"{JobKinds.FiscalRate}:{fiscalDocumentId}");

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (await ReconcileHandler.Pending(db, ct).ConfigureAwait(false))
        {
            return JobResult.PollLater(ReconcileHandler.Wait);
        }

        var id = JsonDocument.Parse(job.Payload).RootElement.GetProperty("fiscal_document_id").GetInt64();
        var voucher = await db.FiscalDocuments.SingleOrDefaultAsync(f => f.Id == id, ct).ConfigureAwait(false);
        var payload = voucher is null ? null : JsonNode.Parse(voucher.Payload)?.AsObject();
        var issuer = voucher is null ? null : await db.IssuerProfiles.SingleOrDefaultAsync(i => i.Id == voucher.IssuerId, ct).ConfigureAwait(false);
        if (voucher is null || payload?["document"] is not JsonObject document || document["exchange_rate"] is not null || issuer?.ModuleInstanceId is null)
        {
            return JobResult.Done;
        }

        var module = await registry.Resolve(issuer.ModuleInstanceId.Value, ct).ConfigureAwait(false);
        if (FiscalEmissionHandler.IsProduction(module) && !fiscal.Value.ProductionEnabled)
        {
            throw new PermanentFailureException(FiscalEmissionHandler.ProductionDisabled);
        }

        var input = new JsonObject { ["point_of_sale"] = voucher.PointOfSale, ["type_code"] = voucher.TypeCode, ["number"] = voucher.Number };
        var found = await FiscalQuery.Call(registry, client, module, "find", input, ct).ConfigureAwait(false);
        document["exchange_rate"] = found["document"]?["exchange_rate"]?.DeepClone()
            ?? throw new PermanentFailureException($"El fisco no informa la cotización del comprobante {voucher.PointOfSale}-{voucher.TypeCode}-{voucher.Number}.");
        voucher.Payload = payload.ToJsonString();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

/// <summary>Una consulta al módulo fiscal fuera de la emisión: guarda el estado que devuelva y falla con reintento o en forma definitiva según el error.</summary>
internal static class FiscalQuery
{
    public static async Task<JsonObject> Call(ModuleRegistry registry, ModuleClient client, ResolvedInstance module, string operation, JsonObject input, CancellationToken ct)
    {
        var result = await client.Call(module.Endpoint, $"/v0/fiscal/{operation}", new ModuleRequest(module.Data, [], input), null, module.Timeout(operation), ct).ConfigureAwait(false);
        if (result.InstanceState is { } state)
        {
            await registry.SaveState(module, state, ct).ConfigureAwait(false);
        }

        if (result.Error is { } error)
        {
            throw ErrorCodes.IsRetryable(error.Code)
                ? new RetryLaterException($"{error.Code}: {error.Message}", error.RetryAfter is { } s ? TimeSpan.FromSeconds(s) : null)
                : new PermanentFailureException($"{error.Code}: {error.Message}");
        }

        return result.Response?.Result as JsonObject ?? [];
    }
}

