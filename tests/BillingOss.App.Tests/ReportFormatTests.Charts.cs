using System.Globalization;
using System.Text;
using BillingOss.App.Reports;
using BillingOss.Web;

namespace BillingOss.App.Tests;

/// <summary>
/// Los gráficos de los reportes, armados con sus tablas: uno por moneda, todos los meses del período, las barras de
/// mayor a menor con tope, la escala redonda y las columnas con su dibujo. Más las horas y las celdas vacías con texto.
/// </summary>
public sealed partial class ReportFormatTests
{
    private static readonly DateOnly Oct = new(2026, 10, 1);

    private static ReportTable Revenue(params object?[][] rows) =>
        new("Revenue", [new("Month", CellKind.Month), new("Currency", CellKind.Text), new("Documents", CellKind.Count), new("Total", CellKind.Money)], rows);

    [TestMethod]
    public void Graficos_UnoPorMonedaConTodosLosMesesDelPeriodo()
    {
        var report = new Report([Revenue([Oct, "ARS", 1, 100m], [Oct.AddMonths(2), "ARS", 1, 50m], [Oct.AddMonths(1), "USD", 1, 10m])], [])
        {
            Charts = [new("Revenue", "Revenue", "Month", ["Total"]), new("Documents", "Revenue", "Month", ["Documents"])],
        };

        var charts = ReportCharts.Build(report, new MonthRange(Oct, Oct.AddMonths(2)));

        Assert.HasCount(3, charts, "los importes, uno por moneda, y las cantidades juntas");
        Assert.AreEqual("ARS", charts[0].Currency);
        Assert.IsTrue(charts[0].OverTime);
        CollectionAssert.AreEqual(new[] { new ChartBar(Oct, 100m), new ChartBar(Oct.AddMonths(1), 0m), new ChartBar(Oct.AddMonths(2), 50m) }, charts[0].Bars.ToArray());
        Assert.AreEqual("USD", charts[1].Currency);
        CollectionAssert.AreEqual(new[] { 0m, 10m, 0m }, charts[1].Bars.Select(b => b.Value).ToArray());
        Assert.IsNull(charts[2].Currency);
        CollectionAssert.AreEqual(new[] { 1m, 1m, 1m }, charts[2].Bars.Select(b => b.Value).ToArray());
    }

    [TestMethod]
    public void Graficos_SinUnaSolaBarraNiTodoEnCero()
    {
        var report = new Report([Revenue([Oct, "ARS", 0, 0m])], []) { Charts = [new("Revenue", "Revenue", "Month", ["Total"])] };

        Assert.IsEmpty(ReportCharts.Build(report, new MonthRange(Oct, Oct)), "un mes solo es una barra: lo dice la tabla");
        Assert.IsEmpty(ReportCharts.Build(report, new MonthRange(Oct, Oct.AddMonths(5))), "todo en cero");
        Assert.IsEmpty(ReportCharts.Build(report with { Tables = [Revenue()] }, null), "sin filas");
    }

    [TestMethod]
    public void Graficos_BarrasDeMayorAMenorConTopeYSinCategoriaUnaPorColumna()
    {
        var products = new ReportTable(
            "ServicesByProduct",
            [new("Product", CellKind.Text), new("Currency", CellKind.Text), new("Active", CellKind.Count)],
            [.. Enumerable.Range(1, 15).Select(i => new object?[] { $"Producto {i}", i % 2 == 0 ? "ARS" : "USD", i })]);
        var aging = new ReportTable(
            "AgingSummary",
            [new("Currency", CellKind.Text), new("Current", CellKind.Money), new("Late30", CellKind.Money), new("Total", CellKind.Money)],
            [["ARS", 100m, 20m, 120m], ["USD", 0m, 5m, 5m]]);
        var report = new Report([products, aging], [])
        {
            Charts = [new("ServicesByProduct", "ServicesByProduct", "Product", ["Active"]), new("Aging", "AgingSummary", null, ["Current", "Late30"])],
        };

        var charts = ReportCharts.Build(report, null);

        var bars = charts[0];
        Assert.IsFalse(bars.OverTime);
        Assert.IsNull(bars.Currency, "las cantidades no se separan por moneda");
        Assert.HasCount(ReportCharts.MaxBars, bars.Bars);
        Assert.AreEqual(3, bars.Hidden);
        Assert.AreEqual(new ChartBar("Producto 15", 15m), bars.Bars[0]);
        Assert.AreEqual(new ChartBar("Producto 4", 4m), bars.Bars[^1]);

        Assert.HasCount(3, charts, "la deuda, una por moneda");
        CollectionAssert.AreEqual(new[] { new ChartBar("ReportCol.Current", 100m), new ChartBar("ReportCol.Late30", 20m) }, charts[1].Bars.ToArray());
        Assert.AreEqual("USD", charts[2].Currency);

        using var _ = new Culture("es-AR");
        var rows = ReportCharts.Rows(charts[1], L);
        Assert.AreEqual(new ChartRow("Sin vencer", "100,00", "100.00", 100), rows[0]);
        Assert.AreEqual(20, rows[1].Percent);
    }

