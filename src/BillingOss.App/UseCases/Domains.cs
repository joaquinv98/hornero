using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.UseCases;

/// <summary>Pedidos de operaciones de dominio: una fila por intención y un trabajo que la lleva al registrar.</summary>
public static class DomainJobs
{
    /// <summary>Operaciones de un pedido, que esperan el pago antes de ir al registrar.</summary>
    public static readonly string[] OrderOperations = ["register", "transfer"];

    /// <summary>
    /// Contratación de una opción con precio, con <c>option</c> y su precio anual en <c>price</c>. Espera el pago de su
    /// factura y no va al registrar: se aplica en el núcleo, y la protección sigue con su set_privacy.
    /// </summary>
    public const string AddOption = "add_option";

    /// <summary>La clave del renglón que factura la contratación <paramref name="operationId"/>: al pagarse, la suelta.</summary>
    public static string OptionLineKey(long operationId) => string.Create(CultureInfo.InvariantCulture, $"domain-option:{operationId}");

    /// <summary>
    /// El renglón que factura la renovación de <paramref name="period"/>. La clave es la del período, así la corrida y
    /// Renovar ahora nunca lo facturan dos veces. Los renglones emitidos están congelados, así que la factura anulada con
    /// una nota de crédito conserva la suya: la que la reemplaza lleva la misma clave con cuántas hubo antes para ese
    /// período ("domain:7:2026-11-07:1") y el texto y el precio del último renglón de ese período, como los períodos de
    /// los servicios. El precio de un período queda congelado al facturarlo: una opción contratada después ya se cobró
    /// aparte hasta su fin. Corre bajo el bloqueo de la fila del dominio.
    /// </summary>
    public static async Task<(string Key, string Description, decimal Amount)> RenewalLine(BillingDbContext db, RegisteredDomain domain, Period period, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(domain);
        var key = string.Create(CultureInfo.InvariantCulture, $"domain:{domain.Id}:{period.Start:yyyy-MM-dd}");
        var reissue = key + ":";
        var before = await db.DocumentLines.Where(l => l.DomainId == domain.Id && (l.DedupKey == key || l.DedupKey!.StartsWith(reissue)))
            .OrderByDescending(l => l.Id).Select(l => new { l.Description, l.UnitPrice }).ToListAsync(ct).ConfigureAwait(false);
        return before.Count == 0
            ? (key, Describe(domain, period), domain.RecurringAmount)
            : (string.Create(CultureInfo.InvariantCulture, $"{reissue}{before.Count}"), before[0].Description, before[0].UnitPrice);
    }

    /// <summary>El inicio del período que factura un renglón de renovación de dominio, si su clave es de esas.</summary>
    public static DateOnly? RenewalPeriodStart(string? lineKey) =>
        lineKey?.Split(':') is ["domain", _, var date, ..] && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ? start : null;

    /// <summary>
    /// Los dominios cuyos renglones de renovación en la factura están acreditados enteros por las líneas con su dominio
    /// de las notas de crédito: el staff los sacó de una factura compartida, por ejemplo porque el cliente no renueva.
    /// Pagar el resto no los renueva.
    /// </summary>
    public static async Task<IReadOnlySet<long>> CreditedDomains(BillingDbContext db, long invoiceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var notes = db.Documents.Where(n => n.Kind == DocumentKind.CreditNote && n.RelatedDocumentId == invoiceId && n.IssueDate != null).Select(n => n.Id);
        var credited = await db.DocumentLines.Where(l => notes.Contains(l.DocumentId) && l.DomainId != null)
            .GroupBy(l => l.DomainId!.Value).Select(g => new { g.Key, Amount = g.Sum(l => l.Amount) }).ToDictionaryAsync(g => g.Key, g => g.Amount, ct).ConfigureAwait(false);
        if (credited.Count == 0)
        {
            return new HashSet<long>();
        }

        var invoiced = await db.DocumentLines.Where(l => l.DocumentId == invoiceId && l.Kind == LineKind.Renewal && l.DomainId != null)
            .GroupBy(l => l.DomainId!.Value).Select(g => new { g.Key, Amount = g.Sum(l => l.Amount) }).ToListAsync(ct).ConfigureAwait(false);
        return invoiced.Where(i => credited.GetValueOrDefault(i.Key) >= i.Amount).Select(i => i.Key).ToHashSet();
    }

