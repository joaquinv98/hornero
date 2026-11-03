using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Fiscal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>
/// Operaciones del staff sobre la emisión fiscal por la API propia, con el fisco falso: adoptar, reintentar y ajustar
/// la serie, con sus rechazos y con la traba de producción.
/// </summary>
[TestClass]
public sealed class FiscalStaffTests
{
    private const string ProductionInstance = """{"environment":"production"}""";

    private sealed record Setup(WebHarness W, TestModule Module, FakeFisco Fisco, string Token, long PlanId) : IAsyncDisposable
    {
        public async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, string? token = null)
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Token);
            return await W.Client.SendAsync(request);
        }

        public async Task<JsonNode> Json(HttpMethod method, string path, HttpStatusCode expected, object? body = null)
        {
            using var response = await Send(method, path, body);
            var text = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(expected, response.StatusCode, text);
            return JsonNode.Parse(text)!;
        }

        public async Task<long> Order()
        {
            await using var scope = W.Services.CreateAsyncScope();
            var placed = await scope.ServiceProvider.GetRequiredService<OrderService>().PlaceOrder(W.AccountId, [new OrderItem(PlanId)]);
            return placed.InvoiceId!.Value;
        }

        public Task<FiscalEmission> Emission(long documentId) => W.Query(db => db.FiscalEmissions.AsNoTracking().SingleAsync(e => e.DocumentId == documentId));

        public Task<List<AuditEvent>> Audit(string action) => W.Query(db => db.Set<AuditEvent>().AsNoTracking().Where(e => e.Action == action).OrderBy(e => e.Id).ToListAsync());

        public async ValueTask DisposeAsync()
        {
            await Module.DisposeAsync();
            await W.DisposeAsync();
        }
    }

    /// <summary>El host web con llaves, el fisco falso y una llave con los permisos fiscales.</summary>
    private static async Task<Setup> Start(Action<FakeFisco>? setup = null, string instanceConfig = "{}", bool productionEnabled = false)
    {
        var w = await WebHarness.Start(settings: new() { ["Fiscal:ProductionEnabled"] = productionEnabled ? "true" : "false" });
        var fisco = new FakeFisco();
        setup?.Invoke(fisco);
        var module = await TestModule.Start(fisco.Definition(), w.Services, instanceConfig);
        var planId = await w.Query(async db =>
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
            return await db.Plans.Where(p => p.ProductId == w.ProductId).Select(p => p.Id).SingleAsync();
        });
        return new Setup(w, module, fisco, await w.Key("fiscal:read", "fiscal:write"), planId);
    }

    /// <summary>Una emisión frenada en el tope de intentos inciertos, con el número 1 propuesto y el trabajo muerto.</summary>
    private static async Task<long> Stuck(Setup s)
    {
        s.Fisco.Failures = FiscalEmissionHandler.MaxAttempts;
        var invoice = await s.Order();
        await s.W.RunJobs();
        var clock = (TestClock)s.W.Services.GetRequiredService<TimeProvider>();
        for (var i = 0; i < FiscalEmissionHandler.MaxAttempts; i++)
        {
            clock.Now = clock.Now.AddSeconds(61);
            await s.W.RunJobs();
        }

        var emission = await s.Emission(invoice);
        Assert.AreEqual(EmissionStatus.Pending, emission.Status);
        Assert.AreEqual(1L, emission.ProposedNumber);
        return invoice;
    }

    [TestMethod]
    public async Task SinPermisoFiscal_Da403()
    {
        await using var s = await Start();
        var invoice = await s.Order();
        var other = await s.W.Key("documents:read", "documents:write");

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, $"/api/v1/fiscal/emissions/{invoice}", null),
            (HttpMethod.Post, $"/api/v1/fiscal/emissions/{invoice}/adopt", new { authorization_code = "CAE1" }),
            (HttpMethod.Post, $"/api/v1/fiscal/emissions/{invoice}/retry", null),
            (HttpMethod.Get, "/api/v1/fiscal/series", null),
            (HttpMethod.Post, "/api/v1/fiscal/series/3/6", new { last_number = 5, reason = "prueba" }),
        })
        {
            using var response = await s.Send(method, path, body, other);
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, path);
        }

        Assert.AreEqual(0, s.Fisco.Calls);
    }

    [TestMethod]
    public async Task Adoptar_ConElCodigoDelFisco_RegistraElComprobanteYAudita()
    {
        await using var s = await Start();
        var invoice = await Stuck(s);

        // El último pedido llegó tarde al fisco: el comprobante 1 existe y es el de esta factura.
        s.Fisco.Register(s.Fisco.Authorizations.Last()["document"]!.AsObject());

        var view = await s.Json(HttpMethod.Get, $"/api/v1/fiscal/emissions/{invoice}", HttpStatusCode.OK);
        Assert.AreEqual("pending", view["status"]!.GetValue<string>());
        Assert.AreEqual(1, view["proposed_number"]!.GetValue<long>());
        Assert.IsFalse(view["job_alive"]!.GetValue<bool>());
        Assert.AreEqual("CAE1", view["found"]!["authorization_code"]!.GetValue<string>());
        Assert.IsTrue(view["found"]!["matches"]!.GetValue<bool>());
        Assert.IsFalse(view["found"]!["taken"]!.GetValue<bool>());

        // La confirmación es el código que el fisco informa: otro no vale.
        await s.Json(HttpMethod.Post, $"/api/v1/fiscal/emissions/{invoice}/adopt", HttpStatusCode.UnprocessableEntity, new { authorization_code = "CAE9" });
        var adopted = await s.Json(HttpMethod.Post, $"/api/v1/fiscal/emissions/{invoice}/adopt", HttpStatusCode.OK, new { authorization_code = "CAE1" });
        Assert.AreEqual(1, adopted["number"]!.GetValue<long>());

        var fiscal = await s.W.Query(db => db.FiscalDocuments.SingleAsync());
        Assert.AreEqual(invoice, fiscal.DocumentId);
        Assert.AreEqual("CAE1", fiscal.AuthorizationCode);
        Assert.AreEqual(EmissionStatus.Authorized, (await s.Emission(invoice)).Status);
        var series = await s.W.Query(db => db.FiscalSeries.SingleAsync());
        Assert.AreEqual(1L, series.LastNumber);
        Assert.IsNull(series.SyncedAt);
        var audit = (await s.Audit("fiscal.adopt")).Single();
        Assert.AreEqual($"document:{invoice}", audit.Entity);
        Assert.Contains("\"number\": 1", audit.Details!);
        var notified = await s.W.Query(db => db.Jobs.Where(j => j.Kind == JobKinds.NotifyEvent).Select(j => j.Payload).ToListAsync());
        Assert.HasCount(1, notified.Where(p => p.Contains(Events.FiscalDocumentAuthorized, StringComparison.Ordinal)));

        // Ya adoptado no se adopta dos veces, y nada viajó por authorize.
        await s.Json(HttpMethod.Post, $"/api/v1/fiscal/emissions/{invoice}/adopt", HttpStatusCode.Conflict, new { authorization_code = "CAE1" });
        Assert.HasCount(FiscalEmissionHandler.MaxAttempts, s.Fisco.Authorizations);
    }

    [TestMethod]
    public async Task Adoptar_SinComprobanteEnElFisco_OEnEstadoEquivocado_Da409()
    {
        await using var s = await Start();
        var invoice = await Stuck(s);

        // El fisco no tiene nada con ese número: no hay qué adoptar.
        var missing = await s.Json(HttpMethod.Post, $"/api/v1/fiscal/emissions/{invoice}/adopt", HttpStatusCode.Conflict, new { authorization_code = "CAE1" });
        StringAssert.Contains(missing["title"]!.GetValue<string>(), "no hay nada que adoptar");

        // Una rechazada nunca se autorizó: tampoco se adopta.
        s.Fisco.Failures = 0;
        s.Fisco.Reject = true;
        var rejected = await s.Order();
        await s.W.RunJobs();
        Assert.AreEqual(EmissionStatus.Rejected, (await s.Emission(rejected)).Status);
        var wrong = await s.Json(HttpMethod.Post, $"/api/v1/fiscal/emissions/{rejected}/adopt", HttpStatusCode.Conflict, new { authorization_code = "CAE1" });
        StringAssert.Contains(wrong["title"]!.GetValue<string>(), "Rejected");

        // Un documento sin emisión no existe para esta API.
        await s.Json(HttpMethod.Get, "/api/v1/fiscal/emissions/999999", HttpStatusCode.NotFound);
        Assert.AreEqual(0, await s.W.Query(db => db.FiscalDocuments.CountAsync()));
    }

    [TestMethod]
    public async Task Reintentar_UnaRechazada_VuelveAPendienteYEmite()
    {
        await using var s = await Start(f => f.Reject = true);
        var invoice = await s.Order();
        await s.W.RunJobs();
        Assert.AreEqual(EmissionStatus.Rejected, (await s.Emission(invoice)).Status);

        // El staff corrigió la causa y reintenta: la emisión vuelve a pendiente, se encola y sale.
        s.Fisco.Reject = false;
        var retried = await s.Json(HttpMethod.Post, $"/api/v1/fiscal/emissions/{invoice}/retry", HttpStatusCode.Accepted);
        Assert.AreEqual("pending", retried["status"]!.GetValue<string>());
        Assert.AreEqual(EmissionStatus.Pending, (await s.Emission(invoice)).Status);

        // Con el trabajo encolado no se reintenta dos veces.
        await s.Json(HttpMethod.Post, $"/api/v1/fiscal/emissions/{invoice}/retry", HttpStatusCode.Conflict);

        await s.W.RunJobs();
        Assert.AreEqual(EmissionStatus.Authorized, (await s.Emission(invoice)).Status);
        Assert.AreEqual(1, s.Fisco.Authorized);
        var audit = (await s.Audit("fiscal.retry")).Single();
        Assert.Contains("Rejected", audit.Details!);

        // Autorizada no se reintenta.
        await s.Json(HttpMethod.Post, $"/api/v1/fiscal/emissions/{invoice}/retry", HttpStatusCode.Conflict);
    }

    [TestMethod]
    public async Task AjustarLaSerie_ConMotivo_YLaProximaEmisionVuelveAConsultar()
    {
        await using var s = await Start();
        var first = await s.Order();
        await s.W.RunJobs();
        Assert.AreEqual(EmissionStatus.Authorized, (await s.Emission(first)).Status);

        var listed = await s.Json(HttpMethod.Get, "/api/v1/fiscal/series", HttpStatusCode.OK);
        Assert.AreEqual(1, listed.AsArray().Single()!["last_number"]!.GetValue<long>());
        Assert.IsNotNull(listed.AsArray().Single()!["synced_at"]);

        // Sin motivo no hay ajuste, y una serie que no existe no se inventa.
        await s.Json(HttpMethod.Post, "/api/v1/fiscal/series/3/6", HttpStatusCode.UnprocessableEntity, new { last_number = 3, reason = " " });
        await s.Json(HttpMethod.Post, "/api/v1/fiscal/series/3/8", HttpStatusCode.NotFound, new { last_number = 3, reason = "no existe" });

        // Otro sistema emitió el 2 y el 3: el staff lleva la serie al 3 con el motivo, y la marca de sincronía se borra.
        var adjusted = await s.Json(HttpMethod.Post, "/api/v1/fiscal/series/3/6", HttpStatusCode.OK, new { last_number = 3, reason = "Se emitieron el 2 y el 3 desde el sistema anterior" });
        Assert.AreEqual(3, adjusted["last_number"]!.GetValue<long>());
        Assert.IsNull(adjusted["synced_at"]);
        var audit = (await s.Audit("fiscal.series.adjust")).Single();
        Assert.Contains("sistema anterior", audit.Details!);
        Assert.Contains("\"from\": 1", audit.Details!);

        // La siguiente consulta el último número (el fisco tiene 3), coincide y emite el 4.
        s.Fisco.Offset = 2;
        var calls = s.Fisco.Calls;
        var next = await s.Order();
        await s.W.RunJobs();
        Assert.AreEqual(EmissionStatus.Authorized, (await s.Emission(next)).Status);
        Assert.AreEqual(4L, await s.W.Query(db => db.FiscalDocuments.Where(f => f.DocumentId == next).Select(f => f.Number).SingleAsync()));
        Assert.AreEqual(calls + 2, s.Fisco.Calls);
    }

    [TestMethod]
    public async Task AjustarLaSerie_ConEmisionesEnCurso_OInciertas_Da409()
    {
        await using var s = await Start();
        var first = await s.Order();
        await s.W.RunJobs();
        Assert.AreEqual(EmissionStatus.Authorized, (await s.Emission(first)).Status);

        // El trabajo de la segunda emisión está encolado y todavía no corrió: la serie tiene una emisión en curso.
        var invoice = await s.Order();
        var busy = await s.Json(HttpMethod.Post, "/api/v1/fiscal/series/3/6", HttpStatusCode.Conflict, new { last_number = 3, reason = "prueba" });
        StringAssert.Contains(busy["title"]!.GetValue<string>(), "en curso");
        Assert.IsTrue((await s.Json(HttpMethod.Get, "/api/v1/fiscal/series", HttpStatusCode.OK)).AsArray().Single()!["emitting"]!.GetValue<bool>());

        // Un resultado incierto a la espera de la consulta también es una emisión en curso.
        s.Fisco.CutOnce = true;
        await s.W.RunJobs();
        Assert.AreEqual(EmissionStatus.Unknown, (await s.Emission(invoice)).Status);
        await s.Json(HttpMethod.Post, "/api/v1/fiscal/series/3/6", HttpStatusCode.Conflict, new { last_number = 3, reason = "prueba" });

        // Si el trabajo murió con la emisión incierta, primero se adopta o se reintenta.
        await s.W.Query(db => db.Jobs.Where(j => j.Kind == JobKinds.FiscalEmit && j.Status == JobStatus.Pending).ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, JobStatus.Dead)));
        var uncertain = await s.Json(HttpMethod.Post, "/api/v1/fiscal/series/3/6", HttpStatusCode.Conflict, new { last_number = 3, reason = "prueba" });
        StringAssert.Contains(uncertain["title"]!.GetValue<string>(), "incierto");
        Assert.AreEqual(1L, (await s.W.Query(db => db.FiscalSeries.SingleAsync())).LastNumber);
        Assert.IsEmpty(await s.Audit("fiscal.series.adjust"));
    }

    [TestMethod]
    public async Task TrabaDeProduccion_AdoptarYReintentarNoEmiten()
    {
        // Sin Fiscal:ProductionEnabled el núcleo no llama a la instancia de producción ni para consultar ni para adoptar.
        await using (var s = await Start(instanceConfig: ProductionInstance))
        {
            var invoice = await s.Order();
            await s.W.RunJobs();
            Assert.AreEqual(FiscalEmissionHandler.ProductionDisabled, (await s.Emission(invoice)).LastError);

            var adopt = await s.Json(HttpMethod.Post, $"/api/v1/fiscal/emissions/{invoice}/adopt", HttpStatusCode.Conflict, new { authorization_code = "CAE1" });
            StringAssert.Contains(adopt["title"]!.GetValue<string>(), "Fiscal:ProductionEnabled");
            var retry = await s.Json(HttpMethod.Post, $"/api/v1/fiscal/emissions/{invoice}/retry", HttpStatusCode.Conflict);
            StringAssert.Contains(retry["title"]!.GetValue<string>(), "Fiscal:ProductionEnabled");
            await s.W.RunJobs();
            Assert.AreEqual(0, s.Fisco.Calls);
            Assert.AreEqual(0, await s.W.Query(db => db.FiscalDocuments.CountAsync()));
        }

        // Con la traba del núcleo abierta y la del módulo cerrada, el módulo rechaza la consulta y la emisión, y nada se autoriza.
        await using (var s = await Start(instanceConfig: ProductionInstance, productionEnabled: true))
        {
            var invoice = await Stuck(s);
            s.Fisco.AllowProduction = false;

            var adopt = await s.Json(HttpMethod.Post, $"/api/v1/fiscal/emissions/{invoice}/adopt", HttpStatusCode.BadGateway, new { authorization_code = "CAE1" });
            StringAssert.Contains(adopt["title"]!.GetValue<string>(), "invalid_config");
            await s.Json(HttpMethod.Post, $"/api/v1/fiscal/emissions/{invoice}/retry", HttpStatusCode.Accepted);
            await s.W.RunJobs();

            Assert.AreEqual(EmissionStatus.Rejected, (await s.Emission(invoice)).Status);
            Assert.AreEqual(0, s.Fisco.Authorized);
            Assert.HasCount(FiscalEmissionHandler.MaxAttempts, s.Fisco.Authorizations);
            Assert.AreEqual(0, await s.W.Query(db => db.FiscalDocuments.CountAsync()));
        }
    }
}
