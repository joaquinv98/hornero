using System.Globalization;
using System.Text;
using System.Text.Json;
using Net.Codecrete.QrCodeGenerator;

namespace BillingOss.App.Documents;

/// <summary>Datos del comprobante que lleva el código QR, con los nombres y tipos de la especificación de ARCA (versión 1).</summary>
public sealed record FiscalQrData(
    DateOnly Date,
    long IssuerTaxId,
    int PointOfSale,
    int TypeCode,
    long Number,
    decimal Total,
    string Currency,
    decimal ExchangeRate,
    int? ReceiverDocType,
    long? ReceiverDocNumber,
    long AuthorizationCode,
    bool Caea = false);

/// <summary>
/// Código QR de los comprobantes electrónicos (RG 4892/2020). El texto es la URL de ARCA con el JSON del comprobante en
/// Base64 en el parámetro p, según la especificación publicada en https://www.afip.gob.ar/fe/qr/documentos/QRespecificaciones.pdf.
/// </summary>
public static class FiscalQr
{
    /// <summary>URL que fija la especificación vigente (2025).</summary>
    public const string BaseUrl = "https://www.arca.gob.ar/fe/qr/";

    /// <summary>JSON en el orden y con los tipos de la especificación: números como números y la fecha como texto.</summary>
    public static string Json(FiscalQrData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("ver", 1);
            w.WriteString("fecha", data.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            w.WriteNumber("cuit", data.IssuerTaxId);
            w.WriteNumber("ptoVta", data.PointOfSale);
            w.WriteNumber("tipoCmp", data.TypeCode);
            w.WriteNumber("nroCmp", data.Number);
            w.WriteNumber("importe", Normalize(data.Total, 2));
            w.WriteString("moneda", data.Currency);
            w.WriteNumber("ctz", Normalize(data.ExchangeRate, 6));
            if (data.ReceiverDocType is { } docType && data.ReceiverDocNumber is { } docNumber)
            {
                w.WriteNumber("tipoDocRec", docType);
                w.WriteNumber("nroDocRec", docNumber);
            }

            w.WriteString("tipoCodAut", data.Caea ? "A" : "E");
            w.WriteNumber("codAut", data.AuthorizationCode);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>El texto que codifica el QR: {URL}?p={JSON en Base64}.</summary>
    public static string Url(FiscalQrData data) => $"{BaseUrl}?p={Convert.ToBase64String(Encoding.UTF8.GetBytes(Json(data)))}";

    /// <summary>La imagen del QR como SVG en una URL data:, para que el PDF no salga a buscar nada.</summary>
    public static string ImageDataUri(string text)
    {
        var svg = QrCode.EncodeText(text, QrCode.Ecc.Medium).ToSvgString(2);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }

    /// <summary>Saca los ceros de más (12100.00 queda 12100), como en el ejemplo de la especificación.</summary>
    private static decimal Normalize(decimal value, int decimals) =>
        decimal.Parse(Math.Round(value, decimals, MidpointRounding.AwayFromZero).ToString("0.######", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
