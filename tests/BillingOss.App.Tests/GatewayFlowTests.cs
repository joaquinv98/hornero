using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>Pasarela falsa: firma de prueba en un encabezado y pagos en memoria.</summary>
internal sealed class FakeGateway
{
    public ConcurrentDictionary<string, JsonObject> Payments { get; } = new();

    public List<(JsonObject Input, string? Key)> Checkouts { get; } = [];

    public List<(JsonObject Input, string? Key)> Refunds { get; } = [];

    /// <summary>Las entradas de verify_webhook, tal como las mandó el núcleo.</summary>
    public List<JsonObject> Verifications { get; } = [];

    /// <summary>Código de error con que contesta refund, por ejemplo rejected cuando venció el plazo de la pasarela.</summary>
    public string? RefundError { get; set; }

    /// <summary>Corre una vez, antes de devolver un pago: ahí se simula una conmutación.</summary>
    public Func<Task>? BeforePayment { get; set; }

    /// <summary>Monedas que declara cobrar, como Mercado Pago con los pesos. Sin lista, cobra en cualquiera.</summary>
    public string[]? Currencies { get; init; }

    public ModuleDefinition Definition() => new ModuleDefinition(Manifest())
        .Operation("payment", "create_checkout", call =>
        {
            Checkouts.Add((call.Request.Input.DeepClone().AsObject(), call.IdempotencyKey));
            var reference = call.Request.Input["attempt_id"]!.GetValue<string>();
            return Task.FromResult(ModuleResponse.Completed(new JsonObject
            {
                ["redirect_url"] = $"https://pay.example.com/checkout?ref={reference}",
                ["checkout_external_id"] = $"chk-{reference}",
            }));
        })
        .Operation("payment", "verify_webhook", call =>
        {
            var input = call.Request.Input;
            Verifications.Add(input.DeepClone().AsObject());
            if (input["headers"]?["x-test-signature"]?.GetValue<string>() != "ok")
            {
                return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["valid"] = false, ["events"] = new JsonArray() }));
            }

            var body = JsonNode.Parse(Convert.FromBase64String(input["body_base64"]!.GetValue<string>()))!;
            var events = new JsonArray(new JsonObject
            {
                ["event_id"] = body["id"]!.GetValue<string>(),
                ["type"] = "payment.updated",
                ["payment_external_id"] = input["query"]!["data.id"]!.GetValue<string>(),
            });
            return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["valid"] = true, ["events"] = events }));
        }, mutating: false)
        .Operation("payment", "get_payment", async call =>
        {
            var id = call.Request.Input["payment_external_id"]!.GetValue<string>();
            if (!Payments.TryGetValue(id, out var p))
            {
                throw new ModuleException(ErrorCodes.NotFound, "No existe el pago");
            }

            if (BeforePayment is { } before)
            {
                BeforePayment = null;
                await before();
            }

            return ModuleResponse.Completed(p.DeepClone());
        }, mutating: false)
        .Operation("payment", "refund", call =>
        {
            Refunds.Add((call.Request.Input.DeepClone().AsObject(), call.IdempotencyKey));
            if (RefundError is { } code)
            {
                throw new ModuleException(code, "La pasarela no reembolsa este pago");
            }

            var id = call.Request.Input["payment_external_id"]!.GetValue<string>();
            var amount = call.Request.Input["amount"]!.DeepClone();
            var p = Payments[id];
            var total = decimal.Parse(p["refunded"]!["value"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)
                + decimal.Parse(amount["value"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
            p["refunded"]!["value"] = total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            p["status"] = total >= decimal.Parse(p["amount"]!["value"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) ? "refunded" : "partially_refunded";
            return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["refund_id"] = $"r-{Refunds.Count}", ["status"] = "approved", ["amount"] = amount }));
        })
        .Operation("payment", "list_payments", _ =>
            Task.FromResult(ModuleResponse.Completed(new JsonObject
            {
                ["payments"] = new JsonArray([.. Payments.Values.Select(p => (JsonNode)p.DeepClone())]),
                ["total"] = Payments.Count,
                ["offset"] = 0,
                ["next_offset"] = null,
            })), mutating: false);

    private JsonObject Manifest()
    {
        var manifest = new JsonObject
        {
            ["id"] = "pasarela",
            ["kinds"] = new JsonArray("payment"),
            ["capabilities"] = new JsonArray("payment.core", "payment.checkout", "payment.webhooks", "payment.reconcile", "payment.refund"),
        };
        if (Currencies is not null)
        {
            manifest["currencies"] = new JsonArray([.. Currencies.Select(c => (JsonNode)c)]);
        }

        return manifest;
    }

    public void Add(string id, string reference, string amount, string status = "approved", string refunded = "0.00") => Payments[id] = new JsonObject
    {
        ["payment_external_id"] = id,
        ["status"] = status,
        ["amount"] = new JsonObject { ["value"] = amount, ["currency"] = "ARS" },
        ["refunded"] = new JsonObject { ["value"] = refunded, ["currency"] = "ARS" },
        ["fee"] = new JsonObject { ["value"] = "10.00", ["currency"] = "ARS" },
        ["external_reference"] = reference,
    };
}

