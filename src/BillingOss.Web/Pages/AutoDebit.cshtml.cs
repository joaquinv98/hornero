using BillingOss.App;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages;

/// <summary>
/// Condiciones del débito automático, a la vista sin iniciar sesión (sección 4.5 del diseño de cobros): cuándo y cuántas
/// veces se intenta, cómo se calcula el importe, los avisos y cómo se revoca. La casilla de consentimiento enlaza acá.
/// </summary>
public sealed class AutoDebitModel(IOptions<PaymentsOptions> options) : PageModel
{
    /// <summary>Días de cada intento respecto del vencimiento: negativos antes, 0 el mismo día.</summary>
    public IReadOnlyList<int> Days => options.Value.AutoCharge.EffectiveDays;

    public int Hour => options.Value.AutoCharge.Hour;

    public void OnGet()
    {
    }
}
