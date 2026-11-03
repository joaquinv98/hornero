using System.Net;
using System.Text.Json.Nodes;

namespace BillingOss.Module.Arca.Tests;

/// <summary>
/// Prueba contra la homologación real de ARCA. Solo corre con un certificado de homologación a mano:
/// ARCA_HOMO_CERT y ARCA_HOMO_KEY (el PEM o la ruta a su archivo) y ARCA_HOMO_CUIT. Si falta alguna, queda inconclusa.
/// Opcionales: ARCA_HOMO_PTOVTA y ARCA_HOMO_CBTE_TIPO (6 por defecto) para pedir también el último número.
/// </summary>
[TestClass]
public sealed class HomologationTests
{
    [TestMethod]
    [TestCategory("Homologacion")]
    public async Task Homologacion_PruebaLaInstanciaLasTablasYLaCotizacion()
    {
        var certificate = Variable("ARCA_HOMO_CERT");
        var key = Variable("ARCA_HOMO_KEY");
        var cuit = Variable("ARCA_HOMO_CUIT");
        if (certificate is null || key is null || cuit is null)
        {
            Assert.Inconclusive("Falta ARCA_HOMO_CERT, ARCA_HOMO_KEY o ARCA_HOMO_CUIT: no hay certificado de homologación para probar.");
        }

        var config = new JsonObject
        {
            ["cuit"] = cuit,
            ["environment"] = "homologation",
            ["certificate_pem"] = PemOrFile(certificate),
            ["private_key_pem"] = PemOrFile(key),
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        await using var host = await ArcaHost.Start(new FakeArca(), new TestClock(DateTimeOffset.UtcNow), http);

        // 1. FEDummy y un login de WSAA. El ticket vuelve en instance_state y las siguientes llamadas lo reusan.
        var (status, body) = await host.Send("/v0/instance/test", [], config: config);
        SkipIfTicketWasJustIssued(status, body);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        Assert.AreEqual("OK", body!["result"]!["servers"]!["app_server"]!.GetValue<string>());
        var state = body["instance_state"];
        Assert.IsNotNull(state, "El login tendría que haber dejado el ticket en instance_state.");

        // 2. Las tablas de ARCA.
        (status, body) = await host.Send("/v0/fiscal/parameters", [], state: state, config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        var result = body!["result"]!;
        Assert.IsTrue(result["voucher_types"]!.AsArray().Any(t => t!["id"]!.GetValue<int>() == 6), "Falta la factura B en los tipos de comprobante.");
        Assert.IsTrue(result["document_types"]!.AsArray().Any(t => t!["id"]!.GetValue<int>() == 80), "Falta el CUIT en los tipos de documento.");
        Assert.IsTrue(result["vat_rates"]!.AsArray().Any(t => t!["id"]!.GetValue<int>() == 5), "Falta la alícuota del 21 %.");
        Assert.IsTrue(result["currencies"]!.AsArray().Any(t => t!["id"]!.GetValue<string>() == "PES"), "Faltan los pesos en las monedas.");
        Assert.IsNotEmpty(result["receiver_vat_conditions"]!.AsArray());

        // 3. La cotización oficial del dólar.
        (status, body) = await host.Send("/v0/fiscal/exchange_rate", new JsonObject { ["currency"] = "DOL" }, state: state, config: config);
        Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
        Assert.IsGreaterThan(0m, decimal.Parse(body!["result"]!["rate"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));

        // 4. El último número autorizado, si se dio un punto de venta.
        if (Variable("ARCA_HOMO_PTOVTA") is { } pointOfSale)
        {
            var type = int.Parse(Variable("ARCA_HOMO_CBTE_TIPO") ?? "6", System.Globalization.CultureInfo.InvariantCulture);
            var input = new JsonObject { ["point_of_sale"] = int.Parse(pointOfSale, System.Globalization.CultureInfo.InvariantCulture), ["type_code"] = type };
            (status, body) = await host.Send("/v0/fiscal/last_number", input, state: state, config: config);
            Assert.AreEqual(HttpStatusCode.OK, status, Failure(body));
            Assert.IsGreaterThanOrEqualTo(0L, body!["result"]!["number"]!.GetValue<long>());
        }
    }

    private static string? Variable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    /// <summary>La variable puede traer el PEM o la ruta al archivo que lo contiene.</summary>
    private static string PemOrFile(string value) =>
        value.Contains("-----BEGIN", StringComparison.Ordinal) ? value : File.ReadAllText(value);

    /// <summary>Si ARCA ya dio un ticket hace pocos minutos y no lo guardamos, no hay nada que probar hasta que pase la espera.</summary>
    private static void SkipIfTicketWasJustIssued(HttpStatusCode status, JsonNode? body)
    {
        if (status == HttpStatusCode.ServiceUnavailable && body?["error"]?["message"]?.GetValue<string>().Contains("ticket de acceso vigente", StringComparison.Ordinal) == true)
        {
            Assert.Inconclusive("WSAA ya entregó un ticket para este certificado y esta prueba no lo tiene. Esperar unos 10 minutos y repetir.");
        }
    }

    /// <summary>El error de la respuesta, sin el instance_state: lleva el ticket y no se imprime.</summary>
    private static string Failure(JsonNode? body) => body?["error"]?.ToJsonString() ?? "(sin error en la respuesta)";
}