    /// <summary>
    /// La factura de renovación del dominio que todavía cuenta, si hay una: un borrador o una emitida sin pagar del todo
    /// (con o sin clave de período, como las importadas), o una paga con la clave del período que empieza en lo pago, que
    /// el pago todavía no aplicó. Con ella no se emite otra. Una anulada, por la mora o por el staff, ya no cuenta, ni una
    /// con el renglón del dominio acreditado, ni <paramref name="except"/>.
    /// </summary>
    public static async Task<long?> OpenRenewalInvoice(BillingDbContext db, long domainId, DateOnly paidThrough, CancellationToken ct, long? except = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var key = string.Create(CultureInfo.InvariantCulture, $"domain:{domainId}:{paidThrough:yyyy-MM-dd}");
        var candidates = await db.Documents
            .Where(d => d.Kind == DocumentKind.Invoice && d.Id != except && d.Lines.Any(l => l.DomainId == domainId && l.Kind == LineKind.Renewal
                && (d.IssueDate == null || d.Total - d.Credited - d.Allocated > 0 || (d.Total - d.Credited > 0 && l.DedupKey != null && l.DedupKey.StartsWith(key)))))
            .OrderByDescending(d => d.Id)
            .Select(d => d.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var id in candidates)
        {
            if (!(await CreditedDomains(db, id, ct).ConfigureAwait(false)).Contains(domainId))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Cierra un pedido de dominio que no salió al registrar, o que ya falló ahí: lo que esperaba el pago falla con el
    /// motivo y el dominio queda cancelado, con el nombre libre. Falso si el pedido está en curso en el registrar, que se
    /// cierra solo después de fallar porque si no puede completarse igual. Corre bajo el bloqueo de la fila del dominio.
    /// </summary>
    public static async Task<bool> CloseOrder(BillingDbContext db, RegisteredDomain domain, string reason, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(domain);
        var op = await db.DomainOperations.Where(o => o.DomainId == domain.Id && OrderOperations.Contains(o.Operation)).OrderByDescending(o => o.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var jobKey = string.Create(CultureInfo.InvariantCulture, $"{JobKinds.DomainOperation}:{op?.Id}");
        if (op is { Status: DomainOperationStatus.Completed }
            || (op is { Status: DomainOperationStatus.Pending } && await db.Jobs.AnyAsync(j => j.DedupKey == jobKey, ct).ConfigureAwait(false)))
        {
            return false;
        }

        // ponytail: un pago que llega justo ahora puede soltar la operación antes de este commit. El handler mira el
        // estado del dominio antes de llamar, así que la ventana es la de esa lectura.
        foreach (var waiting in await db.DomainOperations.Where(o => o.DomainId == domain.Id && o.Status == DomainOperationStatus.Pending).ToListAsync(ct).ConfigureAwait(false))
        {
            waiting.Fail($"Pedido cancelado: {reason}", now);
        }

        domain.Close();
        return true;
    }

    /// <summary>La contratación que factura un renglón, si lo es.</summary>
    public static long? OptionOperation(string? lineKey) =>
        lineKey is { } key && key.StartsWith("domain-option:", StringComparison.Ordinal)
            && long.TryParse(key.AsSpan("domain-option:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>
    /// Renovación en el registrar del período que vencía en <paramref name="currentExpiry"/>. Pedirla dos veces para el
    /// mismo vencimiento no duplica: la clave de deduplicación es por dominio y vencimiento.
    /// </summary>
    public static Task RequestRenewal(BillingDbContext db, JobQueue jobs, RegisteredDomain domain, DateOnly currentExpiry, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var expiry = currentExpiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Request(db, jobs, domain.Id, "renew", new JsonObject { ["years"] = domain.Years, ["current_expiry"] = expiry }, $"renew:{expiry}", now, ct);
    }

    /// <summary>
    /// Anota la operación y, con <paramref name="enqueue"/>, la manda a la cola. Sin encolar queda esperando, como el
    /// registro de un pedido hasta que se paga su factura. Devuelve su ID, o nulo si esa intención ya estaba anotada.
    /// </summary>
    public static async Task<long?> Request(BillingDbContext db, JobQueue jobs, long domainId, string operation, JsonObject input, string dedupKey, DateTimeOffset now, CancellationToken ct, bool enqueue = true)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(input);
        await using var cmd = await Sql.Command(db, """
            INSERT INTO domains.operations (domain_id, operation, input, dedup_key, status, created_at)
            VALUES (@domain, @operation, @input::jsonb, @dedup, 'Pending', @now)
            ON CONFLICT (domain_id, dedup_key) DO NOTHING
            RETURNING id
            """, ct, ("domain", domainId), ("operation", operation), ("input", input.ToJsonString()), ("dedup", dedupKey), ("now", now)).ConfigureAwait(false);
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long id)
        {
            return null;
        }

        if (enqueue)
        {
            await Enqueue(jobs, domainId, id, ct).ConfigureAwait(false);
        }

        return id;
    }

    /// <summary>Los trabajos de un mismo dominio corren en orden y de a uno: su clave de entidad es el dominio.</summary>
    public static Task Enqueue(JobQueue jobs, long domainId, long operationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        return jobs.Enqueue(JobKinds.DomainOperation, new { operation_id = operationId }, Entity(domainId), $"{JobKinds.DomainOperation}:{operationId}", ct: ct);
    }

    /// <summary>Manda a la cola el registro o la transferencia que esperaban el pago. Repetirlo no duplica.</summary>
    public static async Task ReleaseWaiting(BillingDbContext db, JobQueue jobs, long domainId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var waiting = await db.DomainOperations
            .Where(o => o.DomainId == domainId && o.Status == DomainOperationStatus.Pending && OrderOperations.Contains(o.Operation))
            .Select(o => o.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var id in waiting)
        {
            await Enqueue(jobs, domainId, id, ct).ConfigureAwait(false);
        }
    }

    public static string Entity(long domainId) => string.Create(CultureInfo.InvariantCulture, $"domain:{domainId}");

    /// <summary>
    /// De las operaciones de un dominio, la fallida más reciente que no se volvió a pedir: ninguna posterior es del
    /// mismo tipo. Es la que se puede reintentar. Las contrataciones de opciones no cuentan, porque no van al registrar.
    /// </summary>
    public static DomainOperation? LastFailed(IEnumerable<DomainOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var later = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in operations.Where(o => o.Operation != AddOption).OrderByDescending(o => o.Id))
        {
            if (op.Status == DomainOperationStatus.Failed && !later.Contains(op.Operation))
            {
                return op;
            }

            later.Add(op.Operation);
        }

        return null;
    }

    /// <summary>
    /// Texto propio de la línea de renovación. Las opciones del dominio van en la misma línea, porque su precio está
    /// dentro del de la renovación.
    /// </summary>
    public static string Describe(RegisteredDomain domain, Period period)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var years = domain.Years == 1 ? "1 año" : $"{domain.Years} años";
        return WithOptions(BillingCore.Describe($"Renovación del dominio {domain.Name}, {years}", period), DomainOptions.All.Where(domain.HasOption));
    }

    /// <summary>El texto de una línea con las opciones que lleva, como "... + gestión de DNS".</summary>
    public static string WithOptions(string text, IEnumerable<string> options) =>
        string.Concat(new[] { text }.Concat(options.Select(o => " + " + OptionText(o))));

    /// <summary>Cómo se llama la opción en las facturas y los mensajes.</summary>
    public static string OptionText(string option) => option switch
    {
        DomainOptions.Dns => "gestión de DNS",
        DomainOptions.EmailForwarding => "reenvío de correo",
        DomainOptions.Privacy => "protección de identidad",
        _ => throw new ArgumentOutOfRangeException(nameof(option), option, "No es una opción de dominio."),
    };

    /// <summary>
    /// Las instancias que atienden dominios, con sus capacidades: las de módulos registrar y las del registrar manual,
    /// que se reconocen por su registro configurado (las del manual sin registro son de productos).
    /// </summary>
    public static async Task<IReadOnlyList<(long Id, string Name, IReadOnlySet<string> Capabilities)>> Registrars(BillingDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await (from i in db.ModuleInstances.AsNoTracking()
                          join m in db.ModuleInstallations on i.InstallationId equals m.Id
                          orderby i.Name, i.Id
                          select new { i.Id, i.Name, i.Config, m.BaseUrl, m.Manifest }).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<(long, string, IReadOnlySet<string>)>();
        foreach (var r in rows)
        {
            var manifest = JsonNode.Parse(r.Manifest);
            var registrar = (manifest?["kinds"] as JsonArray ?? []).Any(k => k?.ToString() == "registrar")
                && (r.BaseUrl != ManualModule.Address || JsonNode.Parse(r.Config)?["registry"] is not null);
            if (registrar)
            {
                result.Add((r.Id, r.Name, (manifest?["capabilities"] as JsonArray ?? []).Select(c => c?.ToString() ?? string.Empty).ToHashSet(StringComparer.Ordinal)));
            }
        }

        return result;
    }

    /// <summary>Capacidades que declara el manifiesto aceptado de la instancia (sección 5 del protocolo).</summary>
    public static async Task<IReadOnlySet<string>> Capabilities(BillingDbContext db, long instanceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var manifest = await (from i in db.ModuleInstances
                              join m in db.ModuleInstallations on i.InstallationId equals m.Id
                              where i.Id == instanceId
                              select m.Manifest).SingleAsync(ct).ConfigureAwait(false);
        return (JsonNode.Parse(manifest)?["capabilities"] as JsonArray ?? []).Select(c => c?.ToString() ?? string.Empty).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Contexto del tipo registrar (sección 8 del protocolo): el dominio, sus IDs externos, la cuenta y el titular. El
    /// titular es el propio del dominio, o el que sale de la cuenta y su identidad fiscal, salvo que la operación traiga
    /// uno nuevo.
    /// </summary>
    public static async Task<JsonObject> Context(BillingDbContext db, RegisteredDomain domain, CancellationToken ct, JsonObject? registrant = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(domain);
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == domain.AccountId, ct).ConfigureAwait(false);
        var tax = await db.TaxIdentities.AsNoTracking().SingleOrDefaultAsync(t => t.AccountId == domain.AccountId && t.ValidTo == null, ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["domain"] = new JsonObject
            {
                ["id"] = domain.Id,
                ["name"] = domain.Name,
                ["status"] = domain.Status.ToString().ToLowerInvariant(),
                ["expires_on"] = domain.ExpiresOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["nameservers"] = JsonNode.Parse(domain.Nameservers),
                ["external_ids"] = JsonNode.Parse(domain.ExternalIds),
            },
            ["account"] = new JsonObject
            {
                ["id"] = account.Id,
                ["language"] = account.Language,
                ["name"] = account.Name,
                ["company_name"] = account.CompanyName,
                ["email"] = account.Email,
                ["phone"] = account.Phone,
            },
            ["contacts"] = new JsonObject { ["registrant"] = registrant?.DeepClone() ?? DomainContacts.Registrant(account, tax, domain.Registrant) },
        };
    }
}

/// <summary>El titular de un dominio con los campos de context.contacts.registrant (sección 9.2 del protocolo).</summary>
public static partial class DomainContacts
{
    public static IReadOnlyList<string> Fields { get; } = ["first_name", "last_name", "company", "email", "phone", "address1", "address2", "city", "state", "postal_code", "country"];

