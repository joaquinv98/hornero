using BillingOss.App.Identity;
using BillingOss.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingOss.App.Persistence;

internal static class LoginUserMapping
{
    public static void Map<T>(EntityTypeBuilder<T> b, string table)
        where T : LoginUser
    {
        b.ToTable(table, "identity");
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.NormalizedEmail).HasMaxLength(320);
        b.Property(x => x.PasswordHash).HasMaxLength(200);
        b.Property(x => x.SecurityStamp).HasMaxLength(64);
        b.Property(x => x.Language).HasMaxLength(10);
        b.HasIndex(x => x.NormalizedEmail).IsUnique();
    }
}

internal sealed class PortalUserConfiguration : IEntityTypeConfiguration<PortalUser>
{
    public void Configure(EntityTypeBuilder<PortalUser> b) => LoginUserMapping.Map(b, "portal_users");
}

internal sealed class StaffUserConfiguration : IEntityTypeConfiguration<StaffUser>
{
    public void Configure(EntityTypeBuilder<StaffUser> b)
    {
        LoginUserMapping.Map(b, "staff_users");
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.OidcSubject).HasMaxLength(200);
        b.HasIndex(x => x.OidcSubject).IsUnique().HasFilter("oidc_subject IS NOT NULL");
    }
}

internal sealed class AccountUserConfiguration : IEntityTypeConfiguration<AccountUser>
{
    public void Configure(EntityTypeBuilder<AccountUser> b)
    {
        b.ToTable("account_users", "identity");
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<PortalUser>().WithMany().HasForeignKey(x => x.UserId);
        b.HasIndex(x => new { x.AccountId, x.UserId }).IsUnique();
        b.HasIndex(x => x.UserId);

        // Un solo titular por cuenta.
        b.HasIndex(x => x.AccountId, "ix_account_users_owner").IsUnique().HasFilter("is_owner");
    }
}

internal sealed class CancellationRequestConfiguration : IEntityTypeConfiguration<CancellationRequest>
{
    public void Configure(EntityTypeBuilder<CancellationRequest> b)
    {
        b.ToTable("cancellation_requests", "services");
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.Detail).HasMaxLength(1000);
        b.Property(x => x.Reference).HasMaxLength(32);
        b.Property(x => x.CodeHash).HasMaxLength(64);
        b.Property(x => x.Receipt).HasMaxLength(32);
        b.Property(x => x.Ip).HasMaxLength(64);
        b.HasIndex(x => x.Reference).IsUnique();
        b.HasIndex(x => x.Receipt).IsUnique().HasFilter("receipt IS NOT NULL");
        b.HasIndex(x => x.AccountIds).HasMethod("gin");
    }
}
