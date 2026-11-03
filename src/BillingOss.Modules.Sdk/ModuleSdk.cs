using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ASP0016 confunde las lambdas internas de Handle con RequestDelegate: las rutas escriben la respuesta en Handle.
#pragma warning disable ASP0016

namespace BillingOss.Modules.Sdk;

/// <summary>Una llamada del núcleo, ya verificada.</summary>
public sealed class ModuleCall
{
    public required string Kind { get; init; }

    public required string Operation { get; init; }

    public required string RequestId { get; init; }

    public string? IdempotencyKey { get; init; }

    public required ModuleRequest Request { get; init; }

    public CancellationToken Aborted { get; init; }

    public T Config<T>() => Read<T>(Request.Instance.Config);

    public T Input<T>() => Read<T>(Request.Input);

    public T Context<T>() => Read<T>(Request.Context);

    private static T Read<T>(JsonNode node) =>
        node.Deserialize<T>(ModuleJson.Options)
        ?? throw new ModuleException(ErrorCodes.InvalidInput, $"No se pudo leer {typeof(T).Name}.");
}

/// <summary>Error tipado que la SDK convierte en la respuesta de error del protocolo.</summary>
public sealed class ModuleException(string code, string message, int? retryAfter = null, JsonObject? details = null)
    : Exception(message)
{
    public string Code { get; } = code;

    public int? RetryAfter { get; } = retryAfter;

    public JsonObject? Details { get; } = details;

    /// <summary>Estado de la instancia que cambió antes de fallar (un ticket nuevo): viaja en el error para que el núcleo lo guarde.</summary>
    public JsonNode? InstanceState { get; set; }
}

public delegate Task<ModuleResponse> OperationHandler(ModuleCall call);

public delegate Task<ModuleResponse> PollHandler(ModuleCall call, string operationId);

/// <summary>Lo que el autor de un módulo declara: manifiesto, operaciones y consulta de operaciones largas.</summary>
public sealed class ModuleDefinition(JsonObject manifest)
{
    private readonly Dictionary<string, (OperationHandler Handler, bool Mutating)> operations = new(StringComparer.Ordinal);

    public JsonObject Manifest { get; } = manifest;

    internal OperationHandler? InstanceTestHandler { get; private set; }

    internal PollHandler? PollOperationHandler { get; private set; }

    /// <summary>Registra una operación. Las que cambian algo exigen Idempotency-Key.</summary>
    public ModuleDefinition Operation(string kind, string operation, OperationHandler handler, bool mutating = true)
    {
        operations[$"{kind}/{operation}"] = (handler, mutating);
        return this;
    }

    public ModuleDefinition InstanceTest(OperationHandler handler)
    {
        InstanceTestHandler = handler;
        return this;
    }

    public ModuleDefinition Poll(PollHandler handler)
    {
        PollOperationHandler = handler;
        return this;
    }

    internal bool TryGet(string kind, string operation, out (OperationHandler Handler, bool Mutating) entry) =>
        operations.TryGetValue($"{kind}/{operation}", out entry);
}

public sealed class ModuleSdkOptions
{
    /// <summary>Secretos de firma vigentes. Dos durante una rotación.</summary>
    public required IReadOnlyList<byte[]> SigningSecrets { get; init; }

    /// <summary>
    /// Archivo SQLite del registro de idempotencia, para una sola réplica del módulo. Tiene que sobrevivir a los
    /// reinicios. Se usa si no hay IdempotencyConnectionString.
    /// </summary>
    public string? IdempotencyStorePath { get; init; }

    /// <summary>
    /// Cadena de conexión a la base PostgreSQL propia del módulo, para correrlo con varias réplicas que comparten el
    /// registro de idempotencia. Si está, se usa en lugar del archivo. Si falta, la SDK toma
    /// Module:IdempotencyConnectionString de la configuración de la aplicación, así vale para todo módulo.
    /// </summary>
    public string? IdempotencyConnectionString { get; init; }

    /// <summary>Cuánto se recuerda cada clave: el plazo de la operación más 7 días (sección 6 del protocolo).</summary>
    public TimeSpan KeyRetention { get; init; } = TimeSpan.FromDays(8);

    /// <summary>
    /// Cuánto espera una clave en curso antes de darla por abandonada por un proceso caído y dejar que otra réplica la
    /// retome. Va por encima del doble del plazo más largo del manifiesto. Se baja solo en pruebas.
    /// </summary>
    public TimeSpan StaleRun { get; init; } = TimeSpan.FromMinutes(10);

    public int MaxBodyBytes { get; init; } = 1024 * 1024;
}

