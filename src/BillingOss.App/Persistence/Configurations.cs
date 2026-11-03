using System.Text.Json;
using System.Text.Json.Serialization;
using BillingOss.App.Platform;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingOss.App.Persistence;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> b)
    {
        b.ToTable("accounts", "accounts");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.CompanyName).HasMaxLength(200);
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.Phone).HasMaxLength(50);
        b.Property(x => x.Language).HasMaxLength(10);
        // El correo es único sin distinguir mayúsculas: índice ix_accounts_email sobre lower(email), creado a mano
        // en la migración Integracion porque EF no modela índices por expresión. Las cuentas sin correo quedan afuera.
    }
}

internal sealed class TaxIdentityConfiguration : IEntityTypeConfiguration<TaxIdentity>
{
    public void Configure(EntityTypeBuilder<TaxIdentity> b)
    {
        b.ToTable("tax_identities", "accounts");
        b.Property(x => x.DocumentNumber).HasMaxLength(30);
        b.Property(x => x.LegalName).HasMaxLength(200);
        b.Property(x => x.Country).HasMaxLength(2).IsFixedLength();
        b.Property(x => x.VatNumber).HasMaxLength(30);
        b.Property(x => x.VatValidationRef).HasMaxLength(100);
        b.Property(x => x.Source).HasMaxLength(20);
        b.Ignore(x => x.ValidatedBusiness);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasIndex(x => x.AccountId).IsUnique().HasFilter("valid_to IS NULL");
    }
}

internal sealed class ProductGroupConfiguration : IEntityTypeConfiguration<ProductGroup>
{
    public void Configure(EntityTypeBuilder<ProductGroup> b)
    {
        b.ToTable("product_groups", "catalog");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Headline).HasMaxLength(500);
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        // El stock nunca queda negativo: el pedido lo descuenta con un UPDATE condicionado y la base es la última palabra.
        b.ToTable("products", "catalog", t => t.HasCheckConstraint("ck_products_stock", "stock IS NULL OR stock >= 0"));
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(4000);
        b.Property(x => x.TaxCategory).HasMaxLength(50);
        b.Property(x => x.ModuleParams).HasColumnType("jsonb");
        b.HasOne<ProductGroup>().WithMany().HasForeignKey(x => x.GroupId);
    }
}

internal sealed class OptionGroupConfiguration : IEntityTypeConfiguration<OptionGroup>
{
    public void Configure(EntityTypeBuilder<OptionGroup> b)
    {
        b.ToTable("option_groups", "catalog");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(1000);
    }
}

internal sealed class ProductOptionGroupConfiguration : IEntityTypeConfiguration<ProductOptionGroup>
{
    public void Configure(EntityTypeBuilder<ProductOptionGroup> b)
    {
        b.ToTable("product_option_groups", "catalog");
        b.HasKey(x => new { x.ProductId, x.OptionGroupId });
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
        b.HasOne<OptionGroup>().WithMany().HasForeignKey(x => x.OptionGroupId);
    }
}

internal sealed class OptionConfiguration : IEntityTypeConfiguration<Option>
{
    public void Configure(EntityTypeBuilder<Option> b)
    {
        b.ToTable("options", "catalog", t => t.HasCheckConstraint("ck_options_quantity", "min_quantity >= 0 AND max_quantity >= 0"));
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.ModuleKey).HasMaxLength(200);
        b.HasOne<OptionGroup>().WithMany().HasForeignKey(x => x.GroupId);
        b.Ignore(x => x.SingleValue);
    }
}

internal sealed class OptionValueConfiguration : IEntityTypeConfiguration<OptionValue>
{
    public void Configure(EntityTypeBuilder<OptionValue> b)
    {
        b.ToTable("option_values", "catalog");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.ModuleValue).HasMaxLength(200);
        b.HasOne<Option>().WithMany().HasForeignKey(x => x.OptionId);
    }
}

internal sealed class OptionPriceConfiguration : IEntityTypeConfiguration<OptionPrice>
{
    public void Configure(EntityTypeBuilder<OptionPrice> b)
    {
        b.ToTable("option_prices", "catalog", t => t.HasCheckConstraint("ck_option_prices_positive", "amount >= 0 AND setup_fee >= 0"));
        b.Property(x => x.Amount).HasPrecision(18, 6);
        b.Property(x => x.SetupFee).HasPrecision(18, 6);
        b.HasOne<OptionValue>().WithMany().HasForeignKey(x => x.ValueId);
        b.HasIndex(x => new { x.ValueId, x.Currency, x.CycleUnit, x.CycleCount }).IsUnique();
        b.Ignore(x => x.Cycle);
    }
}

