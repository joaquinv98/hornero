using System.Globalization;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.UseCases;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Consumer;

/// <summary>
/// Botón de baja de servicio y botón de arrepentimiento (Disposición 954/2025): a la vista desde el primer acceso,
/// sin iniciar sesión. La identidad se verifica con un código al correo de la cuenta y la constancia sale en pantalla
/// y por correo.
/// </summary>
public sealed class RequestModel(ConsumerRequestService requests, IOptions<BillingOptions> options) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Kind { get; set; } = string.Empty;

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string? Detail { get; set; }

    [BindProperty]
    public string? Reference { get; set; }

    [BindProperty]
    public string Code { get; set; } = string.Empty;

    public ConsumerReceipt? Receipt { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Fecha y hora de la constancia en la zona horaria del proveedor.</summary>
    public string ReceiptTime => Receipt is null ? string.Empty
        : TimeZoneInfo.ConvertTime(Receipt.At, TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone)).ToString("g", CultureInfo.CurrentCulture);

    public bool IsWithdrawal => Kind == "arrepentimiento";

    private ConsumerRequestKind RequestKind => IsWithdrawal ? ConsumerRequestKind.Withdrawal : ConsumerRequestKind.Cancellation;

    public void OnGet() => Reference = null;

    public async Task<IActionResult> OnPostStart()
    {
        try
        {
            Reference = await requests.Start(RequestKind, Email, Detail, HttpContext.Connection.RemoteIpAddress?.ToString(), HttpContext.RequestAborted);
        }
        catch (ValidationException)
        {
            Reference = null;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostConfirm()
    {
        Receipt = Reference is null ? null : await requests.Confirm(Reference, Code, HttpContext.RequestAborted);
        if (Receipt is null || Receipt.Kind != RequestKind)
        {
            Receipt = null;
            Error = "Consumer.CodeFailed";
        }

        return Page();
    }
}
