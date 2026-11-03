using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Catalog;

public enum OptionKind
{
    /// <summary>Lista desplegable: se elige un valor.</summary>
    Select,

    /// <summary>Botones de opción: se elige un valor, como la lista.</summary>
    Radio,

    /// <summary>Sí o no: cobra el precio de su único valor si se elige.</summary>
    YesNo,

    /// <summary>Cantidad: cobra el precio de su único valor por cada unidad.</summary>
    Quantity,
}

/// <summary>Grupo de opciones configurables. Se asocia a varios productos (sección 3 del modelo de dominio).</summary>
public sealed class OptionGroup
{
    public long Id { get; private set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public void SetImportedId(long id) => Id = id;
}

/// <summary>Un producto ofrece las opciones de los grupos que tiene asociados.</summary>
public sealed class ProductOptionGroup
{
    private ProductOptionGroup()
    {
    }

    public ProductOptionGroup(long productId, long optionGroupId)
    {
        ProductId = productId;
        OptionGroupId = optionGroupId;
    }

    public long ProductId { get; private set; }

    public long OptionGroupId { get; private set; }
}

/// <summary>
/// Opción configurable, por ejemplo RAM o jugadores. La clave de módulo dice dónde va el valor elegido en los
/// parámetros del módulo ("memory" o "variables.SERVER_SLOTS"). Sin clave, la opción solo cobra.
/// </summary>
public sealed partial class Option
{
    /// <summary>Una clave de módulo va con letras, números y guiones bajos, y puntos para anidar.</summary>
    public static bool IsValidModuleKey(string key) => ModuleKeyPattern().IsMatch(key);

    public long Id { get; private set; }

    public long GroupId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? ModuleKey { get; set; }

    public OptionKind Kind { get; set; }

    /// <summary>Mínimo de una opción de cantidad.</summary>
    public int MinQuantity { get; set; }

    /// <summary>Máximo de una opción de cantidad. Cero es sin tope.</summary>
    public int MaxQuantity { get; set; }

    public int SortOrder { get; set; }

    /// <summary>No se ofrece al cliente. Toma su valor por defecto salvo que el staff o la API elijan otro.</summary>
    public bool Hidden { get; set; }

    public bool SingleValue => Kind is OptionKind.YesNo or OptionKind.Quantity;

    public void SetImportedId(long id) => Id = id;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex ModuleKeyPattern();
}

/// <summary>Valor de una opción. El valor de módulo es lo que recibe el módulo; sin él, recibe el nombre.</summary>
public sealed class OptionValue
{
    public long Id { get; private set; }

    public long OptionId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? ModuleValue { get; set; }

    public int SortOrder { get; set; }

    public bool Hidden { get; set; }

    public void SetImportedId(long id) => Id = id;
}

/// <summary>
/// Precio de un valor en una moneda y un ciclo, más su cargo de alta. Un valor con precios no se ofrece en los ciclos y
/// monedas donde no tiene. Un valor sin ningún precio no cobra, como una versión que se elige sin costo. En el ciclo
/// gratis las opciones no cobran.
/// </summary>
public sealed class OptionPrice
{
    private OptionPrice()
    {
    }

    public OptionPrice(long valueId, Currency currency, BillingCycle cycle, decimal amount, decimal setupFee)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ValueId = valueId;
        Currency = currency;
        CycleUnit = cycle.Unit;
        CycleCount = cycle.Count;
        Change(amount, setupFee);
    }

    public long Id { get; private set; }

    public long ValueId { get; private set; }

    public Currency Currency { get; private set; }

    public CycleUnit CycleUnit { get; private set; }

    public int CycleCount { get; private set; }

    public decimal Amount { get; private set; }

    public decimal SetupFee { get; private set; }

    public BillingCycle Cycle => new(CycleUnit, CycleCount);

    public void Change(decimal amount, decimal setupFee)
    {
        if (amount < 0m || setupFee < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Un precio no puede ser negativo.");
        }

        Amount = amount;
        SetupFee = setupFee;
    }
}

/// <summary>Lo que se pide de una opción: el valor de una lista o la cantidad de una opción de cantidad o de sí o no.</summary>
public sealed record OptionChoice(long OptionId, long? ValueId = null, int? Quantity = null);

/// <summary>Una opción resuelta: el valor, la cantidad y lo que cobra por período y de alta, por unidad del servicio.</summary>
public sealed record ChosenOption(Option Option, OptionValue Value, int Quantity, decimal Amount, decimal SetupFee)
{
    public OptionChoice Choice => new(Option.Id, Value.Id, Option.SingleValue ? Quantity : null);
}

