using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.UseCases;

/// <summary>
/// Disponibilidad de un nombre. Available nulo es que no se pudo confirmar, y Reason dice por qué. Source dice quién
/// contestó: core (ya está en el sistema), registrar, rdap, dns o none. Offer es el precio con que se vende a un año, o
/// nulo si el TLD no se vende.
/// </summary>
public sealed record DomainAvailability(string Name, bool? Available, string Source, string? Reason = null, bool Premium = false, TldOffer? Offer = null);

/// <summary>
/// Búsqueda de dominios para el portal y el admin. Primero manda lo que hay en el sistema, después el registrar que
/// vende cada TLD, y si el registrar no sabe (el manual de .ar) o no responde, el RDAP del registro con reintentos y el
/// DNS como alternativa. Son consultas sin efecto: se llaman en el momento, con espera corta, y las del registro van en
/// paralelo con un presupuesto total (<see cref="DomainLookupOptions.SearchBudgetSeconds"/>): lo que no llega queda sin
/// confirmar, porque quien busca está esperando la página.
/// </summary>
public sealed class DomainSearch(BillingDbContext db, TldPricing pricing, DomainLookup lookup, BillingCore core, RegistrarQueries? queries = null)
{
    public const int MaxNames = 20;

    /// <summary>
    /// Arma la lista de nombres de lo que escribió la persona: un nombre completo se consulta tal cual y una etiqueta
    /// sola ("miempresa") se prueba con los TLD que se venden a un año, en su orden.
    /// </summary>
    public async Task<IReadOnlyList<string>> Candidates(string text, Currency currency, CancellationToken ct = default)
    {
        var typed = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (typed.Contains('.', StringComparison.Ordinal))
        {
            return DomainNames.TryNormalize(typed, out var name) ? [name] : throw new ValidationException($"{text} no es un nombre de dominio válido.");
        }

        if (!DomainNames.TryNormalize(typed + ".com", out var probe))
        {
            throw new ValidationException($"{text} no es un nombre de dominio válido.");
        }

        var label = DomainNames.Split(probe).Label;
        var tlds = (await pricing.Current(currency, ct).ConfigureAwait(false)).Where(o => o.Years == 1 && o.Register is not null).Select(o => o.Tld).Distinct();
        return [.. tlds.Select(t => label + t).Take(MaxNames)];
    }

    public async Task<IReadOnlyList<DomainAvailability>> Check(IEnumerable<string> names, Currency currency, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        var list = new List<string>();
        foreach (var text in names)
        {
            list.Add(DomainNames.TryNormalize(text, out var name) ? name : throw new ValidationException($"{text} no es un nombre de dominio válido."));
        }

        list = [.. list.Distinct(StringComparer.Ordinal)];
        if (list.Count is 0 or > MaxNames)
        {
            throw new ValidationException($"Se consultan entre 1 y {MaxNames} nombres.");
        }

        var offers = (await pricing.Current(currency, ct).ConfigureAwait(false))
            .Where(o => o.Years == 1)
            .GroupBy(o => o.Tld, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.ValidFrom).ThenBy(o => o.RegistrarInstanceId).First(), StringComparer.Ordinal);
        TldOffer? OfferFor(string name) => offers.GetValueOrDefault("." + DomainNames.Split(name).Extension);

        var live = await db.Domains.AsNoTracking()
            .Where(d => list.Contains(d.Name) && (d.Status == DomainStatus.Pending || d.Status == DomainStatus.Active || d.Status == DomainStatus.Grace))
            .Select(d => d.Name).ToListAsync(ct).ConfigureAwait(false);

        // Cada registrar contesta por los suyos en una sola llamada.
        var answers = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (queries is not null)
        {
            foreach (var group in list.Where(n => !live.Contains(n)).GroupBy(n => OfferFor(n)?.RegistrarInstanceId).Where(g => g.Key is not null))
            {
                var result = await queries.Call(group.Key!.Value, "check", [], new JsonObject { ["names"] = new JsonArray([.. group.Select(n => (JsonNode)n)]) }, ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                foreach (var row in (result.Response?.Result?["results"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (row["name"]?.ToString() is { } answered)
                    {
                        answers[answered] = row;
                    }
                }
            }
        }

        // Lo que nadie contestó va al registro, todo a la vez y dentro del presupuesto.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, core.Options.DomainLookup.SearchBudgetSeconds)));
        var pending = list.Where(n => !live.Contains(n) && answers.GetValueOrDefault(n)?["available"] is not JsonValue).ToList();
        var registry = pending.Zip(await Task.WhenAll(pending.Select(n => Registry(n, budget.Token, ct))).ConfigureAwait(false))
            .ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal);

        var results = new List<DomainAvailability>();
        foreach (var name in list)
        {
            var offer = OfferFor(name);
            if (live.Contains(name))
            {
                results.Add(new DomainAvailability(name, false, "core", "in_system", Offer: offer));
            }
            else if (answers.GetValueOrDefault(name) is { } row && row["available"] is JsonValue available)
            {
                var premium = row["premium"]?.GetValue<bool>() == true;
                results.Add(new DomainAvailability(name, available.GetValue<bool>(), "registrar", premium ? "premium" : null, premium, offer));
            }
            else
            {
                var answer = registry[name];
                results.Add(new DomainAvailability(name, answer.Registered is { } registered ? !registered : null, answer.Source, answer.Reason, Offer: offer));
            }
        }

        return results;
    }

    /// <summary>Una consulta al registro que se corta con el presupuesto: sin respuesta queda sin confirmar, nunca libre.</summary>
    private async Task<RegistryAnswer> Registry(string name, CancellationToken budget, CancellationToken ct)
    {
        try
        {
            return await lookup.Lookup(name, budget).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RegistryAnswer(null, "rdap", Reason: "rdap_unavailable");
        }
    }
}
