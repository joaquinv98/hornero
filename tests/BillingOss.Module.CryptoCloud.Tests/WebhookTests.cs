using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.CryptoCloud.Tests;

/// <summary>
/// payment.verify_webhook: el JWT del aviso de pago (postback) y los eventos normalizados. El JWT de referencia lo
/// calculó aparte Python con HMAC-SHA256 sobre la cabecera y las claims del ejemplo oficial, y la clave es
/// CcHost.FakeSecretKey. Los demás los arma Jwt.Sign, que da lo mismo que Python para esas claims.
/// </summary>
[TestClass]
public sealed class WebhookTests
{
    private const string VerifyRoute = "/v0/payment/verify_webhook";

    // Cabecera {"typ":"JWT","alg":"HS256"} y claims {"id":13,"exp":1631786426}.
    private const string ReferenceJwt =
        "eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.eyJpZCI6MTMsImV4cCI6MTYzMTc4NjQyNn0.u1IjXkOvsSnIRyi6NPI6eu9hbBs8tn-r_NaWaqLyT8Q";

    /// <summary>Un token como el que manda CryptoCloud con cada aviso: vence a los 5 minutos.</summary>
    private static readonly string Fresh = Jwt.Sign(CcHost.FakeSecretKey, payload: Jwt.Claims());

    [TestMethod]
    public async Task AvisoEnJson_ConFirmaValida_DaElEventoNormalizado_SinSalirALaRed()
    {
        var cc = new FakeCryptoCloud();
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send(VerifyRoute, Input(JsonBody(Fresh)));

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.IsTrue(result["valid"]!.GetValue<bool>());
        var events = result["events"]!.AsArray();
        Assert.HasCount(1, events);
        Assert.AreEqual(EventId("INV-ILRAJE1Q", Fresh), events[0]!["event_id"]!.GetValue<string>());
        Assert.AreEqual("invoice.paid", events[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("INV-ILRAJE1Q", events[0]!["payment_external_id"]!.GetValue<string>());

        // Verificar es local. Y la clave no sale en la respuesta.
        Assert.IsEmpty(cc.Requests);
        Assert.DoesNotContain(CcHost.FakeSecretKey, body.ToJsonString());
    }

    [TestMethod]
    public async Task ElJwtSeArmaComoUnHs256Comun()
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());

        // Jwt.Sign da lo mismo que Python con HMAC directo, y lo que firma pasa con un exp vigente.
        Assert.AreEqual(ReferenceJwt, Jwt.Sign(CcHost.FakeSecretKey));
        var (_, body) = await host.Send(VerifyRoute, Input(JsonBody(Fresh)));
        Assert.IsTrue(body!["result"]!["valid"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task AvisoComoFormulario_SeLeeIgual()
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());
        var form = "status=success&invoice_id=ILRAJE1Q&amount_crypto=0.000113&currency=BNB&order_id=att-1042&token=" + Uri.EscapeDataString(Fresh);

        var (_, body) = await host.Send(VerifyRoute, Input(form));

        Assert.IsTrue(body!["result"]!["valid"]!.GetValue<bool>());
        var e = body["result"]!["events"]![0]!;
        Assert.AreEqual(EventId("INV-ILRAJE1Q", Fresh), e["event_id"]!.GetValue<string>());
        Assert.AreEqual("INV-ILRAJE1Q", e["payment_external_id"]!.GetValue<string>());

        // El formulario no trae el estado de la factura: solo se documenta el aviso de pago confirmado.
        Assert.AreEqual("invoice.paid", e["type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ElEstadoDeLaFactura_VaEnElTipo()
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());

        var partial = await host.Send(VerifyRoute, Input(JsonBody(Fresh, state: "partial")));
        var paid = await host.Send(VerifyRoute, Input(JsonBody(Fresh, state: "paid")));
        var odd = await host.Send(VerifyRoute, Input(JsonBody(Fresh, state: "estado-raro-de-mas-de-veinte-letras")));

        Assert.AreEqual("invoice.partial", partial.Body!["result"]!["events"]![0]!["type"]!.GetValue<string>());
        Assert.AreEqual("invoice.paid", paid.Body!["result"]!["events"]![0]!["type"]!.GetValue<string>());
        Assert.AreEqual("invoice.paid", odd.Body!["result"]!["events"]![0]!["type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ElIdDelEvento_SaleDelToken_YUnTokenRepetidoNoOcupaElDeUnAvisoFuturo()
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());
        var later = Jwt.Sign(CcHost.FakeSecretKey, payload: Jwt.Claims(400));

        // El mismo aviso reenviado con el mismo token es el mismo evento.
        var first = await EventIdOf(host, JsonBody(Fresh));
        var again = await EventIdOf(host, JsonBody(Fresh));

        // Otro aviso de la misma factura, por ejemplo el del pago completo después de uno parcial, es otro evento
        // aunque el formulario no traiga el estado.
        var next = await EventIdOf(host, JsonBody(later));

        // Con un token filtrado alguien puede armar un aviso de otra factura, pero su ID no es el del aviso real,
        // que llega con un token nuevo.
        var forged = await EventIdOf(host, JsonBody(Fresh, invoice: "OTRA0001"));
        var real = await EventIdOf(host, JsonBody(later, invoice: "OTRA0001"));

        Assert.AreEqual(first, again);
        Assert.AreNotEqual(first, next);
        Assert.AreNotEqual(forged, real);
        Assert.StartsWith("INV-OTRA0001:", forged);
    }

    [TestMethod]
    [DataRow(-6 * 86400L, true)]
    [DataRow(-8 * 86400L, false)]
    [DataRow(300L, true)]
    public async Task UnTokenVencido_ValeHastaSieteDias(long expFromNow, bool valid)
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());

        // CryptoCloud vence el token a los 5 minutos, pero el núcleo verifica en un trabajo que puede demorar.
        var (_, body) = await host.Send(VerifyRoute, Input(JsonBody(Jwt.Sign(CcHost.FakeSecretKey, payload: Jwt.Claims(expFromNow)))));

        Assert.AreEqual(valid, body!["result"]!["valid"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task ElTokenDeReferenciaDe2021_YaNoVale_YSinExpAlcanzaConLaFirma()
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());

        var old = await host.Send(VerifyRoute, Input(JsonBody(ReferenceJwt)));
        var noExp = await host.Send(VerifyRoute, Input(JsonBody(Jwt.Sign(CcHost.FakeSecretKey, payload: """{"id":13}"""))));

        AssertRejected(old.Body);
        Assert.IsTrue(noExp.Body!["result"]!["valid"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task FirmaMala_NoEsValidaYNoTraeEventos()
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());
        var parts = Fresh.Split('.');
        var signature = Base64Url.DecodeFromChars(parts[2]);
        signature[0] ^= 1;
        var tampered = $"{parts[0]}.{parts[1]}.{Base64Url.EncodeToString(signature)}";

        var (status, body) = await host.Send(VerifyRoute, Input(JsonBody(tampered)));

        Assert.AreEqual(HttpStatusCode.OK, status);
        AssertRejected(body);
    }

    [TestMethod]
    public async Task ClaimsCambiadas_NoEsValida()
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());
        var parts = Fresh.Split('.');
        var forged = $"{parts[0]}.{Jwt.Part(Jwt.Claims(600))}.{parts[2]}";

        var (_, body) = await host.Send(VerifyRoute, Input(JsonBody(forged)));

        AssertRejected(body);
    }

    [TestMethod]
    public async Task ConOtraSecretKey_NoEsValida()
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());
        var config = CcHost.Config();
        config["secret_key"] = "otra-clave-de-prueba";

