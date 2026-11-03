using System.Globalization;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.UseCases;

/// <summary>Precio vigente de un TLD en una instancia de registrar, moneda y años. Nulo es que esa operación no se ofrece.</summary>
public sealed record TldOffer(string Tld, long RegistrarInstanceId, Currency Currency, int Years, decimal? Register, decimal? Renew, decimal? Transfer, DateOnly ValidFrom);

/// <summary>
/// Cómo se pasa del costo del registrar al precio de venta: cotización a la moneda de venta, margen e impuesto en
/// porcentaje, y redondeo hacia arriba al múltiplo pedido. Si los precios se publican con IVA incluido, el impuesto va acá.
/// </summary>
public sealed record PriceRule(Currency Currency, decimal ExchangeRate = 1m, decimal MarginPercent = 0m, decimal TaxPercent = 0m, decimal RoundTo = 1m)
{
    public decimal? Apply(decimal? cost)
    {
        if (cost is not { } c)
        {
            return null;
        }

        var raw = c * ExchangeRate * (1m + (MarginPercent / 100m)) * (1m + (TaxPercent / 100m));
        var step = RoundTo > 0m ? RoundTo : 0.01m;
        return Math.Ceiling(raw / step) * step;
    }
}

/// <summary>Una fila propuesta por la importación: el costo del registrar y el precio que resulta.</summary>
public sealed record ImportedTldPrice(string Tld, int Years, string CostCurrency, decimal? CostRegister, decimal? CostRenew, decimal? CostTransfer, decimal? Register, decimal? Renew, decimal? Transfer);

