using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.MercadoPago.Tests;

/// <summary>payment.subscriptions y payment.subscription_update: el débito automático que administra Mercado Pago.</summary>
[TestClass]
public sealed class SubscriptionTests
{
    private const string SubscriptionId = "2c9380847e9b451c017ea1bd70ba0219";
    private const string Get = "GET /preapproval/" + SubscriptionId;
    private const string Put = "PUT /preapproval/" + SubscriptionId;

    [TestMethod]
    public async Task Crear_PendienteDeAprobacion_ConLaReferenciaDelNucleoYElCorreoDelPagador()
    {
        var mp = new FakeMercadoPago().On("POST /preapproval", Preapproval("pending"), HttpStatusCode.Created);
        await using var host = await MpHost.Start(mp);

        var (status, body) = await Send(host, "/v0/payment/create_subscription", Input(), "subscription-4");

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.AreEqual(SubscriptionId, result["subscription_external_id"]!.GetValue<string>());
        Assert.AreEqual("pending", result["status"]!.GetValue<string>());
        Assert.AreEqual($"https://www.mercadopago.com.ar/subscriptions/checkout?preapproval_id={SubscriptionId}", result["redirect_url"]!.GetValue<string>());

        var sent = mp.Single("POST /preapproval");
        Assert.AreEqual("subscription-4", sent.Headers["X-Idempotency-Key"]);
        var preapproval = sent.Body!;
        Assert.AreEqual("sub-ref-4", preapproval["external_reference"]!.GetValue<string>());
        Assert.AreEqual("cliente@example.com", preapproval["payer_email"]!.GetValue<string>());
        Assert.AreEqual("pending", preapproval["status"]!.GetValue<string>());
        Assert.AreEqual("https://facturacion.example.com/cuenta/medios-de-pago", preapproval["back_url"]!.GetValue<string>());
        var recurring = preapproval["auto_recurring"]!;
        Assert.AreEqual(1, recurring["frequency"]!.GetValue<int>());
        Assert.AreEqual("months", recurring["frequency_type"]!.GetValue<string>());
        Assert.AreEqual("2026-11-05T12:00:00.000Z", recurring["start_date"]!.GetValue<string>());
        Assert.AreEqual(12100m, recurring["transaction_amount"]!.GetValue<decimal>());
        Assert.AreEqual("ARS", recurring["currency_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Crear_SinElCorreoDelPagadorOConDatosInvalidos_NoLlamaAMercadoPago()
    {
        var mp = new FakeMercadoPago();
        await using var host = await MpHost.Start(mp);
        var input = (JsonObject)JsonNode.Parse("""
            { "subscription_id": "", "amount": { "value": "10.123", "currency": "ARS" }, "interval": "week", "interval_count": 0, "reason": " ", "back_url": "ftp://x" }
            """)!;

        var (status, body) = await host.Send("/v0/payment/create_subscription", input, "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        CollectionAssert.AreEquivalent(
            new[] { "subscription_id", "amount", "interval", "interval_count", "start_date", "reason", "back_url", "context.account.contact.email" },
            MpHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(mp.Requests);
    }

    [TestMethod]
    public async Task Crear_SinUrlHttpsParaAprobar_EsIncierto()
    {
        var mp = new FakeMercadoPago().On("POST /preapproval", Preapproval("pending").Replace("https://", "http://", StringComparison.Ordinal), HttpStatusCode.Created);
        await using var host = await MpHost.Start(mp);

        var (_, body) = await Send(host, "/v0/payment/create_subscription", Input(), "k");

        Assert.AreEqual(ErrorCodes.OutcomeUnknown, MpHost.Code(body));
    }

    [TestMethod]
    [DataRow("pending", "pending")]
    [DataRow("authorized", "active")]
    [DataRow("paused", "paused")]
    [DataRow("cancelled", "cancelled")]
    public async Task Consultar_TraduceElEstado(string gatewayStatus, string expected)
    {
        var mp = new FakeMercadoPago().On(Get, Preapproval(gatewayStatus));
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send("/v0/payment/get_subscription", Ref());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(expected, body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual("12100.00", body["result"]!["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("sub-ref-4", body["result"]!["external_reference"]!.GetValue<string>());
        Assert.AreEqual("2026-11-05T15:00:00Z", body["result"]!["next_charge_at"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Consultar_EstadoDesconocidoOIdInvalido()
    {
        var mp = new FakeMercadoPago().On(Get, Preapproval("inventado"));
        await using var host = await MpHost.Start(mp);

        var (_, body) = await host.Send("/v0/payment/get_subscription", Ref());
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, MpHost.Code(body));

        (_, body) = await host.Send("/v0/payment/get_subscription", new JsonObject { ["subscription_external_id"] = "../v1/payments" });
        Assert.AreEqual(ErrorCodes.InvalidInput, MpHost.Code(body));
        Assert.AreEqual(1, mp.Requests.Count);
    }

    [TestMethod]
    public async Task Cancelar_PoneElEstado_YSiYaEstabaCanceladaNoVuelveAPedirlo()
    {
        var mp = new FakeMercadoPago().On(Get, Preapproval("authorized")).On(Put, Preapproval("cancelled"));
        await using var host = await MpHost.Start(mp);

        var (_, body) = await host.Send("/v0/payment/cancel_subscription", Ref(), "subscription-4-cancel");
        Assert.AreEqual("cancelled", body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual("cancelled", mp.Single(Put).Body!["status"]!.GetValue<string>());
        Assert.AreEqual("subscription-4-cancel", mp.Single(Put).Headers["X-Idempotency-Key"]);

        mp.On(Get, Preapproval("cancelled"));
        (_, body) = await host.Send("/v0/payment/cancel_subscription", Ref(), "subscription-4-cancel-2");
        Assert.AreEqual("cancelled", body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual(1, mp.Count(Put));
    }

    [TestMethod]
    public async Task CambiarElMonto_LoMandaEnLaMoneda()
    {
        var mp = new FakeMercadoPago().On(Put, Preapproval("authorized", "15000"));
        await using var host = await MpHost.Start(mp);
        var input = Ref();
        input["amount"] = new JsonObject { ["value"] = "15000.00", ["currency"] = "ARS" };

        var (status, body) = await host.Send("/v0/payment/update_subscription", input, "subscription-4-amount-1500000");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("15000.00", body!["result"]!["amount"]!["value"]!.GetValue<string>());
        var recurring = mp.Single(Put).Body!["auto_recurring"]!;
        Assert.AreEqual(15000m, recurring["transaction_amount"]!.GetValue<decimal>());
        Assert.AreEqual("ARS", recurring["currency_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task AvisoDeSuscripcion_DaElEventoConLaSuscripcion()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());
        const string ts = "1742505638683";
        const string requestId = "bb56a2f1-6aae-46ac-982e-9dcd3581d08e";
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(MpHost.FakeWebhookSecret),
            Encoding.UTF8.GetBytes($"id:{SubscriptionId};request-id:{requestId};ts:{ts};")));
        var notification = $$"""{ "action": "updated", "entity": "preapproval", "id": 991, "data": { "id": "{{SubscriptionId}}" }, "type": "subscription_preapproval" }""";
        var input = new JsonObject
        {
            ["headers"] = new JsonObject { ["x-signature"] = $"ts={ts},v1={signature}", ["x-request-id"] = requestId },
            ["query"] = new JsonObject { ["data.id"] = SubscriptionId, ["type"] = "subscription_preapproval" },
            ["body_base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(notification)),
        };

        var (_, body) = await host.Send("/v0/payment/verify_webhook", input);

        Assert.IsTrue(body!["result"]!["valid"]!.GetValue<bool>());
        var e = body["result"]!["events"]!.AsArray().Single()!;
        Assert.AreEqual($"subscription:{SubscriptionId}:{ts}", e["event_id"]!.GetValue<string>());
        Assert.AreEqual("subscription.updated", e["type"]!.GetValue<string>());
        Assert.AreEqual(SubscriptionId, e["subscription_external_id"]!.GetValue<string>());
        Assert.IsNull(e["payment_external_id"]);
    }

    [TestMethod]
    public async Task AvisoDeSuscripcion_SoloValeLoFirmado()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());
        const string ts = "1742505638683";
        const string requestId = "bb56a2f1-6aae-46ac-982e-9dcd3581d08e";

        JsonObject Input(string? signedId, string bodyId, int notificationId)
        {
            var manifest = signedId is null ? $"request-id:{requestId};ts:{ts};" : $"id:{signedId};request-id:{requestId};ts:{ts};";
            var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(MpHost.FakeWebhookSecret), Encoding.UTF8.GetBytes(manifest)));
            var notification = $$"""{ "id": {{notificationId}}, "data": { "id": "{{bodyId}}" }, "type": "subscription_preapproval" }""";
            var query = new JsonObject { ["type"] = "subscription_preapproval" };
            if (signedId is not null)
            {
                query["data.id"] = signedId;
            }

            return new JsonObject
            {
                ["headers"] = new JsonObject { ["x-signature"] = $"ts={ts},v1={signature}", ["x-request-id"] = requestId },
                ["query"] = query,
                ["body_base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(notification)),
            };
        }

        // El mismo aviso firmado con otro id de notificación en el cuerpo es el mismo evento.
        var (_, first) = await host.Send("/v0/payment/verify_webhook", Input(SubscriptionId, SubscriptionId, 991));
        var (_, replay) = await host.Send("/v0/payment/verify_webhook", Input(SubscriptionId, SubscriptionId, 992));
        Assert.AreEqual(first!["result"]!["events"]![0]!["event_id"]!.GetValue<string>(), replay!["result"]!["events"]![0]!["event_id"]!.GetValue<string>());

        // Un cuerpo que habla de otra suscripción no vale, y sin el id firmado no hay evento.
        var (_, other) = await host.Send("/v0/payment/verify_webhook", Input(SubscriptionId, "2c938084otra", 993));
        Assert.IsFalse(other!["result"]!["valid"]!.GetValue<bool>());
        var (_, unsigned) = await host.Send("/v0/payment/verify_webhook", Input(null, SubscriptionId, 994));
        Assert.IsTrue(unsigned!["result"]!["valid"]!.GetValue<bool>());
        Assert.IsEmpty(unsigned["result"]!["events"]!.AsArray());
    }

    [TestMethod]
    public async Task CobroDeUnaSuscripcion_TraeLaSuscripcion()
    {
        var payment = JsonNode.Parse(Samples.Payment(reference: "sub-ref-4"))!.AsObject();
        payment["point_of_interaction"] = new JsonObject
        {
            ["type"] = "SUBSCRIPTIONS",
            ["transaction_data"] = new JsonObject { ["subscription_id"] = SubscriptionId, ["billing_date"] = "2026-11-05" },
        };
        var mp = new FakeMercadoPago().On("GET /v1/payments/123456789", payment.ToJsonString());
        await using var host = await MpHost.Start(mp);

        var (_, body) = await host.Send("/v0/payment/get_payment", new JsonObject { ["payment_external_id"] = "123456789" });

        Assert.AreEqual(SubscriptionId, body!["result"]!["subscription_external_id"]!.GetValue<string>());
        Assert.AreEqual("sub-ref-4", body["result"]!["external_reference"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Prueba_ConTokenDePrueba_DiceQueNoEsProduccion()
    {
        var mp = new FakeMercadoPago().On("GET /users/me", """{ "id": 1, "site_id": "MLA" }""");
        await using var host = await MpHost.Start(mp);

        var (_, body) = await host.Send("/v0/instance/test", []);
        Assert.IsFalse(body!["result"]!["livemode"]!.GetValue<bool>());

        var config = MpHost.Config();
        config["access_token"] = "APP_USR-inventado-para-pruebas";
        (_, body) = await host.Send("/v0/instance/test", [], config: config);
        Assert.IsNull(body!["result"]!["livemode"], "Con APP_USR el ambiente lo elige el staff.");
    }

    private static Task<(HttpStatusCode Status, JsonNode? Body)> Send(MpHost host, string path, JsonObject input, string key) =>
        host.Send(path, input, key, context: new JsonObject
        {
            ["account"] = new JsonObject { ["id"] = "77", ["contact"] = new JsonObject { ["name"] = "Cliente", ["email"] = "cliente@example.com" } },
        });

    private static JsonObject Input() => (JsonObject)JsonNode.Parse("""
        {
          "subscription_id": "sub-ref-4",
          "amount": { "value": "12100.00", "currency": "ARS" },
          "interval": "month",
          "interval_count": 1,
          "start_date": "2026-11-05",
          "reason": "Débito automático de tus servicios",
          "back_url": "https://facturacion.example.com/cuenta/medios-de-pago"
        }
        """)!;

    private static JsonObject Ref() => new() { ["subscription_external_id"] = SubscriptionId };

    private static string Preapproval(string status, string amount = "12100") => $$"""
        {
          "id": "{{SubscriptionId}}",
          "payer_id": 123,
          "payer_email": "cliente@example.com",
          "back_url": "https://facturacion.example.com/cuenta/medios-de-pago",
          "collector_id": 999,
          "application_id": 555,
          "status": "{{status}}",
          "reason": "Débito automático de tus servicios",
          "external_reference": "sub-ref-4",
          "date_created": "2026-10-28T12:00:00.000-03:00",
          "init_point": "https://www.mercadopago.com.ar/subscriptions/checkout?preapproval_id={{SubscriptionId}}",
          "auto_recurring": { "frequency": 1, "frequency_type": "months", "transaction_amount": {{amount}}, "currency_id": "ARS", "start_date": "2026-11-05T12:00:00.000Z" },
          "next_payment_date": "2026-11-05T12:00:00.000-03:00"
        }
        """;
}
