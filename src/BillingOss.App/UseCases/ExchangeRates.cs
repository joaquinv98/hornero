using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BillingOss.App.UseCases;

/// <summary>Una conversión lista para usar: unidades de la moneda destino por una de la de origen, y la fila de la que sale.</summary>
public sealed record Conversion(decimal Factor, ExchangeRate Rate)
{
    public decimal Apply(decimal amount) => Rounding.Commercial(amount * Factor);
}

/// <summary>
/// Cotizaciones (sección 6.4 del modelo de dominio). La automática del día se usa desde el día siguiente, así todo un
/// día cobra y factura con el mismo valor, que es el cierre del día hábil anterior. La manual vale desde su día y le
/// gana a la automática de la misma fecha. Una más vieja que <see cref="ExchangeRateOptions.MaxAgeDays"/> no se usa.
/// </summary>
public sealed class ExchangeRateService(BillingDbContext db, BillingCore core, BnaRates bna, AuditLog audit, TimeProvider time, ILogger<ExchangeRateService> logger)
{
    /// <summary>Clave de entidad del trabajo que trae las cotizaciones: de a uno entre todos los nodos.</summary>
    public const string Entity = "exchange-rates";

    public Currency Local => InvoiceService.ParseCurrency(core.Options.ExchangeRates.Local);

    /// <summary>La cotización que vale hoy entre dos monedas, en el sentido que haga falta. Null si no hay una reciente.</summary>
    public async Task<Conversion?> Current(Currency from, Currency to, CancellationToken ct = default)
    {
        if (from == to)
        {
            throw new ArgumentException("No hay nada que convertir entre una moneda y ella misma.", nameof(to));
        }

        var today = core.Today();
        var oldest = today.AddDays(-core.Options.ExchangeRates.MaxAgeDays);
        var rate = await db.ExchangeRates.AsNoTracking()
            .Where(r => ((r.Currency == from && r.Quote == to) || (r.Currency == to && r.Quote == from)) && r.Date >= oldest
                && (r.Date < today || (r.Source == ExchangeRate.Manual && r.Date == today)))
            .OrderByDescending(r => r.Date).ThenByDescending(r => r.Source == ExchangeRate.Manual).ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        // Ocho decimales, los mismos que guardan el intento y la imputación: lo que se cobra es lo que se imputa.
        return rate is null ? null : new Conversion(Math.Round(rate.Factor(from, to), 8, MidpointRounding.AwayFromZero), rate);
    }

    /// <summary>Como <see cref="Current"/>, pero sin cotización vigente es un error del pedido, con un texto que explica qué falta.</summary>
    public async Task<Conversion> Require(Currency from, Currency to, CancellationToken ct = default) =>
        await Current(from, to, ct).ConfigureAwait(false)
            ?? throw new ValidationException($"No hay una cotización vigente entre {from} y {to}: cargala a mano o esperá la próxima del banco.");

    /// <summary>Las últimas cotizaciones cargadas, de la más nueva a la más vieja.</summary>
    public Task<List<ExchangeRate>> Recent(int count = 30, CancellationToken ct = default) =>
        db.ExchangeRates.AsNoTracking().OrderByDescending(r => r.Date).ThenByDescending(r => r.Id).Take(count).ToListAsync(ct);

