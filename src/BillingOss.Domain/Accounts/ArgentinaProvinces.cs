using System.Text;

namespace BillingOss.Domain.Accounts;

/// <summary>
/// Subdivisiones de Argentina en ISO 3166-2, que es como las reglas de impuesto y los regímenes de ingresos brutos
/// nombran la provincia del receptor (sección 3.1 del diseño fiscal). El importador y las fichas normalizan el texto
/// libre a estos códigos: una regla por AR-B no alcanza a nadie si la ficha dice "Buenos Aires".
/// </summary>
public static class ArgentinaProvinces
{
    public static IReadOnlyDictionary<string, string> Table { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AR-C"] = "Ciudad Autónoma de Buenos Aires",
        ["AR-B"] = "Buenos Aires",
        ["AR-K"] = "Catamarca",
        ["AR-H"] = "Chaco",
        ["AR-U"] = "Chubut",
        ["AR-X"] = "Córdoba",
        ["AR-W"] = "Corrientes",
        ["AR-E"] = "Entre Ríos",
        ["AR-P"] = "Formosa",
        ["AR-Y"] = "Jujuy",
        ["AR-L"] = "La Pampa",
        ["AR-F"] = "La Rioja",
        ["AR-M"] = "Mendoza",
        ["AR-N"] = "Misiones",
        ["AR-Q"] = "Neuquén",
        ["AR-R"] = "Río Negro",
        ["AR-A"] = "Salta",
        ["AR-J"] = "San Juan",
        ["AR-D"] = "San Luis",
        ["AR-Z"] = "Santa Cruz",
        ["AR-S"] = "Santa Fe",
        ["AR-G"] = "Santiago del Estero",
        ["AR-V"] = "Tierra del Fuego",
        ["AR-T"] = "Tucumán",
    };

    /// <summary>Otras formas de escribirlas que aparecen en las fichas y en el padrón, ya plegadas (minúsculas, sin acentos ni espacios).</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["caba"] = "AR-C",
        ["capitalfederal"] = "AR-C",
        ["ciudadautonomabuenosaires"] = "AR-C",
        ["ciudadautonomadebuenosaires"] = "AR-C",
        ["ciudaddebuenosaires"] = "AR-C",
        ["buenosairesciudad"] = "AR-C",
        ["ciudadautonomaba"] = "AR-C",
        ["bsas"] = "AR-B",
        ["bsaires"] = "AR-B",
        ["pba"] = "AR-B",
        ["gba"] = "AR-B",
        ["granbuenosaires"] = "AR-B",
        ["buenosairesprovincia"] = "AR-B",
        ["provinciadebuenosaires"] = "AR-B",
        ["buenosairesprovince"] = "AR-B",
        ["cba"] = "AR-X",
        ["cordobacapital"] = "AR-X",
        ["santafecapital"] = "AR-S",
        ["santafeprovince"] = "AR-S",
        ["sgodelestero"] = "AR-G",
        ["tuc"] = "AR-T",
        ["tdf"] = "AR-V",
        ["tierradelfuegoantartidaeislasdelatlanticosur"] = "AR-V",
    };

    public static bool IsCode(string? value) => value is not null && Table.ContainsKey(value);

    /// <summary>El código de un texto: un código ya válido en cualquier caja, el nombre de la provincia o un alias. Null si no se reconoce.</summary>
    public static string? Code(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var upper = text.Trim().ToUpperInvariant();
        if (Table.ContainsKey(upper))
        {
            return upper;
        }

        var key = Fold(text);
        if (key.Length == 0)
        {
            return null;
        }

        foreach (var (code, name) in Table)
        {
            if (Fold(name) == key)
            {
                return code;
            }
        }

        return Aliases.GetValueOrDefault(key);
    }

    /// <summary>Minúsculas, sin acentos y solo letras: "Entre Ríos" y "entre rios" son lo mismo.</summary>
    private static string Fold(string text) => new([.. text.Normalize(NormalizationForm.FormD).ToLowerInvariant().Where(char.IsAsciiLetter)]);
}
