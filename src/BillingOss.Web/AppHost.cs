using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using BillingOss.App;
using BillingOss.App.Documents;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Web.Api;
using BillingOss.Web.Portal;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web;

/// <summary>
/// Arma el proceso según su rol (D-03): "web" atiende HTTP, "worker" corre trabajos y planificador, "all" las dos.
/// Las migraciones corren con un comando aparte antes de actualizar (D-14).
/// </summary>
public static class AppHost
{
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var config = builder.Configuration;
        // Con varios hosts se conecta solo a la primaria, y el Command Timeout no baja del mínimo (docs/alta-disponibilidad/configuracion.md).
        var connectionString = Failover.MainConnection(config.GetConnectionString("Main") ?? throw new InvalidOperationException("Falta ConnectionStrings:Main."));
        var role = config["Role"] ?? "all";

        // Ningún rol arranca sin la llave: los trabajadores no pueden emitir ni cobrar sin ella, y el rol web firma las
        // cookies y los formularios con Data Protection, que sin la llave quedaría en un anillo efímero de cada nodo.
        var keyFile = config["Secrets:KeyFile"]
            ?? throw new InvalidOperationException($"El rol {role} necesita Secrets:KeyFile: sin llaves no hay emisión fiscal ni cobros, ni cookies que valgan en todos los nodos.");
        var secrets = SecretBox.FromFile(keyFile);

        builder.Services.AddBillingCore(connectionString, o => config.GetSection("Billing").Bind(o), secrets);
        builder.Services.AddBillingReplica(config.GetConnectionString("Replica"));
        builder.Services.AddDocumentPdf(config["Pdf:GotenbergUrl"]);

