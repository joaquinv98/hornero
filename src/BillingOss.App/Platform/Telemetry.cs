using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BillingOss.App.Platform;

/// <summary>
/// Nombres de trazas y métricas propias (D-15). Sin un destino OTLP nadie escucha y no cuestan nada: el host
/// decide si exportarlas.
/// </summary>
public static class Telemetry
{
    public const string Name = "BillingOss";

    public static ActivitySource Source { get; } = new(Name);

    public static Meter Meter { get; } = new(Name);

    /// <summary>Lo mide el nodo que corrió el trabajo: entre nodos se suma.</summary>
    public static Histogram<double> JobDuration { get; } = Meter.CreateHistogram<double>(
        "billing.job.duration", "s", "Duración de cada trabajo de la cola, por tipo y resultado.");

    /// <summary>Traza del pedido o trabajo actual en formato W3C, para que el trabajo que se encola la continúe.</summary>
    public static string? CurrentTraceParent() => Activity.Current is { IdFormat: ActivityIdFormat.W3C } a ? a.Id : null;
}
