using BillingOss.App.Persistence;
using BillingOss.Domain.Payments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages;

/// <summary>
/// Vuelta desde la pasarela, el paso Listo del pago (sección 9.3 del protocolo y pasos de pago de D-19). Se atiende
/// por GET, no cambia nada y no pide sesión: muestra el estado del intento, que se confirma por aviso o por consulta.
/// La referencia no se puede adivinar. Mientras espera, la página se vuelve a pedir sola.
/// </summary>
public sealed class PayReturnModel(BillingDbContext db) : PageModel
{
    public PaymentAttempt? Attempt { get; private set; }

    /// <summary>El cliente volvió con "cancelar" desde la pasarela y el intento sigue abierto.</summary>
    public bool Cancelled { get; private set; }

    /// <summary>El enlace para volver a pagar las mismas facturas.</summary>
    public string Retry => "/pagar?" + string.Join('&', Attempt!.DocumentIds.Select(d => FormattableString.Invariant($"factura={d}")));

    public async Task<IActionResult> OnGet(string reference)
    {
        Attempt = await db.PaymentAttempts.AsNoTracking().SingleOrDefaultAsync(a => a.Reference == reference);
        if (Attempt is null)
        {
            return NotFound();
        }

        Cancelled = Attempt.Status == AttemptStatus.Open && Request.Query.ContainsKey("cancelado");
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        if (Attempt.Status == AttemptStatus.Open && !Cancelled)
        {
            Response.Headers["Refresh"] = "15";
        }

        return Page();
    }
}
