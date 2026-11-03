using BillingOss.App.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Admin.Reports;

/// <summary>Los reportes y salidas contables que el staff puede ver, con qué muestra cada uno.</summary>
[Authorize(Policy = StaffPermissions.AnyReport)]
public sealed class ReportsIndexModel : PageModel
{
}
