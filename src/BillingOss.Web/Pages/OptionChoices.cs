using BillingOss.Domain.Catalog;
using BillingOss.Domain.Shared;

namespace BillingOss.Web.Pages;

/// <summary>Lo elegido de una opción en un formulario: el valor de una lista o la cantidad de una cantidad o de un sí o no.</summary>
public sealed class ChoiceRow
{
    public long OptionId { get; set; }

    public long? ValueId { get; set; }

    public int? Quantity { get; set; }

    /// <summary>Lo que se le pide al caso de uso: una lista manda el valor y lo demás, la cantidad.</summary>
    public static List<OptionChoice> ToChoices(IEnumerable<ChoiceRow> rows, OptionCatalog? options) =>
        [.. rows.Select(c => options?.Options.FirstOrDefault(o => o.Id == c.OptionId) is { SingleValue: true }
            ? new OptionChoice(c.OptionId, null, c.Quantity ?? 0)
            : new OptionChoice(c.OptionId, c.ValueId))];

    /// <summary>Una fila por opción: lo que ya venía elegido o, si no, el defecto de la opción.</summary>
    public static List<ChoiceRow> Complete(IEnumerable<ChoiceRow> rows, OptionCatalog options, IEnumerable<Option> shown) =>
        [.. shown.Select(o => rows.FirstOrDefault(c => c.OptionId == o.Id) ?? new ChoiceRow
        {
            OptionId = o.Id,
            ValueId = options.ValuesOf(o.Id).FirstOrDefault(v => !v.Hidden)?.Id,
            Quantity = o.Kind == OptionKind.Quantity ? o.MinQuantity : 0,
        })];
}

/// <summary>
/// Los campos de las opciones de un formulario de pedido o de cambio (_OptionChoices): el catálogo de opciones del
/// producto, las filas, la moneda y el ciclo para mostrar el precio de cada valor, si se ofrecen los valores ocultos y
/// el nombre de la lista en el formulario.
/// </summary>
public sealed record OptionChoices(OptionCatalog Options, IReadOnlyList<ChoiceRow> Choices, Currency Currency, BillingCycle Cycle, bool ShowHidden, string Prefix = "Input.Choices");
