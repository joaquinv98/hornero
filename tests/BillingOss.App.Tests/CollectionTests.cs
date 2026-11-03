using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.App.Notifications;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>Pasarela falsa con medios guardados y suscripciones (sección 4 del diseño de cobros), en memoria.</summary>
internal sealed class FakeCollector
{
    public ConcurrentDictionary<string, JsonObject> Payments { get; } = new();

    public ConcurrentDictionary<string, JsonObject> Methods { get; } = new();

    public ConcurrentDictionary<string, JsonObject> Subscriptions { get; } = new();

    public ConcurrentQueue<(string Operation, JsonObject Input, JsonObject Context, string? Key)> Calls { get; } = new();

    /// <summary>Lo que contesta charge_saved: por defecto aprueba. Puede lanzar ModuleException.</summary>
    public Func<JsonObject, JsonObject>? Charge { get; set; }

    /// <summary>livemode que informa instance/test.</summary>
    public bool Livemode { get; set; }

    /// <summary>Error con el que contesta create_subscription, si hay.</summary>
    public ModuleException? SubscriptionError { get; set; }

    /// <summary>Error con el que contestan get_payment y list_payments, si hay (una clave sin un permiso, por ejemplo).</summary>
    public ModuleException? PaymentError { get; set; }

    /// <summary>Cobros hechos con charge_saved: cada uno con su propio ID, así dos cobros del mismo intento se ven.</summary>
    private int charges;

    public JsonObject Manifest { get; } = new()
    {
        ["id"] = "cobros",
        ["name"] = "Cobros de prueba",
        ["kinds"] = new JsonArray("payment"),
        ["capabilities"] = new JsonArray(
            "payment.core", "payment.checkout", "payment.webhooks", "payment.reconcile", "payment.refund",
            "payment.saved_methods", "payment.subscriptions", "payment.subscription_update"),
        ["config_schema"] = JsonNode.Parse("""{"type":"object","required":["api_key"],"properties":{"api_key":{"type":"string","x-secret":true},"account_name":{"type":"string"}}}"""),
        ["data_scopes"] = new JsonArray("account.contact"),
        ["currencies"] = new JsonArray("ARS", "USD"),
        ["browser_hosts"] = new JsonObject { ["test"] = new JsonArray("pay.example.com"), ["live"] = new JsonArray("pay.example.com") },
        ["webhook_events"] = new JsonArray("payment", "method", "subscription"),
    };

    public IEnumerable<(JsonObject Input, JsonObject Context, string? Key)> Of(string operation) =>
        Calls.Where(c => c.Operation == operation).Select(c => (c.Input, c.Context, c.Key));

    public ModuleDefinition Definition()
    {
        return new ModuleDefinition(Manifest)
            .Operation("payment", "create_checkout", call =>
            {
                Log("create_checkout", call);
                return Done(new JsonObject { ["redirect_url"] = $"https://pay.example.com/checkout?ref={call.Request.Input["attempt_id"]}" });
            })
            .Operation("payment", "verify_webhook", call =>
            {
                var input = call.Request.Input;
                if (input["headers"]?["x-test-signature"]?.GetValue<string>() != "ok")
                {
                    return Done(new JsonObject { ["valid"] = false, ["events"] = new JsonArray() });
                }

                var body = JsonNode.Parse(Convert.FromBase64String(input["body_base64"]!.GetValue<string>()))!;
                var kind = body["kind"]!.GetValue<string>();
                var e = new JsonObject { ["event_id"] = body["id"]!.GetValue<string>(), ["type"] = $"{kind}.updated", [$"{kind}_external_id"] = body["ref"]!.GetValue<string>() };
                return Done(new JsonObject { ["valid"] = true, ["events"] = new JsonArray(e) });
            }, mutating: false)
            .Operation("payment", "get_payment", call =>
                PaymentError is { } error ? throw error
                : Payments.TryGetValue(call.Request.Input["payment_external_id"]!.GetValue<string>(), out var p)
                    ? Done(p.DeepClone())
                    : throw new ModuleException(ErrorCodes.NotFound, "No existe el pago"), mutating: false)
            .Operation("payment", "list_payments", _ => PaymentError is { } error ? throw error : Done(new JsonObject
            {
                ["payments"] = new JsonArray([.. Payments.Values.Select(p => (JsonNode)p.DeepClone())]),
                ["next_offset"] = null,
            }), mutating: false)
            .Operation("payment", "charge_saved", call =>
            {
                Log("charge_saved", call);
                var input = call.Request.Input.DeepClone().AsObject();

                // Como el módulo de Stripe: si ya hay un cobro de este intento, devuelve ese y no cobra otra vez.
                if (ChargeOf(input["attempt_id"]!.GetValue<string>()) is { } existing)
                {
                    return Done(existing.DeepClone());
                }

                var p = (Charge ?? Approve)(input);
                Payments[p["payment_external_id"]!.GetValue<string>()] = p;
                return Done(p.DeepClone());
            })
            .Operation("payment", "get_method", call => Done(Methods[call.Request.Input["method_external_id"]!.GetValue<string>()].DeepClone()), mutating: false)
            .Operation("payment", "delete_method", call =>
            {
                Log("delete_method", call);
                return Done(new JsonObject { ["deleted"] = true });
            })
            .Operation("payment", "create_subscription", call =>
            {
                Log("create_subscription", call);
                if (SubscriptionError is { } error)
                {
                    throw error;
                }

                var id = $"sub-{Subscriptions.Count + 1}";
                Subscriptions[id] = new JsonObject { ["subscription_external_id"] = id, ["status"] = "pending" };
                return Done(new JsonObject { ["subscription_external_id"] = id, ["status"] = "pending", ["redirect_url"] = $"https://pay.example.com/subscription?id={id}" });
            })
            .Operation("payment", "get_subscription", call => Done(Subscriptions[call.Request.Input["subscription_external_id"]!.GetValue<string>()].DeepClone()), mutating: false)
            .Operation("payment", "cancel_subscription", call =>
            {
                Log("cancel_subscription", call);
                Subscriptions[call.Request.Input["subscription_external_id"]!.GetValue<string>()]["status"] = "cancelled";
                return Done(new JsonObject { ["status"] = "cancelled" });
            })
            .Operation("payment", "update_subscription", call =>
            {
                Log("update_subscription", call);
                return Done(new JsonObject { ["status"] = "active" });
            })
            .InstanceTest(call =>
            {
                Log("test", call);
                return call.Request.Instance.Config["api_key"] is null
                    ? throw new ModuleException(ErrorCodes.InvalidConfig, "Falta la clave", details: new JsonObject { ["fields"] = new JsonArray("api_key") })
                    : Done(new JsonObject { ["account_id"] = "acc-1", ["livemode"] = Livemode, ["webhook_url"] = call.Request.Input["webhook_url"]?.DeepClone() });
            });

        static Task<ModuleResponse> Done(JsonNode? result) => Task.FromResult(ModuleResponse.Completed(result));
    }

    /// <summary>Un pago con la forma de get_payment.</summary>
    public static JsonObject Payment(string id, string reference, string amount, string status = "approved", JsonObject? saved = null, string? subscription = null) => new()
    {
        ["payment_external_id"] = id,
        ["status"] = status,
        ["amount"] = new JsonObject { ["value"] = amount, ["currency"] = "ARS" },
        ["refunded"] = new JsonObject { ["value"] = "0.00", ["currency"] = "ARS" },
        ["external_reference"] = reference,
        ["saved_method"] = saved,
        ["subscription_external_id"] = subscription,
    };

    public static JsonObject Card(string id = "pm_1", string last4 = "4242", int expYear = 2030, int expMonth = 12) => new()
    {
        ["method_external_id"] = id,
        ["customer_external_id"] = "cus_1",
        ["type"] = "card",
        ["brand"] = "visa",
        ["last4"] = last4,
        ["exp_month"] = expMonth,
        ["exp_year"] = expYear,
        ["status"] = "active",
    };

    /// <summary>Un cobro aprobado nuevo, con su propio ID.</summary>
    public JsonObject Approve(JsonObject input) => Charged(input, "approved");

    /// <summary>Un cobro que queda pendiente en la pasarela, con su propio ID.</summary>
    public JsonObject Pending(JsonObject input) => Charged(input, "pending");

    /// <summary>El cobro de charge_saved de un intento (por su referencia), o null si no cobró.</summary>
    public JsonObject? ChargeOf(string attemptReference) =>
        Payments.Values.FirstOrDefault(p => p["payment_external_id"]!.GetValue<string>().StartsWith("auto-", StringComparison.Ordinal)
            && p["external_reference"]?.GetValue<string>() == attemptReference);

    /// <summary>Cuántos cobros distintos hizo charge_saved.</summary>
    public int Charges => Payments.Keys.Count(k => k.StartsWith("auto-", StringComparison.Ordinal));

    private JsonObject Charged(JsonObject input, string status) =>
        Payment($"auto-{Interlocked.Increment(ref charges)}", input["attempt_id"]!.GetValue<string>(), input["amount"]!["value"]!.GetValue<string>(), status);

    private void Log(string operation, ModuleCall call) =>
        Calls.Enqueue((operation, call.Request.Input.DeepClone().AsObject(), call.Request.Context.DeepClone().AsObject(), call.IdempotencyKey));
}

/// <summary>Arranque común: núcleo, pasarela falsa activada en el asistente y una factura de 12100 que vence el 9/11.</summary>
internal sealed record CollectionSetup(CoreHarness H, TestModule Module, FakeCollector Gateway, long InvoiceId) : IAsyncDisposable
{
    public static readonly ConsentInput Consent = new("Autorizo a cobrar automáticamente con este medio las facturas de mis servicios.", "es-AR", "Navegador de prueba");

    public static async Task<CollectionSetup> Start(Action<PaymentsOptions>? payments = null, bool autoCharge = true)
    {
        var box = TestModule.NewBox();
        var h = await CoreHarness.Start(box, services: s => s.Configure<PaymentsOptions>(o => payments?.Invoke(o)));
        var gateway = new FakeCollector();
        var module = await TestModule.Start(gateway.Definition(), h, box, instanceSecrets: """{"api_key":"k"}""");
        await h.Db(async db =>
        {
            (await db.ModuleInstances.SingleAsync()).BrowserHosts = """["pay.example.com"]""";
            db.GatewaySetups.Add(new GatewaySetup(module.InstanceId, "Tarjeta", h.Clock.Now) { Enabled = true, AutoCharge = autoCharge, Currencies = ["ARS"], Environment = "test", LastTestOk = true });
            await db.SaveChangesAsync();
        });
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));
        return new CollectionSetup(h, module, gateway, placed.InvoiceId!.Value);
    }

    public Task<bool> Notify(string eventId, string kind, string reference) =>
        H.Use<WebhookInbox, bool>(i => i.Receive(
            Module.InstanceId,
            new JsonObject { ["x-test-signature"] = "ok" },
            new JsonObject(),
            Encoding.UTF8.GetBytes(new JsonObject { ["id"] = eventId, ["kind"] = kind, ["ref"] = reference }.ToJsonString())));

    /// <summary>Paga la factura con checkout y guarda la tarjeta: queda como medio del débito.</summary>
    public async Task<SavedPaymentMethod> SaveCard(long invoiceId, string methodId = "pm_1", JsonObject? card = null)
    {
        var started = await H.Use<GatewayPayments, CheckoutStarted>(g => g.Checkout(H.AccountId, [invoiceId], Module.InstanceId, null, Consent));
        var amount = await H.Query(db => db.PaymentAttempts.Where(a => a.Reference == started.Reference).Select(a => a.Amount).SingleAsync());
        Gateway.Payments[$"pay-{methodId}"] = FakeCollector.Payment($"pay-{methodId}", started.Reference, amount.ToString("0.00", CultureInfo.InvariantCulture), saved: card ?? FakeCollector.Card(methodId));
        await Notify($"evt-{methodId}", "payment", $"pay-{methodId}");
        await H.RunJobs();
        return await H.Query(db => db.SavedPaymentMethods.SingleAsync(m => m.MethodExternalId == methodId));
    }

    public Task<long> NewInvoice() =>
        H.Use<OrderService, long>(async s => (await s.PlaceOrder(H.AccountId, [new OrderItem(H.PlanId)])).InvoiceId!.Value);

    public async ValueTask DisposeAsync()
    {
        await Module.DisposeAsync();
        await H.DisposeAsync();
    }
}

[TestClass]
public sealed class SavedMethodFlowTests
{
    [TestMethod]
    public async Task Checkout_ConGuardarMedio_PideGuardarloYDejaLaConstancia()
    {
        await using var s = await CollectionSetup.Start();

        await s.H.Use<GatewayPayments, CheckoutStarted>(g => g.Checkout(s.H.AccountId, [s.InvoiceId], s.Module.InstanceId, null, CollectionSetup.Consent));

        var (input, context, _) = s.Gateway.Of("create_checkout").Single();
        Assert.IsTrue(input["save_method"]!.GetValue<bool>());
        Assert.AreEqual(s.H.AccountId.ToString(CultureInfo.InvariantCulture), context["account"]!["id"]!.GetValue<string>());
        Assert.IsNull(context["account"]!["contact"], "El checkout no necesita el correo: no se manda.");
        var attempt = await s.H.Query(db => db.PaymentAttempts.SingleAsync());
        Assert.IsTrue(attempt.SaveMethod);
        var consent = JsonNode.Parse(attempt.Consent!)!;
        Assert.AreEqual(64, consent["text_sha256"]!.GetValue<string>().Length);
        Assert.AreEqual("Navegador de prueba", consent["user_agent"]!.GetValue<string>());
        Assert.AreEqual(1, await s.H.Query(db => db.Set<AuditEvent>().CountAsync(e => e.Action == "payments.consent")));
    }

