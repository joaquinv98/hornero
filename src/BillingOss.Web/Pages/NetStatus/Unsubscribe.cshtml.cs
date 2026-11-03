using BillingOss.App.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BillingOss.Web.Pages.NetStatus;

/// <summary>
/// La baja de un aviso (sección 6.5 de diseno/contenidos.md). Abrir el enlace no borra nada: la página pide confirmar con un
/// botón, así un programa de correo que abre los enlaces no puede dar de baja a nadie.
/// </summary>
[EnableRateLimiting(Ui.FormLimiter)]
public sealed class UnsubscribeModel(SubscriptionService subscriptions) : ContentPage
{
    public bool Done { get; private set; }

    public async Task<IActionResult> OnGet(string token) =>
        await subscriptions.Exists(token, HttpContext.RequestAborted) ? Page() : NotFound();

    public async Task<IActionResult> OnPostConfirm(string token)
    {
        await subscriptions.Unsubscribe(token, HttpContext.RequestAborted);
        Done = true;
        return Page();
    }
}
