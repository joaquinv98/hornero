using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>Reloj que el test mueve a mano.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>El núcleo sobre una base de prueba propia, con un catálogo mínimo de prueba.</summary>
internal sealed class CoreHarness : IAsyncDisposable
{
    private readonly ServiceProvider provider;
    private readonly Func<ServiceProvider> node;
    private readonly Func<Task> drop;

    private CoreHarness(Func<ServiceProvider> node, TestClock clock, FakeVies vies, string connectionString, Func<Task> drop, CapturedDomainMail domainMail)
    {
        this.node = node;
        provider = node();
        this.drop = drop;
        Clock = clock;
        Vies = vies;
        ConnectionString = connectionString;
        DomainMail = domainMail;
    }

    public TestClock Clock { get; }

    /// <summary>El VIES falso que valida los números de IVA de la UE.</summary>
    public FakeVies Vies { get; }

    public string ConnectionString { get; }

    /// <summary>Los avisos de dominios que pidió el núcleo, como los dejaría el módulo de notificaciones.</summary>
    public CapturedDomainMail DomainMail { get; }

    /// <summary>Los servicios del nodo principal, para quien necesita varios de un mismo alcance.</summary>
    public IServiceProvider Services => provider;

    public long PlanId { get; private set; }

    public long FreePlanId { get; private set; }

    public long AccountId { get; private set; }