[TestClass]
public sealed class GatewayFlowTests
{
    internal static async Task<(CoreHarness H, TestModule Module, FakeGateway Gateway, long InvoiceId)> Start(bool allowHost = true, Action<IServiceCollection>? services = null, bool pooling = true)
    {
        var box = TestModule.NewBox();
        var h = await CoreHarness.Start(box, services: services, pooling: pooling);
        var gateway = new FakeGateway();
        var module = await TestModule.Start(gateway.Definition(), h, box);
        await h.Db(async db =>
        {
            (await db.ModuleInstances.SingleAsync()).BrowserHosts = allowHost ? """["pay.example.com"]""" : "[]";
            await db.SaveChangesAsync();
        });
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        return (h, module, gateway, placed.InvoiceId!.Value);
    }

    internal static Task<CheckoutStarted> Checkout(CoreHarness h, TestModule module, long invoiceId) =>
        h.Use<GatewayPayments, CheckoutStarted>(g => g.Checkout(h.AccountId, [invoiceId], module.InstanceId));

    internal static Task<bool> Notify(CoreHarness h, TestModule module, string eventId, string paymentId, string signature = "ok") =>
        h.Use<WebhookInbox, bool>(i => i.Receive(
            module.InstanceId,
            new JsonObject { ["x-test-signature"] = signature },
            new JsonObject { ["data.id"] = paymentId, ["type"] = "payment" },
            Encoding.UTF8.GetBytes(new JsonObject { ["id"] = eventId, ["data"] = new JsonObject { ["id"] = paymentId } }.ToJsonString())));

    [TestMethod]
    public async Task Checkout_AbreElIntentoYDevuelveLaUrlDeLaPasarela()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;

        var started = await Checkout(h, module, invoiceId);

