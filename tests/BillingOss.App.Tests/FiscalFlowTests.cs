using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Fiscal;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>Un fisco de mentira con los casos difíciles de ARCA: corte después de autorizar, rechazo y serie adelantada.</summary>
internal sealed class FakeFisco
{
    private readonly ConcurrentDictionary<(int Pos, int Type), List<JsonObject>> docs = new();
    private readonly TaskCompletionSource released = new();
    private int tickets;

    public bool CutOnce { get; set; }

    public bool Reject { get; set; }

    public int Offset { get; set; }

    /// <summary>Si está, cada consulta del último número devuelve un ticket nuevo como estado de la instancia.</summary>
    public bool IssueTickets { get; set; }

    public List<string?> TicketsSeen { get; } = [];

    /// <summary>Último ID de pedido de exportación que informa el fisco.</summary>
    public long LastRequestId { get; set; } = 40;

    /// <summary>Cuántos de los próximos authorize fallan como inciertos sin llegar a registrar nada.</summary>
    public int Failures { get; set; }

    /// <summary>
    /// Si está, el próximo authorize queda colgado hasta Release y el núcleo corta a los 5 s, como un módulo que se
    /// cae en medio del pedido y deja la clave en curso.
    /// </summary>
    public bool HangOnce { get; set; }

    /// <summary>Un comprobante que llega tarde al fisco: se registra justo antes del próximo authorize.</summary>
    public JsonObject? Late { get; set; }

    /// <summary>Si está, la próxima consulta del último número vuelve sin el número, como una respuesta rota.</summary>
    public bool BreakLastNumberOnce { get; set; }

    /// <summary>Si está, la próxima consulta del último número falla después de un login: el ticket nuevo viaja en el error.</summary>
    public bool FailWithTicketOnce { get; set; }

    /// <summary>Si está, la próxima cotización no existe, como un feriado largo sin cotización publicada.</summary>
    public bool NoRateOnce { get; set; }

    /// <summary>Si está, el próximo authorize de exportación vuelve con el ID de pedido ya usado por otro comprobante.</summary>
    public bool RequestIdInUseOnce { get; set; }

    /// <summary>Mientras esté, toda consulta de un comprobante falla con ese código, como un certificado vencido.</summary>
    public string? FindError { get; set; }

    /// <summary>Cambia el total de un comprobante ya registrado, como si otro sistema hubiera usado ese número.</summary>
    public void Tamper(int pointOfSale, int typeCode, long number, string total) =>
        docs[(pointOfSale, typeCode)].Single(d => d["number"]!.GetValue<long>() == number)["amounts"]!["total"] = total;

    /// <summary>
    /// Como el módulo de ARCA con Arca:AllowProduction apagado: toda llamada de una instancia de producción se rechaza
    /// con invalid_config antes de hacer nada.
    /// </summary>
    public bool AllowProduction { get; set; } = true;

    /// <summary>
    /// Registra un comprobante por fuera de authorize: un pedido que llegó tarde, un comprobante de otro sistema o uno
    /// emitido después del punto al que se restauró la base.
    /// </summary>
    public void Register(JsonObject doc)
    {
        var stored = doc.DeepClone().AsObject();
        stored["exchange_rate"] ??= "1449.0";
        docs.GetOrAdd(Key(stored), _ => []).Add(stored);
    }

    private void Gate(ModuleCall call)
    {
        if (!AllowProduction && call.Request.Instance.Config["environment"]?.ToString() == "production")
        {
            throw new ModuleException(ErrorCodes.InvalidConfig, "El ambiente de producción está apagado en este módulo", details: new JsonObject { ["fields"] = new JsonArray("environment") });
        }
    }

    public List<JsonObject> Authorizations { get; } = [];

    /// <summary>Las claves de idempotencia con las que llegó cada authorize.</summary>
    public List<string?> Keys { get; } = [];

    /// <summary>Corre una vez, con el comprobante ya registrado y antes de responder: ahí se simula una conmutación.</summary>
    public Func<Task>? AfterAuthorize { get; set; }

    public List<JsonObject> RateCalls { get; } = [];

    /// <summary>Cuántas llamadas de cualquier operación recibió el fisco.</summary>
    public int Calls { get; private set; }

    public int Authorized => docs.Values.Sum(l => l.Count);

    /// <summary>Puntos de venta que informa parameters. Por defecto el 3, habilitado para CAE.</summary>
    public JsonArray PointsOfSale { get; set; } = new(new JsonObject { ["id"] = 3, ["emission_type"] = "CAE", ["blocked"] = false });

    public JsonArray ExportPointsOfSale { get; set; } = new(new JsonObject { ["id"] = 12, ["blocked"] = false });

    public JsonArray ReceiverConditions { get; set; } = new(
        new JsonObject { ["id"] = 1, ["description"] = "IVA Responsable Inscripto", ["voucher_class"] = "A" },
        new JsonObject { ["id"] = 5, ["description"] = "Consumidor Final", ["voucher_class"] = "B" });

    /// <summary>La constancia que devuelve taxpayer, o null para not_found.</summary>
    public JsonObject? Taxpayer { get; set; }

    /// <summary>La entrada de cada instance/test, para ver si pidió un login nuevo.</summary>
    public List<JsonObject> TestCalls { get; } = [];

    /// <summary>El certificado que traía la configuración en cada instance/test y en cada authorize.</summary>
    public List<string?> CertificatesSeen { get; } = [];

    /// <summary>Lo que instance/test devuelve como authorize_enabled para una instancia de producción: la traba del módulo.</summary>
    public bool AuthorizeEnabled { get; set; } = true;

    public void Release() => released.TrySetResult();

    public ModuleDefinition Definition() => new ModuleDefinition(new JsonObject
    {
        ["id"] = "fisco",
        ["kinds"] = new JsonArray("fiscal"),
        ["timeouts"] = HangOnce ? new JsonObject { ["authorize"] = 5 } : null,
    })
        .Operation("fiscal", "last_number", call =>
        {
            Calls++;
            Gate(call);
            var key = Key(call.Request.Input);
            var list = docs.GetOrAdd(key, _ => []);
            if (BreakLastNumberOnce)
            {
                BreakLastNumberOnce = false;
                return Task.FromResult(ModuleResponse.Completed(new JsonObject()));
            }

            if (FailWithTicketOnce)
            {
                FailWithTicketOnce = false;
                throw new ModuleException(ErrorCodes.UpstreamUnavailable, "ARCA no respondió después del login", retryAfter: 1)
                {
                    InstanceState = new JsonObject { ["ticket"] = "T-error" },
                };
            }

            var result = new JsonObject { ["number"] = list.Count + Offset };
            if (list.Count > 0)
            {
                result["date"] = list[^1]["date"]!.GetValue<string>();
            }

            if (call.Request.Input["type_code"]!.GetValue<int>() is 19 or 20 or 21)
            {
                result["last_request_id"] = LastRequestId;
            }

            return Task.FromResult(ModuleResponse.Completed(result, IssueTickets ? new JsonObject { ["ticket"] = $"T{++tickets}" } : null));
        }, mutating: false)
        .Operation("fiscal", "authorize", call =>
        {
            Calls++;
            Gate(call);
            TicketsSeen.Add(call.Request.Instance.State?["ticket"]?.GetValue<string>());
            CertificatesSeen.Add(call.Request.Instance.Config["certificate_pem"]?.GetValue<string>());
            Keys.Add(call.IdempotencyKey);
            Authorizations.Add(call.Request.Input.DeepClone().AsObject());
            if (HangOnce)
            {
                HangOnce = false;
                return Hang(released.Task);
            }

            if (Failures > 0)
            {
                Failures--;
                throw new ModuleException(ErrorCodes.OutcomeUnknown, "Se cortó antes de llegar");
            }

            if (Late is { } late)
            {
                Late = null;
                docs.GetOrAdd(Key(late), _ => []).Add(late);
            }

            var doc = call.Request.Input["document"]!.AsObject();

            // Como el módulo de ARCA: el documento del receptor va solo con dígitos, y si no, ni sale hacia el fisco.
            if (!doc["receiver"]!["doc_number"]!.GetValue<string>().All(char.IsAsciiDigit))
            {
                throw new ModuleException(ErrorCodes.InvalidInput, "Datos inválidos", details: new JsonObject { ["fields"] = new JsonArray("document.receiver.doc_number") });
            }

            var key = Key(doc);
            var list = docs.GetOrAdd(key, _ => []);
            if (Reject)
            {
                throw new ModuleException(ErrorCodes.Rejected, "Receptor inválido", details: new JsonObject { ["upstream"] = new JsonArray("10013") });
            }

            if (RequestIdInUseOnce)
            {
                RequestIdInUseOnce = false;
                throw new ModuleException(ErrorCodes.Rejected, "ARCA devolvió otro comprobante para el ID de pedido.", details: new JsonObject
                {
                    ["upstream"] = new JsonArray(new JsonObject { ["code"] = 0, ["message"] = "wsfex_id_in_use" }),
                });
            }

            if (doc["number"]!.GetValue<long>() != list.Count + Offset + 1)
            {
                throw new ModuleException(ErrorCodes.OutcomeUnknown, "Número fuera de secuencia");
            }

            // Sin cotización (misma moneda) el fisco registra la oficial, como ARCA.
            var stored = doc.DeepClone().AsObject();
            stored["exchange_rate"] ??= "1449.0";
            list.Add(stored);
            if (CutOnce)
            {
                CutOnce = false;
                throw new ModuleException(ErrorCodes.OutcomeUnknown, "Se cortó la conexión");
            }

            return AfterAuthorize is { } after ? Run(after, Approval(doc)) : Task.FromResult(ModuleResponse.Completed(Approval(doc)));
        })
        .Operation("fiscal", "find", call =>
        {
            Calls++;
            Gate(call);
            if (FindError is { } code)
            {
                throw new ModuleException(code, "El certificado venció.");
            }

            var list = docs.GetOrAdd(Key(call.Request.Input), _ => []);
            var number = call.Request.Input["number"]!.GetValue<long>();
            var found = list.FirstOrDefault(d => d["number"]!.GetValue<long>() == number)
                ?? throw new ModuleException(ErrorCodes.NotFound, "No existe");
            var result = Approval(found);
            result["document"] = found.DeepClone();
            return Task.FromResult(ModuleResponse.Completed(result));
        }, mutating: false)
        .Operation("fiscal", "exchange_rate", call =>
        {
            Calls++;
            Gate(call);
            RateCalls.Add(call.Request.Input.DeepClone().AsObject());
            if (NoRateOnce)
            {
                NoRateOnce = false;
                throw new ModuleException(ErrorCodes.NotFound, "ARCA no tiene cotización para DOL.");
            }

            return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["rate"] = "1450.5" }));
        }, mutating: false)
        .Operation("fiscal", "parameters", call =>
        {
            Calls++;
            var result = new JsonObject
            {
                ["vat_rates"] = new JsonArray(new JsonObject { ["id"] = 5, ["description"] = "21%" }, new JsonObject { ["id"] = 6, ["description"] = "27%" }, new JsonObject { ["id"] = 4, ["description"] = "10.5%" }),
                ["tax_types"] = new JsonArray(new JsonObject { ["id"] = 13, ["description"] = "Percepción de IVA No Categorizado según la RG 2126/2006" }),
                ["receiver_vat_conditions"] = ReceiverConditions.DeepClone(),
                ["points_of_sale"] = PointsOfSale.DeepClone(),
            };
            if (call.Request.Input["export"]?.GetValue<bool>() == true)
            {
                result["export_points_of_sale"] = ExportPointsOfSale.DeepClone();
            }

            return Task.FromResult(ModuleResponse.Completed(result));
        }, mutating: false)
        .Operation("fiscal", "taxpayer", call =>
        {
            Calls++;
            return Task.FromResult(ModuleResponse.Completed(Taxpayer?.DeepClone() ?? throw new ModuleException(ErrorCodes.NotFound, "ARCA no tiene constancia para esa CUIT.")));
        }, mutating: false)
        .InstanceTest(call =>
        {
            Calls++;
            TestCalls.Add(call.Request.Input.DeepClone().AsObject());
            var config = call.Request.Instance.Config;
            CertificatesSeen.Add(config["certificate_pem"]?.GetValue<string>());
            var production = config["environment"]?.GetValue<string>() == "production";
            var fresh = call.Request.Input["fresh_login"]?.GetValue<bool>() == true;
            var result = new JsonObject
            {
                ["environment"] = config["environment"]?.GetValue<string>() ?? "homologation",
                ["certificate_expires_on"] = "2028-01-01",
                ["authorize_enabled"] = !production || AuthorizeEnabled,
                ["production_mode"] = production && !AuthorizeEnabled ? "read" : "on",
            };
            return Task.FromResult(ModuleResponse.Completed(result, fresh ? new JsonObject { ["ticket"] = $"T-fresh-{++tickets}" } : null));
        });

    private static async Task<ModuleResponse> Hang(Task until)
    {
        await Task.WhenAny(until, Task.Delay(TimeSpan.FromSeconds(30)));
        throw new ModuleException(ErrorCodes.OutcomeUnknown, "Se cayó en medio del pedido");
    }

    private async Task<ModuleResponse> Run(Func<Task> after, JsonObject result)
    {
        AfterAuthorize = null;
        await after();
        return ModuleResponse.Completed(result);
    }

    private static (int, int) Key(JsonNode n) => (n["point_of_sale"]!.GetValue<int>(), n["type_code"]!.GetValue<int>());

    private static JsonObject Approval(JsonObject doc) => new()
    {
        ["authorization_code"] = $"CAE{doc["number"]!.GetValue<long>()}",
        ["authorization_due"] = "2026-11-12",
    };
}

