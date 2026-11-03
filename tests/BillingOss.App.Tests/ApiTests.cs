using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using BillingOss.Web;
using BillingOss.Web.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BillingOss.App.Tests;

/// <summary>El host web completo sobre una base de prueba, con TestServer.</summary>
internal sealed class WebHarness : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly Func<Task> drop;

    private WebHarness(WebApplication app, Func<Task> drop, long productId, long accountId, HttpClient client, string connectionString)
    {
        this.app = app;
        this.drop = drop;
        ProductId = productId;
        AccountId = accountId;
        Client = client;
        ConnectionString = connectionString;
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => app.Services;

    public string ConnectionString { get; }

    public long ProductId { get; }

    public long AccountId { get; }

    public TestServer Server => app.GetTestServer();

    /// <summary>
    /// Con kestrel, el servidor real en un puerto libre: TestServer no aplica el tope de tamaño del cuerpo. La llave de
    /// los secretos se genera en cada corrida en un archivo temporal que se borra al terminar: ningún rol arranca sin ella.
    /// </summary>
    public static async Task<WebHarness> Start(bool compat = false, bool kestrel = false, string environment = "Production", Dictionary<string, string?>? settings = null, Action<WebApplicationBuilder>? configure = null, bool pooling = true)
    {
        var (connectionString, dropDatabase) = await TestDatabase.Create(pooling, TestDatabase.Template);
        var keyFile = Path.Combine(Path.GetTempPath(), $"llaves-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(keyFile, SecretBox.NewKeyLine(1));

        Func<Task> drop = async () =>
        {
            await dropDatabase();
            File.Delete(keyFile);
        };
        WebApplication? app = null;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
            if (kestrel)
            {
                builder.WebHost.UseUrls("http://127.0.0.1:0");
            }
            else
            {
                builder.WebHost.UseTestServer();
            }

            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Main"] = connectionString,
                ["Role"] = "web",
                ["CompatApi:Enabled"] = compat ? "true" : "false",
                ["CompatApi:Currencies:1"] = "ARS",
                ["Secrets:KeyFile"] = keyFile,

                // Ninguna prueba consulta registros reales: el RDAP queda apagado y, si una lo prende, va al falso.
                ["Billing:DomainLookup:Enabled"] = "false",
                ["Billing:DomainLookup:BootstrapUrl"] = FakeRdap.Bootstrap,
                ["Billing:DomainLookup:DnsResolvers:0"] = "127.0.0.1:9",
            });
            builder.Configuration.AddInMemoryCollection(settings ?? []);
            builder.Services.AddSingleton(new App.UseCases.RdapHandler(new FakeRdap()));
            builder.Services.AddSingleton<TimeProvider>(new TestClock(new DateTimeOffset(2026, 11, 2, 15, 0, 0, TimeSpan.Zero)));
            configure?.Invoke(builder);
            app = AppHost.Build(builder);
            await AppHost.Migrate(app);
            await app.StartAsync();

            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            var (planId, _, accountId) = await CoreHarness.SeedCatalog(db, DateTimeOffset.UtcNow);
            var productId = await db.Plans.Where(p => p.Id == planId).Select(p => p.ProductId).SingleAsync();

            // Con Expect: 100-continue el cliente no manda un cuerpo que el servidor rechaza, así el cierre de la conexión
            // no pisa la respuesta.
            var client = kestrel
                ? new HttpClient(new SocketsHttpHandler { Expect100ContinueTimeout = TimeSpan.FromMinutes(1) }) { BaseAddress = new Uri(app.Urls.First()), DefaultRequestHeaders = { ExpectContinue = true } }
                : app.GetTestClient();
            return new WebHarness(app, drop, productId, accountId, client, connectionString);
        }
        catch
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }

            await drop();
            throw;
        }
    }

    public Task<string> Key(params string[] scopes) => LimitedKey(null, scopes);

    public async Task<string> LimitedKey(int? requestsPerMinute, params string[] scopes)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ApiKeyService>().Issue("prueba", scopes, requestsPerMinute: requestsPerMinute)).Token;
    }

    public async Task<string> KeyFrom(string[] allowedIps, params string[] scopes)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ApiKeyService>().Issue("prueba", scopes, allowedIps)).Token;
    }

    public Task<int> RunJobs() => app.Services.GetRequiredService<JobRunner>().RunUntilIdle(CancellationToken.None);

    public TestClock Clock => (TestClock)app.Services.GetRequiredService<TimeProvider>();

    public async Task<T> Use<TService, T>(Func<TService, Task<T>> work)
        where TService : notnull
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public Task<T> Query<T>(Func<BillingDbContext, Task<T>> work) => Use(work);

    /// <summary>Un servicio activo de la cuenta de prueba: pedido pagado y aprovisionado por el núcleo (producto sin módulo).</summary>
    public async Task<long> ActiveService()
    {
        var planId = await Query(db => db.Plans.Where(p => p.ProductId == ProductId).Select(p => p.Id).SingleAsync());
        var placed = await Use<UseCases.OrderService, UseCases.PlacedOrder>(s => s.PlaceOrder(AccountId, [new UseCases.OrderItem(planId)]));
        await Pay("T-1");
        await RunJobs();
        return placed.ServiceIds.Single();
    }

    /// <summary>Un pago aprobado por el importe de la renovación mensual, imputado a lo más viejo que falte pagar.</summary>
    public Task<long> Pay(string externalId) => Use<UseCases.PaymentService, long>(s =>
        s.Record(new UseCases.PaymentInput(AccountId, "manual:transferencia", externalId, Currency.Ars, 12100m, PaymentStatus.Approved)));

    public Task Db(Func<BillingDbContext, Task> work) => Query(async db =>
    {
        await work(db);
        return true;
    });

    public async Task<JsonNode> Compat(string token, Dictionary<string, string> fields) => (await CompatWithStatus(token, fields)).Body;

    public async Task<(HttpStatusCode Status, JsonNode Body)> CompatWithStatus(string token, Dictionary<string, string> fields)
    {
        var parts = token.Split('.', 2);
        fields["identifier"] = parts[0];
        fields["secret"] = parts[1];
        fields["responsetype"] = "json";
        using var content = new FormUrlEncodedContent(fields);
        using var response = await Client.PostAsync(new Uri("/includes/api.php", UriKind.Relative), content);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
        await drop();
    }
}

