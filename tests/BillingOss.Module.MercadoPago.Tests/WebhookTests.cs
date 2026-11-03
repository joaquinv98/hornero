using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.MercadoPago.Tests;

/// <summary>
/// payment.verify_webhook: la firma x-signature y los eventos normalizados. Las firmas de referencia las calculó
/// aparte Python con HMAC-SHA256 sobre el texto armado a mano, y la clave es MpHost.FakeWebhookSecret.
/// </summary>
[TestClass]
public sealed class WebhookTests
{
    private const string RequestId = "bb56a2f1-6aae-46ac-982e-9dcd3581d08e";
    private const string Ts = "1742505638683";

    // id:123456789;request-id:bb56a2f1-6aae-46ac-982e-9dcd3581d08e;ts:1742505638683;
    private const string SignedAll = "80bf6562bfe642a9c025553d8cec9858891781de618f8acbbcbe9852f1837cae";

    // id:123456789;ts:1742505638683;
    private const string SignedWithoutRequestId = "2f257461df2ce318f164e23107870d0ba994eceb056293499f0ebf5e8b38949a";

    // id:abc123def;request-id:bb56a2f1-6aae-46ac-982e-9dcd3581d08e;ts:1742505638683;
    private const string SignedAlphanumeric = "be4c33baefac58ad916cfa4e3da8943776c0c74cc456db5c6cb488c6589db33f";

    private const string Body = """
        {
          "action": "payment.updated",
          "api_version": "v1",
          "data": { "id": "123456789" },
          "date_created": "2026-10-25T15:01:10.000-04:00",
          "id": 117284521731,
          "live_mode": true,
          "type": "payment",
          "user_id": "44444"
        }
        """;

    [TestMethod]
    public async Task FirmaValida_DaElEventoNormalizado_SinSalirALaRed()
    {
        var mp = new FakeMercadoPago();
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send(VerifyRoute, Input(Header(Ts, SignedAll)));

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.IsTrue(result["valid"]!.GetValue<bool>());
        var events = result["events"]!.AsArray();
        Assert.HasCount(1, events);
        Assert.AreEqual($"123456789:{Ts}", events[0]!["event_id"]!.GetValue<string>());
        Assert.AreEqual("payment.updated", events[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("123456789", events[0]!["payment_external_id"]!.GetValue<string>());

        // Verificar es local. Y la clave no sale en la respuesta.
        Assert.IsEmpty(mp.Requests);
        Assert.DoesNotContain(MpHost.FakeWebhookSecret, body.ToJsonString());
    }

    [TestMethod]
    public async Task ElIdDelCuerpoNoEstaFirmado_RepetirLaFirmaConOtroIdNoEsOtroEvento()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());
        var replayed = Body.Replace("117284521731", "222", StringComparison.Ordinal);

        var original = await host.Send(VerifyRoute, Input(Header(Ts, SignedAll)));
        var replay = await host.Send(VerifyRoute, Input(Header(Ts, SignedAll), body: replayed));

        // Una notificación capturada con el id del cuerpo cambiado es la misma notificación: mismo event_id.
        Assert.IsTrue(replay.Body!["result"]!["valid"]!.GetValue<bool>());
        Assert.AreEqual(original.Body!["result"]!["events"]![0]!["event_id"]!.GetValue<string>(), replay.Body["result"]!["events"]![0]!["event_id"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("2025-03-21T20:00:00Z", true)]
    [DataRow("2025-03-22T00:00:00Z", false)]
    public async Task ConReceivedAt_UnTsDeMasDeUnDiaAntes_NoEsValida(string receivedAt, bool valid)
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());

        // Ts es 2025-03-20T21:20:38Z en milisegundos. El núcleo manda cuándo llegó el aviso.
        var input = Input(Header(Ts, SignedAll));
        input["received_at"] = receivedAt;

        var (status, body) = await host.Send(VerifyRoute, input);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(valid, body!["result"]!["valid"]!.GetValue<bool>());
        Assert.AreEqual(valid ? 1 : 0, body["result"]!["events"]!.AsArray().Count);
    }