    /// <summary>
    /// Cotización cargada por el staff, en la moneda local. Vale desde su día, le gana a la automática de esa fecha y
    /// sigue hasta que la fuente publique una más nueva. Queda en la auditoría con quién la cargó.
    /// </summary>
    public async Task<long> AddManual(string currency, decimal rate, DateOnly? date = null, CancellationToken ct = default)
    {
        var code = InvoiceService.ParseCurrency(currency);
        var today = core.Today();
        var day = date ?? today;
        if (code == Local)
        {
            throw new ValidationException($"La cotización es de otra moneda en {Local}.");
        }

        // Una más vieja que el tope no se usaría nunca (Current la descarta): se rechaza al cargarla, no después.
        var maxAge = core.Options.ExchangeRates.MaxAgeDays;
        if (day > today || day < today.AddDays(-maxAge))
        {
            throw new ValidationException(string.Create(CultureInfo.InvariantCulture, $"La fecha de la cotización va de hace {maxAge} días a hoy: una más vieja no se usa."));
        }

        if (rate <= 0m || rate >= 1_000_000_000_000m || !Rounding.HasAtMostDecimals(rate, 6))
        {
            throw new ValidationException("La cotización tiene que ser positiva y con seis decimales como máximo.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = new ExchangeRate(code, Local, rate, day, ExchangeRate.Manual, time.GetUtcNow(), Actor.Current.Id);
        db.ExchangeRates.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("exchange-rate.manual", string.Create(CultureInfo.InvariantCulture, $"exchange-rate:{row.Id}"), new { currency = code.Code, rate, date = day }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return row.Id;
    }

    /// <summary>
    /// Trae las del Banco Nación y guarda las que cambiaron. Corre una vez por hora: el último valor de cada día queda
    /// como su cierre. Devuelve cuántas filas agregó.
    /// </summary>
    public async Task<int> Refresh(CancellationToken ct = default)
    {
        var options = core.Options.ExchangeRates;
        if (Local != Currency.Ars)
        {
            throw new PermanentFailureException("El Banco Nación cotiza en pesos: con otra moneda local no sirve como fuente.");
        }

        var published = await bna.Fetch(options.BnaSeries, ct).ConfigureAwait(false);
        var added = 0;
        foreach (var code in options.Currencies)
        {
            // La configuración puede decir "usd": se compara sin distinguir mayúsculas, como las demás monedas de los ajustes.
            if (published.FirstOrDefault(p => string.Equals(p.Currency.Code, code, StringComparison.OrdinalIgnoreCase)) is not { } p)
            {
                logger.LogWarning("El Banco Nación no publica {Currency} en {Series}.", code, options.BnaSeries);
                continue;
            }

            var last = await db.ExchangeRates.Where(r => r.Source == ExchangeRate.Bna && r.Currency == p.Currency && r.Quote == Currency.Ars && r.Date == p.Date)
                .OrderByDescending(r => r.Id).Select(r => (decimal?)r.Rate).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (last != p.Sell)
            {
                db.ExchangeRates.Add(new ExchangeRate(p.Currency, Currency.Ars, p.Sell, p.Date, ExchangeRate.Bna, time.GetUtcNow()));
                added++;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return added;
    }
}

/// <summary>Una fila de la tabla de cotizaciones del banco.</summary>
public sealed record PublishedRate(Currency Currency, DateOnly Date, decimal Buy, decimal Sell);

/// <summary>
/// Cotizaciones del Banco de la Nación Argentina, leídas de su página pública: no hay una API oficial. Lee la tabla de
/// billetes o la de divisas, con su fecha y los valores de compra y venta. Si la página cambia y no se puede leer, el
/// trabajo falla a la vista y el staff carga la cotización a mano.
/// </summary>
public sealed partial class BnaRates(HttpClient http)
{
    public static Uri Page { get; } = new("https://www.bna.com.ar/Personas");

    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["dolar u.s.a"] = "USD",
        ["euro"] = "EUR",
        ["real"] = "BRL",
    };

    public async Task<IReadOnlyList<PublishedRate>> Fetch(string series, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(Page, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), series);
        }
        catch (HttpRequestException ex)
        {
            throw new RetryLaterException($"No se pudo leer la página del Banco Nación: {ex.Message}");
        }
    }

    /// <summary>La tabla de la serie ("billetes" o "divisas"): desde su pestaña hasta el cierre de la tabla.</summary>
    public static IReadOnlyList<PublishedRate> Parse(string html, string series)
    {
        ArgumentNullException.ThrowIfNull(html);
        var start = html.IndexOf($"id=\"{series}\"", StringComparison.OrdinalIgnoreCase);
        var end = start < 0 ? -1 : html.IndexOf("</table>", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            throw new PermanentFailureException($"La página del Banco Nación no tiene la tabla de {series}.");
        }

        var table = html[start..end];
        var date = DatePattern().Match(table) is { Success: true } d
            && DateOnly.TryParseExact(d.Value, ["d/M/yyyy", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day
                : throw new PermanentFailureException($"La tabla de {series} del Banco Nación no dice la fecha.");
        var rates = new List<PublishedRate>();
        foreach (Match row in RowPattern().Matches(table))
        {
            var cells = CellPattern().Matches(row.Value).Select(c => Text(c.Groups[1].Value)).ToList();
            if (cells.Count < 3 || Code(cells[0]) is not { } code || Number(cells[^2]) is not { } buy || Number(cells[^1]) is not { } sell)
            {
                continue;
            }

            rates.Add(new PublishedRate(new Currency(code), date, buy, sell));
        }

        return rates.Count > 0 ? rates : throw new PermanentFailureException($"La tabla de {series} del Banco Nación no tiene cotizaciones legibles.");
    }

    private static string Text(string cell) => WebUtility.HtmlDecode(TagPattern().Replace(cell, " ")).Trim();

    /// <summary>"Dolar U.S.A", "Dólar U.S.A." o "Real *": sin acentos, en minúsculas y por el principio.</summary>
    private static string? Code(string name)
    {
        var plain = new string([.. name.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)]).ToLowerInvariant();
        return Names.FirstOrDefault(n => plain.StartsWith(n.Key, StringComparison.Ordinal)).Value;
    }

    /// <summary>
    /// "1465,00", "1.465,5000", "1.465" o "1465.50", con seis decimales como máximo. Sin coma, un punto seguido de tres
    /// cifras separa los miles, como escribe el banco: "1.465" son mil cuatrocientos sesenta y cinco, nunca 1,465.
    /// </summary>
    private static decimal? Number(string text)
    {
        var normalized = text.Contains(',', StringComparison.Ordinal) || ThousandsPattern().IsMatch(text)
            ? text.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.')
            : text;
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value > 0m
            ? Math.Round(value, 6, MidpointRounding.AwayFromZero)
            : null;
    }

    [GeneratedRegex(@"\b\d{1,2}/\d{1,2}/\d{4}\b")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"<tr\b[^>]*>.*?</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RowPattern();

    [GeneratedRegex(@"<td\b[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CellPattern();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^\d{1,3}(\.\d{3})+$")]
    private static partial Regex ThousandsPattern();
}

/// <summary>Trae las cotizaciones del Banco Nación. Lo encola el planificador una vez por hora si está prendido.</summary>
public sealed class ExchangeRatesHandler(ExchangeRateService rates) : IJobHandler
{
    public string Kind => JobKinds.ExchangeRates;

    public async Task<JobResult> Handle(ClaimedJob job, CancellationToken ct)
    {
        await rates.Refresh(ct).ConfigureAwait(false);
        return JobResult.Done;
    }
}