internal sealed class CouponConfiguration : IEntityTypeConfiguration<Coupon>
{
    public void Configure(EntityTypeBuilder<Coupon> b)
    {
        // Los usos nunca pasan el tope: el pedido los suma con un UPDATE condicionado.
        b.ToTable("coupons", "catalog", t =>
        {
            t.HasCheckConstraint("ck_coupons_value", "value >= 0 AND (kind <> 'Percent' OR value <= 100)");
            t.HasCheckConstraint("ck_coupons_uses", "uses >= 0 AND (max_uses IS NULL OR uses <= max_uses)");
        });
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Code).HasMaxLength(100);
        b.Property(x => x.Value).HasPrecision(18, 6);
        b.Property(x => x.Notes).HasMaxLength(1000);

        // Único sin distinguir mayúsculas: índice ix_coupons_code sobre lower(code), creado en la migración Catalogo.
    }
}

internal sealed class BundleConfiguration : IEntityTypeConfiguration<Bundle>
{
    public void Configure(EntityTypeBuilder<Bundle> b)
    {
        b.ToTable("bundles", "catalog", t => t.HasCheckConstraint("ck_bundles_discount", "discount_percent >= 0 AND discount_percent <= 100 AND cardinality(plan_ids) > 0"));
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(4000);
        b.Property(x => x.DiscountPercent).HasPrecision(9, 6);
    }
}

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> b)
    {
        b.ToTable("plans", "catalog");
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
        b.Ignore(x => x.Cycle);
    }
}

internal sealed class PlanPriceConfiguration : IEntityTypeConfiguration<PlanPrice>
{
    public void Configure(EntityTypeBuilder<PlanPrice> b)
    {
        b.ToTable("plan_prices", "catalog");
        b.Property(x => x.Amount).HasPrecision(18, 6);
        b.Property(x => x.SetupFee).HasPrecision(18, 6);
        b.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId);
        b.HasIndex(x => new { x.PlanId, x.Currency, x.ValidFrom }).IsUnique();
    }
}

internal sealed class TaxRuleConfiguration : IEntityTypeConfiguration<TaxRule>
{
    public void Configure(EntityTypeBuilder<TaxRule> b)
    {
        b.ToTable("tax_rules", "catalog");
        b.Property(x => x.TaxCode).HasMaxLength(50);
        b.Property(x => x.Country).HasMaxLength(2).IsFixedLength();
        b.Property(x => x.Province).HasMaxLength(100);
        b.Property(x => x.Category).HasMaxLength(50);
        b.Property(x => x.Rate).HasPrecision(9, 6);
        b.Property(x => x.Reason).HasMaxLength(50);
        b.Property(x => x.Legend).HasMaxLength(500);
        b.Property(x => x.CreatedBy).HasMaxLength(200);
        b.Property(x => x.CertificateRef).HasMaxLength(200);
        b.Property(x => x.Note).HasMaxLength(500);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasIndex(x => new { x.TaxCode, x.AccountId });
        b.Ignore(x => x.Specificity);
        b.Ignore(x => x.IsAccountRule);
    }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> b)
    {
        b.ToTable("orders", "orders");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.ClientReference).HasMaxLength(200);
        b.Property(x => x.RequestFingerprint).HasMaxLength(64);

        // La clave de idempotencia es única por API key, cuenta y referencia. Sin API key (portal), los nulos cuentan como iguales.
        b.HasIndex(x => new { x.ApiKeyId, x.AccountId, x.ClientReference }).IsUnique().HasFilter("client_reference IS NOT NULL").AreNullsDistinct(false);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<Coupon>().WithMany().HasForeignKey(x => x.CouponId);
        b.HasOne<Bundle>().WithMany().HasForeignKey(x => x.BundleId);
        b.HasOne<Platform.ApiKey>().WithMany().HasForeignKey(x => x.ApiKeyId);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId);
        b.Navigation(x => x.Lines).HasField("lines");
    }
}

internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> b)
    {
        b.ToTable("order_lines", "orders");
        b.Property(x => x.UnitPrice).HasPrecision(18, 6);
        b.Property(x => x.SetupFee).HasPrecision(18, 6);
        b.Property(x => x.Options).HasColumnType("jsonb");
        b.Property(x => x.OrderData).HasColumnType("jsonb");
    }
}

internal sealed class ServiceOptionConfiguration : IEntityTypeConfiguration<ServiceOption>
{
    public void Configure(EntityTypeBuilder<ServiceOption> b)
    {
        b.ToTable("service_options", "services", t => t.HasCheckConstraint("ck_service_options_quantity", "quantity >= 0"));
        b.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId);
        b.HasOne<Option>().WithMany().HasForeignKey(x => x.OptionId);
        b.HasOne<OptionValue>().WithMany().HasForeignKey(x => x.ValueId);
        b.HasIndex(x => new { x.ServiceId, x.OptionId }).IsUnique();
        b.Ignore(x => x.Choice);
    }
}