        Assert.AreEqual($"https://pay.example.com/checkout?ref={started.Reference}", started.RedirectUrl);
        Assert.AreEqual(32, started.Reference.Length);
        var (input, key) = gateway.Checkouts.Single();
        Assert.AreEqual(started.Reference, input["attempt_id"]!.GetValue<string>());
        Assert.AreEqual("12100.00", input["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual($"https://billing.example.com/webhooks/{module.InstanceId}", input["notification_url"]!.GetValue<string>());
        Assert.AreEqual($"https://billing.example.com/pay/return/{started.Reference}", input["return_urls"]!["success"]!.GetValue<string>());
        Assert.IsNotNull(key);
        var attempt = await h.Query(db => db.PaymentAttempts.SingleAsync());
        Assert.AreEqual(12100m, attempt.Amount);
        CollectionAssert.AreEqual(new[] { invoiceId }, attempt.DocumentIds.ToArray());
        Assert.AreEqual(started.RedirectUrl, attempt.RedirectUrl);
    }

    [TestMethod]
    public async Task Checkout_HostQueNoEstaEnLaInstancia_NoSeUsa()
    {
        var (h, module, _, invoiceId) = await Start(allowHost: false);
        await using var _h = h;
        await using var _m = module;

        await Assert.ThrowsExactlyAsync<PermanentFailureException>(() => Checkout(h, module, invoiceId));
        Assert.IsNull((await h.Query(db => db.PaymentAttempts.SingleAsync())).RedirectUrl);
    }

    [TestMethod]
    public async Task Checkout_AUnModuloQueNoEsDeCobros_NoAbreIntentoNiLoLlama()
    {
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        var calls = 0;
        var definition = new ModuleDefinition(new JsonObject { ["id"] = "proxmox", ["kinds"] = new JsonArray("provisioning") })
            .Operation("payment", "create_checkout", _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["redirect_url"] = "https://pay.example.com/x" }));
            });
        await using var module = await TestModule.Start(definition, h, box);
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));

        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() =>
            h.Use<GatewayPayments, CheckoutStarted>(g => g.Checkout(h.AccountId, [placed.InvoiceId!.Value], module.InstanceId)));

        Assert.AreEqual(0, calls);
        Assert.AreEqual(0, await h.Query(db => db.PaymentAttempts.CountAsync()));
    }

    [TestMethod]
    public async Task Aviso_ConPagoAprobado_PagaLaFacturaYCierraElIntento()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;
        var started = await Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "12100.00");

        Assert.IsTrue(await Notify(h, module, "evt-1", "pay-1"));
        await h.RunJobs();

        Assert.AreEqual(0m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId))).Pending);
        var payment = await h.Query(db => db.Payments.SingleAsync());
        Assert.AreEqual($"module:{module.InstanceId}", payment.Channel);
        Assert.AreEqual(PaymentStatus.Approved, payment.Status);
        var attempt = await h.Query(db => db.PaymentAttempts.SingleAsync());
        Assert.AreEqual(AttemptStatus.Paid, attempt.Status);
        Assert.AreEqual(payment.Id, attempt.PaymentId);
        var receipt = await h.Query(db => db.WebhookReceipts.SingleAsync());
        Assert.AreEqual(ReceiptStatus.Verified, receipt.Status);

        // El módulo recibe cuándo llegó el aviso: contra ese instante mide su ventana de tiempo, porque verifica después.
        var verification = gateway.Verifications.Single();
        Assert.AreEqual(receipt.ReceivedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture), verification["received_at"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Aviso_Repetido_SeGuardaDosVecesPeroSeProcesaUna()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;
        var started = await Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "12100.00");

        await Notify(h, module, "evt-1", "pay-1");
        await Notify(h, module, "evt-1", "pay-1");
        await h.RunJobs();

        Assert.AreEqual(2, await h.Query(db => db.WebhookReceipts.CountAsync()));
        Assert.AreEqual(1, await h.Query(db => db.GatewayEvents.CountAsync()));
        Assert.AreEqual(1, await h.Query(db => db.Payments.CountAsync()));
    }

    [TestMethod]
    public async Task Aviso_ConFirmaInvalida_NoRegistraNada()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;
        var started = await Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "12100.00");

        await Notify(h, module, "evt-1", "pay-1", signature: "falsa");
        await h.RunJobs();

        var receipt = await h.Query(db => db.WebhookReceipts.SingleAsync());
        Assert.AreEqual(ReceiptStatus.Invalid, receipt.Status);
        // Cualquiera puede mandar basura a la URL: lo que no vale no ocupa lugar.
        Assert.IsEmpty(receipt.Body);
        Assert.AreEqual(0, await h.Query(db => db.GatewayEvents.CountAsync()));
        Assert.AreEqual(0, await h.Query(db => db.Payments.CountAsync()));
    }

    [TestMethod]
    public async Task Retencion_BorraLosAvisosInvalidosViejos_YDejaLosValidosLosEventosYLosSinProcesar()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;
        var started = await Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "12100.00");
        await Notify(h, module, "evt-1", "pay-1");
        await Notify(h, module, "evt-2", "pay-1", signature: "falsa");
        await Notify(h, module, "evt-3", "pay-1");
        await h.RunJobs();
        var ids = await h.Query(db => db.WebhookReceipts.OrderBy(r => r.Id).Select(r => r.Id).ToListAsync());
        var old = h.Clock.Now - TimeSpan.FromDays(31);
        // Los dos primeros pasaron Workers:KeepDone y el tercero es de hoy. Uno viejo sin procesar se queda: lo muestra el monitoreo.
        await h.Db(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE payments.webhook_receipts SET received_at = {old} WHERE id IN ({ids[0]}, {ids[1]})"));
        var stuck = await h.Query(async db =>
        {
            var receipt = new WebhookReceipt { ModuleInstanceId = module.InstanceId, ReceivedAt = old };
            db.WebhookReceipts.Add(receipt);
            await db.SaveChangesAsync();
            return receipt.Id;
        });

        await h.Use<JobQueue, bool>(async q => { await q.Enqueue(RetentionHandler.JobKind, new { }); return true; });
        await h.RunJobs();

        // Se va solo el inválido viejo, que ya no tenía cuerpo ni eventos. El válido viejo conserva la fila y el cuerpo
        // hasta Workers:KeepWebhookBodies (RetentionTests), y los eventos siguen siendo la unicidad que frena una repetición.
        CollectionAssert.AreEqual(new[] { ids[0], ids[2], stuck }, await h.Query(db => db.WebhookReceipts.OrderBy(r => r.Id).Select(r => r.Id).ToListAsync()));
        Assert.IsNotEmpty((await h.Query(db => db.WebhookReceipts.SingleAsync(r => r.Id == ids[0]))).Body);
        var events = await h.Query(db => db.GatewayEvents.OrderBy(e => e.Id).ToListAsync());
        CollectionAssert.AreEqual(new[] { "evt-1", "evt-3" }, events.Select(e => e.EventId).ToArray());
        CollectionAssert.AreEqual(new[] { ids[0], ids[2] }, events.Select(e => e.ReceiptId).ToArray());
    }

    [TestMethod]
    public async Task Pago_ConOtroMonto_VaASaldoAFavorSinImputar()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;
        var started = await Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "100.00");

        await Notify(h, module, "evt-1", "pay-1");
        await h.RunJobs();

        Assert.AreEqual(12100m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId))).Pending);
        Assert.AreEqual(100m, (await h.Query(db => db.CreditBalances.SingleAsync())).Balance);
        Assert.AreEqual(AttemptStatus.Paid, (await h.Query(db => db.PaymentAttempts.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task Pago_RegistradoALaVezPorOtroProceso_IgualCierraElIntento()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;
        var started = await Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "12100.00");

        // Otro proceso inserta el mismo pago y confirma mientras este espera: Record choca con la unicidad y reintenta.
        await h.Db(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Payments.Add(new Payment(h.AccountId, GatewayPayments.Channel(module.InstanceId), "pay-1", Domain.Shared.Currency.Ars, 12100m, PaymentStatus.Pending, h.Clock.Now, 10m));
            await db.SaveChangesAsync();
            var sync = h.Use<GatewayPayments, bool>(async g =>
            {
                await g.Sync(module.InstanceId, "pay-1");
                return true;
            });
            for (var i = 0; i < 200 && await h.Query(q => q.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())").SingleAsync()) == 0; i++)
            {
                await Task.Delay(50);
            }

            await tx.CommitAsync();
            await sync;
        });

        Assert.AreEqual(0m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId))).Pending);
        Assert.AreEqual(AttemptStatus.Paid, (await h.Query(db => db.PaymentAttempts.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task Pago_SinIntentoPropio_NoSeRegistra()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;
        await Checkout(h, module, invoiceId);
        gateway.Add("pay-9", "de-otro-sistema", "12100.00");

        await Notify(h, module, "evt-9", "pay-9");
        await h.RunJobs();

        Assert.AreEqual(1, await h.Query(db => db.GatewayEvents.CountAsync()));
        Assert.AreEqual(0, await h.Query(db => db.Payments.CountAsync()));
    }

    [TestMethod]
    public async Task Reembolso_InformadoPorLaPasarela_DesimputaYLaFacturaVuelveADeber()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;
        var started = await Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "12100.00");
        await Notify(h, module, "evt-1", "pay-1");
        await h.RunJobs();
        Assert.AreEqual(new DateOnly(2026, 12, 2), (await h.Query(db => db.Services.SingleAsync())).PaidThrough);

        // El staff lo devolvió desde el panel de la pasarela.
        gateway.Add("pay-1", started.Reference, "12100.00", "refunded", "12100.00");
        await Notify(h, module, "evt-2", "pay-1");
        await h.RunJobs();

        Assert.AreEqual(12100m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId))).Pending);
        var payment = await h.Query(db => db.Payments.SingleAsync());
        Assert.AreEqual(PaymentStatus.Refunded, payment.Status);
        Assert.AreEqual(12100m, payment.Refunded);
        Assert.AreEqual(new DateOnly(2026, 11, 2), (await h.Query(db => db.Services.SingleAsync())).PaidThrough);
        var refund = await h.Query(db => db.Refunds.SingleAsync());
        Assert.AreEqual(RefundOrigin.Gateway, refund.Origin);
        Assert.AreEqual(0m, refund.Uncovered);

        // Avisarlo de nuevo no devuelve dos veces.
        await Notify(h, module, "evt-3", "pay-1");
        await h.RunJobs();
        Assert.AreEqual(1, await h.Query(db => db.Refunds.CountAsync()));
        Assert.AreEqual(12100m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId))).Pending);
    }

    [TestMethod]
    public async Task Pago_QueOtroProcesoRegistraALaVez_IgualCierraElIntento()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;
        var started = await Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "12100.00");

        // Otra transacción inserta el mismo pago sin confirmar: el alta de Record espera, choca con la unicidad al
        // confirmarse la otra y reintenta con el contexto limpio.
        await h.Db(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Payments.Add(new Payment(h.AccountId, GatewayPayments.Channel(module.InstanceId), "pay-1", Currency.Ars, 12100m, PaymentStatus.Pending, h.Clock.Now));
            await db.SaveChangesAsync();
            var sync = h.Use<GatewayPayments, bool>(async g =>
            {
                await g.Sync(module.InstanceId, "pay-1");
                return true;
            });
            for (var i = 0; i < 200 && !await Waiting(h); i++)
            {
                await Task.Delay(50);
            }

            await tx.CommitAsync();
            await sync;
        });

        Assert.AreEqual(0m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId))).Pending);
        Assert.AreEqual(AttemptStatus.Paid, (await h.Query(db => db.PaymentAttempts.SingleAsync())).Status);
    }

    private static Task<bool> Waiting(CoreHarness h) =>
        h.Query(db => db.Database.SqlQueryRaw<bool>("""SELECT EXISTS (SELECT 1 FROM pg_locks l JOIN pg_database d ON d.oid = l.database WHERE NOT l.granted AND d.datname = current_database()) AS "Value" """).SingleAsync());

    [TestMethod]
    public async Task Reembolso_DespuesDeUnaNotaParcial_SaleDelSaldoQueDejoLaNota_YLaFacturaSiguePagada()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;
        var started = await Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "12100.00");
        await Notify(h, module, "evt-1", "pay-1");
        await h.RunJobs();

        // Una bonificación sobre la factura pagada deja 1210 de ese pago como saldo, y el staff los devuelve desde el panel.
        await h.Use<CreditService, long>(s => s.IssueCreditNote(invoiceId, [new CreditLine("Bonificación", 1, 1210m, TaxTreatment.Taxed, 0.21m)], "Ajuste"));
        gateway.Add("pay-1", started.Reference, "12100.00", "partially_refunded", "1210.00");
        await Notify(h, module, "evt-2", "pay-1");
        await h.RunJobs();

        var invoice = await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId));
        Assert.AreEqual(InvoiceStatus.Paid, invoice.Status);
        Assert.AreEqual(10890m, invoice.Allocated);
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        Assert.AreEqual(0m, (await h.Query(db => db.Refunds.SingleAsync())).Uncovered);
    }

    [TestMethod]
    public async Task Reembolso_SinElImporteDevuelto_ConEstadoReembolsado_SeTomaCompleto()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;
        var started = await Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "12100.00");
        await Notify(h, module, "evt-1", "pay-1");
        await h.RunJobs();

        // El protocolo no obliga a informar el importe devuelto: un pago reembolsado sin ese dato se devolvió entero.
        gateway.Add("pay-1", started.Reference, "12100.00", "refunded");
        gateway.Payments["pay-1"].Remove("refunded");
        await Notify(h, module, "evt-2", "pay-1");
        await h.RunJobs();

        Assert.AreEqual(12100m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId))).Pending);
        Assert.AreEqual(12100m, (await h.Query(db => db.Payments.SingleAsync())).Refunded);
    }

    [TestMethod]
    public async Task Reembolso_DeUnPagoQueFueASaldo_BajaElSaldo()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;
        var started = await Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "100.00");
        await Notify(h, module, "evt-1", "pay-1");
        await h.RunJobs();

        gateway.Add("pay-1", started.Reference, "100.00", "partially_refunded", "60.00");
        await Notify(h, module, "evt-2", "pay-1");
        await h.RunJobs();

        Assert.AreEqual(40m, (await h.Query(db => db.CreditBalances.SingleAsync())).Balance);
        Assert.AreEqual(12100m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId))).Pending);
    }

    [TestMethod]
    public async Task PrimerAviso_YaReembolsadoEnParte_ImputaLoCobradoYDevuelveElResto()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;
        var started = await Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "12100.00", "partially_refunded", "2100.00");

        await Notify(h, module, "evt-1", "pay-1");
        await h.RunJobs();

        Assert.AreEqual(2100m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId))).Pending);
        Assert.AreEqual(PaymentStatus.PartiallyRefunded, (await h.Query(db => db.Payments.SingleAsync())).Status);
        Assert.AreEqual(AttemptStatus.Paid, (await h.Query(db => db.PaymentAttempts.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task Contracargo_RevierteLasImputaciones_ConOSinDisputaPrevia()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;
        var started = await Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "12100.00");
        await Notify(h, module, "evt-1", "pay-1");
        await h.RunJobs();

        gateway.Add("pay-1", started.Reference, "12100.00", "disputed");
        await Notify(h, module, "evt-2", "pay-1");
        await h.RunJobs();
        Assert.AreEqual(0m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId))).Pending);

        gateway.Add("pay-1", started.Reference, "12100.00", "charged_back");
        await Notify(h, module, "evt-3", "pay-1");
        await h.RunJobs();

        Assert.AreEqual(12100m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId))).Pending);
        Assert.AreEqual(PaymentStatus.ChargedBack, (await h.Query(db => db.Payments.SingleAsync())).Status);
        Assert.AreEqual(RefundOrigin.Chargeback, (await h.Query(db => db.Refunds.SingleAsync())).Origin);
    }

    [TestMethod]
    public async Task Contracargo_SinVerLaDisputa_IgualRevierte()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;
        var started = await Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "12100.00");
        await Notify(h, module, "evt-1", "pay-1");
        await h.RunJobs();

        // Mercado Pago no avisa los contracargos: la conciliación encuentra el pago ya contracargado.
        gateway.Add("pay-1", started.Reference, "12100.00", "charged_back");
        await h.Tick();
        await h.RunJobs();

        Assert.AreEqual(12100m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId))).Pending);
        Assert.AreEqual(PaymentStatus.ChargedBack, (await h.Query(db => db.Payments.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task Conciliacion_EncuentraElPagoQueNoLlegoPorAviso()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;
        var started = await Checkout(h, module, invoiceId);
        gateway.Add("pay-1", started.Reference, "12100.00");
        gateway.Add("pay-2", "de-otro-sistema", "500.00");

        await h.Tick();
        await h.RunJobs();

        Assert.AreEqual(0m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId))).Pending);
        Assert.AreEqual("pay-1", (await h.Query(db => db.Payments.SingleAsync())).ExternalId);

        // El pago sin intento propio no se registra: queda a la vista del staff, con alerta, y no se duplica en la próxima vuelta.
        var unmatched = await h.Query(db => db.UnmatchedPayments.SingleAsync());
        Assert.AreEqual("pay-2", unmatched.PaymentExternalId);
        Assert.AreEqual("de-otro-sistema", unmatched.ExternalReference);
        Assert.AreEqual(500m, unmatched.Amount);
        Assert.IsNull(unmatched.ResolvedAt);
        Assert.AreEqual(1d, (await h.Query(db => BusinessMetrics.Read(db, h.Clock.Now, CancellationToken.None))).Single(r => r.Name == "payments_unmatched").Value);
        h.Clock.Now = h.Clock.Now.AddHours(1);
        await h.Tick();
        await h.RunJobs();
        Assert.AreEqual(1, await h.Query(db => db.UnmatchedPayments.CountAsync()));
    }

    [TestMethod]
    public async Task BarridoDeCheckouts_EncuentraElPagoSinAviso_SinPagoNoEsFalla_YNoMiraLosViejos()
    {
        var (h, module, gateway, invoiceId) = await Start();
        await using var _ = h;
        await using var __ = module;

        // El ID del checkout que dio la pasarela queda en el intento.
        var old = await Checkout(h, module, invoiceId);
        Assert.AreEqual($"chk-{old.Reference}", (await h.Query(db => db.PaymentAttempts.SingleAsync())).CheckoutExternalId);

        // El planificador encola el barrido porque hay una pasarela, y sin pago el barrido termina bien: el intento sigue abierto.
        await using (var scope = h.Node().CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            await Scheduler.Tick(sp.GetRequiredService<BillingDbContext>(), sp.GetRequiredService<JobQueue>(), sp.GetRequiredService<BillingCore>(), CancellationToken.None);
        }

        Assert.AreEqual(JobStatus.Pending, (await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.CheckoutSweep))).Status);
        await h.RunJobs();
        Assert.AreEqual(JobStatus.Done, (await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.CheckoutSweep))).Status);
        Assert.AreEqual(AttemptStatus.Open, (await h.Query(db => db.PaymentAttempts.SingleAsync())).Status);

        // Pasada la ventana en que el checkout todavía se puede cobrar, el intento viejo no se consulta más.
        h.Clock.Now = h.Clock.Now.Add(CheckoutSweepHandler.Window + TimeSpan.FromHours(1));
        gateway.Add($"chk-{old.Reference}", old.Reference, "12100.00");
        await Sweep(h);
        Assert.AreEqual(AttemptStatus.Open, (await h.Query(db => db.PaymentAttempts.SingleAsync(a => a.Reference == old.Reference))).Status);

        // Un intento nuevo cuyo pago apareció en la pasarela sin aviso: el barrido lo encuentra y cierra el intento.
        var fresh = await Checkout(h, module, invoiceId);
        gateway.Add($"chk-{fresh.Reference}", fresh.Reference, "12100.00");
        await Sweep(h);
        Assert.AreEqual(AttemptStatus.Paid, (await h.Query(db => db.PaymentAttempts.SingleAsync(a => a.Reference == fresh.Reference))).Status);
        Assert.AreEqual(0m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId))).Pending);
        Assert.IsFalse(await h.Query(db => db.Jobs.AnyAsync(j => j.Status == JobStatus.Dead)));
    }

    private static async Task Sweep(CoreHarness h)
    {
        await h.Use<JobQueue, bool>(async jobs =>
        {
            await jobs.Enqueue(JobKinds.CheckoutSweep, new { }, JobKinds.CheckoutSweep);
            return true;
        });
        await h.RunJobs();
    }
}

