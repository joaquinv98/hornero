using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Shared;
using BillingOss.Domain.Usage;
using BillingOss.Modules.Protocol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages.Admin;

/// <summary>
/// Las métricas de uso de un producto y sus precios (sección 1 de diseno/uso-medido.md). Las métricas salen del
/// manifiesto del módulo del producto. Los números se leen con punto o coma decimal, sin separador de miles, para que
/// "0.5" no se lea como 5 con la cultura es-AR.
/// </summary>
[Authorize(Policy = StaffPermissions.CatalogWrite)]
public sealed class ProductUsageModel(BillingDbContext db, UsageCatalog catalog) : PageModel
{
    public string? Product { get; private set; }

    public IReadOnlyList<UsageMetric> Metrics { get; private set; } = [];

    public IReadOnlyList<UsageMetricPrice> Prices { get; private set; } = [];

    public IReadOnlyDictionary<string, UsageMetricDeclaration> Declared { get; private set; } = new Dictionary<string, UsageMetricDeclaration>();

    /// <summary>El motivo de un cambio que no se guardó, tal como lo da la regla.</summary>
    public string? Error { get; private set; }

    public bool Saved { get; private set; }

    public async Task<IActionResult> OnGet(long id) => await Load(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostMetric(long id, string code, string? name, string? unit, UsageAggregation? aggregation, string? included, string? step, UsageCycle cycle, string? alerts, bool active)
    {
        await Run(async () =>
        {
            var percents = (alerts ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new ValidationException("Los umbrales van como números enteros separados por comas."))
                .ToList();
            await catalog.Save(id, code, new UsageMetricInput(name, unit, aggregation, Number(included, 0m), Number(step, 1m), cycle, percents, active), HttpContext.RequestAborted);
        });
        return await Load(id) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostPrice(long id, string code, string? currency, string? unitPrice, string? validFrom)
    {
        await Run(async () =>
        {
            var from = DateOnly.TryParseExact(validFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : throw new ValidationException("La fecha de vigencia va como aaaa-mm-dd.");
            await catalog.AddPrice(id, code, new Currency((currency ?? string.Empty).Trim().ToUpperInvariant()), Number(unitPrice, -1m), from, HttpContext.RequestAborted);
        });
        return await Load(id) ? Page() : NotFound();
    }

    /// <summary>Un número con punto o coma decimal. Vacío vale el valor por defecto, y algo que no es número da error.</summary>
    private static decimal Number(string? text, decimal empty) =>
        string.IsNullOrWhiteSpace(text) ? empty
        : decimal.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value
        : throw new ValidationException($"«{text}» no es un número: va con punto o coma decimal, sin separador de miles.");

    private async Task Run(Func<Task> change)
    {
        try
        {
            await change();
            Saved = true;
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            Error = ex.Message;
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        }
    }

    private async Task<bool> Load(long id)
    {
        Product = await db.Products.AsNoTracking().Where(p => p.Id == id).Select(p => p.Name).SingleOrDefaultAsync();
        if (Product is null)
        {
            return false;
        }

        Metrics = await db.Set<UsageMetric>().AsNoTracking().Where(m => m.ProductId == id).OrderBy(m => m.Id).ToListAsync();
        var ids = Metrics.Select(m => m.Id).ToList();
        Prices = await db.Set<UsageMetricPrice>().AsNoTracking().Where(p => ids.Contains(p.MetricId)).OrderBy(p => p.Currency).ThenBy(p => p.ValidFrom).ToListAsync();
        try
        {
            Declared = await catalog.Declared(id, HttpContext.RequestAborted);
        }
        catch (ValidationException ex)
        {
            Error ??= ex.Message;
        }

        return true;
    }
}
