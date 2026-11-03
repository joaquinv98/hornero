using System.Globalization;
using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.Domain.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BillingOss.Web.Pages.Admin.Contenidos;

/// <summary>
/// El estado de la red para el staff: el estado de ahora, los eventos en tres vistas (abiertas, programadas y resueltas, estas
/// últimas por mes), los componentes y los suscriptores. Abrir un incidente o programar un mantenimiento se hace en la
/// pantalla "Nueva incidencia" (sección 9 de diseno/contenidos.md).
/// </summary>
[Authorize(Policy = StaffPermissions.StatusWrite)]
public sealed class NetworkModel(StatusService status, SubscriptionService subscriptions, TimeProvider time) : AdminContentPage
{
    public const string ViewOpen = "abiertas";
    public const string ViewScheduled = "programadas";
    public const string ViewResolved = "resueltas";

    /// <summary>La vista pedida: <c>abiertas</c>, <c>programadas</c> o <c>resueltas</c>. Sin ella, la portada con todo.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Vista { get; set; }

    /// <summary>El mes de la vista de resueltas, como <c>2026-10</c>. Sin él, el mes de ahora en la zona del proveedor.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Mes { get; set; }

    public ComponentStatus Overall { get; private set; }

    /// <summary>La vista que se muestra, o nulo en la portada.</summary>
    public string? View { get; private set; }

    /// <summary>El mes que se muestra en la vista de resueltas, como lo pide un campo de mes.</summary>
    public string MonthValue { get; private set; } = string.Empty;

    /// <summary>Los eventos de la vista pedida. En la portada, los abiertos y programados.</summary>
    public IReadOnlyList<EventView> Events { get; private set; } = [];

    /// <summary>Solo en la portada: los últimos que ya terminaron.</summary>
    public IReadOnlyList<EventView> Past { get; private set; } = [];

    public IReadOnlyList<ComponentRow> Components { get; private set; } = [];

    /// <summary>Los componentes con su nivel (0 un grupo o un componente suelto, 1 un hijo), en el orden de la página pública.</summary>
    public IReadOnlyList<(ComponentRow Row, int Depth)> Flat { get; private set; } = [];

    public IReadOnlyList<SubscriberRow> Subscribers { get; private set; } = [];

    public int Confirmed { get; private set; }

    public int Pending { get; private set; }

    public Task OnGet() => Load();

    public async Task<IActionResult> OnPostRemoveSubscriber(long id)
    {
        if (await Try(() => subscriptions.Remove(id, Ct)))
        {
            return SavedRedirect();
        }

        await Load();
        return Page();
    }

    private async Task Load()
    {
        var language = ContentTexts.DefaultLanguage;
        Overall = (await status.Overview(language, Ct)).Overall;
        View = Vista is ViewOpen or ViewScheduled or ViewResolved ? Vista : null;
        if (View == ViewResolved)
        {
            var (year, month) = Month();
            MonthValue = $"{year:0000}-{month:00}";
            Events = await status.FinishedForStaff(year, month, language, Zone, Ct);
            return;
        }

        var open = await status.List(null, openOnly: true, limit: 100, language: language, ct: Ct);
        Events = View switch
        {
            ViewOpen => [.. open.Where(e => e.Kind == StatusEventKind.Incident || e.Phase == MaintenancePhase.InProgress)],
            ViewScheduled => [.. open.Where(e => e.Kind == StatusEventKind.Maintenance && e.Phase == MaintenancePhase.Upcoming)],
            _ => open,
        };
        if (View is not null)
        {
            return;
        }

        var openIds = open.Select(e => e.Id).ToHashSet();
        Past = [.. (await status.List(null, openOnly: false, limit: 30, language: language, ct: Ct)).Where(e => !openIds.Contains(e.Id))];
        Components = await status.Components(Ct);
        Flat = ComponentTree.Of(Components).Flat;
        // ponytail: los últimos 200 suscriptores y 30 eventos pasados, sin paginar. Paginar si la lista crece más que eso.
        Subscribers = await subscriptions.List(null, 200, Ct);
        (Confirmed, Pending) = await subscriptions.Counts(Ct);
    }

    /// <summary>El año y el mes pedidos. Uno ilegible o fuera de rango vale el mes de ahora en la zona del proveedor.</summary>
    private (int Year, int Month) Month()
    {
        if (DateTime.TryParseExact(Mes, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var picked))
        {
            return (picked.Year, picked.Month);
        }

        var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone);
        return (now.Year, now.Month);
    }
}
