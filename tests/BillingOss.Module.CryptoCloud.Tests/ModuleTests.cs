using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.CryptoCloud.Tests;

/// <summary>El manifiesto, las operaciones declaradas, las que CryptoCloud no tiene y la prueba de configuración.</summary>
[TestClass]
public sealed class ModuleTests
{
    private const string BalanceRoute = "POST /v2/merchant/wallet/balance/all";

    [TestMethod]
    public async Task Manifiesto_DeclaraElTipoLasCapacidadesYLosSecretos()
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());

        var (status, manifest) = await host.Get("/v0/manifest");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("cryptocloud", manifest!["id"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "payment" }, CcHost.Strings(manifest["kinds"]));
        CollectionAssert.AreEqual(
            new[] { "payment.core", "payment.checkout", "payment.webhooks", "payment.reconcile" },
            CcHost.Strings(manifest["capabilities"]));
        var schema = manifest["config_schema"]!;
        CollectionAssert.AreEquivalent(new[] { "shop_id", "api_key", "secret_key" }, CcHost.Strings(schema["required"]));
        Assert.IsTrue(schema["properties"]!["api_key"]!["x-secret"]!.GetValue<bool>());
        Assert.IsTrue(schema["properties"]!["secret_key"]!["x-secret"]!.GetValue<bool>());
        Assert.IsNull(schema["properties"]!["shop_id"]!["x-secret"]);
        Assert.AreEqual(720, schema["properties"]!["invoice_hours"]!["maximum"]!.GetValue<int>());

        // Lo que usa el asistente de cobros del núcleo: solo dólares y el host del link de pago.
        CollectionAssert.AreEqual(new[] { "USD" }, CcHost.Strings(manifest["currencies"]));
        CollectionAssert.AreEqual(new[] { "pay.trybit.com" }, CcHost.Strings(manifest["browser_hosts"]!["live"]));

        // Las facturas en modo prueba se confirman sin pago: tomarlas como reales es explícito.
        Assert.AreEqual("boolean", schema["properties"]!["allow_test_invoices"]!["type"]!.GetValue<string>());
        Assert.IsFalse(schema["properties"]!["allow_test_invoices"]!["default"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task CadaOperacionDeclarada_EstaPublicada()
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());

        // Con la entrada vacía contestan invalid_input y no not_supported.
        foreach (var operation in new[] { "create_checkout", "get_payment", "list_payments" })
        {
            var (status, body) = await host.Send($"/v0/payment/{operation}", [], key: $"k-{operation}");
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, operation);
            Assert.AreEqual(ErrorCodes.InvalidInput, CcHost.Code(body), operation);
        }

        // Un aviso sin token no es un error: es una firma que no vale.
        var (webhook, verified) = await host.Send("/v0/payment/verify_webhook", []);
        Assert.AreEqual(HttpStatusCode.OK, webhook);
        Assert.IsFalse(verified!["result"]!["valid"]!.GetValue<bool>());
        Assert.IsEmpty(host.Cc.Requests);
    }

    [TestMethod]
    [DataRow("refund")]
    [DataRow("create_subscription")]
    [DataRow("cancel_subscription")]
    public async Task LoQueLaApiNoTiene_EsNotSupported_SinSalirALaRed(string operation)
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());

        var input = new JsonObject { ["payment_external_id"] = "INV-89UX09KA", ["amount"] = new JsonObject { ["value"] = "5.00", ["currency"] = "USD" } };
        var (status, body) = await host.Send($"/v0/payment/{operation}", input, key: $"k-{operation}");

        Assert.AreEqual(HttpStatusCode.NotImplemented, status);
        Assert.AreEqual(ErrorCodes.NotSupported, CcHost.Code(body));
        Assert.IsFalse(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.IsEmpty(host.Cc.Requests);
    }

    [TestMethod]
    public async Task Test_ValidaLaApiKeyConLosSaldos_YNoLosDevuelve()
    {
        var cc = new FakeCryptoCloud().On(BalanceRoute, Samples.Balance);
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("{}", body!["result"]!.ToJsonString());
        var sent = cc.Single(BalanceRoute);
        Assert.AreEqual("https://api.trybit.com/v2/merchant/wallet/balance/all", sent.Url.ToString());
        Assert.AreEqual($"Token {CcHost.FakeApiKey}", sent.Headers["Authorization"]);
        Assert.IsNull(sent.Body);
    }

    [TestMethod]
    public async Task Test_ConLaApiKeyRechazada_EsInvalidConfig()
    {
        var cc = new FakeCryptoCloud().On(BalanceRoute, Samples.Unauthorized, HttpStatusCode.Unauthorized);
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, CcHost.Code(body));
        Assert.AreEqual("api_key", body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("shop_id")]
    [DataRow("secret_key")]
    public async Task Test_SinUnDatoDeLaInstancia_EsInvalidConfigYNoLlamaALaApi(string field)
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());
        var config = CcHost.Config();
        config.Remove(field);

        var (status, body) = await host.Send("/v0/instance/test", [], config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, CcHost.Code(body));
        Assert.AreEqual(field, body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
        Assert.IsEmpty(host.Cc.Requests);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(721)]
    public async Task Test_ConUnaVidaDeFacturaFueraDeRango_EsInvalidConfig(int hours)
    {
        await using var host = await CcHost.Start(new FakeCryptoCloud());
        var config = CcHost.Config();
        config["invoice_hours"] = hours;

        var (status, body) = await host.Send("/v0/instance/test", [], config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("invoice_hours", body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
        Assert.IsEmpty(host.Cc.Requests);
    }
}
