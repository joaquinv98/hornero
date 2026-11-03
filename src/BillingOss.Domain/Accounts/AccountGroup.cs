namespace BillingOss.Domain.Accounts;

/// <summary>
/// Grupo de clientes. La definición completa (color, insignia, descuento y exención de recargos) es la de
/// diseno/cuentas-avanzadas.md. Esta rama trae solo lo que usan los contenidos para segmentar la audiencia, con los
/// mismos nombres: al integrar vale la otra.
/// </summary>
public sealed partial class AccountGroup
{
    public long Id { get; private set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public void SetImportedId(long id) => Id = id;
}
