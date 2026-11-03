using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Domains;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.UseCases;

/// <summary>
/// Sincronización diaria con el registro (sección 9.2 del protocolo). Una vez por día este trabajo encola uno por cada
/// dominio vivo, con la clave de entidad del dominio, así no se cruza con sus operaciones y cualquier nodo los corre.
/// </summary>
public sealed class DomainSyncDayHandler(BillingDbContext db, BillingCore core, RegistrarQueries? queries = null) : IJobHandler
{
    public string Kind => JobKinds.DomainSyncDay;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        if (queries is null)
        {
            return JobResult.Done; // Sin módulos habilitados no hay con quién sincronizar.
        }

        var day = core.Today().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var now = core.Now();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform.jobs (kind, payload, entity_key, dedup_key, status, run_at, attempts, max_attempts, created_at, updated_at)
            SELECT {JobKinds.DomainSync}, jsonb_build_object('domain_id', d.id), 'domain:' || d.id, {JobKinds.DomainSync} || ':' || d.id || ':' || {day},
                   'Pending', {now}, 0, 10, {now}, {now}
            FROM domains.domains d
            WHERE d.status IN ('Active', 'Grace')
            ON CONFLICT (dedup_key) WHERE dedup_key IS NOT NULL DO NOTHING
            """, ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}

/// <summary>
/// Trae del registro el estado, el vencimiento, los nameservers y el bloqueo de un dominio. Un registrar con API
/// contesta get_domain. El registrar manual de .ar no tiene API, así que el vencimiento sale del RDAP de NIC Argentina,
/// y si no contesta se prueba al día siguiente. El estado de facturación no cambia: sale de lo pago. Solo un dominio que
/// se fue a otro registrar deja de renovarse, y su renovación facturada sin pagar se acredita.
/// </summary>
public sealed class DomainSyncHandler(BillingDbContext db, RegistrarQueries queries, DomainLookup lookup, BillingCore core, CreditService credit) : IJobHandler
{
    /// <summary>Motivo de la nota que acredita la renovación de un dominio que se fue a otro registrar.</summary>
    public const string TransferredAwayReason = "El dominio se transfirió a otro registrar";

    public string Kind => JobKinds.DomainSync;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var id = JsonDocument.Parse(job.Payload).RootElement.GetProperty("domain_id").GetInt64();
        var domain = await db.Domains.SingleOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);
        if (domain is not { Status: DomainStatus.Active or DomainStatus.Grace })
        {
            return JobResult.Done;
        }

        var instance = await (from i in db.ModuleInstances
                              join m in db.ModuleInstallations on i.InstallationId equals m.Id
                              where i.Id == domain.RegistrarInstanceId
                              select new { i.Config, m.BaseUrl }).SingleAsync(ct).ConfigureAwait(false);
        if (instance.BaseUrl == ManualModule.Address)
        {
            var registry = JsonNode.Parse(instance.Config)?["registry"]?.ToString();
            return registry == "nic.ar" ? await FromRdap(domain, ct).ConfigureAwait(false) : JobResult.Done;
        }

        if (instance.BaseUrl.Length == 0)
        {
            return JobResult.Done; // El módulo del registrar todavía no se desplegó.
        }

        var context = await DomainJobs.Context(db, domain, ct).ConfigureAwait(false);
        var result = await queries.Call(domain.RegistrarInstanceId, "get_domain", context, [], ct).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            if (ErrorCodes.IsRetryable(error.Code))
            {
                throw new RetryLaterException($"{error.Code}: {error.Message}", TimeSpan.FromSeconds(error.RetryAfter ?? 900));
            }

            domain.RegistryStatus = "error";
            domain.SyncedAt = core.Now();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return JobResult.Done;
        }

        var answer = result.Response?.Result as JsonObject ?? [];
        Apply(
            domain,
            answer["status"]?.ToString() ?? "unknown",
            Date(answer["expires_on"]?.ToString()),
            (answer["nameservers"] as JsonArray ?? []).Select(n => n?.ToString() ?? string.Empty).Where(n => n.Length > 0).ToList(),
            Flag(answer["locked"]));
        domain.IdProtection = Flag(answer["privacy"]) ?? domain.IdProtection;
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (domain.Status == DomainStatus.Cancelled)
        {
            // Las notas antes de guardar el dominio, en el orden de cualquier nota de crédito: la factura y después el dominio.
            await CreditOpenRenewals(domain.Id, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await core.Commit(tx, ct).ConfigureAwait(false);
        return JobResult.Done;
    }

    /// <summary>
    /// Un dominio que se fue a otro registrar ya no se puede renovar: el renglón de su renovación en cada factura sin
    /// pagar del todo se acredita con una nota, y lo demás de una factura compartida se sigue debiendo. Una ya paga, con
    /// la renovación pedida al registrar, la decide el staff.
    /// </summary>
    private async Task CreditOpenRenewals(long domainId, CancellationToken ct)
    {
        var invoices = await db.Documents
            .Where(d => d.Kind == DocumentKind.Invoice && d.IssueDate != null && d.Total - d.Credited - d.Allocated > 0 && d.Lines.Any(l => l.DomainId == domainId && l.Kind == LineKind.Renewal))
            .OrderBy(d => d.Id).Select(d => d.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var id in invoices)
        {
            if ((await DomainJobs.CreditedDomains(db, id, ct).ConfigureAwait(false)).Contains(domainId))
            {
                continue;
            }

            var lines = await db.DocumentLines.Where(l => l.DocumentId == id && l.DomainId == domainId && l.Kind == LineKind.Renewal)
                .Select(l => new CreditLine(l.Description, l.Quantity, l.UnitPrice, l.Treatment, l.Rate, null, l.Category, domainId)).ToListAsync(ct).ConfigureAwait(false);
            await credit.Credit(id, lines, TransferredAwayReason, ct).ConfigureAwait(false);
        }
    }

    private async Task<JobResult> FromRdap(RegisteredDomain domain, CancellationToken ct)
    {
        var answer = await lookup.Lookup(domain.Name, ct).ConfigureAwait(false);
        if (answer.Source != "rdap" || answer.Registered is null)
        {
            // El DNS no da vencimientos y NIC Argentina falla por horas: se vuelve a probar mañana.
            return JobResult.Done;
        }

        Apply(domain, answer.Registered.Value ? "active" : "not_found", answer.ExpiresOn, [], null);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return JobResult.Done;
    }

    private void Apply(RegisteredDomain domain, string status, DateOnly? expires, IReadOnlyList<string> nameservers, bool? locked)
    {
        domain.RegistryStatus = status.Length > 32 ? status[..32] : status;
        domain.SyncedAt = core.Now();
        domain.ExpiresOn = expires ?? domain.ExpiresOn;
        domain.Locked = locked ?? domain.Locked;
        if (nameservers.Count > 0)
        {
            domain.Nameservers = new JsonArray([.. nameservers.Select(n => (JsonNode)n)]).ToJsonString();
        }

        if (status == "transferred_away")
        {
            domain.Close();
        }
    }

    private static DateOnly? Date(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static bool? Flag(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : null;
}