        // Antes que los trabajadores y que Kestrel: un binario que el esquema ya no soporta no arranca (D-20).
        builder.Services.AddHostedService<SchemaGate>();
        builder.Services.Configure<WorkerOptions>(config.GetSection("Workers"));
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = config.GetValue("Shutdown:Timeout", TimeSpan.FromSeconds(30)));
        builder.Services.AddBillingDataProtection(secrets);

        builder.AddObservability(role);

        if (role is "all" or "worker")
        {
            builder.Services.AddBillingWorkers();
        }

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        });
        builder.Services.AddProblemDetails();
        builder.Services.AddOpenApi();
        builder.Services.AddExceptionHandler<DatabaseFailoverHandler>();
        builder.Services.Configure<ClusterOptions>(config.GetSection("Database"));

        // Con réplica sincrónica, el vigía cierra el freno de las escrituras mientras no haya ninguna (docs/alta-disponibilidad/configuracion.md).
        if (config.GetValue("Database:Durability", "single") == "sync")
        {
            builder.Services.AddHostedService<ClusterWatch>();
        }

        builder.Services.AddSingleton<SchemaCheck>();
        builder.Services.AddHealthChecks()
            .AddCheck<SchemaCheck>("schema", tags: ["startup", "ready"])
            .AddCheck<PrimaryCheck>("primary", tags: ["ready"])
            .AddCheck<ClusterCheck>("cluster", tags: ["startup", "cluster"]);
        builder.Services.Configure<CompatOptions>(config.GetSection("CompatApi"));
        builder.Services.Configure<FiscalOptions>(config.GetSection("Fiscal"));
        builder.Services.Configure<PaymentsOptions>(config.GetSection("Payments"));
        builder.Services.Configure<BillingOss.App.Notifications.NotificationOptions>(config.GetSection("Notifications"));
        builder.Services.Configure<BillingOss.App.Support.SupportOptions>(config.GetSection("Support"));
        builder.Services.Configure<BillingOss.App.Content.ContentOptions>(config.GetSection("Content"));
        if (role is "all" or "web")
        {
            builder.AddBillingUi();
            builder.Services.AddAffiliates();
        }

        // Ningún pedido necesita más de 1 MiB: los avisos cortan en 64 KiB. Un endpoint que pida más lo sube con RequestSizeLimit.
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024 * 1024);

        // Detrás de proxies, la IP del cliente (la que miran los topes por IP y las listas de IP de las API keys) sale
        // de X-Forwarded-For, recorrido de derecha a izquierda mientras cada salto sea un proxy conocido:
        // ForwardedHeaders:KnownProxies (IP o red CIDR) y ForwardedHeaders:KnownNetworks (redes CIDR). Sin ninguno
        // configurado no se le cree a nadie, ni a loopback: con las listas vacías el middleware les creería a todos y
        // cualquiera podría elegir su IP, y en TestServer, que no trae IP remota, pasa lo mismo con la primera entrada.
        var proxies = config.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
        var networks = config.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
            o.ForwardLimit = null;
            foreach (var proxy in proxies)
            {
                if (proxy.Contains('/', StringComparison.Ordinal))
                {
                    o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(proxy));
                }
                else
                {
                    o.KnownProxies.Add(IPAddress.Parse(proxy));
                }
            }

            foreach (var network in networks)
            {
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }

            o.ForwardedHeaders = proxies.Length + networks.Length > 0 ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto : ForwardedHeaders.None;
        });

        // Un cuerpo que pasa el tope, leído a mano (avisos), da 413 y no 500.
        builder.Services.AddExceptionHandler(o => o.StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError);
        builder.Services.AddSingleton<ApiKeyLimiter>();

        // Avisos de pasarela: tope de pedidos por instancia y por nodo (sección 9.3 del protocolo). Con N nodos el tope
        // total es N veces este, y el global va en el balanceador. Un aviso rechazado no se pierde: la pasarela lo
        // reintenta y la conciliación horaria lo encuentra. Solo las instancias que ya recibieron un aviso válido en
        // este nodo tienen balde propio: con un ID inventado en la URL no se crea un limitador nuevo.
        var webhookLimit = config.GetValue("Webhooks:PermitLimitPerNode", 120);
        builder.Services.AddSingleton<WebhookInstances>();
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (rejected, _) =>
            {
                var http = rejected.HttpContext;
                var result = http.Request.Path.StartsWithSegments(CompatApi.Path)
                    ? CompatApi.Error("Demasiados pedidos: probá de nuevo en un minuto.", StatusCodes.Status429TooManyRequests)
                    : Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Demasiados pedidos: probá de nuevo en un minuto.");
                return new ValueTask(result.ExecuteAsync(http));
            };

            // Avisos: además del tope por instancia, uno por IP para todo /webhooks, así rotar IDs que no existen no
            // lo esquiva. El resto de las rutas no pasa por acá ("sin tope" no es una IP, así no comparte partición).
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => http.Request.Path.StartsWithSegments("/webhooks")
                ? RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? string.Empty, _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) })
                : RateLimitPartition.GetNoLimiter("sin tope"));

            // Avisos de pasarela: tope de pedidos por instancia (sección 9.3 del protocolo). La clave es el ID ya
            // convertido como lo convierte la ruta, así 01, +1 y 1 comparten el cupo de la instancia 1.
            o.AddPolicy(Gateways.WebhookLimiter, http => RateLimitPartition.GetFixedWindowLimiter(
                long.TryParse(http.Request.RouteValues["instanceId"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && http.RequestServices.GetRequiredService<WebhookInstances>().Contains(id)
                    ? id.ToString(CultureInfo.InvariantCulture)
                    : WebhookInstances.Unknown,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = webhookLimit, Window = TimeSpan.FromMinutes(1) }));

            // API propia y compatible: tope por IP antes de autenticar. El de cada llave lo aplica ApiKeyLimiter.
            // Cada IPv6 cuenta aparte: si aparece abuso desde un mismo /64, agrupar por prefijo.
            o.AddPolicy(ApiV1.ClientLimiter, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 1200, Window = TimeSpan.FromMinutes(1) }));
        });

        var app = builder.Build();

        // Al apagar, listo pasa a 503 y el proceso sigue atendiendo este rato para que el balanceador lo saque.
        var drain = config.GetValue("Shutdown:DrainDelay", TimeSpan.Zero);
        if (drain > TimeSpan.Zero)
        {
            app.Lifetime.ApplicationStopping.Register(() => Thread.Sleep(drain));
        }

        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.UseRateLimiter();

        // Freno de las escrituras: sin réplica sincrónica, una escritura terminaría confirmada solo en la primaria, así
        // que no se acepta hasta que vuelva una. Las lecturas y la salud siguen.
        var gate = app.Services.GetRequiredService<WriteGate>();
        app.Use(async (http, next) =>
        {
            if (gate.Open || HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method))
            {
                await next(http).ConfigureAwait(false);
                return;
            }

            var title = gate.ClosedReason ?? "La base no acepta escrituras.";
            http.Response.Headers.RetryAfter = "5";
            var result = http.Request.Path.StartsWithSegments(CompatApi.Path)
                ? CompatApi.Error(title, StatusCodes.Status503ServiceUnavailable)
                : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: title);
            await result.ExecuteAsync(http).ConfigureAwait(false);
        });

        // Vivo no mira nada, arranque mira el esquema y listo además que la base sea la primaria y que no se esté apagando.
        app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
        app.MapHealthChecks("/health/startup", new() { Predicate = c => c.Tags.Contains("startup") });
        app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });

        // Réplica sincrónica y reloj: para el arranque y la alerta, nunca en listo. Si fallan, sacarían a todos los nodos
        // del balanceador a la vez.
        app.MapHealthChecks("/health/cluster", new() { Predicate = c => c.Tags.Contains("cluster") });
        if (role is "all" or "web")
        {
            // El documento describe toda la API: afuera de desarrollo no se publica sin autenticar.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
            }

            app.MapApiV1();
            app.MapCompatApi();
            app.MapGateways();
            app.UseBillingUi();
            app.MapBillingUi();
            app.MapAffiliates();
            app.MapPortalDocuments();
        }

        return app;
    }

    /// <summary>
    /// Aplica las migraciones pendientes y da de alta los módulos que trae la receta (Modules:Included), con su URL,
    /// secreto de firma y huella. Esto último necesita Secrets:KeyFile.
    /// </summary>
    public static async Task Migrate(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<BillingDbContext>().Database.MigrateAsync().ConfigureAwait(false);
        var included = app.Configuration.GetSection("Modules:Included").Get<Dictionary<string, IncludedModule>>();
        if (included is { Count: > 0 } && scope.ServiceProvider.GetService<IncludedModules>() is { } modules)
        {
            await modules.Sync(included).ConfigureAwait(false);
        }
    }
}
