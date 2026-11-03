using System.Text.Json;
using System.Text.RegularExpressions;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Options;

namespace BillingOss.App.Support;

/// <summary>Los ajustes de soporte que guardó el staff desde el panel: una sola fila, con su versión.</summary>
public sealed class SupportSettingsRow
{
    public const short SingleId = 1;

    public short Id { get; set; } = SingleId;

    /// <summary>Los <see cref="SupportOptions"/> en JSON.</summary>
    public string Data { get; set; } = "{}";

    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }
}

/// <summary>
/// Los ajustes de soporte vigentes (Configuración, Ajustes de soporte): los que guardó el staff o, sin fila, los de la
/// sección Support de la configuración. Viven en la base, así todos los nodos ven lo mismo sin reiniciar. Dentro de un
/// pedido se leen una sola vez.
/// </summary>
public sealed partial class SupportSettings(BillingDbContext db, IOptions<SupportOptions> defaults, AuditLog audit, TimeProvider time)
{
    /// <summary>Un pedido con adjuntos acepta hasta 64 MiB: los topes juntos dejan lugar al texto.</summary>
    public const long MaxAttachmentsTotal = 60L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private (SupportOptions Options, int Version)? current;

    public async Task<SupportOptions> Get(CancellationToken ct = default) => (await Current(ct).ConfigureAwait(false)).Options;

    /// <summary>Los ajustes y su versión, para guardarlos después sin pisar a otra persona.</summary>
    public async Task<(SupportOptions Options, int Version)> Current(CancellationToken ct = default)
    {
        if (current is { } cached)
        {
            return cached;
        }

        var row = await db.Set<SupportSettingsRow>().AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
        current = row is null ? (defaults.Value, 0) : (JsonSerializer.Deserialize<SupportOptions>(row.Data, Json) ?? defaults.Value, row.Version);
        return current.Value;
    }

    /// <summary>Lo que está fuera de rango, como claves de texto de la interfaz. Vacío si se puede guardar.</summary>
    public static IReadOnlyList<string> Problems(SupportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var problems = new List<string>();
        var extensions = options.AllowedExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Check(extensions.Length is > 0 and <= 30 && extensions.All(e => Extension().IsMatch(e)), "AllowedExtensions");
        Check(options.MaxAttachmentBytes is >= 1024 * 1024 and <= 25 * 1024 * 1024, "MaxAttachmentBytes");
        Check(options.MaxAttachments is >= 1 and <= 10 && options.MaxAttachments * options.MaxAttachmentBytes <= MaxAttachmentsTotal, "MaxAttachments");
        Check(options.AutoCloseAfter is null || (options.AutoCloseAfter >= TimeSpan.FromHours(1) && options.AutoCloseAfter <= TimeSpan.FromDays(365)), "AutoCloseAfter");
        Check(options.MaxEmailBytes is >= 1024 * 1024 and <= 50 * 1024 * 1024, "MaxEmailBytes");
        Check(options.MaxEmailsPerSenderPerHour is >= 1 and <= 1000, "MaxEmailsPerSenderPerHour");
        return problems;

        void Check(bool ok, string field)
        {
            if (!ok)
            {
                problems.Add("SupportSettings." + field);
            }
        }
    }

    /// <summary>
    /// Guarda si la versión sigue siendo <paramref name="version"/>. Si otra persona los cambió antes, da
    /// InvalidOperationException. Queda en la auditoría con los nombres de lo que cambió.
    /// </summary>
    public async Task Save(SupportOptions options, int version, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Problems(options) is { Count: > 0 } problems)
        {
            throw new ValidationException("Ajustes de soporte fuera de rango: " + string.Join(", ", problems));
        }

        var before = (await Current(ct).ConfigureAwait(false)).Options;
        var json = JsonSerializer.Serialize(options, Json);
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO support.settings (id, data, version, updated_at, updated_by)
            VALUES ({SupportSettingsRow.SingleId}, {json}::jsonb, 1, {time.GetUtcNow()}, {Actor.Current.Id})
            ON CONFLICT (id) DO UPDATE SET data = EXCLUDED.data, version = settings.version + 1, updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by
            WHERE settings.version = {version}
            """, ct).ConfigureAwait(false);
        if (updated == 0)
        {
            throw new InvalidOperationException("Otra persona cambió los ajustes mientras los editabas: volvé a abrirlos.");
        }

        current = null;
        var changed = typeof(SupportOptions).GetProperties().Where(p => p.CanWrite && !Equals(p.GetValue(before), p.GetValue(options))).Select(p => p.Name).ToArray();
        await audit.Record("support.settings", "support:settings", new { changed }, ct).ConfigureAwait(false);
    }

    [GeneratedRegex(@"^\.?[a-z0-9]{1,10}$", RegexOptions.IgnoreCase)]
    private static partial Regex Extension();
}

internal sealed class SupportSettingsRowConfiguration : IEntityTypeConfiguration<SupportSettingsRow>
{
    public void Configure(EntityTypeBuilder<SupportSettingsRow> b)
    {
        b.ToTable("settings", "support", t => t.HasCheckConstraint("ck_settings_single", "id = 1"));
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Data).HasColumnType("jsonb");
        b.Property(x => x.UpdatedBy).HasMaxLength(200);
    }
}
