using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.Arca;

/// <summary>Ticket de acceso (TA) de WSAA para un servicio. Es lo que se guarda en instance_state.</summary>
internal sealed record Ticket(string Token, string Sign, DateTimeOffset ExpiresAt, string Cuit, string Environment);

/// <summary>WSAA: pedido de ticket firmado en CMS (especificación técnica 1.2.2 y manual del desarrollador de ARCA).</summary>
internal static class Wsaa
{
    private static readonly XNamespace Ns = "http://wsaa.view.sua.dvadac.desein.afip.gov";

    // Los códigos de la tabla de errores de la especificación: coe.*, cms.*, xml.*, wsn.* y wsaa.*.
    private static readonly Regex CodePattern = new(@"\b(?:coe|cms|xml|wsn|wsaa)\.[A-Za-z0-9]+(?:\.[A-Za-z0-9]+)*", RegexOptions.CultureInvariant);

    /// <summary>
    /// ARCA dice que cuando pasan pocos minutos desde el último ticket vigente no entrega otro. Hoy son 2 minutos en
    /// producción y 10 en homologación, y pueden cambiar sin aviso.
    /// </summary>
    public static int AlreadyAuthenticatedRetry(string environment) => environment == "production" ? 150 : 660;

    /// <summary>
    /// TRA sin source ni destination, como recomienda el manual del desarrollador. Hora de Argentina (UTC-3, sin
    /// horario de verano) y generationTime unos minutos atrás para tolerar relojes desfasados.
    /// </summary>
    public static string BuildTra(string service, DateTimeOffset now)
    {
        var local = now.ToOffset(TimeSpan.FromHours(-3));
        return new XElement(
            "loginTicketRequest",
            new XAttribute("version", "1.0"),
            new XElement(
                "header",
                new XElement("uniqueId", (uint)now.ToUnixTimeSeconds()),
                new XElement("generationTime", Stamp(local.AddMinutes(-10))),
                new XElement("expirationTime", Stamp(local.AddMinutes(10)))),
            new XElement("service", service)).ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>CMS SignedData con el TRA incluido (nodetach), firmado con el certificado, en Base64.</summary>
    public static string Sign(string tra, X509Certificate2 certificate)
    {
        var cms = new SignedCms(new ContentInfo(Encoding.UTF8.GetBytes(tra)));

        // SHA-256: es lo que usan el openssl y el SignedCms de los ejemplos del manual de ARCA.
        cms.ComputeSignature(new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1", "SHA256"),
            IncludeOption = X509IncludeOption.EndCertOnly,
        });
        return Convert.ToBase64String(cms.Encode());
    }

    public static XElement LoginRequest(string cmsBase64) =>
        new(Ns + "loginCms", new XElement(Ns + "in0", cmsBase64));

    /// <summary>Lee el loginTicketResponse, que viene como texto XML dentro de loginCmsReturn.</summary>
    public static Ticket ParseTicket(XElement response, string cuit, string environment, DateTimeOffset now)
    {
        XElement? ticket = null;
        try
        {
            if (response.Child("loginCmsReturn")?.Value is { Length: > 0 } inner)
            {
                ticket = Xml.Parse(inner).Root;
            }
        }
        catch (XmlException)
        {
        }

        var credentials = ticket?.Child("credentials");
        var token = credentials?.Text("token");
        var sign = credentials?.Text("sign");
        if (token is null
            || sign is null
            || !DateTimeOffset.TryParse(ticket?.Child("header")?.Text("expirationTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiresAt))
        {
            throw new ModuleException(ErrorCodes.UpstreamUnavailable, "WSAA devolvió un ticket que no se pudo leer.", 60);
        }

        if (expiresAt <= now)
        {
            throw new ModuleException(ErrorCodes.Internal, "WSAA devolvió un ticket ya vencido: revisar la hora del módulo.", 60);
        }

        return new Ticket(token, sign, expiresAt, cuit, environment);
    }

    public static bool IsAlreadyAuthenticated(SoapException ex) => CodeOf(ex) == "coe.alreadyAuthenticated";

    /// <summary>Convierte una falla de WSAA en el error del protocolo. Las de configuración no son reintentables.</summary>
    public static ModuleException MapFailure(SoapException ex, string environment, string service)
    {
        var code = CodeOf(ex);
        return code switch
        {
            "coe.alreadyAuthenticated" => new ModuleException(
                ErrorCodes.UpstreamUnavailable,
                $"ARCA dice que ya hay un ticket de acceso vigente para el servicio {service} y el módulo no lo tiene: "
                + "se perdió el estado de la instancia o otro sistema pide tickets con el mismo certificado. "
                + "ARCA suele aceptar uno nuevo a los 2 minutos en producción y a los 10 en homologación, "
                + "y si no, cuando venza el vigente (hasta 12 horas).",
                AlreadyAuthenticatedRetry(environment)),
            "coe.notAuthorized" => Config(
                $"El certificado no está autorizado a usar el servicio {service}. Asociarlo al servicio en el Administrador de Relaciones (producción) o en WSASS (homologación)."),
            "cms.cert.untrusted" => Config(
                "ARCA no confía en el certificado. En homologación hay que usar un certificado de homologación y en producción uno de producción."),
            "cms.cert.expired" => Config("El certificado venció."),
            not null when code.StartsWith("cms.", StringComparison.Ordinal) => Config(
                $"ARCA no aceptó la firma ({code}). Revisar que la clave privada sea la del certificado y que el ambiente sea el correcto."),
            "xml.generationTime.invalid" or "xml.expirationTime.expired" or "xml.expirationTime.invalid" => new ModuleException(
                ErrorCodes.Internal,
                $"La hora del módulo no coincide con la de ARCA ({code}). Sincronizar el reloj por NTP (time.afip.gov.ar) y revisar la zona horaria.",
                60),
            "wsaa.unavailable" or "wsn.unavailable" or "wsaa.internalError" => new ModuleException(
                ErrorCodes.UpstreamUnavailable,
                $"WSAA no está disponible ({code}).",
                60),
            not null => new ModuleException(
                ErrorCodes.Internal,
                $"WSAA rechazó el pedido de ticket ({code}): es un error del módulo.",
                60),
            _ => new ModuleException(ErrorCodes.UpstreamUnavailable, $"No se pudo obtener el ticket de WSAA: {ex.Message}", 60),
        };

        static ModuleException Config(string message) => new(ErrorCodes.InvalidConfig, message);
    }

    private static string? CodeOf(SoapException ex)
    {
        var text = $"{ex.FaultCode} {ex.Message}";
        var match = CodePattern.Match(text);
        if (match.Success)
        {
            return match.Value;
        }

        // Si el código no viene en el faultcode, el texto de la tabla del manual igual lo delata.
        return text.Contains("ya posee un TA", StringComparison.OrdinalIgnoreCase) ? "coe.alreadyAuthenticated" : null;
    }

    private static string Stamp(DateTimeOffset time) => time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}
