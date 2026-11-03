using System.Globalization;
using BillingOss.App.Content;
using BillingOss.Web.Content;
using Microsoft.AspNetCore.Mvc;

namespace BillingOss.Web.Pages.NetStatus;

/// <summary>Los eventos terminados de un mes, los más nuevos primero. El mes viaja como <c>?mes=2026-10</c>.</summary>
public sealed class HistoryModel(StatusService status, TimeProvider time) : ContentPage
{
    [BindProperty(SupportsGet = true, Name = "mes")]
    public string? Month { get; set; }

    public DateOnly First { get; private set; }

    public IReadOnlyList<EventView> Items { get; private set; } = [];

    /// <summary>El mes anterior, y el siguiente si no es futuro, como valores de <c>mes</c>.</summary>
    public string Previous => First.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public string? Next
    {
        get
        {
            var next = First.AddMonths(1);
            return next > Current ? null : next.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        }
    }

    private DateOnly Current
    {
        get
        {
            var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone);
            return new DateOnly(now.Year, now.Month, 1);
        }
    }

    public async Task OnGet()
    {
        First = DateTime.TryParseExact(Month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && parsed.Year >= 2000
            ? new DateOnly(parsed.Year, parsed.Month, 1)
            : Current;
        if (First > Current)
        {
            First = Current;
        }

        Items = await status.History(First.Year, First.Month, Language, Zone, HttpContext.RequestAborted);
    }
}
