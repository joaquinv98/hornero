using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BillingOss.Modules.Sdk;

/// <summary>
/// Registro durable de claves de idempotencia (sección 6 del protocolo). La misma clave con la misma operación e
/// input devuelve la respuesta guardada. Los errores reintentables no se guardan, para que el reintento vuelva a
/// ejecutar. Hay dos: SQLite para una sola réplica del módulo y PostgreSQL para varias, con la misma semántica.
/// </summary>
internal abstract class IdempotencyStore
{
    private const string SealedPrefix = "sealed:v1:";
    private static readonly byte[] SealInfo = Encoding.UTF8.GetBytes("billingoss.module.idempotency.secrets");

    private readonly ILogger logger;
    private readonly byte[][] sealKeys;

    /// <param name="staleRun">Una clave en curso más vieja que esto se toma como abandonada por un proceso caído.</param>
    /// <param name="signingSecrets">
    /// De ellos se deriva la llave con que se cifran las credenciales guardadas (result.secrets): el secreto de
    /// firma ya es un secreto de la instalación y sale de la bóveda, así el registro no necesita otra llave.
    /// </param>
    protected IdempotencyStore(TimeSpan staleRun, ILogger logger, IReadOnlyList<byte[]> signingSecrets)
    {
        StaleRun = staleRun;
        this.logger = logger;
        sealKeys = [.. signingSecrets.Select(s => HKDF.DeriveKey(HashAlgorithmName.SHA256, s, 32, info: SealInfo))];
    }

    protected TimeSpan StaleRun { get; }

    protected ILogger Logger => logger;