[TestClass]
public sealed class FiscalFlowTests
{
    /// <summary>El núcleo con un fisco falso. Por defecto la instancia es de homologación y la traba de producción queda apagada.</summary>
    internal static async Task<(CoreHarness H, TestModule Module, FakeFisco Fisco)> Start(Action<FakeFisco>? setup = null, string instanceConfig = "{}", bool productionEnabled = false, bool pooling = true, Action<BillingOptions>? configure = null, Action<IServiceCollection>? services = null)
    {
        var box = TestModule.NewBox();
        var h = await CoreHarness.Start(box, configure, fiscal: o => o.ProductionEnabled = productionEnabled, services: services, pooling: pooling);
        var fisco = new FakeFisco();
        setup?.Invoke(fisco);
        var module = await TestModule.Start(fisco.Definition(), h, box, instanceConfig);
        await h.Db(async db =>
        {
            db.IssuerProfiles.Add(new IssuerProfile
            {
                TaxId = "30712345678",
                LegalName = "Proveedor de prueba",
                ModuleInstanceId = module.InstanceId,
                PointOfSale = 3,
                TypeMap = """{"A":{"Invoice":1,"DebitNote":2,"CreditNote":3},"B":{"Invoice":6,"DebitNote":7,"CreditNote":8},"E":{"Invoice":19,"DebitNote":20,"CreditNote":21}}""",
            });
            await db.SaveChangesAsync();
        });
        return (h, module, fisco);
    }

