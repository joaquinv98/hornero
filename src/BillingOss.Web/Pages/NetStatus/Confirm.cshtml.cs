using BillingOss.App.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Mvc;

namespace BillingOss.Web.Pages.NetStatus;

/// <summary>
/// El enlace del correo de confirmación. El token es de un solo propósito: confirma esa casilla y no inicia sesión ni muestra
/// datos. Un enlace que no existe o ya se dio de baja da 404. Es un GET a propósito: es el enlace del correo.
/// </summary>
public sealed class ConfirmModel(SubscriptionService subscriptions) : ContentPage
{
    public async Task<IActionResult> OnGet(string token) =>
        await subscriptions.Confirm(token, HttpContext.RequestAborted) is null ? NotFound() : Page();
}
