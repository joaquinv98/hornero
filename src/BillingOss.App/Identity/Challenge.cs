using BillingOss.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingOss.App.Identity;

/// <summary>
/// Un desafío anti robots ya usado (diseno/ingreso-clientes.md, sección 7). Cada desafío sirve una sola vez en cualquier
/// nodo: sin esto, una prueba de trabajo resuelta serviría para muchos envíos. La retención borra los vencidos.
/// </summary>
public sealed class ChallengeUse
{
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }
}

public static class ChallengeUses
{
    /// <summary>Anota el desafío como usado. Devuelve false si ya se había usado.</summary>
    public static async Task<bool> TryUse(BillingDbContext db, string id, DateTimeOffset expiresAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO identity.challenge_uses (id, expires_at) VALUES ({id}, {expiresAt}) ON CONFLICT (id) DO NOTHING", ct).ConfigureAwait(false) == 1;
    }
}

internal sealed class ChallengeUseConfiguration : IEntityTypeConfiguration<ChallengeUse>
{
    public void Configure(EntityTypeBuilder<ChallengeUse> b)
    {
        b.ToTable("challenge_uses", "identity");
        b.Property(x => x.Id).HasMaxLength(64);
        b.HasIndex(x => x.ExpiresAt);
    }
}
