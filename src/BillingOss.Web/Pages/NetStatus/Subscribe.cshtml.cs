using System.Globalization;
using BillingOss.App.Content;
using BillingOss.App.UseCases;
using BillingOss.Domain.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BillingOss.Web.Pages.NetStatus;

/// <summary>
/// Suscripción por correo a los avisos de la red (sección 6.5 de diseno/contenidos.md). La respuesta es la misma exista o no la
/// casilla, y no se recibe nada hasta abrir el enlace de confirmación. Tiene el tope por IP de los formularios públicos.
/// </summary>
[EnableRateLimiting(Ui.FormLimiter)]
public sealed class SubscribeModel(StatusService status, App.Content.SubscriptionService subscriptions) : ContentPage
{
    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string? Lang { get; set; }

    [BindProperty]
    public long[] Components { get; set; } = [];

    public IReadOnlyList<ComponentView> Choices { get; private set; } = [];

    public bool Done { get; private set; }

    public string? Error { get; private set; }

    public async Task OnGet()
    {
        Lang = Language;
        await Load();
    }

    public async Task<IActionResult> OnPost()
    {
        await Load();
        try
        {
            await subscriptions.Subscribe(Email, Lang, Components, HttpContext.RequestAborted);
            Done = true;
            Email = string.Empty;
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
        }

        return Page();
    }

    /// <summary>Los servidores y servicios que se pueden elegir: los visibles que no son grupos.</summary>
    private async Task Load()
    {
        var overview = await status.Overview(Language, HttpContext.RequestAborted);
        Choices = [.. Flatten(overview.Components)];
    }

    private static IEnumerable<ComponentView> Flatten(IEnumerable<ComponentView> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Kind != ComponentKind.Group)
            {
                yield return node;
            }

            foreach (var child in Flatten(node.Children))
            {
                yield return child;
            }
        }
    }

    public static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}
