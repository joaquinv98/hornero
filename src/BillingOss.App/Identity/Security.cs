using System.Net;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingOss.App.Identity;

public enum LoginAudience
{
    Portal,
    Staff,
}

/// <summary>Resultado de un intento de ingreso (diseno/fraude-y-acceso.md, sección 6.1).</summary>
public enum LoginResult
{
    Success,
    BadPassword,

    /// <summary>Correo sin usuario. No se guarda el correo que escribió.</summary>
    UnknownUser,

    /// <summary>La cuenta estaba bloqueada por intentos.</summary>
    Locked,

    /// <summary>La IP está en la lista de bloqueo o bloqueada por intentos.</summary>
    BlockedIp,

    /// <summary>Código del segundo factor o de recuperación incorrecto.</summary>
    BadCode,
    Disabled,
}

public enum LoginMethod
{
    Password,
    Totp,
    RecoveryCode,

    /// <summary>Con un proveedor externo vinculado, como Google (diseno/ingreso-clientes.md, sección 4).</summary>
    External,
}

/// <summary>Un intento de ingreso al portal o al admin. Nunca guarda la contraseña ni el correo de quien no tiene usuario.</summary>
public sealed class LoginEvent
{
    public long Id { get; private set; }

    public LoginAudience Audience { get; set; }

    public long? UserId { get; set; }

    public IPAddress? Ip { get; set; }

    /// <summary>La red de la IP (/24 en IPv4, /48 en IPv6): es el "lugar" del aviso de ingreso desde un lugar nuevo.</summary>
    public IPNetwork? Network { get; set; }

    public string? UserAgent { get; set; }

    public LoginResult Result { get; set; }

    public LoginMethod Method { get; set; }

    /// <summary>Ingreso exitoso desde una red desde la que ese usuario nunca había entrado.</summary>
    public bool NewLocation { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Cuenta para el límite por IP. El rechazo por IP bloqueada no suma: si no, el bloqueo no vencería nunca.</summary>
    public bool Failed => Result is not (LoginResult.Success or LoginResult.BlockedIp);
}

public enum BlockKind
{
    /// <summary>Una IP o una red.</summary>
    Ip,
    Email,

    /// <summary>El dominio de un correo, por ejemplo uno de casillas descartables.</summary>
    Domain,
}

public enum BlockSource
{
    Staff,
    Api,

    /// <summary>Bloqueo temporal de una IP por intentos fallidos.</summary>
    Automatic,
}

/// <summary>Entrada de una lista de bloqueo (diseno/fraude-y-acceso.md, sección 4). Un valor está una sola vez por tipo.</summary>
public sealed class BlockedEntry
{
    public long Id { get; private set; }

    public BlockKind Kind { get; set; }

    /// <summary>Normalizado: la red en notación CIDR, el correo o el dominio en minúsculas.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>La red, solo para el tipo IP. Una IP sola es una red /32 o /128.</summary>
    public IPNetwork? Network { get; set; }

    public string Reason { get; set; } = string.Empty;

    public BlockSource Source { get; set; }

    /// <summary>Quién la cargó, como en la auditoría.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Vacío es permanente. Las vencidas no cuentan.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>La fila de los ajustes de seguridad y fraude: una sola, con los ajustes en JSON y una versión.</summary>
public sealed class SecuritySettingsRow
{
    public const short SingleId = 1;

    public short Id { get; set; } = SingleId;

    public string Settings { get; set; } = "{}";

    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

internal sealed class LoginEventConfiguration : IEntityTypeConfiguration<LoginEvent>
{
    public void Configure(EntityTypeBuilder<LoginEvent> b)
    {
        b.ToTable("login_events", "identity");
        b.Property(x => x.UserAgent).HasMaxLength(300);
        b.Ignore(x => x.Failed);

        // Fallas por IP en la ventana (sección 6.2) e historial de cada usuario.
        b.HasIndex(x => new { x.Ip, x.CreatedAt });
        b.HasIndex(x => new { x.Audience, x.UserId, x.CreatedAt });
        b.HasIndex(x => x.CreatedAt);
    }
}

internal sealed class BlockedEntryConfiguration : IEntityTypeConfiguration<BlockedEntry>
{
    public void Configure(EntityTypeBuilder<BlockedEntry> b)
    {
        b.ToTable("blocked_entries", "identity", t => t.HasCheckConstraint("ck_blocked_entries_network", "(kind = 'Ip') = (network IS NOT NULL)"));
        b.Property(x => x.Value).HasMaxLength(320);
        b.Property(x => x.Reason).HasMaxLength(300);
        b.Property(x => x.CreatedBy).HasMaxLength(200);
        b.HasIndex(x => new { x.Kind, x.Value }).IsUnique();
    }
}

internal sealed class SecuritySettingsRowConfiguration : IEntityTypeConfiguration<SecuritySettingsRow>
{
    public void Configure(EntityTypeBuilder<SecuritySettingsRow> b)
    {
        b.ToTable("security_settings", "identity", t => t.HasCheckConstraint("ck_security_settings_single", "id = 1"));
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Settings).HasColumnType("jsonb");
        b.Property(x => x.UpdatedBy).HasMaxLength(200);
        b.Property(x => x.Version).IsConcurrencyToken();
    }
}

internal sealed class FraudCheckConfiguration : IEntityTypeConfiguration<FraudCheck>
{
    public void Configure(EntityTypeBuilder<FraudCheck> b)
    {
        b.ToTable("fraud_checks", "orders", t => t.HasCheckConstraint("ck_fraud_checks_score", "score IS NULL OR (score >= 0 AND score <= 100)"));
        b.Property(x => x.Score).HasPrecision(5, 2);
        b.Property(x => x.Reasons).HasColumnType("jsonb");
        b.Property(x => x.ExternalId).HasMaxLength(100);
        b.Property(x => x.Error).HasMaxLength(500);
        b.Property(x => x.DecidedBy).HasMaxLength(200);
        b.Property(x => x.Note).HasMaxLength(1000);
        b.Ignore(x => x.Holds);
        b.Ignore(x => x.ReasonList);
        b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<Platform.ModuleInstance>().WithMany().HasForeignKey(x => x.ModuleInstanceId);
        b.HasIndex(x => x.OrderId).IsUnique();
        b.HasIndex(x => x.AccountId);

        // La cola de revisión: solo las que retienen.
        b.HasIndex(x => x.Status, "ix_fraud_checks_holding").HasFilter("status IN ('Pending', 'Review')");
    }
}
