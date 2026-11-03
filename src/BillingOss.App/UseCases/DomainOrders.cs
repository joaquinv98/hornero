using System.Globalization;
using System.Text.Json.Nodes;
using BillingOss.App.Platform;
using BillingOss.App.Persistence;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BillingOss.App.UseCases;

/// <summary>
/// Un pedido de dominio: el nombre, los años, los nameservers, el código EPP si es una transferencia, el titular si no es
/// el de la cuenta, si se registra con la protección de identidad y si lleva la gestión de DNS o el reenvío de correo.
/// </summary>
public sealed record DomainOrder(string Name, int Years = 1, IReadOnlyList<string>? Nameservers = null, string? AuthCode = null, JsonObject? Registrant = null, string? PaymentMethod = null, bool Privacy = false, bool Dns = false, bool EmailForwarding = false)
{
    /// <summary>Las opciones pedidas, por su nombre en <see cref="DomainOptions"/>.</summary>
    public IReadOnlyList<string> Options =>
        [.. new[] { (Dns, DomainOptions.Dns), (EmailForwarding, DomainOptions.EmailForwarding), (Privacy, DomainOptions.Privacy) }.Where(o => o.Item1).Select(o => o.Item2)];
}

public sealed record PlacedDomainOrder(long DomainId, long? InvoiceId);

/// <summary>
/// Registro y transferencia de dominios. En una transacción crea el dominio pendiente con el precio de renovación
/// congelado, anota la operación sin mandarla y emite la factura. La operación va al registrar recién cuando la factura
/// queda pagada, o enseguida si el total es cero. Antes de facturar consulta el nombre como la búsqueda: no se factura
/// el registro de un nombre tomado o premium ni la transferencia de uno libre.
/// </summary>
public sealed class DomainOrderService(BillingDbContext db, JobQueue jobs, BillingCore core, TldPricing pricing, SecretBox? box = null, DomainSearch? search = null)
{
    public Task<PlacedDomainOrder> Register(long accountId, DomainOrder order, CancellationToken ct = default) => Place(accountId, "register", order, ct);

    /// <summary>Transferencia entrante con el código EPP del cliente, que se guarda cifrado y solo viaja en la llamada.</summary>
    public Task<PlacedDomainOrder> Transfer(long accountId, DomainOrder order, CancellationToken ct = default) => Place(accountId, "transfer", order with { Years = 1 }, ct);

    private async Task<PlacedDomainOrder> Place(long accountId, string operation, DomainOrder order, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (!DomainNames.TryNormalize(order.Name, out var name))
        {
            throw new ValidationException($"{order.Name} no es un nombre de dominio válido.");
        }

        if (order.Years is < 1 or > 10)
        {
            throw new ValidationException("Los años van de 1 a 10.");
        }

        var transfer = operation == "transfer";
        var authCode = order.AuthCode?.Trim();
        if (transfer && (string.IsNullOrEmpty(authCode) || authCode.Length > 255))
        {
            throw new ValidationException("Falta el código de autorización (EPP) del dominio.");
        }

        if (transfer && box is null)
        {
            throw new ValidationException("Las transferencias necesitan la llave de secretos de la instalación.");
        }

        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No existe la cuenta {accountId}.");
        var tld = "." + DomainNames.Split(name).Extension;
        var offer = await pricing.Offer(tld, account.Currency, order.Years, ct).ConfigureAwait(false);
        var amount = transfer ? offer?.Transfer : offer?.Register;
        // Una transferencia necesita un registrar que la haga: el manual de .ar no tiene a quién pedírsela.
        var capabilities = offer is null ? new HashSet<string>() : await DomainJobs.Capabilities(db, offer.RegistrarInstanceId, ct).ConfigureAwait(false);
        if (offer is null || amount is null || (transfer && !capabilities.Contains("registrar.transfer")))
        {
            throw new ValidationException(transfer ? $"No ofrecemos transferencias de {tld}." : $"No ofrecemos {tld} a {order.Years} años.");
        }

        // La protección se pide con el registro. En una transferencia se prende después, desde el dominio.
        if (order.Privacy && (transfer || !capabilities.Contains("registrar.privacy")))
        {
            throw new ValidationException($"El registro de {tld} no ofrece la protección de identidad al pedirlo.");
        }

        if (order.Options.FirstOrDefault(o => !capabilities.Contains(DomainOptions.Capability(o))) is { } missing)
        {
            throw new ValidationException($"El registrar de {tld} no ofrece esta opción: {DomainJobs.OptionText(missing)}.");
        }

        var offered = await TldPricing.OfferedOptions(db, tld, ct).ConfigureAwait(false);
        if (order.Options.FirstOrDefault(o => !offered.Contains(o)) is { } hidden)
        {
            throw new ValidationException($"No ofrecemos esta opción en {tld}: {DomainJobs.OptionText(hidden)}.");
        }

        // Una opción con precio suma su precio anual por los años del pedido, y queda congelada en la renovación.
        var optionPrices = await pricing.OptionPrices(account.Currency, ct).ConfigureAwait(false);
        var options = order.Options.Sum(o => optionPrices[o]) * order.Years;

        if (await db.Domains.AnyAsync(d => d.Name == name && (d.Status == DomainStatus.Pending || d.Status == DomainStatus.Active || d.Status == DomainStatus.Grace), ct).ConfigureAwait(false))
        {
            throw new ValidationException($"{name} ya está en el sistema.");
        }

        var nameservers = order.Nameservers is { Count: > 0 } given ? DomainService.Hosts(given)
            : core.Options.DomainNameservers.Count > 0 ? DomainService.Hosts(core.Options.DomainNameservers)
            : transfer ? [] : throw new ValidationException("Faltan los nameservers del dominio.");
        var registrant = order.Registrant is null ? null : DomainContacts.Clean(order.Registrant);
        await RequireRegistrant(account.Id, registrant, offer.RegistrarInstanceId, ct).ConfigureAwait(false);
        await RequireAvailability(name, tld, account.Currency, transfer, ct).ConfigureAwait(false);

        // Un dominio no tiene producto: va como servicio digital, con la fecha fiscal de hoy. Su precio se carga en el modo
        // de carga de su moneda, que queda congelado con él como en un servicio.
        var taxes = await core.Taxes(ct).ConfigureAwait(false);
        var now = core.Now();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var domain = new RegisteredDomain(accountId, name, offer.RegistrarInstanceId, account.Currency, offer.Renew ?? amount.Value, now, order.Years)
        {
            PriceMode = taxes.Settings.DefaultPriceMode(account.Currency),
            Nameservers = new JsonArray([.. nameservers.Select(n => (JsonNode)n)]).ToJsonString(),
            Registrant = registrant?.ToJsonString(),
            PaymentMethod = order.PaymentMethod,
        };
        foreach (var option in order.Options)
        {
            domain.ContractOption(option, optionPrices[option]);
        }

        db.Domains.Add(domain);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ValidationException($"{name} ya está en el sistema.");
        }

