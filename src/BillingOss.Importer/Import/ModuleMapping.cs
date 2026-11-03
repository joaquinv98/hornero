using System.Text.Json;
using System.Text.Json.Serialization;

namespace BillingOss.Importer.Import;

/// <summary>
/// Qué hacer con los productos y servicios de un servertype del origen (o de un servertype en un servidor puntual,
/// con la clave "servertype:servidor"). Lo arma el staff y se pasa con --modules. El formato y lo que espera cada
/// módulo están en diseno/importacion.md.
/// </summary>
public sealed class ModuleMapping
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Nombre de la instancia de módulo, que ya tiene que existir en la base destino.</summary>
    public string? Instance { get; init; }

    /// <summary>Sin módulo: el producto pasa a manual y sus operaciones se resuelven en el núcleo.</summary>
    public bool Manual { get; init; }

    /// <summary>
    /// Parámetro del módulo y columna del producto de donde sale (por ejemplo configoption1). Sin params van los
    /// configoptionN del origen tal cual, y con params vacío no va ninguno: así una entrada del paralelo no copia
    /// nada del producto, tampoco un secreto guardado en un configoptionN.
    /// </summary>
    public Dictionary<string, string>? Params { get; init; }

    /// <summary>ID externo y de dónde sale: una columna del servicio o "campo:nombre" de un campo de producto.</summary>
    public Dictionary<string, string> ExternalIds { get; init; } = [];

    /// <summary>
    /// Clave de módulo de cada opción configurable de los productos de este servertype: la opción del origen (lo que va
    /// antes de la barra en su nombre) y dónde va su valor ("variables.SERVER_SLOTS"). Vacío la deja sin clave.
    /// </summary>
    public Dictionary<string, string> Options { get; init; } = [];

    /// <summary>IDs externos fijos por servicio, para lo que no está en la copia.</summary>
    public Dictionary<long, Dictionary<string, string>> Services { get; init; } = [];

    public static IReadOnlyDictionary<string, ModuleMapping> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, ModuleMapping>>(json, Json) ?? [];
}