    [TestMethod]
    public async Task Checkout_ConGuardarMedio_SinDebitoActivadoEnElAsistente_NoSeOfrece()
    {
        await using var s = await CollectionSetup.Start(autoCharge: false);

        await Assert.ThrowsExactlyAsync<ValidationException>(() =>
            s.H.Use<GatewayPayments, CheckoutStarted>(g => g.Checkout(s.H.AccountId, [s.InvoiceId], s.Module.InstanceId, null, CollectionSetup.Consent)));
        Assert.AreEqual(0, s.Gateway.Of("create_checkout").Count());
    }

    [TestMethod]
    public async Task Pago_QueGuardoElMedio_LoDejaComoElDelDebito_YElSiguienteUsaElMismoCliente()
    {
        await using var s = await CollectionSetup.Start();

        var method = await s.SaveCard(s.InvoiceId);

        Assert.AreEqual(0m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == s.InvoiceId))).Pending);
        Assert.IsTrue(method.AutoCharge);
        Assert.AreEqual("4242", method.Last4);
        Assert.AreEqual("cus_1", method.CustomerExternalId);
        StringAssert.Contains(method.Consent, "text_sha256");

        // La segunda tarjeta reemplaza a la primera en el débito, con el cliente que ya tiene en la pasarela.
        var second = await s.SaveCard(await s.NewInvoice(), "pm_2");
        Assert.AreEqual("cus_1", s.Gateway.Of("create_checkout").Last().Input["customer_external_id"]!.GetValue<string>());
        Assert.IsTrue(second.AutoCharge);
        Assert.IsFalse((await s.H.Query(db => db.SavedPaymentMethods.SingleAsync(m => m.Id == method.Id))).AutoCharge);
    }

    [TestMethod]
    public async Task Quitar_ApagaElDebitoEnElMomento_YUnTrabajoLoBorraEnLaPasarela()
    {
        await using var s = await CollectionSetup.Start();
        var method = await s.SaveCard(s.InvoiceId);

        await s.H.Use<SavedMethodService, bool>(async m =>
        {
            await m.Remove(method.Id, [s.H.AccountId]);
            return true;
        });

        var removed = await s.H.Query(db => db.SavedPaymentMethods.SingleAsync());
        Assert.AreEqual(SavedMethodStatus.Deleted, removed.Status);
        Assert.IsFalse(removed.AutoCharge);
        await s.H.RunJobs();
        var (input, _, key) = s.Gateway.Of("delete_method").Single();
        Assert.AreEqual("pm_1", input["method_external_id"]!.GetValue<string>());
        Assert.AreEqual($"method-delete-{method.Id}", key);
    }

    [TestMethod]
    public async Task Quitar_UnMedioAjeno_DaNoEncontrado()
    {
        await using var s = await CollectionSetup.Start();
        var method = await s.SaveCard(s.InvoiceId);

        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => s.H.Use<SavedMethodService, bool>(async m =>
        {
            await m.Remove(method.Id, [s.H.AccountId + 1000]);
            return true;
        }));
    }

    [TestMethod]
    public async Task AvisoDeMedio_LaRedActualizaLaTarjeta_OLaBorraElCliente()
    {
        await using var s = await CollectionSetup.Start();
        await s.SaveCard(s.InvoiceId);

        s.Gateway.Methods["pm_1"] = FakeCollector.Card(last4: "1111", expYear: 2031);
        await s.Notify("evt-m1", "method", "pm_1");
        await s.H.RunJobs();
        var updated = await s.H.Query(db => db.SavedPaymentMethods.SingleAsync());
        Assert.AreEqual("1111", updated.Last4);
        Assert.AreEqual(2031, updated.ExpYear);
        Assert.IsTrue(updated.AutoCharge);

        s.Gateway.Methods["pm_1"] = new JsonObject { ["method_external_id"] = "pm_1", ["status"] = "deleted" };
        await s.Notify("evt-m2", "method", "pm_1");
        await s.H.RunJobs();
        var deleted = await s.H.Query(db => db.SavedPaymentMethods.SingleAsync());
        Assert.AreEqual(SavedMethodStatus.Deleted, deleted.Status);
        Assert.IsFalse(deleted.AutoCharge);
    }

    [TestMethod]
    public async Task ApagarYPrenderElDebito_PrenderPideConsentimientoNuevo()
    {
        await using var s = await CollectionSetup.Start();
        var method = await s.SaveCard(s.InvoiceId);

        await s.H.Use<SavedMethodService, bool>(async m =>
        {
            await m.SetAutoCharge(method.Id, [s.H.AccountId], null);
            return true;
        });
        Assert.IsFalse((await s.H.Query(db => db.SavedPaymentMethods.SingleAsync())).AutoCharge);

        await s.H.Use<SavedMethodService, bool>(async m =>
        {
            await m.SetAutoCharge(method.Id, [s.H.AccountId], CollectionSetup.Consent);
            return true;
        });
        Assert.IsTrue((await s.H.Query(db => db.SavedPaymentMethods.SingleAsync())).AutoCharge);
        var on = await s.H.Query(db => db.Set<AuditEvent>().Where(e => e.Action == "payments.autocharge_on").Select(e => e.Details).SingleAsync());
        StringAssert.Contains(on, "text_sha256");
    }
}

[TestClass]
public sealed class AutoChargeFlowTests
{
    /// <summary>Débito siete días antes del vencimiento: cae hoy para una factura nueva, que vence el 9/11.</summary>
    private static Task<CollectionSetup> Start(params int[] days) =>
        CollectionSetup.Start(o => o.AutoCharge.Days = days.Length > 0 ? days : [-7]);

    private static Task<int> Scan(CollectionSetup s) => s.H.Use<AutoChargeService, int>(a => a.Scan());

    [TestMethod]
    public async Task Debito_ElDiaDeLaPolitica_CobraConLaClaveDelIntento_YPagaLaFactura()
    {
        await using var s = await Start();
        await s.SaveCard(s.InvoiceId);
        var invoice = await s.NewInvoice();
        await s.H.RunJobs();

        Assert.AreEqual(1, await Scan(s));
        await s.H.RunJobs();

        var (input, _, key) = s.Gateway.Of("charge_saved").Single();
        var attempt = await s.H.Query(db => db.PaymentAttempts.SingleAsync(a => a.Initiator == AttemptInitiator.Merchant));
        Assert.AreEqual($"autocharge-{attempt.Id}", key);
        Assert.AreEqual("12100.00", input["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("pm_1", input["method_external_id"]!.GetValue<string>());
        Assert.AreEqual(AttemptStatus.Paid, attempt.Status);
        Assert.AreEqual(0m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == invoice))).Pending);

