using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Stripe.Tests;

/// <summary>Manifiesto y prueba de la instancia: lo que el asistente de cobros necesita para configurarla.</summary>
[TestClass]
public sealed class ModuleTests
{
    private const string WebhookUrl = "https://facturacion.example.com/webhooks/9";

    [TestMethod]
    public async Task Manifiesto_DeclaraLoQueHaceYLoQueNecesitaElAsistente()
    {
        await using var host = await StripeHost.Start(new FakeStripe());

        var (status, manifest) = await host.Get("/v0/manifest");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("stripe", manifest!["id"]!.GetValue<string>());
        CollectionAssert.AreEquivalent(
            new[] { "payment.core", "payment.checkout", "payment.webhooks", "payment.refund", "payment.reconcile", "payment.saved_methods" },
            StripeHost.Strings(manifest["capabilities"]));
        CollectionAssert.DoesNotContain(StripeHost.Strings(manifest["capabilities"]), "payment.subscriptions", "Quien factura es el núcleo: nada de Stripe Billing.");
        var properties = manifest["config_schema"]!["properties"]!;
        Assert.IsTrue(properties["api_key"]!["x-secret"]!.GetValue<bool>());
        Assert.IsTrue(properties["webhook_secret"]!["x-secret"]!.GetValue<bool>());
        Assert.StartsWith("https://dashboard.stripe.com/", properties["api_key"]!["x-help"]!["url"]!.GetValue<string>());
        Assert.IsFalse(string.IsNullOrEmpty(properties["webhook_secret"]!["x-help"]!["en"]!.GetValue<string>()));
        CollectionAssert.AreEqual(new[] { "*" }, StripeHost.Strings(manifest["currencies"]));
        CollectionAssert.AreEqual(new[] { "checkout.stripe.com" }, StripeHost.Strings(manifest["browser_hosts"]!["live"]));
        CollectionAssert.AreEqual(StripeOperations.WebhookEvents, StripeHost.Strings(manifest["webhook_events"]));
        Assert.IsEmpty(manifest["data_scopes"]!.AsArray());
        Assert.AreEqual(60, manifest["timeouts"]!["charge_saved"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task Prueba_ValidaLaClaveYDiceElModo_SinElDestinoNoMiraAvisos()
    {
        var stripe = new FakeStripe().WithReads().On("GET /v1/balance", Samples.Balance(live: false));
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.IsFalse(body!["result"]!["livemode"]!.GetValue<bool>());
        Assert.IsEmpty(body["result"]!["warnings"]!.AsArray());
        Assert.AreEqual(0, stripe.Count("GET /v1/webhook_endpoints"));

        // Y prueba cada lectura que usan las operaciones, con un listado de uno.
        foreach (var (path, _) in StripeOperations.Reads)
        {
            Assert.AreEqual("1", stripe.Single($"GET {path}").Query("limit").ToString(), path);
        }
    }

    [TestMethod]
    public async Task Prueba_ConUnPermisoDeLecturaDeMenos_EsConfiguracionInvalida_YDiceCual()
    {
        var message = $"The provided key '{StripeHost.FakeApiKey}' does not have the required permissions for this endpoint. Having the 'rak_disputes_read' permission would allow this request.";
        var stripe = new FakeStripe().WithReads().On("GET /v1/balance", Samples.Balance())
            .On("GET /v1/disputes", Samples.Error("invalid_request_error", message: message), HttpStatusCode.Forbidden);
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/instance/test", new JsonObject { ["webhook_url"] = WebhookUrl });

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, StripeHost.Code(body));
        CollectionAssert.AreEqual(new[] { "api_key" }, StripeHost.Strings(body!["error"]!["details"]!["fields"]));
        var text = body["error"]!["message"]!.GetValue<string>();
        StringAssert.Contains(text, "Disputes");
        StringAssert.Contains(text, "rak_disputes_read");
        Assert.DoesNotContain(StripeHost.FakeApiKey, body.ToJsonString());
        Assert.AreEqual(0, stripe.Count("GET /v1/webhook_endpoints"), "Sin los permisos no vale la pena seguir.");
    }

    [TestMethod]
    public async Task Prueba_UnListadoQuePideOtroParametro_NoEsUnPermisoQueFalta()
    {
        var stripe = new FakeStripe().WithReads().On("GET /v1/balance", Samples.Balance())
            .On("GET /v1/payment_methods", Samples.Error("invalid_request_error", message: "Missing required param: customer.", param: "customer"), HttpStatusCode.BadRequest);
        await using var host = await StripeHost.Start(stripe);

        var (status, _) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.OK, status);
    }

    [TestMethod]
    public async Task Prueba_ConLaUrlDeAvisos_CompruebaElDestinoYSusEventos()
    {
        var stripe = new FakeStripe().WithReads().On("GET /v1/balance", Samples.Balance(live: true)).On("GET /v1/webhook_endpoints", Samples.Endpoints(WebhookUrl));
        await using var host = await StripeHost.Start(stripe);

        var (status, body) = await host.Send("/v0/instance/test", new JsonObject { ["webhook_url"] = WebhookUrl });

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.IsTrue(body!["result"]!["livemode"]!.GetValue<bool>());
        Assert.AreEqual(WebhookUrl, body["result"]!["webhook_url"]!.GetValue<string>());
        Assert.HasCount(StripeOperations.WebhookEvents.Length, body["result"]!["event_types"]!.AsArray());
    }

    [TestMethod]
    public async Task Prueba_SinDestinoOConEventosDeMenos_EsConfiguracionInvalida()
    {
        var stripe = new FakeStripe().WithReads().On("GET /v1/balance", Samples.Balance()).On("GET /v1/webhook_endpoints", Samples.Endpoints("https://otro.example.com/webhooks/9"));
        await using var host = await StripeHost.Start(stripe);

        var (_, body) = await host.Send("/v0/instance/test", new JsonObject { ["webhook_url"] = WebhookUrl });
        Assert.AreEqual(ErrorCodes.InvalidConfig, StripeHost.Code(body));
        CollectionAssert.AreEqual(new[] { "webhook_secret" }, StripeHost.Strings(body!["error"]!["details"]!["fields"]));

        stripe.On("GET /v1/webhook_endpoints", Samples.Endpoints(WebhookUrl, "disabled"));
        (_, body) = await host.Send("/v0/instance/test", new JsonObject { ["webhook_url"] = WebhookUrl });
        Assert.AreEqual(ErrorCodes.InvalidConfig, StripeHost.Code(body));

        stripe.On("GET /v1/webhook_endpoints", Samples.Endpoints(WebhookUrl, "enabled", "payment_intent.succeeded"));
        (_, body) = await host.Send("/v0/instance/test", new JsonObject { ["webhook_url"] = WebhookUrl });
        Assert.AreEqual(ErrorCodes.InvalidConfig, StripeHost.Code(body));
        StringAssert.Contains(body!["error"]!["message"]!.GetValue<string>(), "charge.refunded");

        stripe.On("GET /v1/webhook_endpoints", Samples.Endpoints(WebhookUrl, "enabled", "*"));
        (_, body) = await host.Send("/v0/instance/test", new JsonObject { ["webhook_url"] = WebhookUrl });
        Assert.IsNotNull(body!["result"], "Con todos los eventos (*) alcanza.");
    }

    [TestMethod]
    public async Task Prueba_ConClaveSecreta_AvisaQueConvieneLaRestringida()
    {
        var stripe = new FakeStripe().WithReads().On("GET /v1/balance", Samples.Balance());
        await using var host = await StripeHost.Start(stripe);
        var config = StripeHost.Config();
        config["api_key"] = "sk_test_inventadaParaPruebas000";

        var (_, body) = await host.Send("/v0/instance/test", [], config: config);

        StringAssert.Contains(body!["result"]!["warnings"]![0]!.GetValue<string>(), "rk_");
        Assert.DoesNotContain("sk_test_inventadaParaPruebas000", body.ToJsonString());
    }

    [TestMethod]
    public async Task OperacionQueNoDeclara_EsNotSupported()
    {
        await using var host = await StripeHost.Start(new FakeStripe());

        var (status, body) = await host.Send("/v0/payment/create_subscription", [], "k");

        Assert.AreEqual(HttpStatusCode.NotImplemented, status);
        Assert.AreEqual(ErrorCodes.NotSupported, StripeHost.Code(body));
    }
}
