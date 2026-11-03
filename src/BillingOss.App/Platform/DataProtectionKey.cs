using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BillingOss.App.Platform;

/// <summary>
/// Una llave del anillo de ASP.NET Data Protection (cookies, antiforgery y tokens de vida corta), compartida por todos
/// los nodos web (D-09). El secreto de la llave va cifrado con la llave maestra: un backup de la base no alcanza para
/// falsificar una cookie.
/// </summary>
public sealed class DataProtectionKey
{
    private DataProtectionKey()
    {
    }

    public DataProtectionKey(string friendlyName, string xml)
    {
        FriendlyName = friendlyName;
        Xml = xml;
    }

    public long Id { get; private set; }

    public string FriendlyName { get; private set; } = string.Empty;

    /// <summary>El elemento XML de la llave, con su secreto ya cifrado.</summary>
    public string Xml { get; private set; } = string.Empty;
}

internal sealed class DataProtectionKeyConfiguration : IEntityTypeConfiguration<DataProtectionKey>
{
    public void Configure(EntityTypeBuilder<DataProtectionKey> b)
    {
        b.ToTable("data_protection_keys", "platform");
        b.Property(x => x.FriendlyName).HasMaxLength(200);
    }
}
