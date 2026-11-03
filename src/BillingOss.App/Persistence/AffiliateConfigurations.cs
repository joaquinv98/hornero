using BillingOss.Domain.Accounts;
using BillingOss.Domain.Affiliates;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingOss.App.Persistence;

/// <summary>Esquema affiliates (diseno/afiliados.md, sección 6). Lo que EF no expresa está en <see cref="AffiliatesSql"/>.</summary>
internal sealed class AffiliateProgramConfiguration : IEntityTypeConfiguration<AffiliateProgram>
{
    public void Configure(EntityTypeBuilder<AffiliateProgram> b)
    {
        b.ToTable("program", "affiliates", t =>
        {
            t.HasCheckConstraint("ck_program_single", "id = 1");
            t.HasCheckConstraint("ck_program_days", "cookie_days BETWEEN 1 AND 365 AND hold_days BETWEEN 0 AND 365");
        });
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.MinimumPayouts).HasColumnType("jsonb");
        b.Property(x => x.SignupBonuses).HasColumnType("jsonb");
        b.Property(x => x.PayoutNotifyEmail).HasMaxLength(320);
        b.Property(x => x.Links).HasMaxLength(4000);
    }
}

internal sealed class CommissionRuleConfiguration : IEntityTypeConfiguration<CommissionRule>
{
    public void Configure(EntityTypeBuilder<CommissionRule> b)
    {
        b.ToTable("commission_rules", "affiliates", t =>
        {
            t.HasCheckConstraint("ck_commission_rules_currency", "(kind = 'Fixed') = (currency IS NOT NULL)");
            t.HasCheckConstraint("ck_commission_rules_value", "kind <> 'Percent' OR (value > 0 AND value <= 100)");
        });
        b.Property(x => x.Value).HasPrecision(18, 4);
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
        b.HasOne<Affiliate>().WithMany().HasForeignKey(x => x.AffiliateId);
        b.HasIndex(x => new { x.ProductId, x.AffiliateId, x.Currency }).IsUnique().AreNullsDistinct(false);
    }
}

internal sealed class AffiliateConfiguration : IEntityTypeConfiguration<Affiliate>
{
    public void Configure(EntityTypeBuilder<Affiliate> b)
    {
        b.ToTable("affiliates", "affiliates");
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        // El código se guarda en minúsculas: el índice único lo hace único sin distinguir mayúsculas.
        b.Property(x => x.Code).HasMaxLength(32);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasIndex(x => x.AccountId).IsUnique();
        b.HasIndex(x => x.Status);
        b.Ignore(x => x.IsActive);
    }
}

internal sealed class AffiliateClickConfiguration : IEntityTypeConfiguration<AffiliateClick>
{
    public void Configure(EntityTypeBuilder<AffiliateClick> b)
    {
        b.ToTable("clicks", "affiliates");
        b.Property(x => x.ReferrerHost).HasMaxLength(255);
        b.HasOne<Affiliate>().WithMany().HasForeignKey(x => x.AffiliateId);
        b.HasIndex(x => new { x.AffiliateId, x.At });
    }
}

internal sealed class AffiliateAttributionConfiguration : IEntityTypeConfiguration<AffiliateAttribution>
{
    public void Configure(EntityTypeBuilder<AffiliateAttribution> b)
    {
        b.ToTable("attributions", "affiliates");
        b.HasKey(x => x.AccountId);
        b.Property(x => x.AccountId).ValueGeneratedNever();
        b.HasOne<Account>().WithOne().HasForeignKey<AffiliateAttribution>(x => x.AccountId);
        b.HasOne<Affiliate>().WithMany().HasForeignKey(x => x.AffiliateId);
        b.HasIndex(x => x.AffiliateId);
    }
}

internal sealed class AffiliateReferralConfiguration : IEntityTypeConfiguration<AffiliateReferral>
{
    public void Configure(EntityTypeBuilder<AffiliateReferral> b)
    {
        b.ToTable("referrals", "affiliates");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasOne<Affiliate>().WithMany().HasForeignKey(x => x.AffiliateId);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<Service>().WithOne().HasForeignKey<AffiliateReferral>(x => x.ServiceId);
        b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId);
        b.HasIndex(x => x.ServiceId).IsUnique();
        b.HasIndex(x => x.AffiliateId);
    }
}