/// <summary>
/// Las opciones que ofrece un producto, con sus valores y precios. Valida lo pedido, completa lo que falta con el valor
/// por defecto y calcula el precio. Los errores son <see cref="ArgumentException"/> con un texto para el que pidió.
/// </summary>
public sealed class OptionCatalog
{
    private readonly List<Option> options;
    private readonly ILookup<long, OptionValue> values;
    private readonly Dictionary<(long Value, Currency Currency, CycleUnit Unit, int Count), OptionPrice> prices;
    private readonly HashSet<long> priced;

    public OptionCatalog(IEnumerable<Option> options, IEnumerable<OptionValue> values, IEnumerable<OptionPrice> prices)
    {
        this.options = [.. options.OrderBy(o => o.SortOrder).ThenBy(o => o.Id)];
        this.values = values.OrderBy(v => v.SortOrder).ThenBy(v => v.Id).ToLookup(v => v.OptionId);
        this.prices = prices.ToDictionary(p => (p.ValueId, p.Currency, p.CycleUnit, p.CycleCount));
        priced = [.. this.prices.Keys.Select(k => k.Value)];
    }

    public IReadOnlyList<Option> Options => options;

    public IEnumerable<OptionValue> ValuesOf(long optionId) => values[optionId];

    public OptionPrice? PriceOf(long valueId, Currency currency, BillingCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        return prices.GetValueOrDefault((valueId, currency, cycle.Unit, cycle.Count));
    }

    /// <summary>
    /// Resuelve todas las opciones del producto. Lo que no se pide toma su defecto: el primer valor visible de una lista,
    /// el mínimo de una cantidad y "no" en sí o no. Un valor oculto se acepta solo si se pide explícitamente.
    /// </summary>
    public IReadOnlyList<ChosenOption> Resolve(IReadOnlyList<OptionChoice>? choices, Currency currency, BillingCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        choices ??= [];
        if (choices.FirstOrDefault(c => options.All(o => o.Id != c.OptionId)) is { } foreign)
        {
            throw new ArgumentException($"La opción {foreign.OptionId} no es de este producto.");
        }

        if (choices.GroupBy(c => c.OptionId).FirstOrDefault(g => g.Count() > 1) is { } repeated)
        {
            throw new ArgumentException($"La opción {repeated.Key} viene más de una vez.");
        }

        var result = new List<ChosenOption>();
        foreach (var option in options)
        {
            var choice = choices.FirstOrDefault(c => c.OptionId == option.Id);
            var candidates = values[option.Id].ToList();
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException($"La opción {option.Name} no tiene valores cargados.");
            }

            OptionValue value;
            int quantity;
            if (option.SingleValue)
            {
                value = candidates[0];
                quantity = choice?.Quantity ?? (option.Kind == OptionKind.Quantity ? option.MinQuantity : 0);
                var max = option.Kind == OptionKind.YesNo ? 1 : option.MaxQuantity;
                if (quantity < (option.Kind == OptionKind.YesNo ? 0 : option.MinQuantity) || (max > 0 && quantity > max) || quantity < 0)
                {
                    throw new ArgumentException(option.Kind == OptionKind.YesNo
                        ? $"La opción {option.Name} va con 0 o 1."
                        : max > 0
                            ? $"La cantidad de {option.Name} va de {option.MinQuantity} a {max}."
                            : $"La cantidad de {option.Name} va desde {option.MinQuantity}.");
                }
            }
            else
            {
                quantity = 1;
                value = choice?.ValueId is { } valueId
                    ? candidates.SingleOrDefault(v => v.Id == valueId) ?? throw new ArgumentException($"El valor {valueId} no es de la opción {option.Name}.")
                    : candidates.FirstOrDefault(v => !v.Hidden) ?? candidates[0];
            }

            var (amount, setup) = Cost(value, quantity, currency, cycle)
                ?? throw new ArgumentException($"{option.Name}: el valor {value.Name} no se ofrece en {currency} con el ciclo {cycle.Code}.");
            result.Add(new ChosenOption(option, value, option.SingleValue ? quantity : 1, amount, setup));
        }

