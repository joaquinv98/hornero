using System.Globalization;
using System.Text;
using BillingOss.App.Reports;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web;

/// <summary>
/// Cómo se ven y se exportan las celdas de un reporte (docs/reportes). En pantalla van con la cultura del pedido. El
/// CSV y los valores para máquinas (data value y time datetime) van con formato invariable, así una planilla los lee
/// igual en cualquier idioma.
/// </summary>
public static class ReportFormat
{
    /// <summary>Filas por tabla en pantalla. El CSV lleva todas.</summary>
    public const int ScreenRows = 1000;

    /// <summary>Lo que una planilla tomaría como fórmula al principio de un texto.</summary>
    private static readonly char[] FormulaStart = ['=', '+', '-', '@', '\t', '\r'];

    public static bool IsNumeric(ReportColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return column.Kind is CellKind.Count or CellKind.Money or CellKind.Rate or CellKind.Percent or CellKind.ExchangeRate or CellKind.Hours or CellKind.Decimal;
    }

    /// <summary>El encabezado: "Neto" o, en una columna por alícuota, "Neto 21 %".</summary>
    public static string Header(ReportColumn column, IStringLocalizer texts)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(texts);
        return column.Rate is { } rate ? texts["ReportCol." + column.Id, Percent(rate)] : texts["ReportCol." + column.Id];
    }

    /// <summary>
    /// La celda en pantalla: "12.100,00", "21 %", "octubre 2026", "3,5 h" o el texto traducido de una clave. Vacía, el
    /// texto que dice qué significa en esa columna, si lo tiene.
    /// </summary>
    public static string Text(object? value, ReportColumn column, IStringLocalizer texts)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(texts);
        var culture = CultureInfo.CurrentCulture;
        return value is null ? Empty(column, texts) : column.Kind switch
        {
            CellKind.Key => texts[(string)value],
            CellKind.Month => ((DateOnly)value).ToString("MMMM yyyy", culture),
            CellKind.Date => ((DateOnly)value).ToString("d", culture),
            CellKind.Count => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString("N0", culture),
            CellKind.Money => ((decimal)value).ToString("N2", culture),
            CellKind.Rate => Percent((decimal)value),
            CellKind.Percent => ((decimal)value).ToString("0.##", culture) + " %",
            CellKind.ExchangeRate => ((decimal)value).ToString("#,##0.######", culture),
            CellKind.Hours => ((decimal)value).ToString("#,##0.#", culture) + " h",
            CellKind.Decimal => ((decimal)value).ToString("#,##0.#", culture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    /// <summary>
    /// El valor para máquinas: importes con punto decimal, alícuotas como fracción, fechas ISO y meses como 2026-10, que
    /// es lo que aceptan data value y time datetime. Las claves van traducidas.
    /// </summary>
    public static string Machine(object? value, ReportColumn column, IStringLocalizer texts)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(texts);
        var invariant = CultureInfo.InvariantCulture;
        return value is null ? string.Empty : column.Kind switch
        {
            CellKind.Key => texts[(string)value],
            CellKind.Month => ((DateOnly)value).ToString("yyyy-MM", invariant),
            CellKind.Date => ((DateOnly)value).ToString("yyyy-MM-dd", invariant),
            CellKind.Money => ((decimal)value).ToString("0.00", invariant),
            CellKind.Rate or CellKind.Percent or CellKind.ExchangeRate or CellKind.Hours or CellKind.Decimal => ((decimal)value).ToString("0.######", invariant),
            _ => Convert.ToString(value, invariant) ?? string.Empty,
        };
    }

    /// <summary>
    /// La tabla en CSV (RFC 4180): UTF-8 con BOM para que las planillas tomen los acentos, coma como separador, CRLF al
    /// final de cada fila, encabezados en el idioma del pedido y valores invariables. Las alícuotas y los porcentajes
    /// van en porcentaje, con "(%)" en el encabezado, y las horas con "(h)". Un texto que empieza como una fórmula lleva un apóstrofo adelante,
    /// para que la planilla no la ejecute: el nombre de una cuenta lo escribe el cliente. Sin la fila de totales, así
    /// la planilla suma sin contar dos veces.
    /// </summary>
    public static byte[] Csv(ReportTable table, IStringLocalizer texts)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(texts);
        var csv = new StringBuilder();
        Line(csv, table.Columns.Select(c => c.Kind switch
        {
            CellKind.Rate or CellKind.Percent => Header(c, texts) + " (%)",
            CellKind.Hours => Header(c, texts) + " (h)",
            _ => Header(c, texts),
        }));
        foreach (var row in table.Rows)
        {
            Line(csv, table.Columns.Select((c, i) => Cell(row[i], c, texts)));
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    private static string Cell(object? value, ReportColumn column, IStringLocalizer texts) =>
        value is null ? Empty(column, texts) : column.Kind switch
        {
            CellKind.Rate => ((decimal)value * 100m).ToString("0.###", CultureInfo.InvariantCulture),
            CellKind.Text or CellKind.Key when Machine(value, column, texts) is { Length: > 0 } text && FormulaStart.Contains(text[0]) => "'" + text,
            _ => Machine(value, column, texts),
        };

    private static void Line(StringBuilder csv, IEnumerable<string> cells)
    {
        csv.AppendJoin(',', cells.Select(c => c.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + c.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : c));
        csv.Append("\r\n");
    }

    private static string Empty(ReportColumn column, IStringLocalizer texts) => column.Empty is { } key ? texts[key] : string.Empty;

    /// <summary>Una alícuota como porcentaje: 0,21 es "21 %" y 0,105 es "10,5 %".</summary>
    private static string Percent(decimal rate) => (rate * 100m).ToString("0.###", CultureInfo.CurrentCulture) + " %";
}