internal sealed class ServiceChangeConfiguration : IEntityTypeConfiguration<ServiceChange>
{
    public void Configure(EntityTypeBuilder<ServiceChange> b)
    {
        b.ToTable("service_changes", "services");
        b.Property(x => x.FromUnitPrice).HasPrecision(18, 6);
        b.Property(x => x.ToUnitPrice).HasPrecision(18, 6);
        b.Property(x => x.Options).HasColumnType("jsonb");
        b.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId);
        b.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId);

        // Un servicio tiene a lo sumo un cambio esperando su pago.
        b.HasIndex(x => x.ServiceId).IsUnique().HasFilter("status = 'Pending'");
        b.HasIndex(x => x.DocumentId);
        b.Ignore(x => x.Cycle);
    }
}

internal sealed class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
    public void Configure(EntityTypeBuilder<Service> b)
    {
        b.ToTable("services", "services");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Label).HasMaxLength(255);
        b.Property(x => x.UnitPrice).HasPrecision(18, 6);
        b.Property(x => x.IndexedUnitPrice).HasPrecision(18, 6);
        b.Property(x => x.DiscountPercent).HasPrecision(9, 4);
        b.Property(x => x.DiscountAmount).HasPrecision(18, 6);
        b.Property(x => x.ObservedStatus).HasMaxLength(32);
        b.Property(x => x.PendingOperation).HasMaxLength(200);
        b.Property(x => x.PaymentMethod).HasMaxLength(100);
        b.Property(x => x.SuspendReason).HasMaxLength(255);
        b.Property(x => x.ModuleParams).HasColumnType("jsonb");
        b.Property(x => x.ExternalIds).HasColumnType("jsonb");

        // El valor por defecto solo completa las filas que ya existían: los servicios nuevos traen el suyo.
        b.Property(x => x.Uuid).HasDefaultValueSql("gen_random_uuid()");
        b.HasIndex(x => x.Uuid).IsUnique();
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
        b.HasOne<Service>().WithMany().HasForeignKey(x => x.ParentServiceId);
        b.HasOne<Coupon>().WithMany().HasForeignKey(x => x.CouponId);
        b.HasIndex(x => x.NextDueDate);
        b.HasIndex(x => x.AccountId);

        // Una prueba gratis por cuenta y producto, aunque lleguen dos pedidos a la vez.
        b.HasIndex(x => new { x.AccountId, x.ProductId }).IsUnique().HasFilter("trial_ends_on IS NOT NULL").HasDatabaseName("ix_services_one_trial");
        b.Ignore(x => x.Cycle);
        b.Ignore(x => x.RecurringAmount);
    }
}

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> b)
    {
        b.ToTable("documents", "billing", t =>
        {
            t.HasCheckConstraint("ck_documents_amounts", "allocated >= 0 AND credited >= 0 AND (issue_date IS NULL OR allocated + credited <= total)");
            t.HasCheckConstraint("ck_documents_refunded", "refunded >= 0 AND refunded <= credited");
        });
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.Receiver).HasColumnType("jsonb");
        b.Property(x => x.Aliquots).HasColumnType("jsonb");
        b.Property(x => x.Taxes).HasColumnType("jsonb");
        b.Property(x => x.Legends).HasColumnType("jsonb");
        b.Property(x => x.SourceStatus).HasMaxLength(32);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<Document>().WithMany().HasForeignKey(x => x.RelatedDocumentId);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.DocumentId);
        b.Navigation(x => x.Lines).HasField("lines");
        b.HasIndex(x => new { x.Kind, x.Number }).IsUnique().HasFilter("number IS NOT NULL");
        b.HasIndex(x => new { x.AccountId, x.Currency });
        b.Ignore(x => x.IsIssued);
        b.Ignore(x => x.Status);
        b.Ignore(x => x.Pending);
    }
}

internal sealed class DocumentLineConfiguration : IEntityTypeConfiguration<DocumentLine>
{
    public void Configure(EntityTypeBuilder<DocumentLine> b)
    {
        b.ToTable("document_lines", "billing");
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Quantity).HasPrecision(18, 4);
        b.Property(x => x.UnitPrice).HasPrecision(18, 6);
        b.Property(x => x.Rate).HasPrecision(9, 6);
        b.Property(x => x.DedupKey).HasMaxLength(200);
        b.Property(x => x.Category).HasMaxLength(50);
        b.Property(x => x.Reason).HasMaxLength(50);
        b.Property(x => x.Legend).HasMaxLength(500);
        b.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId);
        b.HasOne<Domain.Domains.RegisteredDomain>().WithMany().HasForeignKey(x => x.DomainId);
        b.HasIndex(x => x.DedupKey).IsUnique().HasFilter("dedup_key IS NOT NULL");
    }
}