    /// <summary>Lo que pide cualquier registro. El resto es opcional.</summary>
    public static IReadOnlyList<string> Required { get; } = ["first_name", "last_name", "email", "phone", "address1", "city", "postal_code", "country"];

    /// <summary>El titular propio del dominio o, si no tiene, el que sale de la cuenta y de su identidad fiscal vigente.</summary>
    public static JsonObject Registrant(Account account, TaxIdentity? tax, string? own)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (own is not null && JsonNode.Parse(own) is JsonObject custom)
        {
            return custom;
        }

        var name = account.Name.Trim();
        var space = name.IndexOf(' ', StringComparison.Ordinal);
        return Clean(new JsonObject
        {
            ["first_name"] = space > 0 ? name[..space] : name,
            ["last_name"] = space > 0 ? name[(space + 1)..].Trim() : name,
            ["company"] = account.CompanyName,
            ["email"] = account.Email,
            ["phone"] = Phone(account.Phone),
            ["address1"] = tax?.Street,
            ["city"] = tax?.City,
            ["state"] = tax?.Province,
            ["postal_code"] = tax?.PostalCode,
            ["country"] = tax?.Country ?? "AR",
        });
    }

    /// <summary>Solo los campos conocidos, recortados, y sin los vacíos.</summary>
    public static JsonObject Clean(JsonObject registrant)
    {
        ArgumentNullException.ThrowIfNull(registrant);
        var clean = new JsonObject();
        foreach (var field in Fields)
        {
            if (registrant[field]?.ToString().Trim() is { Length: > 0 } value)
            {
                clean[field] = field == "country" ? value.ToUpperInvariant() : field == "phone" ? Phone(value) : value[..Math.Min(value.Length, 200)];
            }
        }

        return clean;
    }

    public static IReadOnlyList<string> Missing(JsonObject registrant)
    {
        ArgumentNullException.ThrowIfNull(registrant);
        return [.. Required.Where(f => string.IsNullOrWhiteSpace(registrant[f]?.ToString()))];
    }

    /// <summary>
    /// Teléfono como lo piden los registros: "+54.1155551234". Sin "+" se toma el código del país por defecto. Con "+",
    /// el código es lo que va hasta el primer separador, o 54 si empieza así.
    /// </summary>
    public static string? Phone(string? text, string defaultCode = "54")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (Formatted().IsMatch(trimmed))
        {
            return trimmed;
        }

        var digits = new string([.. trimmed.Where(char.IsAsciiDigit)]);
        if (digits.Length == 0)
        {
            return null;
        }

        if (!trimmed.StartsWith('+'))
        {
            return $"+{defaultCode}.{digits.TrimStart('0')}";
        }

        var code = Prefix().Match(trimmed) is { Success: true } m ? m.Groups[1].Value
            : digits.StartsWith(defaultCode, StringComparison.Ordinal) ? defaultCode
            : digits[..1];
        return $"+{code}.{digits[code.Length..]}";
    }

    [GeneratedRegex(@"^\+\d{1,3}\.\d{4,14}$")]
    private static partial Regex Formatted();

    [GeneratedRegex(@"^\+(\d{1,3})[\s\-.(]")]
    private static partial Regex Prefix();
}

/// <summary>
/// Lleva una operación de dominio al registrar de su instancia por el protocolo v0 (o al registrar manual, dentro
/// del núcleo) y aplica el resultado: vencimiento, nameservers, bloqueo, código EPP, titular e IDs externos. Las de un
/// mismo dominio van de a una. Un registro, una transferencia o una renovación que termina, y una transferencia que
/// falla, se le avisan al cliente en la misma transacción.
/// </summary>
public sealed class DomainOperationHandler(BillingDbContext db, ModuleRegistry registry, ModuleClient client, ManualModule manual, SecretBox box, BillingCore core, IDomainMail mail, JobQueue jobs) : IJobHandler
{
    public string Kind => JobKinds.DomainOperation;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var id = JsonDocument.Parse(job.Payload).RootElement.GetProperty("operation_id").GetInt64();
        var op = await db.Set<DomainOperation>().SingleAsync(o => o.Id == id, ct).ConfigureAwait(false);
        if (op.Status != DomainOperationStatus.Pending)
        {
            return JobResult.Done;
        }

