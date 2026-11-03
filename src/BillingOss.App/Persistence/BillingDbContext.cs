using BillingOss.App.Platform;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BillingOss.App.Persistence;

/// <summary>
/// Un solo contexto con un esquema de PostgreSQL por módulo (D-02 y D-03). Las invariantes van primero en la base:
/// restricciones únicas, de exclusión y checks. Lo que EF no expresa está en SQL en la migración inicial.
/// </summary>
public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<TaxIdentity> TaxIdentities => Set<TaxIdentity>();

    public DbSet<ProductGroup> ProductGroups => Set<ProductGroup>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<OptionGroup> OptionGroups => Set<OptionGroup>();

    public DbSet<ProductOptionGroup> ProductOptionGroups => Set<ProductOptionGroup>();

    public DbSet<Option> Options => Set<Option>();

    public DbSet<OptionValue> OptionValues => Set<OptionValue>();

    public DbSet<OptionPrice> OptionPrices => Set<OptionPrice>();

    public DbSet<Coupon> Coupons => Set<Coupon>();

    public DbSet<Bundle> Bundles => Set<Bundle>();

    public DbSet<Plan> Plans => Set<Plan>();

    public DbSet<PlanPrice> PlanPrices => Set<PlanPrice>();

    public DbSet<TaxRule> TaxRules => Set<TaxRule>();

    public DbSet<TaxCategory> TaxCategories => Set<TaxCategory>();

    public DbSet<Tax> Taxes => Set<Tax>();

    public DbSet<TaxSettings> TaxSettings => Set<TaxSettings>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<Service> Services => Set<Service>();

    public DbSet<ServiceOption> ServiceOptions => Set<ServiceOption>();

    public DbSet<ServiceChange> ServiceChanges => Set<ServiceChange>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentLine> DocumentLines => Set<DocumentLine>();

    public DbSet<BilledPeriod> BilledPeriods => Set<BilledPeriod>();

    public DbSet<CreditEntry> CreditEntries => Set<CreditEntry>();

    public DbSet<BillableItem> BillableItems => Set<BillableItem>();

    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();

    public DbSet<Quote> Quotes => Set<Quote>();

    public DbSet<CreditBalance> CreditBalances => Set<CreditBalance>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<Allocation> Allocations => Set<Allocation>();

    public DbSet<Refund> Refunds => Set<Refund>();

    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();

    public DbSet<WebhookReceipt> WebhookReceipts => Set<WebhookReceipt>();

    public DbSet<GatewayEvent> GatewayEvents => Set<GatewayEvent>();

    public DbSet<SavedPaymentMethod> SavedPaymentMethods => Set<SavedPaymentMethod>();

    public DbSet<GatewaySubscription> GatewaySubscriptions => Set<GatewaySubscription>();

    public DbSet<GatewaySetup> GatewaySetups => Set<GatewaySetup>();

    public DbSet<TransferAccount> TransferAccounts => Set<TransferAccount>();

    public DbSet<BankMovement> BankMovements => Set<BankMovement>();

    public DbSet<TransferNotice> TransferNotices => Set<TransferNotice>();

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<ModuleInstallation> ModuleInstallations => Set<ModuleInstallation>();

    public DbSet<ModuleInstance> ModuleInstances => Set<ModuleInstance>();

    public DbSet<ServiceSecret> ServiceSecrets => Set<ServiceSecret>();

    public DbSet<UseCases.StaffTask> StaffTasks => Set<UseCases.StaffTask>();

    public DbSet<Domain.Domains.RegisteredDomain> Domains => Set<Domain.Domains.RegisteredDomain>();

    public DbSet<Domain.Domains.DomainOperation> DomainOperations => Set<Domain.Domains.DomainOperation>();

    public DbSet<Domain.Domains.TldPrice> TldPrices => Set<Domain.Domains.TldPrice>();

    public DbSet<Domain.Domains.DomainOptionPrice> DomainOptionPrices => Set<Domain.Domains.DomainOptionPrice>();

    public DbSet<Domain.Domains.TldOptions> TldOptions => Set<Domain.Domains.TldOptions>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<Domain.Fiscal.IssuerProfile> IssuerProfiles => Set<Domain.Fiscal.IssuerProfile>();

    public DbSet<Domain.Fiscal.FiscalSeries> FiscalSeries => Set<Domain.Fiscal.FiscalSeries>();

    public DbSet<Domain.Fiscal.FiscalEmission> FiscalEmissions => Set<Domain.Fiscal.FiscalEmission>();

    public DbSet<Domain.Fiscal.FiscalDocument> FiscalDocuments => Set<Domain.Fiscal.FiscalDocument>();

    public DbSet<Domain.Fiscal.FiscalCredential> FiscalCredentials => Set<Domain.Fiscal.FiscalCredential>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<UnmatchedPayment> UnmatchedPayments => Set<UnmatchedPayment>();

    public DbSet<ReconcileFinding> ReconcileFindings => Set<ReconcileFinding>();

    public DbSet<TimelineMark> Timeline => Set<TimelineMark>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Properties<Currency>().HaveConversion<CurrencyConverter>().HaveMaxLength(3).AreFixedLength();
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BillingDbContext).Assembly);

        // Nada financiero se borra en cascada. Solo las líneas siguen a su borrador o a su pedido.
        foreach (var fk in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()))
        {
            var ownsLines = fk.DeclaringEntityType.ClrType == typeof(DocumentLine) && fk.PrincipalEntityType.ClrType == typeof(Document)
                || fk.DeclaringEntityType.ClrType == typeof(OrderLine);
            fk.DeleteBehavior = ownsLines ? DeleteBehavior.Cascade : DeleteBehavior.Restrict;
        }

        // Enums como texto: se leen en la base y no se rompen si cambia el orden.
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            if (type.IsEnum)
            {
                var converter = (ValueConverter)Activator.CreateInstance(typeof(EnumToStringConverter<>).MakeGenericType(type), [null])!;
                property.SetValueConverter(converter);
                property.SetMaxLength(32);
            }
        }
    }

    private sealed class CurrencyConverter() : ValueConverter<Currency, string>(c => c.Code, s => new Currency(s));
}

/// <summary>Para las herramientas de migraciones. No se conecta a ninguna base.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<BillingDbContext>
{
    public BillingDbContext CreateDbContext(string[] args) =>
        new(Configure(new DbContextOptionsBuilder<BillingDbContext>(), "Host=localhost;Database=billing_oss").Options);

    public static DbContextOptionsBuilder<BillingDbContext> Configure(DbContextOptionsBuilder<BillingDbContext> builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        // La cerca de los trabajos, la guarda de la numeración y el aviso de COMMIT sin réplica valen para todo contexto
        // (docs/alta-disponibilidad). El orden importa: las dos que revisan van antes de CommitGuard, que hace el COMMIT.
        return builder.UseNpgsql(connectionString, o => o.MigrationsHistoryTable("migrations", "platform")).UseSnakeCaseNamingConvention()
            .AddInterceptors(JobFence.Instance, NumberingGuard.Instance, CommitGuard.Instance);
    }
}