internal sealed class BilledPeriodConfiguration : IEntityTypeConfiguration<BilledPeriod>
{
    public void Configure(EntityTypeBuilder<BilledPeriod> b)
    {
        // La restricción de exclusión sobre el rango está en SQL, en la migración inicial.
        b.ToTable("billed_periods", "billing", t => t.HasCheckConstraint("ck_billed_periods_range", "\"end\" > start"));
        b.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId);
        b.HasOne(x => x.Line).WithMany().HasForeignKey(x => x.DocumentLineId);
    }
}

internal sealed class BillableItemConfiguration : IEntityTypeConfiguration<BillableItem>
{
    public void Configure(EntityTypeBuilder<BillableItem> b)
    {
        b.ToTable("billable_items", "billing", t => t.HasCheckConstraint("ck_billable_items_invoiced", "(status = 'Invoiced') = (document_id IS NOT NULL) AND quantity > 0"));
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Quantity).HasPrecision(18, 4);
        b.Property(x => x.UnitPrice).HasPrecision(18, 6);
        b.Property(x => x.Actor).HasMaxLength(200);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId);
        b.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId);

        // La corrida busca lo pendiente por fecha: el índice parcial no crece con lo ya facturado.
        b.HasIndex(x => new { x.InvoiceOn, x.AccountId }).HasFilter("status = 'Pending'");
        b.HasIndex(x => x.AccountId);
        b.Ignore(x => x.DedupKey);
    }
}

internal sealed class QuoteConfiguration : IEntityTypeConfiguration<Quote>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public void Configure(EntityTypeBuilder<Quote> b)
    {
        b.ToTable("quotes", "billing", t => t.HasCheckConstraint("ck_quotes_converted", "(status = 'Ordered') = (order_id IS NOT NULL) AND (status <> 'Invoiced' OR document_id IS NOT NULL)"));
        b.Property(x => x.Subject).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(4000);
        b.Property(x => x.Actor).HasMaxLength(200);

        // Los renglones viajan siempre juntos y nadie los busca por separado: una columna jsonb.
        b.Property(x => x.Lines).HasColumnType("jsonb").HasConversion(
            v => JsonSerializer.Serialize(v, Json),
            v => JsonSerializer.Deserialize<List<QuoteLine>>(v, Json) ?? new List<QuoteLine>(),
            new ValueComparer<IReadOnlyList<QuoteLine>>((a, c) => a!.SequenceEqual(c!), v => v.Count, v => v.ToList()));
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId);
        b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId);
        b.HasIndex(x => x.AccountId);
        b.Ignore(x => x.IsOpen);
        b.Ignore(x => x.HasPlans);
    }
}

internal sealed class ExchangeRateConfiguration : IEntityTypeConfiguration<ExchangeRate>
{
    public void Configure(EntityTypeBuilder<ExchangeRate> b)
    {
        b.ToTable("exchange_rates", "billing", t => t.HasCheckConstraint("ck_exchange_rates_rate", "rate > 0 AND currency <> quote"));
        b.Property(x => x.Rate).HasPrecision(18, 6);
        b.Property(x => x.Source).HasMaxLength(32);
        b.Property(x => x.Actor).HasMaxLength(200);
        b.HasIndex(x => new { x.Currency, x.Quote, x.Date });
    }
}

internal sealed class CreditEntryConfiguration : IEntityTypeConfiguration<CreditEntry>
{
    public void Configure(EntityTypeBuilder<CreditEntry> b)
    {
        b.ToTable("credit_entries", "billing");
        b.Property(x => x.Reason).HasMaxLength(200);
        b.Property(x => x.Actor).HasMaxLength(200);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasIndex(x => new { x.AccountId, x.Currency });
    }
}

internal sealed class CreditBalanceConfiguration : IEntityTypeConfiguration<CreditBalance>
{
    public void Configure(EntityTypeBuilder<CreditBalance> b)
    {
        b.ToTable("credit_balances", "billing", t => t.HasCheckConstraint("ck_credit_balances_positive", "balance >= 0"));
        b.HasKey(x => new { x.AccountId, x.Currency });
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("payments", "payments", t =>
        {
            t.HasCheckConstraint("ck_payments_applied", "applied >= 0 AND applied <= amount");
            t.HasCheckConstraint("ck_payments_refunded", "refunded >= 0 AND refunded <= amount");
        });
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Channel).HasMaxLength(100);
        b.Property(x => x.ExternalId).HasMaxLength(200);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasIndex(x => new { x.Channel, x.ExternalId }).IsUnique().HasFilter("external_id IS NOT NULL");

