using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace BillingOss.Module.Arca;

/// <summary>
/// Padrón de ARCA: constancia de inscripción (ws_sr_constancia_inscripcion, manual 4.1, método getPersona_v2).
/// Devuelve nombre o razón social, domicilio fiscal, impuestos con su estado y categoría de monotributo. Las
/// caracterizaciones no se leen: el núcleo guarda solo nombre, domicilio y condición (sección 2.7 del diseño fiscal).
/// </summary>
internal static class Padron
{
    /// <summary>Nombre del servicio que se pide a WSAA.</summary>
    public const string Service = "ws_sr_constancia_inscripcion";

    public const string Operation = "getPersona_v2";

    public static readonly XNamespace Ns = "http://a5.soap.ws.server.puc.sr/";

    /// <summary>Impuestos del padrón que deciden la condición sugerida: 30 es IVA, 20 monotributo y 32 IVA exento.</summary>
    private const int VatTax = 30;
    private const int MonotaxTax = 20;
    private const int VatExemptTax = 32;

    /// <summary>El pedido lleva los hijos sin espacio de nombres, como el WSDL del padrón (elementFormDefault unqualified).</summary>
    public static XElement BuildGetPersona(Ticket ticket, string cuit, string taxId) =>
        new(
            Ns + Operation,
            new XElement("token", ticket.Token),
            new XElement("sign", ticket.Sign),
            new XElement("cuitRepresentada", cuit),
            new XElement("idPersona", taxId));

    /// <summary>El padrón responde con un SOAP Fault cuando la CUIT no existe.</summary>
    public static bool IsMissingPerson(string message) => message.Contains("No existe persona", StringComparison.OrdinalIgnoreCase);

    public static JsonObject ReadPersona(XElement response, string taxId)
    {
        var persona = response.Child("personaReturn") ?? response;
        var general = persona.Child("datosGenerales");
        var address = general?.Child("domicilioFiscal");
        var regular = persona.Child("datosRegimenGeneral");
        var monotax = persona.Child("datosMonotributo");
        var blocked = persona.Child("errorConstancia");

        var taxes = (regular?.Children("impuesto") ?? []).Concat(monotax?.Children("impuesto") ?? [])
            .Select(i => (Id: int.TryParse(i.Text("idImpuesto"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0, Description: i.Text("descripcionImpuesto"), Since: i.Text("periodo")))
            .ToList();
        var ids = taxes.Select(t => t.Id).ToHashSet();
        int? suggested = ids.Contains(VatTax) ? 1 : ids.Contains(MonotaxTax) ? 6 : ids.Contains(VatExemptTax) ? 4 : null;

        var notes = new[] { blocked, persona.Child("errorRegimenGeneral"), persona.Child("errorMonotributo") }
            .Where(e => e is not null)
            .SelectMany(e => e!.Children("error").Select(x => x.Value.Trim()))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal);

        var name = general?.Text("razonSocial")
            ?? string.Join(" ", new[] { general?.Text("apellido"), general?.Text("nombre") }.Where(p => p is not null))
            .Trim();
        if (name.Length == 0 && blocked is not null)
        {
            name = string.Join(" ", new[] { blocked.Text("apellido"), blocked.Text("nombre") }.Where(p => p is not null)).Trim();
        }

        return new JsonObject
        {
            ["tax_id"] = taxId,
            ["name"] = name,
            ["person_type"] = general?.Text("tipoPersona"),
            ["status"] = general?.Text("estadoClave"),
            ["address"] = new JsonObject
            {
                ["street"] = address?.Text("direccion"),
                ["city"] = address?.Text("localidad"),
                ["postal_code"] = address?.Text("codPostal"),
                ["province"] = address?.Text("descripcionProvincia"),
                ["province_id"] = address?.Text("idProvincia"),
            },
            ["taxes"] = new JsonArray(taxes.Select(t => (JsonNode?)new JsonObject { ["id"] = t.Id, ["description"] = t.Description, ["since"] = t.Since }).ToArray()),
            ["activities"] = new JsonArray((regular?.Children("actividad") ?? []).Concat(monotax?.Children("actividadMonotributista") ?? [])
                .Select(a => (JsonNode?)new JsonObject { ["id"] = a.Text("idActividad"), ["description"] = a.Text("descripcionActividad") }).ToArray()),
            ["monotax_category"] = monotax?.Child("categoriaMonotributo")?.Text("descripcionCategoria"),
            ["suggested_vat_condition"] = suggested,
            ["blocked"] = blocked is not null,
            ["notes"] = new JsonArray(notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
        };
    }
}