    /// <summary>
    /// El núcleo de prueba nunca consulta registros reales: el RDAP va a <paramref name="rdap"/> (o a uno que no
    /// contesta) y la consulta queda apagada salvo que la prueba la prenda con sus servidores falsos. Los avisos de
    /// dominios quedan en <see cref="DomainMail"/>, el mismo para todos los nodos.
    /// </summary>
    /// <param name="services">Servicios de más, como interceptores o la configuración de los trabajadores.</param>
    /// <param name="pooling">Con pool de conexiones, como en producción: una conmutación deja conexiones rotas en el pool.
    /// Sin pool, cada conexión es una sesión nueva (<see cref="TestDatabase.Create(bool, string?)"/>).</param>
    public static async Task<CoreHarness> Start(SecretBox? secrets = null, Action<BillingOptions>? configure = null, Action<FiscalOptions>? fiscal = null, Action<IServiceCollection>? services = null, bool pooling = true, Action<Notifications.NotificationOptions>? notifications = null, HttpMessageHandler? rdap = null)
    {
        var (connectionString, drop) = await TestDatabase.Create(pooling, TestDatabase.Template);

        var clock = new TestClock(new DateTimeOffset(2026, 11, 2, 15, 0, 0, TimeSpan.Zero));
        var vies = new FakeVies();
        var domainMail = new CapturedDomainMail();
        var rdapHandler = rdap ?? new FakeRdap();
        ServiceProvider Node()
        {
            var collection = new ServiceCollection()
                .AddLogging()
                .AddSingleton<TimeProvider>(clock)
                .AddSingleton<UseCases.IVatNumberValidator>(vies)
                .AddSingleton<UseCases.IDomainMail>(domainMail)
                .AddSingleton(new UseCases.RdapHandler(rdapHandler))
                .AddBillingCore(connectionString, o =>
                {
                    o.PublicUrl = "https://billing.example.com";
                    o.DomainLookup.Enabled = false;
                    configure?.Invoke(o);
                }, secrets)
                .AddBillingReplica(null)
                .Configure<FiscalOptions>(o => fiscal?.Invoke(o))
                .Configure<Notifications.NotificationOptions>(o => notifications?.Invoke(o));
            services?.Invoke(collection);
            return collection.BuildServiceProvider();
        }

        var harness = new CoreHarness(Node, clock, vies, connectionString, drop, domainMail);
        try
        {
            await harness.Db(db => db.Database.MigrateAsync());
            await harness.Seed();
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    public async Task<T> Use<TService, T>(Func<TService, Task<T>> work)
        where TService : notnull
    {
        await using var scope = provider.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public Task Db(Func<BillingDbContext, Task> work) => Use<BillingDbContext, bool>(async db =>
    {
        await work(db);
        return true;
    });

    public Task<T> Query<T>(Func<BillingDbContext, Task<T>> work) => Use(work);

    /// <summary>Un alcance entero, para lo que necesita más de un servicio.</summary>
    public async Task<T> Scope<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = provider.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    /// <summary>
    /// Una factura emitida con un renglón del dominio, como la deja la corrida (renovación) o una contratación (una vez).
    /// Devuelve su ID.
    /// </summary>
    public Task<long> DomainInvoice(long domainId, LineKind kind, decimal amount, string description = "Renglón del dominio") => Scope(async sp =>
    {
        var db = sp.GetRequiredService<BillingDbContext>();
        var core = sp.GetRequiredService<UseCases.BillingCore>();
        var domain = await db.Domains.SingleAsync(d => d.Id == domainId);
        var receiver = await core.Receiver(domain.AccountId, CancellationToken.None);
        var invoice = new Document(DocumentKind.Invoice, domain.AccountId, domain.Currency, domain.PriceMode, core.Today().AddDays(core.Options.InvoiceDueDays), Clock.Now, reason: kind == LineKind.Renewal ? "renewal" : null);
        invoice.AddLine(kind, description, 1, amount, TaxTreatment.Taxed, 0.21m, domainId: domainId);
        await using var tx = await db.Database.BeginTransactionAsync();
        db.Documents.Add(invoice);
        await core.IssueDraft(invoice, receiver, CancellationToken.None);
        await core.Commit(tx, CancellationToken.None);
        return invoice.Id;
    });

    public Task<int> RunJobs() => provider.GetRequiredService<JobRunner>().RunUntilIdle(CancellationToken.None);

    /// <summary>Otro nodo sobre la misma base y el mismo reloj, con sus propios servicios y su propio corredor.</summary>
    public ServiceProvider Node() => node();

    public async Task Tick()
    {
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        await Scheduler.Tick(sp.GetRequiredService<BillingDbContext>(), sp.GetRequiredService<JobQueue>(), sp.GetRequiredService<UseCases.BillingCore>(), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await provider.DisposeAsync();
        await drop();
    }

    private async Task Seed() => await Db(async db =>
    {
        (PlanId, FreePlanId, AccountId) = await SeedCatalog(db, Clock.Now);
    });

    /// <summary>Catálogo mínimo: VPS mensual con IVA, prueba gratis, reglas de impuesto y una cuenta de consumidor final.</summary>
    public static async Task<(long PlanId, long FreePlanId, long AccountId)> SeedCatalog(BillingDbContext db, DateTimeOffset now)
    {
        var vps = new Product { Name = "VPS 2 GB", Kind = ProductKind.Manual };
        var trial = new Product { Name = "Prueba gratis", Kind = ProductKind.Manual };
        db.Products.AddRange(vps, trial);
        await db.SaveChangesAsync();

        var monthly = new Plan(vps.Id, BillingCycle.Monthly);
        var free = new Plan(trial.Id, BillingCycle.Free);
        db.Plans.AddRange(monthly, free);
        await db.SaveChangesAsync();

        db.PlanPrices.Add(new PlanPrice(monthly.Id, Currency.Ars, 12100m, 0m, new DateOnly(2026, 1, 1)));
        db.PlanPrices.Add(new PlanPrice(free.Id, Currency.Ars, 0m, 0m, new DateOnly(2026, 1, 1)));
        db.TaxRules.Add(new TaxRule { Country = "AR", Outcome = TaxTreatment.Taxed, Rate = 0.21m, ValidFrom = new DateOnly(2026, 1, 1) });
        db.TaxRules.Add(new TaxRule { Outcome = TaxTreatment.Exempt, ValidFrom = new DateOnly(2026, 1, 1) });

        var account = new Account("Cliente de prueba", "cliente@example.com", Currency.Ars, now);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        db.TaxIdentities.Add(new TaxIdentity(account.Id, TaxDocumentType.Dni, "30111222", "Cliente de prueba", 5, "AR", now));
        await db.SaveChangesAsync();
        return (monthly.Id, free.Id, account.Id);
    }
}
