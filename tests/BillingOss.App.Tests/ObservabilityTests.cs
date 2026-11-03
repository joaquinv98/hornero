using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using BillingOss.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace BillingOss.App.Tests;

/// <summary>Observabilidad (D-15): métricas de negocio desde la base, trazas de la cola, auditoría y logs sin secretos.</summary>
[TestClass]
public sealed class ObservabilityTests
{
    [TestMethod]
    public async Task LasMetricasDeNegocioSalenDeLaBase_YLasGaugesPublicanLoMismo()
    {
        await using var h = await CoreHarness.Start();
        var now = h.Clock.Now;

        // Una corrida de facturación real deja su duración en la auditoría.
        await h.Tick();
        await h.RunJobs();

        await h.Use<JobQueue, bool>(async jobs =>
        {
            await jobs.Enqueue("prueba.a", new { }, runAt: now.AddMinutes(-10));
            await jobs.Enqueue("prueba.a", new { }, runAt: now.AddMinutes(-1));
            await jobs.Enqueue("prueba.a", new { }, runAt: now.AddHours(1));
            await jobs.Enqueue("prueba.muerto", new { });
            await jobs.Enqueue("prueba.trabado", new { });
            return true;
        });
        await h.Db(async db =>
        {
            var expired = now.AddMinutes(-1);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE platform.jobs SET status = 'Dead', attempts = 10 WHERE kind = 'prueba.muerto'");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE platform.jobs SET status = 'Running', locked_until = {expired} WHERE kind = 'prueba.trabado'");

            var installation = new ModuleInstallation { Name = "pasarela", BaseUrl = "http://127.0.0.1:1/", CreatedAt = now };
            db.ModuleInstallations.Add(installation);
            await db.SaveChangesAsync();
            var instance = new ModuleInstance { InstallationId = installation.Id, Name = "instancia", CreatedAt = now };
            db.ModuleInstances.Add(instance);
            await db.SaveChangesAsync();
            db.WebhookReceipts.Add(new WebhookReceipt { ModuleInstanceId = instance.Id, Status = ReceiptStatus.Received, ReceivedAt = now.AddMinutes(-2) });
            db.WebhookReceipts.Add(new WebhookReceipt { ModuleInstanceId = instance.Id, Status = ReceiptStatus.Verified, ReceivedAt = now.AddHours(-3) });
            db.Payments.Add(new Payment(h.AccountId, "manual:efectivo", "cupon-1", Currency.Ars, 1000m, PaymentStatus.Pending, now));
            db.UnmatchedPayments.Add(new UnmatchedPayment { ModuleInstanceId = instance.Id, PaymentExternalId = "pay-x", Status = "approved", Amount = 1m, Currency = "ARS", SeenAt = now });
            db.ReconcileFindings.Add(new ReconcileFinding { Kind = ReconcileFinding.FiscalOrphan, Subject = "3-6-1", CreatedAt = now });
            db.ReconcileFindings.Add(new ReconcileFinding { Kind = ReconcileFinding.FiscalOrphan, Subject = "3-6-2", CreatedAt = now, ResolvedAt = now });
            await db.SaveChangesAsync();
        });

        var rows = await h.Query(db => BusinessMetrics.Read(db, now, CancellationToken.None));
        double Value(string name, string label = "") => rows.Single(r => r.Name == name && r.Label == label).Value;

        Assert.AreEqual(3, Value("jobs_pending", "prueba.a"));
        Assert.AreEqual(2, Value("jobs_due", "prueba.a"));
        Assert.AreEqual(600, Value("jobs_oldest_due_seconds", "prueba.a"));
        Assert.AreEqual(1, Value("jobs_dead", "prueba.muerto"));
        Assert.AreEqual(1, Value("jobs_stuck", "prueba.trabado"));
        Assert.AreEqual(0, Value("jobs_running", "prueba.trabado"));
        Assert.AreEqual(1, Value("webhooks_unprocessed"));
        Assert.AreEqual(120, Value("webhooks_oldest_seconds"));
        Assert.AreEqual(1, Value("payments_pending", "Pending"));
        Assert.AreEqual(1, Value("payments_unmatched"));
        Assert.AreEqual(1, Value("reconcile_findings", ReconcileFinding.FiscalOrphan), "Los hallazgos resueltos no cuentan.");
        Assert.IsGreaterThanOrEqualTo(0d, Value("billing_run_seconds"));
        Assert.AreEqual(0, Value("billing_run_age_seconds"));
        Assert.IsFalse(rows.Any(r => r.Label == JobKinds.BillingRun), "Los trabajos terminados no cuentan.");

