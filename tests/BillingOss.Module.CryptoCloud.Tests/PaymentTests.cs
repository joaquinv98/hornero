using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.CryptoCloud.Tests;

/// <summary>payment.get_payment y payment.list_payments: la factura de CryptoCloud traducida al pago del núcleo.</summary>
[TestClass]
public sealed class PaymentTests
{
    private const string InfoRoute = "POST /v2/invoice/merchant/info";
    private const string ListRoute = "POST /v2/invoice/merchant/list";

    [TestMethod]
    public async Task GetPayment_DevuelveEstadoMontoComisionFechasYReferencia()
    {
        var cc = new FakeCryptoCloud().On(InfoRoute, Samples.Info(Samples.Invoice()));
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send("/v0/payment/get_payment", Input("INV-89UX09KA"));

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        Assert.AreEqual("INV-89UX09KA", result["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual("approved", result["status"]!.GetValue<string>());
        Assert.AreEqual("34.00", result["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("USD", result["amount"]!["currency"]!.GetValue<string>());
        Assert.AreEqual("0.00", result["refunded"]!["value"]!.GetValue<string>());

        // La comisión es la que paga el comercio: la del servicio, redondeada. La de la red la paga el cliente.
        Assert.AreEqual("0.48", result["fee"]!["value"]!.GetValue<string>());
        Assert.AreEqual("USD", result["fee"]!["currency"]!.GetValue<string>());

        Assert.AreEqual("2026-10-25T19:00:00Z", result["created_at"]!.GetValue<string>());
        Assert.AreEqual("2026-10-25T19:12:10Z", result["approved_at"]!.GetValue<string>());
        Assert.AreEqual("att-1042", result["external_reference"]!.GetValue<string>());
        Assert.AreEqual("paid", result["gateway_status"]!.GetValue<string>());
        Assert.AreEqual("success", result["gateway_status_detail"]!.GetValue<string>());

        // Los datos del pagador no hacen falta y no se devuelven.
        Assert.DoesNotContain("comprador", body.ToJsonString());
        Assert.DoesNotContain("1100000000", body.ToJsonString());

        var sent = cc.Single(InfoRoute);
        Assert.AreEqual("https://api.trybit.com/v2/invoice/merchant/info", sent.Url.ToString());
        Assert.AreEqual($"Token {CcHost.FakeApiKey}", sent.Headers["Authorization"]);
        CollectionAssert.AreEqual(new[] { "INV-89UX09KA" }, CcHost.Strings(sent.Body!["uuids"]));
    }

    [TestMethod]
    [DataRow("paid", "approved")]
    [DataRow("overpaid", "approved")]
    [DataRow("created", "pending")]
    [DataRow("partial", "pending")]
    [DataRow("canceled", "rejected")]
    public async Task GetPayment_TraduceLosEstados(string gateway, string expected)
    {
        var cc = new FakeCryptoCloud().On(InfoRoute, Samples.Info(Samples.Invoice(status: gateway)));
        await using var host = await CcHost.Start(cc);

        var (_, body) = await host.Send("/v0/payment/get_payment", Input("INV-89UX09KA"));

        Assert.AreEqual(expected, body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual(gateway, body["result"]!["gateway_status"]!.GetValue<string>());

        // La fecha de aprobación es solo de lo que se cobró.
        Assert.AreEqual(expected == "approved", body["result"]!["approved_at"] is not null);
    }

    [TestMethod]
    public async Task GetPayment_UnEstadoQueNoSeConoce_NoSeAdivina()
    {
        var cc = new FakeCryptoCloud().On(InfoRoute, Samples.Info(Samples.Invoice(status: "frozen")));
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send("/v0/payment/get_payment", Input("INV-89UX09KA"));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, CcHost.Code(body));
        Assert.Contains("frozen", body!["error"]!["message"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task GetPayment_SinOrderId_NoTieneReferencia()
    {
        // CryptoCloud manda "None" cuando la factura no tiene order_id.
        var cc = new FakeCryptoCloud().On(InfoRoute, Samples.Info(Samples.Invoice(orderId: "None")));
        await using var host = await CcHost.Start(cc);

        var (_, body) = await host.Send("/v0/payment/get_payment", Input("INV-89UX09KA"));

        Assert.IsNull(body!["result"]!["external_reference"]);
    }

    [TestMethod]
    public async Task GetPayment_ElIdSinPrefijo_SeConsultaConElPrefijo()
    {
        var cc = new FakeCryptoCloud().On(InfoRoute, Samples.Info(Samples.Invoice()));
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send("/v0/payment/get_payment", Input("89UX09KA"));

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("INV-89UX09KA", body!["result"]!["payment_external_id"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "INV-89UX09KA" }, CcHost.Strings(cc.Single(InfoRoute).Body!["uuids"]));
    }

    [TestMethod]
    public async Task GetPayment_EnOtraMoneda_UsaLaDeLaFacturaYNoInformaComision()
    {
        var cc = new FakeCryptoCloud().On(InfoRoute, Samples.Info(Samples.Invoice(amountInFiat: "48195.5", fiatCurrency: "ARS")));
        await using var host = await CcHost.Start(cc);

        var (_, body) = await host.Send("/v0/payment/get_payment", Input("INV-89UX09KA"));

        Assert.AreEqual("48195.50", body!["result"]!["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("ARS", body["result"]!["amount"]!["currency"]!.GetValue<string>());
        Assert.IsNull(body["result"]!["fee"]);
    }

    [TestMethod]
    public async Task GetPayment_SinFiatCurrency_EsEnUsd()
    {
        var cc = new FakeCryptoCloud().On(InfoRoute, Samples.Info(Samples.Invoice(fiatCurrency: null)));
        await using var host = await CcHost.Start(cc);

        var (_, body) = await host.Send("/v0/payment/get_payment", Input("INV-89UX09KA"));

        Assert.AreEqual("USD", body!["result"]!["amount"]!["currency"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task GetPayment_ConLasDosComisionesDelComercio_LasSuma()
    {
        var cc = new FakeCryptoCloud().On(InfoRoute, Samples.Info(Samples.Invoice(sideCommission: "merchant")));
        await using var host = await CcHost.Start(cc);

        var (_, body) = await host.Send("/v0/payment/get_payment", Input("INV-89UX09KA"));

        Assert.AreEqual("1.68", body!["result"]!["fee"]!["value"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task GetPayment_FacturaEnModoPrueba_NoSeDaPorPagada_SalvoQueLaInstanciaLoHabilite()
    {
        // En modo prueba el staff confirma la factura desde el panel sin que entre plata, y queda en paid.
        var cc = new FakeCryptoCloud().On(InfoRoute, Samples.Info(Samples.Invoice(testMode: true)));
        await using var host = await CcHost.Start(cc);
        var allowed = CcHost.Config();
        allowed["allow_test_invoices"] = true;

        var (status, body) = await host.Send("/v0/payment/get_payment", Input("INV-89UX09KA"));
        var (_, test) = await host.Send("/v0/payment/get_payment", Input("INV-89UX09KA"), config: allowed);

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("rejected", body!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual("paid", body["result"]!["gateway_status"]!.GetValue<string>());
        Assert.AreEqual("test_mode", body["result"]!["gateway_status_detail"]!.GetValue<string>());
        Assert.IsNull(body["result"]!["approved_at"]);
        Assert.AreEqual("approved", test!["result"]!["status"]!.GetValue<string>());
        Assert.AreEqual("success", test["result"]!["gateway_status_detail"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task GetPayment_SinResult_EsUpstreamUnavailable_YNoNotFound()
    {
        // not_found es definitivo para el núcleo: una respuesta rara no alcanza para decirlo.
        var cc = new FakeCryptoCloud().On(InfoRoute, """{ "status": "success" }""");
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send("/v0/payment/get_payment", Input("INV-89UX09KA"));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, CcHost.Code(body));
    }

    [TestMethod]
    public async Task GetPayment_SiNoVieneLaFactura_EsNotFound()
    {
        var cc = new FakeCryptoCloud().On(InfoRoute, Samples.Info(Samples.Invoice(uuid: "INV-OTRA0001")));
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send("/v0/payment/get_payment", Input("INV-89UX09KA"));

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, CcHost.Code(body));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("INV-")]
    [DataRow("ccp_123456789012345")]
    [DataRow("INV-89UX09KA/../x")]
    [DataRow("INV-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task GetPayment_ConUnIdQueNoEsDeFactura_EsInvalidInput(string id)
    {
        var cc = new FakeCryptoCloud();
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send("/v0/payment/get_payment", Input(id));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, CcHost.Code(body));
        Assert.IsEmpty(cc.Requests);
    }

    [TestMethod]
    public async Task ListPayments_PideLosDiasDelRango_YPagina()
    {
        var cc = new FakeCryptoCloud().On(ListRoute, Samples.List(150, Samples.Invoice(), Samples.Invoice(uuid: "INV-AB12CD34", status: "created", dateFinished: null)));
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send("/v0/payment/list_payments", Range("2026-10-25T00:00:00Z", "2026-10-26T00:00:00Z", by: "created"));

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        var payments = result["payments"]!.AsArray();
        Assert.HasCount(2, payments);
        Assert.AreEqual("INV-89UX09KA", payments[0]!["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual("pending", payments[1]!["status"]!.GetValue<string>());
        Assert.AreEqual(150, result["total"]!.GetValue<int>());
        Assert.AreEqual(0, result["offset"]!.GetValue<int>());
        Assert.AreEqual(2, result["next_offset"]!.GetValue<int>());

        // El fin es exclusivo: hasta las 00:00 del 26 es solo el 25.
        var sent = cc.Single(ListRoute).Body!;
        Assert.AreEqual("25.10.2026", sent["start"]!.GetValue<string>());
        Assert.AreEqual("25.10.2026", sent["end"]!.GetValue<string>());
        Assert.AreEqual(0, sent["offset"]!.GetValue<int>());
        Assert.AreEqual(100, sent["limit"]!.GetValue<int>());
        Assert.DoesNotContain("comprador", body.ToJsonString());
    }

    [TestMethod]
    public async Task ListPayments_PorActualizacion_EmpiezaAntes_LoMasQuePuedeVivirUnaFactura()
    {
        var cc = new FakeCryptoCloud().On(ListRoute, Samples.List(0));
        await using var host = await CcHost.Start(cc);

        // Siempre 720 horas, con o sin invoice_hours: una factura creada antes de bajarlo pudo vivir más.
        await host.Send("/v0/payment/list_payments", Range("2026-10-25T10:00:00Z", "2026-10-25T11:00:00Z", by: "updated"));
        var config = CcHost.Config();
        config["invoice_hours"] = 6;
        await host.Send("/v0/payment/list_payments", Range("2026-10-25T10:00:00Z", "2026-10-25T11:00:00Z", by: "approved"), config: config);

        var sent = cc.Requests.Select(r => r.Body!).ToList();
        Assert.AreEqual("25.09.2026", sent[0]["start"]!.GetValue<string>());
        Assert.AreEqual("25.09.2026", sent[1]["start"]!.GetValue<string>());
        Assert.AreEqual("25.10.2026", sent[1]["end"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ListPayments_UnaFacturaQueNoSeLee_SeOmite_YElRestoSeConcilia()
    {
        // Un estado desconocido o una factura sin monto no puede trabar la conciliación de la instancia para siempre.
        var broken = Samples.Invoice(uuid: "INV-RARA0001")
            .Replace("\"amount_in_fiat\": 34.0,", string.Empty, StringComparison.Ordinal)
            .Replace("\"amount_usd\": 34.0,", string.Empty, StringComparison.Ordinal);
        var cc = new FakeCryptoCloud().On(ListRoute, Samples.List(3, Samples.Invoice(uuid: "INV-RARA0002", status: "frozen"), broken, Samples.Invoice()));
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send("/v0/payment/list_payments", Range("2026-10-25T00:00:00Z", "2026-10-26T00:00:00Z"));

        Assert.AreEqual(HttpStatusCode.OK, status);
        var payments = body!["result"]!["payments"]!.AsArray();
        Assert.HasCount(1, payments);
        Assert.AreEqual("INV-89UX09KA", payments[0]!["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual(3, body["result"]!["total"]!.GetValue<int>());
        Assert.IsNull(body["result"]!["next_offset"]);
    }

    [TestMethod]
    public async Task ListPayments_FacturaEnModoPrueba_NoSeDaPorPagada()
    {
        var cc = new FakeCryptoCloud().On(ListRoute, Samples.List(1, Samples.Invoice(testMode: true)));
        await using var host = await CcHost.Start(cc);
        var allowed = CcHost.Config();
        allowed["allow_test_invoices"] = true;

        var (_, body) = await host.Send("/v0/payment/list_payments", Range("2026-10-25T00:00:00Z", "2026-10-26T00:00:00Z"));
        var (_, test) = await host.Send("/v0/payment/list_payments", Range("2026-10-25T00:00:00Z", "2026-10-26T00:00:00Z"), config: allowed);

        Assert.AreEqual("rejected", body!["result"]!["payments"]![0]!["status"]!.GetValue<string>());
        Assert.AreEqual("test_mode", body["result"]!["payments"]![0]!["gateway_status_detail"]!.GetValue<string>());
        Assert.AreEqual("approved", test!["result"]!["payments"]![0]!["status"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ListPayments_LaUltimaPagina_NoTieneSiguiente_YElLimitEsLaPosicionFinal()
    {
        var cc = new FakeCryptoCloud().On(ListRoute, Samples.List(101, Samples.Invoice()));
        await using var host = await CcHost.Start(cc);

        var (_, body) = await host.Send("/v0/payment/list_payments", Range("2026-10-25T00:00:00Z", "2026-10-26T00:00:00Z", offset: 100));

        Assert.IsNull(body!["result"]!["next_offset"]);
        var sent = cc.Single(ListRoute).Body!;
        Assert.AreEqual(100, sent["offset"]!.GetValue<int>());
        Assert.AreEqual(200, sent["limit"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task ListPayments_UnaPaginaVacia_NoTieneSiguiente()
    {
        var cc = new FakeCryptoCloud().On(ListRoute, Samples.List(5));
        await using var host = await CcHost.Start(cc);

        var (_, body) = await host.Send("/v0/payment/list_payments", Range("2026-10-25T00:00:00Z", "2026-10-26T00:00:00Z", offset: 5));

        Assert.IsEmpty(body!["result"]!["payments"]!.AsArray());
        Assert.IsNull(body["result"]!["next_offset"]);
    }

    [TestMethod]
    public async Task ListPayments_SinResult_EsUpstreamUnavailable()
    {
        var cc = new FakeCryptoCloud().On(ListRoute, """{ "status": "success" }""");
        await using var host = await CcHost.Start(cc);

        var (status, body) = await host.Send("/v0/payment/list_payments", Range("2026-10-25T00:00:00Z", "2026-10-26T00:00:00Z"));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, CcHost.Code(body));
    }

    [TestMethod]
    public async Task ListPayments_ConDatosInvalidos_EsInvalidInput_SinSalirALaRed()
    {
        var cc = new FakeCryptoCloud();
        await using var host = await CcHost.Start(cc);

        var reversed = await host.Send("/v0/payment/list_payments", Range("2026-10-26T00:00:00Z", "2026-10-25T00:00:00Z"));
        var negative = await host.Send("/v0/payment/list_payments", Range("2026-10-25T00:00:00Z", "2026-10-26T00:00:00Z", offset: -1));
        var by = await host.Send("/v0/payment/list_payments", Range("2026-10-25T00:00:00Z", "2026-10-26T00:00:00Z", by: "paid"));

        CollectionAssert.AreEqual(new[] { "from", "to" }, CcHost.Strings(reversed.Body!["error"]!["details"]!["fields"]));
        CollectionAssert.AreEqual(new[] { "offset" }, CcHost.Strings(negative.Body!["error"]!["details"]!["fields"]));
        CollectionAssert.AreEqual(new[] { "by" }, CcHost.Strings(by.Body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(cc.Requests);
    }

    private static JsonObject Input(string id) => new() { ["payment_external_id"] = id };

    private static JsonObject Range(string from, string to, int offset = 0, string? by = null)
    {
        var input = new JsonObject { ["from"] = from, ["to"] = to, ["offset"] = offset };
        if (by is not null)
        {
            input["by"] = by;
        }

        return input;
    }
}
