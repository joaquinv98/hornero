using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.MercadoPago.Tests;

/// <summary>El manifiesto, las operaciones declaradas y la prueba de configuración (instance/test).</summary>
[TestClass]
public sealed class ModuleTests
{
    [TestMethod]
    public async Task Manifiesto_DeclaraElTipoLasCapacidadesYLosSecretos()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());

        var (status, manifest) = await host.Get("/v0/manifest");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("mercadopago", manifest!["id"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "payment" }, MpHost.Strings(manifest["kinds"]));
        CollectionAssert.AreEqual(
            new[] { "payment.core", "payment.checkout", "payment.webhooks", "payment.refund", "payment.reconcile", "payment.subscriptions", "payment.subscription_update" },
            MpHost.Strings(manifest["capabilities"]));
        var schema = manifest["config_schema"]!;
        CollectionAssert.AreEquivalent(new[] { "access_token", "webhook_secret" }, MpHost.Strings(schema["required"]));
        Assert.IsTrue(schema["properties"]!["access_token"]!["x-secret"]!.GetValue<bool>());
        Assert.IsTrue(schema["properties"]!["webhook_secret"]!["x-secret"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task CadaOperacionDeclarada_EstaPublicada()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());

        // Con la entrada vacía contestan invalid_input y no not_supported.
        foreach (var operation in new[] { "create_checkout", "get_payment", "refund", "list_payments", "create_subscription", "get_subscription", "cancel_subscription", "update_subscription" })
        {
            var (status, body) = await host.Send($"/v0/payment/{operation}", [], key: $"k-{operation}");
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, operation);
            Assert.AreEqual(ErrorCodes.InvalidInput, MpHost.Code(body), operation);
        }

        // Una notificación sin firma no es un error: es una firma que no vale.
        var (webhook, verified) = await host.Send("/v0/payment/verify_webhook", []);
        Assert.AreEqual(HttpStatusCode.OK, webhook);
        Assert.IsFalse(verified!["result"]!["valid"]!.GetValue<bool>());
        Assert.IsEmpty(host.Mp.Requests);
    }

    [TestMethod]
    public async Task Test_ValidaElTokenContraLaApi_YDevuelveSoloLaCuenta()
    {
        var mp = new FakeMercadoPago().On(
            "GET /users/me",
            """{ "id": 987654321, "nickname": "VENDEDOR_DE_PRUEBA", "site_id": "MLA", "email": "vendedor@example.com" }""");
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("987654321", body!["result"]!["account_id"]!.GetValue<string>());
        Assert.AreEqual("MLA", body["result"]!["site_id"]!.GetValue<string>());

        // El nombre y el correo del vendedor no hacen falta y no se devuelven.
        Assert.DoesNotContain("VENDEDOR_DE_PRUEBA", body.ToJsonString());
        Assert.DoesNotContain("vendedor@example.com", body.ToJsonString());
        var sent = mp.Single("GET /users/me");
        Assert.AreEqual("https://api.mercadopago.com/users/me", sent.Url.ToString());
        Assert.AreEqual($"Bearer {MpHost.FakeAccessToken}", sent.Headers["Authorization"]);
    }

    [TestMethod]
    public async Task Test_ConElTokenRechazado_EsInvalidConfig()
    {
        var mp = new FakeMercadoPago().On("GET /users/me", Samples.Error(401, "unauthorized", "invalid_token"), HttpStatusCode.Unauthorized);
        await using var host = await MpHost.Start(mp);

        var (status, body) = await host.Send("/v0/instance/test", []);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, MpHost.Code(body));
        Assert.AreEqual("access_token", body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Test_SinLaClaveDeLosWebhooks_EsInvalidConfigYNoLlamaALaApi()
    {
        await using var host = await MpHost.Start(new FakeMercadoPago());
        var config = MpHost.Config();
        config.Remove("webhook_secret");

        var (status, body) = await host.Send("/v0/instance/test", [], config: config);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, MpHost.Code(body));
        Assert.AreEqual("webhook_secret", body!["error"]!["details"]!["fields"]![0]!.GetValue<string>());
        Assert.IsEmpty(host.Mp.Requests);
    }
}
