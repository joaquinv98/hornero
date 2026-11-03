using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.App.Reports;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Las pantallas de reportes del admin: el permiso, las tablas, el CSV, los idiomas y el Libro IVA Digital.</summary>
public sealed partial class UiTests
{
    private static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires")).DateTime);

    private static string ThisMonth => MonthRange.Format(Today);

    private static string ThisMonthName => Today.ToString("MMMM yyyy", new System.Globalization.CultureInfo("es-AR"));

    [TestMethod]
    public async Task Reportes_SinElPermiso_NoSeVenNiSeDescargan()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("soporte@example.com", StaffPermissions.AccountsRead);
        using var c = await StaffSession(h, "soporte@example.com");

        Assert.IsFalse((await c.Html("/admin")).Contains("/admin/reportes", StringComparison.Ordinal), "el menú no lo ofrece");
        foreach (var path in (string[])["/admin/reportes", "/admin/reportes/ingresos", "/admin/reportes/ingresos?handler=csv&tabla=Revenue", "/admin/reportes/libro-iva?handler=vouchers&mes=2026-10"])
        {
            using var response = await c.Get(path);
            Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode, path);
            StringAssert.Contains(Location(response), "/estado/403", path);
        }
    }

    [TestMethod]
    public async Task Reportes_ConElPermisoDeUnReporte_SoloVeEseYLosDemasDan403()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("impuestos@example.com", ReportPermissions.Taxes, ReportPermissions.VatBook);
        using var c = await StaffSession(h, "impuestos@example.com");

        // El menú y el índice ofrecen solo los dos reportes de su permiso, en su grupo.
        var index = await c.Html("/admin/reportes");
        StringAssert.Contains(index, "href=\"/admin/reportes/impuestos\"");
        StringAssert.Contains(index, "href=\"/admin/reportes/libro-iva\"");
        StringAssert.Contains(index, "<h2>Impuestos y Libro IVA</h2>");
        Assert.IsFalse(index.Contains("href=\"/admin/reportes/ingresos\"", StringComparison.Ordinal), "ni en el índice ni en el menú");
        Assert.IsFalse(index.Contains("<h2>Ingresos</h2>", StringComparison.Ordinal), "un grupo sin reportes para este staff no aparece");
        foreach (var path in (string[])["/admin/reportes/impuestos", $"/admin/reportes/libro-iva?mes={ThisMonth}"])
        {
            using var response = await c.Get(path);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
        }

        foreach (var path in (string[])["/admin/reportes/ingresos", "/admin/reportes/ingresos?handler=csv&tabla=Revenue", "/admin/reportes/ventas", "/admin/reportes/deuda", "/admin/reportes/pdf-facturas?handler=zip&mes=2026-10"])
        {
            using var response = await c.Get(path);
            Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode, path);
            StringAssert.Contains(Location(response), "/estado/403", path);
        }

        // Una dirección que no es un reporte sigue dando 404, con permiso o sin él.
        using var unknown = await c.Get("/admin/reportes/nada");
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [TestMethod]
    public async Task Reportes_ConElPermiso_TablasCsvEIdiomas()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("contador@example.com", StaffPermissions.ReportsRead);
        using var c = await StaffSession(h, "contador@example.com");

        // La lista agrupada del menú de Reportes, con los grupos y los nombres del prototipo de la sección 10 de la interfaz.
        var index = await c.Html("/admin/reportes");
        StringAssert.Contains(index, ">Ingresos por mes</a>");
        StringAssert.Contains(index, "href=\"/admin/reportes/ingresos-producto\">Ingresos por producto</a>");
        StringAssert.Contains(index, "<h2>Impuestos y Libro IVA</h2>");
        StringAssert.Contains(index, "href=\"/admin/reportes/libro-iva\">Libro IVA Ventas</a>");
        StringAssert.Contains(index, "href=\"/admin/reportes/deuda#t-AgingAccounts\">Clientes con deuda</a>", "lleva a la tabla por cuenta de la deuda");
        StringAssert.Contains(index, "<h2>Clientes y servicios</h2>");
        StringAssert.Contains(index, "href=\"/admin/reportes/altas-clientes\">Altas de clientes</a>");
        StringAssert.Contains(index, "href=\"/admin/reportes/clientes-servicios\"");
        StringAssert.Contains(index, "href=\"/admin/reportes/soporte\"");
        StringAssert.Contains(index, "aria-current=\"page\"");
        StringAssert.Contains(index, "<span class=\"submenu-title\">Impuestos y Libro IVA</span>", "el menú despliega la misma lista");

        // Cada reporte se dibuja con sus gráficos: uno que nombre mal su tabla o su columna da error aunque no haya filas. Una
        // entrada que lleva a una tabla la tiene en su página.
        foreach (var entry in BillingOss.Web.Pages.Admin.Reports.ReportModel.Catalog)
        {
            using var page = await c.Get("/admin/reportes/" + entry.Slug);
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, entry.Slug);
            if (entry.Table is { } table)
            {
                StringAssert.Contains(await page.Content.ReadAsStringAsync(), $"id=\"t-{table}\"", entry.Title);
            }
        }

        // Las dos facturas del arnés, en el producto del pedido, y las dos cuentas que siembra, dadas de alta este mes.
        var byProduct = await c.Html("/admin/reportes/ingresos-producto");
        StringAssert.Contains(byProduct, "Neto por producto y moneda");
        StringAssert.Contains(byProduct, "<td>VPS 2 GB</td>");
        StringAssert.Contains(byProduct, "<data value=\"20000.00\">20.000,00</data>");
        var signups = await c.Html($"/admin/reportes/altas-clientes?desde={ThisMonth}&hasta={ThisMonth}");
        StringAssert.Contains(signups, "Altas por mes");
        StringAssert.Contains(signups, $"<time datetime=\"{ThisMonth}\">");

        // Las dos facturas de 12.100 que siembra el arnés, en el total del mes y en el gráfico de los doce meses.
        var revenue = await c.Html("/admin/reportes/ingresos");
        StringAssert.Contains(revenue, "Ingresos por mes y moneda");
        StringAssert.Contains(revenue, "Total facturado por mes");
        StringAssert.Contains(revenue, "<svg class=\"chart-cols\"");
        StringAssert.Contains(revenue, "role=\"img\" aria-label=\"Total facturado por mes\"");
        Assert.AreEqual(12, Regex.Matches(revenue, "<g class=\"col\">").Count, "un mes por columna, con cero en los que no tienen facturas");
        StringAssert.Contains(revenue, $"<title>{ThisMonthName}: 24.200,00</title>");
        StringAssert.Contains(revenue, "<data value=\"24200.00\">24.200,00</data>");
        StringAssert.Contains(revenue, $"<time datetime=\"{ThisMonth}\">");
        StringAssert.Contains(revenue, $"handler=csv&amp;tabla=Revenue&amp;desde=");

        using (var csv = await c.Get($"/admin/reportes/ingresos?handler=csv&tabla=Revenue&desde={ThisMonth}&hasta={ThisMonth}"))
        {
            Assert.AreEqual(HttpStatusCode.OK, csv.StatusCode);
            Assert.AreEqual("text/csv", csv.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual($"ingresos-Revenue-{ThisMonth}_{ThisMonth}.csv", csv.Content.Headers.ContentDisposition!.FileName);
            var body = await csv.Content.ReadAsByteArrayAsync();
            CollectionAssert.AreEqual(Encoding.UTF8.GetPreamble(), body[..3]);
            var lines = Encoding.UTF8.GetString(body[3..]).Split("\r\n");
            Assert.AreEqual("Mes,Moneda,Documentos,Facturado,Notas de crédito,Anulado sin nota,Neto,Impuestos,Total", lines[0]);
            Assert.AreEqual($"{ThisMonth},ARS,2,24200.00,0.00,0.00,20000.00,4200.00,24200.00", lines[1]);
        }

        var exports = await h.Query(db => db.Set<AuditEvent>().Where(e => e.Action == "report.export").ToListAsync());
        Assert.AreEqual("report:ingresos", exports.Single().Entity);
        StringAssert.StartsWith(exports.Single().Actor, "staff:");

        // En inglés, los encabezados y la tabla.
        (await c.Get("/idioma/en?volver=/admin/reportes/ingresos")).Dispose();
        StringAssert.Contains(await c.Html("/admin/reportes/ingresos"), "Revenue by month and currency");
        using (var english = await c.Get("/admin/reportes/ingresos?handler=csv&tabla=Revenue"))
        {
            StringAssert.StartsWith(Encoding.UTF8.GetString((await english.Content.ReadAsByteArrayAsync())[3..]), "Month,Currency,Documents,Billed");
        }
    }

    [TestMethod]
    public async Task Reportes_EstadoDeCuentaDesdeLaFichaConSuCuentaEnElCsv()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("cobranzas@example.com", StaffPermissions.AccountsRead, ReportPermissions.Statement);
        using var c = await StaffSession(h, "cobranzas@example.com");

        StringAssert.Contains(await c.Html($"/admin/cuentas/{h.AccountA}"), $"href=\"/admin/reportes/estado-cuenta?cuenta={h.AccountA}\">Estado de cuenta</a>");

        // Sin cuenta pide el número. Con ella, los saldos y los movimientos, y los atajos y el CSV la llevan.
        var empty = await c.Html("/admin/reportes/estado-cuenta");
        StringAssert.Contains(empty, "name=\"cuenta\"");
        StringAssert.Contains(empty, "Elegí el número de la cuenta");
        var statement = await c.Html($"/admin/reportes/estado-cuenta?cuenta={h.AccountA}");
        StringAssert.Contains(statement, "Saldos por moneda");
        StringAssert.Contains(statement, "Saldo al empezar");
        StringAssert.Contains(statement, $"&amp;cuenta={h.AccountA}\"");
        using (var csv = await c.Get($"/admin/reportes/estado-cuenta?handler=csv&tabla=StatementMovements&cuenta={h.AccountA}"))
        {
            Assert.AreEqual(HttpStatusCode.OK, csv.StatusCode);
            StringAssert.StartsWith(Encoding.UTF8.GetString((await csv.Content.ReadAsByteArrayAsync())[3..]), "Fecha,Concepto,Referencia,Moneda,Debe,Haber,Saldo\r\n");
        }

        using (var none = await c.Get("/admin/reportes/estado-cuenta?handler=csv&tabla=StatementMovements"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, none.StatusCode);
        }

        StringAssert.Contains(await c.Html("/admin/reportes/estado-cuenta?cuenta=999999"), "No existe la cuenta 999999.");
    }

    [TestMethod]
    public async Task Reportes_ClientesYServiciosAlDiaYSoporteSinElModulo()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("contador@example.com", StaffPermissions.ReportsRead);
        using var c = await StaffSession(h, "contador@example.com");

        // Los dos pedidos del arnés esperan el pago: por activar y sin servidor, porque el producto es manual.
        var services = await c.Html("/admin/reportes/clientes-servicios");
        StringAssert.Contains(services, "Clientes y servicios al ");
        Assert.IsFalse(services.Contains("name=\"desde\"", StringComparison.Ordinal), "es al día, sin período");
        StringAssert.Contains(services, "Servicios por producto");
        StringAssert.Contains(services, "<td>VPS 2 GB</td>");
        StringAssert.Contains(services, "<td>Sin servidor</td>");
        StringAssert.Contains(services, "Por activar");
        using (var csv = await c.Get("/admin/reportes/clientes-servicios?handler=csv&tabla=ServicesByServer"))
        {
            var lines = Encoding.UTF8.GetString((await csv.Content.ReadAsByteArrayAsync())[3..]).Split("\r\n");
            Assert.AreEqual("Servidor,Módulo,Moneda,Cuentas,Activos,Suspendidos,Por activar,Ingreso recurrente", lines[0]);
            Assert.AreEqual("Sin servidor,,ARS,0,0,0,2,0.00", lines[1]);
        }

        var support = await c.Html("/admin/reportes/soporte");
        StringAssert.Contains(support, "Tickets por departamento y tiempos de respuesta");
        Assert.IsFalse(support.Contains("El módulo de soporte todavía no está instalado", StringComparison.Ordinal), "con el soporte mezclado lee sus tablas");
        StringAssert.Contains(support, "name=\"desde\"");
    }

    [TestMethod]
    public async Task Reportes_DeudaPeriodoInvalidoYLibroIva()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("contador@example.com", StaffPermissions.ReportsRead);
        using var c = await StaffSession(h, "contador@example.com");

        var aging = await c.Html("/admin/reportes/deuda");
        StringAssert.Contains(aging, "Deuda al ");
        StringAssert.Contains(aging, $"<a href=\"/admin/cuentas/{h.AccountA}\">{h.AccountA}</a>");
        Assert.IsFalse(aging.Contains("name=\"desde\"", StringComparison.Ordinal), "la deuda es al día, sin período");

        var invalid = await c.Html("/admin/reportes/ingresos?desde=2026-12&hasta=2026-01");
        StringAssert.Contains(invalid, "role=\"alert\"");
        StringAssert.Contains(invalid, "El período no es válido");
        using (var badCsv = await c.Get("/admin/reportes/ingresos?handler=csv&tabla=Revenue&desde=nada"))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, badCsv.StatusCode);
        }

        using (var unknown = await c.Get("/admin/reportes/nada"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        // Sin módulo fiscal no hay comprobantes: el libro avisa que faltan y los archivos salen vacíos.
        var libro = await c.Html($"/admin/reportes/libro-iva?mes={ThisMonth}");
        StringAssert.Contains(libro, "No hay comprobantes autorizados en este mes.");
        StringAssert.Contains(libro, "todavía no tienen comprobante fiscal");
        using var file = await c.Get($"/admin/reportes/libro-iva?handler=vouchers&mes={ThisMonth}");
        Assert.AreEqual(HttpStatusCode.OK, file.StatusCode);
        Assert.AreEqual("iso-8859-1", file.Content.Headers.ContentType!.CharSet);
        Assert.AreEqual($"LIBRO_IVA_DIGITAL_VENTAS_CBTE_{ThisMonth.Replace("-", string.Empty, StringComparison.Ordinal)}.txt", file.Content.Headers.ContentDisposition!.FileName);
        Assert.IsEmpty(await file.Content.ReadAsByteArrayAsync());
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(e => e.Action == "report.export" && e.Entity == "report:libro-iva")));
    }
}
