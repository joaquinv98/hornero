using System.Globalization;
using BillingOss.App.Reports;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web;

/// <summary>Una barra: la etiqueta, con el tipo de la columna de donde sale, y el valor.</summary>
public sealed record ChartBar(object? Label, decimal Value);

/// <summary>
/// Un gráfico listo para dibujar (docs/reportes). Si las etiquetas son meses o días van columnas en el orden del tiempo,
/// y si no, barras horizontales. <see cref="Hidden"/> cuenta las barras que no entraron: están en la tabla.
/// </summary>
public sealed record ChartView(string Id, string? Currency, ReportColumn Label, ReportColumn Value, IReadOnlyList<ChartBar> Bars, int Hidden)
{
    public bool OverTime => Label.Kind is CellKind.Month or CellKind.Date;
}

/// <summary>Una línea de la grilla con su valor.</summary>
public sealed record ChartTick(double Y, string Label);

/// <summary>
/// Una columna: su dibujo con el extremo de los datos redondeado y la base recta, la franja que responde al puntero, el
/// texto del cartel y el mes abajo, con el año en la segunda línea cuando cambia. Por día, el día y el mes cuando cambia.
/// </summary>
public sealed record ChartColumn(string Path, double SlotX, string Title, string? Month, string? Year);

/// <summary>Las medidas de un gráfico de columnas, en píxeles del SVG.</summary>
public sealed record ColumnLayout(double Width, double Height, double Left, double Top, double Plot, double Slot, double Zero, IReadOnlyList<ChartTick> Ticks, IReadOnlyList<ChartColumn> Columns)
{
    public double Right => Width - ReportCharts.PadRight;
}

/// <summary>Una barra horizontal: el largo en porcentaje del ancho, la etiqueta y el valor en pantalla y para máquinas.</summary>
public sealed record ChartRow(string Label, string Value, string Machine, double Percent);

/// <summary>
/// Los gráficos de un reporte, armados con sus tablas: barras finas de un solo color, una grilla de un pelo y los valores
/// en el texto de siempre. Sin JavaScript ni estilos en línea, que el CSP no deja: el SVG lleva solo atributos.
/// </summary>
public static class ReportCharts
{
    /// <summary>Barras horizontales como máximo: las de mayor valor. El resto queda en la tabla.</summary>
    public const int MaxBars = 12;

    public const double PadRight = 8;

    private const double AxisWidth = 56;

    private const double PadTop = 10;

    private const double PlotHeight = 150;

    private const double MonthBand = 34;

    private const double Radius = 4;

    /// <summary>
    /// Los gráficos del reporte, uno por moneda cuando son importes. Con el período, los gráficos por mes o por día
    /// tienen todos sus meses o sus días, con cero en los que no tienen filas, así el tiempo no se achica. Los que
    /// tendrían una barra sola o todas en cero no van: la tabla lo dice mejor.
    /// </summary>
    public static IReadOnlyList<ChartView> Build(Report report, MonthRange? range)
    {
        ArgumentNullException.ThrowIfNull(report);
        var charts = new List<ChartView>();
        foreach (var chart in report.Charts)
        {
            var table = report.Table(chart.Table);
            var columns = table.Columns.ToList();
            var values = chart.Values.Select(v => columns.FindIndex(c => c.Id == v)).ToList();
            var value = columns[values[0]];
            var currency = value.Kind == CellKind.Money ? columns.FindIndex(c => c.Id == "Currency") : -1;
            var category = chart.Category is null ? -1 : columns.FindIndex(c => c.Id == chart.Category);
            foreach (var group in table.Rows.GroupBy(r => currency < 0 ? null : (string?)r[currency]).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                List<ChartBar> bars;
                var hidden = 0;
                if (category < 0)
                {
                    bars = [.. values.Select(i => new ChartBar("ReportCol." + columns[i].Id, group.Sum(r => Number(r[i]))))];
                }
                else
                {
                    bars = [.. group.Where(r => r[values[0]] is not null).GroupBy(r => r[category]).Select(g => new ChartBar(g.Key, g.Sum(r => Number(r[values[0]]))))];
                    if (columns[category].Kind is CellKind.Month or CellKind.Date)
                    {
                        var steps = range is null ? bars.Select(b => (DateOnly)b.Label!).Order().ToList()
                            : columns[category].Kind == CellKind.Month ? Months(range) : Days(range);
                        bars = [.. steps.Select(m => bars.Find(b => Equals(b.Label, m)) ?? new ChartBar(m, 0m))];
                    }
                    else
                    {
                        hidden = Math.Max(0, bars.Count - MaxBars);
                        bars = [.. bars.OrderByDescending(b => b.Value).Take(MaxBars)];
                    }
                }

                if (bars.Count + hidden > 1 && bars.Any(b => b.Value != 0m))
                {
                    charts.Add(new(chart.Id, group.Key, category < 0 ? new ReportColumn("Label", CellKind.Key) : columns[category], value, bars, hidden));
                }
            }
        }

        return charts;
    }