        var domain = await db.Set<RegisteredDomain>().SingleAsync(d => d.Id == op.DomainId, ct).ConfigureAwait(false);
        if (domain.Status == DomainStatus.Cancelled)
        {
            // Un pedido cancelado o un dominio que se fue: nada más va al registrar.
            op.Fail("El dominio está cancelado.", core.Now());
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return JobResult.Done;
        }

        if (op.Operation == DomainJobs.AddOption)
        {
            // Una opción pagada se aplica en el núcleo. La protección, además, se le pide al registrar.
            var contract = JsonNode.Parse(op.Input)!.AsObject();
            var option = contract["option"]!.GetValue<string>();
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            domain.ContractOption(option, contract["price"]!.GetValue<decimal>());
            op.Complete("{}", core.Now());
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (option == DomainOptions.Privacy && !domain.IdProtection)
            {
                await DomainJobs.Request(db, jobs, domain.Id, "set_privacy", new JsonObject { ["enabled"] = true }, string.Create(CultureInfo.InvariantCulture, $"set_privacy:{DomainJobs.AddOption}:{op.Id}"), core.Now(), ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return JobResult.Done;
        }

        ResolvedInstance module;
        try
        {
            module = await registry.Resolve(domain.RegistrarInstanceId, ct).ConfigureAwait(false);
        }
        catch (PermanentFailureException ex)
        {
            op.Fail(ex.Message, core.Now());
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            throw;
        }

        var stored = JsonNode.Parse(op.Input)!.AsObject();
        var context = await DomainJobs.Context(db, domain, ct, stored["registrant"] as JsonObject).ConfigureAwait(false);
        var input = stored.DeepClone().AsObject();
        if (op.Operation == "transfer" && domain.AuthCode is { } code)
        {
            // El código del cliente viaja solo en la llamada: la operación guarda la intención sin el secreto.
            input["auth_code"] = box.Decrypt(code, "domains.domains", "auth_code", domain.Id);
        }

        var (path, body, key) = op.PendingOperation is { } pending
            ? ($"/v0/operations/{pending}", new JsonObject(), null)
            : ($"/v0/registrar/{op.Operation}", input, string.Create(CultureInfo.InvariantCulture, $"dom-{domain.Id}-{op.Operation}-{op.Id}"));
        var request = new ModuleRequest(module.Data, context, body);
        var result = module.InCore
            ? await manual.Call(module, path, request, key, ct).ConfigureAwait(false)
            : await client.Call(module.Endpoint, path, request, key, module.Timeout(op.Operation), ct).ConfigureAwait(false);

        // El estado se guarda también cuando la llamada falló: un token renovado no se pierde con el error.
        if (result.InstanceState is { } state)
        {
            await registry.SaveState(module, state, ct).ConfigureAwait(false);
        }

        if (result.Error is { } error)
        {
            if (op.PendingOperation is not null && error.Code == ErrorCodes.NotFound)
            {
                // El módulo perdió la operación: se repite la original con la misma clave (sección 7 del protocolo).
                op.PendingOperation = null;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                throw new RetryLaterException("El registrar perdió la operación en curso.", TimeSpan.FromSeconds(5));
            }

            if (ProtocolServiceModules.Retryable(error))
            {
                throw new RetryLaterException($"{error.Code}: {error.Message}", error.RetryAfter is { } s ? TimeSpan.FromSeconds(s) : null);
            }

            await using (var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                op.Fail($"{error.Code}: {error.Message}", core.Now());
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                if (op.Operation == "transfer")
                {
                    // El motivo estable del registrar (sección 9.2 del protocolo), nunca su mensaje: el aviso va al cliente.
                    var reason = error.Details?["upstream"]?["reason"]?.ToString() is { Length: > 0 and <= 40 } upstream ? upstream : "failed";
                    await DomainNotices.OperationFinished(db, mail, core.Options, domain, op, DomainNotice.TransferFailed, reason, ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            throw new PermanentFailureException($"{error.Code}: {error.Message}");
        }

        var response = result.Response!;
        if (response.Status == ModuleResponse.PendingStatus)
        {
            op.PendingOperation = response.OperationId;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return JobResult.PollLater(TimeSpan.FromSeconds(response.RetryAfter ?? 15));
        }

        await using (var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            var started = Apply(domain, op, stored, response.Result as JsonObject ?? []);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            DomainNotice? notice = started ? (op.Operation == "transfer" ? DomainNotice.Transferred : DomainNotice.Registered)
                : op.Operation == "renew" ? DomainNotice.Renewed
                : null;
            if (notice is { } finished)
            {
                await DomainNotices.OperationFinished(db, mail, core.Options, domain, op, finished, null, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        return JobResult.Done;
    }

    /// <summary>Aplica el resultado al dominio y cierra la operación. Verdadero si con esto arrancó un dominio pedido.</summary>
    private bool Apply(RegisteredDomain domain, DomainOperation op, JsonObject input, JsonObject result)
    {
        var started = DomainJobs.OrderOperations.Contains(op.Operation) && domain.Status == DomainStatus.Pending;
        if (result["expires_on"]?.GetValue<string>() is { } text
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires))
        {
            if (started)
            {
                domain.Start(expires);
            }

            domain.ExpiresOn = expires;
        }
        else if (started)
        {
            // El registro no informó el vencimiento: se cuenta desde hoy y la sincronización lo corrige.
            domain.Start(core.Today().AddYears(input["years"]?.GetValue<int>() ?? domain.Years));
        }

        if (result["nameservers"] is JsonArray { Count: > 0 } nameservers)
        {
            domain.Nameservers = nameservers.ToJsonString();
        }

        if (result["external_ids"] is JsonObject { Count: > 0 } ids)
        {
            var merged = JsonNode.Parse(domain.ExternalIds)?.AsObject() ?? [];
            foreach (var (k, v) in ids)
            {
                merged[k] = v?.DeepClone();
            }

            domain.ExternalIds = merged.ToJsonString();
        }

        if (result["locked"] is JsonValue locked && locked.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            domain.Locked = locked.GetValue<bool>();
        }

        // El código con que se trajo el dominio ya se usó: el registro lo cambia, así que no se guarda para mostrarlo.
        if (op.Operation == "transfer")
        {
            domain.AuthCode = null;
        }

        // El código EPP se guarda cifrado en el dominio y nunca en el resultado de la operación (D-09).
        if (result["secrets"]?["auth_code"]?.ToString() is { Length: > 0 } code)
        {
            domain.AuthCode = box.Encrypt(code, "domains.domains", "auth_code", domain.Id);
        }

        result.Remove("secrets");
        if (op.Operation == "set_contacts" && input["registrant"] is JsonObject registrant)
        {
            domain.Registrant = registrant.ToJsonString();
        }

        // La protección de identidad: la que se pidió al registrar y quedó hecha.
        if ((op.Operation == "set_privacy" ? result["enabled"] : started ? input["privacy"] : null) is JsonValue privacy
            && privacy.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            domain.IdProtection = privacy.GetValue<bool>();
        }

        if (op.Operation == "renew")
        {
            domain.Renewed(core.Today());
        }

        op.Complete(result.ToJsonString(), core.Now());
        return started;
    }
}

/// <summary>
/// Una opción que ofrece el registrar del dominio: su precio anual vigente (cero es gratis), si el dominio la tiene, si
/// está contratada con cargo y la factura de la contratación que espera el pago.
/// </summary>
public sealed record DomainOptionState(string Option, decimal Price, bool On, bool Paid, long? WaitingInvoice)
{
    /// <summary>Se puede usar: es gratis o está contratada.</summary>
    public bool Usable => On || Price <= 0m;
}

/// <summary>
/// Lo que el cliente y el staff piden sobre un dominio vivo. Cada pedido es una operación por la cola, en el orden del
/// dominio, y solo se ofrece si el registrar de su instancia declara la capacidad.
/// </summary>
public sealed class DomainService(BillingDbContext db, JobQueue jobs, BillingCore core, CreditService credit, SecretBox? box = null, RegistrarQueries? queries = null)
{
    /// <summary>
    /// Las opciones que ofrece el registrar del dominio, con su precio anual vigente, si el dominio la tiene y la factura
    /// de la contratación que espera el pago.
    /// </summary>
    public async Task<IReadOnlyList<DomainOptionState>> Options(RegisteredDomain domain, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var capabilities = await DomainJobs.Capabilities(db, domain.RegistrarInstanceId, ct).ConfigureAwait(false);
        var prices = await TldPricing.OptionPrices(db, core.Today(), domain.Currency, ct).ConfigureAwait(false);
        var waiting = (await db.DomainOperations.AsNoTracking()
            .Where(o => o.DomainId == domain.Id && o.Operation == DomainJobs.AddOption && o.Status == DomainOperationStatus.Pending)
            .Select(o => new { o.Id, o.Input }).ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(o => DomainJobs.OptionLineKey(o.Id), o => JsonNode.Parse(o.Input)?["option"]?.ToString(), StringComparer.Ordinal);
        var keys = waiting.Keys.ToList();
        var invoices = await db.DocumentLines.AsNoTracking().Where(l => l.DedupKey != null && keys.Contains(l.DedupKey))
            .Select(l => new { l.DedupKey, l.DocumentId }).ToListAsync(ct).ConfigureAwait(false);
        var ids = invoices.Select(i => i.DocumentId).ToList();

        // Una factura anulada ya no espera nada: la opción se puede volver a contratar.
        var open = (await db.Documents.AsNoTracking().Where(d => ids.Contains(d.Id)).ToListAsync(ct).ConfigureAwait(false))
            .Where(d => d.Status != InvoiceStatus.Cancelled).Select(d => d.Id).ToHashSet();

        // Una que la extensión dejó de ofrecer sigue en el dominio que ya la tiene, para usarla o darla de baja.
        var offered = await Offered(domain, ct).ConfigureAwait(false);
        return [.. DomainOptions.All.Where(o => capabilities.Contains(DomainOptions.Capability(o)) && (offered.Contains(o) || domain.HasOption(o)))
            .Select(o => new DomainOptionState(o, prices[o], domain.HasOption(o), domain.HasPaidOption(o),
                invoices.Where(i => waiting[i.DedupKey!] == o && open.Contains(i.DocumentId)).Select(i => (long?)i.DocumentId).Max()))];
    }

    /// <summary>La zona como la tiene el registrar ahora, si la administra (registrar.dns). Es una consulta en el momento.</summary>
    public async Task<IReadOnlyList<DnsRecord>> Dns(long domainId, CancellationToken ct = default)
    {
        var answer = await Read(domainId, DomainOptions.Dns, "get_dns", ct).ConfigureAwait(false);
        return answer["records"]?.Deserialize<List<DnsRecord>>(ModuleJson.Options) ?? [];
    }

    /// <summary>
    /// Deja la zona con estos registros. Va entera, así el registrar saca lo que no viene: repetir el pedido deja lo
    /// mismo. Se valida acá con la misma regla que el módulo, para avisar antes de encolar.
    /// </summary>
    public async Task SetDns(long domainId, IEnumerable<DnsRecord> records, CancellationToken ct = default)
    {
        var domain = await Live(domainId, ct).ConfigureAwait(false);
        await RequireOption(domain, DomainOptions.Dns, ct).ConfigureAwait(false);
        if (!DnsRecords.TryNormalize(domain.Name, records, out var clean, out var error))
        {
            throw new ValidationException(error);
        }

        await Request(domain, "set_dns", new JsonObject { ["records"] = JsonSerializer.SerializeToNode(clean, ModuleJson.Options) }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EmailForward>> Forwards(long domainId, CancellationToken ct = default)
    {
        var answer = await Read(domainId, DomainOptions.EmailForwarding, "get_email_forwards", ct).ConfigureAwait(false);
        return answer["forwards"]?.Deserialize<List<EmailForward>>(ModuleJson.Options) ?? [];
    }

    /// <summary>Deja estos reenvíos, enteros como la zona.</summary>
    public async Task SetForwards(long domainId, IEnumerable<EmailForward> forwards, CancellationToken ct = default)
    {
        var domain = await Live(domainId, ct).ConfigureAwait(false);
        await RequireOption(domain, DomainOptions.EmailForwarding, ct).ConfigureAwait(false);
        if (!EmailForwards.TryNormalize(forwards, out var clean, out var error))
        {
            throw new ValidationException(error);
        }

        await Request(domain, "set_email_forwards", new JsonObject { ["forwards"] = JsonSerializer.SerializeToNode(clean, ModuleJson.Options) }, ct).ConfigureAwait(false);
    }

    /// <summary>Prende o apaga la protección de los datos del titular. Al terminar queda en el dominio.</summary>
    public Task<long?> SetPrivacy(long domainId, bool enabled, CancellationToken ct = default) => SetOption(domainId, DomainOptions.Privacy, enabled, ct);

    /// <summary>
    /// Prende o apaga una opción del dominio (<see cref="DomainOptions"/>) que el registrar ofrece. Sin precio es gratis:
    /// la gestión de DNS y el reenvío quedan marcados y la protección se pide al registrar. Con precio, prenderla la
    /// contrata: se factura por día lo que queda hasta el vencimiento ya facturado y se aplica al pagarse, con su precio
    /// anual congelado dentro de la renovación. Apagarla la da de baja sin devolver lo pagado. Devuelve la factura, si
    /// hizo falta una.
    /// </summary>
    public async Task<long?> SetOption(long domainId, string option, bool enabled, CancellationToken ct = default)
    {
        if (!DomainOptions.All.Contains(option))
        {
            throw new ValidationException($"{option} no es una opción de dominio.");
        }

        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "domains.domains", [domainId], ct).ConfigureAwait(false);
        var domain = await Live(domainId, ct).ConfigureAwait(false);
        await Require(domain, DomainOptions.Capability(option), ct).ConfigureAwait(false);
        if (!enabled)
        {
            domain.DropOption(option);
        }
        else if (!domain.HasOption(option))
        {
            if (!(await Offered(domain, ct).ConfigureAwait(false)).Contains(option))
            {
                throw new ValidationException($"No ofrecemos esta opción en .{DomainNames.Split(domain.Name).Extension}: {DomainJobs.OptionText(option)}.");
            }

            var price = (await TldPricing.OptionPrices(db, core.Today(), domain.Currency, ct).ConfigureAwait(false))[option];
            if (price > 0m)
            {
                var invoiceId = await Contract(domain, option, price, ct).ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await core.Commit(tx, ct).ConfigureAwait(false);
                return invoiceId;
            }

            domain.ContractOption(option, 0m);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // La protección la cambia el registrar. Contratada y apagada en el registro (por ejemplo, porque falló), se vuelve a pedir.
        if (option == DomainOptions.Privacy && (!enabled || !domain.IdProtection))
        {
            await DomainJobs.Request(db, jobs, domain.Id, "set_privacy", new JsonObject { ["enabled"] = enabled }, $"set_privacy:{Guid.NewGuid():N}", core.Now(), ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// La contratación de una opción con precio: queda anotada sin mandar y se factura lo que queda, por día, hasta el
    /// vencimiento ya facturado. Al pagarse la factura se aplica, y si no queda nada por cobrar se aplica enseguida. Si
    /// la misma opción ya espera el pago, devuelve esa factura en vez de hacer otra.
    /// </summary>
    private async Task<long?> Contract(RegisteredDomain domain, string option, decimal price, CancellationToken ct)
    {
        var now = core.Now();
        var waiting = await db.DomainOperations.Where(o => o.DomainId == domain.Id && o.Operation == DomainJobs.AddOption && o.Status == DomainOperationStatus.Pending)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var op in waiting.Where(o => JsonNode.Parse(o.Input)?["option"]?.ToString() == option))
        {
            var key = DomainJobs.OptionLineKey(op.Id);
            if (await db.DocumentLines.Where(l => l.DedupKey == key).Select(l => (long?)l.DocumentId).FirstOrDefaultAsync(ct).ConfigureAwait(false) is not { } id)
            {
                return null; // Sin nada que cobrar ya está en la cola.
            }

            if ((await db.Documents.AsNoTracking().SingleAsync(d => d.Id == id, ct).ConfigureAwait(false)).Status != InvoiceStatus.Cancelled)
            {
                return id;
            }

            op.Fail("La factura de la opción se anuló.", now);
        }

        var today = core.Today();
        var end = domain.PaidThrough is { } paid && domain.NextDueDate is { } next && next > paid ? next : domain.PaidThrough ?? today;
        // ponytail: por día sobre 365, también en los bisiestos (la diferencia es de un día de la opción). Si hiciera
        // falta exacto, prorratear sobre el período de la renovación con BillingSchedule.Prorate.
        var amount = end > today ? Rounding.Commercial(price * (end.DayNumber - today.DayNumber) / 365m) : 0m;
        var input = new JsonObject { ["option"] = option, ["price"] = price };
        var opId = await DomainJobs.Request(db, jobs, domain.Id, DomainJobs.AddOption, input, $"{DomainJobs.AddOption}:{option}:{Guid.NewGuid():N}", now, ct, enqueue: amount <= 0m).ConfigureAwait(false);
        if (amount <= 0m)
        {
            return null;
        }

        // Como la renovación: servicio digital, con la fecha fiscal de hoy y el modo de carga congelado en el dominio.
        var receiver = await core.Receiver(domain.AccountId, ct).ConfigureAwait(false);
        var tax = (await core.Taxes(ct).ConfigureAwait(false)).Line(TaxCategories.Digital, receiver, domain.AccountId, today);
        var invoice = new Document(DocumentKind.Invoice, domain.AccountId, domain.Currency, domain.PriceMode, today.AddDays(core.Options.InvoiceDueDays), now);
        var text = string.Create(CultureInfo.InvariantCulture, $"{char.ToUpperInvariant(DomainJobs.OptionText(option)[0])}{DomainJobs.OptionText(option)[1..]} del dominio {domain.Name}, hasta el {end:dd/MM/yyyy}");
        invoice.AddLine(LineKind.OneTime, text, 1, amount, tax.Treatment, tax.Rate, dedupKey: DomainJobs.OptionLineKey(opId!.Value), category: TaxCategories.Digital, reason: tax.Reason, legend: tax.Legend, domainId: domain.Id);
        db.Documents.Add(invoice);
        await core.IssueDraft(invoice, receiver, ct).ConfigureAwait(false);
        return invoice.Id;
    }

    /// <summary>
    /// Renovar ahora se ofrece a un dominio vivo cuya renovación no está facturada, o cuya factura se anuló (por ejemplo
    /// la mora) y nadie la volvió a emitir: el período quedó tomado sin factura abierta.
    /// </summary>
    public async Task<bool> CanRenewNow(RegisteredDomain domain, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return domain is { Status: DomainStatus.Active or DomainStatus.Grace, NextDueDate: { } next, PaidThrough: { } paid }
            && (paid >= next || await DomainJobs.OpenRenewalInvoice(db, domain.Id, paid, ct).ConfigureAwait(false) is null);
    }

    /// <summary>
    /// Renovar ahora: factura el próximo período sin esperar a la corrida, por ejemplo un dominio sin renovación
    /// automática o en gracia. Usa la misma clave de renglón que la corrida, así ese período nunca se factura dos veces,
    /// y lo facturado sin pagar no suma otra factura. Si el período sigue facturado pero su factura se anuló, como la que
    /// anula la mora, lo vuelve a emitir con la clave siguiente y el texto y el precio del renglón anulado, con
    /// vencimiento a los días de hoy. A precio cero avanza el período y pide la renovación enseguida. Devuelve la
    /// factura, o nulo si no hizo falta.
    /// </summary>
    public async Task<long?> RenewNow(long domainId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Sql.LockRows(db, "domains.domains", [domainId], ct).ConfigureAwait(false);
        var domain = await Live(domainId, ct).ConfigureAwait(false);
        if (domain.NextDueDate is not { } next || domain.PaidThrough is not { } paid)
        {
            throw new ValidationException($"{domain.Name} no se renueva desde acá.");
        }

        var reissue = paid < next;
        if (reissue && await DomainJobs.OpenRenewalInvoice(db, domain.Id, paid, ct).ConfigureAwait(false) is not null)
        {
            throw new ValidationException($"La renovación de {domain.Name} ya está facturada: falta pagarla.");
        }

        if (domain.RecurringAmount <= 0m)
        {
            if (!reissue)
            {
                domain.TakeNextPeriod();
            }

            var renewed = domain.RenewalPaid(core.Today())!.Value;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await DomainJobs.RequestRenewal(db, jobs, domain, renewed, core.Now(), ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return null;
        }

        // Como en la corrida: servicio digital, con la fecha fiscal de hoy y el modo de carga congelado en el dominio.
        var receiver = await core.Receiver(domain.AccountId, ct).ConfigureAwait(false);
        var tax = (await core.Taxes(ct).ConfigureAwait(false)).Line(TaxCategories.Digital, receiver, domain.AccountId, core.Today());
        var invoice = new Document(DocumentKind.Invoice, domain.AccountId, domain.Currency, domain.PriceMode, core.Today().AddDays(core.Options.InvoiceDueDays), core.Now(), reason: "renewal");
        var period = reissue ? new Period(paid, paid.AddYears(domain.Years)) : domain.TakeNextPeriod();
        var line = await DomainJobs.RenewalLine(db, domain, period, ct).ConfigureAwait(false);
        invoice.AddLine(LineKind.Renewal, line.Description, 1, line.Amount, tax.Treatment, tax.Rate,
            dedupKey: line.Key, category: TaxCategories.Digital, reason: tax.Reason, legend: tax.Legend, domainId: domain.Id);
        db.Documents.Add(invoice);
        await core.IssueDraft(invoice, receiver, ct).ConfigureAwait(false);
        await core.Commit(tx, ct).ConfigureAwait(false);
        return invoice.Id;
    }

    /// <summary>
    /// Vuelve a pedir la última operación que falló en el registrar, como intención nueva con otra clave: el registro o
    /// la transferencia de un pedido, o la renovación, los nameservers y las demás de un dominio vivo o vencido, por
    /// ejemplo cuando el módulo todavía no estaba desplegado o el revendedor no tenía saldo. En una transferencia se
    /// puede corregir el código EPP, que es la falla más común. Solo la última de su tipo: una que se volvió a pedir y
    /// terminó ya no se reintenta, y una en curso espera. Un dominio cancelado no vuelve al registrar.
    /// </summary>
    public async Task Retry(long domainId, string? authCode = null, CancellationToken ct = default)
    {
        var domain = await db.Domains.SingleOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el dominio {domainId}.");
        var op = domain.Status == DomainStatus.Cancelled ? null
            : DomainJobs.LastFailed(await db.DomainOperations.AsNoTracking().Where(o => o.DomainId == domainId).ToListAsync(ct).ConfigureAwait(false));
        if (op is null)
        {
            throw new ValidationException($"{domain.Name} no tiene una operación fallida para reintentar.");
        }

        if (authCode?.Trim() is { Length: > 0 } code)
        {
            if (op.Operation != "transfer" || code.Length > 255 || box is null)
            {
                throw new ValidationException("El código EPP solo se cambia en una transferencia, y tiene hasta 255 caracteres.");
            }

            domain.AuthCode = box.Encrypt(code, "domains.domains", "auth_code", domain.Id);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await DomainJobs.Request(db, jobs, domain.Id, op.Operation, JsonNode.Parse(op.Input)!.AsObject(), string.Create(CultureInfo.InvariantCulture, $"{op.Operation}:retry:{op.Id}"), core.Now(), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancela un pedido de dominio que no se pudo completar: el dominio queda cancelado, la operación que esperaba el
    /// pago no sale nunca y la factura del pedido se anula con una nota de crédito (lo pagado vuelve como saldo a
    /// favor). Un pedido que ya fue al registrar solo se cancela después de fallar, porque si no puede completarse
    /// igual. Repetirlo termina lo que haya quedado a medias.
    /// </summary>
    public async Task CancelOrder(long domainId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ValidationException("Falta el motivo de la cancelación.");
        }

        db.ChangeTracker.Clear();
        await using (var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            await Sql.LockRows(db, "domains.domains", [domainId], ct).ConfigureAwait(false);
            var domain = await db.Domains.SingleOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"No existe el dominio {domainId}.");
            var op = await LastOrderOperation(domainId, ct).ConfigureAwait(false);
            if (op is null || (domain.Status != DomainStatus.Pending && !(domain.Status == DomainStatus.Cancelled && op.Status == DomainOperationStatus.Failed)))
            {
                throw new ValidationException($"{domain.Name} no es un pedido pendiente.");
            }

            if (!await DomainJobs.CloseOrder(db, domain, reason.Trim(), core.Now(), ct).ConfigureAwait(false))
            {
                throw new ValidationException($"El pedido de {domain.Name} está en curso en el registrar: se cancela solo si falla.");
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        var invoiceId = await db.DocumentLines.Where(l => l.DomainId == domainId && l.Kind == LineKind.OneTime).Select(l => (long?)l.DocumentId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (invoiceId is { } id && await db.Documents.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.Id == id, ct).ConfigureAwait(false) is { IsIssued: true } invoice
            && invoice.Status != InvoiceStatus.Cancelled)
        {
            var lines = invoice.Lines.Select(l => new CreditLine(l.Description, l.Quantity, l.UnitPrice, l.Treatment, l.Rate, l.ServiceId, l.Category, l.DomainId)).ToList();
            await credit.IssueCreditNote(id, lines, $"Pedido de dominio cancelado: {reason.Trim()}", ct).ConfigureAwait(false);
        }
    }

    public async Task SetNameservers(long domainId, IEnumerable<string> nameservers, CancellationToken ct = default)
    {
        var hosts = Hosts(nameservers);
        var domain = await Live(domainId, ct).ConfigureAwait(false);
        await Request(domain, "set_nameservers", new JsonObject { ["nameservers"] = new JsonArray([.. hosts.Select(h => (JsonNode)h)]) }, ct).ConfigureAwait(false);
    }

    public async Task SetLock(long domainId, bool locked, CancellationToken ct = default)
    {
        var domain = await Live(domainId, ct).ConfigureAwait(false);
        await Require(domain, "registrar.lock", ct).ConfigureAwait(false);
        await Request(domain, "set_lock", new JsonObject { ["locked"] = locked }, ct).ConfigureAwait(false);
    }

    /// <summary>Pide el código EPP al registrar. Llega cifrado al dominio y se muestra con <see cref="RevealAuthCode"/>.</summary>
    public async Task RequestAuthCode(long domainId, CancellationToken ct = default)
    {
        var domain = await Live(domainId, ct).ConfigureAwait(false);
        await Require(domain, "registrar.auth_code", ct).ConfigureAwait(false);
        await Request(domain, "get_auth_code", [], ct).ConfigureAwait(false);
    }

    public async Task SetContacts(long domainId, JsonObject registrant, CancellationToken ct = default)
    {
        var clean = DomainContacts.Clean(registrant);
        if (DomainContacts.Missing(clean) is { Count: > 0 } missing)
        {
            throw new ValidationException($"Faltan datos del titular: {string.Join(", ", missing)}.");
        }

        var domain = await Live(domainId, ct).ConfigureAwait(false);
        await Require(domain, "registrar.contacts", ct).ConfigureAwait(false);
        await Request(domain, "set_contacts", new JsonObject { ["registrant"] = clean }, ct).ConfigureAwait(false);
    }

    /// <summary>"No renovar" es distinto de dar de baja: el dominio sigue vivo hasta su vencimiento y no se factura más.</summary>
    public async Task SetAutoRenew(long domainId, bool autoRenew, CancellationToken ct = default)
    {
        var domain = await Live(domainId, ct).ConfigureAwait(false);
        domain.AutoRenew = autoRenew;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Sincroniza ya con el registro, en la cola del dominio, sin esperar a la corrida diaria.</summary>
    public async Task SyncNow(long domainId, CancellationToken ct = default)
    {
        var domain = await Live(domainId, ct).ConfigureAwait(false);
        await jobs.Enqueue(JobKinds.DomainSync, new { domain_id = domain.Id }, DomainJobs.Entity(domain.Id), $"{JobKinds.DomainSync}:{domain.Id}:{Guid.NewGuid():N}", ct: ct).ConfigureAwait(false);
    }

    /// <summary>El código EPP en claro. Quien lo muestra pide confirmación y lo deja en la auditoría.</summary>
    public string? RevealAuthCode(RegisteredDomain domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return domain.AuthCode is null || box is null ? null : box.Decrypt(domain.AuthCode, "domains.domains", "auth_code", domain.Id);
    }

    /// <summary>Entre 2 y 13 nombres de host válidos, sin repetir, como pide la delegación.</summary>
    public static IReadOnlyList<string> Hosts(IEnumerable<string> nameservers)
    {
        ArgumentNullException.ThrowIfNull(nameservers);
        var hosts = new List<string>();
        foreach (var text in nameservers.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            hosts.Add(DomainNames.TryNormalize(text, out var host) ? host : throw new ValidationException($"{text.Trim()} no es un nameserver válido."));
        }

        hosts = [.. hosts.Distinct(StringComparer.Ordinal)];
        return hosts.Count is >= 2 and <= 13 ? hosts : throw new ValidationException("Van entre 2 y 13 nameservers.");
    }

    private async Task<RegisteredDomain> Live(long domainId, CancellationToken ct)
    {
        var domain = await db.Domains.SingleOrDefaultAsync(d => d.Id == domainId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe el dominio {domainId}.");
        return domain.Status is DomainStatus.Active or DomainStatus.Grace
            ? domain
            : throw new ValidationException($"El dominio {domain.Name} no está activo.");
    }

    private Task<DomainOperation?> LastOrderOperation(long domainId, CancellationToken ct) =>
        db.DomainOperations.Where(o => o.DomainId == domainId && DomainJobs.OrderOperations.Contains(o.Operation))
            .OrderByDescending(o => o.Id).FirstOrDefaultAsync(ct);

    private async Task Require(RegisteredDomain domain, string capability, CancellationToken ct)
    {
        if (!(await DomainJobs.Capabilities(db, domain.RegistrarInstanceId, ct).ConfigureAwait(false)).Contains(capability))
        {
            throw new ValidationException($"El registrar de {domain.Name} no permite esta operación.");
        }
    }

    /// <summary>
    /// La opción la ofrece el registrar y el dominio la puede usar: la tiene, o es gratis y su extensión la ofrece. Si tiene
    /// precio y no está contratada, primero se contrata con <see cref="SetOption"/>.
    /// </summary>
    private async Task RequireOption(RegisteredDomain domain, string option, CancellationToken ct)
    {
        await Require(domain, DomainOptions.Capability(option), ct).ConfigureAwait(false);
        if (!domain.HasOption(option)
            && (!(await Offered(domain, ct).ConfigureAwait(false)).Contains(option) || (await TldPricing.OptionPrices(db, core.Today(), domain.Currency, ct).ConfigureAwait(false))[option] > 0m))
        {
            throw new ValidationException($"{domain.Name} no tiene contratada esta opción: {DomainJobs.OptionText(option)}.");
        }
    }

    private Task<IReadOnlySet<string>> Offered(RegisteredDomain domain, CancellationToken ct) =>
        TldPricing.OfferedOptions(db, "." + DomainNames.Split(domain.Name).Extension, ct);

    /// <summary>Una consulta sin efecto al registrar del dominio, en el momento y con espera corta, sobre una de sus opciones.</summary>
    private async Task<JsonObject> Read(long domainId, string option, string operation, CancellationToken ct)
    {
        var domain = await Live(domainId, ct).ConfigureAwait(false);
        await RequireOption(domain, option, ct).ConfigureAwait(false);
        if (queries is null)
        {
            throw new ValidationException("Los módulos no están habilitados en esta instalación.");
        }

        var context = await DomainJobs.Context(db, domain, ct).ConfigureAwait(false);
        var result = await queries.Call(domain.RegistrarInstanceId, operation, context, [], ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        return result.Error is { } error
            ? throw new ValidationException($"El registrar de {domain.Name} no respondió ({error.Code}): {error.Message}")
            : result.Response?.Result as JsonObject ?? [];
    }

    /// <summary>Cada pedido es una intención nueva: repetirlo vuelve a llamar al registrar con otra clave.</summary>
    private async Task Request(RegisteredDomain domain, string operation, JsonObject input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await DomainJobs.Request(db, jobs, domain.Id, operation, input, $"{operation}:{Guid.NewGuid():N}", core.Now(), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}
