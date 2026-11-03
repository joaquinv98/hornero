using System.Globalization;
using System.Text.RegularExpressions;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql;

namespace BillingOss.App.Identity;

public enum ExternalProviderKind
{
    /// <summary>Google, con su dirección fija.</summary>
    Google,

    /// <summary>Cualquier otro proveedor OpenID Connect, con la dirección que carga el staff.</summary>
    Oidc,
}

/// <summary>
/// Un proveedor de ingreso externo del portal (diseno/ingreso-clientes.md, sección 3). El secreto del cliente va cifrado
/// por campo y atado a la dirección del proveedor: cambiarla obliga a cargarlo de nuevo.
/// </summary>
public sealed class ExternalProvider
{
    public const string GoogleAuthority = "https://accounts.google.com";

    public const string GoogleKey = "google";

    public long Id { get; private set; }

    /// <summary>Va en la dirección de vuelta: /ingresar/externo/{key}/vuelta.</summary>
    public string Key { get; set; } = string.Empty;

    public ExternalProviderKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>La dirección del proveedor, de donde sale su configuración (.well-known/openid-configuration).</summary>
    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    /// <summary>Cifrado con <see cref="SecretBox"/>, atado a la fila y a <see cref="Authority"/>.</summary>
    public byte[]? ClientSecret { get; set; }

    public bool Enabled { get; set; }

    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>
/// El vínculo entre una persona del proveedor (su identificador "sub") y un usuario del portal. Se crea con el correo
/// verificado por el proveedor y confirmado por el usuario, nunca solo (diseno/ingreso-clientes.md, sección 5).
/// </summary>
public sealed class ExternalLogin
{
    public long Id { get; private set; }

    public long ProviderId { get; set; }

    public string Subject { get; set; } = string.Empty;

    public long UserId { get; set; }

