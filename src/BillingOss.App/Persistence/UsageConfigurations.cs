using BillingOss.Domain.Catalog;
using BillingOss.Domain.Services;
using BillingOss.Domain.Usage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingOss.App.Persistence;

/// <summary>Uso medido (diseno/uso-medido.md): las métricas van con el catálogo, las muestras con los servicios y los cargos con la facturación.</summary>
internal sealed class UsageMetricConfiguration : IEntityTypeConfiguration<UsageMetric>
{
    public void Configure(EntityTypeBuilder<UsageMetric> b)
    {
        b.ToTable("usage_metrics", "catalog", t => t.HasCheckConstraint("ck_usage_metrics_amounts", "included >= 0 AND step > 0"));
        b.Property(x => x.Code).HasMaxLength(64);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Unit).HasMaxLength(16);
        b.Property(x => x.Included).HasPrecision(18, 4);
        b.Property(x => x.Step).HasPrecision(18, 4);
        b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
        b.HasIndex(x => new { x.ProductId, x.Code }).IsUnique();
    }
}

internal sealed class UsageMetricPriceConfiguration : IEntityTypeConfiguration<UsageMetricPrice>
{
    public void Configure(EntityTypeBuilder<UsageMetricPrice> b)
    {
        b.ToTable("usage_metric_prices", "catalog", t => t.HasCheckConstraint("ck_usage_metric_prices_positive", "unit_price >= 0"));
        b.Property(x => x.UnitPrice).HasPrecision(18, 6);
        b.HasOne<UsageMetric>().WithMany().HasForeignKey(x => x.MetricId);
        b.HasIndex(x => new { x.MetricId, x.Currency, x.ValidFrom }).IsUnique();
    }
}

internal sealed class UsageSampleConfiguration : IEntityTypeConfiguration<UsageSample>
{
    public void Configure(EntityTypeBuilder<UsageSample> b)
    {
        b.ToTable("usage_samples", "services", t => t.HasCheckConstraint("ck_usage_samples_positive", "reading >= 0 AND value >= 0"));
        b.Property(x => x.Code).HasMaxLength(64);
        b.Property(x => x.Reading).HasPrecision(18, 6);
        b.Property(x => x.Value).HasPrecision(18, 6);
        b.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId);
        b.HasOne(x => x.Charge).WithMany().HasForeignKey(x => x.ChargeId);

        // Una por servicio, métrica y día: una lectura repetida no guarda otra ni corre la base del contador.
        b.HasIndex(x => new { x.ServiceId, x.Code, x.Date }).IsUnique();
        b.HasIndex(x => new { x.ServiceId, x.Code }).HasDatabaseName("ix_usage_samples_unbilled").HasFilter("charge_id IS NULL");
    }
}

internal sealed class UsageChargeConfiguration : IEntityTypeConfiguration<UsageCharge>
{
    public void Configure(EntityTypeBuilder<UsageCharge> b)
    {
        b.ToTable("usage_charges", "billing", t => t.HasCheckConstraint("ck_usage_charges_window", "\"end\" > start AND measured >= 0 AND included >= 0 AND excess >= 0"));
        b.Property(x => x.Code).HasMaxLength(64);
        b.Property(x => x.Measured).HasPrecision(18, 6);
        b.Property(x => x.Included).HasPrecision(18, 4);
        b.Property(x => x.Excess).HasPrecision(18, 4);
        b.Property(x => x.UnitPrice).HasPrecision(18, 6);
        b.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId);
        b.HasOne<UsageMetric>().WithMany().HasForeignKey(x => x.MetricId);
        b.HasOne(x => x.Line).WithMany().HasForeignKey(x => x.DocumentLineId);

        // Un cargo por corte: dos corridas a la vez no cobran dos veces la misma ventana.
        b.HasIndex(x => new { x.ServiceId, x.Code, x.End }).IsUnique();
    }
}