        // La misma consulta que el UserParameter de deploy/observabilidad/zabbix: un arreglo JSON con números.
        var zabbix = await h.Query(db => db.Database.SqlQuery<string>($"SELECT coalesce(json_agg(m), '[]')::text AS \"Value\" FROM platform.metrics({now}) m").SingleAsync());
        var due = JsonNode.Parse(zabbix)!.AsArray().Single(n => n!["name"]!.GetValue<string>() == "jobs_due" && n["label"]!.GetValue<string>() == "prueba.a")!;
        Assert.AreEqual(2d, due["value"]!.GetValue<double>());

        var run = await h.Query(db => db.Set<AuditEvent>().AsNoTracking().SingleAsync(e => e.Action == JobKinds.BillingRun));
        Assert.AreEqual(ActorKind.System, run.ActorKind);
        Assert.IsTrue(run.Entity!.StartsWith("job:", StringComparison.Ordinal), run.Entity);
        Assert.AreEqual(0, JsonNode.Parse(run.Details!)!["accounts"]!.GetValue<int>());

        // Las gauges publican lo último que se leyó, con el tipo de trabajo como atributo.
        var seen = new ConcurrentBag<(string Instrument, double Value, string? Tag)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == Telemetry.Name && instrument.Name.StartsWith("billing.jobs.", StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            seen.Add((instrument.Name, value, tags.ToArray().FirstOrDefault(t => t.Key == "job.kind").Value?.ToString())));
        await h.Use<IServiceScopeFactory, bool>(async scopes =>
        {
            using var metrics = new BusinessMetrics(scopes, h.Clock, NullLogger<BusinessMetrics>.Instance);
            listener.Start();
            listener.RecordObservableInstruments();
            Assert.IsFalse(seen.Any(s => s.Tag == "prueba.a"), "Antes de leer la base no se publica nada.");
            await metrics.Refresh(CancellationToken.None);
            listener.RecordObservableInstruments();
            return true;
        });

