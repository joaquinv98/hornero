using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.PayPal.Tests;

/// <summary>El manifiesto, las operaciones declaradas y la prueba de configuración (instance/test).</summary>
[TestClass]
public sealed class ModuleTests
{
    private const string WebhookRoute = $"GET /v1/notifications/webhooks/{PpHost.WebhookId}";

    private const string Webhook = """
        {
          "id": "8PT597110X687430LKGECATA",
          "url": "https://facturacion.example.com/webhooks/7",
          "event_types": [
            { "name": "CHECKOUT.ORDER.APPROVED", "description": "An order has been approved by buyer." },
            { "name": "PAYMENT.CAPTURE.COMPLETED", "description": "A payment capture completes." },
            { "name": "PAYMENT.CAPTURE.REFUNDED", "description": "A merchant refunds a payment capture." },
            { "name": "PAYMENT.CAPTURE.REVERSED", "description": "PayPal reverses a payment capture." }
          ]
        }
        """;

    [TestMethod]
    public async Task Manifiesto_DeclaraElTipoLasCapacidadesYLosSecretos()
    {
        await using var host = await PpHost.Start(new FakePayPal());

        var (status, manifest) = await host.Get("/v0/manifest");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("paypal", manifest!["id"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "payment" }, PpHost.Strings(manifest["kinds"]));
        CollectionAssert.AreEqual(
            new[] { "payment.core", "payment.checkout", "payment.webhooks", "payment.refund", "payment.reconcile" },
            PpHost.Strings(manifest["capabilities"]));
        var schema = manifest["config_schema"]!;
        CollectionAssert.AreEquivalent(new[] { "environment", "client_id", "client_secret", "webhook_id" }, PpHost.Strings(schema["required"]));
        Assert.IsTrue(schema["properties"]!["client_secret"]!["x-secret"]!.GetValue<bool>());
        Assert.IsNull(schema["properties"]!["client_id"]!["x-secret"]);

        // Ninguna operación pasa del máximo de 120 segundos del protocolo.
        Assert.IsTrue(manifest["timeouts"]!.AsObject().All(t => t.Value!.GetValue<int>() is > 30 and <= 120));

        // Lo que usa el asistente de cobros del núcleo: los hosts de la aprobación por ambiente y los eventos del webhook.
        CollectionAssert.AreEqual(new[] { "www.sandbox.paypal.com" }, PpHost.Strings(manifest["browser_hosts"]!["test"]));
        CollectionAssert.AreEqual(new[] { "www.paypal.com" }, PpHost.Strings(manifest["browser_hosts"]!["live"]));
        CollectionAssert.Contains(PpHost.Strings(manifest["webhook_events"]), "PAYMENT.CAPTURE.COMPLETED");
    }

    [TestMethod]
    public async Task CadaOperacionDeclarada_EstaPublicada()
    {
        await using var host = await PpHost.Start(new FakePayPal());

        // Con la entrada vacía contestan invalid_input y no not_supported.
        foreach (var operation in new[] { "create_checkout", "get_payment", "refund", "list_payments" })
        {
            var (status, body) = await host.Send($"/v0/payment/{operation}", [], key: $"k-{operation}");
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, operation);
            Assert.AreEqual(ErrorCodes.InvalidInput, PpHost.Code(body), operation);
        }