        if (transfer)
        {
            domain.AuthCode = box!.Encrypt(authCode!, "domains.domains", "auth_code", domain.Id);
        }

        var input = new JsonObject { ["name"] = name, ["years"] = order.Years };
        if (nameservers.Count > 0)
        {
            input["nameservers"] = new JsonArray([.. nameservers.Select(n => (JsonNode)n)]);
        }

        if (order.Privacy)
        {
            input["privacy"] = true;
        }

        await DomainJobs.Request(db, jobs, domain.Id, operation, input, operation, now, ct, enqueue: false).ConfigureAwait(false);

        var receiver = await core.Receiver(accountId, ct).ConfigureAwait(false);
        var tax = taxes.Line(TaxCategories.Digital, receiver, accountId, core.Today());
        var invoice = new Document(DocumentKind.Invoice, accountId, account.Currency, domain.PriceMode, core.Today().AddDays(core.Options.InvoiceDueDays), now);
        invoice.AddLine(LineKind.OneTime, DomainJobs.WithOptions(Describe(operation, name, order.Years), order.Options), 1, amount.Value + options, tax.Treatment, tax.Rate,
            dedupKey: string.Create(CultureInfo.InvariantCulture, $"domain:{domain.Id}:{operation}"), category: TaxCategories.Digital, reason: tax.Reason, legend: tax.Legend, domainId: domain.Id);
        long? invoiceId = null;
        if (invoice.Compute().Total > 0m)
        {
            db.Documents.Add(invoice);
            await core.IssueDraft(invoice, receiver, ct).ConfigureAwait(false);
            invoiceId = invoice.Id;
        }
        else
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await DomainJobs.ReleaseWaiting(db, jobs, domain.Id, ct).ConfigureAwait(false);
        }

        await core.Commit(tx, ct).ConfigureAwait(false);
        return new PlacedDomainOrder(domain.Id, invoiceId);
    }

    public static string Describe(string operation, string name, int years) => operation == "transfer"
        ? $"Transferencia del dominio {name}, con 1 año de renovación"
        : $"Registro del dominio {name}, {(years == 1 ? "1 año" : $"{years} años")}";

    /// <summary>
    /// La misma consulta que la búsqueda, también para los pedidos por la API: un nombre tomado no se registra, uno
    /// premium no se vende al precio de la extensión y uno libre no se transfiere. Si nadie lo sabe (el registrar no
    /// contesta y el RDAP tampoco), el pedido sigue: el registrar lo verifica al registrar y el staff lo cancela si hace
    /// falta, con la nota de crédito.
    /// </summary>
    private async Task RequireAvailability(string name, string tld, Currency currency, bool transfer, CancellationToken ct)
    {
        if (search is null)
        {
            return;
        }

        var found = (await search.Check([name], currency, ct).ConfigureAwait(false)).Single();
        if (found.Premium)
        {
            throw new ValidationException($"{name} es un nombre premium: no se vende al precio de {tld}.");
        }

        if (transfer && found.Available == true)
        {
            throw new ValidationException($"{name} no está registrado: se pide como registro, no como transferencia.");
        }

        if (!transfer && found.Available == false)
        {
            throw new ValidationException($"{name} no está disponible.");
        }
    }

    /// <summary>
    /// Un registrar con API rechaza el registro si faltan datos del titular: se avisa antes de facturar. El manual no los
    /// pide, porque el staff hace el trámite con el cliente.
    /// </summary>
    private async Task RequireRegistrant(long accountId, JsonObject? own, long instanceId, CancellationToken ct)
    {
        var inCore = await (from i in db.ModuleInstances
                            join m in db.ModuleInstallations on i.InstallationId equals m.Id
                            where i.Id == instanceId
                            select m.BaseUrl).SingleAsync(ct).ConfigureAwait(false) == ManualModule.Address;
        if (inCore)
        {
            return;
        }

        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId, ct).ConfigureAwait(false);
        var tax = await db.TaxIdentities.AsNoTracking().SingleOrDefaultAsync(t => t.AccountId == accountId && t.ValidTo == null, ct).ConfigureAwait(false);
        var registrant = DomainContacts.Registrant(account, tax, own?.ToJsonString());
        if (DomainContacts.Missing(registrant) is { Count: > 0 } missing)
        {
            throw new ValidationException($"Faltan datos del titular: {string.Join(", ", missing)}.");
        }
    }
}
