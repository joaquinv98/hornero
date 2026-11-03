using BillingOss.Domain.Catalog;
using BillingOss.Domain.Fiscal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingOss.App.Persistence;

internal sealed class TaxCategoryConfiguration : IEntityTypeConfiguration<TaxCategory>
{
    public void Configure(EntityTypeBuilder<TaxCategory> b)
    {
        b.ToTable("tax_categories", "catalog");
        b.Property(x => x.Code).HasMaxLength(50);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Explanation).HasMaxLength(2000);
        b.Property(x => x.Reason).HasMaxLength(50);
        b.Property(x => x.ExternalCodes).HasColumnType("jsonb");
        b.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class TaxConfiguration : IEntityTypeConfiguration<Tax>
{
    public void Configure(EntityTypeBuilder<Tax> b)
    {
        b.ToTable("taxes", "catalog");
        b.Property(x => x.Code).HasMaxLength(50);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Group).HasMaxLength(100);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class TaxSettingsConfiguration : IEntityTypeConfiguration<TaxSettings>
{
    public void Configure(EntityTypeBuilder<TaxSettings> b)
    {
        b.ToTable("tax_settings", "catalog");
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.IssuerCountry).HasMaxLength(2).IsFixedLength();
        b.Property(x => x.PriceModeByCurrency).HasColumnType("jsonb");
        b.Property(x => x.CatalogDisplay).HasMaxLength(50);
        b.Property(x => x.GrossIncomeRegimes).HasColumnType("jsonb");
    }
}

internal sealed class FiscalCredentialConfiguration : IEntityTypeConfiguration<FiscalCredential>
{
    public void Configure(EntityTypeBuilder<FiscalCredential> b)
    {
        b.ToTable("credentials", "fiscal");
        b.Property(x => x.Environment).HasMaxLength(20);
        b.Property(x => x.Alias).HasMaxLength(100);
        b.Property(x => x.SubjectTaxId).HasMaxLength(20);
        b.Property(x => x.Organization).HasMaxLength(200);
        b.Property(x => x.CertificateTaxId).HasMaxLength(20);
        b.Property(x => x.CertificateAlias).HasMaxLength(200);
        b.Property(x => x.CertificateIssuer).HasMaxLength(500);
        b.Property(x => x.CertificateSerial).HasMaxLength(100);
        b.Property(x => x.Thumbprint).HasMaxLength(64);
        b.Property(x => x.NoticesSent).HasColumnType("jsonb");
        b.HasOne<IssuerProfile>().WithMany().HasForeignKey(x => x.IssuerId);

        // Una sola credencial activa por emisor y ambiente.
        b.HasIndex(x => new { x.IssuerId, x.Environment }).IsUnique().HasFilter("status = 'Active'");
    }
}