        Assert.Contains(("billing.jobs.due", 2d, (string?)"prueba.a"), seen);
        Assert.Contains(("billing.jobs.dead", 1d, (string?)"prueba.muerto"), seen);
    }

    [TestMethod]
    public async Task CadaTrabajoEsUnSpanHijoDeQuienLoEncolo_YMideSuDuracion()
    {
        await using var h = await CoreHarness.Start();
        var spans = new ConcurrentBag<Activity>();
        using var activities = new ActivityListener
        {
            ShouldListenTo = s => s.Name == Telemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(activities);
        var durations = new ConcurrentBag<(string? Kind, string? Outcome)>();
        using var meters = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "billing.job.duration")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        meters.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            var all = tags.ToArray();
            durations.Add((all.FirstOrDefault(t => t.Key == "job.kind").Value?.ToString(), all.FirstOrDefault(t => t.Key == "outcome").Value?.ToString()));
        });
        meters.Start();

        ActivityTraceId traceId;
        ActivitySpanId spanId;
        using (var request = new Activity("pedido de prueba").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            traceId = request.TraceId;
            spanId = request.SpanId;
            await h.Use<JobQueue, bool>(async jobs =>
            {
                await jobs.Enqueue(JobKinds.BillingRun, new { date = "2026-11-02" }, dedupKey: "prueba-traza");
                return true;
            });
        }

        await h.RunJobs();

        var job = spans.Single(a => a.TraceId == traceId);
        Assert.AreEqual(spanId, job.ParentSpanId);
        Assert.AreEqual(JobKinds.BillingRun, job.GetTagItem("job.kind"));
        Assert.AreEqual(ActivityKind.Consumer, job.Kind);
        Assert.Contains(((string?)JobKinds.BillingRun, (string?)"done"), durations);
    }

    [TestMethod]
    public async Task LasApisQuedanEnLaAuditoria_YLaAuditoriaNoSeModifica()
    {
        await using var w = await WebHarness.Start(compat: true);
        var token = await w.Key("accounts:write", "accounts:read", "compat:AddClient", "compat:GetClientsDetails");
        var prefix = token.Split('.')[0];

        using var created = await Send(w.Client, HttpMethod.Post, "/api/v1/accounts", token, new { name = "Cuenta auditada", email = "auditada@example.com", currency = "ARS" });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
        using var read = await Send(w.Client, HttpMethod.Get, $"/api/v1/accounts/{id}", token);
        using var bad = await Send(w.Client, HttpMethod.Post, "/api/v1/accounts", token, new { name = "x", email = "no-es-correo", currency = "ARS" });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        using var anonymous = await Send(w.Client, HttpMethod.Post, "/api/v1/accounts", null, new { name = "x", email = "x@example.com", currency = "ARS" });
        var client = await w.Compat(token, new() { ["action"] = "AddClient", ["firstname"] = "Ana", ["lastname"] = "Pérez", ["email"] = "ana@example.com", ["password2"] = "x", ["country"] = "AR", ["currency"] = "1", ["noemail"] = "true", ["skipvalidation"] = "true" });
        await w.Compat(token, new() { ["action"] = "GetClientsDetails", ["clientid"] = client["clientid"]!.ToJsonString() });

        // La emisión de la llave de la prueba también queda en la auditoría, pero no es un pedido a la API.
        var events = await w.Query(db => db.Set<AuditEvent>().AsNoTracking().Where(e => e.Action != "apikeys.issue").OrderBy(e => e.Id).ToListAsync());
        Assert.HasCount(3, events, "Ni las lecturas ni los pedidos sin credencial se auditan.");
        Assert.IsTrue(events.All(e => e.ActorKind == ActorKind.Api && e.Actor.StartsWith("apikey:", StringComparison.Ordinal) && e.Actor.EndsWith(prefix, StringComparison.Ordinal)));
        Assert.AreEqual("POST /api/v1/accounts", events[0].Action);
        Assert.AreEqual("/api/v1/accounts", events[0].Entity);
        var details = JsonNode.Parse(events[0].Details!)!;
        Assert.AreEqual(201, details["status"]!.GetValue<int>());
        Assert.AreEqual(id, details["ids"]!["id"]!.GetValue<long>());
        Assert.AreEqual(422, JsonNode.Parse(events[1].Details!)!["status"]!.GetValue<int>());
        Assert.AreEqual("compat AddClient", events[2].Action);
        Assert.AreEqual(client["clientid"]!.GetValue<long>(), JsonNode.Parse(events[2].Details!)!["ids"]!["clientid"]!.GetValue<long>());

        await w.Query<bool>(async db =>
        {
            var update = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE platform.audit_events SET actor = 'otro'"));
            var delete = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM platform.audit_events"));
            Assert.AreEqual(PostgresErrorCodes.CheckViolation, update.SqlState);
            Assert.AreEqual(PostgresErrorCodes.CheckViolation, delete.SqlState);
            return true;
        });
    }

    [TestMethod]
    public async Task LasOperacionesConEfectoDeLosModulosQuedanEnLaAuditoriaConQuienLasPidio()
    {
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        var module = new ModuleDefinition(TestModule.Manifest("prueba"))
            .Operation("provisioning", "create", _ => Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = "active" })))
            .Operation("provisioning", "status", _ => Task.FromResult(ModuleResponse.Completed()), mutating: false);
        await using var fake = await TestModule.Start(module, h, box);
        var resolved = await h.Use<ModuleRegistry, ResolvedInstance>(r => r.Resolve(fake.InstanceId, CancellationToken.None));

        Actor.Current = new Actor(ActorKind.Staff, "staff:7", IPAddress.Loopback);
        var create = await h.Use<ModuleClient, ModuleCallResult>(c => c.Call(resolved.Endpoint, "/v0/provisioning/create", new ModuleRequest(resolved.Data, [], []), "servicio-1-alta", TimeSpan.FromSeconds(10), CancellationToken.None));
        var status = await h.Use<ModuleClient, ModuleCallResult>(c => c.Call(resolved.Endpoint, "/v0/provisioning/status", new ModuleRequest(resolved.Data, [], []), null, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.IsNull(create.Error);
        Assert.IsNull(status.Error);

        var events = await h.Query(db => db.Set<AuditEvent>().AsNoTracking().ToListAsync());
        var e = events.Single();
        Assert.AreEqual("module /v0/provisioning/create", e.Action);
        Assert.AreEqual(fake.Url, e.Entity);
        Assert.AreEqual(ActorKind.Staff, e.ActorKind);
        Assert.AreEqual("staff:7", e.Actor);
        Assert.AreEqual(IPAddress.Loopback, e.Ip);
        var details = JsonNode.Parse(e.Details!)!;
        Assert.AreEqual("servicio-1-alta", details["idempotency_key"]!.GetValue<string>());
        Assert.AreEqual("ok", details["outcome"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ElResultadoFinalDeUnaOperacionLargaQuedaEnLaAuditoria()
    {
        // La operación queda como pending. El sondeo no se audita mientras siga pending, y sí cuando termina.
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        var polls = 0;
        var module = new ModuleDefinition(TestModule.Manifest("prueba"))
            .Operation("provisioning", "create", _ => Task.FromResult(ModuleResponse.Pending("op-9", retryAfter: 0)))
            .Poll((_, id) => Task.FromResult(++polls == 1 ? ModuleResponse.Pending(id, 0) : ModuleResponse.Completed(new JsonObject { ["observed_state"] = "active" })));
        await using var fake = await TestModule.Start(module, h, box);
        var resolved = await h.Use<ModuleRegistry, ResolvedInstance>(r => r.Resolve(fake.InstanceId, CancellationToken.None));
        Task<ModuleCallResult> Call(string path, string? key) =>
            h.Use<ModuleClient, ModuleCallResult>(c => c.Call(resolved.Endpoint, path, new ModuleRequest(resolved.Data, [], []), key, TimeSpan.FromSeconds(10), CancellationToken.None));

        await Call("/v0/provisioning/create", "servicio-1-alta");
        await Call("/v0/operations/op-9", null);
        await Call("/v0/operations/op-9", null);

        var events = await h.Query(db => db.Set<AuditEvent>().AsNoTracking().OrderBy(e => e.Id).ToListAsync());
        Assert.HasCount(2, events);
        Assert.AreEqual("pending", JsonNode.Parse(events[0].Details!)!["outcome"]!.GetValue<string>());
        Assert.AreEqual("module /v0/operations/op-9", events[1].Action);
        var final = JsonNode.Parse(events[1].Details!)!;
        Assert.AreEqual("op-9", final["operation_id"]!.GetValue<string>());
        Assert.AreEqual("ok", final["outcome"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task LosLogsNoLlevanCredencialesNiLaCadenaDeConexion()
    {
        var (connectionString, drop) = await TestDatabase.Create();
        var keyFile = Path.Combine(Path.GetTempPath(), $"llaves-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(keyFile, SecretBox.NewKeyLine(1));
        try
        {
            var logs = new CapturingLogs();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(logs);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Main"] = connectionString,
                ["Role"] = "web",
                ["Secrets:KeyFile"] = keyFile,
                ["CompatApi:Enabled"] = "true",
                ["CompatApi:Currencies:1"] = "ARS",
            });
            await using var app = AppHost.Build(builder);
            await AppHost.Migrate(app);
            await app.StartAsync();
            string token;
            await using (var scope = app.Services.CreateAsyncScope())
            {
                token = (await scope.ServiceProvider.GetRequiredService<ApiKeyService>().Issue("prueba", ["accounts:write", "compat:AddClient"])).Token;
            }

            var secret = token.Split('.')[1];
            var client = app.GetTestClient();
            using var created = await Send(client, HttpMethod.Post, "/api/v1/accounts", token, new { name = "Cuenta", email = "cuenta@example.com", currency = "ARS" });
            using var wrong = await Send(client, HttpMethod.Post, "/api/v1/accounts", token[..^4] + "AAAA", new { name = "Cuenta", email = "cuenta@example.com", currency = "ARS" });
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["action"] = "AddClient",
                ["identifier"] = token.Split('.')[0],
                ["secret"] = secret,
                ["firstname"] = "Ana",
                ["lastname"] = "Pérez",
                ["email"] = "ana@example.com",
                ["country"] = "AR",
                ["currency"] = "1",
            });
            using var compat = await client.PostAsync(new Uri("/includes/api.php", UriKind.Relative), form);
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
            await app.StopAsync();

            Assert.IsNotEmpty(logs.Lines, "Con nivel Trace tiene que haber logs para revisar.");
            var password = new NpgsqlConnectionStringBuilder(connectionString).Password;
            foreach (var line in logs.Lines)
            {
                Assert.DoesNotContain(secret, line);
                Assert.IsFalse(!string.IsNullOrEmpty(password) && line.Contains(password, StringComparison.Ordinal), "Un log trae la contraseña de la base.");
            }
        }
        finally
        {
            await drop();
            File.Delete(keyFile);
        }
    }

    [TestMethod]
    public void SinDestinoOtlpNoHayOpenTelemetry_ConDestinoExportaLasTresSenales()
    {
        var keyFile = Path.Combine(Path.GetTempPath(), $"llaves-{Guid.NewGuid():N}");
        File.WriteAllText(keyFile, SecretBox.NewKeyLine(1));
        WebApplication Build(string? endpoint)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Main"] = "Host=127.0.0.1;Database=sin_conexion",
                ["Role"] = "worker",
                ["Secrets:KeyFile"] = keyFile,
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint,
            });
            return AppHost.Build(builder);
        }

        using var off = Build(null);
        Assert.IsNull(off.Services.GetService<TracerProvider>());
        Assert.IsNull(off.Services.GetService<MeterProvider>());
        Assert.IsFalse(off.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<BusinessMetrics>().Any());

        using var on = Build("http://127.0.0.1:4317");
        Assert.IsNotNull(on.Services.GetService<TracerProvider>());
        Assert.IsNotNull(on.Services.GetService<MeterProvider>());
        Assert.IsTrue(on.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<BusinessMetrics>().Any());
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Junta cada mensaje, sus valores estructurados y su excepción.</summary>
    private sealed class CapturingLogs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            Lines.Enqueue(state.ToString() ?? string.Empty);
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            Lines.Enqueue($"{formatter(state, exception)} {string.Join(' ', values.Select(v => v.Value))} {exception}");
        }

        public void Dispose()
        {
        }
    }
}
