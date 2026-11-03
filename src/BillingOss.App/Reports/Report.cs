using System.Globalization;

namespace BillingOss.App.Reports;

/// <summary>Cómo se muestra y se exporta una celda. El valor de la fila tiene el tipo que dice cada uno.</summary>
public enum CellKind
{
    /// <summary>string, tal cual.</summary>
    Text,

    /// <summary>string con una clave de texto de la interfaz, que se traduce.</summary>
    Key,

    /// <summary>DateOnly con el primer día del mes.</summary>
    Month,

    /// <summary>DateOnly.</summary>
    Date,

    /// <summary>int o long.</summary>
    Count,

    /// <summary>decimal, con dos decimales.</summary>
    Money,

    /// <summary>decimal con la alícuota: 0,21 es 21 %.</summary>
    Rate,

    /// <summary>decimal ya en porcentaje: 12,5 es 12,5 %.</summary>
    Percent,

    /// <summary>decimal con la cotización, hasta seis decimales.</summary>
    ExchangeRate,

    /// <summary>long con el número de cuenta, que en pantalla lleva a su ficha.</summary>
    Account,

    /// <summary>decimal con horas, con un decimal: 1,5 es una hora y media.</summary>
    Hours,

    /// <summary>decimal sin unidad con un decimal, como un puntaje promedio: 8,5.</summary>
    Decimal,
}

/// <summary>
/// Una columna. El encabezado sale de la clave ReportCol.{Id}. Con <paramref name="Rate"/> el encabezado lleva esa
/// alícuota, como en "Neto 21 %". Con <paramref name="Empty"/>, una celda vacía muestra ese texto, como "Sin servidor".
/// </summary>
public sealed record ReportColumn(string Id, CellKind Kind, decimal? Rate = null, string? Empty = null);

/// <summary>
/// Una tabla del reporte. El título sale de la clave ReportTable.{Id}. Las filas tienen una celda por columna. Los
/// totales van al pie en pantalla y no en el CSV, para que el CSV se pueda sumar sin contar dos veces.
/// </summary>
public sealed record ReportTable(string Id, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<object?[]> Rows)
{
    public IReadOnlyList<object?[]> Totals { get; init; } = [];
}

/// <summary>Lo que el staff tiene que revisar, con su clave de texto (ReportNote.{Key}) y los valores que lleva.</summary>
public sealed record ReportNote(string Key, IReadOnlyList<string> Args);

/// <summary>
/// Un gráfico de barras sobre una tabla del reporte, así nunca dice otra cosa que la tabla. Con
/// <paramref name="Category"/>, una barra por cada valor de esa columna con la suma de la primera de
/// <paramref name="Values"/>. Sin categoría, una barra por cada columna de <paramref name="Values"/> con la suma de la
/// tabla. Los importes van en un gráfico por moneda. El título sale de la clave ReportChart.{Id}.
/// </summary>
public sealed record ReportChart(string Id, string Table, string? Category, IReadOnlyList<string> Values);

public sealed record Report(IReadOnlyList<ReportTable> Tables, IReadOnlyList<ReportNote> Notes)
{
    public IReadOnlyList<ReportChart> Charts { get; init; } = [];

    public ReportTable Table(string id) => Tables.SingleOrDefault(t => t.Id == id)
        ?? throw new KeyNotFoundException($"El reporte no tiene la tabla {id}.");
}

/// <summary>Meses del reporte, los dos incluidos. Cada fecha es el primer día de su mes.</summary>
public sealed record MonthRange
{
    /// <summary>Tope de meses por consulta, para que nadie pida un siglo por error.</summary>
    public const int MaxMonths = 120;

    public MonthRange(DateOnly from, DateOnly to)
    {
        From = new DateOnly(from.Year, from.Month, 1);
        To = new DateOnly(to.Year, to.Month, 1);
        if (To < From)
        {
            throw new ArgumentException("El mes de inicio es posterior al de fin.", nameof(from));
        }

        if (Months > MaxMonths)
        {
            throw new ArgumentException($"El período pasa de {MaxMonths} meses.", nameof(to));
        }
    }

    public DateOnly From { get; }

    public DateOnly To { get; }

    /// <summary>Primer día después del período.</summary>
    public DateOnly End => To.AddMonths(1);

    public int Months => ((To.Year - From.Year) * 12) + To.Month - From.Month + 1;

    /// <summary>Los últimos doce meses hasta el de <paramref name="today"/>, incluido.</summary>
    public static MonthRange LastYear(DateOnly today) => new(today.AddMonths(-11), today);

    /// <summary>Un mes escrito como "2026-10", que es lo que manda un input de tipo month.</summary>
    public static DateOnly? ParseMonth(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month) ? month : null;

    public static string Format(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
