using System.Net;
using System.Reflection;
using System.Text;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Shared;
using BillingOss.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BillingOss.App.Tests;

/// <summary>Varios nodos web: chequeos de salud, Data Protection compartido, tope de avisos y lecturas en réplicas.</summary>
[TestClass]
public sealed class ClusterTests
{
    [TestMethod]
    public async Task Salud_ConLaPrimaria_TodoDa200()
    {
        await using var w = await WebHarness.Start();

        Assert.AreEqual(HttpStatusCode.OK, await Status(w.Client, "/health/live"));
        Assert.AreEqual(HttpStatusCode.OK, await Status(w.Client, "/health/startup"));
        Assert.AreEqual(HttpStatusCode.OK, await Status(w.Client, "/health/ready"));
    }

    [TestMethod]
    public async Task Listo_SinPrimaria_Da503YVivoSigueEn200()
    {
        // Sin pool: la base en solo lectura vale para las sesiones nuevas, y una del pool abierta en ese rato seguiría así.
        await using var w = await WebHarness.Start(pooling: false);
        try
        {
            await Chaos.Demote(w.ConnectionString);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, await Status(w.Client, "/health/ready"));
            Assert.AreEqual(HttpStatusCode.OK, await Status(w.Client, "/health/live"));

            await Chaos.Promote(w.ConnectionString);
            Assert.AreEqual(HttpStatusCode.OK, await Status(w.Client, "/health/ready"));
        }
        finally
        {
            await Chaos.Promote(w.ConnectionString);
        }
    }

    [TestMethod]
    public async Task Listo_AlApagar_Da503()
    {
        await using var w = await WebHarness.Start();

        w.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, await Status(w.Client, "/health/ready"));
        Assert.AreEqual(HttpStatusCode.OK, await Status(w.Client, "/health/live"));
    }

    [TestMethod]
    public async Task Arranque_ConMigracionesPendientes_Da503HastaMigrar()
    {
        var (connectionString, drop) = await TestDatabase.Create();
        var keyFile = Path.Combine(Path.GetTempPath(), $"llaves-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(keyFile, SecretBox.NewKeyLine(1));
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Main"] = connectionString, ["Role"] = "web", ["Secrets:KeyFile"] = keyFile });
            await using var app = AppHost.Build(builder);
            await app.StartAsync();
            using var client = app.GetTestClient();

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, await Status(client, "/health/startup"));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, await Status(client, "/health/ready"));

            await AppHost.Migrate(app);
            Assert.AreEqual(HttpStatusCode.OK, await Status(client, "/health/startup"));
            Assert.AreEqual(HttpStatusCode.OK, await Status(client, "/health/ready"));
        }
        finally
        {
            await drop();
            File.Delete(keyFile);
        }
    }

    [TestMethod]
    public void Arranque_SinLlaves_NoArrancaConNingunRol()
    {
        // El rol web firma cookies y formularios con Data Protection: sin la llave quedaría con un anillo efímero por nodo
        // y las sesiones se caerían al rotar entre nodos, sin que nadie avise.
        foreach (var role in new[] { "web", "worker", "all" })
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Main"] = "Host=127.0.0.1;Database=sin_conexion", ["Role"] = role });

            var error = Assert.ThrowsExactly<InvalidOperationException>(() => { AppHost.Build(builder); });

            StringAssert.Contains(error.Message, "Secrets:KeyFile", $"rol {role}");
        }
    }

    [TestMethod]
    public async Task DataProtection_LoQueProtegeUnNodoLoLeeOtro_YLaLlaveVaCifrada()
    {
        var (connectionString, drop) = await TestDatabase.Create();
        try
        {
            var box = TestModule.NewBox();
            await using var a = Node(connectionString, box);
            await using var b = Node(connectionString, box);
            await using (var scope = a.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<BillingDbContext>().Database.MigrateAsync();
            }

            var cookie = a.GetDataProtector("cookies").Protect("sesión del cliente");

            Assert.AreEqual("sesión del cliente", b.GetDataProtector("cookies").Unprotect(cookie));
            await using var read = b.CreateAsyncScope();
            var stored = await read.ServiceProvider.GetRequiredService<BillingDbContext>().DataProtectionKeys.Select(k => k.Xml).ToListAsync();
            Assert.HasCount(1, stored);
            StringAssert.Contains(stored[0], "secretBox");
            Assert.DoesNotContain("masterKey", stored[0]);
        }
        finally
        {
            await drop();
        }
    }

    [TestMethod]
    public async Task Avisos_IdsInventados_SoloAgotanElBaldeDesconocido()
    {
        await using var w = await WebHarness.Start(settings: new() { ["Webhooks:PermitLimitPerNode"] = "3" });
        var instance = await w.Query(async db =>
        {
            var installation = new ModuleInstallation
            {
                Name = "pasarela",
                BaseUrl = "https://modulo.invalid/",
                Manifest = """{"id":"pasarela","kinds":["payment"],"capabilities":["payment.core","payment.webhooks"]}""",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ModuleInstallations.Add(installation);
            await db.SaveChangesAsync();
            var created = new ModuleInstance { InstallationId = installation.Id, Name = "instancia", Config = "{}", CreatedAt = DateTimeOffset.UtcNow };
            db.ModuleInstances.Add(created);
            await db.SaveChangesAsync();
            return created.Id;
        });

        // El primer aviso de la instancia todavía va al balde de las desconocidas. Después tiene el suyo.
        Assert.AreEqual(HttpStatusCode.OK, await Webhook(w.Client, instance));
        var flood = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            flood.Add(await Webhook(w.Client, 900_000 + i));
        }

        CollectionAssert.AreEqual(
            new[] { HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests },
            flood);
        Assert.AreEqual(HttpStatusCode.OK, await Webhook(w.Client, instance));
    }

    [TestMethod]
    public async Task Replica_LeeYNoPuedeEscribir()
    {
        var (connectionString, drop) = await TestDatabase.Create();
        try
        {
            await using var sp = new ServiceCollection()
                .AddLogging()
                .AddBillingCore(connectionString)
                .AddBillingReplica(connectionString)
                .BuildServiceProvider();
            await using var scope = sp.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<BillingDbContext>().Database.MigrateAsync();
            var replica = scope.ServiceProvider.GetRequiredKeyedService<BillingDbContext>(Failover.Replica);

            Assert.AreEqual(0, await replica.Accounts.CountAsync());
            replica.Accounts.Add(new Account("No se guarda", "nada@example.com", Currency.Ars, DateTimeOffset.UtcNow));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => replica.SaveChangesAsync());
            Assert.AreEqual(PostgresErrorCodes.ReadOnlySqlTransaction, ((PostgresException)error.InnerException!).SqlState);
        }
        finally
        {
            await drop();
        }
    }

    [TestMethod]
    public void Replica_NuncaLlegaACasosDeUsoNiTrabajos()
    {
        // Leer apenas viejo es aceptable en listados y reportes, nunca donde se decide algo (docs/alta-disponibilidad).
        var offenders = new[] { typeof(BillingDbContext).Assembly, typeof(AppHost).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IJobHandler).IsAssignableFrom(t) || t.Namespace?.EndsWith(".UseCases", StringComparison.Ordinal) == true)
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Any(p => p.GetCustomAttribute<FromKeyedServicesAttribute>()?.Key as string == Failover.Replica))
            .Select(t => t.FullName)
            .ToList();

        Assert.IsEmpty(offenders, string.Join(", ", offenders));
    }

    [TestMethod]
    public async Task FrenoCerrado_LaApiNoAceptaEscrituras_YLasLecturasSiguen()
    {
        // Sin réplica sincrónica, una escritura terminaría confirmada solo en la primaria: el vigía cierra el freno y la API
        // da 503 con Retry-After a las escrituras, mientras las lecturas y la salud siguen. Contra una base sin replicación
        // sincrónica, como la de prueba, el vigía no cierra nada.
        await using var w = await WebHarness.Start();
        var token = await w.Key("accounts:write", "accounts:read");
        var gate = w.Services.GetRequiredService<WriteGate>();
        await using (var scope = w.Services.CreateAsyncScope())
        {
            Assert.IsFalse(await ClusterWatch.WaitingForReplica(scope.ServiceProvider.GetRequiredService<BillingDbContext>(), CancellationToken.None));
        }

        gate.Close("La base no tiene ninguna réplica sincrónica");
        using var denied = await w.Client.SendAsync(Account(token, "una@example.com"));
        using var health = await w.Client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        using var read = await w.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/accounts/{w.AccountId}", UriKind.Relative)) { Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token) } });
        gate.Reopen();
        using var created = await w.Client.SendAsync(Account(token, "otra@example.com"));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        Assert.AreEqual("5", denied.Headers.RetryAfter?.ToString());
        StringAssert.Contains(await denied.Content.ReadAsStringAsync(), "réplica sincrónica");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
    }

    private static HttpRequestMessage Account(string token, string email) =>
        new(HttpMethod.Post, new Uri("/api/v1/accounts", UriKind.Relative))
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { name = "Cuenta nueva", email, currency = "ARS" }),
            Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token) },
        };

    private static ServiceProvider Node(string connectionString, SecretBox box) =>
        new ServiceCollection()
            .AddLogging()
            .AddBillingCore(connectionString, secrets: box)
            .AddBillingDataProtection(box)
            .BuildServiceProvider();

    private static async Task<HttpStatusCode> Status(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> Webhook(HttpClient client, long instance)
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri($"/webhooks/{instance}", UriKind.Relative), content);
        return response.StatusCode;
    }
}
