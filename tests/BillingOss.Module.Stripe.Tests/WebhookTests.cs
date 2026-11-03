using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace BillingOss.Module.Stripe.Tests;

/// <summary>
/// payment.verify_webhook: la firma Stripe-Signature y los eventos normalizados. La firma de referencia se calculó
/// aparte con HMAC-SHA256 sobre "t.cuerpo" y la clave StripeHost.FakeWebhookSecret.
/// </summary>
[TestClass]
public sealed class WebhookTests
{
    private const string Route = "/v0/payment/verify_webhook";
    private const long T = 1793199600;

    [TestMethod]
    public async Task FirmaValida_DaElEventoConElPaymentIntent_SinSalirALaRed()
    {
        var stripe = new FakeStripe();
        await using var host = await StripeHost.Start(stripe);
        var body = Samples.Event("checkout.session.completed", new JsonObject { ["id"] = "cs_test_1", ["payment_intent"] = "pi_3Prueba" });

        var (status, response) = await host.Send(Route, Input(body, $"t={T},v1={StripeHost.Sign(body, T)}"));

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = response!["result"]!;
        Assert.IsTrue(result["valid"]!.GetValue<bool>());
        var e = result["events"]!.AsArray().Single()!;
        Assert.AreEqual("evt_1Prueba", e["event_id"]!.GetValue<string>());
        Assert.AreEqual("checkout.session.completed", e["type"]!.GetValue<string>());
        Assert.AreEqual("pi_3Prueba", e["payment_external_id"]!.GetValue<string>());
        Assert.IsEmpty(stripe.Requests);
        Assert.DoesNotContain(StripeHost.FakeWebhookSecret, response.ToJsonString());
    }

    [TestMethod]
    public void LaFirmaEsHmacDeTPuntoCuerpo()
    {
        // Calculada aparte con openssl: printf '1793199600.{"id":"evt_1"}' | openssl dgst -sha256 -hmac whsec_inventadoParaPruebas000
        const string reference = "12d8e591d5210cb163b2f2a611c06f7462c26372c8fa4f26d5a97c0b16116d24";
        var body = Encoding.UTF8.GetBytes("""{"id":"evt_1"}""");

        Assert.IsTrue(WebhookSignature.Verify(StripeHost.FakeWebhookSecret, $"t={T},v1={reference}", body, null));
        Assert.IsTrue(WebhookSignature.Verify(StripeHost.FakeWebhookSecret, $"t={T},v1={reference.ToUpperInvariant()}", body, null));
        Assert.IsFalse(WebhookSignature.Verify(StripeHost.FakeWebhookSecret, $"t={T + 1},v1={reference}", body, null));
        Assert.IsFalse(WebhookSignature.Verify(StripeHost.FakeWebhookSecret, $"t={T},v0={reference}", body, null));
    }

    [TestMethod]
    public async Task FirmaMala_NoEsValidaYNoTraeEventos()
    {
        await using var host = await StripeHost.Start(new FakeStripe());
        var body = Samples.Event("payment_intent.succeeded", new JsonObject { ["id"] = "pi_3Prueba" });

        var (_, bad) = await host.Send(Route, Input(body, $"t={T},v1={StripeHost.Sign(body, T, "whsec_otro")}"));
        var (_, missing) = await host.Send(Route, Input(body, null));
        var (_, tampered) = await host.Send(Route, Input(body.Replace("pi_3Prueba", "pi_3Otro", StringComparison.Ordinal), $"t={T},v1={StripeHost.Sign(body, T)}"));

        foreach (var response in new[] { bad, missing, tampered })
        {
            Assert.IsFalse(response!["result"]!["valid"]!.GetValue<bool>());
            Assert.IsEmpty(response["result"]!["events"]!.AsArray());
        }
    }

