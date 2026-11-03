using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin;

/// <summary>Avance de un pedido de actualización: los pasos que deja el actualizador y, al final, el resultado o el motivo.</summary>
[Authorize(Policy = StaffPermissions.SystemUpdates)]
public sealed class AdminUpdateRequestModel(UpdateService updates, IOptions<BillingOptions> billing) : PageModel
{
    public UpdateRequest Current { get; private set; } = null!;

    public IReadOnlyList<UpdateEvent> Events { get; private set; } = [];

    public async Task<IActionResult> OnGet(long id)
    {
        if (await updates.Find(id) is not { } request)
        {
            return NotFound();
        }

        Current = request;
        Events = UpdateService.Events(request);
        return Page();
    }

    public string When(DateTimeOffset at) => Clock.When(at, billing.Value);
}