        var (_, body) = await host.Send(VerifyRoute, Input(JsonBody(Fresh)), config: config);

        AssertRejected(body);
    }

    [TestMethod]
    [DataRow("""{"typ":"JWT","alg":"none"}""")]
    [DataRow("""{"typ":"JWT","alg":"HS512"}""")]
    [DataRow("""{"typ":"JWT"}""")]
    public async Task SoloSeAceptaHs256(string header)
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());

        // Firmado con la clave correcta pero declarando otro algoritmo: no pasa igual.
        var (_, body) = await host.Send(VerifyRoute, Input(JsonBody(Jwt.Sign(CcHost.FakeSecretKey, header: header, payload: Jwt.Claims()))));

        AssertRejected(body);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("no-es-un-jwt")]
    [DataRow("a.b")]
    [DataRow("a.b.c.d")]
    [DataRow("###.###.###")]
    [DataRow(".eyJpZCI6MTN9.")]
    public async Task TokenQueNoEsUnJwt_NoEsValido(string token)
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());

        var (status, body) = await host.Send(VerifyRoute, Input(JsonBody(token)));

        Assert.AreEqual(HttpStatusCode.OK, status);
        AssertRejected(body);
    }

    [TestMethod]
    public async Task UnaCabeceraQueNoEsUtf8_NoEsValida_NiUnError()
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());

        // {"alg":"\xC3("}: JSON que se parsea pero con un texto que no es UTF-8. Cualquiera lo puede mandar sin la
        // clave, y un error acá haría reintentar la verificación en vez de descartarla.
        byte[] header = [.. Encoding.UTF8.GetBytes("{\"alg\":\""), 0xC3, 0x28, .. Encoding.UTF8.GetBytes("\"}")];
        var token = $"{Base64Url.EncodeToString(header)}.{Jwt.Part(Jwt.Claims())}.firma";

        var (status, body) = await host.Send(VerifyRoute, Input(JsonBody(token)));

        Assert.AreEqual(HttpStatusCode.OK, status);
        AssertRejected(body);
    }

    [TestMethod]
    public async Task SinToken_NoEsValido()
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());
        var json = JsonNode.Parse(JsonBody(Fresh))!.AsObject();
        json.Remove("token");

        var withoutToken = await host.Send(VerifyRoute, Input(json.ToJsonString()));
        var emptyBody = await host.Send(VerifyRoute, Input(string.Empty));
        var noBody = await host.Send(VerifyRoute, []);
        var notBase64 = await host.Send(VerifyRoute, new JsonObject { ["body_base64"] = "###no-es-base64" });

        foreach (var (status, body) in new[] { withoutToken, emptyBody, noBody, notBase64 })
        {
            Assert.AreEqual(HttpStatusCode.OK, status);
            AssertRejected(body);
        }
    }

    [TestMethod]
    public async Task ConFirmaValidaYSinUnaFacturaReconocible_ValePeroNoDaEventos()
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());
        var json = JsonNode.Parse(JsonBody(Fresh))!.AsObject();
        json["invoice_id"] = "../otra cosa";
        json.Remove("invoice_info");

        var (_, body) = await host.Send(VerifyRoute, Input(json.ToJsonString()));

        Assert.IsTrue(body!["result"]!["valid"]!.GetValue<bool>());
        Assert.IsEmpty(body["result"]!["events"]!.AsArray());
    }

    [TestMethod]
    public async Task SinInvoiceId_UsaElUuidDeInvoiceInfo()
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());
        var json = JsonNode.Parse(JsonBody(Fresh))!.AsObject();
        json.Remove("invoice_id");

        var (_, body) = await host.Send(VerifyRoute, Input(json.ToJsonString()));

        Assert.AreEqual("INV-ILRAJE1Q", body!["result"]!["events"]![0]!["payment_external_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task SinLaSecretKey_EsInvalidConfig()
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());
        var config = CcHost.Config();
        config["secret_key"] = " ";

        var (status, body) = await host.Send(VerifyRoute, Input(JsonBody(Fresh)), config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, CcHost.Code(body));
        Assert.AreEqual("secret_key", body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
    }

    /// <summary>El ID que se espera: la factura y los primeros 16 hex del SHA-256 de la firma del token.</summary>
    private static string EventId(string invoice, string jwt) =>
        $"{invoice}:{Convert.ToHexStringLower(SHA256.HashData(Base64Url.DecodeFromChars(jwt.Split('.')[2])))[..16]}";

    private static async Task<string> EventIdOf(CcHost host, string body)
    {
        var (_, response) = await host.Send(VerifyRoute, Input(body));
        return response!["result"]!["events"]![0]!["event_id"]!.GetValue<string>();
    }

    /// <summary>El aviso en JSON con la forma del ejemplo oficial, abreviado.</summary>
    private static string JsonBody(string jwt, string state = "paid", string invoice = "ILRAJE1Q") => new JsonObject
    {
        ["status"] = "success",
        ["invoice_id"] = invoice,
        ["amount_crypto"] = 0.000113,
        ["currency"] = "BNB",
        ["order_id"] = "att-1042",
        ["token"] = jwt,
        ["invoice_info"] = new JsonObject
        {
            ["uuid"] = $"INV-{invoice}",
            ["status"] = state,
            ["invoice_status"] = "success",
            ["amount_paid"] = 0.000113,
            ["fee"] = 0,
        },
    }.ToJsonString();

    /// <summary>El aviso tal como llegó al núcleo: encabezados, parámetros de la URL y cuerpo en base64.</summary>
    private static JsonObject Input(string body) => new()
    {
        ["headers"] = new JsonObject { ["content-type"] = "application/json" },
        ["query"] = new JsonObject(),
        ["body_base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(body)),
    };

    private static void AssertRejected(JsonNode? body)
    {
        Assert.IsFalse(body!["result"]!["valid"]!.GetValue<bool>());
        Assert.IsEmpty(body["result"]!["events"]!.AsArray());
    }
}