    [TestMethod]
    public void Escala_RedondaYCorta()
    {
        Assert.AreEqual((0d, 30000d, 10000d), ReportCharts.Scale([24200m, 1000m]));
        Assert.AreEqual((-5000d, 15000d, 5000d), ReportCharts.Scale([-1210m, 12100m]), "con negativos, el cero queda adentro");
        Assert.AreEqual((0d, 1d, 0.25d), ReportCharts.Scale([0m]));
        Assert.AreEqual((0d, 8d, 2d), ReportCharts.Scale([8m]), "un máximo redondo no suma un paso");

        var ar = CultureInfo.GetCultureInfo("es-AR");
        Assert.AreEqual("12,5 k", ReportCharts.Compact(12500, ar));
        Assert.AreEqual("1.2 M", ReportCharts.Compact(1200000, CultureInfo.GetCultureInfo("en")));
        Assert.AreEqual("950", ReportCharts.Compact(950, ar));
        Assert.AreEqual("-2 k", ReportCharts.Compact(-2000, ar));
    }

    [TestMethod]
    public void Columnas_ExtremoRedondeadoBaseRectaYMesesPorTrimestre()
    {
        using var _ = new Culture("es-AR");
        var bars = Enumerable.Range(0, 15).Select(i => new ChartBar(Oct.AddMonths(i), i == 1 ? -50m : i == 2 ? 0m : 100m)).ToList();
        var chart = new ChartView("Revenue", "ARS", new("Month", CellKind.Month), new("Total", CellKind.Money), bars, 0);

        var layout = ReportCharts.Columns(chart, L);

        Assert.HasCount(15, layout.Columns);
        Assert.AreEqual(layout.Left + (15 * layout.Slot) + ReportCharts.PadRight, layout.Width);
        CollectionAssert.AreEqual(new[] { "-50", "0", "50", "100" }, layout.Ticks.Select(t => t.Label).ToArray());

        // Con más de doce meses, el nombre solo en enero, abril, julio y octubre, y el año cuando cambia.
        CollectionAssert.AreEqual(
            new[] { "oct", null, null, "ene", null, null, "abr", null, null, "jul", null, null, "oct", null, null },
            layout.Columns.Select(c => c.Month).ToArray());
        CollectionAssert.AreEqual(new[] { "2026", "2027" }, layout.Columns.Select(c => c.Year).OfType<string>().ToArray());
        Assert.AreEqual("octubre 2026: 100,00", layout.Columns[0].Title);

        // La positiva sube desde el cero y la negativa baja, las dos con el extremo de los datos redondeado.
        var up = layout.Columns[0].Path;
        var zero = layout.Zero.ToString("0.#", CultureInfo.InvariantCulture);
        StringAssert.StartsWith(up, "M");
        StringAssert.Contains(up, $" {zero}V");
        StringAssert.EndsWith(up, $"V{zero}Z");
        StringAssert.Contains(up, "Q");
        var down = layout.Columns[1].Path;
        var bottom = double.Parse(down.Split('Q')[1].Split(' ')[1], CultureInfo.InvariantCulture);
        Assert.IsGreaterThan(layout.Zero, bottom);
        Assert.AreEqual(string.Empty, layout.Columns[2].Path, "el cero no se dibuja");
    }

    [TestMethod]
    public void Columnas_PorDia_TodosLosDiasDelPeriodoYElMesCuandoCambia()
    {
        using var _ = new Culture("es-AR");
        var table = new ReportTable(
            "DailyAmounts",
            [new("Date", CellKind.Date), new("Currency", CellKind.Text), new("Collected", CellKind.Money)],
            [[new DateOnly(2026, 10, 3), "ARS", 100m], [new DateOnly(2026, 11, 2), "ARS", 50m]]);
        var report = new Report([table], []) { Charts = [new("DailyCollected", "DailyAmounts", "Date", ["Collected"])] };

        var chart = ReportCharts.Build(report, new MonthRange(Oct, Oct.AddMonths(1))).Single();
        var layout = ReportCharts.Columns(chart, L);

        Assert.IsTrue(chart.OverTime);
        Assert.HasCount(61, chart.Bars, "todos los días de octubre y noviembre, con cero en los que no tienen filas");
        Assert.AreEqual(100m, chart.Bars[2].Value);

        // Con dos meses, un día por semana y el mes debajo cuando cambia.
        CollectionAssert.AreEqual(new[] { "1", "8", "15", "22", "29", "1", "8", "15", "22", "29" }, layout.Columns.Select(c => c.Month).OfType<string>().ToArray());
        CollectionAssert.AreEqual(new[] { "oct", "nov" }, layout.Columns.Select(c => c.Year).OfType<string>().ToArray());
        Assert.AreEqual("3/10/2026: 100,00", layout.Columns[2].Title);
    }

    [TestMethod]
    public void Horas_YCeldaVaciaConTexto_EnPantallaYEnCsv()
    {
        using var _ = new Culture("es-AR");
        var table = new ReportTable(
            "TicketsByDepartment",
            [new("Server", CellKind.Text, Empty: "ReportCol.NoServer"), new("FirstResponseMedian", CellKind.Hours)],
            [["Cluster", 3.5m], [null, 26m]]);

        Assert.AreEqual("3,5 h", ReportFormat.Text(3.5m, table.Columns[1], L));
        Assert.AreEqual("26 h", ReportFormat.Text(26m, table.Columns[1], L));
        Assert.AreEqual("Sin servidor", ReportFormat.Text(null, table.Columns[0], L));
        Assert.AreEqual(string.Empty, ReportFormat.Text(null, table.Columns[1], L));
        Assert.IsTrue(ReportFormat.IsNumeric(table.Columns[1]));

        var lines = Encoding.UTF8.GetString(ReportFormat.Csv(table, L)[3..]).Split("\r\n");
        Assert.AreEqual("Servidor,\"Primera respuesta, mediana (h)\"", lines[0]);
        Assert.AreEqual("Cluster,3.5", lines[1]);
        Assert.AreEqual("Sin servidor,26", lines[2]);
    }
}