/// <summary>
/// Precios por TLD (sección 9 del modelo de dominio): los vigentes, la carga a mano y la importación desde los costos
/// que publica el registrar (get_prices, sección 9.2 del protocolo). Un cambio es una fila nueva con su vigencia: los
/// dominios ya contratados conservan su precio congelado.
/// </summary>
public sealed class TldPricing(BillingDbContext db, BillingCore core, RegistrarQueries? queries = null)
{
    /// <summary>Los precios vigentes hoy en la moneda: por TLD, instancia y años, la fila de vigencia más reciente.</summary>
    public async Task<IReadOnlyList<TldOffer>> Current(Currency currency, CancellationToken ct = default)
    {
        var today = core.Today();
        var rows = await db.TldPrices.AsNoTracking().Where(p => p.Currency == currency && p.ValidFrom <= today).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.GroupBy(p => (p.Tld, p.RegistrarInstanceId, p.Years))
            .Select(g => g.MaxBy(p => p.ValidFrom)!)
            .Select(p => new TldOffer(p.Tld, p.RegistrarInstanceId, p.Currency, p.Years, p.Register, p.Renew, p.Transfer, p.ValidFrom))
            .OrderBy(o => o.Tld, StringComparer.Ordinal).ThenBy(o => o.Years).ThenBy(o => o.RegistrarInstanceId)];
    }

    /// <summary>
    /// El precio con que se vende el TLD a esos años. Si dos instancias lo venden, gana la de vigencia más reciente y,
    /// a igual vigencia, la de menor ID, así el resultado no depende del orden de la base.
    /// </summary>
    public async Task<TldOffer?> Offer(string tld, Currency currency, int years, CancellationToken ct = default) =>
        (await Current(currency, ct).ConfigureAwait(false))
            .Where(o => o.Tld == Tld(tld) && o.Years == years)
            .OrderByDescending(o => o.ValidFrom).ThenBy(o => o.RegistrarInstanceId)
            .FirstOrDefault();

    /// <summary>
    /// El precio anual vigente hoy de cada opción del dominio (<see cref="DomainOptions"/>) en la moneda. Sin precio
    /// cargado es cero: la opción es gratis y no se contrata.
    /// </summary>
    public Task<IReadOnlyDictionary<string, decimal>> OptionPrices(Currency currency, CancellationToken ct = default) => OptionPrices(db, core.Today(), currency, ct);

    public static async Task<IReadOnlyDictionary<string, decimal>> OptionPrices(BillingDbContext db, DateOnly today, Currency currency, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.DomainOptionPrices.AsNoTracking().Where(p => p.Currency == currency && p.ValidFrom <= today).ToListAsync(ct).ConfigureAwait(false);
        return DomainOptions.All.ToDictionary(o => o, o => rows.Where(r => r.Option == o).MaxBy(r => r.ValidFrom)?.Price ?? 0m, StringComparer.Ordinal);
    }

    /// <summary>
    /// Las opciones que se ofrecen en la extensión <paramref name="tld"/> (".com.ar"). Sin fila en <see cref="TldOptions"/>,
    /// todas. Cada una sale además solo si el registrar declara su capacidad.
    /// </summary>
    public static async Task<IReadOnlySet<string>> OfferedOptions(BillingDbContext db, string tld, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await db.TldOptions.AsNoTracking().SingleOrDefaultAsync(t => t.Tld == tld, ct).ConfigureAwait(false);
        return DomainOptions.All.Where(o => row?.Offers(o) != false).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Las extensiones con sus opciones cargadas. Las que no figuran ofrecen todas.</summary>
    public async Task<IReadOnlyList<TldOptions>> OptionsByTld(CancellationToken ct = default) =>
        await db.TldOptions.AsNoTracking().OrderBy(t => t.Tld).ToListAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Qué opciones se ofrecen en la extensión. Las que se dejan de ofrecer no se piden más, y los dominios que ya las
    /// tienen las conservan.
    /// </summary>
    public async Task SetTldOptions(string tld, IEnumerable<string> offered, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(offered);
        var extension = Tld(tld);
        var on = offered.ToHashSet(StringComparer.Ordinal);
        if (on.FirstOrDefault(o => !DomainOptions.All.Contains(o)) is { } unknown)
        {
            throw new ValidationException($"{unknown} no es una opción de dominio: van {string.Join(", ", DomainOptions.All)}.");
        }

        var row = await db.TldOptions.SingleOrDefaultAsync(t => t.Tld == extension, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new TldOptions(extension);
            db.TldOptions.Add(row);
        }

        row.Dns = on.Contains(DomainOptions.Dns);
        row.EmailForwarding = on.Contains(DomainOptions.EmailForwarding);
        row.Privacy = on.Contains(DomainOptions.Privacy);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Carga o corrige el precio anual de una opción desde una fecha, con la misma regla que los de los TLD.</summary>
    public async Task SetOptionPrice(string option, Currency currency, decimal price, DateOnly validFrom, CancellationToken ct = default)
    {
        if (!DomainOptions.All.Contains(option))
        {
            throw new ValidationException($"{option} no es una opción de dominio: van {string.Join(", ", DomainOptions.All)}.");
        }

        if (price < 0m)
        {
            throw new ValidationException("El precio de la opción no puede ser negativo.");
        }

        var row = await db.DomainOptionPrices.SingleOrDefaultAsync(p => p.Option == option && p.Currency == currency && p.ValidFrom == validFrom, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new DomainOptionPrice(option, currency, validFrom);
            db.DomainOptionPrices.Add(row);
        }

        row.Price = Math.Round(price, 2, MidpointRounding.AwayFromZero);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Carga o corrige un precio desde una fecha. La misma fecha corrige la fila, otra fecha agrega una nueva.</summary>
    public async Task Set(string tld, long instanceId, Currency currency, int years, decimal? register, decimal? renew, decimal? transfer, DateOnly validFrom, CancellationToken ct = default)
    {
        await Upsert([new ImportedTldPrice(Tld(tld), years, currency.Code, null, null, null, register, renew, transfer)], instanceId, currency, validFrom, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Trae los costos del registrar de la instancia y calcula el precio de venta con la regla. Sin
    /// <paramref name="apply"/> solo devuelve la propuesta para revisarla. Con él la guarda desde <paramref name="validFrom"/>.
    /// Si el registrar publica en una sola moneda, la cotización de la regla la pasa a la de venta. Si publica en
    /// varias, se toman solo los costos en la moneda de venta.
    /// </summary>
    public async Task<IReadOnlyList<ImportedTldPrice>> ImportFromRegistrar(long instanceId, PriceRule rule, DateOnly validFrom, bool apply, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (queries is null)
        {
            throw new ValidationException("Los módulos no están habilitados en esta instalación.");
        }

        if (rule.ExchangeRate <= 0m || rule.MarginPercent < -100m || rule.TaxPercent < 0m || rule.RoundTo < 0m)
        {
            throw new ValidationException("La cotización tiene que ser positiva y el margen, el impuesto y el redondeo no pueden ser negativos.");
        }

        var result = await queries.Call(instanceId, "get_prices", [], new JsonObject(), ct).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            throw new ValidationException(error.Code == ErrorCodes.NotSupported
                ? "El registrar de esta instancia no publica sus costos."
                : $"El registrar no respondió los costos ({error.Code}): {error.Message}");
        }

        var costs = (result.Response?.Result?["prices"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var currencies = costs.Select(c => c["currency"]?.ToString()).Where(c => c is not null).Distinct().ToList();
        var source = currencies.Count == 1 ? currencies[0]! : rule.Currency.Code;
        var rows = new List<ImportedTldPrice>();
        foreach (var c in costs.Where(c => c["currency"]?.ToString() == source))
        {
            var years = c["years"]?.GetValue<int>() ?? 0;
            if (c["tld"]?.ToString() is not { Length: > 1 } tld || years is < 1 or > 10)
            {
                continue;
            }

            var (register, renew, transfer) = (Decimal(c["register"]), Decimal(c["renew"]), Decimal(c["transfer"]));
            rows.Add(new ImportedTldPrice(Tld(tld), years, source, register, renew, transfer, rule.Apply(register), rule.Apply(renew), rule.Apply(transfer)));
        }

        rows = [.. rows.OrderBy(r => r.Tld, StringComparer.Ordinal).ThenBy(r => r.Years)];
        if (apply)
        {
            await Upsert(rows, instanceId, rule.Currency, validFrom, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return rows;
    }

    /// <summary>
    /// Precios desde un CSV propio, para los registrars que no publican sus costos (ResellerClub o el manual de .ar). Una
    /// fila por extensión y años, con el encabezado tld, years, register, renew y transfer en cualquier orden. Acepta
    /// punto y coma con coma decimal, como exporta una planilla en castellano. Una celda vacía es que no se ofrece. Sin
    /// <paramref name="apply"/> solo valida y devuelve las filas.
    /// </summary>
    public async Task<IReadOnlyList<ImportedTldPrice>> ImportCsv(long instanceId, Currency currency, string csv, DateOnly validFrom, bool apply, CancellationToken ct = default)
    {
        var lines = (csv ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count is < 2 or > 2001)
        {
            throw new ValidationException("El CSV lleva un encabezado y entre 1 y 2000 filas.");
        }

        if (!await db.ModuleInstances.AnyAsync(i => i.Id == instanceId, ct).ConfigureAwait(false))
        {
            throw new ValidationException($"No existe la instancia {instanceId}.");
        }

        var separator = lines[0].Contains(';', StringComparison.Ordinal) ? ';' : ',';
        var header = lines[0].Split(separator).Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToArray();
        int Column(string name) => Array.IndexOf(header, name) is var i and >= 0 ? i : throw new ValidationException($"Falta la columna {name} en el encabezado.");
        var (tldAt, yearsAt, registerAt, renewAt, transferAt) = (Column("tld"), Column("years"), Column("register"), Column("renew"), Column("transfer"));

        var rows = new List<ImportedTldPrice>();
        for (var n = 1; n < lines.Count; n++)
        {
            var cells = lines[n].Split(separator);
            string Cell(int i) => i < cells.Length ? cells[i].Trim().Trim('"') : string.Empty;
            decimal? Amount(int i)
            {
                var text = Cell(i);
                if (text.Length == 0)
                {
                    return null;
                }

                // Con punto y coma, si hay coma es la decimal y los puntos separan miles.
                var normalized = separator == ';' && text.Contains(',', StringComparison.Ordinal) ? text.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.') : text;
                return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : throw new ValidationException($"Fila {n + 1}: {text} no es un importe.");
            }

            if (!int.TryParse(Cell(yearsAt), NumberStyles.None, CultureInfo.InvariantCulture, out var years) || years is < 1 or > 10)
            {
                throw new ValidationException($"Fila {n + 1}: los años van de 1 a 10.");
            }

            rows.Add(new ImportedTldPrice(Tld(Cell(tldAt)), years, currency.Code, null, null, null, Amount(registerAt), Amount(renewAt), Amount(transferAt)));
        }

        if (rows.GroupBy(r => (r.Tld, r.Years)).FirstOrDefault(g => g.Count() > 1) is { } repeated)
        {
            throw new ValidationException($"{repeated.Key.Tld} a {repeated.Key.Years} años está más de una vez.");
        }

        rows = [.. rows.OrderBy(r => r.Tld, StringComparer.Ordinal).ThenBy(r => r.Years)];
        if (apply)
        {
            await Upsert(rows, instanceId, currency, validFrom, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return rows;
    }

    /// <summary>".com.ar" en minúsculas, en punycode y con el punto inicial, que es como se guarda.</summary>
    public static string Tld(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return DomainNames.TryNormalize("x." + text.Trim().TrimStart('.'), out var name) ? name[1..] : throw new ValidationException($"{text} no es una extensión válida.");
    }

    private async Task Upsert(IReadOnlyList<ImportedTldPrice> rows, long instanceId, Currency currency, DateOnly validFrom, CancellationToken ct)
    {
        foreach (var row in rows)
        {
            if (row.Years is < 1 or > 10 || new[] { row.Register, row.Renew, row.Transfer }.Any(p => p < 0m))
            {
                throw new ValidationException($"{row.Tld}: los años van de 1 a 10 y los precios no pueden ser negativos.");
            }

            var price = await db.TldPrices.SingleOrDefaultAsync(
                p => p.Tld == row.Tld && p.RegistrarInstanceId == instanceId && p.Currency == currency && p.Years == row.Years && p.ValidFrom == validFrom, ct).ConfigureAwait(false);
            if (price is null)
            {
                price = new TldPrice(row.Tld, instanceId, currency, row.Years, validFrom);
                db.TldPrices.Add(price);
            }

            price.Register = row.Register;
            price.Renew = row.Renew;
            price.Transfer = row.Transfer;
        }
    }

    private static decimal? Decimal(JsonNode? node) =>
        node?.ToString() is { Length: > 0 } text && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>Consultas sin efecto a un registrar, con espera corta, para lo que alguien pide en el momento (sección 13 del modelo).</summary>
public sealed class RegistrarQueries(ModuleRegistry registry, ModuleClient client, ManualModule manual)
{
    /// <summary>
    /// Prueba la configuración de la instancia contra el registrar (/v0/instance/test). El registrar manual corre en el
    /// núcleo y no tiene nada que probar.
    /// </summary>
    public Task<ModuleCallResult> Test(long instanceId, CancellationToken ct) => Send(instanceId, null, [], [], TimeSpan.FromSeconds(15), ct);

    public Task<ModuleCallResult> Call(long instanceId, string operation, JsonObject context, JsonObject input, CancellationToken ct, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        return Send(instanceId, operation, context, input, timeout, ct);
    }

    /// <summary>Una operación del tipo registrar o, sin <paramref name="operation"/>, la prueba de la instancia.</summary>
    private async Task<ModuleCallResult> Send(long instanceId, string? operation, JsonObject context, JsonObject input, TimeSpan? timeout, CancellationToken ct)
    {
        ResolvedInstance module;
        try
        {
            module = await registry.Resolve(instanceId, ct).ConfigureAwait(false);
        }
        catch (PermanentFailureException ex)
        {
            return new ModuleCallResult(null, new ModuleError(ErrorCodes.InvalidConfig, ex.Message, false));
        }

        var path = operation is null ? "/v0/instance/test" : $"/v0/registrar/{operation}";
        var request = new ModuleRequest(module.Data, context, input);
        var result = module.InCore
            ? operation is null ? new ModuleCallResult(ModuleResponse.Completed(), null) : await manual.Call(module, path, request, null, ct).ConfigureAwait(false)
            : await client.Call(module.Endpoint, path, request, null, timeout ?? module.Timeout(operation ?? "test"), ct).ConfigureAwait(false);
        if (result.Response?.InstanceState is { } state)
        {
            await registry.SaveState(module, state, ct).ConfigureAwait(false);
        }

        return result;
    }
}