        return result;
    }

    /// <summary>
    /// Lo menos que cobran las opciones por unidad y período en la moneda y el ciclo, con el cargo de alta de esa elección:
    /// el valor más barato de cada lista que ve el cliente, el mínimo de una cantidad y "no" en un sí o no. Una opción
    /// oculta cobra su defecto. Es el "desde" de la tienda. Nulo si alguna opción no tiene un valor que se ofrezca ahí.
    /// </summary>
    public (decimal Amount, decimal SetupFee)? Cheapest(Currency currency, BillingCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        decimal amount = 0m, setup = 0m;
        foreach (var option in options)
        {
            var candidates = values[option.Id].ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            var fallback = candidates.FirstOrDefault(v => !v.Hidden) ?? candidates[0];
            IEnumerable<(OptionValue Value, int Quantity)> choices = option.SingleValue
                ? [(candidates[0], option.Kind == OptionKind.Quantity ? option.MinQuantity : 0)]
                : option.Hidden ? [(fallback, 1)] : candidates.Where(v => !v.Hidden).DefaultIfEmpty(fallback).Select(v => (v, 1));
            var costs = choices.Select(c => Cost(c.Value, c.Quantity, currency, cycle)).OfType<(decimal Amount, decimal SetupFee)>().ToList();
            if (costs.Count == 0)
            {
                return null;
            }

            var best = costs.MinBy(c => c.Amount);
            amount += best.Amount;
            setup += best.SetupFee;
        }

        return (amount, setup);
    }

    /// <summary>
    /// Lo pedido a mano que el cliente no ve: una opción oculta, o un valor oculto de una lista que no sea uno de
    /// <paramref name="kept"/> (lo que el servicio ya tiene). Nulo si no pidió nada oculto.
    /// </summary>
    public static ChosenOption? HiddenPick(IEnumerable<ChosenOption> chosen, IReadOnlyList<OptionChoice>? asked, IEnumerable<long>? kept = null)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        var keep = kept?.ToHashSet() ?? [];
        return chosen.FirstOrDefault(c => asked?.Any(a => a.OptionId == c.Option.Id) == true
            && (c.Option.Hidden || (!c.Option.SingleValue && c.Value.Hidden && !keep.Contains(c.Value.Id))));
    }

    /// <summary>
    /// Lo que cobran por unidad y período las elecciones que el producto todavía ofrece, a los precios de hoy. Lo que ya
    /// no es del producto o no tiene precio en esa moneda y ese ciclo no cuenta.
    /// </summary>
    public decimal Cost(IEnumerable<OptionChoice> choices, Currency currency, BillingCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(cycle);
        return choices.Sum(c => options.FirstOrDefault(o => o.Id == c.OptionId) is { } option
            && (option.SingleValue ? values[option.Id].FirstOrDefault() : values[option.Id].FirstOrDefault(v => v.Id == c.ValueId)) is { } value
            && Cost(value, option.SingleValue ? c.Quantity ?? 0 : 1, currency, cycle) is { } cost
                ? cost.Amount
                : 0m);
    }

    /// <summary>Lo que cobra un valor por período y de alta, o nulo si tiene precios pero no en esa moneda y ese ciclo.</summary>
    private (decimal Amount, decimal SetupFee)? Cost(OptionValue value, int quantity, Currency currency, BillingCycle cycle)
    {
        if (cycle.Unit == CycleUnit.Free || quantity <= 0 || !priced.Contains(value.Id))
        {
            return (0m, 0m);
        }

        return PriceOf(value.Id, currency, cycle) is { } price ? (price.Amount * quantity, price.SetupFee) : null;
    }

    /// <summary>
    /// Los parámetros del módulo de un servicio: los del producto con los valores elegidos encima. Cada opción con clave
    /// va en su clave, con puntos para anidar. Una lista manda el valor de módulo (un entero como número), una cantidad
    /// manda el número y un sí o no manda verdadero o falso.
    /// </summary>
    public static string ModuleParams(string productParams, IEnumerable<ChosenOption> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        var root = JsonNode.Parse(string.IsNullOrWhiteSpace(productParams) ? "{}" : productParams) as JsonObject ?? [];
        foreach (var c in chosen.Where(c => !string.IsNullOrWhiteSpace(c.Option.ModuleKey)))
        {
            JsonNode node = c.Option.Kind switch
            {
                OptionKind.Quantity => JsonValue.Create(c.Quantity),
                OptionKind.YesNo => JsonValue.Create(c.Quantity > 0),
                _ => Scalar(string.IsNullOrWhiteSpace(c.Value.ModuleValue) ? c.Value.Name : c.Value.ModuleValue!),
            };
            var path = c.Option.ModuleKey!.Trim().Split('.');
            var parent = root;
            foreach (var segment in path[..^1])
            {
                if (parent[segment] is not JsonObject next)
                {
                    next = [];
                    parent[segment] = next;
                }

                parent = next;
            }

            parent[path[^1]] = node;
        }

        return root.ToJsonString();
    }

    /// <summary>Un entero sin ceros a la izquierda va como número. Lo demás, como "1.20", queda como texto.</summary>
    private static JsonNode Scalar(string text) =>
        text.Length is > 0 and < 19 && (text == "0" || (text[0] is >= '1' and <= '9' && text.All(char.IsAsciiDigit)))
            ? JsonValue.Create(long.Parse(text, CultureInfo.InvariantCulture))
            : JsonValue.Create(text);
}