    /// <summary>Si el registro responde desde esta réplica, con un plazo corto: es el listo del balanceador.</summary>
    public async Task<bool> Ready(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await Ping(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DbException or OperationCanceledException or InvalidOperationException)
        {
            logger.LogWarning("El registro de idempotencia no responde: {Type}", ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Una consulta mínima al registro.</summary>
    protected abstract Task Ping(CancellationToken ct);

    public async Task<(int Status, byte[] Body)> RunOnce(
        string key,
        string operation,
        string fingerprint,
        Func<Task<(int Status, byte[] Body)>> run)
    {
        var (started, saved) = await Claim(key, operation, fingerprint).ConfigureAwait(false);
        if (saved is { } done)
        {
            return (done.Status, Open(key, done.Body));
        }

        (int Status, byte[] Body) result;
        try
        {
            result = await run().ConfigureAwait(false);
        }
        catch (ModuleException ex) when (!ErrorCodes.IsRetryable(ex.Code))
        {
            // Un error definitivo se guarda: repetir la llamada tiene que dar lo mismo.
            await Finish(key, started, ErrorCodes.HttpStatus(ex.Code), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new ErrorBody(new ModuleError(ex.Code, ex.Message, false, ex.RetryAfter, ex.Details)), ModuleJson.Options))).ConfigureAwait(false);
            throw;
        }
        catch (ModuleException)
        {
            // Reintentable: el módulo sabe que el efecto no ocurrió, así que el reintento vuelve a ejecutar.
            await Release(key, started).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // Cualquier otra falla, incluida una cancelación, no dice si el efecto ocurrió: la clave queda en curso
            // hasta que venza, para que un reintento del núcleo que caiga en otra réplica no lo repita.
            logger.LogError(
                "La clave {Key} ({Operation}) falló con {Type} sin resultado conocido: queda en curso hasta que venza.",
                key, operation, ex.GetType().Name);
            throw;
        }

        await Finish(key, started, result.Status, Prepare(key, result.Body)).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Toma la clave. Devuelve la respuesta guardada si ya terminó, o la hora de la toma, que identifica a esta
    /// ejecución: guardar y olvidar solo tocan la fila si sigue siendo de ella.
    /// </summary>
    protected abstract Task<(DateTime Started, (int Status, byte[] Body)? Saved)> Claim(string key, string operation, string fingerprint);

    /// <summary>Guarda la respuesta. Devuelve si la fila seguía siendo de esta ejecución.</summary>
    protected abstract Task<bool> Save(string key, DateTime started, int status, byte[] body);

    /// <summary>Suelta la clave. Devuelve si la fila seguía siendo de esta ejecución.</summary>
    protected abstract Task<bool> Forget(string key, DateTime started);

    protected static ModuleException Conflict() => new(ErrorCodes.IdempotencyConflict, "La clave ya se usó con otra entrada.");

    protected static ModuleException InProgress() => new(ErrorCodes.Conflict, "La operación con esta clave sigue en curso.", retryAfter: 5);

    /// <summary>
    /// Si el registro no responde después del efecto (por ejemplo, en plena conmutación de la base), la respuesta
    /// sale igual: devolver un error haría que el núcleo reintente y, al vencer la clave en curso, repita el efecto.
    /// </summary>
    private async Task Finish(string key, DateTime started, int status, byte[] body)
    {
        try
        {
            if (!await Save(key, started, status, body).ConfigureAwait(false))
            {
                logger.LogError("La clave {Key} ya no era de esta ejecución: otra réplica la retomó y el efecto pudo repetirse.", key);
            }
        }
        catch (DbException ex)
        {
            logger.LogError(
                "No se pudo guardar la respuesta de la clave {Key} ({Type}). Queda en curso hasta que venza.", key, ex.GetType().Name);
        }
    }

    /// <summary>Un registro caído no reemplaza el error reintentable del manejador, que es el que lleva retry_after.</summary>
    private async Task Release(string key, DateTime started)
    {
        try
        {
            if (!await Forget(key, started).ConfigureAwait(false))
            {
                logger.LogError("La clave {Key} ya no era de esta ejecución: otra réplica la retomó.", key);
            }
        }
        catch (DbException ex)
        {
            logger.LogError("No se pudo soltar la clave {Key} ({Type}). Queda en curso hasta que venza.", key, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Lo que se guarda: sin instance_state, porque puede traer tickets de acceso y una respuesta repetida con un
    /// estado viejo pisaría el vigente en el núcleo, y con result.secrets cifradas (AES-GCM, atadas a la clave),
    /// porque el registro se replica y entra en los backups.
    /// </summary>
    private byte[] Prepare(string key, byte[] body)
    {
        JsonObject response;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject parsed)
            {
                return body;
            }

            response = parsed;
        }
        catch (JsonException)
        {
            return body;
        }

        var changed = response.Remove("instance_state");
        if (response["result"] is JsonObject result && result["secrets"] is JsonNode secrets)
        {
            var plain = Encoding.UTF8.GetBytes(secrets.ToJsonString(ModuleJson.Options));
            var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
            var cipher = new byte[plain.Length];
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];
            using var aes = new AesGcm(sealKeys[0], tag.Length);
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(key));
            result["secrets"] = SealedPrefix + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
            changed = true;
        }

        return changed ? JsonSerializer.SerializeToUtf8Bytes<JsonNode>(response, ModuleJson.Options) : body;
    }

    /// <summary>Descifra result.secrets de una respuesta guardada, con cualquiera de los secretos de firma vigentes.</summary>
    private byte[] Open(string key, byte[] body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject response
                || response["result"] is not JsonObject result
                || result["secrets"] is not JsonValue value
                || !value.TryGetValue<string>(out var sealedText)
                || !sealedText.StartsWith(SealedPrefix, StringComparison.Ordinal))
            {
                return body;
            }

            var blob = Convert.FromBase64String(sealedText[SealedPrefix.Length..]);
            var nonceSize = AesGcm.NonceByteSizes.MaxSize;
            var tagSize = AesGcm.TagByteSizes.MaxSize;
            var plain = new byte[blob.Length - nonceSize - tagSize];
            foreach (var sealKey in sealKeys)
            {
                try
                {
                    using var aes = new AesGcm(sealKey, tagSize);
                    aes.Decrypt(blob.AsSpan(0, nonceSize), blob.AsSpan(nonceSize, plain.Length), blob.AsSpan(^tagSize..), plain, Encoding.UTF8.GetBytes(key));
                    result["secrets"] = JsonNode.Parse(plain);
                    return JsonSerializer.SerializeToUtf8Bytes<JsonNode>(response, ModuleJson.Options);
                }
                catch (CryptographicException)
                {
                }
            }

            logger.LogError(
                "Las credenciales guardadas de la clave {Key} se cifraron con un secreto de firma ya retirado: la respuesta repetida sale sin ellas.", key);
            result.Remove("secrets");
            return JsonSerializer.SerializeToUtf8Bytes<JsonNode>(response, ModuleJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return body;
        }
    }
}

/// <summary>Registro en un archivo SQLite local, para una sola réplica: SQLite no se comparte por la red.</summary>
internal sealed class SqliteIdempotencyStore : IdempotencyStore
{
    private readonly string connectionString;
    private readonly TimeSpan retention;
    private readonly TimeProvider time;

