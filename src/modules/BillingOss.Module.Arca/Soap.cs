using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BillingOss.Module.Arca;

/// <summary>Falla al hablar con un servicio SOAP. Sent dice si el pedido pudo haber llegado al servidor.</summary>
internal sealed class SoapException(string message, bool sent, string? faultCode = null) : Exception(message)
{
    public bool Sent { get; } = sent;

    public string? FaultCode { get; } = faultCode;
}

/// <summary>Cliente SOAP 1.1 mínimo con HttpClient y XDocument, sin WCF.</summary>
internal sealed class SoapClient(HttpClient http)
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";

    /// <summary>Manda el cuerpo y devuelve el primer elemento del Body de la respuesta. Un Fault sale como SoapException.</summary>
    public async Task<XElement> Call(Uri url, string action, XElement body, CancellationToken ct)
    {
        var envelope = new XElement(
            Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap", Soap),
            new XElement(Soap + "Body", body));
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{action}\"");

        HttpStatusCode status;
        string text;
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            status = response.StatusCode;
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SoapException(
                $"No se pudo comunicar con {url.Host}: {Short(ex.Message)}",
                sent: !BeforeSending(ex.HttpRequestError));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Venció el plazo del HttpClient: el pedido ya pudo haber salido.
            throw new SoapException($"{url.Host} no respondió a tiempo.", sent: true);
        }

        XElement? first;
        try
        {
            first = Xml.Parse(text).Root?.Child("Body")?.Elements().FirstOrDefault();
        }
        catch (XmlException)
        {
            throw new SoapException($"{url.Host} respondió HTTP {(int)status} con algo que no es XML.", sent: true);
        }

        if (first is null)
        {
            throw new SoapException($"{url.Host} respondió HTTP {(int)status} sin cuerpo SOAP.", sent: true);
        }

        if (first.Name.LocalName == "Fault")
        {
            throw new SoapException(
                $"{url.Host} respondió un SOAP Fault: {Short(first.Child("faultstring")?.Value)}",
                sent: true,
                faultCode: first.Child("faultcode")?.Value.Trim());
        }

        return first;
    }

    /// <summary>Errores de la etapa de conexión: ahí el pedido seguro no salió.</summary>
    private static bool BeforeSending(HttpRequestError error) =>
        error is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError;

    private static string Short(string? text) =>
        text is null ? string.Empty : text.Length <= 300 ? text.Trim() : text[..300].Trim() + "...";
}

/// <summary>
/// Atajos para leer XML de ARCA. Se ignoran los espacios de nombres y las mayúsculas porque los manuales y los WSDL no
/// coinciden entre sí (CondicionIVAReceptorId o CondicionIvaReceptorId, Cbte_Tipo o Cbte_tipo) y nunca hay dos hermanos
/// que se llamen igual salvo por eso.
/// </summary>
internal static class Xml
{
    /// <summary>Parseo sin DTD, por si alguna respuesta llegara armada para abusar de las entidades.</summary>
    public static XDocument Parse(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        return XDocument.Load(reader);
    }

    public static XElement? Child(this XElement element, string name) =>
        element.Elements().FirstOrDefault(e => Is(e, name));

    public static IEnumerable<XElement> Children(this XElement element, string name) =>
        element.Elements().Where(e => Is(e, name));

    private static bool Is(XElement element, string name) =>
        string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Texto del hijo, o null si no está o viene vacío.</summary>
    public static string? Text(this XElement element, string name)
    {
        var value = element.Child(name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Saca los caracteres que XML no admite, por ejemplo controles que llegan en un nombre de cliente.</summary>
    public static string Clean(string? text) =>
        text is null ? string.Empty : new string(text.Where(XmlConvert.IsXmlChar).ToArray()).Trim();
}