internal sealed class AffiliateCommissionConfiguration : IEntityTypeConfiguration<AffiliateCommission>
{
    public void Configure(EntityTypeBuilder<AffiliateCommission> b)
    {
        b.ToTable("commissions", "affiliates", t => t.HasCheckConstraint("ck_commissions_amounts", "amount > 0 AND reversed >= 0 AND reversed <= amount"));
        b.Property(x => x.Rate).HasPrecision(18, 4);
        b.HasOne<Affiliate>().WithMany().HasForeignKey(x => x.AffiliateId);
        b.HasOne<AffiliateReferral>().WithMany().HasForeignKey(x => x.ReferralId);
        b.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId);
        b.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId);

        // Una sola comisión vigente por factura y servicio: repetir el aviso de pago no duplica, y una revertida deja
        // lugar a la de un pago nuevo.
        b.HasIndex(x => new { x.DocumentId, x.ServiceId }).IsUnique().HasFilter("status <> 'Reversed' AND document_id IS NOT NULL");
        b.HasIndex(x => new { x.Status, x.ReleaseOn });
        b.HasIndex(x => x.AffiliateId);
        b.Ignore(x => x.Remaining);
    }
}

internal sealed class AffiliateLedgerEntryConfiguration : IEntityTypeConfiguration<AffiliateLedgerEntry>
{
    public void Configure(EntityTypeBuilder<AffiliateLedgerEntry> b)
    {
        b.ToTable("ledger_entries", "affiliates", t => t.HasCheckConstraint("ck_ledger_entries_amount", "amount <> 0"));
        b.HasOne<Affiliate>().WithMany().HasForeignKey(x => x.AffiliateId);
        b.HasOne<AffiliateCommission>().WithMany().HasForeignKey(x => x.CommissionId);
        b.HasOne<AffiliatePayout>().WithMany().HasForeignKey(x => x.PayoutId);
        b.HasIndex(x => new { x.AffiliateId, x.Currency });

        // El bono de alta se acredita una sola vez por afiliado, aunque dos aprobaciones lleguen a la vez.
        b.HasIndex(x => x.AffiliateId, "ix_ledger_entries_bonus").IsUnique().HasFilter("kind = 'Bonus'").HasDatabaseName("ix_ledger_entries_bonus");
    }
}

internal sealed class AffiliatePayoutConfiguration : IEntityTypeConfiguration<AffiliatePayout>
{
    public void Configure(EntityTypeBuilder<AffiliatePayout> b)
    {
        b.ToTable("payouts", "affiliates", t => t.HasCheckConstraint("ck_payouts_amount", "amount > 0"));
        b.Property(x => x.Details).HasMaxLength(500);
        b.Property(x => x.Reference).HasMaxLength(200);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasOne<Affiliate>().WithMany().HasForeignKey(x => x.AffiliateId);
        b.HasOne<CreditEntry>().WithMany().HasForeignKey(x => x.CreditEntryId);
        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.AffiliateId);
    }
}

/// <summary>Lo que EF no expresa en el esquema affiliates: el libro y los clics solo se agregan.</summary>
internal static class AffiliatesSql
{
    public const string Up = """
        CREATE FUNCTION affiliates.append_only() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          RAISE EXCEPTION 'La tabla % solo se agrega', TG_TABLE_NAME USING ERRCODE = 'check_violation';
        END $$;
        CREATE TRIGGER ledger_entries_append_only BEFORE UPDATE OR DELETE ON affiliates.ledger_entries
          FOR EACH ROW EXECUTE FUNCTION affiliates.append_only();
        CREATE TRIGGER clicks_append_only BEFORE UPDATE OR DELETE ON affiliates.clicks
          FOR EACH ROW EXECUTE FUNCTION affiliates.append_only();
        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS clicks_append_only ON affiliates.clicks;
        DROP TRIGGER IF EXISTS ledger_entries_append_only ON affiliates.ledger_entries;
        DROP FUNCTION IF EXISTS affiliates.append_only();
        """;
}
