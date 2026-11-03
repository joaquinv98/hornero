using BillingOss.Domain.Accounts;
using BillingOss.Domain.Content;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingOss.App.Persistence;

// Tablas del esquema content (diseno/contenidos.md). Las claves foráneas, los checks y la columna de búsqueda están en
// SQL en la migración (ContentSql): los hijos se borran con su padre, y OnModelCreating pone todo lo demás en Restrict.

/// <summary>El mínimo del grupo de clientes que usan los contenidos. La definición completa es la de cuentas avanzadas.</summary>
internal sealed class AccountGroupConfiguration : IEntityTypeConfiguration<AccountGroup>
{
    public void Configure(EntityTypeBuilder<AccountGroup> b)
    {
        b.ToTable("groups", "accounts");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Name).HasMaxLength(100);
        b.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class ContentCategoryConfiguration : IEntityTypeConfiguration<ContentCategory>
{
    public void Configure(EntityTypeBuilder<ContentCategory> b)
    {
        b.ToTable("categories", "content");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Slug).HasMaxLength(Slugs.MaxLength);
        b.Property(x => x.Texts).HasColumnType("jsonb");
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasIndex(x => new { x.Kind, x.Slug }).IsUnique();
        b.HasIndex(x => new { x.Kind, x.ParentId });
    }
}

internal sealed class ArticleConfiguration : IEntityTypeConfiguration<Article>
{
    public void Configure(EntityTypeBuilder<Article> b)
    {
        b.ToTable("articles", "content");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Slug).HasMaxLength(Slugs.MaxLength);
        b.Property(x => x.ProductIds).HasColumnType("bigint[]");
        b.Property(x => x.GroupIds).HasColumnType("bigint[]");
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasIndex(x => x.Slug).IsUnique();
        b.HasIndex(x => new { x.Status, x.CategoryId });
    }
}

internal sealed class ArticleTextConfiguration : IEntityTypeConfiguration<ArticleText>
{
    public void Configure(EntityTypeBuilder<ArticleText> b)
    {
        // La columna de búsqueda (search) es generada y no pertenece al modelo: está en ContentSql.
        b.ToTable("article_texts", "content");
        b.HasKey(x => new { x.ArticleId, x.Language });
        b.Property(x => x.Language).HasMaxLength(10);
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Summary).HasMaxLength(2000);
        b.Property(x => x.Tags).HasMaxLength(1000);
        b.Ignore(x => x.TagList);
    }
}

internal sealed class ArticleVoteConfiguration : IEntityTypeConfiguration<ArticleVote>
{
    public void Configure(EntityTypeBuilder<ArticleVote> b)
    {
        b.ToTable("article_votes", "content");
        b.HasKey(x => new { x.ArticleId, x.Voter });
        b.Property(x => x.Voter).HasMaxLength(80);
    }
}

internal sealed class AnnouncementConfiguration : IEntityTypeConfiguration<Announcement>
{
    public void Configure(EntityTypeBuilder<Announcement> b)
    {
        b.ToTable("announcements", "content");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.ProductIds).HasColumnType("bigint[]");
        b.Property(x => x.GroupIds).HasColumnType("bigint[]");
        b.Property(x => x.Texts).HasColumnType("jsonb");
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasIndex(x => new { x.Status, x.PublishAt });
    }
}

internal sealed class DownloadConfiguration : IEntityTypeConfiguration<Download>
{
    public void Configure(EntityTypeBuilder<Download> b)
    {
        b.ToTable("downloads", "content");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.ProductIds).HasColumnType("bigint[]");
        b.Property(x => x.GroupIds).HasColumnType("bigint[]");
        b.Property(x => x.FileName).HasMaxLength(DownloadRules.MaxNameLength);
        b.Property(x => x.ContentType).HasMaxLength(100);
        b.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength();
        b.Property(x => x.Texts).HasColumnType("jsonb");
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasIndex(x => x.CategoryId);
        b.Ignore(x => x.HasFile);
    }
}

internal sealed class DownloadFileConfiguration : IEntityTypeConfiguration<DownloadFile>
{
    public void Configure(EntityTypeBuilder<DownloadFile> b)
    {
        b.ToTable("download_files", "content");
        b.HasKey(x => x.DownloadId);
        b.Property(x => x.DownloadId).ValueGeneratedNever();
    }
}

internal sealed class StatusComponentConfiguration : IEntityTypeConfiguration<StatusComponent>
{
    public void Configure(EntityTypeBuilder<StatusComponent> b)
    {
        b.ToTable("components", "content");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Slug).HasMaxLength(Slugs.MaxLength);
        b.Property(x => x.ProductIds).HasColumnType("bigint[]");
        b.Property(x => x.Texts).HasColumnType("jsonb");
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasIndex(x => x.Slug).IsUnique();
        b.HasIndex(x => x.ParentId);
    }
}

internal sealed class StatusEventConfiguration : IEntityTypeConfiguration<StatusEvent>
{
    public void Configure(EntityTypeBuilder<StatusEvent> b)
    {
        b.ToTable("status_events", "content");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Texts).HasColumnType("jsonb");
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasIndex(x => new { x.Kind, x.State });
        b.HasIndex(x => x.StartsAt);
    }
}

internal sealed class StatusEventComponentConfiguration : IEntityTypeConfiguration<StatusEventComponent>
{
    public void Configure(EntityTypeBuilder<StatusEventComponent> b)
    {
        b.ToTable("status_event_components", "content");
        b.HasKey(x => new { x.EventId, x.ComponentId });
        b.HasIndex(x => x.ComponentId);
    }
}

internal sealed class StatusUpdateConfiguration : IEntityTypeConfiguration<StatusUpdate>
{
    public void Configure(EntityTypeBuilder<StatusUpdate> b)
    {
        b.ToTable("status_event_updates", "content");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Texts).HasColumnType("jsonb");
        b.Property(x => x.Author).HasMaxLength(200);
        b.HasIndex(x => new { x.EventId, x.PostedAt });
    }
}

internal sealed class StatusSubscriberConfiguration : IEntityTypeConfiguration<StatusSubscriber>
{
    public void Configure(EntityTypeBuilder<StatusSubscriber> b)
    {
        // El correo es único sin distinguir mayúsculas: índice sobre lower(email), a mano en ContentSql.
        b.ToTable("subscribers", "content");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.Language).HasMaxLength(10);
        b.Property(x => x.Token).HasMaxLength(64);
        b.Property(x => x.ComponentIds).HasColumnType("bigint[]");
        b.HasIndex(x => x.Token).IsUnique();
    }
}