        // El barrido de nuevo el mismo día no vuelve a cobrar.
        await Scan(s);
        await s.H.RunJobs();
        Assert.AreEqual(1, s.Gateway.Of("charge_saved").Count());
    }

    [TestMethod]
    public async Task Debito_ConUnaSuscripcionVigenteEnLaCuenta_NoCobraNiSeAnuncia()
    {
        await using var s = await Start();
        await s.SaveCard(s.InvoiceId);
        var invoice = await s.NewInvoice();
        await s.H.RunJobs();

        // Datos que vienen así de otro lado, por ejemplo importados: la suscripción ya cobra las facturas de la cuenta.
        await s.H.Db(async db =>
        {
            var subscription = new GatewaySubscription(s.H.AccountId, s.Module.InstanceId, Currency.Ars, 12100m, GatewayPayments.NewReference(), "{}", s.H.Clock.Now);
            subscription.Created("sub-importada", "https://pay.example.com/sub", s.H.Clock.Now);
            db.GatewaySubscriptions.Add(subscription);
            await db.SaveChangesAsync();
        });

        Assert.AreEqual(0, await Scan(s));
        Assert.IsNull(await s.H.Use<AutoChargeService, long?>(a => a.Prepare(invoice)));
        Assert.IsNull(await s.H.Query(async db => await AutoChargeService.Preview(db, await db.Documents.SingleAsync(d => d.Id == invoice), new AutoChargeOptions { Days = [-7] }, new DateOnly(2026, 11, 2), default)));
        Assert.AreEqual(0, s.Gateway.Of("charge_saved").Count());
    }

    [TestMethod]
    public async Task Debito_FueraDeLosDiasDeLaPolitica_NoCobra()
    {
        await using var s = await Start(0, 1, 3, 5);
        await s.SaveCard(s.InvoiceId);
        await s.NewInvoice();

        Assert.AreEqual(0, await Scan(s));
    }

    [TestMethod]
    public async Task Debito_ConTope_UnaFacturaMasGrandeNoSeDebita()
    {
        await using var s = await CollectionSetup.Start(o =>
        {
            o.AutoCharge.Days = [-7];
            o.AutoCharge.MaxAmount["ARS"] = 10000m;
        });
        await s.SaveCard(s.InvoiceId);
        await s.NewInvoice();

        Assert.AreEqual(0, await Scan(s));
    }

    [TestMethod]
    public async Task Debito_RechazoQueAdmiteReintento_VuelveElProximoDiaDeLaPolitica()
    {
        await using var s = await Start(-7, -6);
        await s.SaveCard(s.InvoiceId);
        var invoice = await s.NewInvoice();
        s.Gateway.Charge = _ => throw new ModuleException(ErrorCodes.Rejected, "Fondos insuficientes", details: new JsonObject { ["retry"] = "later", ["decline_code"] = "insufficient_funds" });

        await Scan(s);
        await s.H.RunJobs();
        var failed = await s.H.Query(db => db.PaymentAttempts.SingleAsync(a => a.Initiator == AttemptInitiator.Merchant));
        Assert.AreEqual(AttemptStatus.Failed, failed.Status);
        Assert.AreEqual("later", failed.Retry);
        Assert.AreEqual("rejected:insufficient_funds", failed.Outcome);

        // Al día siguiente, el segundo intento de la política cobra con otra clave: es otro intento.
        s.Gateway.Charge = null;
        s.H.Clock.Now = s.H.Clock.Now.AddDays(1);
        Assert.AreEqual(1, await Scan(s));
        await s.H.RunJobs();
        Assert.AreEqual(2, s.Gateway.Of("charge_saved").Select(c => c.Key).Distinct().Count());
        Assert.AreEqual(0m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == invoice))).Pending);
    }

    [TestMethod]
    public async Task Debito_RechazoSinReintento_VenceElMedio_YAvisaAlClienteSinElMotivo()
    {
        await using var s = await Start(-7, -6);
        await s.SaveCard(s.InvoiceId);
        await s.NewInvoice();
        s.Gateway.Charge = _ => throw new ModuleException(ErrorCodes.Rejected, "Tarjeta robada", details: new JsonObject { ["retry"] = "never", ["decline_code"] = "stolen_card" });

        await Scan(s);
        await s.H.RunJobs();

        var method = await s.H.Query(db => db.SavedPaymentMethods.SingleAsync());
        Assert.AreEqual(SavedMethodStatus.Expired, method.Status);
        Assert.IsFalse(method.AutoCharge);
        var mail = await s.H.Query(db => db.Set<OutboundEmail>().SingleAsync(e => e.Template == TemplateKeys.AutoChargeNotice));
        var html = System.Net.WebUtility.HtmlDecode(mail.Html);
        StringAssert.Contains(html, "Visa •••• 4242");
        Assert.IsFalse(html.Contains("stolen", StringComparison.OrdinalIgnoreCase) || html.Contains("robada", StringComparison.OrdinalIgnoreCase), "El motivo de un rechazo por fraude no se muestra.");

        s.H.Clock.Now = s.H.Clock.Now.AddDays(1);
        Assert.AreEqual(0, await Scan(s));
    }

    [TestMethod]
    public async Task Debito_RechazoQuePideAlCliente_ElAvisoDelMismoRechazoNoLoVuelveAReintentar()
    {
        await using var s = await Start(-7, -6);
        await s.SaveCard(s.InvoiceId);
        await s.NewInvoice();
        s.Gateway.Charge = _ => throw new ModuleException(ErrorCodes.Rejected, "Hace falta autenticar", details: new JsonObject { ["retry"] = "customer_action" });
        await Scan(s);
        await s.H.RunJobs();
        var attempt = await s.H.Query(db => db.PaymentAttempts.SingleAsync(a => a.Initiator == AttemptInitiator.Merchant));
        Assert.AreEqual("customer_action", attempt.Retry);

        // Después llega el aviso del mismo rechazo, que no dice si se reintenta: no pisa lo que ya se decidió.
        s.Gateway.Payments["pi-rechazado"] = FakeCollector.Payment("pi-rechazado", attempt.Reference, "12100.00", "rejected");
        await s.Notify("evt-rechazo", "payment", "pi-rechazado");
        await s.H.RunJobs();
        Assert.AreEqual("customer_action", (await s.H.Query(db => db.PaymentAttempts.SingleAsync(a => a.Id == attempt.Id))).Retry);

        // Y al día siguiente el débito sigue cortado para esa factura: el cliente tiene que confirmar.
        s.Gateway.Charge = null;
        s.H.Clock.Now = s.H.Clock.Now.AddDays(1);
        await Scan(s);
        await s.H.RunJobs();
        Assert.AreEqual(1, s.Gateway.Of("charge_saved").Count());
    }

    [TestMethod]
    public async Task Debito_QuePideAlCliente_SoloMandaElEnlaceDeLaPasarelaSiEsDeUnHostDeLaInstancia()
    {
        await using var s = await Start();
        await s.SaveCard(s.InvoiceId);
        async Task<string> Rejected(string url)
        {
            var invoice = await s.NewInvoice();
            s.Gateway.Charge = _ => throw new ModuleException(ErrorCodes.Rejected, "Hace falta autenticar", details: new JsonObject { ["retry"] = "customer_action", ["customer_action_url"] = url });
            await Scan(s);
            await s.H.RunJobs();
            var attempt = await s.H.Query(db => db.PaymentAttempts.Where(a => a.Initiator == AttemptInitiator.Merchant).OrderByDescending(a => a.Id).Select(a => a.Id).FirstAsync());
            var mail = await s.H.Query(db => db.Set<OutboundEmail>().SingleAsync(e => e.DedupKey == $"{TemplateKeys.AutoChargeNotice}:attempt:{attempt}"));
            return System.Net.WebUtility.HtmlDecode(mail.Html);
        }

        var foreign = await Rejected("https://otro-sitio.example.net/pagar");
        Assert.IsFalse(foreign.Contains("otro-sitio.example.net", StringComparison.Ordinal), "Un enlace a otro host no sale en el correo.");
        StringAssert.Contains(foreign, "https://billing.example.com/facturas/");

        StringAssert.Contains(await Rejected("https://pay.example.com/confirmar?id=1"), "https://pay.example.com/confirmar?id=1");
    }

    [TestMethod]
    public async Task Debito_SinRespuestaCierta_QuedaIncierto_NoSeRepiteConOtraClave_YLoResuelveLaConciliacion()
    {
        await using var s = await Start(-7, -6);
        await s.SaveCard(s.InvoiceId);
        var invoice = await s.NewInvoice();
        await s.H.RunJobs();
        var attemptId = await s.H.Use<AutoChargeService, long?>(a => a.Prepare(invoice));
        s.Gateway.Charge = _ => throw new ModuleException(ErrorCodes.OutcomeUnknown, "Se cortó después de enviar");

        // El último intento del trabajo sigue sin respuesta: el intento queda abierto e incierto.
        await s.H.Use<AutoChargeService, bool>(async a =>
        {
            await a.Charge(attemptId!.Value, lastTry: true);
            return true;
        });
        var attempt = await s.H.Query(db => db.PaymentAttempts.SingleAsync(a => a.Id == attemptId));
        Assert.AreEqual(AttemptStatus.Open, attempt.Status);
        StringAssert.StartsWith(attempt.Outcome, "uncertain:");

        // Mientras siga incierto, ni el débito del día siguiente ni el portal abren otro cobro de esa factura.
        s.H.Clock.Now = s.H.Clock.Now.AddDays(1);
        Assert.IsNull(await s.H.Use<AutoChargeService, long?>(a => a.Prepare(invoice)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => s.H.Use<GatewayPayments, CheckoutStarted>(g => g.Checkout(s.H.AccountId, [invoice], s.Module.InstanceId)));

        // En realidad la pasarela cobró: la conciliación lo encuentra por la referencia del intento.
        s.Gateway.Payments["late"] = FakeCollector.Payment("late", attempt.Reference, "12100.00");
        await s.H.Use<GatewayPayments, bool>(async g =>
        {
            await g.Reconcile(s.Module.InstanceId, s.H.Clock.Now.AddDays(-2), s.H.Clock.Now);
            return true;
        });
        Assert.AreEqual(AttemptStatus.Paid, (await s.H.Query(db => db.PaymentAttempts.SingleAsync(a => a.Id == attemptId))).Status);
        Assert.AreEqual(0m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == invoice))).Pending);
    }

    [TestMethod]
    public async Task Debito_Incierto_ElBarridoLoRepiteConLaMismaClave_YCobraUnaSolaVez()
    {
        await using var s = await Start(-7, -6);
        await s.SaveCard(s.InvoiceId);
        var invoice = await s.NewInvoice();
        await s.H.RunJobs();
        var attemptId = await s.H.Use<AutoChargeService, long?>(a => a.Prepare(invoice));

        // El pedido cobra en la pasarela, pero la respuesta se pierde.
        s.Gateway.Charge = input =>
        {
            var p = s.Gateway.Approve(input);
            s.Gateway.Payments[p["payment_external_id"]!.GetValue<string>()] = p;
            throw new ModuleException(ErrorCodes.OutcomeUnknown, "Se cortó después de enviar");
        };
        await s.H.Use<AutoChargeService, bool>(async a =>
        {
            await a.Charge(attemptId!.Value, lastTry: true);
            return true;
        });

        // Vuelve la pasarela. Un barrido de una hora después repite el cobro incierto con su misma clave: el módulo
        // vuelve a correr (la clave quedó libre porque el error era reintentable), encuentra el cobro del intento y lo
        // devuelve sin cobrar otra vez.
        s.Gateway.Charge = null;
        s.H.Clock.Now = s.H.Clock.Now.AddHours(2);
        await Scan(s);
        await s.H.RunJobs();

        Assert.AreEqual($"autocharge-{attemptId}", s.Gateway.Of("charge_saved").Select(c => c.Key).Distinct().Single());
        Assert.AreEqual(2, s.Gateway.Of("charge_saved").Count(), "El módulo corrió dos veces con la misma clave.");
        Assert.AreEqual(1, s.Gateway.Charges, "Y cobró una sola vez.");
        Assert.AreEqual(AttemptStatus.Paid, (await s.H.Query(db => db.PaymentAttempts.SingleAsync(a => a.Id == attemptId))).Status);
        Assert.AreEqual(0m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == invoice))).Pending);
    }

    [TestMethod]
    public async Task Debito_QueNoLlegoALaPasarela_NoQuedaIncierto_YVuelveOtroDiaDeLaPolitica()
    {
        await using var s = await Start(-7, -6);
        await s.SaveCard(s.InvoiceId);
        var invoice = await s.NewInvoice();
        await s.H.RunJobs();
        var attemptId = await s.H.Use<AutoChargeService, long?>(a => a.Prepare(invoice));

        // El módulo está caído durante todo el trabajo: ningún pedido salió. El intento falla con later, no traba la
        // factura (el portal la ofrece) y no la saca del débito.
        s.Gateway.Charge = _ => throw new ModuleException(ErrorCodes.UpstreamUnavailable, "La pasarela no responde");
        await Assert.ThrowsExactlyAsync<RetryLaterException>(() => s.H.Use<AutoChargeService, bool>(async a =>
        {
            await a.Charge(attemptId!.Value, lastTry: false);
            return true;
        }));
        await s.H.Use<AutoChargeService, bool>(async a =>
        {
            await a.Charge(attemptId!.Value, lastTry: true);
            return true;
        });
        var attempt = await s.H.Query(db => db.PaymentAttempts.SingleAsync(a => a.Id == attemptId));
        Assert.AreEqual((AttemptStatus.Failed, ErrorCodes.UpstreamUnavailable, "later"), (attempt.Status, attempt.Outcome, attempt.Retry));
        await s.H.Use<GatewayPayments, CheckoutStarted>(g => g.Checkout(s.H.AccountId, [invoice], s.Module.InstanceId));

        // Al otro día de la política, vuelve con otro intento.
        s.Gateway.Charge = null;
        s.H.Clock.Now = s.H.Clock.Now.AddDays(1);
        await Scan(s);
        await s.H.RunJobs();
        Assert.AreEqual(2, await s.H.Query(db => db.PaymentAttempts.CountAsync(a => a.Initiator == AttemptInitiator.Merchant)));
        Assert.AreEqual(1, s.Gateway.Charges);
        Assert.AreEqual(0m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == invoice))).Pending);
    }

    [TestMethod]
    public async Task Debito_InciertoYDespuesSinConexion_SigueIncierto_YNoAbreOtroIntento()
    {
        await using var s = await Start(-7, -6);
        await s.SaveCard(s.InvoiceId);
        var invoice = await s.NewInvoice();
        await s.H.RunJobs();
        var attemptId = await s.H.Use<AutoChargeService, long?>(a => a.Prepare(invoice));

        // El primer pedido pudo llegar y cobrar, pero la respuesta se pierde. En el último intento del trabajo el módulo ya
        // no contesta: ese pedido no salió, pero el primero igual pudo haber cobrado.
        s.Gateway.Charge = _ => throw new ModuleException(ErrorCodes.OutcomeUnknown, "Se cortó después de enviar");
        await Assert.ThrowsExactlyAsync<RetryLaterException>(() => s.H.Use<AutoChargeService, bool>(async a =>
        {
            await a.Charge(attemptId!.Value, lastTry: false);
            return true;
        }));
        s.Gateway.Charge = _ => throw new ModuleException(ErrorCodes.UpstreamUnavailable, "La pasarela no responde");
        await s.H.Use<AutoChargeService, bool>(async a =>
        {
            await a.Charge(attemptId!.Value, lastTry: true);
            return true;
        });

        var attempt = await s.H.Query(db => db.PaymentAttempts.SingleAsync(a => a.Id == attemptId));
        Assert.AreEqual(AttemptStatus.Open, attempt.Status, "No se cierra como si nada hubiera salido.");
        StringAssert.StartsWith(attempt.Outcome, "uncertain:");
        s.H.Clock.Now = s.H.Clock.Now.AddDays(1);
        Assert.IsNull(await s.H.Use<AutoChargeService, long?>(a => a.Prepare(invoice)), "Ningún intento nuevo sobre la misma factura.");
    }

    [TestMethod]
    public async Task Debito_Incierto_ConElMedioQuitadoYOtroNuevo_NoAbreOtroIntentoSobreLaMismaFactura()
    {
        await using var s = await Start(-7, -6);
        var x = await s.SaveCard(s.InvoiceId);
        var invoice = await s.NewInvoice();
        await s.H.RunJobs();
        var a = await s.H.Use<AutoChargeService, long?>(svc => svc.Prepare(invoice));

        // El primer pedido llega a la pasarela y cobra, pero la respuesta se pierde: incierto.
        s.Gateway.Charge = input =>
        {
            var p = s.Gateway.Approve(input);
            s.Gateway.Payments[p["payment_external_id"]!.GetValue<string>()] = p;
            throw new ModuleException(ErrorCodes.OutcomeUnknown, "Se cortó después de enviar");
        };
        await s.H.Use<AutoChargeService, bool>(async svc =>
        {
            await svc.Charge(a!.Value, lastTry: true);
            return true;
        });

        // El cliente quita la tarjeta X y guarda la tarjeta Y pagando otra factura: Y pasa a ser el medio del débito.
        await s.H.Use<SavedMethodService, bool>(async m =>
        {
            await m.Remove(x.Id, [s.H.AccountId]);
            return true;
        });
        var y = await s.SaveCard(await s.NewInvoice(), "pm_2");
        Assert.IsTrue(y.AutoCharge);

        // Al día siguiente el barrido repasa A y prepara la misma factura: A sigue abierto (no se cierra como cambiado
        // ni se repite con la tarjeta quitada) y no se abre otro intento con Y.
        s.Gateway.Charge = null;
        s.H.Clock.Now = s.H.Clock.Now.AddDays(1);
        await Scan(s);
        await s.H.RunJobs();
        var attempts = await s.H.Query(db => db.PaymentAttempts.Where(t => t.Initiator == AttemptInitiator.Merchant).OrderBy(t => t.Id).ToListAsync());
        Assert.AreEqual(1, attempts.Count, "Ningún intento nuevo mientras el anterior no se sepa qué cobró.");
        Assert.AreEqual((AttemptStatus.Open, x.Id), (attempts[0].Status, attempts[0].SavedMethodId));
        StringAssert.StartsWith(attempts[0].Outcome, "uncertain:");
        Assert.AreEqual(1, s.Gateway.Of("charge_saved").Count(), "No se volvió a pedir con la tarjeta quitada.");

        // La conciliación encuentra el cobro del primer pedido: lo registra contra la factura, una sola vez.
        await s.H.Use<GatewayPayments, bool>(async g =>
        {
            await g.Reconcile(s.Module.InstanceId, s.H.Clock.Now.AddDays(-2), s.H.Clock.Now);
            return true;
        });
        Assert.AreEqual(AttemptStatus.Paid, (await s.H.Query(db => db.PaymentAttempts.SingleAsync(t => t.Id == a))).Status);
        Assert.AreEqual(1, s.Gateway.Charges);
        Assert.AreEqual(0m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == invoice))).Pending);
        Assert.AreEqual(0m, await s.H.Query(db => db.CreditBalances.Where(b => b.AccountId == s.H.AccountId).Select(b => b.Balance).SingleOrDefaultAsync()), "Nada cobrado de más.");
    }

    [TestMethod]
    public async Task Debito_CortadoEnMedioDelPedido_ConElMedioCambiado_NoSeCierraNiCobraDosVeces()
    {
        await using var s = await Start(-7, -6);
        var x = await s.SaveCard(s.InvoiceId);
        var invoice = await s.NewInvoice();
        await s.H.RunJobs();
        var a = await s.H.Use<AutoChargeService, long?>(svc => svc.Prepare(invoice));

        // El pedido llega a la pasarela y cobra, pero el nodo se apaga antes de la respuesta: el trabajo no anota nada
        // después de pedir y vuelve a la cola.
        using var shutdown = new CancellationTokenSource();
        s.Gateway.Charge = input =>
        {
            var p = s.Gateway.Approve(input);
            s.Gateway.Payments[p["payment_external_id"]!.GetValue<string>()] = p;
            shutdown.Cancel();
            Thread.Sleep(200);
            return p;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => s.H.Use<AutoChargeService, bool>(async svc =>
        {
            await svc.Charge(a!.Value, lastTry: false, shutdown.Token);
            return true;
        }));
        Assert.AreEqual(PaymentAttempt.SendingOutcome, (await s.H.Query(db => db.PaymentAttempts.SingleAsync(t => t.Id == a))).Outcome, "Quedó anotado antes de pedir.");

        // Antes del reintento el cliente quita la tarjeta y guarda otra. El reintento no cierra el intento como cambiado,
        // y al día siguiente no se abre otro con la tarjeta nueva.
        await s.H.Use<SavedMethodService, bool>(async m =>
        {
            await m.Remove(x.Id, [s.H.AccountId]);
            return true;
        });
        await s.SaveCard(await s.NewInvoice(), "pm_2");
        s.Gateway.Charge = null;
        await s.H.Use<AutoChargeService, bool>(async svc =>
        {
            await svc.Charge(a!.Value, lastTry: false);
            return true;
        });
        s.H.Clock.Now = s.H.Clock.Now.AddDays(1);
        await Scan(s);
        await s.H.RunJobs();
        Assert.AreEqual(1, await s.H.Query(db => db.PaymentAttempts.CountAsync(t => t.Initiator == AttemptInitiator.Merchant)), "Ningún intento nuevo.");
        Assert.AreEqual(AttemptStatus.Open, (await s.H.Query(db => db.PaymentAttempts.SingleAsync(t => t.Id == a))).Status);
        Assert.AreEqual(1, s.Gateway.Charges, "Un solo cobro en la pasarela.");

        // La conciliación lo registra contra la factura.
        await s.H.Use<GatewayPayments, int>(g => g.Reconcile(s.Module.InstanceId, s.H.Clock.Now.AddDays(-2), s.H.Clock.Now));
        Assert.AreEqual(AttemptStatus.Paid, (await s.H.Query(db => db.PaymentAttempts.SingleAsync(t => t.Id == a))).Status);
        Assert.AreEqual(0m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == invoice))).Pending);
    }

    [TestMethod]
    public async Task TarjetaPorVencer_AvisaUnaVez_YVencidaDejaDeDebitar()
    {
        await using var s = await Start();
        await s.SaveCard(s.InvoiceId, card: FakeCollector.Card(expYear: 2026, expMonth: 11));

        await Scan(s);
        s.H.Clock.Now = s.H.Clock.Now.AddDays(1);
        await Scan(s);

        var mail = await s.H.Query(db => db.Set<OutboundEmail>().SingleAsync(e => e.Template == TemplateKeys.CardExpiring));
        var html = System.Net.WebUtility.HtmlDecode(mail.Html);
        StringAssert.Contains(html, "Visa •••• 4242");
        StringAssert.Contains(html, "30/11/2026");
        StringAssert.Contains(html, "https://billing.example.com/cuenta/medios-de-pago");

        s.H.Clock.Now = new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero);
        await Scan(s);
        var method = await s.H.Query(db => db.SavedPaymentMethods.SingleAsync());
        Assert.AreEqual(SavedMethodStatus.Expired, method.Status);
        Assert.IsFalse(method.AutoCharge);
    }

    [TestMethod]
    public async Task Debito_ConLaFacturaPagaAntesDeCobrar_NoLlamaALaPasarela()
    {
        await using var s = await Start();
        await s.SaveCard(s.InvoiceId);
        var invoice = await s.NewInvoice();
        await s.H.RunJobs();
        var attemptId = await s.H.Use<AutoChargeService, long?>(a => a.Prepare(invoice));

        // Entre el intento y el cobro, el cliente pagó por transferencia.
        await s.H.Use<PaymentService, long>(p => p.Record(new PaymentInput(s.H.AccountId, "manual:transferencia", "t-1", Currency.Ars, 12100m, PaymentStatus.Approved, [invoice])));
        await s.H.Use<AutoChargeService, bool>(async a =>
        {
            await a.Charge(attemptId!.Value, lastTry: false);
            return true;
        });

        Assert.AreEqual(0, s.Gateway.Of("charge_saved").Count());
        var attempt = await s.H.Query(db => db.PaymentAttempts.SingleAsync(a => a.Id == attemptId));
        Assert.AreEqual(AttemptStatus.Failed, attempt.Status);
        Assert.AreEqual("not_needed", attempt.Outcome);
    }

    [TestMethod]
    public async Task Checkout_EsperaAlDebitoQueEstaAbriendoSuIntento_YNoSeCruzan()
    {
        await using var s = await Start();
        var method = await s.SaveCard(s.InvoiceId);
        var invoice = await s.NewInvoice();
        await s.H.RunJobs();

        await s.H.Db(async db =>
        {
            // El débito tiene la factura trabada y está abriendo su intento.
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM billing.documents WHERE id = {invoice} FOR UPDATE");
            var checkout = s.H.Use<GatewayPayments, CheckoutStarted>(g => g.Checkout(s.H.AccountId, [invoice], s.Module.InstanceId));
            // Desde otra conexión: dentro de una transacción pg_stat_activity devuelve siempre la primera foto.
            Task<int> Waiting() => s.H.Query(q => q.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'""").SingleAsync());
            for (var i = 0; await Waiting() == 0; i++)
            {
                if (i >= 300 || checkout.IsCompleted)
                {
                    Assert.Fail($"El pago del cliente no esperó la traba de la factura: {checkout.Status} {checkout.Exception?.InnerException}");
                }

                await Task.Delay(100);
            }

            db.PaymentAttempts.Add(PaymentAttempt.Automatic(s.H.AccountId, s.Module.InstanceId, Currency.Ars, 12100m, invoice, method.Id, GatewayPayments.NewReference(), s.H.Clock.Now));
            await db.SaveChangesAsync();
            await tx.CommitAsync();

            await Assert.ThrowsExactlyAsync<ValidationException>(() => checkout);
        });

        Assert.AreEqual(0, s.Gateway.Of("create_checkout").Count(c => c.Input["items"]![0]!["id"]!.GetValue<string>() == $"doc-{invoice}"));
    }

    [TestMethod]
    public async Task Debito_ReintentoDespuesDeQuitarElMedio_NoVuelveAPedir()
    {
        await using var s = await Start();
        var method = await s.SaveCard(s.InvoiceId);
        var invoice = await s.NewInvoice();
        await s.H.RunJobs();
        var attemptId = await s.H.Use<AutoChargeService, long?>(a => a.Prepare(invoice));

        // El primer pedido no tuvo respuesta y, antes del reintento, el cliente quitó la tarjeta: la revocación vale ya.
        s.Gateway.Charge = _ => throw new ModuleException(ErrorCodes.UpstreamUnavailable, "La pasarela no responde");
        await Assert.ThrowsExactlyAsync<RetryLaterException>(() => s.H.Use<AutoChargeService, bool>(async a =>
        {
            await a.Charge(attemptId!.Value, lastTry: false);
            return true;
        }));
        await s.H.Use<SavedMethodService, bool>(async m =>
        {
            await m.Remove(method.Id, [s.H.AccountId]);
            return true;
        });
        s.Gateway.Charge = null;
        await s.H.Use<AutoChargeService, bool>(async a =>
        {
            await a.Charge(attemptId!.Value, lastTry: false);
            return true;
        });

        Assert.AreEqual(1, s.Gateway.Of("charge_saved").Count());
        var attempt = await s.H.Query(db => db.PaymentAttempts.SingleAsync(a => a.Id == attemptId));
        Assert.AreEqual((AttemptStatus.Failed, "changed"), (attempt.Status, attempt.Outcome));
    }

    [TestMethod]
    public async Task Debito_InciertoPasadoElPlazo_NoSeRepite_YLaFacturaVuelveAlPortal()
    {
        await using var s = await Start(-7, -6, -5, -4);
        await s.SaveCard(s.InvoiceId);
        var invoice = await s.NewInvoice();
        await s.H.RunJobs();
        var attemptId = await s.H.Use<AutoChargeService, long?>(a => a.Prepare(invoice));
        s.Gateway.Charge = _ => throw new ModuleException(ErrorCodes.OutcomeUnknown, "Se cortó después de enviar");
        await s.H.Use<AutoChargeService, bool>(async a =>
        {
            await a.Charge(attemptId!.Value, lastTry: true);
            return true;
        });

        // Tres días después la pasarela ya pudo olvidar la clave y el módulo ya no busca tan atrás: no se repite.
        s.Gateway.Charge = null;
        s.H.Clock.Now = s.H.Clock.Now.AddDays(3);
        await Scan(s);
        await s.H.RunJobs();

        Assert.AreEqual(1, s.Gateway.Of("charge_saved").Count());
        var attempt = await s.H.Query(db => db.PaymentAttempts.SingleAsync(a => a.Id == attemptId));
        Assert.AreEqual((AttemptStatus.Failed, "uncertain_expired", "never"), (attempt.Status, attempt.Outcome, attempt.Retry));
        Assert.AreEqual(1, await s.H.Query(db => db.Set<AuditEvent>().CountAsync(e => e.Action == "payments.autocharge_uncertain")));
        Assert.IsNull(await s.H.Use<AutoChargeService, long?>(a => a.Prepare(invoice)), "Esa factura no se vuelve a debitar sola.");
        await s.H.Use<GatewayPayments, CheckoutStarted>(g => g.Checkout(s.H.AccountId, [invoice], s.Module.InstanceId));
    }

    [TestMethod]
    public async Task Debito_PendienteSinAviso_NoCobraDosVeces_YPasadoElPlazoLaFacturaVuelveAlPortal()
    {
        await using var s = await Start(-7, -6, -5, -4);
        await s.SaveCard(s.InvoiceId);
        var invoice = await s.NewInvoice();
        await s.H.RunJobs();
        s.Gateway.Charge = s.Gateway.Pending;
        await Scan(s);
        await s.H.RunJobs();
        var attempt = await s.H.Query(db => db.PaymentAttempts.SingleAsync(a => a.Initiator == AttemptInitiator.Merchant));
        Assert.AreEqual((AttemptStatus.Open, "pending"), (attempt.Status, attempt.Outcome));

        // El aviso no llega. Los barridos siguientes lo repasan con la misma clave y nunca abren otro cobro.
        s.Gateway.Charge = null;
        for (var day = 1; day <= 3; day++)
        {
            s.H.Clock.Now = s.H.Clock.Now.AddDays(1);
            await Scan(s);
            await s.H.RunJobs();
        }

        Assert.AreEqual($"autocharge-{attempt.Id}", s.Gateway.Of("charge_saved").Select(c => c.Key).Distinct().Single());
        Assert.AreEqual(1, s.Gateway.Of("charge_saved").Count(), "Con la respuesta guardada, el módulo no vuelve a correr.");
        Assert.AreEqual(1, s.Gateway.Charges);
        Assert.AreEqual(1, await s.H.Query(db => db.PaymentAttempts.CountAsync(a => a.Initiator == AttemptInitiator.Merchant)));

        // Pasado el plazo queda a la vista y el portal vuelve a ofrecer la factura.
        Assert.AreEqual(AttemptStatus.Failed, (await s.H.Query(db => db.PaymentAttempts.SingleAsync(a => a.Id == attempt.Id))).Status);
        await s.H.Use<GatewayPayments, CheckoutStarted>(g => g.Checkout(s.H.AccountId, [invoice], s.Module.InstanceId));

        // Si al final la pasarela lo cobró, el aviso tardío igual lo registra contra su factura.
        var charged = s.Gateway.ChargeOf(attempt.Reference)!;
        charged["status"] = "approved";
        await s.Notify("evt-tarde", "payment", charged["payment_external_id"]!.GetValue<string>());
        await s.H.RunJobs();
        Assert.AreEqual(AttemptStatus.Paid, (await s.H.Query(db => db.PaymentAttempts.SingleAsync(a => a.Id == attempt.Id))).Status);
        Assert.AreEqual(0m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == invoice))).Pending);
    }

    [TestMethod]
    public async Task Debito_DeUnServicioConLaBaja_NoCobraNiSeAnuncia()
    {
        await using var s = await Start();
        await s.SaveCard(s.InvoiceId);
        var invoice = await s.NewInvoice();
        await s.H.RunJobs();
        var policy = new AutoChargeOptions { Days = [-7] };
        var today = new DateOnly(2026, 11, 2);
        Task<(DateOnly, SavedPaymentMethod)?> Preview() =>
            s.H.Query(async db => await AutoChargeService.Preview(db, await db.Documents.SingleAsync(d => d.Id == invoice), policy, today, default));
        Assert.IsNotNull(await Preview());

        // La baja pedida desde el inicio del período que factura corta el débito (Disposición 954/2025).
        var serviceId = await s.H.Query(async db =>
        {
            var period = await (from l in db.DocumentLines join p in db.BilledPeriods on l.Id equals p.DocumentLineId where l.DocumentId == invoice select p).SingleAsync();
            (await db.Services.SingleAsync(x => x.Id == period.ServiceId)).CancelAt = period.Start;
            await db.SaveChangesAsync();
            return period.ServiceId;
        });

        Assert.AreEqual(0, await Scan(s));
        Assert.IsNull(await s.H.Use<AutoChargeService, long?>(a => a.Prepare(invoice)));
        Assert.IsNull(await Preview());

        // Lo mismo con el servicio ya dado de baja.
        await s.H.Db(async db =>
        {
            var service = await db.Services.SingleAsync(x => x.Id == serviceId);
            service.CancelAt = null;
            service.MoveTo(Domain.Services.ServiceStatus.Cancelled, s.H.Clock.Now);
            await db.SaveChangesAsync();
        });
        Assert.AreEqual(0, await Scan(s));
        Assert.IsNull(await Preview());
        await s.H.RunJobs();
        Assert.AreEqual(0, s.Gateway.Of("charge_saved").Count());
    }

    [TestMethod]
    public async Task Disputa_DeUnDebitoAutomatico_ApagaElDebitoDeLaCuenta()
    {
        await using var s = await Start();
        await s.SaveCard(s.InvoiceId);
        await s.NewInvoice();
        await Scan(s);
        await s.H.RunJobs();
        var reference = await s.H.Query(db => db.PaymentAttempts.Where(a => a.Initiator == AttemptInitiator.Merchant).Select(a => a.Reference).SingleAsync());

        var charged = s.Gateway.ChargeOf(reference)!;
        charged["status"] = "disputed";
        await s.Notify("evt-d", "payment", charged["payment_external_id"]!.GetValue<string>());
        await s.H.RunJobs();

        Assert.IsFalse((await s.H.Query(db => db.SavedPaymentMethods.SingleAsync())).AutoCharge);
        Assert.AreEqual(1, await s.H.Query(db => db.Set<AuditEvent>().CountAsync(e => e.Action == "payments.autocharge_stopped")));
    }

    [TestMethod]
    public async Task CorreoDeLaFactura_DiceCuandoYConQueMedioSeVaADebitar()
    {
        await using var s = await CollectionSetup.Start();
        await s.SaveCard(s.InvoiceId);

        var invoice = await s.NewInvoice();
        await s.H.RunJobs();

        var mail = await s.H.Query(db => db.Set<OutboundEmail>().SingleAsync(e => e.DedupKey == $"{TemplateKeys.InvoiceIssued}:{invoice}"));
        StringAssert.Contains(System.Net.WebUtility.HtmlDecode(mail.Html), "Vamos a debitar el saldo el 09/11/2026 de Visa •••• 4242");
    }

    [TestMethod]
    public async Task Barrido_EsperaLaHoraDeLaPolitica()
    {
        await using var s = await CollectionSetup.Start(o => o.AutoCharge.Hour = 20);

        // 12:00 en Buenos Aires: faltan 8 horas.
        Assert.AreEqual(TimeSpan.FromHours(8), s.H.Use<AutoChargeService, TimeSpan?>(a => Task.FromResult(a.UntilHour())).Result);
    }
}