    /// <summary>El correo que tenía en el proveedor al vincularse, para mostrarlo.</summary>
    public string? Email { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>Lo que el staff edita de un proveedor. El secreto vacío deja el que estaba.</summary>
public sealed record ExternalProviderInput(string Name, string Authority, string ClientId, string? ClientSecret, bool Enabled);

/// <summary>Proveedores y vínculos. Los secretos nunca salen de acá en claro, salvo para el canje del código.</summary>
public sealed partial class ExternalLoginStore(BillingDbContext db, AuditLog audit, TimeProvider time, IServiceProvider services)
{
    private const string Table = "identity.external_providers";

    private const string Column = "client_secret";

    public Task<List<ExternalProvider>> Providers(CancellationToken ct = default) =>
        db.Set<ExternalProvider>().AsNoTracking().OrderBy(p => p.Kind).ThenBy(p => p.Id).ToListAsync(ct);

    /// <summary>Los prendidos y completos, para los botones del ingreso y del registro.</summary>
    public Task<List<ExternalProvider>> Enabled(CancellationToken ct = default) =>
        db.Set<ExternalProvider>().AsNoTracking().Where(p => p.Enabled && p.ClientSecret != null && p.ClientId != "")
            .OrderBy(p => p.Kind).ThenBy(p => p.Id).ToListAsync(ct);

    public Task<ExternalProvider?> Find(string key, CancellationToken ct = default) =>
        db.Set<ExternalProvider>().AsNoTracking().SingleOrDefaultAsync(p => p.Key == key, ct);

    /// <summary>El secreto en claro, para el canje del código. Null si no está cargado.</summary>
    public string? Secret(ExternalProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.ClientSecret is null ? null : Box.Decrypt(provider.ClientSecret, Table, Column, provider.Id, provider.Authority);
    }

    /// <summary>
    /// Crea o cambia un proveedor si la versión sigue siendo <paramref name="version"/>. Google lleva su dirección fija.
    /// Cambiar la dirección de otro sin cargar el secreto de nuevo da error: el cifrado está atado a ella. Queda en la
    /// auditoría con los nombres de lo que cambió, nunca con los valores.
    /// </summary>
    public async Task<long> Save(string key, ExternalProviderKind kind, ExternalProviderInput input, int version, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(input);
        key = key.Trim().ToLowerInvariant();
        var name = input.Name.Trim();
        var authority = kind == ExternalProviderKind.Google ? ExternalProvider.GoogleAuthority : input.Authority.Trim().TrimEnd('/');
        var clientId = input.ClientId.Trim();
        var secret = string.IsNullOrWhiteSpace(input.ClientSecret) ? null : input.ClientSecret.Trim();
        if (!KeyPattern().IsMatch(key) || (kind == ExternalProviderKind.Google) != (key == ExternalProvider.GoogleKey))
        {
            throw new ValidationException("External.BadKey");
        }

        if (name.Length is 0 or > 100)
        {
            throw new ValidationException("External.BadName");
        }

        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ValidationException("External.BadAuthority");
        }

        if (clientId.Length > 200 || secret?.Length > 500)
        {
            throw new ValidationException("External.TooLong");
        }

        var provider = await db.Set<ExternalProvider>().SingleOrDefaultAsync(p => p.Key == key, ct).ConfigureAwait(false);
        if ((provider?.Version ?? 0) != version)
        {
            throw new InvalidOperationException("External.Conflict");
        }

        var changed = new List<string>();
        if (provider is null)
        {
            provider = new ExternalProvider { Key = key, Kind = kind };
            db.Set<ExternalProvider>().Add(provider);
        }
        else if (provider.Authority != authority && secret is null && provider.ClientSecret is not null)
        {
            throw new ValidationException("External.SecretAgain");
        }

        Track(provider.Name != name, "name");
        Track(provider.Authority != authority, "authority");
        Track(provider.ClientId != clientId, "client_id");
        Track(secret is not null, "client_secret");
        Track(provider.Enabled != input.Enabled, "enabled");
        if (input.Enabled && (clientId.Length == 0 || (secret is null && (provider.ClientSecret is null || provider.Authority != authority))))
        {
            throw new ValidationException("External.Incomplete");
        }

        provider.Name = name;
        provider.Authority = authority;
        provider.ClientId = clientId;
        provider.Enabled = input.Enabled;
        provider.Version++;
        provider.UpdatedAt = time.GetUtcNow();
        provider.UpdatedBy = Actor.Current.Id;
        await using var tx = await Sql.OwnTransaction(db, ct).ConfigureAwait(false);
        await SaveUnique(ct).ConfigureAwait(false);
        if (secret is not null)
        {
            // El cifrado va atado a la fila: hace falta su ID, que recién existe después del primer guardado.
            provider.ClientSecret = Box.Encrypt(secret, Table, Column, provider.Id, authority);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await audit.Record("security.external_provider", string.Create(CultureInfo.InvariantCulture, $"external_provider:{provider.Id}"), new { key, changed }, ct).ConfigureAwait(false);
        await Sql.Commit(tx, ct).ConfigureAwait(false);
        return provider.Id;

        void Track(bool different, string field)
        {
            if (different)
            {
                changed.Add(field);
            }
        }
    }

    /// <summary>El vínculo de esa persona del proveedor, si existe.</summary>
    public Task<ExternalLogin?> Link(long providerId, string subject, CancellationToken ct = default) =>
        db.Set<ExternalLogin>().AsNoTracking().SingleOrDefaultAsync(l => l.ProviderId == providerId && l.Subject == subject, ct);

    /// <summary>Los vínculos de un usuario, con el nombre de su proveedor.</summary>
    public async Task<List<(ExternalLogin Link, string Provider)>> Links(long userId, CancellationToken ct = default) =>
        [.. (await (from l in db.Set<ExternalLogin>().AsNoTracking()
                    join p in db.Set<ExternalProvider>().AsNoTracking() on l.ProviderId equals p.Id
                    where l.UserId == userId
                    orderby l.Id
                    select new { l, p.Name }).ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.l, x.Name))];

    /// <summary>
    /// Vincula. Si esa persona del proveedor ya estaba vinculada a otro usuario (dos pedidos a la vez), da false y no
    /// cambia nada: el índice único decide.
    /// </summary>
    public async Task<bool> Add(long providerId, string subject, long userId, string? email, string how, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var now = time.GetUtcNow();
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO identity.external_logins (provider_id, subject, user_id, email, created_at)
            VALUES ({providerId}, {subject}, {userId}, {email}, {now})
            ON CONFLICT (provider_id, subject) DO NOTHING
            """, ct).ConfigureAwait(false);
        if (inserted == 0)
        {
            return await db.Set<ExternalLogin>().AnyAsync(l => l.ProviderId == providerId && l.Subject == subject && l.UserId == userId, ct).ConfigureAwait(false);
        }

        await audit.Record("portal.external_linked", string.Create(CultureInfo.InvariantCulture, $"user:{userId}"), new { provider_id = providerId, how }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Saca un vínculo del usuario. Devuelve false si no era suyo o no existía.</summary>
    public async Task<bool> Remove(long linkId, long userId, string action, CancellationToken ct = default)
    {
        var removed = await db.Set<ExternalLogin>().Where(l => l.Id == linkId && l.UserId == userId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (removed > 0)
        {
            await audit.Record(action, string.Create(CultureInfo.InvariantCulture, $"user:{userId}"), new { link_id = linkId }, ct).ConfigureAwait(false);
        }

        return removed > 0;
    }

    /// <summary>Anota el último uso del vínculo.</summary>
    public Task Touch(long linkId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return db.Set<ExternalLogin>().Where(l => l.Id == linkId).ExecuteUpdateAsync(s => s.SetProperty(l => l.LastUsedAt, now), ct);
    }

    private SecretBox Box => services.GetService(typeof(SecretBox)) as SecretBox
        ?? throw new InvalidOperationException("Falta la llave de secretos (Secrets:KeyFile): sin ella no se guarda ni se lee el secreto del proveedor.");

    private async Task SaveUnique(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Otra persona lo guardó a la vez: la versión o la clave única deciden.
            throw new InvalidOperationException("External.Conflict", ex);
        }
    }

    [GeneratedRegex("^[a-z0-9-]{2,32}$")]
    private static partial Regex KeyPattern();
}

internal sealed class ExternalProviderConfiguration : IEntityTypeConfiguration<ExternalProvider>
{
    public void Configure(EntityTypeBuilder<ExternalProvider> b)
    {
        b.ToTable("external_providers", "identity", t => t.HasCheckConstraint("ck_external_providers_key", "key ~ '^[a-z0-9-]{2,32}$'"));
        b.Property(x => x.Key).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Authority).HasMaxLength(300);
        b.Property(x => x.ClientId).HasMaxLength(200);
        b.Property(x => x.UpdatedBy).HasMaxLength(200);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => x.Key).IsUnique();
    }
}

internal sealed class ExternalLoginConfiguration : IEntityTypeConfiguration<ExternalLogin>
{
    public void Configure(EntityTypeBuilder<ExternalLogin> b)
    {
        b.ToTable("external_logins", "identity");
        b.Property(x => x.Subject).HasMaxLength(255);
        b.Property(x => x.Email).HasMaxLength(320);
        b.HasOne<ExternalProvider>().WithMany().HasForeignKey(x => x.ProviderId);
        b.HasOne<PortalUser>().WithMany().HasForeignKey(x => x.UserId);
        b.HasIndex(x => new { x.ProviderId, x.Subject }).IsUnique();
        b.HasIndex(x => x.UserId);
    }
}
