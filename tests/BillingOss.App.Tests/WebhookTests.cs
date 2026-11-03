using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BillingOss.App.Tests;

/// <summary>Un mensaje que llegó al receptor falso, con lo necesario para verificar la firma por su cuenta.</summary>
internal sealed record ReceivedWebhook(string Id, string Timestamp, string Signature, string UserAgent, byte[] Body)
{
    public JsonNode Json => JsonNode.Parse(Body)!;

    public string Type => Json["type"]!.GetValue<string>();

    /// <summary>La verificación de Standard Webhooks, escrita de nuevo acá para no depender del código que firma.</summary>
    public bool SignedWith(string secret)
    {
        var key = Convert.FromBase64String(secret["whsec_".Length..]);
        var content = Encoding.UTF8.GetBytes($"{Id}.{Timestamp}.").Concat(Body).ToArray();
        var expected = "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, content));
        return Signature.Split(' ').Contains(expected);
    }
}

/// <summary>El receptor de una integración: un servidor real en 127.0.0.1 que responde los códigos que le planean.</summary>
internal sealed class WebhookReceiver : IAsyncDisposable
{
    private readonly WebApplication app;

    private WebhookReceiver(WebApplication app) => this.app = app;

    public ConcurrentQueue<ReceivedWebhook> Received { get; } = new();

    /// <summary>Códigos a responder en orden. Vacía, responde 200.</summary>
    public ConcurrentQueue<int> Plan { get; } = new();

    public string Url { get; private set; } = string.Empty;

    public static async Task<WebhookReceiver> Start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var receiver = new WebhookReceiver(app);
        app.MapPost("/hook", async (HttpContext http) =>
        {
            using var body = new MemoryStream();
            await http.Request.Body.CopyToAsync(body);
            var headers = http.Request.Headers;
            receiver.Received.Enqueue(new ReceivedWebhook(headers["webhook-id"].ToString(), headers["webhook-timestamp"].ToString(), headers["webhook-signature"].ToString(), headers.UserAgent.ToString(), body.ToArray()));
            return Results.StatusCode(receiver.Plan.TryDequeue(out var status) ? status : 200);
        });
        await app.StartAsync();
        receiver.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/hook";
        return receiver;
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}

[TestClass]
public sealed class WebhookTests
{
    [TestMethod]
    public void Firma_ElVectorDePruebaDeStandardWebhooks() =>
        Assert.AreEqual(
            "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=",
            WebhookSignature.Sign("whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw", "msg_p5jXN8AQM9LWM0D4loKWxJek", 1614265330, "{\"test\": 2432232314}"u8));