[TestClass]
public sealed class SubscriptionFlowTests
{
    private static async Task<SubscriptionStarted> Subscribe(CollectionSetup s) =>
        await s.H.Use<SubscriptionService, SubscriptionStarted>(x => x.Start(s.H.AccountId, s.Module.InstanceId, CollectionSetup.Consent));

    [TestMethod]
    public async Task Apagadas_PorDefecto_NoSeOfrecen_PeroLosCobrosDeUnaVigenteSeRegistranIgual()
    {
        await using var s = await CollectionSetup.Start();

        Assert.AreEqual(0, (await s.H.Use<SubscriptionService, IReadOnlyList<(long, string)>>(x => x.Offered(Currency.Ars))).Count);
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Subscribe(s));

        // Una suscripción que ya existía (se prendió la bandera, el cliente la aprobó y después se apagó, o una
        // importada): la pasarela sigue debitando, así que su cobro entra igual. Si no, la plata estaría y la cuenta en
        // mora. Lo que la bandera apagada no hace es tocar la pasarela: el monto no se actualiza.
        await s.H.Db(async db =>
        {
            var subscription = new GatewaySubscription(s.H.AccountId, s.Module.InstanceId, Currency.Ars, 12100m, "ref-sub", "{}", s.H.Clock.Now);
            subscription.Created("sub-9", "https://pay.example.com/sub", s.H.Clock.Now);
            subscription.Observe(SubscriptionStatus.Active, s.H.Clock.Now);
            db.GatewaySubscriptions.Add(subscription);
            await db.SaveChangesAsync();
        });
        s.Gateway.Payments["cuota-1"] = FakeCollector.Payment("cuota-1", "otra-ref", "12100.00", subscription: "sub-9");
        await s.Notify("evt-c1", "payment", "cuota-1");
        await s.H.RunJobs();
        Assert.AreEqual(1, await s.H.Query(db => db.Payments.CountAsync()));
        Assert.AreEqual(0m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == s.InvoiceId))).Pending);

        await s.NewInvoice();
        Assert.AreEqual(0, await s.H.Use<SubscriptionService, int>(x => x.ScanAmounts()));
        Assert.AreEqual(0, s.Gateway.Of("update_subscription").Count());

        // Si la pasarela la da de baja, el núcleo lo registra igual: con el estado viejo la cuenta seguiría sin poder
        // usar el débito con un medio guardado.
        s.Gateway.Subscriptions["sub-9"] = new JsonObject { ["status"] = "cancelled" };
        await s.Notify("evt-s9", "subscription", "sub-9");
        await s.H.RunJobs();
        Assert.AreEqual(SubscriptionStatus.Cancelled, (await s.H.Query(db => db.GatewaySubscriptions.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task CobroDeSuscripcion_SinElIdDeLaSuscripcion_SeRegistraPorLaReferencia()
    {
        await using var s = await CollectionSetup.Start(o => o.SubscriptionsEnabled = true);
        var started = await Subscribe(s);
        var reference = await s.H.Query(db => db.GatewaySubscriptions.Where(x => x.Id == started.Id).Select(x => x.Reference).SingleAsync());

        // Mercado Pago copia el external_reference de la suscripción en cada cobro: alcanza aunque el pago no diga de
        // qué suscripción es.
        s.Gateway.Payments["cuota-1"] = FakeCollector.Payment("cuota-1", reference, "12100.00");
        await s.Notify("evt-c1", "payment", "cuota-1");
        await s.H.RunJobs();

        Assert.AreEqual(1, await s.H.Query(db => db.Payments.CountAsync()));
        Assert.AreEqual(0m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == s.InvoiceId))).Pending);
    }

    [TestMethod]
    public async Task Prendidas_ConUnServicioAnualQueVenceAntes_ElInicioYElCobroSonDeLosMensuales()
    {
        await using var s = await CollectionSetup.Start(o => o.SubscriptionsEnabled = true);
        var today = new DateOnly(2026, 11, 2);

        // Un servicio anual contratado ayer, con su factura impaga: vence un día antes que la mensual y es la más vieja.
        var annualPlan = await s.H.Query(async db =>
        {
            var product = await db.Plans.Where(p => p.Id == s.H.PlanId).Select(p => p.ProductId).SingleAsync();
            var plan = new Domain.Catalog.Plan(product, Domain.Catalog.BillingCycle.Annually);
            db.Plans.Add(plan);
            await db.SaveChangesAsync();
            db.PlanPrices.Add(new Domain.Catalog.PlanPrice(plan.Id, Currency.Ars, 100000m, 0m, new DateOnly(2026, 1, 1)));
            await db.SaveChangesAsync();
            return plan.Id;
        });
        s.H.Clock.Now = s.H.Clock.Now.AddDays(-1);
        var annualInvoice = (await s.H.Use<OrderService, PlacedOrder>(o => o.PlaceOrder(s.H.AccountId, [new OrderItem(annualPlan)]))).InvoiceId!.Value;
        s.H.Clock.Now = s.H.Clock.Now.AddDays(1);
        var (annualDue, monthlyDue) = await s.H.Query(async db => (
            (await db.Documents.SingleAsync(d => d.Id == annualInvoice)).DueDate,
            (await db.Documents.SingleAsync(d => d.Id == s.InvoiceId)).DueDate));
        Assert.IsLessThan(monthlyDue, annualDue);

        // El servicio anual renueva antes que el mensual.
        await s.H.Db(async db =>
        {
            foreach (var service in await db.Services.ToListAsync())
            {
                db.Entry(service).Property(nameof(Domain.Services.Service.NextDueDate)).CurrentValue = service.CycleUnit == Domain.Catalog.CycleUnit.Year ? today.AddDays(14) : today.AddDays(30);
            }

            await db.SaveChangesAsync();
        });
        var annualPending = (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == annualInvoice))).Pending;

        var started = await Subscribe(s);

        // El monto y el inicio salen de los mismos servicios: el mensual.
        var (input, _, _) = s.Gateway.Of("create_subscription").Single();
        Assert.AreEqual("12100.00", input["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("2026-12-02", input["start_date"]!.GetValue<string>());

        // La cuota paga la renovación mensual, no la factura anual aunque sea más vieja.
        s.Gateway.Payments["cuota-1"] = FakeCollector.Payment("cuota-1", "de-la-pasarela", "12100.00", subscription: "sub-1");
        await s.Notify("evt-c1", "payment", "cuota-1");
        await s.H.RunJobs();
        Assert.AreEqual(0m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == s.InvoiceId))).Pending);
        Assert.AreEqual(annualPending, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == annualInvoice))).Pending);
        Assert.AreEqual(SubscriptionStatus.Pending, (await s.H.Query(db => db.GatewaySubscriptions.SingleAsync(x => x.Id == started.Id))).Status);
    }

    [TestMethod]
    public async Task Prendidas_SeCreaPorElTotalMensual_YCadaCobroVaALaFacturaMasVieja()
    {
        await using var s = await CollectionSetup.Start(o => o.SubscriptionsEnabled = true);

        var started = await Subscribe(s);

        Assert.AreEqual("https://pay.example.com/subscription?id=sub-1", started.RedirectUrl);
        var (input, context, key) = s.Gateway.Of("create_subscription").Single();
        Assert.AreEqual("12100.00", input["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("month", input["interval"]!.GetValue<string>());
        Assert.AreEqual($"subscription-{started.Id}", key);
        Assert.AreEqual("cliente@example.com", context["account"]!["contact"]!["email"]!.GetValue<string>());
        Assert.AreEqual("https://billing.example.com/cuenta/medios-de-pago", input["back_url"]!.GetValue<string>());

        s.Gateway.Payments["cuota-1"] = FakeCollector.Payment("cuota-1", "de-la-pasarela", "12100.00", subscription: "sub-1");
        await s.Notify("evt-c1", "payment", "cuota-1");
        await s.H.RunJobs();
        Assert.AreEqual(0m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == s.InvoiceId))).Pending);
    }

    [TestMethod]
    public async Task Prendidas_SiLaPasarelaNoLaCrea_SePuedeVolverAIntentar()
    {
        await using var s = await CollectionSetup.Start(o => o.SubscriptionsEnabled = true);
        s.Gateway.SubscriptionError = new ModuleException(ErrorCodes.InvalidConfig, "Falta la clave");

        await Assert.ThrowsExactlyAsync<PermanentFailureException>(() => Subscribe(s));
        Assert.AreEqual(SubscriptionStatus.Cancelled, (await s.H.Query(db => db.GatewaySubscriptions.SingleAsync())).Status);

        s.Gateway.SubscriptionError = null;
        var started = await Subscribe(s);
        Assert.AreEqual("https://pay.example.com/subscription?id=sub-1", started.RedirectUrl);
        Assert.AreEqual(SubscriptionStatus.Pending, (await s.H.Query(db => db.GatewaySubscriptions.SingleAsync(x => x.Id == started.Id))).Status);
    }

    [TestMethod]
    public async Task SuscripcionYDebitoConMedioGuardado_SonUnoDeLosDos()
    {
        await using var s = await CollectionSetup.Start(o => o.SubscriptionsEnabled = true);
        var method = await s.SaveCard(s.InvoiceId);

        // Con el débito del medio prendido, la suscripción no se crea: cada factura se cobraría dos veces.
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Subscribe(s));
        Assert.AreEqual(0, s.Gateway.Of("create_subscription").Count());

        // Apagado el débito, se crea. Con la suscripción, ni se prende el del medio ni se guarda otro para el débito.
        await s.H.Use<SavedMethodService, bool>(async m =>
        {
            await m.SetAutoCharge(method.Id, [s.H.AccountId], null);
            return true;
        });
        await Subscribe(s);
        await Assert.ThrowsExactlyAsync<ValidationException>(() => s.H.Use<SavedMethodService, bool>(async m =>
        {
            await m.SetAutoCharge(method.Id, [s.H.AccountId], CollectionSetup.Consent);
            return true;
        }));
        var invoice = await s.NewInvoice();
        await Assert.ThrowsExactlyAsync<ValidationException>(() =>
            s.H.Use<GatewayPayments, CheckoutStarted>(g => g.Checkout(s.H.AccountId, [invoice], s.Module.InstanceId, null, CollectionSetup.Consent)));
    }

    [TestMethod]
    public async Task Prendidas_OtraSuscripcionVigente_NoSeDuplica()
    {
        await using var s = await CollectionSetup.Start(o => o.SubscriptionsEnabled = true);
        await Subscribe(s);

        await Assert.ThrowsExactlyAsync<ValidationException>(() => Subscribe(s));
    }

    [TestMethod]
    public async Task Prendidas_DosPedidosALaVez_CreanUnaSola()
    {
        await using var s = await CollectionSetup.Start(o => o.SubscriptionsEnabled = true);

        var created = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                await Subscribe(s);
                return true;
            }
            catch (ValidationException)
            {
                return false;
            }
        }));

        Assert.AreEqual(1, created.Count(c => c));
        Assert.AreEqual(1, await s.H.Query(db => db.GatewaySubscriptions.CountAsync()));
        Assert.AreEqual(1, s.Gateway.Of("create_subscription").Count());
    }

    [TestMethod]
    public async Task CambiaElTotal_ActualizaElMontoAntesDelProximoCobro()
    {
        await using var s = await CollectionSetup.Start(o => o.SubscriptionsEnabled = true);
        var started = await Subscribe(s);
        var placed = await s.NewInvoice();
        await s.H.Use<PaymentService, long>(p => p.Record(new PaymentInput(s.H.AccountId, "manual:x", "pago-2", Currency.Ars, 12100m, PaymentStatus.Approved, [placed])));
        await s.H.Use<PaymentService, long>(p => p.Record(new PaymentInput(s.H.AccountId, "manual:x", "pago-1", Currency.Ars, 12100m, PaymentStatus.Approved, [s.InvoiceId])));
        await s.H.RunJobs();

        Assert.AreEqual(1, await s.H.Use<SubscriptionService, int>(x => x.ScanAmounts()));
        await s.H.RunJobs();

        var (input, _, key) = s.Gateway.Of("update_subscription").Single();
        Assert.AreEqual("24200.00", input["amount"]!["value"]!.GetValue<string>());
        StringAssert.StartsWith(key, $"subscription-{started.Id}-amount-");
        StringAssert.EndsWith(key, "-24200.00");
        Assert.AreEqual(24200m, (await s.H.Query(db => db.GatewaySubscriptions.SingleAsync(x => x.Id == started.Id))).Amount);
        Assert.AreEqual(0, await s.H.Use<SubscriptionService, int>(x => x.ScanAmounts()), "Con el monto al día no se encola nada.");
    }

    [TestMethod]
    public async Task ElTotalVaYVuelve_CadaCambioLlegaALaPasarela()
    {
        await using var s = await CollectionSetup.Start(o => o.SubscriptionsEnabled = true);
        await Subscribe(s);
        await s.NewInvoice();

        async Task CancelSecond(DateOnly? at)
        {
            await s.H.Db(async db =>
            {
                (await db.Services.OrderByDescending(x => x.Id).FirstAsync()).CancelAt = at;
                await db.SaveChangesAsync();
            });
        }

        async Task NextDay()
        {
            await s.H.Use<SubscriptionService, int>(x => x.ScanAmounts());
            await s.H.RunJobs();
            s.H.Clock.Now = s.H.Clock.Now.AddDays(1);
        }

        // 12100, 24200 con el segundo servicio, 12100 con su baja pedida y 24200 cuando la retira.
        await NextDay();
        await CancelSecond(new DateOnly(2026, 12, 9));
        await NextDay();
        await CancelSecond(null);
        await NextDay();

        var updates = s.Gateway.Of("update_subscription").ToList();
        CollectionAssert.AreEqual(new[] { "24200.00", "12100.00", "24200.00" }, updates.Select(u => u.Input["amount"]!["value"]!.GetValue<string>()).ToArray());
        Assert.AreEqual(3, updates.Select(u => u.Key).Distinct().Count());
        Assert.AreEqual(24200m, (await s.H.Query(db => db.GatewaySubscriptions.SingleAsync())).Amount);
    }

    [TestMethod]
    public async Task SinServiciosQueRenueven_LaSuscripcionSeDaDeBaja()
    {
        await using var s = await CollectionSetup.Start(o => o.SubscriptionsEnabled = true);
        var started = await Subscribe(s);
        await s.H.Db(async db =>
        {
            (await db.Services.SingleAsync()).CancelAt = new DateOnly(2026, 12, 2);
            await db.SaveChangesAsync();
        });

        await s.H.Use<SubscriptionService, int>(x => x.ScanAmounts());
        await s.H.RunJobs();

        Assert.AreEqual(SubscriptionStatus.Cancelled, (await s.H.Query(db => db.GatewaySubscriptions.SingleAsync())).Status);
        Assert.AreEqual($"subscription-cancel-{started.Id}", s.Gateway.Of("cancel_subscription").Single().Key);
        Assert.AreEqual(0, s.Gateway.Of("update_subscription").Count());
    }

    [TestMethod]
    public async Task LaPasarelaLaDaDeBajaSola_LeAvisaAlCliente()
    {
        await using var s = await CollectionSetup.Start(o => o.SubscriptionsEnabled = true);
        await Subscribe(s);

        s.Gateway.Subscriptions["sub-1"]["status"] = "cancelled";
        await s.Notify("evt-s9", "subscription", "sub-1");
        await s.H.RunJobs();

        Assert.AreEqual(SubscriptionStatus.Cancelled, (await s.H.Query(db => db.GatewaySubscriptions.SingleAsync())).Status);
        var mail = await s.H.Query(db => db.Set<OutboundEmail>().SingleAsync(e => e.Template == TemplateKeys.SubscriptionCancelled));
        StringAssert.Contains(System.Net.WebUtility.HtmlDecode(mail.Html), "Tarjeta dio de baja el débito automático");
    }

    [TestMethod]
    public async Task AvisoDeUnaSuscripcionAjena_NoEncolaNada()
    {
        await using var s = await CollectionSetup.Start(o => o.SubscriptionsEnabled = true);
        s.Gateway.Subscriptions["sub-de-otro-sistema"] = new JsonObject { ["status"] = "active" };

        await s.Notify("evt-x", "subscription", "sub-de-otro-sistema");
        await s.H.RunJobs();

        Assert.AreEqual(0, await s.H.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.SubscriptionSync)));
    }

    [TestMethod]
    public async Task LaPasarelaLaDaDeBaja_QuedaCancelada_YDesactivarLaCancelaEnLaPasarela()
    {
        await using var s = await CollectionSetup.Start(o => o.SubscriptionsEnabled = true);
        var started = await Subscribe(s);

        s.Gateway.Subscriptions["sub-1"]["status"] = "active";
        await s.Notify("evt-s1", "subscription", "sub-1");
        await s.H.RunJobs();
        Assert.AreEqual(SubscriptionStatus.Active, (await s.H.Query(db => db.GatewaySubscriptions.SingleAsync())).Status);

        await s.H.Use<SubscriptionService, bool>(async x =>
        {
            await x.Cancel(started.Id, [s.H.AccountId]);
            return true;
        });
        Assert.AreEqual(SubscriptionStatus.Cancelled, (await s.H.Query(db => db.GatewaySubscriptions.SingleAsync())).Status);
        await s.H.RunJobs();
        Assert.AreEqual($"subscription-cancel-{started.Id}", s.Gateway.Of("cancel_subscription").Single().Key);
    }
}