    [TestMethod]
    public async Task DuranteLaRotacion_AlcanzaConUnaDeLasFirmas()
    {
        await using var host = await StripeHost.Start(new FakeStripe());
        var body = Samples.Event("payment_intent.succeeded", new JsonObject { ["id"] = "pi_3Prueba" });

        var (_, response) = await host.Send(Route, Input(body, $"t={T},v1={StripeHost.Sign(body, T, "whsec_viejo")},v1={StripeHost.Sign(body, T)},v0=ignorada"));

        Assert.IsTrue(response!["result"]!["valid"]!.GetValue<bool>());
        Assert.AreEqual("pi_3Prueba", response["result"]!["events"]![0]!["payment_external_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ConLaHoraDeRecepcion_SeAplicanCincoMinutos()
    {
        await using var host = await StripeHost.Start(new FakeStripe());
        var body = Samples.Event("payment_intent.succeeded", new JsonObject { ["id"] = "pi_3Prueba" });
        var header = $"t={T},v1={StripeHost.Sign(body, T)}";

        var (_, inside) = await host.Send(Route, Input(body, header, DateTimeOffset.FromUnixTimeSeconds(T + 240)));
        var (_, outside) = await host.Send(Route, Input(body, header, DateTimeOffset.FromUnixTimeSeconds(T + 360)));
        var (_, without) = await host.Send(Route, Input(body, header));

        Assert.IsTrue(inside!["result"]!["valid"]!.GetValue<bool>());
        Assert.IsFalse(outside!["result"]!["valid"]!.GetValue<bool>());
        Assert.IsTrue(without!["result"]!["valid"]!.GetValue<bool>(), "Sin la hora de recepción no hay ventana, como en Mercado Pago.");
    }

    [TestMethod]
    [DataRow("payment_intent.succeeded", "id", "pi_3Prueba")]
    [DataRow("payment_intent.payment_failed", "id", "pi_3Prueba")]
    [DataRow("charge.refunded", "payment_intent", "pi_3Prueba")]
    [DataRow("refund.updated", "payment_intent", "pi_3Prueba")]
    [DataRow("charge.dispute.created", "payment_intent", "pi_3Prueba")]
    [DataRow("charge.dispute.closed", "payment_intent", "pi_3Prueba")]
    public async Task CadaEventoLlevaASuPaymentIntent(string type, string field, string intent)
    {
        await using var host = await StripeHost.Start(new FakeStripe());
        var data = new JsonObject { ["id"] = field == "id" ? intent : "ch_o_re_1", [field] = intent };
        var body = Samples.Event(type, data);

        var (_, response) = await host.Send(Route, Input(body, $"t={T},v1={StripeHost.Sign(body, T)}"));

        var e = response!["result"]!["events"]!.AsArray().Single()!;
        Assert.AreEqual(intent, e["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual(type, e["type"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("payment_method.detached", "method.deleted")]
    [DataRow("payment_method.automatically_updated", "method.updated")]
    public async Task EventosDeMedios_LlevanAlMedio(string type, string normalized)
    {
        await using var host = await StripeHost.Start(new FakeStripe());
        var body = Samples.Event(type, new JsonObject { ["id"] = "pm_Prueba123", ["object"] = "payment_method" });

        var (_, response) = await host.Send(Route, Input(body, $"t={T},v1={StripeHost.Sign(body, T)}"));

        var e = response!["result"]!["events"]!.AsArray().Single()!;
        Assert.AreEqual("pm_Prueba123", e["method_external_id"]!.GetValue<string>());
        Assert.AreEqual(normalized, e["type"]!.GetValue<string>());
        Assert.IsNull(e["payment_external_id"]);
    }

    [TestMethod]
    public async Task OtrosEventos_OSesionSinPago_SonValidosSinEventos()
    {
        await using var host = await StripeHost.Start(new FakeStripe());
        foreach (var body in new[]
        {
            Samples.Event("customer.created", new JsonObject { ["id"] = "cus_1" }),
            Samples.Event("checkout.session.expired", new JsonObject { ["id"] = "cs_test_1", ["payment_intent"] = null }),
        })
        {
            var (_, response) = await host.Send(Route, Input(body, $"t={T},v1={StripeHost.Sign(body, T)}"));
            Assert.IsTrue(response!["result"]!["valid"]!.GetValue<bool>());
            Assert.IsEmpty(response["result"]!["events"]!.AsArray());
        }
    }

    private static JsonObject Input(string body, string? signature, DateTimeOffset? receivedAt = null)
    {
        var headers = new JsonObject { ["Content-Type"] = "application/json" };
        if (signature is not null)
        {
            headers["Stripe-Signature"] = signature;
        }

        var input = new JsonObject { ["headers"] = headers, ["query"] = new JsonObject(), ["body_base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(body)) };
        if (receivedAt is { } at)
        {
            input["received_at"] = at.ToString("O");
        }

        return input;
    }
}