        // platform.metrics() cuenta solo los que alertan: el índice parcial no crece con los aprobados.
        b.HasIndex(x => x.Status).HasDatabaseName("ix_payments_open").HasFilter("status IN ('Pending', 'Disputed')");
        b.Ignore(x => x.Unapplied);
        b.Ignore(x => x.Kept);
        b.Ignore(x => x.Collected);
    }
}

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> b)
    {
        b.ToTable("refunds", "payments", t => t.HasCheckConstraint("ck_refunds_amounts", "amount > 0 AND uncovered >= 0 AND uncovered <= amount"));
        b.Property(x => x.ExternalId).HasMaxLength(200);
        b.Property(x => x.Error).HasMaxLength(2000);
        b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId);
        b.HasOne<Document>().WithMany().HasForeignKey(x => x.CreditNoteId);
        b.HasIndex(x => new { x.PaymentId, x.Status });
    }
}

internal sealed class PaymentAttemptConfiguration : IEntityTypeConfiguration<PaymentAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentAttempt> b)
    {
        b.ToTable("attempts", "payments");
        b.Property(x => x.Reference).HasMaxLength(64);
        b.Property(x => x.Amount).HasPrecision(18, 2);
        b.Property(x => x.RedirectUrl).HasMaxLength(2000);
        b.Property(x => x.Rate).HasPrecision(18, 8);
        b.HasOne<ExchangeRate>().WithMany().HasForeignKey(x => x.ExchangeRateId);
        b.Property(x => x.Consent).HasColumnType("jsonb");
        b.Property(x => x.Outcome).HasMaxLength(200);
        b.Property(x => x.Retry).HasMaxLength(32);
        b.Property(x => x.CheckoutExternalId).HasMaxLength(200);
        b.HasIndex(x => x.Reference).IsUnique();
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<ModuleInstance>().WithMany().HasForeignKey(x => x.ModuleInstanceId);
        b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId);
        b.HasOne<SavedPaymentMethod>().WithMany().HasForeignKey(x => x.SavedMethodId);
    }
}

internal sealed class WebhookReceiptConfiguration : IEntityTypeConfiguration<WebhookReceipt>
{
    public void Configure(EntityTypeBuilder<WebhookReceipt> b)
    {
        b.ToTable("webhook_receipts", "payments");
        b.Property(x => x.Headers).HasColumnType("jsonb");
        b.Property(x => x.Query).HasColumnType("jsonb");
        b.HasOne<ModuleInstance>().WithMany().HasForeignKey(x => x.ModuleInstanceId);

        // La retención vacía los cuerpos viejos: el índice cubre solo las filas que todavía tienen cuerpo.
        b.HasIndex(x => x.ReceivedAt).HasDatabaseName("ix_webhook_receipts_with_body").HasFilter("octet_length(body) > 0");
    }
}

internal sealed class GatewayEventConfiguration : IEntityTypeConfiguration<GatewayEvent>
{
    public void Configure(EntityTypeBuilder<GatewayEvent> b)
    {
        b.ToTable("gateway_events", "payments");
        b.Property(x => x.EventId).HasMaxLength(200);
        b.Property(x => x.Type).HasMaxLength(100);
        b.Property(x => x.PaymentExternalId).HasMaxLength(200);

        // La unicidad va acá y no en la recepción: si no, un pedido falso podría ocupar el ID de un evento real.
        b.HasIndex(x => new { x.ModuleInstanceId, x.EventId }).IsUnique();
        b.HasIndex(x => x.CreatedAt);
        b.HasOne<ModuleInstance>().WithMany().HasForeignKey(x => x.ModuleInstanceId);
        b.HasOne<WebhookReceipt>().WithMany().HasForeignKey(x => x.ReceiptId);
    }
}

/// <summary>
/// Pago que una pasarela informó sin un intento propio (sección 8 del modelo de dominio): puede ser de otro
/// sistema que usa la misma cuenta, o un cobro posterior a una restauración. Queda a la vista del staff con alerta.
/// </summary>
internal sealed class UnmatchedPaymentConfiguration : IEntityTypeConfiguration<UnmatchedPayment>
{
    public void Configure(EntityTypeBuilder<UnmatchedPayment> b)
    {
        b.ToTable("unmatched", "payments");
        b.Property(x => x.PaymentExternalId).HasMaxLength(200);
        b.Property(x => x.ExternalReference).HasMaxLength(256);
        b.Property(x => x.Status).HasMaxLength(32);
        b.Property(x => x.Amount).HasPrecision(18, 2);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.HasIndex(x => new { x.ModuleInstanceId, x.PaymentExternalId }).IsUnique();
        b.HasIndex(x => x.Id).HasDatabaseName("ix_unmatched_open").HasFilter("resolved_at IS NULL");
        b.HasOne<ModuleInstance>().WithMany().HasForeignKey(x => x.ModuleInstanceId);
    }
}