[TestClass]
public sealed class WebhookEndpointTests
{
    private const string Gateway = """{"id":"pasarela","kinds":["payment"],"capabilities":["payment.core","payment.webhooks"]}""";

    private static async Task<long> Instance(WebHarness web, string manifest = Gateway) => await web.Query(async db =>
    {
        var installation = new ModuleInstallation { Name = "pasarela", BaseUrl = "http://127.0.0.1:9/", Manifest = manifest, CreatedAt = DateTimeOffset.UtcNow };
        db.ModuleInstallations.Add(installation);
        await db.SaveChangesAsync();
        var instance = new ModuleInstance { InstallationId = installation.Id, Name = "instancia", CreatedAt = DateTimeOffset.UtcNow };
        db.ModuleInstances.Add(instance);
        await db.SaveChangesAsync();
        return instance.Id;
    });

    [TestMethod]
    public async Task Aviso_SeGuardaSinCookieNiAuthorization()
    {
        await using var web = await WebHarness.Start();
        var id = await Instance(web);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{id}?data.id=123&type=payment") { Content = new StringContent("""{"id":"evt"}""", Encoding.UTF8, "application/json") };
        request.Headers.Add("Cookie", "sesion=secreta");
        request.Headers.Add("Authorization", "Bearer secreto");
        request.Headers.Add("x-signature", "ts=1,v1=abc");

        using var response = await web.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var receipt = await web.Query(db => db.WebhookReceipts.SingleAsync());
        var headers = JsonNode.Parse(receipt.Headers)!.AsObject();
        Assert.IsFalse(headers.Any(h => h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual("ts=1,v1=abc", headers["x-signature"]!.GetValue<string>());
        Assert.AreEqual("123", JsonNode.Parse(receipt.Query)!["data.id"]!.GetValue<string>());
        Assert.AreEqual("""{"id":"evt"}""", Encoding.UTF8.GetString(receipt.Body));
        Assert.AreEqual(1, await web.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.WebhookVerify)));
    }

    [TestMethod]
    public async Task Aviso_MasGrandeQueElTope_NoSeGuarda()
    {
        await using var web = await WebHarness.Start();
        var id = await Instance(web);

        // Las pasarelas reales mandan pocos KiB: lo que pasa de 64 KiB es basura y no llega a la base.
        using var response = await web.Client.PostAsync($"/webhooks/{id}", new ByteArrayContent(new byte[(64 * 1024) + 1]));

        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.AreEqual(0, await web.Query(db => db.WebhookReceipts.CountAsync()));
    }

    [TestMethod]
    public async Task Aviso_ElTopePorInstanciaNoSeEsquivaConCerosNiSigno()
    {
        await using var web = await WebHarness.Start();
        var last = HttpStatusCode.OK;
        for (var i = 0; i < 1000 && last != HttpStatusCode.TooManyRequests; i++)
        {
            using var response = await web.Client.PostAsync(new Uri("/webhooks/999", UriKind.Relative), new StringContent("{}"));
            last = response.StatusCode;
        }

        Assert.AreEqual(HttpStatusCode.TooManyRequests, last);
        foreach (var variant in new[] { "0999", "000999", "+999" })
        {
            using var response = await web.Client.PostAsync(new Uri($"/webhooks/{variant}", UriKind.Relative), new StringContent("{}"));
            Assert.AreEqual(HttpStatusCode.TooManyRequests, response.StatusCode, variant);
        }
    }

    [TestMethod]
    public async Task Aviso_RotarIdsQueNoExistenNoEsquivaElTopePorIp()
    {
        await using var web = await WebHarness.Start();
        // Con más de 64 KiB el aviso se corta antes de ir a la base: la prueba no depende de las conexiones libres
        // del PostgreSQL compartido.
        var body = new byte[(64 * 1024) + 1];
        var last = HttpStatusCode.RequestEntityTooLarge;
        for (var i = 0; i < 1000 && last != HttpStatusCode.TooManyRequests; i++)
        {
            using var response = await web.Client.PostAsync(new Uri($"/webhooks/{1000 + i}", UriKind.Relative), new ByteArrayContent(body));
            last = response.StatusCode;
        }

        Assert.AreEqual(HttpStatusCode.TooManyRequests, last);
    }

    [TestMethod]
    [DataRow("""{"id":"proxmox","kinds":["provisioning"],"capabilities":["payment.webhooks"]}""")]
    [DataRow("""{"id":"pasarela","kinds":["payment"],"capabilities":["payment.core","payment.checkout"]}""")]
    public async Task Aviso_AUnaInstanciaQueNoRecibeAvisosDeCobro_DaNoEncontrado(string manifest)
    {
        await using var web = await WebHarness.Start();
        var id = await Instance(web, manifest);

        using var response = await web.Client.PostAsync($"/webhooks/{id}", new StringContent("{}"));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual(0, await web.Query(db => db.WebhookReceipts.CountAsync()));
        Assert.AreEqual(0, await web.Query(db => db.Jobs.CountAsync()));
    }

    [TestMethod]
    public async Task Aviso_DeUnaInstanciaQueNoExiste_DaNoEncontrado()
    {
        await using var web = await WebHarness.Start();

        using var response = await web.Client.PostAsync("/webhooks/999", new StringContent("{}"));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>Reembolsos del staff (sección 12.4 del modelo de dominio): primero la nota de crédito y después la plata.</summary>
[TestClass]
public sealed class RefundFlowTests
{
    private static async Task<(CoreHarness H, TestModule Module, FakeGateway Gateway, long InvoiceId, long PaymentId)> Paid()
    {
        var box = TestModule.NewBox();
        var h = await CoreHarness.Start(box);
        var gateway = new FakeGateway();
        var module = await TestModule.Start(gateway.Definition(), h, box);
        await h.Db(async db =>
        {
            (await db.ModuleInstances.SingleAsync()).BrowserHosts = """["pay.example.com"]""";
            await db.SaveChangesAsync();
        });
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        var started = await h.Use<GatewayPayments, CheckoutStarted>(g => g.Checkout(h.AccountId, [placed.InvoiceId!.Value], module.InstanceId));
        gateway.Add("pay-1", started.Reference, "12100.00");
        await h.Use<GatewayPayments, bool>(async g =>
        {
            await g.Sync(module.InstanceId, "pay-1");
            return true;
        });
        await h.RunJobs();
        return (h, module, gateway, placed.InvoiceId!.Value, await h.Query(db => db.Payments.Select(p => p.Id).SingleAsync()));
    }

    private static Task<long> Refund(CoreHarness h, long paymentId, long invoiceId, decimal amount = 12100m) =>
        h.Use<PaymentService, long>(s => s.RequestRefund(paymentId, invoiceId, [new CreditLine("Devolución", 1, amount, TaxTreatment.Taxed, 0.21m)], "Arrepentimiento"));

    private static Task Sync(CoreHarness h, TestModule module) =>
        h.Use<GatewayPayments, bool>(async g =>
        {
            await g.Sync(module.InstanceId, "pay-1");
            return true;
        });

    [TestMethod]
    public async Task Reembolso_EmiteLaNotaYDevuelveLaPlataPorLaPasarela_SinDescontarDosVeces()
    {
        var (h, module, gateway, invoiceId, paymentId) = await Paid();
        await using var _ = h;
        await using var __ = module;

        var refundId = await Refund(h, paymentId, invoiceId);

        // La nota sale en la misma transacción: lo pagado queda como saldo a favor hasta que la pasarela devuelve.
        var note = await h.Query(db => db.Documents.SingleAsync(d => d.Kind == DocumentKind.CreditNote));
        Assert.AreEqual(12100m, note.Total);
        Assert.AreEqual(12100m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        await h.RunJobs();

        var (input, key) = gateway.Refunds.Single();
        Assert.AreEqual($"refund-{refundId}", key);
        Assert.AreEqual("pay-1", input["payment_external_id"]!.GetValue<string>());
        Assert.AreEqual("12100.00", input["amount"]!["value"]!.GetValue<string>());
        var refund = await h.Query(db => db.Refunds.SingleAsync());
        Assert.AreEqual(RefundStatus.Completed, refund.Status);
        Assert.AreEqual(note.Id, refund.CreditNoteId);
        Assert.AreEqual("r-1", refund.ExternalId);
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        var payment = await h.Query(db => db.Payments.SingleAsync());
        Assert.AreEqual(PaymentStatus.Refunded, payment.Status);
        Assert.AreEqual(12100m, payment.Refunded);

        // La pasarela confirma el reembolso: ya está en las cuentas y la factura sigue anulada.
        await Sync(h, module);
        var invoice = await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId));
        Assert.AreEqual(InvoiceStatus.Cancelled, invoice.Status);
        Assert.AreEqual(12100m, invoice.Refunded);
        Assert.AreEqual(1, await h.Query(db => db.Refunds.CountAsync()));
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
    }

    [TestMethod]
    public async Task Reembolso_Parcial_DejaElPagoReembolsadoEnParte()
    {
        var (h, module, gateway, invoiceId, paymentId) = await Paid();
        await using var _ = h;
        await using var __ = module;

        await Refund(h, paymentId, invoiceId, 1210m);
        await h.RunJobs();
        await Sync(h, module);

        var payment = await h.Query(db => db.Payments.SingleAsync());
        Assert.AreEqual(PaymentStatus.PartiallyRefunded, payment.Status);
        Assert.AreEqual(1210m, payment.Refunded);
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        Assert.AreEqual(InvoiceStatus.Paid, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId))).Status);
        Assert.AreEqual("1210.00", gateway.Refunds.Single().Input["amount"]!["value"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Reembolso_QueLaPasarelaRechaza_QuedaComoSaldo_YSiDespuesSeDevuelveDesdeElPanelSeCierra()
    {
        var (h, module, gateway, invoiceId, paymentId) = await Paid();
        await using var _ = h;
        await using var __ = module;
        gateway.RefundError = "rejected";

        await Refund(h, paymentId, invoiceId);
        await h.RunJobs();

        var refund = await h.Query(db => db.Refunds.SingleAsync());
        Assert.AreEqual(RefundStatus.Failed, refund.Status);
        Assert.IsNotNull(refund.Error);
        Assert.AreEqual(12100m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        Assert.AreEqual(0m, (await h.Query(db => db.Payments.SingleAsync())).Refunded);

        // El staff devolvió la plata desde el panel: la pasarela lo informa y se cierra el reembolso que había fallado.
        gateway.Add("pay-1", gateway.Payments["pay-1"]["external_reference"]!.GetValue<string>(), "12100.00", "refunded", "12100.00");
        await Sync(h, module);

        refund = await h.Query(db => db.Refunds.SingleAsync());
        Assert.AreEqual(RefundStatus.Completed, refund.Status);
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        var invoice = await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiceId));
        Assert.AreEqual(InvoiceStatus.Cancelled, invoice.Status);
        Assert.AreEqual(0m, invoice.Pending);
    }

    [TestMethod]
    public async Task Reembolso_DeUnaFacturaSinNadaPagado_NoSeHace_NiQuedaLaNota()
    {
        var (h, module, _, _, paymentId) = await Paid();
        await using var _h = h;
        await using var _m = module;
        var other = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));

        await Assert.ThrowsExactlyAsync<ValidationException>(() => Refund(h, paymentId, other.InvoiceId!.Value));

        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync(d => d.Kind == DocumentKind.CreditNote)));
        Assert.AreEqual(0, await h.Query(db => db.Refunds.CountAsync()));
    }
}
