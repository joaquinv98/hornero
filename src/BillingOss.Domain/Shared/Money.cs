using System.Globalization;

namespace BillingOss.Domain.Shared;

/// <summary>Moneda ISO 4217: tres letras mayúsculas.</summary>
public readonly record struct Currency
{
    public Currency(string code)
    {
        if (code is not { Length: 3 } || !code.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException($"Moneda inválida: '{code}'.", nameof(code));
        }

        Code = code;
    }

    public string Code { get; }

    public static Currency Ars { get; } = new("ARS");

    public static Currency Usd { get; } = new("USD");

    public override string ToString() => Code;
}

/// <summary>Importe con su moneda. Operar monedas distintas es un error, nunca una conversión implícita.</summary>
public readonly record struct Money(decimal Amount, Currency Currency)
{
    public bool IsZero => Amount == 0m;

    public bool IsNegative => Amount < 0m;

    public static Money Zero(Currency currency) => new(0m, currency);

    public Money Round() => this with { Amount = Rounding.Commercial(Amount) };

    public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount, SameCurrency(a, b));

    public static Money operator -(Money a, Money b) => new(a.Amount - b.Amount, SameCurrency(a, b));

    public static Money operator -(Money a) => a with { Amount = -a.Amount };

    public static Money operator *(Money a, decimal factor) => a with { Amount = a.Amount * factor };

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Amount:0.00} {Currency}");

    private static Currency SameCurrency(Money a, Money b) =>
        a.Currency == b.Currency
            ? a.Currency
            : throw new InvalidOperationException($"No se pueden operar {a.Currency} y {b.Currency} juntos.");
}

public static class Rounding
{
    /// <summary>Redondeo comercial: la mitad hacia arriba (D-04).</summary>
    public static decimal Commercial(decimal value, int decimals = 2) =>
        Math.Round(value, decimals, MidpointRounding.AwayFromZero);

    public static bool HasAtMostDecimals(decimal value, int decimals = 2) =>
        Math.Round(value, decimals) == value;

    /// <summary>
    /// Los importes de pagos, imputaciones y saldo se guardan con dos decimales. Uno con más se redondearía al
    /// guardar y movería centavos que nadie pagó, así que no entra.
    /// </summary>
    public static void EnsureCents(decimal value, string name)
    {
        if (!HasAtMostDecimals(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "Los importes van con dos decimales como máximo.");
        }
    }
}