[TestClass]
public sealed class TransferFlowTests
{
    private const string Cuit = "20301112223";

    private static async Task<(CoreHarness H, long Account, long Invoice, long Bank)> Start(bool autoApply)
    {
        var h = await CoreHarness.Start(services: s => s.Configure<PaymentsOptions>(o => o.TransfersAutoApply = autoApply));
        await h.Db(async db =>
        {
            var current = await db.TaxIdentities.SingleAsync(t => t.AccountId == h.AccountId);
            current.Close(h.Clock.Now);
            await db.SaveChangesAsync();
            db.TaxIdentities.Add(new TaxIdentity(h.AccountId, TaxDocumentType.Cuit, "20-30111222-3", "Cliente de prueba", 1, "AR", h.Clock.Now));
            await db.SaveChangesAsync();
        });
        var invoice = (await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]))).InvoiceId!.Value;
        var bank = await h.Use<TransferService, long>(t => t.SaveAccount(null, new TransferAccountInput("Banco de prueba", "Ejemplo SRL", "30-71234567-8", "0170099220000012345678", "ejemplo.cobros", "ARS")));
        return (h, h.AccountId, invoice, bank);
    }

    private static string Statement(params string[] lines) =>
        "fecha,importe,referencia,cuit,ordenante,concepto\n" + string.Join("\n", lines);

    [TestMethod]
    public async Task Extracto_ImportadoDosVeces_NoDuplica_YLaCoincidenciaExactaSeImputaSola()
    {
        var (h, account, invoice, bank) = await Start(autoApply: true);
        await using var _ = h;
        var csv = Statement(
            $"02/11/2026,\"12.100,00\",TRF-1,{Cuit},Cliente de prueba,FAC",
            "02/11/2026,\"-500,00\",DEB-1,,,Comisión",
            "02/11/2026,\"777,00\",TRF-2,27999888777,Otra persona,VAR");

        var first = await h.Use<TransferService, StatementImport>(t => t.Import(bank, csv));
        var second = await h.Use<TransferService, StatementImport>(t => t.Import(bank, csv));

        Assert.AreEqual((2, 1, 1, 0), (first.Created, first.Applied, first.Unmatched, first.Existing));
        Assert.AreEqual((0, 2, 0), (second.Created, second.Existing, second.Errors.Count), "Reimportar cuenta lo que ya estaba, sin errores.");
        Assert.AreEqual(0m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoice))).Pending);
        var payment = await h.Query(db => db.Payments.SingleAsync());
        Assert.AreEqual(TransferService.Channel, payment.Channel);
        Assert.AreEqual($"bank-{bank}-TRF-1", payment.ExternalId);
        Assert.AreEqual(account, payment.AccountId);
        var unmatched = await h.Query(db => db.BankMovements.SingleAsync(m => m.Status == MovementStatus.Unmatched));
        Assert.AreEqual("TRF-2", unmatched.Reference);
        Assert.IsNull(unmatched.AccountId);
    }

    [TestMethod]
    public async Task Extracto_SinImputarSolo_QuedaEnLaBandejaConLaSugerencia_YElStaffLaAplica()
    {
        var (h, account, invoice, bank) = await Start(autoApply: false);
        await using var _ = h;

        await h.Use<TransferService, StatementImport>(t => t.Import(bank, Statement($"02/11/2026,\"12.100,00\",TRF-1,{Cuit},Cliente,FAC")));

        var movement = await h.Query(db => db.BankMovements.SingleAsync());
        Assert.AreEqual(MovementStatus.Unmatched, movement.Status);
        Assert.AreEqual(account, movement.AccountId);
        CollectionAssert.AreEqual(new[] { invoice }, movement.SuggestedDocumentIds);
        Assert.AreEqual(0, await h.Query(db => db.Payments.CountAsync()));

        await h.Use<TransferService, long>(t => t.Apply(movement.Id, account, movement.SuggestedDocumentIds));
        await h.Use<TransferService, long>(t => t.Apply(movement.Id, account, movement.SuggestedDocumentIds));
        Assert.AreEqual(1, await h.Query(db => db.Payments.CountAsync()));
        Assert.AreEqual(0m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoice))).Pending);
    }

    [TestMethod]
    public async Task Extracto_ReferenciaRepetidaConOtroImporte_NoSePierdeEnSilencio()
    {
        var (h, _, _, bank) = await Start(autoApply: false);
        await using var __ = h;

        // La columna mapeada como referencia trae el tipo de operación: tres créditos distintos con la misma referencia. El
        // tercero es igual al primero (el mismo cliente pagó dos facturas del mismo importe): tampoco se descarta.
        var result = await h.Use<TransferService, StatementImport>(t => t.Import(bank, Statement(
            $"02/11/2026,\"1.000,00\",TRANSFERENCIA RECIBIDA,{Cuit},Uno,A",
            "02/11/2026,\"2.000,00\",TRANSFERENCIA RECIBIDA,27999888777,Dos,B",
            $"02/11/2026,\"1.000,00\",TRANSFERENCIA RECIBIDA,{Cuit},Uno,A")));

        Assert.AreEqual((3, 1, 0), (result.Rows, result.Created, result.Existing));
        Assert.AreEqual(2, result.Errors.Count);
        StringAssert.StartsWith(result.Errors[0], "Línea 3: la referencia TRANSFERENCIA RECIBIDA ya está en la línea 2 del mismo archivo");
        StringAssert.StartsWith(result.Errors[1], "Línea 4: la referencia TRANSFERENCIA RECIBIDA ya está en la línea 2 del mismo archivo");

        // Contra la base, en otra importación: con otra fecha, otro importe u otro ordenante no entra y se dice.
        foreach (var line in (string[])["03/11/2026,\"2.000,00\",TRANSFERENCIA RECIBIDA,27999888777,Dos,B", "02/11/2026,\"1.000,00\",TRANSFERENCIA RECIBIDA,27999888777,Dos,B"])
        {
            result = await h.Use<TransferService, StatementImport>(t => t.Import(bank, Statement(line)));
            Assert.AreEqual((0, 0, 1), (result.Created, result.Existing, result.Errors.Count), line);
            StringAssert.Contains(result.Errors[0], "ya está en la base con otra fecha, otro importe u otro ordenante");
        }

        // El mismo crédito reimportado sí es lo que ya estaba.
        result = await h.Use<TransferService, StatementImport>(t => t.Import(bank, Statement($"02/11/2026,\"1.000,00\",TRANSFERENCIA RECIBIDA,{Cuit},Uno,A")));
        Assert.AreEqual((0, 1, 0), (result.Created, result.Existing, result.Errors.Count));
        Assert.AreEqual(1, await h.Query(db => db.BankMovements.CountAsync()));
    }

    [TestMethod]
    public async Task Extracto_CuentaIdentificadaSinFactura_QuedaComoSaldoAFavor()
    {
        var (h, _, invoice, bank) = await Start(autoApply: true);
        await using var __ = h;

        await h.Use<TransferService, StatementImport>(t => t.Import(bank, Statement($"02/11/2026,\"500,00\",TRF-9,{Cuit},Cliente,VAR")));

        Assert.AreEqual(500m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        Assert.AreEqual(12100m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoice))).Pending);
    }

    [TestMethod]
    public async Task Extracto_ConFilasQueNoSeEntienden_LasDevuelveConSuLinea()
    {
        var (h, _, _, bank) = await Start(autoApply: false);
        await using var __ = h;

        var result = await h.Use<TransferService, StatementImport>(t => t.Import(bank, Statement(
            "2026-11-02,\"10,00\",TRF-A,,,",
            "02/11/2026,diez,TRF-B,,,",
            "02/11/2026,\"10,00\",TRF-C,,,")));

        Assert.AreEqual(1, result.Created);
        Assert.AreEqual(2, result.Errors.Count);
        StringAssert.StartsWith(result.Errors[0], "Línea 2:");
        StringAssert.StartsWith(result.Errors[1], "Línea 3:");
    }

    [TestMethod]
    public async Task Extracto_ConOtroMapaDeColumnas_PorNumeroYPuntoDecimal()
    {
        var (h, _, invoice, _) = await Start(autoApply: true);
        await using var __ = h;
        var map = """{"delimiter":";","has_header":false,"date":"0","date_format":"yyyy-MM-dd","amount":"1","decimal_separator":".","reference":"2","payer_cuit":"3","payer_name":null,"concept":null}""";
        var bank = await h.Use<TransferService, long>(t => t.SaveAccount(null, new TransferAccountInput("Otro banco", "Ejemplo SRL", "30712345678", "0170099220000012345678", null, "ARS", CsvMap: map)));

        var result = await h.Use<TransferService, StatementImport>(t => t.Import(bank, $"2026-11-02;12100.00;X-1;{Cuit}"));

        Assert.AreEqual(1, result.Applied);
        Assert.AreEqual(0m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoice))).Pending);
    }

    [TestMethod]
    public async Task CuentaParaTransferir_ValidaCbuCuitYAlias()
    {
        await using var h = await CoreHarness.Start();

        var error = await Assert.ThrowsExactlyAsync<ValidationException>(() =>
            h.Use<TransferService, long>(t => t.SaveAccount(null, new TransferAccountInput("Banco", "Titular", "30-7123", "0170099120000012345678", "no", "ARS"))));

        StringAssert.Contains(error.Message, "CUIT");
        StringAssert.Contains(error.Message, "CBU");
        StringAssert.Contains(error.Message, "alias");
    }

    [TestMethod]
    public async Task YaTransferi_DejaUnAviso_QueSeCierraCuandoEntraElMovimiento()
    {
        var (h, account, invoice, bank) = await Start(autoApply: true);
        await using var _ = h;

        var noticeId = await h.Use<TransferService, long>(t => t.Notify(account, [invoice], new DateOnly(2026, 11, 2), "op 123"));
        var notice = await h.Query(db => db.TransferNotices.SingleAsync());
        Assert.AreEqual(12100m, notice.Amount);
        Assert.AreEqual(0, await h.Query(db => db.Payments.CountAsync()), "El aviso no es un pago.");

        await h.Use<TransferService, StatementImport>(t => t.Import(bank, Statement($"02/11/2026,\"12.100,00\",TRF-1,{Cuit},Cliente,FAC")));

        notice = await h.Query(db => db.TransferNotices.SingleAsync(n => n.Id == noticeId));
        Assert.AreEqual(NoticeStatus.Resolved, notice.Status);
        Assert.IsNotNull(notice.MovementId);
    }

    [TestMethod]
    public async Task YaTransferi_ConComprobante_LoGuarda_YRechazaLoQueNoEsPdfNiImagen()
    {
        var (h, account, invoice, _) = await Start(autoApply: false);
        await using var _ = h;
        var pdf = "%PDF-1.7\n1 0 obj\n"u8.ToArray();

        var id = await h.Use<TransferService, long>(t => t.Notify(account, [invoice], new DateOnly(2026, 11, 2), null, new NoticeFile("comprobante.pdf", pdf)));

        var notice = await h.Query(db => db.TransferNotices.SingleAsync(n => n.Id == id));
        Assert.AreEqual("application/pdf", notice.ReceiptType);
        Assert.AreEqual("comprobante.pdf", notice.ReceiptName);
        CollectionAssert.AreEqual(pdf, notice.Receipt);
        await Assert.ThrowsExactlyAsync<ValidationException>(() =>
            h.Use<TransferService, long>(t => t.Notify(account, [invoice], new DateOnly(2026, 11, 2), null, new NoticeFile("comprobante.pdf", "MZ\x90\x00"u8.ToArray()))));
    }

    [TestMethod]
    public async Task YaTransferi_TieneTopeDeAvisosAbiertosPorCuenta()
    {
        var (h, account, invoice, _) = await Start(autoApply: false);
        await using var _ = h;

        for (var i = 0; i < TransferService.MaxOpenNotices; i++)
        {
            await h.Use<TransferService, long>(t => t.Notify(account, [invoice], new DateOnly(2026, 11, 2), $"op {i}"));
        }

        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TransferService, long>(t => t.Notify(account, [invoice], new DateOnly(2026, 11, 2), "una más")));
        Assert.AreEqual(TransferService.MaxOpenNotices, await h.Query(db => db.TransferNotices.CountAsync()));

        // Cuando el staff cierra uno, el cliente puede volver a avisar.
        var first = await h.Query(db => db.TransferNotices.OrderBy(n => n.Id).Select(n => n.Id).FirstAsync());
        await h.Use<TransferService, bool>(async t =>
        {
            await t.ResolveNotice(first);
            return true;
        });
        await h.Use<TransferService, long>(t => t.Notify(account, [invoice], new DateOnly(2026, 11, 2), "otra"));
    }

    [TestMethod]
    public async Task Csv_ConComillasSeparadorYSaltos() =>
        Assert.AreEqual(
            """a|b,c|d"e|""",
            string.Join('|', TransferService.Csv("a,\"b,c\",\"d\"\"e\"\r\n", ',')[0]) + "|");
}