    public SqliteIdempotencyStore(string path, TimeSpan retention, TimeSpan staleRun, TimeProvider time, ILogger logger, IReadOnlyList<byte[]> signingSecrets)
        : base(staleRun, logger, signingSecrets)
    {
        // La ruta se resuelve acá y el directorio se crea acá: así un módulo que copia el ejemplo del README no falla
        // al arrancar con "unable to open database file" ni deja el archivo donde lo lanzaron.
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = full, Pooling = false }.ToString();
        this.retention = retention;
        this.time = time;
        using var db = Open();
        Execute(db, null, """
            CREATE TABLE IF NOT EXISTS idempotency (
              key TEXT PRIMARY KEY,
              operation TEXT NOT NULL,
              fingerprint TEXT NOT NULL,
              state TEXT NOT NULL,
              status INTEGER,
              response BLOB,
              started_at INTEGER NOT NULL,
              expires_at INTEGER NOT NULL);
            DELETE FROM idempotency WHERE expires_at < $now;
            """, ("$now", Now()));
    }

    protected override Task<(DateTime Started, (int Status, byte[] Body)? Saved)> Claim(string key, string operation, string fingerprint)
    {
        var now = Now();
        using var db = Open();

        // BEGIN IMMEDIATE: leer y tomar la clave es una sola operación frente a otro pedido con la misma clave.
        using var tx = db.BeginTransaction(deferred: false);
        using (var read = db.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT operation, fingerprint, state, status, response, started_at FROM idempotency WHERE key = $key";
            read.Parameters.AddWithValue("$key", key);
            using var row = read.ExecuteReader();
            if (row.Read())
            {
                if (row.GetString(0) != operation || row.GetString(1) != fingerprint)
                {
                    throw Conflict();
                }

                if (row.GetString(2) == "done")
                {
                    return Task.FromResult<(DateTime, (int, byte[])?)>((default, (row.GetInt32(3), (byte[])row[4])));
                }

                if (now - row.GetInt64(5) < (long)StaleRun.TotalSeconds)
                {
                    throw InProgress();
                }
            }
        }

        Execute(db, tx, """
            INSERT INTO idempotency (key, operation, fingerprint, state, started_at, expires_at)
            VALUES ($key, $op, $fp, 'running', $now, $exp)
            ON CONFLICT(key) DO UPDATE SET state = 'running', started_at = $now
            """, ("$key", key), ("$op", operation), ("$fp", fingerprint), ("$now", now), ("$exp", now + (long)retention.TotalSeconds));
        tx.Commit();
        return Task.FromResult<(DateTime, (int, byte[])?)>((DateTimeOffset.FromUnixTimeSeconds(now).UtcDateTime, null));
    }

    protected override Task<bool> Save(string key, DateTime started, int status, byte[] body)
    {
        using var db = Open();
        var rows = Execute(db, null, "UPDATE idempotency SET state = 'done', status = $status, response = $body WHERE key = $key AND started_at = $started",
            ("$status", status), ("$body", body), ("$key", key), ("$started", Seconds(started)));
        return Task.FromResult(rows > 0);
    }

    protected override Task<bool> Forget(string key, DateTime started)
    {
        using var db = Open();
        var rows = Execute(db, null, "DELETE FROM idempotency WHERE key = $key AND started_at = $started", ("$key", key), ("$started", Seconds(started)));
        return Task.FromResult(rows > 0);
    }

    protected override Task Ping(CancellationToken ct)
    {
        using var db = Open();
        Execute(db, null, "SELECT count(*) FROM idempotency WHERE key = ''");
        return Task.CompletedTask;
    }

    private static long Seconds(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private long Now() => time.GetUtcNow().ToUnixTimeSeconds();

    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Registro en PostgreSQL, compartido por todas las réplicas del módulo. Tomar la clave es un solo
/// INSERT ... ON CONFLICT, así que dos réplicas con la misma clave a la vez no ejecutan las dos. Las horas salen de
/// la base y no del reloj de cada réplica. La base es del módulo, nunca la del núcleo (D-08).
/// </summary>
internal sealed class PostgresIdempotencyStore : IdempotencyStore
{
    private readonly NpgsqlDataSource db;
    private readonly TimeSpan retention;

    public PostgresIdempotencyStore(NpgsqlDataSource db, TimeSpan retention, TimeSpan staleRun, ILogger logger, IReadOnlyList<byte[]> signingSecrets)
        : base(staleRun, logger, signingSecrets)
    {
        this.db = db;
        this.retention = retention;

        // Dos réplicas que arrancan juntas no pueden crear la tabla a la vez: CREATE TABLE IF NOT EXISTS no es atómico.
        using var conn = db.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(hashtext('billingoss.module_idempotency'));
            CREATE TABLE IF NOT EXISTS module_idempotency (
              key text PRIMARY KEY,
              operation text NOT NULL,
              fingerprint text NOT NULL,
              state text NOT NULL,
              status integer,
              response bytea,
              started_at timestamptz NOT NULL,
              expires_at timestamptz NOT NULL);
            DELETE FROM module_idempotency WHERE expires_at < now();
            """, conn, tx);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>
    /// Arma la cadena para el registro. El Command Timeout es 0 porque también acota el COMMIT: con replicación
    /// sincrónica, un COMMIT cancelado mientras espera a la réplica queda confirmado solo en la primaria y se pierde
    /// en la conmutación (punto 2 de la revisión de alta disponibilidad).
    /// </summary>
    public static string ConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { CommandTimeout = 0 }.ConnectionString;

    protected override async Task<(DateTime Started, (int Status, byte[] Body)? Saved)> Claim(string key, string operation, string fingerprint)
    {
        await using var conn = await db.OpenConnectionAsync().ConfigureAwait(false);
        using var watch = new CommitWatch(conn);

        // Toma la clave si es nueva, o si quedó en curso de un proceso caído con la misma entrada.
        await using (var claim = new NpgsqlCommand("""
            INSERT INTO module_idempotency AS i (key, operation, fingerprint, state, started_at, expires_at)
            VALUES ($1, $2, $3, 'running', now(), now() + $4)
            ON CONFLICT (key) DO UPDATE SET started_at = now()
              WHERE i.state = 'running' AND i.started_at < now() - $5 AND i.operation = $2 AND i.fingerprint = $3
            RETURNING started_at
            """, conn))
        {
            claim.Parameters.AddWithValue(key);
            claim.Parameters.AddWithValue(operation);
            claim.Parameters.AddWithValue(fingerprint);
            claim.Parameters.AddWithValue(retention);
            claim.Parameters.AddWithValue(StaleRun);
            if (await claim.ExecuteScalarAsync().ConfigureAwait(false) is DateTime started)
            {
                if (watch.Uncertain)
                {
                    // La toma puede desaparecer en la conmutación: no se ejecuta, y el núcleo vuelve a intentar.
                    Logger.LogError("La toma de la clave {Key} quedó confirmada solo en la primaria: no se ejecuta.", key);
                    await TryForget(key, started).ConfigureAwait(false);
                    throw InProgress();
                }

                return (started, null);
            }
        }

        await using var read = new NpgsqlCommand("SELECT operation, fingerprint, state, status, response FROM module_idempotency WHERE key = $1", conn);
        read.Parameters.AddWithValue(key);
        await using var row = await read.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await row.ReadAsync().ConfigureAwait(false))
        {
            // La ejecución que la tenía falló y la soltó entre las dos consultas: que el núcleo reintente.
            throw InProgress();
        }

        if (row.GetString(0) != operation || row.GetString(1) != fingerprint)
        {
            throw Conflict();
        }

        return row.GetString(2) == "done" ? (default, (row.GetInt32(3), (byte[])row[4])) : throw InProgress();
    }

    protected override async Task<bool> Save(string key, DateTime started, int status, byte[] body)
    {
        await using var conn = await db.OpenConnectionAsync().ConfigureAwait(false);
        using var watch = new CommitWatch(conn);
        await using var cmd = new NpgsqlCommand(
            "UPDATE module_idempotency SET state = 'done', status = $3, response = $4 WHERE key = $1 AND started_at = $2", conn);
        cmd.Parameters.AddWithValue(key);
        cmd.Parameters.AddWithValue(started);
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.AddWithValue(body);
        var rows = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (watch.Uncertain)
        {
            Logger.LogError("La respuesta de la clave {Key} quedó confirmada solo en la primaria: si hay conmutación, el reintento repite el efecto.", key);
        }

        return rows > 0;
    }

    protected override async Task<bool> Forget(string key, DateTime started)
    {
        await using var conn = await db.OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("DELETE FROM module_idempotency WHERE key = $1 AND started_at = $2", conn);
        cmd.Parameters.AddWithValue(key);
        cmd.Parameters.AddWithValue(started);
        return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false) > 0;
    }

    protected override async Task Ping(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT 1", conn) { CommandTimeout = 2 };
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private async Task TryForget(string key, DateTime started)
    {
        try
        {
            await Forget(key, started).ConfigureAwait(false);
        }
        catch (DbException)
        {
        }
    }

    /// <summary>
    /// Escucha los avisos de la conexión: PostgreSQL contesta con un WARNING cuando cancela la espera de la réplica
    /// sincrónica y termina el COMMIT igual, visible solo en la primaria (syncrep.c). Ese éxito es incierto.
    /// </summary>
    private sealed class CommitWatch : IDisposable
    {
        private readonly NpgsqlConnection conn;

        public CommitWatch(NpgsqlConnection conn)
        {
            this.conn = conn;
            conn.Notice += OnNotice;
        }

        public bool Uncertain { get; private set; }

        public void Dispose() => conn.Notice -= OnNotice;

        private void OnNotice(object sender, NpgsqlNoticeEventArgs e)
        {
            // InvariantSeverity y no Severity: ese viene traducido según lc_messages del servidor (WARNUNG, AVISO).
            if (e.Notice.InvariantSeverity == "WARNING")
            {
                Uncertain = true;
            }
        }
    }
}
