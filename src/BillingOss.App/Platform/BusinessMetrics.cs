using System.Diagnostics.Metrics;
using BillingOss.App.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BillingOss.App.Platform;

/// <summary>Una fila de platform.metrics(): nombre, etiqueta (tipo de trabajo o estado, o vacía) y valor.</summary>
public sealed record MetricRow(string Name, string Label, double Value);

/// <summary>
/// Métricas de negocio para varios nodos (D-15). Salen de la base con platform.metrics(), la misma función que
/// lee Zabbix, así que cada nodo publica el mismo valor: se agregan con max, nunca con sum. Solo las publica el
/// rol worker y solo con un destino OTLP.
/// </summary>
public sealed class BusinessMetrics : BackgroundService
{
    public static TimeSpan Interval { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Nombre en platform.metrics(), instrumento, unidad, descripción y atributo para la etiqueta.</summary>
    private static readonly (string Row, string Instrument, string Unit, string Description, string? Tag)[] Definitions =
    [
        ("jobs_pending", "billing.jobs.pending", "{job}", "Trabajos pendientes, incluidos los programados para más adelante.", "job.kind"),
        ("jobs_due", "billing.jobs.due", "{job}", "Trabajos pendientes que ya tendrían que estar corriendo.", "job.kind"),
        ("jobs_retrying", "billing.jobs.retrying", "{job}", "Trabajos pendientes que ya fallaron al menos una vez.", "job.kind"),
        ("jobs_running", "billing.jobs.running", "{job}", "Trabajos en curso con el lease vigente.", "job.kind"),
        ("jobs_stuck", "billing.jobs.stuck", "{job}", "Trabajos en curso con el lease vencido: el nodo que los tenía se cayó.", "job.kind"),
        ("jobs_dead", "billing.jobs.dead", "{job}", "Trabajos que agotaron los intentos y esperan al staff.", "job.kind"),
        ("jobs_oldest_due_seconds", "billing.jobs.oldest_due", "s", "Espera del trabajo pendiente más viejo.", "job.kind"),
        ("fiscal_emissions", "billing.fiscal.emissions", "{emission}", "Emisiones fiscales por estado.", "emission.status"),
        ("webhooks_unprocessed", "billing.webhooks.unprocessed", "{webhook}", "Avisos de pasarela recibidos y sin verificar.", null),
        ("webhooks_oldest_seconds", "billing.webhooks.oldest", "s", "Espera del aviso de pasarela sin verificar más viejo.", null),
        ("payments_pending", "billing.payments.pending", "{payment}", "Pagos pendientes o en disputa, por estado.", "payment.status"),
        ("payments_unmatched", "billing.payments.unmatched", "{payment}", "Pagos que la pasarela informó sin un intento propio, a la vista del staff.", null),
        ("reconcile_findings", "billing.reconcile.findings", "{finding}", "Hallazgos abiertos de la conciliación, por tipo.", "finding.kind"),
        ("billing_run_seconds", "billing.run.duration", "s", "Duración de la última corrida de facturación.", null),
        ("billing_run_age_seconds", "billing.run.age", "s", "Tiempo desde que terminó la última corrida de facturación.", null),
        ("fiscal_certificate_days", "billing.fiscal.certificate_days", "d", "Días que le quedan a cada certificado fiscal activo. Negativo si venció.", "credential"),
    ];

    private readonly IServiceScopeFactory scopes;
    private readonly TimeProvider time;
    private readonly ILogger<BusinessMetrics> logger;
    private readonly Meter meter = new(Telemetry.Name);
    private volatile IReadOnlyList<MetricRow> snapshot = [];

    public BusinessMetrics(IServiceScopeFactory scopes, TimeProvider time, ILogger<BusinessMetrics> logger)
    {
        this.scopes = scopes;
        this.time = time;
        this.logger = logger;
        foreach (var d in Definitions)
        {
            meter.CreateObservableGauge(d.Instrument, () => Observe(d.Row, d.Tag), d.Unit, d.Description);
        }
    }

    /// <summary>Lee platform.metrics() a la hora dada, con la misma regla de "vencido" que la cola.</summary>
    public static async Task<IReadOnlyList<MetricRow>> Read(BillingDbContext db, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        await using var cmd = await Sql.Command(db, "SELECT name, label, value FROM platform.metrics(@at)", ct, ("at", at)).ConfigureAwait(false);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<MetricRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new MetricRow(reader.GetString(0), reader.GetString(1), reader.GetDouble(2)));
        }

        return rows;
    }

    public async Task Refresh(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        snapshot = await Read(scope.ServiceProvider.GetRequiredService<BillingDbContext>(), time.GetUtcNow(), ct).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        meter.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Refresh(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sin datos frescos no se publica nada: una serie que desaparece se nota, un valor viejo no.
                snapshot = [];
                logger.LogWarning(ex, "No se pudieron leer las métricas de negocio");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }

    private IEnumerable<Measurement<double>> Observe(string row, string? tag) =>
        snapshot.Where(r => r.Name == row).Select(r => tag is null
            ? new Measurement<double>(r.Value)
            : new Measurement<double>(r.Value, new KeyValuePair<string, object?>(tag, r.Label)));
}