public static class ModuleHost
{
    /// <summary>Publica las rutas del protocolo v0 para un módulo.</summary>
    public static WebApplication MapModule(this WebApplication app, ModuleDefinition module, ModuleSdkOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(options);

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BillingOss.Modules.Sdk");
        IdempotencyStore store;
        var pg = options.IdempotencyConnectionString;
        if (string.IsNullOrEmpty(pg))
        {
            // La lee la SDK y no cada Program.cs: así ningún módulo ignora la variable en silencio.
            pg = app.Configuration["Module:IdempotencyConnectionString"];
        }

        if (options.StaleRun < TimeSpan.FromSeconds(1))
        {
            throw new InvalidOperationException("StaleRun tiene que ser de al menos un segundo.");
        }

        if (!string.IsNullOrEmpty(pg))
        {
            var source = Npgsql.NpgsqlDataSource.Create(PostgresIdempotencyStore.ConnectionString(pg));
            app.Lifetime.ApplicationStopped.Register(source.Dispose);
            store = new PostgresIdempotencyStore(source, options.KeyRetention, options.StaleRun, logger, options.SigningSecrets);
            logger.LogInformation("Registro de idempotencia en PostgreSQL: admite varias réplicas.");
        }
        else
        {
            store = new SqliteIdempotencyStore(
                options.IdempotencyStorePath ?? throw new InvalidOperationException(
                    "Falta el registro de idempotencia: IdempotencyConnectionString o IdempotencyStorePath."),
                options.KeyRetention,
                options.StaleRun,
                TimeProvider.System,
                logger,
                options.SigningSecrets);
            logger.LogInformation("Registro de idempotencia en SQLite: una sola réplica, con el archivo en un disco persistente.");
        }

        // Vivo no mira nada. Listo mira el registro: una réplica que no llega a su base contesta 500 a toda operación con
        // efecto y le gasta intentos al núcleo, así que el balanceador la tiene que sacar.
        app.MapGet("/v0/health", () => Results.Json(new { status = "ok" }));
        app.MapGet("/v0/ready", async (CancellationToken ct) =>
            await store.Ready(ct).ConfigureAwait(false)
                ? Results.Json(new { status = "ok" })
                : Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

        app.MapGet("/v0/manifest", (HttpContext http) =>
            Handle(http, options, logger, (_, _) =>
                Task.FromResult((200, JsonSerializer.SerializeToUtf8Bytes<JsonNode>(module.Manifest, ModuleJson.Options)))));

        app.MapPost("/v0/instance/test", (HttpContext http) =>
            Handle(http, options, logger, (body, aborted) =>
                Run(http, body, "instance", "test", module.InstanceTestHandler, aborted)));

        app.MapPost("/v0/operations/{id}", (HttpContext http, string id) =>
            Handle(http, options, logger, (body, aborted) =>
                Run(http, body, "operations", "poll", module.PollOperationHandler is { } poll ? call => poll(call, id) : null, aborted)));

        app.MapPost("/v0/{kind}/{operation}", (HttpContext http, string kind, string operation) =>
            Handle(http, options, logger, async (body, aborted) =>
            {
                if (!module.TryGet(kind, operation, out var entry))
                {
                    throw new ModuleException(ErrorCodes.NotSupported, $"Operación no soportada: {kind}.{operation}.");
                }

                if (!entry.Mutating)
                {
                    return await Run(http, body, kind, operation, entry.Handler, aborted).ConfigureAwait(false);
                }

                var key = http.Request.Headers[ModuleHeaders.IdempotencyKey].FirstOrDefault();
                if (string.IsNullOrEmpty(key))
                {
                    throw new ModuleException(ErrorCodes.InvalidInput, "Falta Idempotency-Key.");
                }

                // El manejador de una operación con efecto no se corta cuando el núcleo se desconecta: si lo hiciera,
                // el efecto podría quedar hecho sin guardar y el reintento lo repetiría. Su plazo es el doble del que
                // declara el manifiesto, y mientras tanto la clave sigue en curso para cualquier réplica.
                using var deadline = new CancellationTokenSource(OperationTimeout(module.Manifest, operation) * 2);
                return await store.RunOnce(key, $"{kind}/{operation}", InputFingerprint(body), () =>
                    Run(http, body, kind, operation, entry.Handler, deadline.Token)).ConfigureAwait(false);
            }));

        return app;
    }

    private static async Task Handle(
        HttpContext http,
        ModuleSdkOptions options,
        ILogger logger,
        Func<byte[], CancellationToken, Task<(int Status, byte[] Body)>> work)
    {
        var started = TimeProvider.System.GetTimestamp();
        var requestId = http.Request.Headers[ModuleHeaders.RequestId].ToString();
        int status;
        byte[] payload;
        try
        {
            // Un encabezado repetido deja tres lecturas distintas de la clave (la firmada, la del registro y la del
            // handler): un pedido capturado se podría repetir con efecto. Cada uno viene una sola vez o no vale.
            foreach (var name in new[] { ModuleHeaders.RequestId, ModuleHeaders.Timestamp, ModuleHeaders.Signature, ModuleHeaders.IdempotencyKey })
            {
                if (http.Request.Headers[name].Count > 1)
                {
                    throw new ModuleException(ErrorCodes.InvalidSignature, $"El encabezado {name} viene repetido.");
                }
            }

            var body = await ReadBody(http, options.MaxBodyBytes).ConfigureAwait(false);
            if (!ModuleSignature.Verify(
                    options.SigningSecrets,
                    http.Request.Headers[ModuleHeaders.Signature].ToString(),
                    http.Request.Headers[ModuleHeaders.Timestamp].ToString(),
                    TimeProvider.System.GetUtcNow(),
                    http.Request.Method,
                    http.Request.Path.Value ?? string.Empty,
                    requestId,
                    http.Request.Headers[ModuleHeaders.IdempotencyKey].FirstOrDefault(),
                    body))
            {
                throw new ModuleException(ErrorCodes.InvalidSignature, "Firma inválida o reloj fuera de ventana.");
            }

            (status, payload) = await work(body, http.RequestAborted).ConfigureAwait(false);
        }
        catch (ModuleException ex)
        {
            (status, payload) = Error(ex.Code, ex.Message, ex.RetryAfter, ex.Details, ex.InstanceState);
        }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
        {
            // Venció el plazo del manejador: el pedido pudo haberse ejecutado y la clave sigue en curso.
            logger.LogError("Venció el plazo de {Path} ({RequestId}): resultado incierto.", http.Request.Path, requestId);
            (status, payload) = Error(ErrorCodes.OutcomeUnknown, "Venció el plazo de la operación: el pedido pudo haberse ejecutado.", null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nunca se devuelve el detalle: puede tener secretos de la configuración.
            logger.LogError("Falla del módulo en {Path} ({RequestId}): {Type}", http.Request.Path, requestId, ex.GetType().Name);
            (status, payload) = Error(ErrorCodes.Internal, "Error interno del módulo.", null, null);
        }

        logger.LogInformation(
            "{Method} {Path} {Status} en {Elapsed} ms ({RequestId})",
            http.Request.Method,
            http.Request.Path,
            status,
            (int)TimeProvider.System.GetElapsedTime(started).TotalMilliseconds,
            requestId);
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        await http.Response.Body.WriteAsync(payload, http.RequestAborted).ConfigureAwait(false);
    }

    private static async Task<(int Status, byte[] Body)> Run(
        HttpContext http,
        byte[] body,
        string kind,
        string operation,
        OperationHandler? handler,
        CancellationToken aborted)
    {
        if (handler is null)
        {
            throw new ModuleException(ErrorCodes.NotSupported, $"Operación no soportada: {kind}.{operation}.");
        }

        ModuleRequest request;
        try
        {
            request = JsonSerializer.Deserialize<ModuleRequest>(body, ModuleJson.Options)
                ?? throw new JsonException("Sobre vacío.");
        }
        catch (JsonException)
        {
            throw new ModuleException(ErrorCodes.InvalidInput, "El sobre no es JSON válido.");
        }

        var response = await handler(new ModuleCall
        {
            Kind = kind,
            Operation = operation,
            RequestId = http.Request.Headers[ModuleHeaders.RequestId].ToString(),
            IdempotencyKey = http.Request.Headers[ModuleHeaders.IdempotencyKey].FirstOrDefault(),
            Request = request,
            Aborted = aborted,
        }).ConfigureAwait(false);

        if (response.Status == ModuleResponse.PendingStatus && !ModuleSignature.IsValidPath($"/v0/operations/{response.OperationId}"))
        {
            throw new InvalidOperationException("El operation_id tiene caracteres que no van en una ruta.");
        }

        return (200, JsonSerializer.SerializeToUtf8Bytes(response, ModuleJson.Options));
    }

    private static (int Status, byte[] Body) Error(string code, string message, int? retryAfter, JsonObject? details, JsonNode? instanceState = null) =>
        (ErrorCodes.HttpStatus(code),
         JsonSerializer.SerializeToUtf8Bytes(
             new ErrorBody(new ModuleError(code, message, ErrorCodes.IsRetryable(code), retryAfter, details), instanceState),
             ModuleJson.Options));

    private static async Task<byte[]> ReadBody(HttpContext http, int maxBytes)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new ModuleException(ErrorCodes.InvalidInput, "El cuerpo supera el límite.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>El plazo que el manifiesto declara para la operación, con el mismo valor por defecto y tope que el núcleo.</summary>
    private static TimeSpan OperationTimeout(JsonObject manifest, string operation) =>
        TimeSpan.FromSeconds(Math.Clamp(manifest["timeouts"]?[operation]?.GetValue<int>() ?? 30, 1, 120));

    /// <summary>Huella de la operación y su input. La configuración, el estado y el contexto no cuentan.</summary>
    private static string InputFingerprint(byte[] body)
    {
        try
        {
            var input = JsonNode.Parse(body)?["input"]?.ToJsonString() ?? "{}";
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
        }
        catch (JsonException)
        {
            throw new ModuleException(ErrorCodes.InvalidInput, "El sobre no es JSON válido.");
        }
    }
}
