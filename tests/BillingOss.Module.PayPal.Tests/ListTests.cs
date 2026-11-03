using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.PayPal.Tests;

/// <summary>payment.list_payments: la búsqueda de transacciones y el estado actual de cada captura que tocaron.</summary>
[TestClass]
public sealed class ListTests
{
    private const string SearchRoute = "GET /v1/reporting/transactions";
    private const string List = "/v0/payment/list_payments";
    private const string Old = "7AB12345CD6789012";

    [TestMethod]
    public async Task CadaCapturaTocadaSaleConSuEstadoActual()
    {
        // Un cobro nuevo, el reembolso de un cobro viejo, un retiro de la cuenta (T0400) y el cobro repetido como
        // transacción que no afecta el saldo. Del reembolso sale la captura vieja, ya reembolsada.
        var pp = new FakePayPal()
            .On(SearchRoute, Samples.Search(
                4,
                1,
                1,
                Samples.Transaction(Samples.CaptureId, "T0006"),
                Samples.Transaction("1JU08902781691411", "T1107", "-40.00", reference: Old),
                Samples.Transaction("5WD67891AB1234567", "T0400", "-500.00"),
                Samples.Transaction(Samples.CaptureId, "T0006")))
            .On($"GET /v2/payments/captures/{Samples.CaptureId}", Samples.Capture())
            .On($"GET /v2/payments/captures/{Old}", Samples.Capture(id: Old, status: "REFUNDED", amount: "40.00", customId: "att-0999"));
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(List, Range());

        Assert.AreEqual(HttpStatusCode.OK, status);
        var result = body!["result"]!;
        var payments = result["payments"]!.AsArray();
        Assert.HasCount(2, payments);
        Assert.AreEqual(Samples.CaptureId, payments[0]!["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual("approved", payments[0]!["status"]!.GetValue<string>());
        Assert.AreEqual(Old, payments[1]!["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual("refunded", payments[1]!["status"]!.GetValue<string>());
        Assert.AreEqual("att-0999", payments[1]!["external_reference"]!.GetValue<string>());
        Assert.AreEqual(4, result["total"]!.GetValue<int>());
        Assert.IsNull(result["next_offset"]);

        var search = pp.Single(SearchRoute);
        Assert.AreEqual("2026-10-25T00:00:00Z", search.Query("start_date"));
        Assert.AreEqual("2026-10-27T00:00:00Z", search.Query("end_date"));
        Assert.AreEqual("transaction_info", search.Query("fields"));
        Assert.AreEqual("20", search.Query("page_size"));
        Assert.AreEqual("1", search.Query("page"));
    }

    [TestMethod]
    public async Task ElOffsetEsLaPagina_YNextOffsetSigueMientrasHayaPaginas()
    {
        var pp = new FakePayPal()
            .On(SearchRoute, Samples.Search(45, 2, 3, Samples.Transaction(Samples.CaptureId, "T0006")))
            .On($"GET /v2/payments/captures/{Samples.CaptureId}", Samples.Capture());
        await using var host = await PpHost.Start(pp);
        var input = Range();
        input["offset"] = 20;

        var (_, body) = await host.Send(List, input);

        Assert.AreEqual("2", pp.Single(SearchRoute).Query("page"));
        Assert.AreEqual(20, body!["result"]!["offset"]!.GetValue<int>());
        Assert.AreEqual(40, body["result"]!["next_offset"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task SoloLosCobrosT0006YT0007SeConsultan()
    {
        // La cuenta es compartida: un cobro de otro tipo (T0000, T0011) no es una captura de órdenes y no se consulta.
        var pp = new FakePayPal()
            .On(SearchRoute, Samples.Search(
                3,
                1,
                1,
                Samples.Transaction(Old, "T0000"),
                Samples.Transaction("5WD67891AB1234567", "T0011"),
                Samples.Transaction(Samples.CaptureId, "T0006")))
            .On($"GET /v2/payments/captures/{Samples.CaptureId}", Samples.Capture());
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(List, Range());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.HasCount(1, body!["result"]!["payments"]!.AsArray());
        Assert.AreEqual(0, pp.Count($"GET /v2/payments/captures/{Old}"));
        Assert.AreEqual(0, pp.Count("GET /v2/payments/captures/5WD67891AB1234567"));
    }

    [TestMethod]
    public async Task UnaTransaccionQueNoEsCapturaDeOrdenes_SeSalta()
    {
        var pp = new FakePayPal()
            .On(SearchRoute, Samples.Search(1, 1, 1, Samples.Transaction(Samples.CaptureId, "T0007")))
            .On($"GET /v2/payments/captures/{Samples.CaptureId}", Samples.Error("RESOURCE_NOT_FOUND", "The specified resource does not exist."), HttpStatusCode.NotFound);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(List, Range());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.IsEmpty(body!["result"]!["payments"]!.AsArray());
    }

    [TestMethod]
    public async Task UnaCapturaQueEstaAppNoPuedeLeer_SeSalta_YLaPaginaSigue()
    {
        // La cuenta es compartida: una captura de otra app da 403 PERMISSION_DENIED y no tiene que frenar la conciliación.
        var pp = new FakePayPal()
            .On(SearchRoute, Samples.Search(2, 1, 1, Samples.Transaction(Old, "T0006"), Samples.Transaction(Samples.CaptureId, "T0006")))
            .On(
                $"GET /v2/payments/captures/{Old}",
                Samples.Error("NOT_AUTHORIZED", "Authorization failed due to insufficient permissions.", ("PERMISSION_DENIED", "You do not have permission to access or perform operations on this resource.")),
                HttpStatusCode.Forbidden)
            .On($"GET /v2/payments/captures/{Samples.CaptureId}", Samples.Capture());
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(List, Range());

        Assert.AreEqual(HttpStatusCode.OK, status);
        var payments = body!["result"]!["payments"]!.AsArray();
        Assert.HasCount(1, payments);
        Assert.AreEqual(Samples.CaptureId, payments[0]!["payment_external_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task SinPermisoDeBusqueda_EsInvalidConfig()
    {
        // La app de PayPal necesita Transaction search activado.
        var pp = new FakePayPal().On(SearchRoute, """{ "name": "NOT_AUTHORIZED", "message": "Authorization failed due to insufficient permissions." }""", HttpStatusCode.Forbidden);
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(List, Range());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, PpHost.Code(body));
    }

    [TestMethod]
    [DataRow("2026-10-27T00:00:00Z", "2026-10-25T00:00:00Z", 0, null, "from")]
    [DataRow("2026-09-01T00:00:00Z", "2026-10-25T00:00:00Z", 0, null, "from")]
    [DataRow("2026-10-25T00:00:00Z", "2026-10-27T00:00:00Z", 7, null, "offset")]
    [DataRow("2026-10-25T00:00:00Z", "2026-10-27T00:00:00Z", 0, "paid", "by")]
    public async Task EntradaInvalida_NoLlamaAPayPal(string from, string to, int offset, string? by, string field)
    {
        var pp = new FakePayPal();
        await using var host = await PpHost.Start(pp);

        var (status, body) = await host.Send(List, new JsonObject { ["from"] = from, ["to"] = to, ["offset"] = offset, ["by"] = by });

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, PpHost.Code(body));
        CollectionAssert.Contains(PpHost.Strings(body!["error"]!["details"]!["fields"]), field);
        Assert.IsEmpty(pp.Requests);
    }

    private static JsonObject Range() => new()
    {
        ["from"] = "2026-10-25T00:00:00Z",
        ["to"] = "2026-10-27T00:00:00Z",
        ["offset"] = 0,
        ["by"] = "updated",
    };
}