internal sealed class AllocationConfiguration : IEntityTypeConfiguration<Allocation>
{
    public void Configure(EntityTypeBuilder<Allocation> b)
    {
        b.ToTable("allocations", "payments", t => t.HasCheckConstraint("ck_allocations_source", "(payment_id IS NULL) <> (credit_entry_id IS NULL) AND amount > 0"));
        b.Property(x => x.Rate).HasPrecision(18, 8);
        b.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId);
        b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId);
        b.HasOne<CreditEntry>().WithMany().HasForeignKey(x => x.CreditEntryId);
        b.Ignore(x => x.PaymentShare);
    }
}

internal sealed class ModuleInstallationConfiguration : IEntityTypeConfiguration<ModuleInstallation>
{
    public void Configure(EntityTypeBuilder<ModuleInstallation> b)
    {
        b.ToTable("module_installations", "platform");
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.BaseUrl).HasMaxLength(500);
        b.Property(x => x.TlsFingerprint).HasMaxLength(100);
        b.Property(x => x.Manifest).HasColumnType("jsonb");
        b.Property(x => x.PendingManifest).HasColumnType("jsonb");
        b.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class ModuleInstanceConfiguration : IEntityTypeConfiguration<ModuleInstance>
{
    public void Configure(EntityTypeBuilder<ModuleInstance> b)
    {
        b.ToTable("module_instances", "platform");
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Config).HasColumnType("jsonb");
        b.Property(x => x.BrowserHosts).HasColumnType("jsonb");
        b.HasOne<ModuleInstallation>().WithMany().HasForeignKey(x => x.InstallationId);
    }
}

internal sealed class ServiceSecretConfiguration : IEntityTypeConfiguration<ServiceSecret>
{
    public void Configure(EntityTypeBuilder<ServiceSecret> b)
    {
        b.ToTable("service_secrets", "services");
        b.Property(x => x.Name).HasMaxLength(100);
        b.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId);
        b.HasIndex(x => new { x.ServiceId, x.Name }).IsUnique();
    }
}

internal sealed class IssuerProfileConfiguration : IEntityTypeConfiguration<Domain.Fiscal.IssuerProfile>
{
    public void Configure(EntityTypeBuilder<Domain.Fiscal.IssuerProfile> b)
    {
        b.ToTable("issuers", "fiscal");
        b.Property(x => x.TaxId).HasMaxLength(20);
        b.Property(x => x.VatCondition).HasDefaultValue(1);
        b.Property(x => x.LegalName).HasMaxLength(200);
        b.Property(x => x.TypeMap).HasColumnType("jsonb");
        b.Property(x => x.ClassAConditions).HasColumnType("jsonb");
        b.Property(x => x.Settings).HasColumnType("jsonb");
        b.Property(x => x.LastTest).HasColumnType("jsonb");
        b.Property(x => x.KnownPointsOfSale).HasColumnType("jsonb");
        b.HasOne<ModuleInstance>().WithMany().HasForeignKey(x => x.ModuleInstanceId);
    }
}

internal sealed class FiscalSeriesConfiguration : IEntityTypeConfiguration<Domain.Fiscal.FiscalSeries>
{
    public void Configure(EntityTypeBuilder<Domain.Fiscal.FiscalSeries> b)
    {
        b.ToTable("series", "fiscal");

        // Token de concurrencia (xmin): un escritor atrasado no pisa el último número de otro.
        b.Property<uint>("Version").IsRowVersion();
        b.HasOne<Domain.Fiscal.IssuerProfile>().WithMany().HasForeignKey(x => x.IssuerId);
        b.HasIndex(x => new { x.IssuerId, x.PointOfSale, x.TypeCode }).IsUnique();
    }
}

internal sealed class FiscalEmissionConfiguration : IEntityTypeConfiguration<Domain.Fiscal.FiscalEmission>
{
    public void Configure(EntityTypeBuilder<Domain.Fiscal.FiscalEmission> b)
    {
        b.ToTable("emissions", "fiscal");

        // Token de concurrencia (xmin): un escritor atrasado no pisa el estado que dejó otro.
        b.Property<uint>("Version").IsRowVersion();
        b.Property(x => x.Codes).HasColumnType("jsonb");

        // Un ID de pedido usado por otra emisión haría que el fisco devuelva aquel comprobante.
        b.HasIndex(x => new { x.IssuerId, x.RequestId }).IsUnique();
        b.Property(x => x.LastError).HasMaxLength(2000);
        b.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId);
        b.HasOne<Domain.Fiscal.IssuerProfile>().WithMany().HasForeignKey(x => x.IssuerId);
        b.HasIndex(x => x.DocumentId).IsUnique();

