using System.Globalization;
using BillingOss.App.UseCases;

namespace BillingOss.Web;

/// <summary>
/// Conciliación por línea de comandos, después de una restauración o de una promoción con pérdida (D-14):
/// <code>
/// reconcile [--since 2026-10-27T18:00:00Z] [--counter-gap 100] [--dry-run]
/// </code>
/// Sin --since, usa la hora de la última marca de línea de tiempo si la base cambió de línea de tiempo, y si no
/// cambió, pide la fecha. Sale con 0 sin hallazgos, 2 con hallazgos y 1 si no pudo correr.
/// </summary>
public static class ReconcileCommand
{
    public sealed record Arguments(DateTimeOffset? Since, long CounterGap, bool DryRun);

    public const string Usage = "Uso: reconcile [--since <instante ISO 8601>] [--counter-gap <n>] [--dry-run]";

    public static async Task<int> Run(WebApplication app, string[] args)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(args);
        if (!TryParse(args, out var parsed, out var error))
        {
            return Fail(error);
        }

        await using var scope = app.Services.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<ReconcileService>() is not { } service)
        {
            return Fail("La conciliación necesita Secrets:KeyFile: sin llaves no se puede hablar con los módulos.");
        }

        var since = parsed.Since ?? await service.SinceIfTimelineChanged(CancellationToken.None).ConfigureAwait(false);
        if (since is null)
        {
            return Fail("La línea de tiempo de la base no cambió desde la última marca: indicá desde cuándo conciliar con --since.");
        }

        var report = await service.Run(new ReconcileRequest(since.Value, parsed.CounterGap, parsed.DryRun), CancellationToken.None).ConfigureAwait(false);
        var output = Console.Out;
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Conciliación{(report.DryRun ? " en modo de prueba (nada se escribió)" : string.Empty)} desde {report.Since:O}, línea de tiempo {report.Timeline}.")).ConfigureAwait(false);
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Comprobantes adoptados: {report.Adopted}. Pagos sin intento propio: {report.UnmatchedPayments}. Hallazgos: {report.Findings.Count}.")).ConfigureAwait(false);
        foreach (var f in report.Findings)
        {
            await output.WriteLineAsync($"  {f.Kind} {f.Subject} {f.Details}").ConfigureAwait(false);
        }

        if (report.Findings.Count > 0)
        {
            await output.WriteLineAsync("Qué hacer con cada hallazgo: docs/despliegue/backups.md, sección Después de restaurar.").ConfigureAwait(false);
        }

        return report.Findings.Count == 0 ? 0 : 2;
    }

    public static bool TryParse(string[] args, out Arguments parsed, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);
        parsed = new Arguments(null, 0, false);
        error = string.Empty;
        DateTimeOffset? since = null;
        long gap = 0;
        var dryRun = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--since" when i + 1 < args.Length && DateTimeOffset.TryParse(args[i + 1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var s):
                    since = s;
                    i++;
                    break;
                case "--counter-gap" when i + 1 < args.Length && long.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var g) && g > 0:
                    gap = g;
                    i++;
                    break;
                default:
                    error = $"No se entendió {args[i]}. {Usage}";
                    return false;
            }
        }

        parsed = new Arguments(since, gap, dryRun);
        return true;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
