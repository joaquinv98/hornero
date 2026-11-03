using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BillingOss.App.Reports;
using BillingOss.Web;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BillingOss.App.Tests;

/// <summary>
/// Cómo se ven y se exportan los reportes: celdas con la cultura del pedido, CSV invariable y protegido contra
/// fórmulas, y todas las claves de texto que arman los reportes en los dos idiomas. Sin base de datos: corre siempre.
/// </summary>
[TestClass]
[DoNotParallelize] // Cambia la cultura del hilo.
public sealed partial class ReportFormatTests
{
    private static readonly IStringLocalizer L = new ResourceManagerStringLocalizerFactory(
        Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }), NullLoggerFactory.Instance).Create(typeof(BillingOss.Web.Texts));

    private static readonly ReportTable Table = new(
        "SalesByAliquot",
        [
            new("Month", CellKind.Month), new("Currency", CellKind.Text), new("Treatment", CellKind.Key), new("Rate", CellKind.Rate),
            new("Net", CellKind.Money), new("AccountName", CellKind.Text), new("Documents", CellKind.Count), new("ChurnRate", CellKind.Percent),
            new("Date", CellKind.Date),
        ],
        [
            [new DateOnly(2026, 10, 1), "ARS", "TaxTreatment.Taxed", 0.105m, 12100m, "Pérez, \"el de la VPS\"", 3, 12.5m, new DateOnly(2026, 10, 28)],
            [new DateOnly(2026, 11, 1), "ARS", "TaxTreatment.Exempt", null, -1000m, "=HYPERLINK(\"https://ejemplo.invalid\")", 1, null, null],
        ]);

    [TestMethod]
    public void Pantalla_ConLaCulturaDelPedido()
    {
        using var _ = new Culture("es-AR");
        var columns = Table.Columns;
        var row = Table.Rows[0];

        Assert.AreEqual("octubre 2026", ReportFormat.Text(row[0], columns[0], L));
        Assert.AreEqual("Gravado", ReportFormat.Text(row[2], columns[2], L));
        Assert.AreEqual("10,5 %", ReportFormat.Text(row[3], columns[3], L));
        Assert.AreEqual("12.100,00", ReportFormat.Text(row[4], columns[4], L));
        Assert.AreEqual("12,5 %", ReportFormat.Text(row[7], columns[7], L));
        Assert.AreEqual("28/10/2026", ReportFormat.Text(row[8], columns[8], L));
        Assert.AreEqual("Neto 21 %", ReportFormat.Header(new("NetAt", CellKind.Money, 0.21m), L));

        // Lo de máquinas no depende de la cultura: data value y time datetime.
        Assert.AreEqual("2026-10", ReportFormat.Machine(row[0], columns[0], L));
        Assert.AreEqual("12100.00", ReportFormat.Machine(row[4], columns[4], L));
        Assert.AreEqual("0.105", ReportFormat.Machine(row[3], columns[3], L));

        using var __ = new Culture("en");
        Assert.AreEqual("October 2026", ReportFormat.Text(row[0], columns[0], L));
        Assert.AreEqual("12,100.00", ReportFormat.Text(row[4], columns[4], L));
        Assert.AreEqual("Taxed", ReportFormat.Text(row[2], columns[2], L));
    }

    [TestMethod]
    public void Csv_InvariableConEncabezadosTraducidosYSinFormulas()
    {
        using var _ = new Culture("es-AR");

        var bytes = ReportFormat.Csv(Table, L);

        CollectionAssert.AreEqual(Encoding.UTF8.GetPreamble(), bytes[..3], "BOM para que las planillas tomen los acentos");
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n");
        Assert.AreEqual("Mes,Moneda,Tratamiento,Alícuota (%),Neto,Nombre,Documentos,Tasa de bajas (%),Fecha", lines[0]);
        Assert.AreEqual("2026-10,ARS,Gravado,10.5,12100.00,\"Pérez, \"\"el de la VPS\"\"\",3,12.5,2026-10-28", lines[1]);
        Assert.AreEqual("2026-11,ARS,Exento,,-1000.00,\"'=HYPERLINK(\"\"https://ejemplo.invalid\"\")\",1,,", lines[2], "el importe negativo sigue siendo número");
        Assert.AreEqual(string.Empty, lines[3], "cada fila termina en CRLF");
        Assert.HasCount(4, lines);

        using var __ = new Culture("en");
        StringAssert.StartsWith(Encoding.UTF8.GetString(ReportFormat.Csv(Table, L)[3..]), "Month,Currency,Treatment,Rate (%),Net");
    }

    [TestMethod]
    public void Catalogo_CadaReporteConSuPermisoYReportsReadAlcanzaParaTodos()
    {
        var catalog = BillingOss.Web.Pages.Admin.Reports.ReportModel.Catalog;

        // Cada permiso de reporte lo usa un reporte, y lo usa uno solo: una entrada que lleva a una tabla pide el del suyo.
        CollectionAssert.AreEquivalent(Identity.ReportPermissions.All.ToList(), catalog.Where(e => e.Table is null).Select(e => e.Permission).ToList());
        Assert.IsTrue(catalog.Where(e => e.Table is not null).All(e => catalog.Any(r => r.Table is null && r.Slug == e.Slug && r.Permission == e.Permission)));
        Assert.IsTrue(Identity.ReportPermissions.All.All(Identity.StaffPermissions.All.Contains), "el alta del staff los acepta");
        Assert.IsTrue(Identity.ReportPermissions.All.All(p => p.StartsWith("reports.", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(new[] { Identity.ReportPermissions.Taxes, Identity.StaffPermissions.ReportsRead }, Identity.StaffPermissions.Granting(Identity.ReportPermissions.Taxes));
        CollectionAssert.AreEqual(new[] { Identity.StaffPermissions.AccountsRead }, Identity.StaffPermissions.Granting(Identity.StaffPermissions.AccountsRead));
    }

    [TestMethod]
    public void Claves_TodoLoQueArmanLosReportesExisteEnLosDosIdiomas()
    {
        var reports = Path.Combine(Root(), "src", "BillingOss.App", "Reports");
        var source = string.Concat(Directory.EnumerateFiles(reports, "*.cs").Select(File.ReadAllText));
        var keys = ColumnPattern().Matches(source).Select(m => "ReportCol." + m.Groups[1].Value)
            .Concat(TablePattern().Matches(source).Select(m => "ReportTable." + m.Groups[1].Value))
            .Concat(NotePattern().Matches(source).Select(m => "ReportNote." + m.Groups[1].Value))
            .Concat(ChartPattern().Matches(source).Select(m => "ReportChart." + m.Groups[1].Value))
            .Concat(LiteralPattern().Matches(source).Select(m => m.Groups[1].Value))
            .Concat(Enum.GetNames<Domain.Billing.TaxTreatment>().Select(n => "TaxTreatment." + n))
            .Concat(Enum.GetNames<Domain.Accounts.TaxDocumentType>().Select(n => "ReportDocType." + n))
            .Concat(Enum.GetNames<Domain.Catalog.CycleUnit>().Select(n => "ReportCycle." + n))
            .Distinct()
            .Order()
            .ToList();
        Assert.IsGreaterThan(60, keys.Count, "el patrón dejó de encontrar las claves");

        foreach (var culture in new[] { "es-AR", "en" })
        {
            using var _ = new Culture(culture);
            var missing = keys.Where(k => L[k].ResourceNotFound).ToList();
            Assert.IsEmpty(missing, $"Faltan en {culture}: {string.Join(", ", missing)}");
        }
    }

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BillingOss.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }

    [GeneratedRegex("new\\(\"([A-Za-z]+)\", CellKind\\.")]
    private static partial Regex ColumnPattern();

    [GeneratedRegex("ReportTable\\(\\s*\"([A-Za-z]+)\"")]
    private static partial Regex TablePattern();

    [GeneratedRegex("new\\(\"([A-Za-z]+)\", \\[")]
    private static partial Regex NotePattern();

    [GeneratedRegex("\"(Report[A-Za-z]*\\.[A-Z][A-Za-z]+|DocumentKind\\.[A-Z][A-Za-z]+)\"")]
    private static partial Regex LiteralPattern();

    /// <summary>Los gráficos: new("Revenue", "Revenue", "Month", [...]), con la categoría o null.</summary>
    [GeneratedRegex("new\\(\"([A-Za-z]+)\", \"[A-Za-z]+\", (?:\"[A-Za-z]+\"|null), \\[")]
    private static partial Regex ChartPattern();

    /// <summary>Cambia la cultura del hilo mientras dura.</summary>
    private sealed class Culture : IDisposable
    {
        private readonly CultureInfo culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo ui = CultureInfo.CurrentUICulture;

        public Culture(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = ui;
        }
    }
}
