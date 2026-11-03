using BillingOss.App.Platform;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingOss.App.Persistence;

// Cobros incluidos (sección 4 del diseño de cobros): medios guardados, suscripciones, cómo se ofrece cada pasarela y
// transferencias. Todo en el esquema payments.

internal sealed class SavedPaymentMethodConfiguration : IEntityTypeConfiguration<SavedPaymentMethod>
{
    public void Configure(EntityTypeBuilder<SavedPaymentMethod> b)
    {
        b.ToTable("saved_methods", "payments");
        b.Property(x => x.CustomerExternalId).HasMaxLength(200);
        b.Property(x => x.MethodExternalId).HasMaxLength(200);
        b.Property(x => x.Type).HasMaxLength(32);
        b.Property(x => x.Brand).HasMaxLength(32);
        b.Property(x => x.Last4).HasMaxLength(4);
        b.Property(x => x.Consent).HasColumnType("jsonb");
        b.HasIndex(x => new { x.ModuleInstanceId, x.MethodExternalId }).IsUnique();

        // A lo sumo un medio del débito automático por cuenta.
        b.HasIndex(x => x.AccountId).IsUnique().HasFilter("auto_charge").HasDatabaseName("ix_saved_methods_auto_charge");
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<ModuleInstance>().WithMany().HasForeignKey(x => x.ModuleInstanceId);
    }
}

internal sealed class GatewaySubscriptionConfiguration : IEntityTypeConfiguration<GatewaySubscription>
{
    public void Configure(EntityTypeBuilder<GatewaySubscription> b)
    {
        b.ToTable("subscriptions", "payments", t => t.HasCheckConstraint("ck_subscriptions_amount", "amount >= 0"));
        b.Property(x => x.Reference).HasMaxLength(64);
        b.Property(x => x.ExternalId).HasMaxLength(200);
        b.Property(x => x.RedirectUrl).HasMaxLength(2000);
        b.Property(x => x.Consent).HasColumnType("jsonb");
        b.HasIndex(x => x.Reference).IsUnique();
        b.HasIndex(x => new { x.ModuleInstanceId, x.ExternalId }).IsUnique().HasFilter("external_id IS NOT NULL");
        b.HasIndex(x => new { x.AccountId, x.Status });

        // A lo sumo una suscripción vigente por cuenta: el lock de la cuenta ordena los pedidos y la base es la última palabra.
        b.HasIndex(x => x.AccountId).IsUnique().HasFilter("status <> 'Cancelled'").HasDatabaseName("ix_subscriptions_live");
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<ModuleInstance>().WithMany().HasForeignKey(x => x.ModuleInstanceId);
    }
}

internal sealed class GatewaySetupConfiguration : IEntityTypeConfiguration<GatewaySetup>
{
    public void Configure(EntityTypeBuilder<GatewaySetup> b)
    {
        b.ToTable("gateways", "payments");
        b.HasKey(x => x.ModuleInstanceId);
        b.Property(x => x.ModuleInstanceId).ValueGeneratedNever();
        b.Property(x => x.DisplayName).HasMaxLength(100);
        b.Property(x => x.Environment).HasMaxLength(16);
        b.Property(x => x.LastTestMessage).HasMaxLength(2000);
        b.HasOne<ModuleInstance>().WithOne().HasForeignKey<GatewaySetup>(x => x.ModuleInstanceId);
    }
}

internal sealed class TransferAccountConfiguration : IEntityTypeConfiguration<TransferAccount>
{
    public void Configure(EntityTypeBuilder<TransferAccount> b)
    {
        b.ToTable("transfer_accounts", "payments");
        b.Property(x => x.Bank).HasMaxLength(100);
        b.Property(x => x.Holder).HasMaxLength(200);
        b.Property(x => x.Cuit).HasMaxLength(11);
        b.Property(x => x.Cbu).HasMaxLength(22);
        b.Property(x => x.Alias).HasMaxLength(50);
        b.Property(x => x.CsvMap).HasColumnType("jsonb");
    }
}

internal sealed class BankMovementConfiguration : IEntityTypeConfiguration<BankMovement>
{
    public void Configure(EntityTypeBuilder<BankMovement> b)
    {
        b.ToTable("bank_movements", "payments", t => t.HasCheckConstraint("ck_bank_movements_amount", "amount > 0"));
        b.Property(x => x.Reference).HasMaxLength(200);
        b.Property(x => x.PayerCuit).HasMaxLength(11);
        b.Property(x => x.PayerName).HasMaxLength(200);
        b.Property(x => x.Concept).HasMaxLength(500);

        // Reimportar el mismo extracto no duplica.
        b.HasIndex(x => new { x.TransferAccountId, x.Reference }).IsUnique();
        b.HasIndex(x => x.Status);
        b.HasOne<TransferAccount>().WithMany().HasForeignKey(x => x.TransferAccountId);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId);
    }
}

internal sealed class TransferNoticeConfiguration : IEntityTypeConfiguration<TransferNotice>
{
    public void Configure(EntityTypeBuilder<TransferNotice> b)
    {
        b.ToTable("transfer_notices", "payments", t => t.HasCheckConstraint("ck_transfer_notices_amount", "amount > 0"));
        b.Property(x => x.Reference).HasMaxLength(200);
        b.Property(x => x.ReceiptName).HasMaxLength(200);
        b.Property(x => x.ReceiptType).HasMaxLength(100);
        b.HasIndex(x => new { x.AccountId, x.Status });
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<BankMovement>().WithMany().HasForeignKey(x => x.MovementId);
    }
}
