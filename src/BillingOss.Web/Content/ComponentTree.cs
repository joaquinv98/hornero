using BillingOss.App.Content;
using BillingOss.Domain.Content;

namespace BillingOss.Web.Content;

/// <summary>Los componentes de la red en el orden de la página pública, para las listas y los selectores del panel.</summary>
public sealed class ComponentTree
{
    private ComponentTree(IReadOnlyList<(ComponentRow Row, int Depth)> flat, IReadOnlyList<Choice> choices)
    {
        Flat = flat;
        Choices = choices;
    }

    /// <summary>Cada componente con su nivel: 0 un grupo o un componente suelto, 1 un hijo.</summary>
    public IReadOnlyList<(ComponentRow Row, int Depth)> Flat { get; }

    /// <summary>Lo que se puede elegir en un evento: los componentes que no son grupos, con el nombre de su grupo adelante.</summary>
    public IReadOnlyList<Choice> Choices { get; }

    public static string NameOf(ComponentRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return ContentTexts.Pick(row.Texts, ContentTexts.DefaultLanguage)?.Title ?? row.Component.Slug;
    }

    public static ComponentTree Of(IReadOnlyList<ComponentRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var roots = rows.Where(r => r.Component.ParentId is null).ToList();
        var flat = roots.SelectMany(r => new[] { (Row: r, Depth: 0) }.Concat(rows.Where(c => c.Component.ParentId == r.Component.Id).Select(c => (Row: c, Depth: 1)))).ToList();
        var names = rows.ToDictionary(r => r.Component.Id, NameOf);
        var choices = flat.Where(f => f.Row.Component.Kind != ComponentKind.Group)
            .Select(f => new Choice(f.Row.Component.Id, f.Row.Component.ParentId is { } parent && names.TryGetValue(parent, out var group) ? $"{group} / {NameOf(f.Row)}" : NameOf(f.Row)))
            .ToList();
        return new ComponentTree(flat, choices);
    }
}
