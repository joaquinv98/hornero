namespace BillingOss.App.Platform;

/// <summary>
/// Freno de las escrituras mientras la base no tiene con quién confirmar (D-14). Con replicación sincrónica y sin
/// ninguna réplica, PostgreSQL deja cada COMMIT esperando hasta que el Command Timeout lo cancela, y entonces lo
/// confirma solo en la primaria: todo lo que se arranca en ese estado termina confirmado sin réplica. Cerrado, el
/// corredor no toma trabajos y la API no acepta escrituras, así no se arranca ninguna transacción que vaya a terminar
/// así. Lo cierra y lo abre el vigía del clúster (ClusterWatch, con Database:Durability=sync) según
/// synchronous_standby_names y pg_stat_replication. Sin vigía queda siempre abierto.
/// </summary>
public sealed class WriteGate
{
    private volatile string? closedReason;

    /// <summary>Abierto: se escribe. Cerrado: nada arranca una transacción nueva.</summary>
    public bool Open => closedReason is null;

    public string? ClosedReason => closedReason;

    public void Close(string reason) => closedReason = reason;

    public void Reopen() => closedReason = null;
}