    internal static Task<PlacedOrder> Order(CoreHarness h) =>
        h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));

    [TestMethod]
    public async Task FacturaB_SeEmiteAlEmitirLaFactura()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;

        var placed = await Order(h);
        await h.RunJobs();

        var fiscal = await h.Query(db => db.FiscalDocuments.SingleAsync());
        Assert.AreEqual(placed.InvoiceId, fiscal.DocumentId);
        Assert.AreEqual(6, fiscal.TypeCode);
        Assert.AreEqual(3, fiscal.PointOfSale);
        Assert.AreEqual(1L, fiscal.Number);
        Assert.AreEqual("CAE1", fiscal.AuthorizationCode);
        var payload = JsonNode.Parse(fiscal.Payload)!["document"]!;
        Assert.AreEqual(96, payload["receiver"]!["doc_type"]!.GetValue<int>());
        Assert.AreEqual(5, payload["receiver"]!["vat_condition"]!.GetValue<int>());
        Assert.AreEqual("12100.00", payload["amounts"]!["total"]!.GetValue<string>());
        Assert.AreEqual("2026-12-01", payload["service_period"]!["to"]!.GetValue<string>());
        Assert.AreEqual(1, fisco.Authorized);

        // La clave de idempotencia lleva la instancia: otra instancia del mismo proceso del módulo no comparte la respuesta.
        StringAssert.StartsWith(fisco.Keys.Single(), $"emision-{module.InstanceId}-", StringComparison.Ordinal);
    }

    /// <summary>Cuenta en dólares (del exterior salvo que se pida otro país), con el emisor configurado para exportación.</summary>
    private static async Task<long> UsdAccount(CoreHarness h, string country = "US")
    {
        long id = 0;
        await h.Db(async db =>
        {
            var issuer = await db.IssuerProfiles.SingleAsync();
            issuer.ExportPointOfSale = 12;
            issuer.Settings = new IssuerSettings(
                new Dictionary<string, ExportCountry> { ["US"] = new(212, 55000002126) },
                null,
                "Pago Online",
                "en",
                7,
                new Dictionary<string, bool> { ["paypal_ppcpv"] = true, ["mercadopago"] = false }).ToJson();
            db.PlanPrices.Add(new PlanPrice(h.PlanId, Currency.Usd, 10m, 0m, new DateOnly(2026, 1, 1)));
            var account = new Account("Foreign Co", "billing@example.com", Currency.Usd, h.Clock.Now);
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            db.TaxIdentities.Add(country == "AR"
                ? new TaxIdentity(account.Id, TaxDocumentType.Dni, "30111223", "Cliente en dólares", 5, "AR", h.Clock.Now)
                : new TaxIdentity(account.Id, TaxDocumentType.Foreign, "123456789", "Foreign Co", 9, country, h.Clock.Now) { Street = "1 Main St", City = "Austin" });
            await db.SaveChangesAsync();
            id = account.Id;
        });
        return id;
    }

    [TestMethod]
    public async Task FacturaE_PagoEnLaMismaMoneda_VaSinCotizacionYConLosDatosDelExterior()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        var account = await UsdAccount(h);

        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)], paymentMethod: "paypal_ppcpv"));
        await h.RunJobs();

        var fiscal = await h.Query(db => db.FiscalDocuments.SingleAsync());
        Assert.AreEqual(19, fiscal.TypeCode);
        Assert.AreEqual(12, fiscal.PointOfSale);
        var sent = fisco.Authorizations.Single();
        Assert.AreEqual(41L, sent["wsfex_id"]!.GetValue<long>());
        var doc = sent["document"]!;
        Assert.AreEqual("DOL", doc["currency"]!.GetValue<string>());
        Assert.IsTrue(doc["same_currency_payment"]!.GetValue<bool>());
        Assert.IsNull(doc["exchange_rate"]);
        Assert.AreEqual("Pago Online", doc["payment_terms"]!.GetValue<string>());
        Assert.AreEqual("en", doc["language"]!.GetValue<string>());
        Assert.AreEqual(7, doc["items"]![0]!["unit_code"]!.GetValue<int>());
        var receiver = doc["receiver"]!;
        Assert.AreEqual(212, receiver["country_code"]!.GetValue<int>());
        Assert.AreEqual(55000002126L, receiver["country_tax_id"]!.GetValue<long>());
        Assert.AreEqual("123456789", receiver["tax_id"]!.GetValue<string>());
        Assert.AreEqual("1 Main St, Austin, US", receiver["address"]!.GetValue<string>());
        Assert.IsEmpty(fisco.RateCalls);
        Assert.AreEqual(41L, (await h.Query(db => db.FiscalEmissions.SingleAsync())).RequestId);

        // Queda guardada la cotización que registró el fisco, para el QR y para las notas.
        Assert.AreEqual("1449.0", JsonNode.Parse(fiscal.Payload)!["document"]!["exchange_rate"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task FacturaE_OtroMedioDePago_PideLaCotizacionDeExportacion()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        var account = await UsdAccount(h);

        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)], paymentMethod: "mercadopago_1"));
        await h.RunJobs();

        Assert.AreEqual(19, fisco.RateCalls.Single()["type_code"]!.GetValue<int>());
        var doc = fisco.Authorizations.Single()["document"]!;
        Assert.IsFalse(doc["same_currency_payment"]!.GetValue<bool>());
        Assert.AreEqual("1450.5", doc["exchange_rate"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task FacturaE_ElReintentoRepiteElIdDePedido()
    {
        var (h, module, fisco) = await Start(f => f.Failures = 1);
        await using var _ = h;
        await using var __ = module;
        var account = await UsdAccount(h);

        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)], paymentMethod: "paypal_ppcpv"));
        await h.RunJobs();
        Assert.AreEqual(EmissionStatus.Unknown, (await h.Query(db => db.FiscalEmissions.SingleAsync())).Status);

        // Otro sistema usó IDs en el medio: el reintento igual manda el que quedó reservado.
        fisco.LastRequestId = 45;
        h.Clock.Now = h.Clock.Now.AddSeconds(61);
        await h.RunJobs();

        Assert.AreEqual(EmissionStatus.Authorized, (await h.Query(db => db.FiscalEmissions.SingleAsync())).Status);
        CollectionAssert.AreEqual(new[] { 41L, 41L }, fisco.Authorizations.Select(a => a["wsfex_id"]!.GetValue<long>()).ToArray());
    }

    [TestMethod]
    public async Task NotaDeCreditoE_DeUnaFacturaEnLaMismaMoneda_UsaLaCotizacionRegistradaYNoInformaLaCancelacion()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        var account = await UsdAccount(h);
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)], paymentMethod: "paypal_ppcpv"));
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(account, "manual:paypal", "P-1", Currency.Usd, 10m, PaymentStatus.Approved)));
        await h.RunJobs();

        await h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value, [new CreditLine("Anulación", 1, 10m, TaxTreatment.Exempt, 0m)], "Arrepentimiento"));
        await h.RunJobs();

        var note = fisco.Authorizations.Last();
        var doc = note["document"]!;
        Assert.AreEqual(21, doc["type_code"]!.GetValue<int>());
        Assert.AreEqual(19, doc["associated"]![0]!["type_code"]!.GetValue<int>());
        Assert.IsNull(doc["same_currency_payment"]);
        Assert.AreEqual("1449.0", doc["exchange_rate"]!.GetValue<string>());

        // La cotización copiada de la factura se comprueba contra la orientativa de la fecha de la nota (validación 10119).
        Assert.AreEqual(21, fisco.RateCalls.Single()["type_code"]!.GetValue<int>());

        // El fisco informa 40, pero la factura ya usó el 41: la nota toma el siguiente propio.
        Assert.AreEqual(42L, note["wsfex_id"]!.GetValue<long>());
    }

    [TestMethod]
    public async Task NotaDeCreditoE_SiElOriginalNoGuardoCotizacion_LaConsultaAlFisco()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        var account = await UsdAccount(h);
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)], paymentMethod: "paypal_ppcpv"));
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(account, "manual:paypal", "P-1", Currency.Usd, 10m, PaymentStatus.Approved)));
        await h.RunJobs();

        // Como un comprobante importado del sistema anterior, que no tiene la cotización.
        await h.Db(async db =>
        {
            var original = await db.FiscalDocuments.SingleAsync();
            var payload = JsonNode.Parse(original.Payload)!;
            payload["document"]!.AsObject().Remove("exchange_rate");
            original.Payload = payload.ToJsonString();
            await db.SaveChangesAsync();
        });

        await h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value, [new CreditLine("Anulación", 1, 10m, TaxTreatment.Exempt, 0m)], "Arrepentimiento"));
        await h.RunJobs();

        Assert.AreEqual("1449.0", fisco.Authorizations.Last()["document"]!["exchange_rate"]!.GetValue<string>());
        Assert.AreEqual(21, fisco.RateCalls.Single()["type_code"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task CotizacionFueraDeRango_SeRechazaSinLlamarAlFisco()
    {
        // Manual de WSFEv1 4.7, validación 10119: la cotización puede variar entre el 2 % y el 400 % de la orientativa.
        // Una nota de exportación copia la de su factura, y una factura importada puede traer 1: el núcleo lo frena antes.
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        var account = await UsdAccount(h);
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)], paymentMethod: "paypal_ppcpv"));
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(account, "manual:paypal", "P-1", Currency.Usd, 10m, PaymentStatus.Approved)));
        await h.RunJobs();
        await h.Db(async db =>
        {
            var original = await db.FiscalDocuments.SingleAsync();
            var payload = JsonNode.Parse(original.Payload)!;
            payload["document"]!["exchange_rate"] = "1";
            original.Payload = payload.ToJsonString();
            await db.SaveChangesAsync();
        });

        var noteId = await h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value, [new CreditLine("Anulación", 1, 10m, TaxTreatment.Exempt, 0m)], "Arrepentimiento"));
        await h.RunJobs();

        var emission = await h.Query(db => db.FiscalEmissions.SingleAsync(e => e.DocumentId == noteId));
        Assert.AreEqual(EmissionStatus.Rejected, emission.Status);
        StringAssert.Contains(emission.LastError, "10119");
        Assert.Contains("10119", emission.Codes!);
        Assert.HasCount(1, fisco.Authorizations);
        Assert.AreEqual(1, await h.Query(db => db.FiscalDocuments.CountAsync()));
        Assert.AreEqual(JobStatus.Dead, (await h.Query(db => db.Jobs.Where(j => j.Kind == JobKinds.FiscalEmit).OrderByDescending(j => j.Id).FirstAsync())).Status);
    }

    [TestMethod]
    public async Task CotizacionRegistrada_SiLaConsultaFallaDespuesDeAutorizar_SeCompletaDespues()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        var account = await UsdAccount(h);

        // Cancelación en la misma moneda: el comprobante va sin cotización y la registrada se consulta después de
        // autorizar. ARCA cae justo ahí: el CAE se guarda igual y la cotización, que el QR exige (RG 4892), queda a
        // cargo de un trabajo que se reintenta.
        fisco.FindError = ErrorCodes.UpstreamUnavailable;
        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)], paymentMethod: "paypal_ppcpv"));
        await h.RunJobs();

        var voucher = await h.Query(db => db.FiscalDocuments.SingleAsync());
        Assert.AreEqual("CAE1", voucher.AuthorizationCode);
        Assert.IsNull(JsonNode.Parse(voucher.Payload)!["document"]!["exchange_rate"]);
        Assert.AreEqual(JobStatus.Pending, (await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.FiscalRate))).Status);

        // Sigue fallando, ahora en forma definitiva: el trabajo muere visible y el barrido del día siguiente lo revive.
        fisco.FindError = ErrorCodes.InvalidConfig;
        h.Clock.Now = h.Clock.Now.AddSeconds(61);
        await h.RunJobs();
        Assert.AreEqual(JobStatus.Dead, (await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.FiscalRate))).Status);
        Assert.IsNull(JsonNode.Parse((await h.Query(db => db.FiscalDocuments.SingleAsync())).Payload)!["document"]!["exchange_rate"]);

        fisco.FindError = null;
        h.Clock.Now = h.Clock.Now.AddDays(1);
        await h.Tick();
        await h.RunJobs();
        voucher = await h.Query(db => db.FiscalDocuments.SingleAsync());
        Assert.AreEqual("1449.0", JsonNode.Parse(voucher.Payload)!["document"]!["exchange_rate"]!.GetValue<string>());
        Assert.AreEqual(JobStatus.Done, (await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.FiscalRate))).Status);
        Assert.AreEqual(1, await h.Query(db => db.FiscalDocuments.CountAsync()));
    }

    [TestMethod]
    public async Task FacturaBEnDolares_CanceladaEnLaMismaMoneda_VaSinCotizacionYLaNotaTambien()
    {
        // Manual de WSFEv1 4.7, validación 10038: con CanMisMonExt en S la cotización tiene que ser exactamente la del
        // día hábil anterior o se omite. El núcleo la omite y guarda la que registró el fisco.
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        var account = await UsdAccount(h, "AR");
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)], paymentMethod: "paypal_ppcpv"));
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(account, "manual:paypal", "P-1", Currency.Usd, 10m, PaymentStatus.Approved)));
        await h.RunJobs();

        var invoice = fisco.Authorizations.Single()["document"]!;
        Assert.AreEqual(6, invoice["type_code"]!.GetValue<int>());
        Assert.IsTrue(invoice["same_currency_payment"]!.GetValue<bool>());
        Assert.IsNull(invoice["exchange_rate"]);
        Assert.AreEqual("1449.0", JsonNode.Parse((await h.Query(db => db.FiscalDocuments.SingleAsync())).Payload)!["document"]!["exchange_rate"]!.GetValue<string>());

        await h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value, [new CreditLine("Anulación", 1, 10m, TaxTreatment.Taxed, 0.21m)], "Arrepentimiento"));
        await h.RunJobs();

        var note = fisco.Authorizations.Last()["document"]!;
        Assert.AreEqual(8, note["type_code"]!.GetValue<int>());
        Assert.IsTrue(note["same_currency_payment"]!.GetValue<bool>());
        Assert.IsNull(note["exchange_rate"]);
        Assert.IsEmpty(fisco.RateCalls);
        Assert.IsNull(fisco.Authorizations.Last()["wsfex_id"]);
    }

    [TestMethod]
    public async Task FacturaBEnDolares_CanceladaEnPesos_PideLaCotizacionYLaNotaLaDeSuFecha()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        var account = await UsdAccount(h, "AR");
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)], paymentMethod: "mercadopago_1"));
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(account, "manual:mercadopago", "P-1", Currency.Usd, 10m, PaymentStatus.Approved)));
        await h.RunJobs();

        var rate = fisco.RateCalls.Single();
        Assert.AreEqual("2026-11-02", rate["date"]!.GetValue<string>());
        Assert.AreEqual(6, rate["type_code"]!.GetValue<int>());
        var invoice = fisco.Authorizations.Single()["document"]!;
        Assert.IsFalse(invoice["same_currency_payment"]!.GetValue<bool>());
        Assert.AreEqual("1450.5", invoice["exchange_rate"]!.GetValue<string>());

        // La nota de otro día no copia la cotización de la factura: pide la de su propia fecha.
        h.Clock.Now = h.Clock.Now.AddDays(1);
        await h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value, [new CreditLine("Anulación", 1, 10m, TaxTreatment.Taxed, 0.21m)], "Arrepentimiento"));
        await h.RunJobs();

        Assert.HasCount(2, fisco.RateCalls);
        Assert.AreEqual("2026-11-03", fisco.RateCalls[1]["date"]!.GetValue<string>());
        var note = fisco.Authorizations.Last()["document"]!;
        Assert.IsFalse(note["same_currency_payment"]!.GetValue<bool>());
        Assert.AreEqual("1450.5", note["exchange_rate"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task FacturaE_IdDePedidoUsado_LiberaElIdParaQueElReintentoTomeOtro()
    {
        var (h, module, fisco) = await Start(f => f.RequestIdInUseOnce = true);
        await using var _ = h;
        await using var __ = module;
        var account = await UsdAccount(h);

        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)], paymentMethod: "paypal_ppcpv"));
        await h.RunJobs();

        var emission = await h.Query(db => db.FiscalEmissions.SingleAsync());
        Assert.AreEqual(EmissionStatus.Rejected, emission.Status);
        Assert.IsNull(emission.RequestId);
    }

    [TestMethod]
    public async Task SinCotizacionPublicada_LaEmisionEsperaUnasHoras()
    {
        var (h, module, fisco) = await Start(f => f.NoRateOnce = true);
        await using var _ = h;
        await using var __ = module;
        var account = await UsdAccount(h);

        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)], paymentMethod: "mercadopago_1"));
        await h.RunJobs();

        var job = await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.FiscalEmit));
        Assert.AreEqual(JobStatus.Pending, job.Status);
        Assert.IsGreaterThan(h.Clock.Now.AddHours(1), job.RunAt);
        Assert.AreEqual(EmissionStatus.Pending, (await h.Query(db => db.FiscalEmissions.SingleAsync())).Status);

        h.Clock.Now = h.Clock.Now.AddHours(5);
        await h.RunJobs();

        Assert.AreEqual(EmissionStatus.Authorized, (await h.Query(db => db.FiscalEmissions.SingleAsync())).Status);
        Assert.AreEqual(1, fisco.Authorized);
    }

    [TestMethod]
    public async Task ClienteDelExteriorConIva_VaComoFacturaBConCondicionDelExterior()
    {
        var (h, module, _) = await Start();
        await using var _h = h;
        await using var _m = module;
        var account = await UsdAccount(h);
        await h.Db(async db =>
        {
            // La regla forzada: el servicio se usa en el país y el cliente del exterior paga IVA (sección 6.2 del modelo).
            db.TaxRules.Add(new TaxRule { Country = "US", Outcome = TaxTreatment.Taxed, Rate = 0.21m, ValidFrom = new DateOnly(2026, 1, 1) });
            await db.SaveChangesAsync();
        });

        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(h.PlanId)], paymentMethod: "paypal_ppcpv"));
        await h.RunJobs();

        var fiscal = await h.Query(db => db.FiscalDocuments.SingleAsync());
        Assert.AreEqual(6, fiscal.TypeCode);
        Assert.AreEqual(3, fiscal.PointOfSale);
        var doc = JsonNode.Parse(fiscal.Payload)!["document"]!;
        Assert.AreEqual(FiscalTypes.ForeignClient, doc["receiver"]!["vat_condition"]!.GetValue<int>());
        Assert.AreEqual("1.74", doc["amounts"]!["vat"]![0]!["amount"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task EstadoDeLaInstancia_LlegaALaLlamadaSiguienteYQuedaGuardado()
    {
        var (h, module, fisco) = await Start(f => f.IssueTickets = true);
        await using var _ = h;
        await using var __ = module;

        await Order(h);
        await h.RunJobs();

        CollectionAssert.AreEqual(new[] { "T1" }, fisco.TicketsSeen);
        var instance = await h.Query(db => db.ModuleInstances.SingleAsync());
        Assert.AreEqual(1, instance.StateVersion);
        Assert.IsNotNull(instance.State);
    }

    [TestMethod]
    public async Task EstadoDeLaInstancia_EnUnError_TambienQuedaGuardado_YLoLlevaElReintento()
    {
        // El fisco entregó un ticket y la consulta siguiente falló: el ticket viaja en el error, el núcleo lo guarda y el
        // reintento ya lo lleva (punto 13 de la revisión de alta disponibilidad).
        var (h, module, fisco) = await Start(f => f.FailWithTicketOnce = true);
        await using var _ = h;
        await using var __ = module;

        await Order(h);
        await h.RunJobs();
        Assert.AreEqual(0, fisco.Authorized);
        Assert.IsNotNull((await h.Query(db => db.ModuleInstances.SingleAsync())).State, "El estado del error quedó guardado.");

        h.Clock.Now = h.Clock.Now.AddSeconds(5);
        await h.RunJobs();

        Assert.AreEqual(1, fisco.Authorized);
        CollectionAssert.AreEqual(new[] { "T-error" }, fisco.TicketsSeen);
    }

    [TestMethod]
    public async Task CorteDespuesDeAutorizar_SeResuelveConsultando_SinOtroNumero()
    {
        var (h, module, fisco) = await Start(f => f.CutOnce = true);
        await using var _ = h;
        await using var __ = module;

        await Order(h);
        await h.RunJobs();
        Assert.AreEqual(EmissionStatus.Unknown, (await h.Query(db => db.FiscalEmissions.SingleAsync())).Status);

        h.Clock.Now = h.Clock.Now.AddSeconds(61);
        await h.RunJobs();

        Assert.AreEqual(EmissionStatus.Authorized, (await h.Query(db => db.FiscalEmissions.SingleAsync())).Status);
        Assert.AreEqual(1L, (await h.Query(db => db.FiscalDocuments.SingleAsync())).Number);
        Assert.AreEqual(1, fisco.Authorized);
    }

    [TestMethod]
    public async Task Rechazo_QuedaRechazadaConLosCodigos()
    {
        var (h, module, _) = await Start(f => f.Reject = true);
        await using var _h = h;
        await using var _m = module;

        await Order(h);
        await h.RunJobs();

        var emission = await h.Query(db => db.FiscalEmissions.SingleAsync());
        Assert.AreEqual(EmissionStatus.Rejected, emission.Status);
        Assert.Contains("10013", emission.Codes!);
        Assert.AreEqual(0, await h.Query(db => db.FiscalDocuments.CountAsync()));
    }

    [TestMethod]
    public async Task ErrorDefinitivoDelModulo_QuedaRechazado_YNoFrenaLaCola()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;

        // Un DNI con puntos, como los que aceptaba la API: el módulo lo rechaza con invalid_input sin ir al fisco.
        long dotted = 0;
        await h.Db(async db =>
        {
            var account = new Account("Cliente con puntos", "puntos@example.com", Currency.Ars, h.Clock.Now);
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            db.TaxIdentities.Add(new TaxIdentity(account.Id, TaxDocumentType.Dni, "30.111.224", "Cliente con puntos", 5, "AR", h.Clock.Now));
            await db.SaveChangesAsync();
            dotted = account.Id;
        });
        var bad = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(dotted, [new OrderItem(h.PlanId)]));
        var good = await Order(h);
        await h.RunJobs();
        h.Clock.Now = h.Clock.Now.AddSeconds(61);
        await h.RunJobs();

        var emission = await h.Query(db => db.FiscalEmissions.SingleAsync(e => e.DocumentId == bad.InvoiceId));
        Assert.AreEqual(EmissionStatus.Rejected, emission.Status);
        Assert.Contains("document.receiver.doc_number", emission.Codes!);
        Assert.HasCount(1, fisco.Authorizations.Where(a => a["document"]!["receiver"]!["doc_number"]!.GetValue<string>() == "30.111.224"));
        var jobs = await h.Query(db => db.Jobs.Where(j => j.Kind == JobKinds.FiscalEmit).OrderBy(j => j.Id).Select(j => j.Status).ToListAsync());
        CollectionAssert.AreEqual(new[] { JobStatus.Dead, JobStatus.Done }, jobs);

        // La factura siguiente obtiene su CAE con el número que sigue, sin saltear ninguno.
        var fiscal = await h.Query(db => db.FiscalDocuments.SingleAsync());
        Assert.AreEqual(good.InvoiceId, fiscal.DocumentId);
        Assert.AreEqual(1L, fiscal.Number);
    }

    [TestMethod]
    public async Task ModuloCaidoEnMedio_ElReintentoDeOtroDiaNoChocaConLaClaveAnterior()
    {
        var (h, module, fisco) = await Start(f => f.HangOnce = true);
        await using var _ = h;
        await using var __ = module;

        await Order(h);
        await h.RunJobs();
        Assert.AreEqual(EmissionStatus.Unknown, (await h.Query(db => db.FiscalEmissions.SingleAsync())).Status);

        // El primer pedido sigue en curso en el módulo. Al otro día el fisco no lo tiene y se emite con la fecha nueva.
        h.Clock.Now = h.Clock.Now.AddDays(1);
        await h.RunJobs();

        // Con la máquina cargada el segundo pedido también puede pasar el plazo: otra vuelta lo encuentra.
        h.Clock.Now = h.Clock.Now.AddSeconds(61);
        await h.RunJobs();
        fisco.Release();

        var fiscal = await h.Query(db => db.FiscalDocuments.SingleAsync());
        Assert.AreEqual(1L, fiscal.Number);
        Assert.AreEqual(new DateOnly(2026, 11, 3), fiscal.Date);
    }

    [TestMethod]
    public async Task ConsultaConErrorDefinitivo_MuereVisibleEnElPrimerIntento_YLaSerieSigueLibre()
    {
        var (h, module, fisco) = await Start(f => f.CutOnce = true);
        await using var _ = h;
        await using var __ = module;
        await Order(h);
        await h.RunJobs();
        Assert.AreEqual(EmissionStatus.Unknown, (await h.Query(db => db.FiscalEmissions.SingleAsync())).Status);

        // Venció el certificado: la consulta devuelve invalid_config. El trabajo muere en el primer intento con el
        // motivo, en vez de dar vueltas dos horas con la clave de la serie tomada.
        fisco.FindError = ErrorCodes.InvalidConfig;
        h.Clock.Now = h.Clock.Now.AddSeconds(61);
        await h.RunJobs();
        var job = await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.FiscalEmit));
        Assert.AreEqual(JobStatus.Dead, job.Status);
        Assert.AreEqual(1, job.Attempts);
        StringAssert.Contains(job.LastError, "invalid_config");
        var emission = await h.Query(db => db.FiscalEmissions.SingleAsync());
        Assert.AreEqual(EmissionStatus.Unknown, emission.Status);
        StringAssert.Contains(emission.LastError, "invalid_config");

        // Corregido el certificado, el barrido del día siguiente la reencola y la consulta encuentra el comprobante.
        fisco.FindError = null;
        h.Clock.Now = h.Clock.Now.AddDays(1);
        await h.Tick();
        await h.RunJobs();
        Assert.AreEqual(EmissionStatus.Authorized, (await h.Query(db => db.FiscalEmissions.SingleAsync())).Status);
        Assert.AreEqual(1, await h.Query(db => db.FiscalDocuments.CountAsync()));
        Assert.AreEqual(JobStatus.Done, (await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.FiscalCheck))).Status);
    }

    [TestMethod]
    public async Task IncertidumbreQueNoTermina_TieneTope_YLiberaLaCola()
    {
        var (h, module, fisco) = await Start(f => f.Failures = int.MaxValue);
        await using var _ = h;
        await using var __ = module;

        await Order(h);
        await h.RunJobs();
        for (var i = 0; i < FiscalEmissionHandler.MaxAttempts; i++)
        {
            h.Clock.Now = h.Clock.Now.AddSeconds(61);
            await h.RunJobs();
        }

        var emission = await h.Query(db => db.FiscalEmissions.SingleAsync());
        Assert.AreEqual(EmissionStatus.Pending, emission.Status);
        StringAssert.Contains(emission.LastError, "intentos");
        Assert.HasCount(FiscalEmissionHandler.MaxAttempts, fisco.Authorizations);
        Assert.AreEqual(JobStatus.Dead, await h.Query(db => db.Jobs.Where(j => j.Kind == JobKinds.FiscalEmit).Select(j => j.Status).SingleAsync()));

        // La cola sigue: la factura siguiente obtiene su CAE.
        fisco.Failures = 0;
        await Order(h);
        await h.RunJobs();
        Assert.AreEqual(1L, (await h.Query(db => db.FiscalDocuments.SingleAsync())).Number);
    }

    [TestMethod]
    public void Coincidencia_ComparaElDocumentoDelReceptorComoNumero()
    {
        // ARCA guarda el DNI como número: lo devuelve sin el cero de adelante que tiene la ficha.
        var payload = new JsonObject
        {
            ["document"] = new JsonObject
            {
                ["date"] = "2026-11-02",
                ["amounts"] = new JsonObject { ["total"] = "12100.00" },
                ["receiver"] = new JsonObject { ["doc_number"] = "07123456" },
            },
        };
        var found = new JsonObject { ["document"] = payload["document"]!.DeepClone() };
        found["document"]!["receiver"]!["doc_number"] = "7123456";
        Assert.IsTrue(FiscalPayload.Matches(found, payload));

        found["document"]!["receiver"]!["doc_number"] = "7123457";
        Assert.IsFalse(FiscalPayload.Matches(found, payload));
    }

    [TestMethod]
    public void Coincidencia_ExigeLosDemasDatosDelComprobante()
    {
        // Dos facturas del mismo cliente con la misma fecha y el mismo total solo se distinguen por estos datos.
        var payload = new JsonObject
        {
            ["wsfex_id"] = 41,
            ["document"] = new JsonObject
            {
                ["date"] = "2026-11-02",
                ["concept"] = "services",
                ["service_period"] = new JsonObject { ["from"] = "2026-11-02", ["to"] = "2026-12-01" },
                ["payment_due"] = "2026-11-12",
                ["receiver"] = new JsonObject { ["doc_number"] = "30111222" },
                ["currency"] = "PES",
                ["amounts"] = new JsonObject
                {
                    ["total"] = "12100.00",
                    ["vat"] = new JsonArray(new JsonObject { ["rate_id"] = 5, ["base"] = "10000.00", ["amount"] = "2100.00" }),
                },
            },
        };
        JsonObject Found(Action<JsonNode> change)
        {
            var found = payload.DeepClone().AsObject();
            found["wsfex_id"] = "41";
            change(found);
            return found;
        }

        Assert.IsTrue(FiscalPayload.Matches(Found(_ => { }), payload));
        Assert.IsTrue(FiscalPayload.Matches(Found(f => f["document"]!["amounts"]!["vat"]![0]!["base"] = "10000"), payload));
        Assert.IsTrue(FiscalPayload.Matches(Found(f => f["document"]!.AsObject().Remove("payment_due")), payload));
        foreach (var change in new Action<JsonNode>[]
        {
            f => f["document"]!["payment_due"] = "2026-11-17",
            f => f["document"]!["service_period"]!["from"] = "2026-10-02",
            f => f["document"]!["service_period"]!["to"] = "2026-11-01",
            f => f["document"]!["currency"] = "DOL",
            f => f["document"]!["concept"] = "products",
            f => f["document"]!["amounts"]!["vat"]![0]!["base"] = "9000.00",
            f => f["document"]!["amounts"]!["vat"] = new JsonArray(),
            f => f["wsfex_id"] = "42",
        })
        {
            Assert.IsFalse(FiscalPayload.Matches(Found(change), payload));
        }
    }

    [TestMethod]
    public async Task DosEmisionesConElMismoNumero_NingunaAdoptaElComprobante()
    {
        var (h, module, fisco) = await Start(f => f.Failures = FiscalEmissionHandler.MaxAttempts);
        await using var _ = h;
        await using var __ = module;

        // La primera factura queda frenada en el tope, con el número 1 propuesto.
        await Order(h);
        await h.RunJobs();
        for (var i = 0; i < FiscalEmissionHandler.MaxAttempts; i++)
        {
            h.Clock.Now = h.Clock.Now.AddSeconds(61);
            await h.RunJobs();
        }

        // Su último pedido llega tarde al fisco justo cuando una segunda factura, igual a la primera, propone el 1.
        fisco.Late = fisco.Authorizations.Last()["document"]!.DeepClone().AsObject();
        await Order(h);
        await h.RunJobs();
        h.Clock.Now = h.Clock.Now.AddSeconds(61);
        await h.RunJobs();

        var statuses = await h.Query(db => db.FiscalEmissions.OrderBy(e => e.Id).Select(e => e.Status).ToListAsync());
        CollectionAssert.AreEqual(new[] { EmissionStatus.Pending, EmissionStatus.Conflict }, statuses);
        Assert.AreEqual(0, await h.Query(db => db.FiscalDocuments.CountAsync()));
    }

    [TestMethod]
    public async Task DocumentoConComprobante_NoSeVuelveAEmitir_AunqueLaEmisionQuedePisada()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        var placed = await Order(h);
        await h.RunJobs();

        // Un escritor atrasado dejó la emisión pendiente y llega otro aviso del mismo documento.
        await h.Db(async db =>
        {
            var emission = await db.FiscalEmissions.SingleAsync();
            db.Entry(emission).Property(e => e.Status).CurrentValue = EmissionStatus.Pending;
            await db.SaveChangesAsync();
        });
        await h.Use<JobQueue, bool>(async q =>
        {
            await q.Enqueue(JobKinds.FiscalEmit, new { document_id = placed.InvoiceId }, Subscriptions.FiscalEntity);
            return true;
        });
        await h.RunJobs();

        Assert.AreEqual(1, fisco.Authorized);
        Assert.HasCount(1, fisco.Authorizations);
    }

    [TestMethod]
    public async Task EscritorAtrasado_NoPisaLaEmisionNiLaSerie()
    {
        var (h, module, _) = await Start();
        await using var _h = h;
        await using var _m = module;
        await Order(h);
        await h.RunJobs();

        await StaleWrite(h, db => db.FiscalEmissions.SingleAsync(), e => e.LastError = "otro");
        await StaleWrite(h, db => db.FiscalSeries.SingleAsync(), s => s.LastNumber = 7);
    }

    /// <summary>Uno lee la fila, otro la cambia y guarda, y el cambio del primero tiene que fallar.</summary>
    private static Task StaleWrite<T>(CoreHarness h, Func<BillingDbContext, Task<T>> load, Action<T> change) => h.Db(async stale =>
    {
        var row = await load(stale);
        await h.Db(async fresh =>
        {
            change(await load(fresh));
            await fresh.SaveChangesAsync();
        });
        change(row);
        await Assert.ThrowsExactlyAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
    });

    [TestMethod]
    public async Task AltaConDniConPuntosOSinCondicionDeIva_SeRechaza()
    {
        var (h, module, _) = await Start();
        await using var _h = h;
        await using var _m = module;

        foreach (var tax in new[]
        {
            new TaxIdentityInput(TaxDocumentType.Dni, "30.111.224", "Cliente", 5, "AR"),
            new TaxIdentityInput(TaxDocumentType.Dni, "30111224", "Cliente", 0, "AR"),
        })
        {
            await Assert.ThrowsExactlyAsync<ValidationException>(() =>
                h.Use<AccountService, long>(s => s.Create(new AccountInput("Cliente", "alta@example.com", Currency.Ars, Tax: tax))));
        }
    }

    [TestMethod]
    public async Task FiscoAdelantado_FrenaLaSerie()
    {
        var (h, module, fisco) = await Start(f => f.Offset = 5);
        await using var _ = h;
        await using var __ = module;

        await Order(h);
        await h.RunJobs();

        var emission = await h.Query(db => db.FiscalEmissions.SingleAsync());
        Assert.AreEqual(EmissionStatus.Pending, emission.Status);
        StringAssert.Contains(emission.LastError, "fuera de sincronía");
        Assert.AreEqual(0, fisco.Authorized);
    }

    [TestMethod]
    public async Task CuentaSinComprobante_NoEmite()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        await h.Db(async db =>
        {
            (await db.Accounts.SingleAsync()).NoFiscalDocument = true;
            await db.SaveChangesAsync();
        });

        await Order(h);
        await h.RunJobs();

        Assert.AreEqual(0, await h.Query(db => db.FiscalEmissions.CountAsync()));
        Assert.AreEqual(0, fisco.Authorized);
    }

    [TestMethod]
    public async Task UnaFallaImprevista_MuereVisibleEnElPrimerIntentoYNoFrenaLaCola()
    {
        var (h, module, fisco) = await Start(f => f.BreakLastNumberOnce = true);
        await using var _ = h;
        await using var __ = module;

        await Order(h);
        await Order(h);
        await h.RunJobs();

        // La primera emisión murió con el motivo a la vista y la segunda salió igual.
        var dead = await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.FiscalEmit && j.Status == JobStatus.Dead));
        Assert.AreEqual(1, dead.Attempts);
        Assert.AreEqual(1, fisco.Authorized);
        var failed = await h.Query(db => db.FiscalEmissions.SingleAsync(e => e.Status == EmissionStatus.Pending));
        Assert.IsNotNull(failed.LastError);
    }

    /// <summary>Recargo por mora prendido: a los 5 días del vencimiento, el 10 % de lo que se debe.</summary>
    private static void LateFees(BillingOptions o)
    {
        o.LateFeeAfterDays = 5;
        o.LateFeePercent = 10m;
    }

    [TestMethod]
    public async Task RecargoDeUnaFacturaImportada_SeEmiteConElReceptorDeLaCuenta_YAsociaElComprobanteImportado()
    {
        var (h, module, fisco) = await Start(configure: LateFees);
        await using var _h = h;
        await using var _m = module;
        await AdvancedBillingTests.ImportedInvoice(h, 7000, new DateOnly(2026, 10, 20));
        await h.Db(async db =>
        {
            // El comprobante que la factura tuvo en el sistema anterior (D-17): la nota del recargo se asocia a él.
            var issuer = await db.IssuerProfiles.SingleAsync();
            db.FiscalDocuments.Add(new FiscalDocument
            {
                DocumentId = 7000,
                IssuerId = issuer.Id,
                PointOfSale = 3,
                TypeCode = 6,
                Number = 90,
                Date = new DateOnly(2026, 10, 1),
                AuthorizationCode = "CAE90",
                Payload = """{"imported":true,"currency":"PES","exchange_rate":"1"}""",
                Imported = true,
                CreatedAt = h.Clock.Now,
            });
            await db.SaveChangesAsync();
        });

        Assert.IsNotNull(await h.Use<DunningService, long?>(s => s.ChargeLateFee(7000)));
        await h.RunJobs();

        var note = await h.Query(db => db.FiscalDocuments.SingleAsync(f => !f.Imported));
        Assert.AreEqual(7, note.TypeCode, "Nota de débito B: el receptor es el consumidor final de la cuenta, no el vacío de la importación.");
        var sent = JsonNode.Parse(note.Payload)!["document"]!;
        Assert.AreEqual("30111222", sent["receiver"]!["doc_number"]!.GetValue<string>());
        Assert.AreEqual(90L, sent["associated"]!.AsArray().Single()!["number"]!.GetValue<long>());
        Assert.AreEqual(1, fisco.Authorized);
    }

    [TestMethod]
    public async Task RecargoDeUnaFacturaSinComprobante_EsperaAlDeLaFactura_YSaleCuandoLaFacturaTieneElSuyo()
    {
        var (h, module, fisco) = await Start(configure: LateFees);
        await using var _h = h;
        await using var _m = module;
        await AtFirstPayment(h);
        var placed = await Order(h); // 12.100, vence el 09/11 y no tiene comprobante hasta el primer cobro.
        await h.RunJobs();

        h.Clock.Now = new DateTimeOffset(2026, 11, 15, 15, 0, 0, TimeSpan.Zero);
        var fee = (await h.Use<DunningService, long?>(s => s.ChargeLateFee(placed.InvoiceId!.Value)))!.Value;
        await h.RunJobs();

        // Sin el comprobante de la factura la nota de débito no se manda, pero tampoco se rechaza: espera pendiente.
        var waiting = await h.Query(db => db.FiscalEmissions.SingleAsync(e => e.DocumentId == fee));
        Assert.AreEqual((EmissionStatus.Pending, FiscalEmissionHandler.NoteWaiting), (waiting.Status, waiting.LastError));
        Assert.AreEqual(0, fisco.Authorized);

        // El cliente paga primero el recargo: la factura sigue sin comprobante y la nota sigue esperando.
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, 1210m, PaymentStatus.Approved, [fee])));
        await h.RunJobs();
        Assert.AreEqual(0, fisco.Authorized);

        // Cuando paga la factura, la factura recibe su comprobante y la nota sale enseguida, asociada a él: no espera al
        // barrido del día siguiente, aunque la factura y la nota vayan por colas de series distintas.
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-2", Currency.Ars, 12100m, PaymentStatus.Approved, [placed.InvoiceId!.Value])));
        await h.RunJobs();

        Assert.AreEqual(2, fisco.Authorized);
        var invoice = await h.Query(db => db.FiscalDocuments.SingleAsync(f => f.DocumentId == placed.InvoiceId));
        var note = await h.Query(db => db.FiscalDocuments.SingleAsync(f => f.DocumentId == fee));
        var associated = JsonNode.Parse(note.Payload)!["document"]!["associated"]!.AsArray().Single()!;
        Assert.AreEqual((invoice.TypeCode, invoice.Number), (associated["type_code"]!.GetValue<int>(), associated["number"]!.GetValue<long>()));
        Assert.AreEqual(EmissionStatus.Authorized, (await h.Query(db => db.FiscalEmissions.SingleAsync(e => e.DocumentId == fee))).Status);
    }

    [TestMethod]
    public async Task NotaDeUnaFacturaImportadaConVariosComprobantes_AsociaLaUltimaFactura()
    {
        var (h, module, _) = await Start();
        await using var _h = h;
        await using var _m = module;
        await h.Db(async db =>
        {
            // Una factura del sistema anterior con tres comprobantes (D-17): dos facturas, una reemitida, y la nota de allá.
            var amounts = TaxCalculator.Compute(PriceMode.TaxIncluded, [new("0", 12100m, TaxTreatment.Taxed, 0.21m)]);
            var receiver = System.Text.Json.JsonSerializer.Serialize(new ReceiverSnapshot(TaxDocumentType.Dni, "30111222", "Cliente de prueba", 5, "AR", null));
            var invoice = Document.FromImport(7000, DocumentKind.Invoice, h.AccountId, Currency.Ars, 7000, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 8), amounts, 0m, 0m, receiver, h.Clock.Now);
            invoice.ImportLine(LineKind.Manual, "VPS", 12100m, TaxTreatment.Taxed, 0.21m, null);
            db.Documents.Add(invoice);
            await db.SaveChangesAsync();
            var issuer = await db.IssuerProfiles.SingleAsync();
            foreach (var (type, number, date) in new[] { (6, 90L, new DateOnly(2026, 9, 1)), (6, 95L, new DateOnly(2026, 9, 3)), (8, 3L, new DateOnly(2026, 9, 4)) })
            {
                db.FiscalDocuments.Add(new FiscalDocument
                {
                    DocumentId = 7000,
                    IssuerId = issuer.Id,
                    PointOfSale = 3,
                    TypeCode = type,
                    Number = number,
                    Date = date,
                    AuthorizationCode = $"CAE{number}",
                    Payload = """{"imported":true,"currency":"PES","exchange_rate":"1"}""",
                    Imported = true,
                    CreatedAt = h.Clock.Now,
                });
            }

            await db.SaveChangesAsync();
        });

        await h.Use<CreditService, long>(s => s.IssueCreditNote(7000, [new CreditLine("Anulación", 1, 12100m, TaxTreatment.Taxed, 0.21m)], "Baja"));
        await h.RunJobs();

        var note = await h.Query(db => db.FiscalDocuments.SingleAsync(f => !f.Imported));
        Assert.AreEqual(8, note.TypeCode);
        var associated = JsonNode.Parse(note.Payload)!["document"]!["associated"]!.AsArray().Single()!;
        Assert.AreEqual(6, associated["type_code"]!.GetValue<int>());
        Assert.AreEqual(95L, associated["number"]!.GetValue<long>());
    }

    [TestMethod]
    public async Task NotaDeCredito_UsaElReceptorCongeladoEnLaFactura()
    {
        var (h, module, _) = await Start();
        await using var _h = h;
        await using var _m = module;
        var placed = await Order(h);
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, 12100m, PaymentStatus.Approved)));
        await h.RunJobs();

        // La cuenta pasa a responsable inscripto: la nota de una factura B sigue siendo B, con el receptor de entonces.
        await h.Db(async db =>
        {
            (await db.TaxIdentities.SingleAsync(t => t.AccountId == h.AccountId && t.ValidTo == null)).Close(h.Clock.Now);
            db.TaxIdentities.Add(new TaxIdentity(h.AccountId, TaxDocumentType.Cuit, "30711111110", "Cliente SA", 1, "AR", h.Clock.Now));
            await db.SaveChangesAsync();
        });
        await h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value, [new CreditLine("Anulación", 1, 12100m, TaxTreatment.Taxed, 0.21m)], "Arrepentimiento"));
        await h.RunJobs();

        var note = await h.Query(db => db.FiscalDocuments.SingleAsync(f => f.DocumentId != placed.InvoiceId));
        Assert.AreEqual(8, note.TypeCode);
        var receiver = JsonNode.Parse(note.Payload)!["document"]!["receiver"]!;
        Assert.AreEqual("30111222", receiver["doc_number"]!.GetValue<string>());
        Assert.AreEqual(5, receiver["vat_condition"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task NotaDeUnaFacturaSinComprobante_SeRechazaSinLlamarAlFisco()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        await AtFirstPayment(h);
        var placed = await Order(h);
        await h.RunJobs();

        // La factura impaga se anula (mora) sin haber tenido comprobante: la nota no tiene qué asociar (D-05).
        await h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value, [new CreditLine("Anulación", 1, 12100m, TaxTreatment.Taxed, 0.21m)], "Factura impaga anulada"));
        await h.RunJobs();

        var emission = await h.Query(db => db.FiscalEmissions.SingleAsync());
        Assert.AreEqual(EmissionStatus.Rejected, emission.Status);
        StringAssert.Contains(emission.LastError, "no tiene comprobante fiscal");
        Assert.AreEqual(0, fisco.Authorized);
    }

    private static Task AtFirstPayment(CoreHarness h) => h.Db(async db =>
    {
        (await db.IssuerProfiles.SingleAsync()).Moment = EmissionMoment.AtFirstPayment;
        await db.SaveChangesAsync();
    });

    [TestMethod]
    public async Task Planificador_EncolaElBarridoFiscalUnaVezPorDia()
    {
        var (h, module, _) = await Start();
        await using var _h = h;
        await using var _m = module;

        await h.Tick();
        await h.Tick();

        Assert.AreEqual(1, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == "fiscal.sweep")));
    }

    [TestMethod]
    public async Task BarridoFiscal_AlPrimerCobro_EmiteLoImpagoElUltimoDiaDelMes()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        await AtFirstPayment(h);
        await Order(h);
        await h.RunJobs();
        Assert.AreEqual(0, fisco.Authorized);

        // El 2 de noviembre el barrido no toca una factura impaga. El 30, la emite (RG 5866, sección 7.2 del modelo).
        await h.Tick();
        await h.RunJobs();
        Assert.AreEqual(0, fisco.Authorized);

        h.Clock.Now = new DateTimeOffset(2026, 11, 30, 15, 0, 0, TimeSpan.Zero);
        await h.Tick();
        await h.RunJobs();

        Assert.AreEqual(1, fisco.Authorized);
        Assert.AreEqual(EmissionStatus.Authorized, (await h.Query(db => db.FiscalEmissions.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task BarridoFiscal_NoEmiteUnaFacturaAnuladaSinCobro_NiSuNota()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        await AtFirstPayment(h);
        var placed = await Order(h);
        await h.RunJobs();

        // La mora la anula el 25 sin un peso cobrado: la nota no tiene qué asociar (D-05) y la factura ya no va a
        // tener comprobante, porque no se cobró ni se prestó el servicio.
        h.Clock.Now = new DateTimeOffset(2026, 11, 25, 15, 0, 0, TimeSpan.Zero);
        var noteId = await h.Use<CreditService, long?>(s => s.CancelIfUnpaid(placed.InvoiceId!.Value, "Factura impaga anulada"));
        await h.RunJobs();
        var note = await h.Query(db => db.FiscalEmissions.SingleAsync(e => e.DocumentId == noteId));
        Assert.AreEqual(EmissionStatus.Rejected, note.Status);
        StringAssert.Contains(note.LastError, "no tiene comprobante fiscal");

        // La red de fin de mes deja pasar a la anulada: nada de CAE por una factura en estado Cancelled.
        h.Clock.Now = new DateTimeOffset(2026, 11, 30, 15, 0, 0, TimeSpan.Zero);
        await h.Tick();
        await h.RunJobs();
        Assert.AreEqual(0, fisco.Authorized);
        Assert.AreEqual(0, await h.Query(db => db.FiscalDocuments.CountAsync()));
        Assert.AreEqual(0, await h.Query(db => db.FiscalEmissions.CountAsync(e => e.DocumentId == placed.InvoiceId)));
    }

    [TestMethod]
    public async Task NotaAntesDelCobro_EsperaElComprobanteDelOriginal_YLoSigue()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        await AtFirstPayment(h);
        var placed = await Order(h);
        await h.RunJobs();

        // Bonificación parcial antes del cobro: la factura es emitible pero todavía no tiene comprobante, y la nota
        // espera en vez de quedar rechazada para siempre.
        var noteId = await h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value, [new CreditLine("Bonificación", 1, 2420m, TaxTreatment.Taxed, 0.21m)], "Bonificación"));
        await h.RunJobs();
        var note = await h.Query(db => db.FiscalEmissions.SingleAsync(e => e.DocumentId == noteId));
        Assert.AreEqual(EmissionStatus.Pending, note.Status);
        StringAssert.Contains(note.LastError, "espera");
        Assert.AreEqual(0, fisco.Authorized);

        // El cliente paga lo que queda: la factura sale por el total y la nota la sigue enseguida, sin esperar al barrido.
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, 9680m, PaymentStatus.Approved)));
        await h.RunJobs();
        Assert.AreEqual(2, fisco.Authorized);

        var vouchers = await h.Query(db => db.FiscalDocuments.OrderBy(f => f.Id).ToListAsync());
        CollectionAssert.AreEqual(new[] { 6, 8 }, vouchers.Select(v => v.TypeCode).ToArray());
        Assert.AreEqual(noteId, vouchers[1].DocumentId);
        var associated = JsonNode.Parse(vouchers[1].Payload)!["document"]!["associated"]![0]!;
        Assert.AreEqual(6, associated["type_code"]!.GetValue<int>());
        Assert.AreEqual(vouchers[0].Number, associated["number"]!.GetValue<long>());
        Assert.AreEqual(EmissionStatus.Authorized, (await h.Query(db => db.FiscalEmissions.SingleAsync(e => e.DocumentId == noteId))).Status);
    }

    [TestMethod]
    public async Task BarridoFiscal_ConLaSerieDesviada_MuereUnaVezPorDia_YNoSeMultiplica()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        await Order(h);
        await h.RunJobs();
        Assert.AreEqual(1, fisco.Authorized);
        await AtFirstPayment(h);
        await Order(h);
        await h.RunJobs();
        var calls = fisco.Calls;

        // Otro sistema usó el punto de venta: el fisco va 3 adelante. El barrido de fin de mes encola la emisión de la
        // impaga y el control de la serie, los dos mueren visibles una sola vez, y los ticks siguientes del día no
        // reviven nada ni vuelven a consultar al fisco.
        fisco.Offset = 3;
        h.Clock.Now = new DateTimeOffset(2026, 11, 30, 15, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 3; i++)
        {
            await h.Tick();
            await h.RunJobs();
            h.Clock.Now = h.Clock.Now.AddMinutes(1);
        }

        var jobs = await h.Query(db => db.Jobs.Where(j => j.Kind.StartsWith("fiscal.")).OrderBy(j => j.Id).Select(j => new { j.Kind, j.Status, j.LastError }).ToListAsync());
        Assert.AreEqual(JobStatus.Done, jobs.Single(j => j.Kind == JobKinds.FiscalSweep).Status);
        var emits = jobs.Where(j => j.Kind == JobKinds.FiscalEmit).ToList();
        Assert.HasCount(3, emits);
        Assert.AreEqual(JobStatus.Dead, emits[2].Status);
        StringAssert.Contains(emits[2].LastError, "fuera de sincronía");
        Assert.IsTrue(emits.Take(2).All(e => e.Status == JobStatus.Done));
        var check = jobs.Single(j => j.Kind == JobKinds.FiscalCheck);
        Assert.AreEqual(JobStatus.Dead, check.Status);
        StringAssert.Contains(check.LastError, "fuera de sincronía");
        Assert.AreEqual(calls + 2, fisco.Calls);
        Assert.AreEqual(1, fisco.Authorized);
    }

    [TestMethod]
    public async Task BarridoFiscal_ElDelUltimoDiaCorridoAlDiaSiguiente_IgualEmite()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        await AtFirstPayment(h);
        await Order(h);
        await h.RunJobs();

        // El planificador encola el barrido del 30 a última hora (hora de Buenos Aires) y el corredor lo toma el 1:
        // la factura del mes que cerró sale igual, dentro del plazo de la RG 5866.
        h.Clock.Now = new DateTimeOffset(2026, 12, 1, 2, 30, 0, TimeSpan.Zero);
        await h.Tick();
        Assert.AreEqual("2026-11-30", JsonNode.Parse((await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.FiscalSweep))).Payload)!["date"]!.GetValue<string>());
        h.Clock.Now = new DateTimeOffset(2026, 12, 1, 3, 30, 0, TimeSpan.Zero);
        await h.RunJobs();

        Assert.AreEqual(1, fisco.Authorized);
    }

    [TestMethod]
    public async Task BarridoFiscal_SiNoCorrioElUltimoDia_EmiteLoDelMesAnteriorEnElSiguiente()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        await AtFirstPayment(h);
        await Order(h);
        await h.RunJobs();

        // Ningún barrido corrió el 30 (nodo caído). El del 1 emite lo impago del mes anterior en vez de esperar al 31.
        h.Clock.Now = new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero);
        await h.Tick();
        await h.RunJobs();
        Assert.AreEqual(1, fisco.Authorized);

        // Una factura del mes en curso sigue esperando a fin de mes.
        await Order(h);
        h.Clock.Now = h.Clock.Now.AddDays(1);
        await h.Tick();
        await h.RunJobs();
        Assert.AreEqual(1, fisco.Authorized);
    }

    [TestMethod]
    public async Task BarridoFiscal_ReencolaLaEmisionCuyoTrabajoMurio_YControlaLaSerie()
    {
        var (h, module, fisco) = await Start(f => f.BreakLastNumberOnce = true);
        await using var _ = h;
        await using var __ = module;
        await Order(h);
        await h.RunJobs();
        Assert.AreEqual(0, fisco.Authorized);

        await h.Tick();
        await h.RunJobs();
        Assert.AreEqual(1, fisco.Authorized);

        // El control del día encontró la serie en sincronía y la dejó marcada.
        var series = await h.Query(db => db.FiscalSeries.SingleAsync());
        Assert.AreEqual(h.Clock.Now, series.SyncedAt);

        // Al otro día el fisco va adelante: el control de la serie lo deja a la vista en vez de callar, y borra la marca.
        fisco.Offset = 3;
        h.Clock.Now = h.Clock.Now.AddDays(1);
        await h.Tick();
        await h.RunJobs();

        var check = await h.Query(db => db.Jobs.Where(j => j.Kind == JobKinds.FiscalCheck).OrderByDescending(j => j.Id).FirstAsync());
        Assert.AreEqual(JobStatus.Dead, check.Status);
        StringAssert.Contains(check.LastError, "fuera de sincronía");
        Assert.IsNull((await h.Query(db => db.FiscalSeries.SingleAsync())).SyncedAt);
    }

    [TestMethod]
    public async Task SerieEnSincronia_NoPideElUltimoNumero_YVuelveAPedirloPasadoUnDia()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;

        // La primera emisión consulta el último número y autoriza: dos llamadas.
        await Order(h);
        await h.RunJobs();
        Assert.AreEqual(2, fisco.Calls);
        Assert.AreEqual(h.Clock.Now, (await h.Query(db => db.FiscalSeries.SingleAsync())).SyncedAt);

        // La segunda propone el siguiente sin consultar: una sola llamada.
        await Order(h);
        await h.RunJobs();
        Assert.AreEqual(3, fisco.Calls);
        Assert.AreEqual(2L, (await h.Query(db => db.FiscalDocuments.MaxAsync(f => f.Number))));

        // Pasado un día sin control, vuelve a consultar.
        h.Clock.Now = h.Clock.Now.AddHours(25);
        await Order(h);
        await h.RunJobs();
        Assert.AreEqual(5, fisco.Calls);
        Assert.AreEqual(3L, (await h.Query(db => db.FiscalDocuments.MaxAsync(f => f.Number))));
    }

    [TestMethod]
    public async Task ComprobanteAjenoEnLaSerie_EsConflicto_YLaSerieVuelveAConsultar()
    {
        var (h, module, fisco) = await Start();
        await using var _ = h;
        await using var __ = module;
        await Order(h);
        await h.RunJobs();

        // Otro sistema usó el 2 en el punto de venta. La emisión siguiente, con la serie en sincronía, propone el 2 sin
        // consultar: el fisco lo rechaza por fuera de secuencia, la consulta ve otro comprobante y es conflicto.
        var foreign = fisco.Authorizations.Single()["document"]!.DeepClone().AsObject();
        foreign["number"] = 2L;
        foreign["amounts"]!["total"] = "999.00";
        fisco.Late = foreign;
        var second = await Order(h);
        await h.RunJobs();
        h.Clock.Now = h.Clock.Now.AddSeconds(61);
        await h.RunJobs();

        var emission = await h.Query(db => db.FiscalEmissions.SingleAsync(e => e.DocumentId == second.InvoiceId));
        Assert.AreEqual(EmissionStatus.Conflict, emission.Status);
        StringAssert.Contains(emission.LastError, "otros datos");
        Assert.IsNull((await h.Query(db => db.FiscalSeries.SingleAsync())).SyncedAt);
        Assert.AreEqual(1, await h.Query(db => db.FiscalDocuments.CountAsync()));

        // La que sigue consulta el último número, ve al fisco adelante y frena la serie hasta que el staff la ajuste.
        var calls = fisco.Calls;
        var third = await Order(h);
        await h.RunJobs();
        var stopped = await h.Query(db => db.FiscalEmissions.SingleAsync(e => e.DocumentId == third.InvoiceId));
        Assert.AreEqual(EmissionStatus.Pending, stopped.Status);
        StringAssert.Contains(stopped.LastError, "fuera de sincronía");
        Assert.AreEqual(calls + 1, fisco.Calls);
    }

    [TestMethod]
    public async Task DosSeries_NoSeEsperan_YLaMismaSerieVaDeAUna()
    {
        var (h, module, fisco) = await Start(f => f.HangOnce = true);
        await using var _ = h;
        await using var __ = module;
        var usd = await UsdAccount(h);

        // Una factura B, otra B y una E. La primera B queda colgada en el módulo: la segunda B espera, la E no.
        var first = await Order(h);
        var second = await Order(h);
        var export = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(usd, [new OrderItem(h.PlanId)], paymentMethod: "paypal_ppcpv"));
        var keys = await h.Query(db => db.Jobs.Where(j => j.Kind == JobKinds.FiscalEmit).OrderBy(j => j.Id).Select(j => j.EntityKey).ToListAsync());
        var issuerId = await h.Query(db => db.IssuerProfiles.Select(i => i.Id).SingleAsync());
        CollectionAssert.AreEqual(new[] { Subscriptions.FiscalSeries(issuerId, 3, 6), Subscriptions.FiscalSeries(issuerId, 3, 6), Subscriptions.FiscalSeries(issuerId, 12, 19) }, keys);

        await h.RunJobs();

        Assert.AreEqual(EmissionStatus.Unknown, (await h.Query(db => db.FiscalEmissions.SingleAsync(e => e.DocumentId == first.InvoiceId))).Status);
        Assert.AreEqual(EmissionStatus.Authorized, (await h.Query(db => db.FiscalEmissions.SingleAsync(e => e.DocumentId == export.InvoiceId))).Status);
        Assert.AreEqual(0, await h.Query(db => db.FiscalEmissions.CountAsync(e => e.DocumentId == second.InvoiceId)));
        Assert.AreEqual(0, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.FiscalEmit && j.Status == JobStatus.Done && j.EntityKey == Subscriptions.FiscalSeries(issuerId, 3, 6))));

        // Resuelta la primera, la segunda B sale con el número que sigue.
        fisco.Release();
        h.Clock.Now = h.Clock.Now.AddSeconds(61);
        await h.RunJobs();
        h.Clock.Now = h.Clock.Now.AddSeconds(61);
        await h.RunJobs();
        var numbers = await h.Query(db => db.FiscalDocuments.Where(f => f.TypeCode == 6).OrderBy(f => f.DocumentId).Select(f => f.Number).ToListAsync());
        CollectionAssert.AreEqual(new[] { 1L, 2L }, numbers);
    }

    [TestMethod]
    public async Task NumeracionComercial_SeAsignaAlConfirmar_YNoSeConfirmaSinNumero()
    {
        var (h, module, _) = await Start();
        await using var _h = h;
        await using var _m = module;
        await using var scope = h.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var core = scope.ServiceProvider.GetRequiredService<BillingCore>();
        var receiver = await core.Receiver(h.AccountId, CancellationToken.None);

        // Emitido pero sin pasar por BillingCore.Commit: la transacción no sale, mejor que una factura sin número.
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var draft = new Document(DocumentKind.Invoice, h.AccountId, Currency.Ars, PriceMode.TaxIncluded, new DateOnly(2026, 11, 9), h.Clock.Now);
            draft.AddLine(LineKind.Manual, "Servicio", 1, 12100m, TaxTreatment.Taxed, 0.21m);
            db.Documents.Add(draft);
            await core.IssueDraft(draft, receiver, CancellationToken.None);
            Assert.IsNull(draft.Number);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => tx.CommitAsync());
        }

        db.ChangeTracker.Clear();
        Assert.AreEqual(0, await db.Documents.CountAsync());

        // Por el camino normal el número sale del contador justo antes del commit, en el orden de emisión.
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            foreach (var _ in Enumerable.Range(0, 2))
            {
                var draft = new Document(DocumentKind.Invoice, h.AccountId, Currency.Ars, PriceMode.TaxIncluded, new DateOnly(2026, 11, 9), h.Clock.Now);
                draft.AddLine(LineKind.Manual, "Servicio", 1, 12100m, TaxTreatment.Taxed, 0.21m);
                db.Documents.Add(draft);
                await core.IssueDraft(draft, receiver, CancellationToken.None);
            }

            await core.Commit(tx, CancellationToken.None);
        }

        CollectionAssert.AreEqual(new[] { 1L, 2L }, await db.Documents.OrderBy(d => d.Id).Select(d => d.Number!.Value).ToListAsync());
    }

    [TestMethod]
    public async Task NotaDeCredito_AsociaElComprobanteOriginal()
    {
        var (h, module, _) = await Start();
        await using var _h = h;
        await using var _m = module;
        var placed = await Order(h);
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, 12100m, PaymentStatus.Approved)));
        await h.RunJobs();

        await h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value, [new CreditLine("Anulación", 1, 12100m, TaxTreatment.Taxed, 0.21m)], "Arrepentimiento"));
        await h.RunJobs();

        var note = await h.Query(db => db.FiscalDocuments.SingleAsync(f => f.TypeCode == 8));
        var associated = JsonNode.Parse(note.Payload)!["document"]!["associated"]!.AsArray().Single()!;
        Assert.AreEqual(6, associated["type_code"]!.GetValue<int>());
        Assert.AreEqual(1L, associated["number"]!.GetValue<long>());
    }

    private const string ProductionInstance = """{"environment":"production"}""";

    [TestMethod]
    [DataRow(ProductionInstance)]
    [DataRow("""{"Environment":"Production"}""")]
    [DataRow("""{"Environment":"homologation","environment":"production"}""")]
    public async Task InstanciaDeProduccion_SinHabilitar_NoLlamaAlModuloYLaEmisionQuedaPendiente(string instanceConfig)
    {
        // La traba del núcleo: con Fiscal:ProductionEnabled apagada no sale ni la consulta del número hacia el fisco real.
        // La clave y el valor cuentan sin distinguir mayúsculas, como los lee el módulo, y con la clave repetida en
        // distinta caja el módulo se queda con la última: basta con que una diga producción.
        var (h, module, fisco) = await Start(instanceConfig: instanceConfig);
        await using var _ = h;
        await using var __ = module;

        await Order(h);
        await h.RunJobs();

        var emission = await h.Query(db => db.FiscalEmissions.SingleAsync());
        Assert.AreEqual(EmissionStatus.Pending, emission.Status);
        Assert.AreEqual(FiscalEmissionHandler.ProductionDisabled, emission.LastError);
        var job = await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.FiscalEmit));
        Assert.AreEqual(JobStatus.Dead, job.Status);
        StringAssert.Contains(job.LastError, "Fiscal:ProductionEnabled");
        Assert.AreEqual(0, fisco.Calls);
        Assert.AreEqual(0, await h.Query(db => db.FiscalDocuments.CountAsync()));
    }

    [TestMethod]
    public async Task InstanciaDeProduccion_Habilitada_Emite()
    {
        var (h, module, fisco) = await Start(instanceConfig: ProductionInstance, productionEnabled: true);
        await using var _ = h;
        await using var __ = module;

        await Order(h);
        await h.RunJobs();

        Assert.AreEqual(1, fisco.Authorized);
        Assert.AreEqual(EmissionStatus.Authorized, (await h.Query(db => db.FiscalEmissions.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task InstanciaDeHomologacion_EmiteSinHabilitarProduccion()
    {
        var (h, module, fisco) = await Start(instanceConfig: """{"environment":"homologation"}""");
        await using var _ = h;
        await using var __ = module;

        await Order(h);
        await h.RunJobs();

        Assert.AreEqual(1, fisco.Authorized);
    }
}
