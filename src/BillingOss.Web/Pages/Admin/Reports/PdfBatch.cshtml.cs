using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.App.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Admin.Reports;

/// <summary>
/// Los PDF de las facturas y notas emitidas en un mes, en un ZIP para el contador o el archivo: cuántos hay, cuántos
/// todavía no tienen su PDF y la descarga. Por defecto, el mes anterior.
/// </summary>
[Authorize(Policy = ReportPermissions.InvoicePdfs)]
public sealed class PdfBatchModel(ReportService reports, AuditLog audit) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Mes { get; set; }

    public DateOnly Month { get; private set; }

    public Report? Result { get; private set; }

    public bool InvalidMonth { get; private set; }

    public string MonthText => MonthRange.Format(Month);

    /// <summary>Los documentos del mes que tienen su PDF: los que lleva el ZIP.</summary>
    public long WithPdf => Result?.Tables.SingleOrDefault()?.Totals.SingleOrDefault()?[2] is long count ? count : 0;

    public async Task OnGet(CancellationToken ct)
    {
        if (Parse())
        {
            Result = await reports.InvoicePdfs(Month, ct);
        }
    }

    /// <summary>El ZIP del mes. Queda en la auditoría: los PDF llevan nombres y CUIT de clientes.</summary>
    public async Task<IActionResult> OnGetZip(CancellationToken ct)
    {
        if (!Parse())
        {
            return BadRequest();
        }

        if (await reports.InvoicePdfZip(Month, ct) is not { } zip)
        {
            return NotFound();
        }

        await audit.Record("report.export", "report:pdf-facturas", new { file = "zip", period = MonthText }, ct);
        return File(zip, "application/zip", string.Create(CultureInfo.InvariantCulture, $"facturas-{Month:yyyy-MM}.zip"));
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
