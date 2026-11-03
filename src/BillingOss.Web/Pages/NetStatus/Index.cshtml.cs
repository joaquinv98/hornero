using BillingOss.App.Content;
using BillingOss.Web.Content;

namespace BillingOss.Web.Pages.NetStatus;

/// <summary>
/// Estado de la red (sección 6.6 de diseno/contenidos.md): el estado global, los componentes, los eventos vigentes, los
/// mantenimientos próximos y lo de los últimos días. Es público: el estado de la red no depende de quién mira.
/// </summary>
public sealed class IndexModel(StatusService status) : ContentPage
{
    public StatusOverview Overview { get; private set; } = null!;

    public async Task OnGet() => Overview = await status.Overview(Language, HttpContext.RequestAborted);
}