    /// <summary>
    /// La escala del eje: de cero, o del mínimo si hay negativos, a un máximo redondo, en pasos de 1, 2, 2,5 o 5 por
    /// una potencia de diez, unos cuatro.
    /// </summary>
    public static (double Low, double High, double Step) Scale(IEnumerable<decimal> values)
    {
        var list = values.Select(v => (double)v).ToList();
        var min = Math.Min(0, list.DefaultIfEmpty().Min());
        var max = Math.Max(0, list.DefaultIfEmpty().Max());
        if (max - min <= 0)
        {
            max = min + 1;
        }

        var raw = (max - min) / 4;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(f => f * magnitude).First(s => s >= raw * (1 - 1e-9));
        return (Math.Floor((min / step) + 1e-9) * step, Math.Ceiling((max / step) - 1e-9) * step, step);
    }

    /// <summary>Un valor del eje, corto: 950, "12,5 k" o "1,2 M".</summary>
    public static string Compact(double value, CultureInfo culture)
    {
        var abs = Math.Abs(value);
        return abs >= 1e6 ? (value / 1e6).ToString("0.#", culture) + " M"
            : abs >= 1e3 ? (value / 1e3).ToString("0.#", culture) + " k"
            : value.ToString("0.##", culture);
    }

    /// <summary>
    /// Las medidas de las columnas. Cada mes tiene su franja, más angosta cuantos más meses hay, y la columna no pasa de
    /// 24 px. Con más de doce meses, el nombre va solo en los que abren el trimestre, el semestre o el año. Por día, hasta
    /// un mes van todos los días, hasta tres uno por semana y con más solo el primero de cada mes.
    /// </summary>
    public static ColumnLayout Columns(ChartView chart, IStringLocalizer texts)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(texts);
        var culture = CultureInfo.CurrentCulture;
        var count = chart.Bars.Count;
        double slot = count <= 12 ? 44 : count <= 24 ? 28 : count <= 48 ? 18 : 12;
        var width = Math.Min(24, slot * 0.7);
        var daily = chart.Label.Kind == CellKind.Date;
        var every = count <= 12 ? 1 : count <= 24 ? 3 : count <= 60 ? 6 : 12;
        var (low, high, step) = Scale(chart.Bars.Select(b => b.Value));
        double Y(double v) => PadTop + ((high - v) / (high - low) * PlotHeight);

        var ticks = new List<ChartTick>();
        for (var t = low; t <= high + (step / 2); t += step)
        {
            ticks.Add(new(Y(t), Compact(t, culture)));
        }

        var zero = Y(0);
        var columns = new List<ChartColumn>();
        string? lastSecond = null;
        for (var i = 0; i < count; i++)
        {
            var bar = chart.Bars[i];
            var when = (DateOnly)bar.Label!;
            var labeled = daily ? count <= 31 || when.Day == 1 || (count <= 92 && (when.Day - 1) % 7 == 0) : every == 1 || (when.Month - 1) % every == 0;

            // Abajo, el mes con el año cuando cambia, o el día con el mes cuando cambia.
            var first = daily ? when.Day.ToString(culture) : when.ToString("MMM", culture).TrimEnd('.');
            var second = daily ? when.ToString("MMM", culture).TrimEnd('.') : when.Year.ToString(CultureInfo.InvariantCulture);
            var x = AxisWidth + (i * slot) + ((slot - width) / 2);
            var end = Y((double)bar.Value);
            var r = Math.Min(Radius, Math.Min(width / 2, Math.Abs(zero - end)));
            var toward = end <= zero ? 1 : -1;
            var path = Math.Abs(zero - end) < 0.5 ? string.Empty : string.Create(
                CultureInfo.InvariantCulture,
                $"M{x:0.#} {zero:0.#}V{end + (toward * r):0.#}Q{x:0.#} {end:0.#} {x + r:0.#} {end:0.#}H{x + width - r:0.#}Q{x + width:0.#} {end:0.#} {x + width:0.#} {end + (toward * r):0.#}V{zero:0.#}Z");
            columns.Add(new(
                path,
                AxisWidth + (i * slot),
                $"{ReportFormat.Text(bar.Label, chart.Label, texts)}: {ReportFormat.Text(bar.Value, chart.Value, texts)}",
                labeled ? first : null,
                labeled && second != lastSecond ? second : null));
            if (labeled)
            {
                lastSecond = second;
            }
        }

        return new(AxisWidth + (count * slot) + PadRight, PadTop + PlotHeight + MonthBand, AxisWidth, PadTop, PlotHeight, slot, zero, ticks, columns);
    }

    /// <summary>Las barras horizontales, con el largo relativo a la mayor. Una negativa queda sin barra, con su valor.</summary>
    public static IReadOnlyList<ChartRow> Rows(ChartView chart, IStringLocalizer texts)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(texts);
        var max = chart.Bars.Max(b => b.Value);
        return [.. chart.Bars.Select(b => new ChartRow(
            ReportFormat.Text(b.Label, chart.Label, texts),
            ReportFormat.Text(b.Value, chart.Value, texts),
            ReportFormat.Machine(b.Value, chart.Value, texts),
            max <= 0m || b.Value <= 0m ? 0 : Math.Round((double)(b.Value / max) * 100, 2)))];
    }

    private static List<DateOnly> Months(MonthRange range) => [.. Enumerable.Range(0, range.Months).Select(range.From.AddMonths)];

    private static List<DateOnly> Days(MonthRange range) => [.. Enumerable.Range(0, range.End.DayNumber - range.From.DayNumber).Select(range.From.AddDays)];

    private static decimal Number(object? value) => value is null ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
}
