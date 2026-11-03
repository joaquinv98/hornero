using System.Text;
using System.Text.Json.Nodes;
using BillingOss.Modules.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BillingOss.Modules.Hosting;

/// <summary>Arma el panel de una llamada con su configuración, el plazo de la operación y los secretos a tapar.</summary>
public delegate IHostingPanel PanelFactory(ModuleCall call, Budget budget, IReadOnlyList<string?> hidden);

/// <summary>
/// Lo común de los módulos de paneles de hosting (cPanel y WHM, Plesk, DirectAdmin): las operaciones del protocolo, la
/// parte del manifiesto que no depende del panel y el arranque del proceso.
/// </summary>
public static class HostingModule
{
    /// <summary>Métrica de uso: el disco ocupado, un nivel.</summary>
    public const string DiskMetric = "disk_gb";

    /// <summary>Métrica de uso: lo transferido en el mes calendario, un contador que el panel vuelve a cero cada mes.</summary>
    public const string TransferMetric = "transfer_gb";

    /// <summary>Espera del núcleo por operación, en segundos. Es la misma tabla que va en el manifiesto.</summary>
    public static readonly IReadOnlyDictionary<string, int> Timeouts = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["validate"] = 30,
        ["create"] = 120,
        ["suspend"] = 90,
        ["unsuspend"] = 90,
        ["terminate"] = 120,
        ["change_plan"] = 90,
        ["get_status"] = 30,
        ["run_action"] = 60,
        ["view"] = 30,
        ["sso"] = 30,
        ["usage"] = 30,
    };

    /// <param name="panels">Arma el panel de cada llamada.</param>
    /// <param name="time">Reloj para el plazo de cada operación. Los tests lo adelantan a mano.</param>
    public static ModuleDefinition Create(JsonObject manifest, PanelFactory panels, TimeProvider? time = null)
    {
        var operations = new HostingOperations(panels, time ?? TimeProvider.System);
        return new ModuleDefinition(manifest)
            .Operation("provisioning", "validate", Guard(operations.Validate), mutating: false)
            .Operation("provisioning", "create", Guard(c => operations.Create(c)))
            .Operation("provisioning", "suspend", Guard(c => operations.Suspend(c)))
            .Operation("provisioning", "unsuspend", Guard(c => operations.Unsuspend(c)))
            .Operation("provisioning", "terminate", Guard(c => operations.Terminate(c)))
            .Operation("provisioning", "change_plan", Guard(c => operations.ChangePlan(c)))
            .Operation("provisioning", "get_status", Guard(operations.GetStatus), mutating: false)
            .Operation("provisioning", "run_action", Guard(operations.RunAction))
            .Operation("provisioning", "view", Guard(operations.View), mutating: false)
            .Operation("provisioning", "sso", Guard(operations.Sso), mutating: false)
            .Operation("provisioning", "usage", Guard(operations.Usage), mutating: false)
            .InstanceTest(Guard(operations.Test))
            .Poll((call, id) => Guard(c => operations.Poll(c, id))(call));
    }

    /// <summary>
    /// El manifiesto de un módulo de panel. Lo que cambia entre paneles es la identidad, la configuración de la instancia
    /// y la descripción del plan.
    /// </summary>
    public static JsonObject Manifest(string id, string name, string version, JsonObject configSchema, string planDescription)
    {
        var manifest = JsonNode.Parse("""
            {
              "protocol": ["0"],
              "vendor": "Hornero",
              "license": "Apache-2.0",
              "kinds": ["provisioning"],
              "capabilities": [
                "provisioning.core", "provisioning.change_plan", "provisioning.status", "provisioning.actions",
                "provisioning.view", "provisioning.sso", "provisioning.usage"
              ],
              "order_schema": {
                "type": "object",
                "required": ["domain"],
                "properties": {
                  "domain": { "type": "string", "format": "hostname", "description": "Dominio principal del hosting" }
                }
              },
              "credentials": ["password"],
              "actions": [
                {
                  "id": "change_password",
                  "label": { "es": "Cambiar la contraseña del panel", "en": "Change the panel password" },
                  "audience": ["staff", "client"],
                  "confirm": true,
                  "credentials": ["password"]
                }
              ],
              "usage_metrics": [
                { "id": "disk_gb", "kind": "gauge", "unit": "GB", "label": { "es": "Disco", "en": "Disk" } },
                { "id": "transfer_gb", "kind": "counter", "unit": "GB", "label": { "es": "Transferencia", "en": "Transfer" } }
              ],
              "data_scopes": ["account.contact"],
              "max_in_flight": 2
            }
            """)!.AsObject();
        manifest["id"] = id;
        manifest["name"] = name;
        manifest["version"] = version;
        manifest["config_schema"] = configSchema;
        manifest["product_schema"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("plan"),
            ["properties"] = new JsonObject
            {
                ["plan"] = new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^[A-Za-z0-9_.-]([A-Za-z0-9 _.-]{0,126}[A-Za-z0-9_.-])?$",
                    ["description"] = planDescription,
                },
            },
        };
        manifest["timeouts"] = new JsonObject(Timeouts.Select(t => KeyValuePair.Create(t.Key, (JsonNode?)t.Value)));
        return manifest;
    }

    /// <summary>
    /// Arranca el módulo: el secreto de firma (y el anterior durante una rotación) y el registro de idempotencia salen de
    /// la configuración, nunca del código.
    /// </summary>
    /// <param name="build">Arma el módulo con el cliente HTTP por huella de certificado y el logger.</param>
    public static void Serve(string[] args, Func<Func<string?, HttpClient>, ILogger, ModuleDefinition> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var builder = WebApplication.CreateBuilder(args);

        // Secretos como archivos (Docker secrets): /run/secrets/Module__SigningSecret es Module:SigningSecret.
        builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);
        var app = builder.Build();
        var secrets = new List<byte[]> { Encoding.UTF8.GetBytes(Required(app.Configuration, "Module:SigningSecret")) };
        if (app.Configuration["Module:PreviousSigningSecret"] is { Length: > 0 } previous)
        {
            secrets.Add(Encoding.UTF8.GetBytes(previous));
        }

        app.MapModule(
            build(PanelHttp.For, app.Logger),
            new ModuleSdkOptions { SigningSecrets = secrets, IdempotencyStorePath = IdempotencyFile(app.Configuration, app.Environment.ContentRootPath) });
        app.Run();
    }

    /// <summary>
    /// El archivo SQLite del registro de idempotencia, para una sola réplica, con su carpeta creada. Una ruta relativa se
    /// toma desde el directorio del módulo. Null con Module:IdempotencyConnectionString: el registro va en PostgreSQL, lo
    /// comparten las réplicas y lo toma la SDK (docs/alta-disponibilidad/modulos.md), así el módulo no escribe en el disco.
    /// </summary>
    internal static string? IdempotencyFile(IConfiguration configuration, string contentRoot)
    {
        if (!string.IsNullOrEmpty(configuration["Module:IdempotencyConnectionString"]))
        {
            return null;
        }

        var store = Path.GetFullPath(configuration["Module:IdempotencyStorePath"] ?? "data/idempotency.db", contentRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        return store;
    }

    /// <summary>Lo que se escapa sin clasificar de una operación es un plazo vencido antes de cambiar nada: se puede reintentar.</summary>
    private static OperationHandler Guard(OperationHandler handler) => async call =>
    {
        try
        {
            return await handler(call).ConfigureAwait(false);
        }
        catch (OutOfTimeException)
        {
            throw Fail.Unavailable("El panel tardó demasiado en responder.", "out_of_time");
        }
    };

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Falta la configuración {key}. Se pasa por variable de entorno como {key.Replace(":", "__", StringComparison.Ordinal)}.");
}