[TestClass]
public sealed class GatewaySetupTests
{
    private static async Task<(CoreHarness H, TestModule Module, FakeCollector Gateway, long Installation)> Start()
    {
        var box = TestModule.NewBox();
        var h = await CoreHarness.Start(box);
        var gateway = new FakeCollector();
        var module = await TestModule.Start(gateway.Definition(), h, box);
        var installation = await h.Query(db => db.ModuleInstallations.Select(i => i.Id).SingleAsync());
        return (h, module, gateway, installation);
    }

    [TestMethod]
    public async Task CrearInstancia_TomaLosHostsDelManifiesto_YLasCredencialesSecretasNoSeVuelvenAMostrar()
    {
        var (h, module, gateway, installation) = await Start();
        await using var _ = h;
        await using var __ = module;

        var id = await h.Use<GatewaySetupService, long>(g => g.CreateInstance(installation, "test"));
        await h.Use<GatewaySetupService, bool>(async g =>
        {
            await g.SaveCredentials(id, new Dictionary<string, string?> { ["api_key"] = "sk_test_1", ["account_name"] = "Ejemplo", ["otro"] = "no se guarda" });
            return true;
        });

        var instance = await h.Query(db => db.ModuleInstances.SingleAsync(i => i.Id == id));
        Assert.AreEqual("""["pay.example.com"]""", instance.BrowserHosts);
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse("""{"account_name":"Ejemplo"}"""), JsonNode.Parse(instance.Config)), instance.Config);
        Assert.IsFalse(Encoding.UTF8.GetString(instance.Secrets!).Contains("sk_test_1", StringComparison.Ordinal));

        // Guardar sin el secreto conserva el que estaba.
        await h.Use<GatewaySetupService, bool>(async g =>
        {
            await g.SaveCredentials(id, new Dictionary<string, string?> { ["api_key"] = "", ["account_name"] = "Ejemplo SRL" });
            return true;
        });
        var config = await h.Use<ModuleRegistry, JsonObject>(async r => (await r.Resolve(id, CancellationToken.None)).Data.Config);
        Assert.AreEqual("sk_test_1", config["api_key"]!.GetValue<string>());
        Assert.AreEqual("Ejemplo SRL", config["account_name"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Credenciales_SinUnCampoObligatorio_SeRechazan()
    {
        var (h, module, _, installation) = await Start();
        await using var _ = h;
        await using var __ = module;
        var id = await h.Use<GatewaySetupService, long>(g => g.CreateInstance(installation, "live"));

        var error = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<GatewaySetupService, bool>(async g =>
        {
            await g.SaveCredentials(id, new Dictionary<string, string?> { ["account_name"] = "Ejemplo" });
            return true;
        }));
        StringAssert.Contains(error.Message, "api_key");
    }

    [TestMethod]
    public async Task ProbarConexion_PasaLaUrlDeAvisos_YAvisaUnaClaveDePruebaEnProduccion()
    {
        var (h, module, gateway, installation) = await Start();
        await using var _ = h;
        await using var __ = module;
        var id = await h.Use<GatewaySetupService, long>(g => g.CreateInstance(installation, "live"));

        var missing = await h.Use<GatewaySetupService, ConnectionTest>(g => g.TestConnection(id));
        Assert.IsFalse(missing.Ok);
        StringAssert.Contains(missing.Message, "api_key");

        await h.Use<GatewaySetupService, bool>(async g =>
        {
            await g.SaveCredentials(id, new Dictionary<string, string?> { ["api_key"] = "sk_test_1" });
            return true;
        });
        var test = await h.Use<GatewaySetupService, ConnectionTest>(g => g.TestConnection(id));

        // Credenciales de prueba en una instancia de producción no pasan: no se puede ofrecer así.
        Assert.IsFalse(test.Ok);
        Assert.AreEqual($"https://billing.example.com/webhooks/{id}", gateway.Of("test").Last().Input["webhook_url"]!.GetValue<string>());
        Assert.AreEqual("Las credenciales son de prueba y la instancia es de producción.", test.Warnings.Single());
        var setup = await h.Query(db => db.GatewaySetups.SingleAsync(s => s.ModuleInstanceId == id));
        Assert.IsFalse(setup.LastTestOk);
        StringAssert.Contains(setup.LastTestMessage, "Cuenta acc-1");
        StringAssert.Contains(setup.LastTestMessage, "son de prueba");
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Activate(h, id, new GatewayActivation(true, "Tarjeta", 1, ["ARS"], false, false)));

        gateway.Livemode = true;
        Assert.IsTrue((await h.Use<GatewaySetupService, ConnectionTest>(g => g.TestConnection(id))).Ok);
    }

    [TestMethod]
    public async Task RotarCredenciales_ConUnasDePruebaEnProduccion_LaApagaEnElMomento()
    {
        var (h, module, gateway, installation) = await Start();
        await using var _ = h;
        await using var __ = module;
        var id = await h.Use<GatewaySetupService, long>(g => g.CreateInstance(installation, "live"));
        gateway.Livemode = true;
        await h.Use<GatewaySetupService, bool>(async g =>
        {
            await g.SaveCredentials(id, new Dictionary<string, string?> { ["api_key"] = "sk_live_1" });
            return true;
        });
        await h.Use<GatewaySetupService, ConnectionTest>(g => g.TestConnection(id));
        await Activate(h, id, new GatewayActivation(true, "Tarjeta", 1, ["ARS"], false, false));

        // Alguien pega la clave de prueba en la instancia de producción ofrecida en el portal.
        gateway.Livemode = false;
        await h.Use<GatewaySetupService, bool>(async g =>
        {
            await g.SaveCredentials(id, new Dictionary<string, string?> { ["api_key"] = "sk_test_1" });
            return true;
        });

        var setup = await h.Query(db => db.GatewaySetups.SingleAsync(s => s.ModuleInstanceId == id));
        Assert.IsFalse(setup.Enabled);
        Assert.IsFalse(setup.LastTestOk);
    }

    [TestMethod]
    public async Task PagoDelAmbienteDePrueba_EnUnaInstanciaDeProduccion_NoSeRegistra()
    {
        await using var s = await CollectionSetup.Start();
        await s.H.Db(async db =>
        {
            (await db.GatewaySetups.SingleAsync()).Environment = GatewaySetupService.Live;
            await db.SaveChangesAsync();
        });
        var started = await s.H.Use<GatewayPayments, CheckoutStarted>(g => g.Checkout(s.H.AccountId, [s.InvoiceId], s.Module.InstanceId));

        var payment = FakeCollector.Payment("tarjeta-de-prueba", started.Reference, "12100.00");
        payment["livemode"] = false;
        s.Gateway.Payments["tarjeta-de-prueba"] = payment;
        await s.Notify("evt-t", "payment", "tarjeta-de-prueba");
        await s.H.RunJobs();

        Assert.AreEqual(0, await s.H.Query(db => db.Payments.CountAsync()));
        Assert.AreEqual(12100m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == s.InvoiceId))).Pending);

        // El mismo pago, de producción, sí es plata.
        payment["livemode"] = true;
        await s.Notify("evt-t2", "payment", "tarjeta-de-prueba");
        await s.H.RunJobs();
        Assert.AreEqual(0m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == s.InvoiceId))).Pending);
    }

    [TestMethod]
    public async Task PagoDePrueba_SoloEnPrueba_YNoRegistraPagoNiTocaFacturas()
    {
        var (h, module, gateway, installation) = await Start();
        await using var _ = h;
        await using var __ = module;
        var live = await h.Use<GatewaySetupService, long>(g => g.CreateInstance(installation, "live"));
        var test = await h.Use<GatewaySetupService, long>(g => g.CreateInstance(installation, "test"));
        await h.Use<GatewaySetupService, bool>(async g =>
        {
            await g.SaveCredentials(test, new Dictionary<string, string?> { ["api_key"] = "sk_test_1" });
            return true;
        });

        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<GatewaySetupService, CheckoutStarted>(g => g.StartTestPayment(live, Currency.Usd)));
        var started = await h.Use<GatewaySetupService, CheckoutStarted>(g => g.StartTestPayment(test, Currency.Usd));

        Assert.AreEqual("1.00", gateway.Of("create_checkout").Single().Input["amount"]!["value"]!.GetValue<string>());
        gateway.Payments["prueba-1"] = FakeCollector.Payment("prueba-1", started.Reference, "1.00");
        await h.Use<GatewayPayments, bool>(async g =>
        {
            await g.Sync(test, "prueba-1");
            return true;
        });
        var attempt = await h.Query(db => db.PaymentAttempts.SingleAsync());
        Assert.AreEqual(AttemptStatus.Paid, attempt.Status);
        Assert.IsTrue(attempt.IsTest);
        Assert.AreEqual(0, await h.Query(db => db.Payments.CountAsync()));
    }

    [TestMethod]
    public async Task Activar_ExigeUnaPruebaCorrecta_YMonedasQueLaPasarelaCobra()
    {
        var (h, module, _, installation) = await Start();
        await using var _ = h;
        await using var __ = module;
        var id = await h.Use<GatewaySetupService, long>(g => g.CreateInstance(installation, "test"));
        var activation = new GatewayActivation(true, "Tarjeta", 1, ["ARS"], false, true);

        await Assert.ThrowsExactlyAsync<ValidationException>(() => Activate(h, id, activation));
        await h.Use<GatewaySetupService, bool>(async g =>
        {
            await g.SaveCredentials(id, new Dictionary<string, string?> { ["api_key"] = "sk_test_1" });
            return true;
        });
        await h.Use<GatewaySetupService, ConnectionTest>(g => g.TestConnection(id));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Activate(h, id, activation with { Currencies = ["EUR"] }));

        await Activate(h, id, activation);

        var setup = await h.Query(db => db.GatewaySetups.SingleAsync(s => s.ModuleInstanceId == id));
        Assert.IsTrue(setup.Offers(Currency.Ars));
        Assert.IsTrue(setup.AutoCharge);
    }

    [TestMethod]
    public async Task Activar_ConUnManifiestoSinMonedas_AceptaLasQueEscribaElStaff()
    {
        var (h, module, _, installation) = await Start();
        await using var _ = h;
        await using var __ = module;
        await h.Db(async db =>
        {
            // currencies es opcional en el protocolo: un módulo puede no declararlo.
            var stored = await db.ModuleInstallations.SingleAsync();
            var manifest = JsonNode.Parse(stored.Manifest)!.AsObject();
            manifest.Remove("currencies");
            stored.Manifest = manifest.ToJsonString();
            await db.SaveChangesAsync();
        });
        var id = await h.Use<GatewaySetupService, long>(g => g.CreateInstance(installation, "test"));
        await h.Use<GatewaySetupService, bool>(async g =>
        {
            await g.SaveCredentials(id, new Dictionary<string, string?> { ["api_key"] = "sk_test_1" });
            return true;
        });
        await h.Use<GatewaySetupService, ConnectionTest>(g => g.TestConnection(id));

        await Activate(h, id, new GatewayActivation(true, "Exterior", 2, ["usd", "EUR"], false, false));

        var setup = await h.Query(db => db.GatewaySetups.SingleAsync(s => s.ModuleInstanceId == id));
        CollectionAssert.AreEqual(new[] { "USD", "EUR" }, setup.Currencies);
    }

    [TestMethod]
    public async Task ConsultaDelPago_ConLaConfiguracionRota_NoMataElAviso_YLoMuestraEnPasarelas()
    {
        await using var s = await CollectionSetup.Start();
        var started = await s.H.Use<GatewayPayments, CheckoutStarted>(g => g.Checkout(s.H.AccountId, [s.InvoiceId], s.Module.InstanceId));
        s.Gateway.Payments["pay-1"] = FakeCollector.Payment("pay-1", started.Reference, "12100.00");

        // A la clave le falta un permiso que la prueba de conexión no vio: get_payment responde invalid_config.
        s.Gateway.PaymentError = new ModuleException(ErrorCodes.InvalidConfig, "A la clave restringida le falta un permiso (HTTP 403).", details: new JsonObject { ["fields"] = new JsonArray("api_key") });
        await s.Notify("evt-1", "payment", "pay-1");
        await s.H.RunJobs();

        var job = await s.H.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.PaymentSync));
        Assert.AreEqual(JobStatus.Pending, job.Status, "El aviso válido ya está guardado: el trabajo no muere.");
        Assert.AreEqual(s.H.Clock.Now + GatewayPayments.ConfigRetry, job.RunAt);
        var setup = await s.H.Query(db => db.GatewaySetups.SingleAsync());
        Assert.IsFalse(setup.LastTestOk);
        StringAssert.Contains(setup.LastTestMessage, "falta un permiso");
        Assert.AreEqual(1, await s.H.Query(db => db.Set<AuditEvent>().CountAsync(e => e.Action == "payments.gateway_config_error")));
        Assert.AreEqual(12100m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == s.InvoiceId))).Pending);

        // La conciliación tampoco muere ni se queda reintentando: la de la hora siguiente vuelve a probar.
        await s.H.Use<GatewayPayments, bool>(async g =>
        {
            await g.Reconcile(s.Module.InstanceId, s.H.Clock.Now.AddDays(-2), s.H.Clock.Now);
            return true;
        });
        Assert.AreEqual(2, await s.H.Query(db => db.Set<AuditEvent>().CountAsync(e => e.Action == "payments.gateway_config_error")));

        // El staff arregla la clave: la próxima consulta registra el pago.
        s.Gateway.PaymentError = null;
        s.H.Clock.Now += GatewayPayments.ConfigRetry;
        await s.H.RunJobs();
        Assert.AreEqual(JobStatus.Done, (await s.H.Query(db => db.Jobs.SingleAsync(j => j.Id == job.Id))).Status);
        Assert.AreEqual(0m, (await s.H.Query(db => db.Documents.SingleAsync(d => d.Id == s.InvoiceId))).Pending);
    }

    [TestMethod]
    public async Task Checkout_EnUnaPasarelaApagadaOEnOtraMoneda_NoCobra()
    {
        await using var s = await CollectionSetup.Start();
        await s.H.Db(async db =>
        {
            (await db.GatewaySetups.SingleAsync()).Currencies = ["USD"];
            await db.SaveChangesAsync();
        });

        await Assert.ThrowsExactlyAsync<ValidationException>(() => s.H.Use<GatewayPayments, CheckoutStarted>(g => g.Checkout(s.H.AccountId, [s.InvoiceId], s.Module.InstanceId)));
        Assert.AreEqual(0, s.Gateway.Of("create_checkout").Count());
    }

    private static Task Activate(CoreHarness h, long id, GatewayActivation activation) =>
        h.Use<GatewaySetupService, bool>(async g =>
        {
            await g.Activate(id, activation);
            return true;
        });
}

