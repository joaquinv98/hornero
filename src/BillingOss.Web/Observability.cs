using BillingOss.App.Platform;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BillingOss.Web;

/// <summary>
/// OpenTelemetry (D-15): logs, métricas y trazas por OTLP. Se configura con las variables estándar OTEL_* y queda
/// apagado si no hay OTEL_EXPORTER_OTLP_ENDPOINT. Nada viaja al proyecto: el destino lo elige el proveedor.
/// </summary>
internal static class Observability
{
    public const string ServiceName = "billing-oss";

    public static void AddObservability(this WebApplicationBuilder builder, string role)
    {
        var config = builder.Configuration;
        if (string.IsNullOrWhiteSpace(config["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            return;
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(config["OTEL_SERVICE_NAME"] ?? ServiceName, serviceInstanceId: $"{Environment.MachineName}:{Environment.ProcessId}")
                .AddAttributes([new("billing.role", role)]))
            // ponytail: sin trazas de Npgsql, porque cada sondeo de la cola sería una traza suelta. Se suman con un
            // filtro de comandos si hace falta ver el SQL dentro de un pedido o trabajo.
            // Siempre se muestrea: un traceparent entrante con la marca "sin muestrear", de una pasarela que propaga
            // su contexto o de alguien que quiere esconderse, no deja sin span al pedido ni a los trabajos que
            // cuelgan de él por platform.jobs.trace_parent.
            .WithTracing(t => t
                .SetSampler(new AlwaysOnSampler())
                .AddSource(Telemetry.Name, "System.Net.Http")
                .AddAspNetCoreInstrumentation(o => o.Filter = http => !http.Request.Path.StartsWithSegments("/health")))
            .WithMetrics(m => m.AddMeter(
                Telemetry.Name,
                "Microsoft.AspNetCore.Hosting",
                "Microsoft.AspNetCore.Server.Kestrel",
                "Microsoft.AspNetCore.RateLimiting",
                "System.Net.Http",
                "System.Runtime",
                "Npgsql"))
            .WithLogging(configureBuilder: null, configureOptions: o =>
            {
                o.IncludeFormattedMessage = true;
                o.IncludeScopes = true;
            })
            .UseOtlpExporter();

        // Las de la cola salen de la base: con publicarlas desde los trabajadores alcanza.
        if (role is "all" or "worker")
        {
            builder.Services.AddHostedService<BusinessMetrics>();
        }
    }
}