        // platform.metrics() cuenta las emisiones que no terminaron bien: el índice parcial no crece con las autorizadas.
        b.HasIndex(x => x.Status).HasDatabaseName("ix_emissions_open").HasFilter("status <> 'Authorized'");
    }
}

/// <summary>Hallazgo de la conciliación (D-14): lo que el fisco, la pasarela o un módulo tienen y la base no, o al revés.</summary>
internal sealed class ReconcileFindingConfiguration : IEntityTypeConfiguration<ReconcileFinding>
{
    public void Configure(EntityTypeBuilder<ReconcileFinding> b)
    {
        b.ToTable("reconcile_findings", "platform");
        b.Property(x => x.Kind).HasMaxLength(50);
        b.Property(x => x.Subject).HasMaxLength(200);
        b.Property(x => x.Details).HasColumnType("jsonb");

        // Un mismo hallazgo abierto no se repite aunque la conciliación corra varias veces.
        b.HasIndex(x => new { x.Kind, x.Subject }).IsUnique().HasDatabaseName("ix_reconcile_findings_open").HasFilter("resolved_at IS NULL");
    }
}

/// <summary>Línea de tiempo de la base que vio el planificador por última vez: cambia con cada promoción o restauración.</summary>
internal sealed class TimelineMarkConfiguration : IEntityTypeConfiguration<TimelineMark>
{
    public void Configure(EntityTypeBuilder<TimelineMark> b)
    {
        b.ToTable("timeline", "platform", t => t.HasCheckConstraint("ck_timeline_single", "id = 1"));
        b.Property(x => x.Id).ValueGeneratedNever();
    }
}

internal sealed class FiscalDocumentConfiguration : IEntityTypeConfiguration<Domain.Fiscal.FiscalDocument>
{
    public void Configure(EntityTypeBuilder<Domain.Fiscal.FiscalDocument> b)
    {
        b.ToTable("documents", "fiscal");
        b.Property(x => x.AuthorizationCode).HasMaxLength(50);
        b.Property(x => x.Payload).HasColumnType("jsonb");
        b.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId);
        b.HasOne<Domain.Fiscal.IssuerProfile>().WithMany().HasForeignKey(x => x.IssuerId);
        b.HasIndex(x => new { x.IssuerId, x.PointOfSale, x.TypeCode, x.Number }).IsUnique();
        b.HasIndex(x => x.DocumentId).IsUnique().HasFilter("NOT imported");
    }
}

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> b)
    {
        b.ToTable("api_keys", "platform", t => t.HasCheckConstraint("ck_api_keys_requests_per_minute", "requests_per_minute >= 1"));
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Prefix).HasMaxLength(20);
        b.Property(x => x.SecretHash).HasMaxLength(64);
        b.Property(x => x.Scopes).HasColumnType("jsonb");
        b.Property(x => x.AllowedIps).HasColumnType("jsonb");
        b.HasIndex(x => x.Prefix).IsUnique();
    }
}

internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> b)
    {
        b.ToTable("jobs", "platform");
        b.Property(x => x.Kind).HasMaxLength(100);
        b.Property(x => x.Payload).HasColumnType("jsonb");
        b.Property(x => x.EntityKey).HasMaxLength(100);
        b.Property(x => x.DedupKey).HasMaxLength(200);
        b.Property(x => x.LockedBy).HasMaxLength(100);
        b.Property(x => x.TraceParent).HasMaxLength(55);
        b.Property(x => x.ActorKind).HasMaxLength(32);
        b.Property(x => x.Actor).HasMaxLength(200);
        b.HasIndex(x => x.DedupKey).IsUnique().HasFilter("dedup_key IS NOT NULL");

        // Índices parciales sobre lo vivo, con el mismo predicado que la toma: no crecen con el historial (D-07).
        b.HasIndex(x => new { x.RunAt, x.Id }).HasDatabaseName("ix_jobs_live_run_at").HasFilter("status IN ('Pending', 'Running')");
        b.HasIndex(x => new { x.EntityKey, x.Id }).HasDatabaseName("ix_jobs_live_entity_key").HasFilter("status IN ('Pending', 'Running') AND entity_key IS NOT NULL");
    }
}

internal sealed class StaffTaskConfiguration : IEntityTypeConfiguration<UseCases.StaffTask>
{
    public void Configure(EntityTypeBuilder<UseCases.StaffTask> b)
    {
        b.ToTable("staff_tasks", "platform");
        b.Property(x => x.Kind).HasMaxLength(32);
        b.Property(x => x.Operation).HasMaxLength(32);
        b.Property(x => x.IdempotencyKey).HasMaxLength(200);
        b.Property(x => x.Fingerprint).HasMaxLength(64);
        b.Property(x => x.Subject).HasMaxLength(100);
        b.Property(x => x.Details).HasColumnType("jsonb");
        b.Property(x => x.Input).HasColumnType("jsonb");
        b.Property(x => x.Result).HasColumnType("jsonb");
        b.Property(x => x.Reason).HasMaxLength(2000);
        b.HasOne<ModuleInstance>().WithMany().HasForeignKey(x => x.ModuleInstanceId);

        // La clave de idempotencia es única por instancia, y un servicio o dominio tiene a lo sumo una tarea abierta.
        b.HasIndex(x => new { x.ModuleInstanceId, x.IdempotencyKey }).IsUnique();
        b.HasIndex(x => x.Subject).IsUnique().HasFilter("status = 'Open'");
        b.HasIndex(x => x.Status);
    }
}