/// <summary>La migración de los cobros sobre una base con datos.</summary>
[TestClass]
public sealed class PasarelasMigrationTests
{
    [TestMethod]
    public void IntentosQueYaExistian_QuedanComoDelCliente()
    {
        // EF genera "" para una columna nueva no nula: no es ningún AttemptInitiator y leer los intentos viejos fallaría.
        var add = new Persistence.Migrations.Pasarelas().UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.AddColumnOperation>()
            .Single(o => o.Table == "attempts" && o.Name == "initiator");
        Assert.AreEqual(nameof(AttemptInitiator.Customer), add.DefaultValue);
    }

    [TestMethod]
    public async Task UnaCuenta_TieneALoSumoUnaSuscripcionVigente_TambienEnLaBase()
    {
        await using var s = await CollectionSetup.Start();
        GatewaySubscription New() => new(s.H.AccountId, s.Module.InstanceId, Currency.Ars, 12100m, GatewayPayments.NewReference(), "{}", s.H.Clock.Now);
        await s.H.Db(async db =>
        {
            db.GatewaySubscriptions.Add(New());
            await db.SaveChangesAsync();
        });

        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => s.H.Db(async db =>
        {
            db.GatewaySubscriptions.Add(New());
            await db.SaveChangesAsync();
        }));

