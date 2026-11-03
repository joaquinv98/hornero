using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.UseCases;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Admin.Catalog;

/// <summary>Una fila de precio del formulario. Los importes llegan como texto con punto decimal, igual en cualquier idioma.</summary>
public sealed class PriceRow
{
    public string? Cycle { get; set; }

    public string? Currency { get; set; }

    public string? Amount { get; set; }

    public string? SetupFee { get; set; }

    public bool IsBlank => string.IsNullOrWhiteSpace(Cycle) && string.IsNullOrWhiteSpace(Currency) && string.IsNullOrWhiteSpace(Amount) && string.IsNullOrWhiteSpace(SetupFee);

    public static PriceRow From(PriceView p) => new() { Cycle = p.Cycle, Currency = p.Currency, Amount = CatalogForm.Format(p.Amount), SetupFee = CatalogForm.Format(p.SetupFee) };
}

/// <summary>Un producto para elegir en una lista de casillas, con su grupo para ordenarla.</summary>
public sealed record ProductChoice(long Id, string Name, string? Group);

/// <summary>La tabla de precios de un formulario: el prefijo de los nombres de campo, las filas y su título.</summary>
public sealed record PriceTable(string Prefix, IReadOnlyList<PriceRow> Rows, string Caption);

/// <summary>
/// Una lista de casillas: el nombre del campo, su título, lo que se puede elegir y lo elegido. Sin grupo, lo que no se
/// eligió va bajo <paramref name="Ungrouped"/>.
/// </summary>
public sealed record ProductPicker(string Name, string Legend, IReadOnlyList<ProductChoice> All, IReadOnlyCollection<long> Selected, string? Help = null, string? Ungrouped = null);

/// <summary>Lo que comparten las pantallas del catálogo: ciclos, importes, permisos y filas en blanco para agregar.</summary>
public static class CatalogForm
{
    /// <summary>Los ciclos que ofrece el formulario. Uno guardado con otra cantidad se muestra igual, con su código.</summary>
    public static IReadOnlyList<string> Cycles { get; } = ["P1M", "P3M", "P6M", "P1Y", "P2Y", "P3Y", "onetime", "free"];

    /// <summary>Filas en blanco que se agregan al final de cada lista, para cargar sin JavaScript.</summary>
    public const int BlankRows = 2;

    public static bool HasCatalogWrite(this PageModel page) => page.User.HasClaim(Claims.Permission, StaffPermissions.CatalogWrite);

    /// <summary>El importe como lo espera un campo numérico del navegador: punto decimal y sin separador de miles.</summary>
    public static string Format(decimal amount) => amount.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Un importe del formulario. Vacío es cero, y lo que no es un número es un error para el que lo cargó.</summary>
    public static decimal Amount(string? text, string what) =>
        string.IsNullOrWhiteSpace(text) ? 0m
        : decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount) ? amount
        : throw new ValidationException($"{what}: «{text}» no es un importe.");

    /// <summary>Las filas cargadas como precios del caso de uso. Las filas en blanco no cuentan.</summary>
    public static List<PriceInput> Prices(IEnumerable<PriceRow> rows, string what) =>
        [.. rows.Where(r => !r.IsBlank).Select(r => new PriceInput(r.Cycle ?? string.Empty, r.Currency ?? string.Empty, Amount(r.Amount, what), Amount(r.SetupFee, what)))];

    /// <summary>Saca las filas en blanco y agrega las de cargar, así un error no multiplica las filas vacías.</summary>
    public static List<PriceRow> WithBlanks(IEnumerable<PriceRow>? rows, int blanks = BlankRows) =>
        [.. (rows ?? []).Where(r => !r.IsBlank), .. Enumerable.Range(0, blanks).Select(_ => new PriceRow())];
}

/// <summary>El segmento de la ruta de un formulario del catálogo: "nuevo" es un alta y un número, una edición.</summary>
public static class Ids
{
    public static bool TryParse(string id, out long? value)
    {
        value = null;
        if (id == "nuevo")
        {
            return true;
        }

        if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            value = n;
            return true;
        }

        return false;
    }
}