    [TestMethod]
    public void Catalogo_DataSoloConLosCamposPublicos()
    {
        var paid = PublicEvents.ByInternal(Events.InvoicePaid)!;
        var data = paid.Data(new JsonObject { ["document_id"] = 42, ["event"] = Events.InvoicePaid });

        Assert.AreEqual("invoice.paid", paid.Type);
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse("""{"invoice_id":42}"""), data));
        Assert.AreEqual("document:42", PublicEvents.Entity(new JsonObject { ["document_id"] = 42 }, "x"));
        Assert.AreEqual("service:7", PublicEvents.Entity(new JsonObject { ["service_id"] = 7, ["generation"] = 3 }, "x"));
        Assert.IsNull(PublicEvents.ByInternal(Events.PaymentAllocated), "Los eventos internos no salen.");
        Assert.AreEqual(PublicEvents.All.Count, PublicEvents.All.Select(e => e.Type).Distinct().Count());
    }

    [TestMethod]
    public async Task EventoPublicado_LlegaFirmadoSoloALosDestinosSuscriptos()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        await using var receiver = await WebhookReceiver.Start();
        var (_, invoices) = await Create(h, receiver.Url, "invoice.issued", "invoice.paid");
        var (_, orders) = await Create(h, receiver.Url, "order.placed");
        var off = await Create(h, receiver.Url, "invoice.paid");
        await h.Use<WebhookService, bool>(async s =>
        {
            await s.Update(off.Id, new WebhookInput(receiver.Url, null, ["invoice.paid"], Active: false));
            return true;
        });

        var invoiceId = await OrderAndPay(h);
        await h.RunJobs();

        var received = receiver.Received.ToList();
        Assert.HasCount(3, received);
        var byType = received.ToDictionary(r => r.Type);
        CollectionAssert.AreEquivalent(new[] { "order.placed", "invoice.issued", "invoice.paid" }, byType.Keys.ToArray());
        Assert.IsTrue(byType["invoice.issued"].SignedWith(invoices));
        Assert.IsTrue(byType["invoice.paid"].SignedWith(invoices));
        Assert.IsTrue(byType["order.placed"].SignedWith(orders));
        Assert.IsFalse(byType["order.placed"].SignedWith(invoices));
        Assert.AreEqual(invoiceId, byType["invoice.paid"].Json["data"]!["invoice_id"]!.GetValue<long>());
        Assert.AreEqual("2026-11-02T15:00:00.000Z", byType["invoice.paid"].Json["timestamp"]!.GetValue<string>());
        Assert.AreEqual("billing-oss-webhooks", byType["invoice.paid"].UserAgent);

        var deliveries = await h.Query(db => db.Set<WebhookDelivery>().ToListAsync());
        Assert.HasCount(3, deliveries);
        Assert.IsTrue(deliveries.All(d => d.Status == WebhookDeliveryStatus.Delivered && d.Attempts == 1 && d.LastStatusCode == 200));
        CollectionAssert.AreEquivalent(deliveries.Select(d => d.MessageId).ToList(), received.Select(r => r.Id).ToList());
    }

    [TestMethod]
    public async Task DestinoQueFalla_ReintentaConEsperaYRespetaElOrdenDeLaEntidad()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        await using var receiver = await WebhookReceiver.Start();
        await Create(h, receiver.Url, "invoice.issued", "invoice.paid");
        receiver.Plan.Enqueue(500);

        await OrderAndPay(h);
        await h.RunJobs();

        // La emitida falló y la pagada de la misma factura espera atrás, aunque su trabajo ya esté listo.
        Assert.AreEqual("invoice.issued", receiver.Received.Single().Type);
        var issued = await h.Query(db => db.Set<WebhookDelivery>().SingleAsync(d => d.EventType == "invoice.issued"));
        Assert.AreEqual(WebhookDeliveryStatus.Pending, issued.Status);
        Assert.AreEqual(1, issued.Attempts);
        Assert.AreEqual(500, issued.LastStatusCode);
        Assert.AreEqual("El destino respondió 500.", issued.LastError);

        h.Clock.Now = h.Clock.Now.AddMinutes(1);
        await h.RunJobs();

        CollectionAssert.AreEqual(new[] { "invoice.issued", "invoice.issued", "invoice.paid" }, receiver.Received.Select(r => r.Type).ToArray());
        Assert.AreEqual(receiver.Received.First().Id, receiver.Received.ElementAt(1).Id, "El reintento lleva el mismo webhook-id.");
        Assert.IsTrue(await h.Query(db => db.Set<WebhookDelivery>().AllAsync(d => d.Status == WebhookDeliveryStatus.Delivered)));
    }

    [TestMethod]
    public async Task UltimoIntento_QuedaFallidaConSuTrabajoMuerto()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        await using var receiver = await WebhookReceiver.Start();
        await Create(h, receiver.Url, "order.placed");
        receiver.Plan.Enqueue(503);
        await h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        await h.Db(db => db.Jobs.Where(j => j.Kind == JobKinds.WebhookDeliver).ExecuteUpdateAsync(s => s.SetProperty(j => j.Attempts, PublicEvents.Attempts - 1)));

        await h.RunJobs();

        var delivery = await h.Query(db => db.Set<WebhookDelivery>().SingleAsync());
        Assert.AreEqual(WebhookDeliveryStatus.Failed, delivery.Status);
        Assert.AreEqual(503, delivery.LastStatusCode);
        Assert.AreEqual(JobStatus.Dead, await h.Query(db => db.Jobs.Where(j => j.Kind == JobKinds.WebhookDeliver).Select(j => j.Status).SingleAsync()));
    }

    [TestMethod]
    public async Task DestinoApagadoAntesDeMandar_CancelaLaEntrega()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        await using var receiver = await WebhookReceiver.Start();
        var (id, _) = await Create(h, receiver.Url, "order.placed");
        await h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        await h.Use<WebhookService, bool>(async s =>
        {
            await s.Update(id, new WebhookInput(receiver.Url, "n8n", ["order.placed"], Active: false));
            return true;
        });

        await h.RunJobs();

        Assert.IsTrue(receiver.Received.IsEmpty);
        Assert.AreEqual(WebhookDeliveryStatus.Cancelled, await h.Query(db => db.Set<WebhookDelivery>().Select(d => d.Status).SingleAsync()));
    }

    [TestMethod]
    public async Task ReenvioYPrueba_SalenConElMismoWebhookIdYSoloAlDestino()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        await using var receiver = await WebhookReceiver.Start();
        var (id, secret) = await Create(h, receiver.Url, "order.placed");
        await h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        await h.RunJobs();
        var delivery = await h.Query(db => db.Set<WebhookDelivery>().SingleAsync());

        await h.Use<WebhookService, bool>(async s =>
        {
            await s.Resend(delivery.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Resend(delivery.Id));
            await s.SendTest(id);
            return true;
        });
        await h.RunJobs();

        var received = receiver.Received.ToList();
        Assert.HasCount(3, received);
        Assert.AreEqual(received[0].Id, received[1].Id);
        Assert.AreEqual("webhook.test", received[2].Type);
        Assert.IsTrue(JsonNode.DeepEquals(new JsonObject(), received[2].Json["data"]));
        Assert.IsTrue(received.All(r => r.SignedWith(secret)));
        Assert.AreEqual(2, await h.Query(db => db.Set<WebhookDelivery>().Where(d => d.Id == delivery.Id).Select(d => d.Attempts).SingleAsync()));
    }

    [TestMethod]
    public async Task Secreto_SeRotaYNuncaQuedaEnClaroNiEnLaAuditoria()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        await using var receiver = await WebhookReceiver.Start();
        var (id, first) = await Create(h, receiver.Url, "order.placed");
        var second = await h.Use<WebhookService, string>(s => s.RotateSecret(id));
        await h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        await h.RunJobs();

        StringAssert.StartsWith(first, "whsec_");
        Assert.AreNotEqual(first, second);
        Assert.IsTrue(receiver.Received.Single().SignedWith(second));
        Assert.IsFalse(receiver.Received.Single().SignedWith(first));
        var stored = await h.Query(db => db.Set<WebhookEndpoint>().Select(e => e.Secret).SingleAsync());
        Assert.IsFalse(Encoding.UTF8.GetString(stored).Contains(second["whsec_".Length..], StringComparison.Ordinal));
        var audit = (await h.Query(db => db.Set<AuditEvent>().Where(a => a.Action.StartsWith("webhooks.")).ToListAsync())).Select(a => $"{a.Action} {a.Details}").ToList();
        CollectionAssert.AreEquivalent(new[] { "webhooks.create", "webhooks.rotate" }, audit.Select(a => a.Split(' ')[0]).ToArray());
        Assert.IsFalse(audit.Any(a => a.Contains(first[6..], StringComparison.Ordinal) || a.Contains(second[6..], StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Alta_RechazaUrlEventosYDescripcionInvalidos()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        foreach (var input in new[]
        {
            new WebhookInput("http://example.com/hook", null, ["order.placed"]),
            new WebhookInput("ftp://127.0.0.1/hook", null, ["order.placed"]),
            new WebhookInput("https://example.com/hook", null, []),
            new WebhookInput("https://example.com/hook", null, ["OrderPlaced"]),
            new WebhookInput("https://example.com/hook", new string('x', 201), ["order.placed"]),
        })
        {
            await Assert.ThrowsAsync<ValidationException>(() => h.Use<WebhookService, (long, string)>(s => s.Create(input)), input.Url);
        }

        Assert.AreEqual(0, await h.Query(db => db.Set<WebhookEndpoint>().CountAsync()));
    }

    [TestMethod]
    public async Task Borrado_SeLlevaElRegistroYLoPendienteTermina()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        await using var receiver = await WebhookReceiver.Start();
        var (id, _) = await Create(h, receiver.Url, "order.placed");
        await h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));

        await h.Use<WebhookService, bool>(async s =>
        {
            await s.Delete(id);
            return true;
        });
        await h.RunJobs();

        Assert.IsTrue(receiver.Received.IsEmpty);
        Assert.AreEqual(0, await h.Query(db => db.Set<WebhookDelivery>().CountAsync()));
        Assert.AreEqual(JobStatus.Done, await h.Query(db => db.Jobs.Where(j => j.Kind == JobKinds.WebhookDeliver).Select(j => j.Status).SingleAsync()));
    }

    [TestMethod]
    public async Task Retencion_BorraLasEntregasViejasYDejaLasPendientes()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        await using var receiver = await WebhookReceiver.Start();
        await Create(h, receiver.Url, "order.placed");
        await h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        await h.RunJobs();
        await h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));

        // Las dos entregas tienen más de 30 días, pero la segunda sigue pendiente.
        h.Clock.Now = h.Clock.Now.AddDays(31);
        await h.Db(db => db.Jobs.Where(j => j.Kind == JobKinds.WebhookDeliver && j.Status == JobStatus.Pending).ExecuteUpdateAsync(s => s.SetProperty(j => j.RunAt, h.Clock.Now.AddDays(1))));
        await h.Use<JobQueue, bool>(async q =>
        {
            await q.Enqueue(RetentionHandler.JobKind, new { date = "hoy" });
            return true;
        });
        await h.RunJobs();

        Assert.AreEqual(WebhookDeliveryStatus.Pending, await h.Query(db => db.Set<WebhookDelivery>().Select(d => d.Status).SingleAsync()));
    }

    private static Task<(long Id, string Secret)> Create(CoreHarness h, string url, params string[] events) =>
        h.Use<WebhookService, (long, string)>(s => s.Create(new WebhookInput(url, "Prueba", events)));

    private static async Task<long> OrderAndPay(CoreHarness h)
    {
        var placed = await h.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        var invoiceId = placed.InvoiceId!.Value;
        await h.Use<PaymentService, long>(p => p.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, 12100m, PaymentStatus.Approved, [invoiceId])));
        return invoiceId;
    }
}