        // Una notificación sin firma no es un error: es una firma que no vale.
        var (webhook, verified) = await host.Send("/v0/payment/verify_webhook", []);
        Assert.AreEqual(HttpStatusCode.OK, webhook);
        Assert.IsFalse(verified!["result"]!["valid"]!.GetValue<bool>());
        Assert.IsEmpty(host.Pp.Requests);
    }

    [TestMethod]
    public async Task Test_PideElTokenYLeeElWebhook_SinDevolverNadaSecreto()
    {
        var pp = new FakePayPal().On(WebhookRoute, Webhook);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.AreEqual("sandbox", result["environment"]!.GetValue<string>());
        Assert.IsFalse(result["livemode"]!.GetValue<bool>());
        Assert.AreEqual("https://facturacion.example.com/webhooks/7", result["webhook_url"]!.GetValue<string>());
        Assert.HasCount(4, result["event_types"]!.AsArray());

        // El token se pide con las credenciales en Basic y client_credentials, y se usa como Bearer.
        var token = pp.Single(FakePayPal.TokenRoute);
        Assert.AreEqual("https://api-m.sandbox.paypal.com/v1/oauth2/token", token.Url.ToString());
        Assert.AreEqual("grant_type=client_credentials", token.Text);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{PpHost.ClientId}:{PpHost.ClientSecret}"));
        Assert.AreEqual($"Basic {basic}", token.Headers["Authorization"]);
        var webhook = pp.Single(WebhookRoute);
        Assert.AreEqual($"Bearer {pp.AccessToken}", webhook.Headers["Authorization"]);

        Assert.DoesNotContain(pp.AccessToken, body.ToJsonString());
        Assert.DoesNotContain(PpHost.ClientSecret, body.ToJsonString());
    }

    [TestMethod]
    public async Task Test_EnLive_UsaLaApiDeProduccion()
    {
        var pp = new FakePayPal().On(WebhookRoute, Webhook);
        await using var host = await PpHost.Start(pp);
        var config = PpHost.Config();
        config["environment"] = "live";

        var (status, body) = await host.Send("/v0/instance/test", [], config: config);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("live", body!["result"]!["environment"]!.GetValue<string>());
        Assert.IsTrue(body["result"]!["livemode"]!.GetValue<bool>());
        Assert.IsTrue(pp.Requests.All(r => r.Url.Host == "api-m.paypal.com"));
    }

    [TestMethod]
    public async Task Test_SinElEventoDeOrdenAprobada_EsInvalidConfig()
    {
        // Sin CHECKOUT.ORDER.APPROVED nadie captura las órdenes y ningún cobro se completa.
        var webhook = JsonNode.Parse(Webhook)!;
        webhook["event_types"]!.AsArray().RemoveAt(0);
        var pp = new FakePayPal().On(WebhookRoute, webhook.ToJsonString());
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, PpHost.Code(body));
        Assert.AreEqual("webhook_id", body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
        Assert.Contains("CHECKOUT.ORDER.APPROVED", body["error"]!["message"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Test_ConTodosLosEventos_Alcanza()
    {
        var pp = new FakePayPal().On(WebhookRoute, """{ "id": "X", "url": "https://facturacion.example.com/webhooks/7", "event_types": [ { "name": "*" } ] }""");
        await using var host = await PpHost.Start(pp);

        var (status, _) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.OK, status);
    }

    [TestMethod]
    public async Task Test_ConUnWebhookQueNoExiste_EsInvalidConfig()
    {
        var pp = new FakePayPal().On(WebhookRoute, Samples.Error("INVALID_RESOURCE_ID", "Webhook not found."), HttpStatusCode.NotFound);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, PpHost.Code(body));
        Assert.AreEqual("webhook_id", body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Test_ConCredencialesRechazadas_EsInvalidConfig()
    {
        var pp = new FakePayPal().On(
            FakePayPal.TokenRoute,
            """{ "error": "invalid_client", "error_description": "Client Authentication failed" }""",
            HttpStatusCode.Unauthorized);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, PpHost.Code(body));
        CollectionAssert.AreEqual(new[] { "client_id", "client_secret" }, PpHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.AreEqual(0, pp.Count(WebhookRoute));
    }

    [TestMethod]
    [DataRow("environment", "produccion")]
    [DataRow("environment", null)]
    [DataRow("client_secret", "con espacio")]
    [DataRow("client_id", "")]
    [DataRow("webhook_id", "no-es-un-id")]
    public async Task Test_ConfiguracionQueNoSirve_EsInvalidConfigSinSalirALaRed(string field, string? value)
    {
        await using var host = await PpHost.Start(new FakePayPal());
        var config = PpHost.Config();
        config[field] = value;

        var (status, body) = await host.Send("/v0/instance/test", [], config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, PpHost.Code(body));
        Assert.AreEqual(field, body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
        Assert.IsEmpty(host.Pp.Requests);
    }
}