[TestClass]
public sealed class ApiTests
{
    private static HttpRequestMessage Request(HttpMethod method, string path, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    [TestMethod]
    public async Task ApiPropia_ExigeLlaveYPermisos()
    {
        await using var w = await WebHarness.Start();
        var reader = await w.Key("accounts:read");
        var writer = await w.Key("accounts:write", "accounts:read");
        var account = new { name = "Otra cuenta", email = "otra@example.com", currency = "ARS" };

        using var anonymous = await w.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/accounts", null, account));
        using var forbidden = await w.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/accounts", reader, account));
        using var created = await w.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/accounts", writer, account));
        using var bad = await w.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/accounts", writer, new { name = "x", email = "no-es-correo", currency = "ARS" }));

        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
        using var read = await w.Client.SendAsync(Request(HttpMethod.Get, $"/api/v1/accounts/{id}", reader));
        Assert.AreEqual("otra@example.com", JsonNode.Parse(await read.Content.ReadAsStringAsync())!["email"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ApiPropia_PedidoConIdempotencyKey_NoDuplica()
    {
        await using var w = await WebHarness.Start();
        var token = await w.Key("orders:write");
        var planId = await w.Query(db => db.Plans.Where(p => p.ProductId == w.ProductId).Select(p => p.Id).SingleAsync());
        var body = new { account_id = w.AccountId, items = new[] { new { plan_id = planId, quantity = 1 } } };

        async Task<long> Post()
        {
            using var request = Request(HttpMethod.Post, "/api/v1/orders", token, body);
            request.Headers.Add("Idempotency-Key", "pedido-ml-123");
            using var response = await w.Client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["order_id"]!.GetValue<long>();
        }

        Assert.AreEqual(await Post(), await Post());
        Assert.AreEqual(1, await w.Query(db => db.Orders.CountAsync()));
    }

    [TestMethod]
    public async Task ApiPropia_ExtractoDelBanco_SeImportaUnaSolaVez()
    {
        await using var w = await WebHarness.Start();
        var token = await w.Key("payments:write");
        long bank;
        await using (var scope = w.Services.CreateAsyncScope())
        {
            bank = await scope.ServiceProvider.GetRequiredService<UseCases.TransferService>().SaveAccount(null, new UseCases.TransferAccountInput("Banco de prueba", "Ejemplo SRL", "30712345678", "0170099220000012345678", null, "ARS"));
        }

        async Task<(HttpStatusCode Status, JsonNode? Body)> Post(long account)
        {
            using var request = Request(HttpMethod.Post, $"/api/v1/transfer-accounts/{account}/statements", token);
            request.Content = new StringContent("fecha,importe,referencia,cuit,ordenante,concepto\n02/11/2026,\"500,00\",TRF-1,27999888777,Alguien,VAR\n", Encoding.UTF8, "text/csv");
            using var response = await w.Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
        }

        var first = await Post(bank);
        var second = await Post(bank);

        Assert.AreEqual(HttpStatusCode.OK, first.Status);
        Assert.AreEqual(1, first.Body!["created"]!.GetValue<int>());
        Assert.AreEqual(0, second.Body!["created"]!.GetValue<int>());
        Assert.AreEqual(1, second.Body!["existing"]!.GetValue<int>(), "La API dice cuántos ya estaban.");
        Assert.AreEqual(1, await w.Query(db => db.BankMovements.CountAsync()));
        Assert.AreEqual(HttpStatusCode.NotFound, (await Post(999_999)).Status);
    }

    [TestMethod]
    public async Task ApiPropia_IdempotencyKey_ConOtroCuerpoDa422_YDesdeOtraLlaveEsOtroPedido()
    {
        await using var w = await WebHarness.Start();
        var token = await w.Key("orders:write");
        var other = await w.Key("orders:write");
        var plans = await w.Query(db => db.Plans.OrderBy(p => p.Id).Select(p => new { p.Id, p.ProductId }).ToListAsync());
        var planId = plans.Single(p => p.ProductId == w.ProductId).Id;
        var otherPlanId = plans.Single(p => p.ProductId != w.ProductId).Id;

        async Task<HttpResponseMessage> Post(string key, long plan)
        {
            // El mismo pedido, escrito distinto: el orden de las propiedades y el formato no cambian la huella.
            using var request = Request(HttpMethod.Post, "/api/v1/orders", key);
            request.Content = new StringContent(plan == planId
                ? $$"""{ "items": [ { "plan_id": {{plan}}, "quantity": 1 } ], "account_id": {{w.AccountId}} }"""
                : $$"""{"account_id":{{w.AccountId}},"items":[{"plan_id":{{plan}}}]}""", Encoding.UTF8, "application/json");
            request.Headers.Add("Idempotency-Key", "pedido-7");
            return await w.Client.SendAsync(request);
        }

        using var first = await Post(token, planId);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync());
        using var again = await Post(token, planId);
        Assert.AreEqual(HttpStatusCode.Created, again.StatusCode);
        Assert.AreEqual(await first.Content.ReadAsStringAsync(), await again.Content.ReadAsStringAsync());

        using var changed = await Post(token, otherPlanId);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, changed.StatusCode);
        Assert.Contains("Idempotency-Key", await changed.Content.ReadAsStringAsync());

        using var fromOther = await Post(other, planId);
        Assert.AreEqual(HttpStatusCode.Created, fromOther.StatusCode, await fromOther.Content.ReadAsStringAsync());
        Assert.AreNotEqual(await first.Content.ReadAsStringAsync(), await fromOther.Content.ReadAsStringAsync());

        var orders = await w.Query(db => db.Orders.OrderBy(o => o.Id).ToListAsync());
        Assert.HasCount(2, orders);
        Assert.IsTrue(orders.All(o => o.ClientReference == "pedido-7" && o.ApiKeyId is not null && o.RequestFingerprint?.Length == 64), "Huella y llave guardadas en el pedido.");
        Assert.AreEqual(orders[0].RequestFingerprint, orders[1].RequestFingerprint);
        Assert.AreNotEqual(orders[0].ApiKeyId, orders[1].ApiKeyId);
    }

    [TestMethod]
    public async Task ApiPropia_SuspensionManual_GuardaElMotivo_YElPagoNoLaLevanta()
    {
        await using var w = await WebHarness.Start();
        var id = await w.ActiveService();
        var token = await w.Key("services:write", "services:read");

        // Reactivar lo que está activo no encola nada: se rechaza antes.
        using var notSuspended = await w.Client.SendAsync(Request(HttpMethod.Post, $"/api/v1/services/{id}/unsuspend", token));
        Assert.AreEqual(HttpStatusCode.Conflict, notSuspended.StatusCode);

        using var suspended = await w.Client.SendAsync(Request(HttpMethod.Post, $"/api/v1/services/{id}/suspend", token, new { reason = "Abuso de recursos" }));
        Assert.AreEqual(HttpStatusCode.Accepted, suspended.StatusCode, await suspended.Content.ReadAsStringAsync());
        await w.RunJobs();
        var service = await w.Query(db => db.Services.SingleAsync(s => s.Id == id));
        Assert.AreEqual(ServiceStatus.Suspended, service.Status);
        Assert.AreEqual("Abuso de recursos", service.SuspendReason);
        using var listed = await w.Client.SendAsync(Request(HttpMethod.Get, $"/api/v1/accounts/{w.AccountId}/services", token));
        Assert.AreEqual("Abuso de recursos", JsonNode.Parse(await listed.Content.ReadAsStringAsync())![0]!["suspend_reason"]!.GetValue<string>());

        // Se factura y se paga la renovación: la suspensión propia queda, solo la de mora se levanta con el pago.
        w.Clock.Now = w.Clock.Now.AddDays(25);
        Assert.AreEqual(1, await w.Use<UseCases.BillingRunService, int>(s => s.Run()));
        await w.Pay("T-2");
        await w.RunJobs();
        Assert.AreEqual(ServiceStatus.Suspended, (await w.Query(db => db.Services.SingleAsync(s => s.Id == id))).Status);
        Assert.AreEqual(InvoiceStatus.Paid, (await w.Query(db => db.Documents.SingleAsync(d => d.Reason == "renewal"))).Status);

        // Sin motivo queda el genérico, y reactivar lo borra.
        using var unsuspended = await w.Client.SendAsync(Request(HttpMethod.Post, $"/api/v1/services/{id}/unsuspend", token));
        Assert.AreEqual(HttpStatusCode.Accepted, unsuspended.StatusCode);
        await w.RunJobs();
        using var plain = await w.Client.SendAsync(Request(HttpMethod.Post, $"/api/v1/services/{id}/suspend", token));
        Assert.AreEqual(HttpStatusCode.Accepted, plain.StatusCode, await plain.Content.ReadAsStringAsync());
        await w.RunJobs();
        service = await w.Query(db => db.Services.SingleAsync(s => s.Id == id));
        Assert.AreEqual(ServiceStatus.Suspended, service.Status);
        Assert.AreEqual(UseCases.ServiceAdmin.ManualSuspension, service.SuspendReason);
    }

    [TestMethod]
    public async Task ApiPropia_BajaAFinDePeriodo_YSuAnulacion()
    {
        await using var w = await WebHarness.Start();
        var id = await w.ActiveService();
        var token = await w.Key("services:write", "services:read");

        using var past = await w.Client.SendAsync(Request(HttpMethod.Post, $"/api/v1/services/{id}/cancel", token, new { at = "2026-10-01" }));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, past.StatusCode);

        // Sin fecha, la baja queda para el próximo vencimiento.
        using var cancel = await w.Client.SendAsync(Request(HttpMethod.Post, $"/api/v1/services/{id}/cancel", token));
        Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode, await cancel.Content.ReadAsStringAsync());
        Assert.AreEqual("2026-12-02", JsonNode.Parse(await cancel.Content.ReadAsStringAsync())!["cancel_at"]!.GetValue<string>());
        using var listed = await w.Client.SendAsync(Request(HttpMethod.Get, $"/api/v1/accounts/{w.AccountId}/services", token));
        Assert.AreEqual("2026-12-02", JsonNode.Parse(await listed.Content.ReadAsStringAsync())![0]!["cancel_at"]!.GetValue<string>());
        w.Clock.Now = w.Clock.Now.AddDays(25);
        Assert.AreEqual(0, await w.Use<UseCases.BillingRunService, int>(s => s.Run()));

        using var keep = await w.Client.SendAsync(Request(HttpMethod.Delete, $"/api/v1/services/{id}/cancel", token));
        Assert.AreEqual(HttpStatusCode.NoContent, keep.StatusCode);
        Assert.IsNull((await w.Query(db => db.Services.SingleAsync(s => s.Id == id))).CancelAt);
        Assert.AreEqual(1, await w.Use<UseCases.BillingRunService, int>(s => s.Run()));

        // Con fecha propia, posterior al vencimiento ya facturado.
        using var later = await w.Client.SendAsync(Request(HttpMethod.Post, $"/api/v1/services/{id}/cancel", token, new { at = "2027-01-15" }));
        Assert.AreEqual(HttpStatusCode.OK, later.StatusCode);
        Assert.AreEqual(new DateOnly(2027, 1, 15), (await w.Query(db => db.Services.SingleAsync(s => s.Id == id))).CancelAt);

        // Llega la fecha y la mora encola la baja. Conservar el servicio antes de que corra la deja sin efecto.
        w.Clock.Now = new DateTimeOffset(2027, 1, 15, 15, 0, 0, TimeSpan.Zero);
        await w.Use<UseCases.DunningService, int>(s => s.Run());
        Assert.AreEqual(1, await w.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.ServiceOperation && j.DedupKey!.EndsWith(":cancel-at") && j.Status == JobStatus.Pending)));
        using var kept = await w.Client.SendAsync(Request(HttpMethod.Delete, $"/api/v1/services/{id}/cancel", token));
        Assert.AreEqual(HttpStatusCode.NoContent, kept.StatusCode);
        await w.RunJobs();
        var service = await w.Query(db => db.Services.SingleAsync(s => s.Id == id));
        Assert.AreEqual(ServiceStatus.Active, service.Status);
        Assert.IsNull(service.CancelAt);
    }

    [TestMethod]
    public async Task ApiPropia_ExencionDeLaSuspensionAutomatica()
    {
        await using var w = await WebHarness.Start();
        var id = await w.ActiveService();
        var token = await w.Key("services:write", "services:read");

        using var exempt = await w.Client.SendAsync(Request(HttpMethod.Patch, $"/api/v1/services/{id}", token, new { no_auto_suspend = true }));
        Assert.AreEqual(HttpStatusCode.NoContent, exempt.StatusCode, await exempt.Content.ReadAsStringAsync());
        Assert.IsTrue((await w.Query(db => db.Services.SingleAsync(s => s.Id == id))).NoAutoSuspend);
        using var listed = await w.Client.SendAsync(Request(HttpMethod.Get, $"/api/v1/accounts/{w.AccountId}/services", token));
        Assert.IsTrue(JsonNode.Parse(await listed.Content.ReadAsStringAsync())![0]!["no_auto_suspend"]!.GetValue<bool>());

        // Lo que no viene no se toca.
        using var untouched = await w.Client.SendAsync(Request(HttpMethod.Patch, $"/api/v1/services/{id}", token, new { }));
        Assert.AreEqual(HttpStatusCode.NoContent, untouched.StatusCode);
        Assert.IsTrue((await w.Query(db => db.Services.SingleAsync(s => s.Id == id))).NoAutoSuspend);

        using var back = await w.Client.SendAsync(Request(HttpMethod.Patch, $"/api/v1/services/{id}", token, new { no_auto_suspend = false }));
        Assert.AreEqual(HttpStatusCode.NoContent, back.StatusCode);
        Assert.IsFalse((await w.Query(db => db.Services.SingleAsync(s => s.Id == id))).NoAutoSuspend);
    }

    [TestMethod]
    public async Task ApiCompatible_ModuleSuspend_GuardaElMotivo_YModuleUnsuspendLoLevanta()
    {
        await using var w = await WebHarness.Start(compat: true);
        var id = await w.ActiveService();
        var token = await w.Key("compat:ModuleSuspend", "compat:ModuleUnsuspend");

        // El servicio va en accountid, como lo llama WHMCS, o en serviceid.
        var suspended = await w.Compat(token, new() { ["action"] = "ModuleSuspend", ["accountid"] = id.ToString(CultureInfo.InvariantCulture), ["suspendreason"] = "Pedido del cliente" });
        Assert.AreEqual("success", suspended["result"]!.GetValue<string>(), suspended.ToJsonString());
        await w.RunJobs();
        var service = await w.Query(db => db.Services.SingleAsync(s => s.Id == id));
        Assert.AreEqual(ServiceStatus.Suspended, service.Status);
        Assert.AreEqual("Pedido del cliente", service.SuspendReason);

        var unsuspended = await w.Compat(token, new() { ["action"] = "ModuleUnsuspend", ["serviceid"] = id.ToString(CultureInfo.InvariantCulture) });
        Assert.AreEqual("success", unsuspended["result"]!.GetValue<string>(), unsuspended.ToJsonString());
        await w.RunJobs();
        service = await w.Query(db => db.Services.SingleAsync(s => s.Id == id));
        Assert.AreEqual(ServiceStatus.Active, service.Status);
        Assert.IsNull(service.SuspendReason);

        var missing = await w.Compat(token, new() { ["action"] = "ModuleSuspend", ["accountid"] = "999999" });
        Assert.AreEqual("error", missing["result"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ApiPropia_OperacionDeServicioQueElEstadoNoAdmite_Da409YNoEncolaNada()
    {
        await using var w = await WebHarness.Start();
        var token = await w.Key("orders:write", "payments:write", "services:write");
        var planId = await w.Query(db => db.Plans.Where(p => p.ProductId == w.ProductId).Select(p => p.Id).SingleAsync());
        using var order = await w.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/orders", token, new { account_id = w.AccountId, items = new[] { new { plan_id = planId } } }));
        Assert.AreEqual(HttpStatusCode.Created, order.StatusCode, await order.Content.ReadAsStringAsync());
        var placed = JsonNode.Parse(await order.Content.ReadAsStringAsync())!;
        var serviceId = placed["service_ids"]!.AsArray().Single()!.GetValue<long>();
        var invoiceId = placed["invoice_id"]!.GetValue<long>();

        async Task<HttpStatusCode> Operate(string operation)
        {
            using var response = await w.Client.SendAsync(Request(HttpMethod.Post, $"/api/v1/services/{serviceId}/{operation}", token));
            return response.StatusCode;
        }

        Task<ServiceStatus> Status() => w.Query(db => db.Services.Where(s => s.Id == serviceId).Select(s => s.Status).SingleAsync());

        // Pendiente de pago: nada de esto corresponde todavía, y no queda encolado delante del alta.
        Assert.AreEqual(HttpStatusCode.Conflict, await Operate("suspend"));
        Assert.AreEqual(HttpStatusCode.Conflict, await Operate("unsuspend"));
        Assert.AreEqual(HttpStatusCode.Conflict, await Operate("terminate"));
        Assert.AreEqual(0, await w.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.ServiceOperation)));

        using var paid = await w.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/payments", token, new { account_id = w.AccountId, method = "transferencia", external_id = "T-1", currency = "ARS", amount = 12100m, document_ids = new[] { invoiceId } }));
        Assert.AreEqual(HttpStatusCode.OK, paid.StatusCode, await paid.Content.ReadAsStringAsync());
        await w.RunJobs();
        Assert.AreEqual(ServiceStatus.Active, await Status());

        Assert.AreEqual(HttpStatusCode.Accepted, await Operate("suspend"));
        await w.RunJobs();
        Assert.AreEqual(ServiceStatus.Suspended, await Status());
        Assert.AreEqual(HttpStatusCode.Accepted, await Operate("unsuspend"));
        await w.RunJobs();
        Assert.AreEqual(ServiceStatus.Active, await Status());
        Assert.AreEqual(HttpStatusCode.Accepted, await Operate("terminate"));
        await w.RunJobs();
        Assert.AreEqual(ServiceStatus.Terminated, await Status());
        Assert.AreEqual(HttpStatusCode.Conflict, await Operate("unsuspend"));
    }

    [TestMethod]
    public async Task ApiPropia_SinLlave_ContestaAntesDeLeerElCuerpo()
    {
        await using var w = await WebHarness.Start();
        using var request = Request(HttpMethod.Post, "/api/v1/orders", null);
        request.Content = new StringContent("{esto no es json", Encoding.UTF8, "application/json");

        using var response = await w.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task ApiPropia_CuerpoMasGrandeQueElTope_DaPayloadTooLarge()
    {
        await using var w = await WebHarness.Start(kestrel: true);
        var token = await w.Key("orders:write");
        using var request = Request(HttpMethod.Post, "/api/v1/orders", token);
        request.Content = new StringContent(new string(' ', 2 * 1024 * 1024) + "{}", Encoding.UTF8, "application/json");

        using var response = await w.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task ApiCompatible_CuerpoMasGrandeQueElTope_DaPayloadTooLarge()
    {
        await using var w = await WebHarness.Start(compat: true, kestrel: true);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "GetInvoice", ["relleno"] = new string('a', 2 * 1024 * 1024) });

        using var response = await w.Client.PostAsync(new Uri("/includes/api.php", UriKind.Relative), content);

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode, text);
        Assert.AreEqual("error", JsonNode.Parse(text)!["result"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ApiCompatible_FormularioConDemasiadosCampos_DaErrorDeLaApi()
    {
        await using var w = await WebHarness.Start(compat: true);
        var fields = Enumerable.Range(0, 1100).ToDictionary(i => string.Create(CultureInfo.InvariantCulture, $"campo{i}"), _ => "x");
        fields["action"] = "GetClientsDetails";
        using var content = new FormUrlEncodedContent(fields);

        using var response = await w.Client.PostAsync(new Uri("/includes/api.php", UriKind.Relative), content);

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, text);
        Assert.AreEqual("error", JsonNode.Parse(text)!["result"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ApiPropiaYCompatible_TopeDePedidosPorLlave()
    {
        await using var w = await WebHarness.Start(compat: true);
        var own = await w.LimitedKey(2, "accounts:read");
        var compat = await w.LimitedKey(2, "compat:GetClientsDetails");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await w.Client.SendAsync(Request(HttpMethod.Get, $"/api/v1/accounts/{w.AccountId}", own));
            statuses.Add(response.StatusCode);
            statuses.Add((await w.CompatWithStatus(compat, new() { ["action"] = "GetClientsDetails", ["clientid"] = w.AccountId.ToString() })).Status);
        }

        CollectionAssert.AreEqual(
            new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests },
            statuses);
    }

    [TestMethod]
    public async Task ApiPropiaYCompatible_TopeDePedidosPorIp_ConElCuerpoDeErrorDeCadaApi()
    {
        await using var w = await WebHarness.Start(compat: true);
        var last = HttpStatusCode.OK;
        string? contentType = null;
        // X-Forwarded-For cambia en cada pedido: sin proxies configurados no se le cree y el tope llega igual.
        for (var i = 0; i < 3000 && last != HttpStatusCode.TooManyRequests; i++)
        {
            using var request = Request(HttpMethod.Get, $"/api/v1/accounts/{w.AccountId}", null);
            request.Headers.Add("X-Forwarded-For", string.Create(CultureInfo.InvariantCulture, $"203.0.113.{i % 250}"));
            using var response = await w.Client.SendAsync(request);
            last = response.StatusCode;
            contentType = response.Content.Headers.ContentType?.MediaType;
        }

        Assert.AreEqual(HttpStatusCode.TooManyRequests, last);
        Assert.AreEqual("application/problem+json", contentType);

        // La compatible comparte el cupo de la IP y responde con su propio cuerpo de error.
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "GetClientsDetails" });
        using var compat = await w.Client.PostAsync(new Uri("/includes/api.php", UriKind.Relative), form);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, compat.StatusCode);
        Assert.AreEqual("error", JsonNode.Parse(await compat.Content.ReadAsStringAsync())!["result"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ApiPropia_DetrasDeUnProxyConocido_ElTopeEsPorLaIpDelCliente()
    {
        // Una IP suelta y una red: el pedido llega desde 127.0.0.1, que entra en la red.
        await using var w = await WebHarness.Start(kestrel: true, settings: new()
        {
            ["ForwardedHeaders:KnownProxies:0"] = "192.0.2.1",
            ["ForwardedHeaders:KnownProxies:1"] = "127.0.0.0/8",
        });

        async Task<HttpStatusCode> From(string client)
        {
            using var request = Request(HttpMethod.Get, $"/api/v1/accounts/{w.AccountId}", null);
            request.Headers.Add("X-Forwarded-For", client);
            using var response = await w.Client.SendAsync(request);
            return response.StatusCode;
        }

        var last = HttpStatusCode.OK;
        for (var i = 0; i < 3000 && last != HttpStatusCode.TooManyRequests; i++)
        {
            last = await From("203.0.113.1");
        }

        Assert.AreEqual(HttpStatusCode.TooManyRequests, last);
        Assert.AreEqual(HttpStatusCode.Unauthorized, await From("203.0.113.2"));
    }

    [TestMethod]
    public async Task ApiKey_TopeDePedidosCeroONegativo_LaBaseLoRechaza()
    {
        await using var w = await WebHarness.Start();
        await w.Key("accounts:read");

        foreach (var limit in new[] { 0, -1 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => w.Query(db => db.ApiKeys.ExecuteUpdateAsync(s => s.SetProperty(k => k.RequestsPerMinute, limit))));
            Assert.AreEqual(PostgresErrorCodes.CheckViolation, ex.SqlState);
        }
    }

    [TestMethod]
    public async Task ApiPropia_PedidoConCantidadCeroONegativa_SeRechaza()
    {
        await using var w = await WebHarness.Start();
        var token = await w.Key("orders:write");
        var planId = await w.Query(db => db.Plans.Where(p => p.ProductId == w.ProductId).Select(p => p.Id).SingleAsync());
        var services = await w.Query(db => db.Services.CountAsync());

        foreach (var quantities in new[] { new[] { 0 }, new[] { 1, -1 }, new[] { 2, -2 } })
        {
            var body = new { account_id = w.AccountId, items = quantities.Select(q => new { plan_id = planId, quantity = q }).ToArray() };
            using var response = await w.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/orders", token, body));
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode, string.Join(',', quantities));
        }

        Assert.AreEqual(0, await w.Query(db => db.Orders.CountAsync()));
        Assert.AreEqual(services, await w.Query(db => db.Services.CountAsync()));
        Assert.AreEqual(0, await w.Query(db => db.Jobs.CountAsync()));
    }

    [TestMethod]
    public async Task ApiPropia_ErroresConTextoPropio_NuncaElDelFramework()
    {
        await using var w = await WebHarness.Start();
        var token = await w.Key("orders:write");

        using var noPlan = await w.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/orders", token, new { account_id = w.AccountId, items = new[] { new { plan_id = 999999 } } }));
        using var noItems = await w.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/orders", token, new { account_id = w.AccountId }));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, noPlan.StatusCode);
        Assert.AreEqual("No existe el plan 999999 o no está activo.", JsonNode.Parse(await noPlan.Content.ReadAsStringAsync())!["title"]!.GetValue<string>());
        Assert.AreEqual(HttpStatusCode.BadRequest, noItems.StatusCode);
        Assert.AreEqual("Un pedido sin líneas no se confirma.", JsonNode.Parse(await noItems.Content.ReadAsStringAsync())!["title"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ApiCompatible_DatoIlegible_DaTextoFijo()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key("compat:GetInvoice");

        var unreadable = await w.Compat(token, new() { ["action"] = "GetInvoice", ["invoiceid"] = "abc" });
        var missing = await w.Compat(token, new() { ["action"] = "GetInvoice", ["invoiceid"] = "999999" });

        Assert.AreEqual("Hay un dato con formato inválido.", unreadable["message"]!.GetValue<string>());
        Assert.AreEqual("No existe la factura.", missing["message"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ListaDeIp_MiraAlClienteSoloDetrasDeProxiesConocidos()
    {
        await using var w = await WebHarness.Start(settings: new()
        {
            ["ForwardedHeaders:KnownProxies:0"] = "10.20.30.5",
            ["ForwardedHeaders:KnownNetworks:0"] = "10.1.0.0/16",
        });
        var token = await w.KeyFrom(["203.0.113.7"], "accounts:read");

        async Task<int> Get(string from, string? forwardedFor)
        {
            var context = await w.Server.SendAsync(c =>
            {
                c.Connection.RemoteIpAddress = IPAddress.Parse(from);
                c.Request.Method = HttpMethods.Get;
                c.Request.Path = $"/api/v1/accounts/{w.AccountId}";
                c.Request.Headers.Authorization = $"Bearer {token}";
                if (forwardedFor is not null)
                {
                    c.Request.Headers["X-Forwarded-For"] = forwardedFor;
                }
            });
            return context.Response.StatusCode;
        }

        Assert.AreEqual(StatusCodes.Status200OK, await Get("10.20.30.5", "203.0.113.7"));
        Assert.AreEqual(StatusCodes.Status200OK, await Get("10.20.30.5", "198.51.100.1, 203.0.113.7, 10.1.2.3"));
        Assert.AreEqual(StatusCodes.Status401Unauthorized, await Get("10.20.30.5", "203.0.113.7, 198.51.100.1"));
        Assert.AreEqual(StatusCodes.Status401Unauthorized, await Get("198.51.100.9", "203.0.113.7"));
        Assert.AreEqual(StatusCodes.Status401Unauthorized, await Get("10.20.30.5", null));
    }

    [TestMethod]
    public async Task OpenApi_SoloEnDesarrollo()
    {
        await using (var production = await WebHarness.Start())
        {
            using var hidden = await production.Client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
            Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode);
        }

        await using var development = await WebHarness.Start(environment: "Development");
        using var shown = await development.Client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.OK, shown.StatusCode);
    }

    [TestMethod]
    public async Task ApiCompatible_ApagadaPorDefecto()
    {
        await using var w = await WebHarness.Start();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "GetClientsDetails" });
        using var response = await w.Client.PostAsync(new Uri("/includes/api.php", UriKind.Relative), content);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task ApiCompatible_FlujoDelBotDeMercadoLibre()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key("compat:AddClient", "compat:AddOrder", "compat:AddInvoicePayment", "compat:AcceptOrder",
            "compat:GetInvoice", "compat:GetClientsDetails", "compat:GetClientsProducts", "compat:UpdateClient");

        await w.Query(async db =>
        {
            await CatalogSeed.VpsOptions(db, w.ProductId);
            return true;
        });
        var client = await w.Compat(token, new() { ["action"] = "AddClient", ["firstname"] = "Ana", ["lastname"] = "Pérez", ["email"] = "ana@example.com", ["password2"] = "x", ["country"] = "AR", ["currency"] = "1", ["noemail"] = "true", ["skipvalidation"] = "true" });
        Assert.AreEqual("success", client["result"]!.GetValue<string>(), client.ToJsonString());
        var clientId = client["clientid"]!.GetValue<long>();

        var options = Convert.ToBase64String(Encoding.UTF8.GetBytes("a:1:{i:5;s:2:\"16\";}"));
        var order = await w.Compat(token, new() { ["action"] = "AddOrder", ["clientid"] = clientId.ToString(), ["pid[0]"] = w.ProductId.ToString(), ["billingcycle[0]"] = "monthly", ["paymentmethod"] = "mercadopago", ["configoptions[0]"] = options });
        Assert.AreEqual("success", order["result"]!.GetValue<string>(), order.ToJsonString());
        var invoiceId = order["invoiceid"]!.GetValue<long>();

        for (var i = 0; i < 2; i++)
        {
            var paid = await w.Compat(token, new() { ["action"] = "AddInvoicePayment", ["invoiceid"] = invoiceId.ToString(), ["transid"] = "ML-2000001", ["gateway"] = "mercadolibre", ["noemail"] = "true" });
            Assert.AreEqual("success", paid["result"]!.GetValue<string>(), paid.ToJsonString());
        }

        var accepted = await w.Compat(token, new() { ["action"] = "AcceptOrder", ["orderid"] = order["orderid"]!.GetValue<long>().ToString(), ["autosetup"] = "true", ["sendemail"] = "true" });
        Assert.AreEqual("success", accepted["result"]!.GetValue<string>());
        await w.RunJobs();

        var invoice = await w.Compat(token, new() { ["action"] = "GetInvoice", ["invoiceid"] = invoiceId.ToString() });
        Assert.AreEqual("Paid", invoice["status"]!.GetValue<string>());
        Assert.AreEqual(clientId, invoice["userid"]!.GetValue<long>());
        Assert.AreEqual(1, await w.Query(db => db.Payments.CountAsync()));

        var details = await w.Compat(token, new() { ["action"] = "GetClientsDetails", ["email"] = "ana@example.com" });
        Assert.AreEqual(clientId, details["client_id"]!.GetValue<long>());
        Assert.AreEqual("Pérez", details["lastname"]!.GetValue<string>());

        var products = await w.Compat(token, new() { ["action"] = "GetClientsProducts", ["clientid"] = clientId.ToString() });
        var product = products["products"]!["product"]!.AsArray().Single()!;
        Assert.AreEqual("Active", product["status"]!.GetValue<string>());
        Assert.AreEqual("monthly", product["billingcycle"]!.GetValue<string>());
        // configoptions llega con los IDs del sistema anterior: la RAM (5) con el valor de 2 GB (16).
        var line = await w.Query(db => db.Orders.Include(o => o.Lines).SelectMany(o => o.Lines).SingleAsync());
        Assert.AreEqual(12100m + 5000m, line.UnitPrice);
        Assert.AreEqual(CatalogSeed.Ram2, await w.Query(db => db.ServiceOptions.Where(o => o.OptionId == CatalogSeed.Ram).Select(o => o.ValueId).SingleAsync()));
        Assert.AreEqual("17100.00", product["recurringamount"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ApiCompatible_AddOrderAceptaCiclosDeDosYTresAnios()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key("compat:AddOrder", "compat:GetClientsProducts");
        await w.Query(async db =>
        {
            foreach (var years in new[] { 2, 3 })
            {
                var plan = new Plan(w.ProductId, new BillingCycle(CycleUnit.Year, years));
                db.Plans.Add(plan);
                await db.SaveChangesAsync();
                db.PlanPrices.Add(new PlanPrice(plan.Id, Currency.Ars, 100000m * years, 0m, new DateOnly(2026, 1, 1)));
            }

            return await db.SaveChangesAsync();
        });

        var biennial = await w.Compat(token, new() { ["action"] = "AddOrder", ["clientid"] = w.AccountId.ToString(), ["pid[0]"] = w.ProductId.ToString(), ["billingcycle[0]"] = "biennially" });
        var triennial = await w.Compat(token, new() { ["action"] = "AddOrder", ["clientid"] = w.AccountId.ToString(), ["pid[0]"] = w.ProductId.ToString(), ["billingcycle[0]"] = "triennially" });

        Assert.AreEqual("success", biennial["result"]!.GetValue<string>(), biennial.ToJsonString());
        Assert.AreEqual("success", triennial["result"]!.GetValue<string>(), triennial.ToJsonString());
        var products = await w.Compat(token, new() { ["action"] = "GetClientsProducts", ["clientid"] = w.AccountId.ToString() });
        var cycles = products["products"]!["product"]!.AsArray().Select(p => p!["billingcycle"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
        CollectionAssert.AreEqual(new[] { "biennially", "triennially" }, cycles);
    }

    [TestMethod]
    public async Task ApiCompatible_UpdateClientNoDejaDosCuentasConElMismoCorreo()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key("compat:AddClient", "compat:UpdateClient", "compat:GetClientsDetails");
        var added = await w.Compat(token, new() { ["action"] = "AddClient", ["firstname"] = "Otra", ["lastname"] = "Cuenta", ["email"] = "otra@example.com", ["currency"] = "1" });
        Assert.AreEqual("success", added["result"]!.GetValue<string>(), added.ToJsonString());

        var updated = await w.Compat(token, new() { ["action"] = "UpdateClient", ["clientid"] = added["clientid"]!.ToString(), ["email"] = "Cliente@example.com" });
        var found = await w.Compat(token, new() { ["action"] = "GetClientsDetails", ["email"] = "CLIENTE@example.com" });

        Assert.AreEqual("error", updated["result"]!.GetValue<string>(), updated.ToJsonString());
        Assert.AreEqual(w.AccountId, found["client_id"]!.GetValue<long>(), found.ToJsonString());
        Assert.AreEqual(1, await w.Query(db => db.Accounts.CountAsync(a => a.Email.ToLower() == "cliente@example.com")));
    }

    [TestMethod]
    public async Task ApiCompatible_PagoSinTransidOMayorQueElSaldo_DaError()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key("compat:AddOrder", "compat:AddInvoicePayment");
        async Task<string> Invoice()
        {
            var order = await w.Compat(token, new() { ["action"] = "AddOrder", ["clientid"] = w.AccountId.ToString(), ["pid[0]"] = w.ProductId.ToString(), ["billingcycle[0]"] = "monthly" });
            return order["invoiceid"]!.GetValue<long>().ToString();
        }

        var first = await Invoice();
        var second = await Invoice();

        // Sin transid, el segundo pago del mismo gateway chocaba con el primero y respondía success sin registrar nada.
        var noId = await w.Compat(token, new() { ["action"] = "AddInvoicePayment", ["invoiceid"] = first, ["gateway"] = "mercadolibre" });
        var noIdAgain = await w.Compat(token, new() { ["action"] = "AddInvoicePayment", ["invoiceid"] = second, ["gateway"] = "mercadolibre", ["transid"] = " " });
        var tooMuch = await w.Compat(token, new() { ["action"] = "AddInvoicePayment", ["invoiceid"] = first, ["gateway"] = "mercadolibre", ["transid"] = "ML-1", ["amount"] = "12100.01" });

        Assert.AreEqual("error", noId["result"]!.GetValue<string>(), noId.ToJsonString());
        Assert.AreEqual("error", noIdAgain["result"]!.GetValue<string>(), noIdAgain.ToJsonString());
        Assert.AreEqual("error", tooMuch["result"]!.GetValue<string>(), tooMuch.ToJsonString());
        Assert.AreEqual(0, await w.Query(db => db.Payments.CountAsync()));
    }

    [TestMethod]
    public async Task ApiCompatible_FacturaPagadaYReembolsada_SaleComoRefunded()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key("compat:AddOrder", "compat:AddInvoicePayment", "compat:GetInvoice");
        var staff = await w.Key("payments:refund", "payments:read");
        var order = await w.Compat(token, new() { ["action"] = "AddOrder", ["clientid"] = w.AccountId.ToString(), ["pid[0]"] = w.ProductId.ToString(), ["billingcycle[0]"] = "monthly" });
        var invoiceId = order["invoiceid"]!.GetValue<long>();
        await w.Compat(token, new() { ["action"] = "AddInvoicePayment", ["invoiceid"] = invoiceId.ToString(), ["gateway"] = "mercadolibre", ["transid"] = "ML-1" });
        var paymentId = await w.Query(db => db.Payments.Select(p => p.Id).SingleAsync());

        var body = new { invoice_id = invoiceId, reason = "Arrepentimiento", lines = new[] { new { description = "Devolución", quantity = 1, unit_price = 12100m, treatment = "taxed", rate = 0.21m } } };
        using var refund = await w.Client.SendAsync(Request(HttpMethod.Post, $"/api/v1/payments/{paymentId}/refunds", staff, body));
        Assert.AreEqual(HttpStatusCode.Accepted, refund.StatusCode, await refund.Content.ReadAsStringAsync());
        var refundId = JsonNode.Parse(await refund.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();

        // Sin pasarela no hay a quién pedirle la plata: queda como saldo a favor y el staff lo resuelve.
        using var read = await w.Client.SendAsync(Request(HttpMethod.Get, $"/api/v1/refunds/{refundId}", staff));
        Assert.AreEqual("failed", JsonNode.Parse(await read.Content.ReadAsStringAsync())!["status"]!.GetValue<string>());
        Assert.AreEqual(12100m, await w.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        var invoice = await w.Compat(token, new() { ["action"] = "GetInvoice", ["invoiceid"] = invoiceId.ToString() });
        Assert.AreEqual("Refunded", invoice["status"]!.GetValue<string>(), invoice.ToJsonString());
    }

    [TestMethod]
    public async Task ApiCompatible_SinPermisoONiCredencial()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key("compat:GetInvoice");

        var denied = await w.Compat(token, new() { ["action"] = "GetClientsDetails", ["clientid"] = w.AccountId.ToString() });
        var bad = await w.Compat("bk_00000000.mal", new() { ["action"] = "GetInvoice", ["invoiceid"] = "1" });

        Assert.AreEqual("error", denied["result"]!.GetValue<string>());
        Assert.AreEqual("error", bad["result"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ApiCompatible_EstadoDeFacturasImportadas()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key("compat:GetInvoice");
        await w.Query(async db =>
        {
            var zero = new TaxBreakdown([], [], 0m, 0m);
            var paid = new TaxBreakdown([], [], 0m, 500m);
            db.Documents.AddRange(
                Document.FromImport(9001, DocumentKind.Invoice, w.AccountId, Currency.Ars, 9001, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1), zero, 0m, 0m, "{}", DateTimeOffset.UtcNow, sourceStatus: "Paid"),
                Document.FromImport(9002, DocumentKind.Invoice, w.AccountId, Currency.Ars, 9002, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1), zero, 0m, 0m, "{}", DateTimeOffset.UtcNow, sourceStatus: "Cancelled"),
                Document.FromImport(9003, DocumentKind.Invoice, w.AccountId, Currency.Ars, 9003, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1), paid, 0m, 500m, "{}", DateTimeOffset.UtcNow, sourceStatus: "Refunded"));
            return await db.SaveChangesAsync();
        });

        foreach (var (id, expected) in new[] { (9001, "Paid"), (9002, "Cancelled"), (9003, "Refunded") })
        {
            var invoice = await w.Compat(token, new() { ["action"] = "GetInvoice", ["invoiceid"] = id.ToString(CultureInfo.InvariantCulture) });
            Assert.AreEqual(expected, invoice["status"]!.GetValue<string>(), invoice.ToJsonString());
        }
    }
}

[TestClass]
public sealed class PublicErrorsTests
{
    private static Exception Caught(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new AssertFailedException("No lanzó nada.");
    }

    [TestMethod]
    public void SoloEsPublicoLoQueLanzaElCodigoPropio()
    {
        Assert.IsTrue(PublicErrors.IsOwn(Caught(() => _ = new Domain.Shared.Currency("pesos"))));
        Assert.IsTrue(PublicErrors.IsOwn(Caught(() => PhpSerialized.ParseFlatArray("x"))));
        Assert.IsFalse(PublicErrors.IsOwn(Caught(() => _ = Array.Empty<int>().Single())));
        Assert.IsFalse(PublicErrors.IsOwn(Caught(() => _ = new Dictionary<int, int>()[1])));
        Assert.IsFalse(PublicErrors.IsOwn(Caught(() => _ = long.Parse("x", System.Globalization.CultureInfo.InvariantCulture))));
        Assert.IsFalse(PublicErrors.IsOwn(new InvalidOperationException("nunca se lanzó")));
    }
}

[TestClass]
public sealed class PhpSerializedTests
{
    [TestMethod]
    public void ArregloPlano() =>
        CollectionAssert.AreEquivalent(
            new Dictionary<string, string> { ["5"] = "16", ["7"] = "2", ["host"] = "vps1" },
            PhpSerialized.ParseFlatArray("a:3:{i:5;s:2:\"16\";i:7;i:2;s:4:\"host\";s:4:\"vps1\";}"));

    [TestMethod]
    public void CadenaConLargoEnBytes() =>
        Assert.AreEqual("año", PhpSerialized.ParseFlatArray("a:1:{i:1;s:4:\"año\";}")["1"]);

    [TestMethod]
    public void ArregloGrande_SinCopiarElRestoEnCadaValor()
    {
        const int pairs = 5000;
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"a:{pairs}:{{"));
        for (var i = 0; i < pairs; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"i:{i};s:0:\"\";");
        }

        var input = text.Append('}').ToString();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var parsed = PhpSerialized.ParseFlatArray(input);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.HasCount(pairs, parsed);
        // Lineal: unas pocas veces la entrada. Copiar el resto del texto en cada valor pasaba de 400 MB acá.
        Assert.IsLessThan(input.Length * 50L, allocated);
    }

    [TestMethod]
    [DataRow("O:8:\"stdClass\":0:{}")]
    [DataRow("a:1:{i:1;a:0:{}}")]
    [DataRow("a:1:{i:1;s:9:\"corto\";}")]
    public void RechazaLoQueNoEsPlano(string text) =>
        Assert.ThrowsExactly<FormatException>(() => PhpSerialized.ParseFlatArray(text));
}

