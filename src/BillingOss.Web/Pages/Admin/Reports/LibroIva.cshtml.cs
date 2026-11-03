using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.App.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Admin.Reports;

/// <summary>
/// Libro IVA Digital de ventas de un mes (RG 4597): el resumen, lo que hay que revisar antes de presentarlo y los dos
/// archivos para importar en ARCA. Por defecto, el mes anterior, que es el que se presenta.
/// </summary>
[Authorize(Policy = ReportPermissions.VatBook)]
public sealed class LibroIvaModel(ReportService reports, AuditLog audit) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Mes { get; set; }

    public DateOnly Month { get; private set; }

    public LibroIva? Libro { get; private set; }

    public bool InvalidMonth { get; private set; }

    public string MonthText => MonthRange.Format(Month);

    public async Task OnGet(CancellationToken ct)
    {
        if (Parse())
        {
            Libro = await reports.LibroIva(Month, ct);
        }
    }

    public Task<IActionResult> OnGetVouchers(CancellationToken ct) => Download("VENTAS_CBTE", l => l.VouchersFile, ct);

    public Task<IActionResult> OnGetAliquots(CancellationToken ct) => Download("VENTAS_ALICUOTAS", l => l.AliquotsFile, ct);

    /// <summary>Uno de los dos archivos, en ISO 8859-1 como los pide ARCA. Queda en la auditoría: lleva nombres y CUIT.</summary>
    private async Task<IActionResult> Download(string file, Func<LibroIva, byte[]> content, CancellationToken ct)
    {
        if (!Parse())
        {
            return BadRequest();
        }

        var libro = await reports.LibroIva(Month, ct);
        await audit.Record("report.export", "report:libro-iva", new { file, period = MonthText }, ct);
        var name = string.Create(CultureInfo.InvariantCulture, $"LIBRO_IVA_DIGITAL_{file}_{Month:yyyyMM}.txt");
        return File(content(libro), "text/plain; charset=iso-8859-1", name);
    }

    private bool Parse()
    {
        var today = reports.Today;
        var month = Mes is null ? new DateOnly(today.Year, today.Month, 1).AddMonths(-1) : MonthRange.ParseMonth(Mes);
        InvalidMonth = month is null;
        Month = month ?? default;
        return !InvalidMonth;
    }
}