internal sealed class RegisteredDomainConfiguration : IEntityTypeConfiguration<Domain.Domains.RegisteredDomain>
{
    public void Configure(EntityTypeBuilder<Domain.Domains.RegisteredDomain> b)
    {
        b.ToTable("domains", "domains");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Name).HasMaxLength(255);
        b.Property(x => x.RecurringAmount).HasPrecision(18, 2);
        b.Property(x => x.Nameservers).HasColumnType("jsonb");
        b.Property(x => x.ExternalIds).HasColumnType("jsonb");
        b.Property(x => x.PaymentMethod).HasMaxLength(100);
        b.Property(x => x.RegistryStatus).HasMaxLength(32);
        b.Property(x => x.Registrant).HasColumnType("jsonb");
        b.Property(x => x.OptionPrices).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<ModuleInstance>().WithMany().HasForeignKey(x => x.RegistrarInstanceId);

        // Un nombre vive en una sola cuenta a la vez: dos pedidos del mismo dominio no pueden convivir.
        b.HasIndex(x => x.Name).IsUnique().HasFilter("status IN ('Pending', 'Active', 'Grace')").HasDatabaseName("ux_domains_live_name");
        b.HasIndex(x => x.NextDueDate);
        b.Ignore(x => x.IsLive);
    }
}

internal sealed class DomainOperationConfiguration : IEntityTypeConfiguration<Domain.Domains.DomainOperation>
{
    public void Configure(EntityTypeBuilder<Domain.Domains.DomainOperation> b)
    {
        b.ToTable("operations", "domains");
        b.Property(x => x.Operation).HasMaxLength(32);
        b.Property(x => x.Input).HasColumnType("jsonb");
        b.Property(x => x.Result).HasColumnType("jsonb");
        b.Property(x => x.DedupKey).HasMaxLength(100);
        b.Property(x => x.PendingOperation).HasMaxLength(200);
        b.Property(x => x.LastError).HasMaxLength(2000);
        b.HasOne<Domain.Domains.RegisteredDomain>().WithMany().HasForeignKey(x => x.DomainId);
        b.HasIndex(x => new { x.DomainId, x.DedupKey }).IsUnique();
    }
}

internal sealed class TldPriceConfiguration : IEntityTypeConfiguration<Domain.Domains.TldPrice>
{
    public void Configure(EntityTypeBuilder<Domain.Domains.TldPrice> b)
    {
        b.ToTable("tld_prices", "catalog", t => t.HasCheckConstraint("ck_tld_prices_years", "years BETWEEN 1 AND 10"));
        b.Property(x => x.Tld).HasMaxLength(63);
        b.Property(x => x.Register).HasPrecision(18, 2);
        b.Property(x => x.Renew).HasPrecision(18, 2);
        b.Property(x => x.Transfer).HasPrecision(18, 2);
        b.HasOne<ModuleInstance>().WithMany().HasForeignKey(x => x.RegistrarInstanceId);
        b.HasIndex(x => new { x.Tld, x.RegistrarInstanceId, x.Currency, x.Years, x.ValidFrom }).IsUnique();
    }
}

internal sealed class DomainOptionPriceConfiguration : IEntityTypeConfiguration<Domain.Domains.DomainOptionPrice>
{
    public void Configure(EntityTypeBuilder<Domain.Domains.DomainOptionPrice> b)
    {
        b.ToTable("domain_option_prices", "catalog", t => t.HasCheckConstraint("ck_domain_option_prices_price", "price >= 0"));
        b.Property(x => x.Option).HasMaxLength(32);
        b.Property(x => x.Price).HasPrecision(18, 2);
        b.HasIndex(x => new { x.Option, x.Currency, x.ValidFrom }).IsUnique();
    }
}

internal sealed class TldOptionsConfiguration : IEntityTypeConfiguration<Domain.Domains.TldOptions>
{
    public void Configure(EntityTypeBuilder<Domain.Domains.TldOptions> b)
    {
        b.ToTable("tld_options", "catalog");
        b.HasKey(x => x.Tld);
        b.Property(x => x.Tld).HasMaxLength(63);
    }
}