[TestClass]
public sealed class ApiKeyTests
{
    [TestMethod]
    [DataRow("10.20.31.2", "::ffff:10.20.31.2", true)]
    [DataRow("10.20.31.2", "::ffff:10.20.31.3", false)]
    [DataRow("10.20.31.2", "10.20.31.2", true)]
    [DataRow("10.20.31.0/24", "::ffff:10.20.31.9", true)]
    [DataRow("2001:db8::1", "2001:db8::1", true)]
    [DataRow("::ffff:10.20.31.2", "::ffff:10.20.31.2", true)]
    [DataRow("::ffff:10.20.31.2", "10.20.31.2", true)]
    [DataRow("::ffff:10.20.31.0/120", "10.20.31.9", true)]
    [DataRow("::ffff:10.20.31.0/120", "10.20.32.9", false)]
    public void ListaDeIp_ConDireccionMapeadaDeSocketDual(string allowed, string client, bool expected) =>
        Assert.AreEqual(expected, new ApiKey { AllowedIps = $"[\"{allowed}\"]" }.AllowsIp(IPAddress.Parse(client)));

    [TestMethod]
    public async Task ApiCompatible_ConfigoptionsConJsonRoto_EsUnErrorPropioYNo500()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key("compat:AddOrder");
        var broken = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"5\":"));

        var (status, body) = await w.CompatWithStatus(token, new()
        {
            ["action"] = "AddOrder",
            ["clientid"] = w.AccountId.ToString(CultureInfo.InvariantCulture),
            ["pid[0]"] = w.ProductId.ToString(CultureInfo.InvariantCulture),
            ["billingcycle[0]"] = "monthly",
            ["configoptions[0]"] = broken,
        });

        // Un base64 de JSON mal armado es un dato inválido del pedido, no una caída del servidor.
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("error", body["result"]!.GetValue<string>());
        Assert.AreEqual("Hay un dato con formato inválido.", body["message"]!.GetValue<string>());
        Assert.AreEqual(0, await w.Query(db => db.Orders.CountAsync()));
    }

    [TestMethod]
    public async Task Emision_ValidaLoQueSeCargaYLaRevocacionCortaEnElMomento()
    {
        await using var h = await CoreHarness.Start();
        string[] none = [];
        Task<IssuedApiKey> Issue(string name, string[] scopes, string[]? ips = null, DateTimeOffset? expires = null) =>
            h.Use<ApiKeyService, IssuedApiKey>(k => k.Issue(name, scopes, ips, expires));
        Task<ApiKey?> Auth(IssuedApiKey key, string ip) =>
            h.Use<ApiKeyService, ApiKey?>(k => k.Authenticate(key.Prefix, key.Secret, IPAddress.Parse(ip)));

        await Assert.ThrowsAsync<UseCases.ValidationException>(() => Issue(" ", ["accounts:read"]));
        await Assert.ThrowsAsync<UseCases.ValidationException>(() => Issue("n8n", none));
        await Assert.ThrowsAsync<UseCases.ValidationException>(() => Issue("n8n", ["accounts:read"], ["300.1.2.3"]));
        await Assert.ThrowsAsync<UseCases.ValidationException>(() => Issue("n8n", ["accounts:read"], expires: h.Clock.Now));

        var issued = await Issue(" n8n ", ["accounts:read", "accounts:read", " compat:GetClients "], ["203.0.113.0/24"], h.Clock.Now.AddDays(1));
        var stored = await h.Query(db => db.ApiKeys.SingleAsync());
        Assert.AreEqual("n8n", stored.Name);
        // jsonb normaliza los espacios: se compara la lista, no el texto.
        CollectionAssert.AreEqual(new[] { "accounts:read", "compat:GetClients" }, System.Text.Json.JsonSerializer.Deserialize<string[]>(stored.Scopes));
        Assert.IsNotNull(await Auth(issued, "203.0.113.9"));
        Assert.IsNull(await Auth(issued, "198.51.100.1"), "Fuera de su lista de IP no pasa.");

        await h.Use<ApiKeyService, bool>(async k =>
        {
            await k.Revoke(issued.Id);
            await k.Revoke(issued.Id);
            return true;
        });
        Assert.IsNull(await Auth(issued, "203.0.113.9"), "Revocada no pasa más.");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Use<ApiKeyService, bool>(async k =>
        {
            await k.Revoke(issued.Id + 1000);
            return true;
        }));
        var audit = await h.Query(db => db.Set<AuditEvent>().Where(a => a.Action.StartsWith("apikeys.")).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync());
        CollectionAssert.AreEqual(new[] { "apikeys.issue", "apikeys.revoke" }, audit, "Revocar dos veces deja una sola marca.");
    }
}