        // Dada de baja la primera, la cuenta puede tener otra.
        await s.H.Db(async db =>
        {
            (await db.GatewaySubscriptions.SingleAsync()).Observe(SubscriptionStatus.Cancelled, s.H.Clock.Now);
            await db.SaveChangesAsync();
            db.GatewaySubscriptions.Add(New());
            await db.SaveChangesAsync();
        });
        Assert.AreEqual(2, await s.H.Query(db => db.GatewaySubscriptions.CountAsync()));
    }
}

/// <summary>Alta automática de los módulos de la receta en migrate (sección 4.2 del diseño de cobros).</summary>
[TestClass]
public sealed class IncludedModulesTests
{
    private static Task Sync(CoreHarness h, Dictionary<string, IncludedModule> included) =>
        h.Use<IncludedModules, bool>(async m =>
        {
            await m.Sync(included, TimeSpan.Zero);
            return true;
        });

    [TestMethod]
    public async Task DaDeAltaElModuloDeLaReceta_YUnManifiestoQueCambiaEsquemasEsperaAprobacion()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());
        var gateway = new FakeCollector();
        await using var module = await TestModule.Serve(gateway.Definition());
        var included = new Dictionary<string, IncludedModule> { ["cobros"] = new() { Url = module.Url, SigningSecret = module.Signing } };

        await Sync(h, included);
        var installation = await h.Query(db => db.ModuleInstallations.SingleAsync(i => i.Name == "cobros"));
        Assert.AreEqual(module.Url, installation.BaseUrl);
        Assert.AreEqual("Cobros de prueba", JsonNode.Parse(installation.Manifest)!["name"]!.GetValue<string>());
        Assert.IsFalse(Encoding.UTF8.GetString(installation.SigningSecret).Contains(module.Signing, StringComparison.Ordinal), "El secreto de firma se guarda cifrado.");

        // Una versión que pide un dato más en la configuración queda pendiente, y mientras tanto vale la aceptada.
        gateway.Manifest["config_schema"]!["properties"]!.AsObject()["region"] = new JsonObject { ["type"] = "string" };
        await Sync(h, included);
        installation = await h.Query(db => db.ModuleInstallations.SingleAsync(i => i.Name == "cobros"));
        Assert.IsNotNull(installation.PendingManifest);
        Assert.IsNull(JsonNode.Parse(installation.Manifest)!["config_schema"]!["properties"]!["region"]);

        await h.Use<GatewaySetupService, bool>(async g =>
        {
            await g.ApproveManifest(installation.Id);
            return true;
        });
        installation = await h.Query(db => db.ModuleInstallations.SingleAsync(i => i.Name == "cobros"));
        Assert.IsNull(installation.PendingManifest);
        Assert.IsNotNull(JsonNode.Parse(installation.Manifest)!["config_schema"]!["properties"]!["region"]);
        Assert.AreEqual(1, await h.Query(db => db.ModuleInstallations.CountAsync()));
    }

    [TestMethod]
    public async Task UnModuloQueNoResponde_NoFrenaLasMigraciones()
    {
        await using var h = await CoreHarness.Start(TestModule.NewBox());

        await Sync(h, new Dictionary<string, IncludedModule> { ["caido"] = new() { Url = "http://127.0.0.1:9/", SigningSecret = "secreto" } });

        Assert.AreEqual(0, await h.Query(db => db.ModuleInstallations.CountAsync()));
    }

    [TestMethod]
    public void Huella_DelCertificadoPem_EsLaQueImprimeCertificadosSh()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("CN=stripe", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)), IncludedModules.Fingerprint(certificate.ExportCertificatePem()));
    }
}