    [TestMethod]
    [DataRow("2025-03-21T20:00:00Z", true)]
    [DataRow("2025-03-23T00:00:00Z", false)]
    public async Task ConReceivedAt_ElTsEnSegundosTambienSeMide(string receivedAt, bool valid)
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());

        // El mismo instante que Ts, en segundos, como lo muestra la documentación de Mercado Pago.
        const string seconds = "1742505638";
        var input = Input(Header(seconds, Sign($"id:123456789;request-id:{RequestId};ts:{seconds};")));
        input["received_at"] = receivedAt;

        var (_, body) = await host.Send(VerifyRoute, input);

        Assert.AreEqual(valid, body!["result"]!["valid"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task SinReceivedAt_NoHayVentanaDeTiempo()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());

        // Un núcleo viejo no manda received_at: la firma de 2025 sigue valiendo.
        var (_, body) = await host.Send(VerifyRoute, Input(Header(Ts, SignedAll)));

        Assert.IsTrue(body!["result"]!["valid"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task ElTextoFirmadoSeArmaComoDiceMercadoPago()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());

        // Lo mismo que las firmas de referencia, pero calculado acá con HMAC directo sobre el texto armado a mano.
        var signature = Sign($"id:123456789;request-id:{RequestId};ts:{Ts};");

        Assert.AreEqual(SignedAll, signature);
        var (_, body) = await host.Send(VerifyRoute, Input(Header(Ts, signature)));
        Assert.IsTrue(body!["result"]!["valid"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task FirmaMala_NoEsValidaYNoTraEventos()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());
        var tampered = SignedAll[..^1] + (SignedAll[^1] == '0' ? '1' : '0');

        var (status, body) = await host.Send(VerifyRoute, Input(Header(Ts, tampered)));

        Assert.AreEqual(HttpStatusCode.OK, status);
        AssertRejected(body);
    }

    [TestMethod]
    public async Task TimestampAlterado_NoEsValida()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());

        var (_, body) = await host.Send(VerifyRoute, Input(Header("1742505638684", SignedAll)));

        AssertRejected(body);
    }

    [TestMethod]
    public async Task ConOtraClaveDeWebhooks_NoEsValida()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());
        var config = MpHost.Config();
        config["webhook_secret"] = "otra-clave-de-prueba";

        var (_, body) = await host.Send(VerifyRoute, Input(Header(Ts, SignedAll)), config: config);

        AssertRejected(body);
    }

    [TestMethod]
    public async Task ElDataIdDeLaUrlEstaFirmado_SiCambiaNoEsValida()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());

        var (_, body) = await host.Send(VerifyRoute, Input(Header(Ts, SignedAll), dataId: "123456780"));

        AssertRejected(body);
    }

    [TestMethod]
    public async Task ElCuerpoNoEstaFirmado_SiHablaDeOtroPagoNoEsValida()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());
        var other = Body.Replace("\"123456789\"", "\"999000111\"", StringComparison.Ordinal);

        var (_, body) = await host.Send(VerifyRoute, Input(Header(Ts, SignedAll), body: other));

        AssertRejected(body);
    }

    [TestMethod]
    public async Task SinEncabezados_NoEsValida()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());

        var withoutSignature = Input(Header(Ts, SignedAll));
        withoutSignature["headers"]!.AsObject().Remove("x-signature");
        var withoutV1 = Input($"ts={Ts}");
        var withoutTs = Input($"v1={SignedAll}");
        var empty = Input(string.Empty);
        var withoutRequestId = Input(Header(Ts, SignedAll), requestId: null);
        var noHeaders = Input(Header(Ts, SignedAll));
        noHeaders.Remove("headers");

        foreach (var input in new[] { withoutSignature, withoutV1, withoutTs, empty, withoutRequestId, noHeaders })
        {
            var (status, body) = await host.Send(VerifyRoute, input);
            Assert.AreEqual(HttpStatusCode.OK, status);
            AssertRejected(body);
        }
    }

    [TestMethod]
    public async Task SinXRequestId_SeFirmaSinEl()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());

        // Lo que no llegó se quita del texto firmado: sin request-id, el texto es solo id y ts.
        var (_, body) = await host.Send(VerifyRoute, Input(Header(Ts, SignedWithoutRequestId), requestId: null));

        Assert.IsTrue(body!["result"]!["valid"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task ElIdAlfanumericoSeFirmaEnMinusculas_YNoEsUnPagoParaConsultar()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());
        var other = Body.Replace("\"123456789\"", "\"ABC123DEF\"", StringComparison.Ordinal);

        var (_, body) = await host.Send(VerifyRoute, Input(Header(Ts, SignedAlphanumeric), dataId: "ABC123DEF", body: other));

        // La firma vale, pero un pago de Mercado Pago tiene un id numérico: no hay qué consultar.
        Assert.IsTrue(body!["result"]!["valid"]!.GetValue<bool>());
        Assert.IsEmpty(body["result"]!["events"]!.AsArray());
    }

    [TestMethod]
    public async Task OtroTema_SeVerificaLaFirmaYNoDaEventos()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());
        var signature = Sign($"id:5555;request-id:{RequestId};ts:{Ts};");
        var order = """{ "action": "created", "id": 777, "data": { "id": "5555" }, "type": "topic_merchant_order_wh" }""";

        var (_, body) = await host.Send(VerifyRoute, Input(Header(Ts, signature), dataId: "5555", body: order, type: "topic_merchant_order_wh"));

        Assert.IsTrue(body!["result"]!["valid"]!.GetValue<bool>());
        Assert.IsEmpty(body["result"]!["events"]!.AsArray());
    }

    [TestMethod]
    public async Task EncabezadosYParametrosEnCualquierCasoYEnListas_SeLeenIgual()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());
        var input = new JsonObject
        {
            ["headers"] = new JsonObject
            {
                ["X-Signature"] = new JsonArray(Header(Ts, SignedAll)),
                ["X-Request-Id"] = new JsonArray(RequestId),
            },
            ["query"] = new JsonObject { ["data.id"] = new JsonArray("123456789"), ["type"] = new JsonArray("payment") },
            ["body_base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Body)),
        };

        var (_, body) = await host.Send(VerifyRoute, input);

        Assert.IsTrue(body!["result"]!["valid"]!.GetValue<bool>());
        Assert.AreEqual("123456789", body["result"]!["events"]![0]!["payment_external_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ConElCuerpoIlegible_AlcanzaLoFirmado()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());
        var input = Input(Header(Ts, SignedAll));
        input["body_base64"] = "###no-es-base64";

        var (_, body) = await host.Send(VerifyRoute, input);

        Assert.IsTrue(body!["result"]!["valid"]!.GetValue<bool>());
        var received = body["result"]!["events"]![0]!;
        Assert.AreEqual("123456789", received["payment_external_id"]!.GetValue<string>());

        // El evento se identifica por lo firmado: el pago y el ts.
        Assert.AreEqual($"123456789:{Ts}", received["event_id"]!.GetValue<string>());
        Assert.AreEqual("payment", received["type"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("[1,2,3]")]
    [DataRow("\"texto\"")]
    [DataRow("null")]
    [DataRow("")]
    public async Task ConUnCuerpoQueNoEsUnObjeto_AlcanzaLoFirmado_SinFallar(string text)
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());
        var input = Input(Header(Ts, SignedAll));
        input["body_base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

        var (status, body) = await host.Send(VerifyRoute, input);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.IsTrue(body!["result"]!["valid"]!.GetValue<bool>());
        Assert.AreEqual($"123456789:{Ts}", body["result"]!["events"]![0]!["event_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ElMismoEventoReenviado_TieneElMismoId()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());

        var first = await host.Send(VerifyRoute, Input(Header(Ts, SignedAll)));
        var second = await host.Send(VerifyRoute, Input(Header(Ts, SignedAll)));

        Assert.AreEqual(first.Body!["result"]!["events"]!.ToJsonString(), second.Body!["result"]!["events"]!.ToJsonString());
    }

    [TestMethod]
    public async Task SinLaClaveDeLosWebhooks_EsInvalidConfig()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());
        var config = MpHost.Config();
        config["webhook_secret"] = " ";

        var (status, body) = await host.Send(VerifyRoute, Input(Header(Ts, SignedAll)), config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, MpHost.Code(body));
    }

    private const string VerifyRoute = "/v0/payment/verify_webhook";

    private static string Header(string ts, string v1) => $"ts={ts},v1={v1}";

    /// <summary>La notificación tal como llegó al núcleo: encabezados, parámetros de la URL y cuerpo en base64.</summary>
    private static JsonObject Input(string signature, string? requestId = RequestId, string dataId = "123456789", string body = Body, string type = "payment")
    {
        var headers = new JsonObject { ["x-signature"] = signature, ["content-type"] = "application/json" };
        if (requestId is not null)
        {
            headers["x-request-id"] = requestId;
        }

        return new JsonObject
        {
            ["headers"] = headers,
            ["query"] = new JsonObject { ["data.id"] = dataId, ["type"] = type },
            ["body_base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(body)),
        };
    }

    private static string Sign(string text) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(MpHost.FakeWebhookSecret), Encoding.UTF8.GetBytes(text)));

    private static void AssertRejected(JsonNode? body)
    {
        Assert.IsFalse(body!["result"]!["valid"]!.GetValue<bool>());
        Assert.IsEmpty(body["result"]!["events"]!.AsArray());
    }
}
