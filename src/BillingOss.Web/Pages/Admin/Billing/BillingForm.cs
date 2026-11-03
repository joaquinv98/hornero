using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Admin.Billing;

/// <summary>Un renglón del formulario de una factura manual o de un presupuesto. Los números llegan como texto con punto decimal.</summary>
public sealed class LineRow
{
    public string? Description { get; set; }

    public string? Quantity { get; set; }

    public string? UnitPrice { get; set; }

    public TaxTreatment Treatment { get; set; } = TaxTreatment.Taxed;

    public long? PlanId { get; set; }

    public bool IsBlank => string.IsNullOrWhiteSpace(Description) && string.IsNullOrWhiteSpace(UnitPrice) && PlanId is null;

    public static LineRow From(QuoteLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new() { Description = line.Description, Quantity = BillingForm.Format(line.Quantity), UnitPrice = BillingForm.Format(line.UnitPrice), Treatment = line.Treatment, PlanId = line.PlanId };
    }
}

/// <summary>Un plan para elegir en un renglón de presupuesto: el producto y su ciclo.</summary>
public sealed record PlanChoice(long Id, string Name);

/// <summary>El editor de renglones: las filas, los planes si el renglón puede cotizar uno, y si se puede cambiar.</summary>
public sealed record LinesEditor(IReadOnlyList<LineRow> Rows, IReadOnlyList<PlanChoice>? Plans = null);

/// <summary>Lo que comparten las pantallas de facturación del admin: renglones, números y permisos.</summary>
public static class BillingForm
{
    /// <summary>Filas en blanco al final del editor, para cargar sin JavaScript.</summary>
    public const int BlankRows = 3;

    public static IReadOnlyList<TaxTreatment> Treatments { get; } = Enum.GetValues<TaxTreatment>();

    public static bool Can(this PageModel page, string permission)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.User.HasClaim(Claims.Permission, permission);
    }

    /// <summary>Como lo espera un campo numérico: punto decimal, sin separador de miles ni ceros de más.</summary>
    public static string Format(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Un número del formulario. Lo que no es un número es un error para el que lo cargó.</summary>
    public static decimal Number(string? text, string what) =>
        decimal.TryParse(text?.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ValidationException($"{what}: «{text}» no es un número.");

    /// <summary>Los renglones cargados. Las filas en blanco no cuentan y sin cantidad vale 1.</summary>
    public static List<QuoteLine> Lines(IEnumerable<LineRow>? rows) =>
        [.. (rows ?? []).Where(r => !r.IsBlank).Select(r => new QuoteLine(
            r.Description ?? string.Empty,
            string.IsNullOrWhiteSpace(r.Quantity) ? 1m : Number(r.Quantity, "Cantidad"),
            Number(r.UnitPrice, "Precio"),
            r.Treatment,
            r.PlanId))];

    /// <summary>Saca las filas en blanco y agrega las de cargar, así un error no multiplica las filas vacías.</summary>
    public static List<LineRow> WithBlanks(IEnumerable<LineRow>? rows) =>
        [.. (rows ?? []).Where(r => !r.IsBlank), .. Enumerable.Range(0, BlankRows).Select(_ => new LineRow())];
}
