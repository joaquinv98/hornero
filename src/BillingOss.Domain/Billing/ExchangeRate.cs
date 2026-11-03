using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Billing;

/// <summary>
/// Cotización de una moneda en otra (D-04): cuánto vale una unidad de <see cref="Currency"/> en <see cref="Quote"/>, con
/// su fuente y el día que corresponde. Solo altas, como el libro de saldo: un valor nuevo del mismo día es otra fila y
/// vale la última. Así un documento o un intento que apunta a una fila sabe siempre qué cotización usó.
/// </summary>
public sealed class ExchangeRate
{
    /// <summary>Banco de la Nación Argentina, tipo vendedor.</summary>
    public const string Bna = "bna";

    /// <summary>Cargada por el staff: vale el mismo día y le gana a la automática de su fecha.</summary>
    public const string Manual = "manual";

    private ExchangeRate()
    {
    }

    public ExchangeRate(Currency currency, Currency quote, decimal rate, DateOnly date, string source, DateTimeOffset now, string? actor = null)
    {
        if (currency == quote)
        {
            throw new ArgumentException("Una cotización es entre dos monedas distintas.", nameof(quote));
        }

        if (rate <= 0m || rate >= 1_000_000_000_000m || !Rounding.HasAtMostDecimals(rate, 6))
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "La cotización tiene que ser positiva y con seis decimales como máximo.");
        }

        Currency = currency;
        Quote = quote;
        Rate = rate;
        Date = date;
        Source = source;
        Actor = actor;
        CreatedAt = now;
    }

    public long Id { get; private set; }

    /// <summary>La moneda que se cotiza, por ejemplo USD.</summary>
    public Currency Currency { get; private set; }

    /// <summary>La moneda en que se expresa, por ejemplo ARS.</summary>
    public Currency Quote { get; private set; }

    /// <summary>Unidades de <see cref="Quote"/> por una de <see cref="Currency"/>.</summary>
    public decimal Rate { get; private set; }

    /// <summary>El día que publicó la fuente. La automática se usa desde el día siguiente, la manual desde ese día.</summary>
    public DateOnly Date { get; private set; }

    public string Source { get; private set; } = string.Empty;

    /// <summary>Quién cargó una manual, como en la auditoría.</summary>
    public string? Actor { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Unidades de <paramref name="to"/> por una de <paramref name="from"/>, en el sentido que haga falta.</summary>
    public decimal Factor(Currency from, Currency to) =>
        (from, to) == (Currency, Quote) ? Rate
        : (from, to) == (Quote, Currency) ? 1m / Rate
        : throw new InvalidOperationException($"La cotización {Id} no es entre {from} y {to}.");
}
