using System.Globalization;
using System.Text;
using System.Text.Json;
using BillingOss.Updates;
using Npgsql;
using NpgsqlTypes;

namespace BillingOss.Updater;

/// <summary>Lo que el actualizador guarda de la instalación, en la única fila de updates.state.</summary>
public sealed record StateRow(
    Guid InstallationId,
    string? Channel,
    long Serial,
    DateTimeOffset? Issued,
    string[] Revoked,
    DateTimeOffset? CheckedAt,
    DateTimeOffset? AttemptAt,
    string? Version,
    string? Images,
    string? MinCoreVersion,
    string[] Blocked);

public sealed record RequestRow(long Id, string Version, string Origin, string Status, string? Phase, int Step, int NodeIndex, string? Plan, string? FromVersion, bool Expired);

public sealed record NodeRow(string Name, string? Version, string? Images, string? PreviousVersion, string? PreviousImages, bool Fresh, string? Pulled, string? Applied, string? Status, string? Detail);

/// <summary>El camino del pedido y los nodos que lo aplican, en orden. Back es la vuelta atrás del binario: sin migraciones.</summary>
public sealed record PlanDoc(IReadOnlyList<string> Path, IReadOnlyList<string> Nodes, bool Back);

/// <summary>
/// El motor del actualizador (sección 7): uno por nodo, coordinados por la base como en D-07. Cada vuelta deja la señal
/// del nodo, y el que tiene el lease de la instalación chequea el canal y lleva el pedido de paso en paso: chequeos,
/// imágenes bajadas en todos los nodos, marca de recuperación, migraciones y nodo por nodo. Cada nodo hace lo suyo con el
/// Docker propio, sin SSH entre nodos. Todo paso queda en la base: si el proceso se corta, retoma desde ahí.
/// </summary>
public sealed class UpdaterEngine(UpdaterOptions options, NpgsqlDataSource db, ComposeHost host, HttpClient http, TimeProvider clock, Action<string> log)
{
    private DateTimeOffset? selfUpdateFailedAt;

    /// <summary>La versión de este actualizador.</summary>
    public static Version Own { get; } = Versions.Of(typeof(UpdaterEngine).Assembly);

    /// <summary>El lease de la instalación: quien lo tiene chequea el canal y lleva los pedidos.</summary>
    public static TimeSpan Lease { get; } = TimeSpan.FromMinutes(2);

    /// <summary>Un nodo que dio señales en este rato está encendido.</summary>
    public static TimeSpan Fresh { get; } = TimeSpan.FromMinutes(2);

    /// <summary>La pausa del planificador vence sola si el actualizador se cae.</summary>
    public static TimeSpan Pause { get; } = TimeSpan.FromMinutes(10);

    private TimeSpan NodeTimeout => options.ReadyTimeout + options.SoakPeriod + TimeSpan.FromMinutes(10);

    /// <summary>Una vuelta. Devuelve true si se reemplazó el binario y hay que reiniciar.</summary>
    public async Task<bool> Tick(CancellationToken ct)
    {
        var (version, images) = host.Current();
        await Exec(
            """
            INSERT INTO updates.nodes (name, version, images, updater_version, seen_at) VALUES ($1, $2, $3, $4, now())
            ON CONFLICT (name) DO UPDATE SET version = excluded.version, images = excluded.images, updater_version = excluded.updater_version, seen_at = now()
            """,
            ct, options.Node, version?.Text(), Jsonb(ImageMap.ToJson(images)), Own.Text());

        var driver = await TakeLease(ct);
        if (driver && CheckDue(await State(ct)))
        {
            await Check(null, null, ct);
        }

        var state = await State(ct);
        if (await UpdateSelf(state, ct))
        {
            return true;
        }

        if (driver)
        {
            await Drive(state, ct);
        }

        await Work(await State(ct), ct);
        return false;
    }

    /// <summary>
    /// Baja el canal y su firma, o usa los que se cargaron a mano, y guarda lo que verificó. Un canal rechazado no pisa al
    /// último bueno: queda el motivo, y el panel dice "sin datos".
    /// </summary>
    /// <returns>El motivo si no quedó un canal nuevo.</returns>
    public async Task<string?> Check(byte[]? document, string? signature, CancellationToken ct)
    {
        try
        {
            if (document is null)
            {
                var url = options.ChannelUrl ?? throw new UpdateException("El chequeo de versiones está apagado: cargá el canal con check --file.");
                document = await Download(url, 1024 * 1024, ct);
                signature = Encoding.ASCII.GetString(await Download(options.SignatureUrl ?? new Uri(url + ".sig"), 16 * 1024, ct));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UpdateException)
        {
            await Exec("UPDATE updates.state SET check_attempt_at = now(), check_error = $1, check_rejected = false WHERE id = 1", ct, Short(ex.Message));
            return ex.Message;
        }

        var state = await State(ct);
        VerifiedChannel verified;
        try
        {
            verified = ChannelVerifier.Verify(document, signature ?? string.Empty, options.PublicKeys, new ChannelTrust(state.Serial, state.Issued, state.Revoked), clock.GetUtcNow());
        }
        catch (UpdateException ex)
        {
            await Exec("UPDATE updates.state SET check_attempt_at = now(), check_error = $1, check_rejected = true WHERE id = 1", ct, Short(ex.Message));
            log("Canal rechazado: " + ex.Message);
            return ex.Message;
        }

        // Con un formato que no conoce guarda solo la cabecera: alcanza para autoactualizarse, y el panel dice "sin datos".
        var error = verified.FormatKnown ? null : $"El canal viene en el formato {verified.Channel.Format}, que este actualizador no conoce: se actualiza solo o por consola.";
        await Exec(
            """
            UPDATE updates.state SET channel = $1, channel_serial = $2, channel_issued = $3, revoked_keys = $4, checked_at = now(),
                check_attempt_at = now(), check_error = $5, check_rejected = false WHERE id = 1
            """,
            ct, Jsonb(verified.Channel.ToJson()), verified.Trust.Serial, verified.Trust.Issued?.ToUniversalTime(), verified.Trust.RevokedKeys.ToArray(), error);
        log($"Canal {verified.Channel.Name} verificado: serial {verified.Trust.Serial}.");
        return error;
    }

    /// <summary>Deja un pedido de la consola: puede volver por debajo del último parche de seguridad, como manda la sección 7.5.</summary>
    public async Task<long> RequestFromConsole(string version, string by, CancellationToken ct)
    {
        var target = Versions.Parse(version);
        try
        {
            return await Scalar<long>("INSERT INTO updates.requests (version, requested_by, origin) VALUES ($1, $2, 'console') RETURNING id", ct, target.Text(), by);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new UpdateException("Ya hay una actualización pedida o en curso.");
        }
    }

    public async Task<StateRow> State(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT installation_id, channel::text, channel_serial, channel_issued, revoked_keys, checked_at, check_attempt_at, version,
                   images::text, min_core_version, blocked_versions
            FROM updates.state WHERE id = 1
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            throw new UpdateException("Falta la fila de updates.state: corré las migraciones del núcleo.");
        }

        return new StateRow(
            r.GetGuid(0), Text(r, 1), r.GetInt64(2), Time(r, 3), r.GetFieldValue<string[]>(4), Time(r, 5), Time(r, 6), Text(r, 7), Text(r, 8), Text(r, 9), r.GetFieldValue<string[]>(10));
    }

    public async Task<RequestRow?> Active(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT id, version, origin, status, phase, step, node_index, plan::text, from_version, coalesce(deadline < now(), false)
            FROM updates.requests WHERE status IN ('pending', 'running') ORDER BY id LIMIT 1
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new RequestRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), Text(r, 4), r.GetInt32(5), r.GetInt32(6), Text(r, 7), Text(r, 8), r.GetBoolean(9))
            : null;
    }

    public async Task<IReadOnlyDictionary<string, NodeRow>> Nodes(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "SELECT name, version, images::text, previous_version, previous_images::text, seen_at > now() - $1, pulled, applied, status, detail FROM updates.nodes ORDER BY name");
        cmd.Parameters.Add(new NpgsqlParameter { Value = Fresh });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var nodes = new Dictionary<string, NodeRow>(StringComparer.Ordinal);
        while (await r.ReadAsync(ct))
        {
            nodes[r.GetString(0)] = new NodeRow(r.GetString(0), Text(r, 1), Text(r, 2), Text(r, 3), Text(r, 4), r.GetBoolean(5), Text(r, 6), Text(r, 7), Text(r, 8), Text(r, 9));
        }

        return nodes;
    }

    /// <summary>El camino que seguiría un pedido, con lo que hay en la base. Lo usa la consola.</summary>
    public async Task<PlanDoc> Preview(string version, string origin, CancellationToken ct)
    {
        var state = await State(ct);
        return Planned(Versions.Parse(version), origin, state, UsableChannel(state), await Nodes(ct));
    }

    public static ReleaseChannel? Channel(StateRow state) => state?.Channel is { } json ? ReleaseChannel.Parse(json) : null;

    private ReleaseChannel UsableChannel(StateRow state)
    {
        var channel = Channel(state) ?? throw new UpdateException("Todavía no hay un canal verificado.");
        if (channel.Format != ChannelVerifier.Format)
        {
            throw new UpdateException($"El canal viene en el formato {channel.Format}, que este actualizador no conoce.");
        }

        return clock.GetUtcNow() > channel.Expires
            ? throw new UpdateException("El último canal verificado venció: sin un canal vigente no se aplica nada.")
            : channel;
    }

    private bool CheckDue(StateRow state)
    {
        if (options.ChannelUrl is null)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        var failed = state.AttemptAt is { } attempt && (state.CheckedAt is null || state.CheckedAt < attempt);
        return state.AttemptAt is not { } last || now - last >= (failed ? TimeSpan.FromMinutes(15) : options.CheckInterval);
    }

    private async Task<bool> UpdateSelf(StateRow state, CancellationToken ct)
    {
        var binary = Channel(state)?.Updater;
        var now = clock.GetUtcNow();
        if (!SelfUpdate.IsNewer(binary, Own) || options.BinaryPath is not { } path || now - selfUpdateFailedAt < TimeSpan.FromMinutes(15))
        {
            return false;
        }

        try
        {
            log($"Actualizador {Own.Text()} a {binary!.Version}.");
            return await SelfUpdate.Apply(binary, path, http, ct);
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            // Se reintenta más tarde. Un actualizador viejo sigue andando con el formato que conoce.
            selfUpdateFailedAt = now;
            log("No se pudo actualizar el actualizador: " + ex.Message);
            return false;
        }
    }

    private async Task<bool> TakeLease(CancellationToken ct) =>
        await Exec(
            """
            UPDATE updates.state SET lease_holder = $1, lease_until = now() + $2,
                scheduler_paused_until = CASE WHEN scheduler_paused_until IS NULL THEN NULL ELSE greatest(scheduler_paused_until, now() + $3) END
            WHERE id = 1 AND (lease_holder IS NULL OR lease_holder = $1 OR lease_until < now())
            """,
            ct, options.Node, Lease, Pause) == 1;

    /// <summary>Mientras dura un paso largo, renueva el lease y la pausa: una corrida no arranca a mitad de camino.</summary>
    private async Task<T> Keeping<T>(Func<Task<T>> work, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keeper = Task.Run(
            async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    try
                    {
                        await TakeLease(stop.Token);
                    }
                    catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
                    {
                        // La base puede estar reiniciando (minor de PostgreSQL en la receta chica).
                    }
                }
            },
            CancellationToken.None);
        try
        {
            return await work();
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await keeper;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    // ---- El que tiene el lease lleva el pedido ----

    private async Task Drive(StateRow state, CancellationToken ct)
    {
        var request = await Active(ct);
        if (request is null)
        {
            return;
        }

        if (request.Status == "pending")
        {
            await Start(request, state, ct);
            return;
        }

        await Exec("UPDATE updates.state SET scheduler_paused_until = now() + $1 WHERE id = 1", ct, Pause);
        var plan = PlanOf(request);
        var nodes = await Nodes(ct);
        var marker = Marker(request);
        var version = plan.Path[request.Step];
        switch (request.Phase)
        {
            case "pull":
                if (plan.Nodes.FirstOrDefault(n => Node(nodes, n).Pulled == marker + ":x") is { } failed)
                {
                    await Finish(request, "failed", $"El nodo {failed} no pudo prepararse: {Node(nodes, failed).Detail}", block: false, ct);
                }
                else if (plan.Nodes.All(n => Node(nodes, n).Pulled == marker))
                {
                    var next = plan.Back ? "nodes" : request.Step == 0 ? "mark" : "migrate";
                    await Exec("UPDATE updates.requests SET phase = $2, deadline = now() + $3 WHERE id = $1", ct, request.Id, next, NodeTimeout);
                }
                else if (request.Expired)
                {
                    var late = string.Join(", ", plan.Nodes.Where(n => Node(nodes, n).Pulled != marker));
                    await Finish(request, "failed", $"Venció el plazo para bajar las imágenes en {late}.", block: false, ct);
                }

                break;

            case "mark":
                try
                {
                    var mark = await Keeping(() => Mark(request, plan, ct), ct);
                    await Progress(request.Id, "Mark", ct, detail: mark);
                    await Exec("UPDATE updates.requests SET phase = 'migrate' WHERE id = $1", ct, request.Id);
                }
                catch (Exception ex) when (ex is UpdateException or NpgsqlException or IOException or UnauthorizedAccessException)
                {
                    await Finish(request, "failed", "No se pudo hacer la marca de recuperación: " + ex.Message, block: false, ct);
                }

                break;

            case "migrate":
                var release = ReleaseOf(state, version);
                var problem = await Keeping(() => Migrate(release, ct), ct);
                if (problem is null)
                {
                    await Progress(request.Id, "Migrated", ct, version: version);
                    await Exec("UPDATE updates.requests SET phase = 'nodes', node_index = 0, deadline = now() + $2 WHERE id = $1", ct, request.Id, NodeTimeout);
                }
                else
                {
                    // Nada cambió en los nodos, y lo aplicado del esquema lo soporta la versión que sigue atendiendo.
                    await Finish(request, "failed", problem, block: false, ct);
                }

                break;

            case "nodes":
                var name = plan.Nodes[request.NodeIndex];
                var node = Node(nodes, name);
                if (node.Applied == marker)
                {
                    await NodeDone(request, plan, version, ReleaseOf(state, version), ct);
                }
                else if (node.Applied == marker + ":x" || request.Expired)
                {
                    var reason = node.Applied == marker + ":x" ? $"Falló el nodo {name}: {node.Detail}" : $"El nodo {name} no terminó a tiempo.";
                    await Exec("UPDATE updates.requests SET phase = 'rollback', error = $2, deadline = now() + $3 WHERE id = $1", ct, request.Id, Short(reason), NodeTimeout);
                    await Progress(request.Id, "RollingBack", ct, node: name);
                }

                break;

            case "rollback":
                // Vuelven los nodos que ya habían cambiado en este paso. El que falló volvió solo.
                var stuck = plan.Nodes.Where(n => Node(nodes, n).Applied == marker).ToArray();
                if (stuck.Length == 0 || request.Expired)
                {
                    // El que no volvió a tiempo (apagado o sin Docker) queda con falla a la vista: su actualizador lo pone al día cuando vuelve.
                    await Exec(
                        "UPDATE updates.nodes SET status = 'failed', detail = $3 WHERE name = ANY($1) AND applied = $2",
                        ct, stuck, marker, $"No volvió antes del plazo y sigue con {version}, que quedó bloqueada. Su actualizador lo pone al día cuando vuelve.");
                    await Finish(request, "failed", null, block: true, ct);
                }

                break;
        }
    }

    private async Task Start(RequestRow request, StateRow state, CancellationToken ct)
    {
        string? problem;
        PlanDoc? plan = null;
        var nodes = await Nodes(ct);
        try
        {
            plan = Planned(Versions.Parse(request.Version), request.Origin, state, UsableChannel(state), nodes);

            // Primero la pausa y después los chequeos: entre el chequeo y el cambio podría arrancar una corrida.
            await Exec("UPDATE updates.state SET scheduler_paused_until = now() + $1 WHERE id = 1", ct, Pause);
            problem = await Prechecks(state, plan, nodes, ct);
        }
        catch (UpdateException ex)
        {
            problem = ex.Message;
        }

        if (problem is not null)
        {
            await Exec("UPDATE updates.requests SET status = 'rejected', error = $2, finished_at = now() WHERE id = $1", ct, request.Id, Short(problem));
            await Progress(request.Id, "Rejected", ct, detail: problem);
            await Exec("UPDATE updates.state SET scheduler_paused_until = NULL WHERE id = 1", ct);
            log($"Pedido {request.Id} rechazado: {problem}");
            return;
        }

        await Exec(
            """
            UPDATE updates.requests SET status = 'running', phase = 'pull', step = 0, node_index = 0, plan = $2, from_version = $3,
                started_at = now(), deadline = now() + $4 WHERE id = $1
            """,
            ct, request.Id, Jsonb(JsonSerializer.Serialize(plan)), state.Version ?? CommonVersion(nodes), options.PullTimeout);
        await Progress(request.Id, "Started", ct, version: request.Version, detail: string.Join(" > ", plan!.Path));
        log($"Pedido {request.Id}: {string.Join(" > ", plan.Path)} en {string.Join(", ", plan.Nodes)}.");
    }

    /// <summary>El camino y los nodos. Valida todo de nuevo: la web solo deja un número de versión.</summary>
    private PlanDoc Planned(Version target, string origin, StateRow state, ReleaseChannel channel, IReadOnlyDictionary<string, NodeRow> nodes)
    {
        var installed = Versions.Parse(state.Version ?? CommonVersion(nodes) ?? throw new UpdateException("No se sabe qué versión corre la instalación: los nodos no la informan."));
        var on = nodes.Values.Where(n => n.Fresh).Select(n => n.Name).Order(StringComparer.Ordinal).ToList();
        if (on.Count == 0)
        {
            throw new UpdateException("No hay ningún nodo con su actualizador encendido.");
        }

        if (target > installed)
        {
            var path = UpdatePlanner.Plan(channel, installed, target, state.Blocked);
            if (origin == "web" && UpdatePlanner.SecurityFloor(channel, target) is { } floor && target < floor)
            {
                throw new UpdateException($"Desde el panel no se puede pedir {target.Text()}: su línea tiene el parche de seguridad {floor.Text()}.");
            }

            return new PlanDoc([.. path.Select(r => r.ReleaseVersion)], on, Back: false);
        }

        if (target == installed)
        {
            throw new UpdateException($"La instalación ya está en {installed.Text()}.");
        }

        // Vuelta atrás del binario (sección 7.5): la anterior soporta el esquema nuevo mientras no haya otra minor en el medio.
        if (!UpdatePlanner.Offered(channel, state.Blocked).Any(r => r.Version == target))
        {
            throw new UpdateException($"La versión {target.Text()} no está en el canal, fue retirada o está bloqueada en esta instalación.");
        }

        var between = channel.Releases.Select(r => r.Version.Line()).Any(l => l.CompareTo(target.Line()) > 0 && l.CompareTo(installed.Line()) < 0);
        if (between || target.Major != installed.Major)
        {
            throw new UpdateException($"No se vuelve de {installed.Text()} a {target.Text()}: hay otra minor en el medio.");
        }

        if (Versions.TryParse(state.MinCoreVersion, out var minimum) && target < minimum)
        {
            throw new UpdateException($"El esquema ya no soporta la versión {target.Text()}: la más vieja que soporta es {minimum.Text()}.");
        }

        if (origin == "web" && UpdatePlanner.SecurityFloor(channel, target) is { } securityFloor && target < securityFloor)
        {
            throw new UpdateException($"Desde el panel no se vuelve por debajo del último parche de seguridad de la línea ({securityFloor.Text()}): se hace por consola en el host.");
        }

        return new PlanDoc([target.Text()], on, Back: true);
    }

    /// <summary>Chequeos previos que frenan si fallan (sección 7.4, paso 2). Los del disco y Docker los hace cada nodo al bajar.</summary>
    private async Task<string?> Prechecks(StateRow state, PlanDoc plan, IReadOnlyDictionary<string, NodeRow> nodes, CancellationToken ct)
    {
        var problems = new List<string>();
        var installed = state.Version ?? CommonVersion(nodes);
        var off = plan.Nodes.Where(n => Node(nodes, n).Version != installed).ToList();
        if (off.Count > 0)
        {
            problems.Add($"Los nodos {string.Join(", ", off)} no están en la versión de la instalación ({installed}).");
        }

        if (await Scalar<long>("SELECT count(*) FROM platform.jobs WHERE status = 'Running' AND kind IN ('billing.run', 'fiscal.emit') AND locked_until > now()", ct) > 0)
        {
            problems.Add("Hay una corrida del ciclo de facturación o una emisión fiscal en curso.");
        }

        if (await Scalar<long>("SELECT count(*) FROM fiscal.emissions WHERE status = 'Sending'", ct) > 0)
        {
            problems.Add("Hay una emisión fiscal esperando respuesta del fisco.");
        }

        if (options.RequireSyncReplica && await Scalar<long>("SELECT count(*) FROM pg_stat_replication WHERE sync_state IN ('sync', 'quorum')", ct) == 0)
        {
            problems.Add("La base no tiene réplica sincrónica.");
        }

        if (options.RecoveryMark == "restore-point"
            && await Scalar<bool>("SELECT archived_count = 0 OR coalesce(last_failed_time > last_archived_time, false) FROM pg_stat_archiver", ct))
        {
            problems.Add("El archivado de WAL no está al día: la marca de recuperación no serviría para volver.");
        }

        var skew = (await DbNow(ct) - clock.GetUtcNow()).Duration();
        if (skew > TimeSpan.FromSeconds(5))
        {
            problems.Add($"El reloj de este nodo difiere {skew.TotalSeconds:0} s del de la base: sincronizalo.");
        }

        if (!plan.Back)
        {
            foreach (var step in plan.Path)
            {
                var release = ReleaseOf(state, step);
                if (release.Modules.FirstOrDefault(m => Versions.TryParse(m.MinCore, out var min) && min > release.Version) is { } module)
                {
                    problems.Add($"El módulo {module.Id} de {step} pide un núcleo {module.MinCore}.");
                }
            }
        }

        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    private async Task<string> Mark(RequestRow request, PlanDoc plan, CancellationToken ct)
    {
        var target = plan.Path[^1];
        switch (options.RecoveryMark)
        {
            case "restore-point":
                // Con la versión y el pedido en el nombre: con dos marcas iguales, la recuperación para en la primera.
                var name = $"billing-oss-{target}-pedido-{request.Id.ToString(CultureInfo.InvariantCulture)}";
                var lsn = await Scalar<string>("SELECT pg_create_restore_point($1)::text", ct, name);
                return $"{name} ({lsn})";

            case "dump":
                PrivateFile.Directory(options.DumpDirectory);
                var file = Path.Combine(options.DumpDirectory, $"billing-oss-{request.FromVersion}-a-{target}-pedido-{request.Id.ToString(CultureInfo.InvariantCulture)}.dump");
                await host.Dump(file, options.Database, ct);

                // Se conservan los dos últimos: un parche por mes no llena el disco.
                foreach (var old in new DirectoryInfo(options.DumpDirectory).GetFiles("billing-oss-*.dump").OrderByDescending(f => f.LastWriteTimeUtc).Skip(2))
                {
                    old.Delete();
                }

                return Path.GetFileName(file);

            case "none":
                return "sin marca (RecoveryMark: none)";

            default:
                throw new UpdateException($"RecoveryMark desconocido: {options.RecoveryMark}.");
        }
    }

    /// <summary>Las migraciones una sola vez (lo asegura el lease). Reintenta solo si falla por lock_timeout.</summary>
    private async Task<string?> Migrate(Release release, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await host.Migrate(release.Images.Nucleo!, ct);
            if (result.Ok)
            {
                return null;
            }

            var lockTimeout = result.Output.Contains("55P03", StringComparison.Ordinal) || result.Output.Contains("lock timeout", StringComparison.OrdinalIgnoreCase);
            if (!lockTimeout || attempt == 4)
            {
                return $"Las migraciones de {release.ReleaseVersion} fallaron: {ComposeHost.Tail(result.Output)}";
            }

            log($"Migraciones de {release.ReleaseVersion}: lock_timeout, intento {attempt}.");
            await Task.Delay(TimeSpan.FromSeconds(5 * Math.Pow(2, attempt - 1)), clock, ct);
        }
    }

    private async Task NodeDone(RequestRow request, PlanDoc plan, string version, Release release, CancellationToken ct)
    {
        if (request.NodeIndex + 1 < plan.Nodes.Count)
        {
            await Exec("UPDATE updates.requests SET node_index = node_index + 1, deadline = now() + $2 WHERE id = $1", ct, request.Id, NodeTimeout);
            return;
        }

        // Toda la instalación quedó en este paso.
        await Exec("UPDATE updates.state SET version = $1, images = $2 WHERE id = 1", ct, version, Jsonb(JsonSerializer.Serialize(release.Images, ReleaseChannel.Json)));
        if (request.Step + 1 < plan.Path.Count)
        {
            await Progress(request.Id, "StepDone", ct, version: version);
            await Exec("UPDATE updates.requests SET step = step + 1, node_index = 0, phase = 'pull', deadline = now() + $2 WHERE id = $1", ct, request.Id, options.PullTimeout);
            return;
        }

        await Finish(request, "done", null, block: false, ct);
    }

    /// <summary>Cierra el pedido y levanta la pausa. Si un nodo falló, la versión queda bloqueada en esta instalación.</summary>
    private async Task Finish(RequestRow request, string status, string? error, bool block, CancellationToken ct)
    {
        var plan = PlanOf(request);
        if (block)
        {
            string[] versions = [plan.Path[request.Step], plan.Path[^1]];
            await Exec("UPDATE updates.state SET blocked_versions = ARRAY(SELECT DISTINCT unnest(blocked_versions || $1) ORDER BY 1) WHERE id = 1", ct, versions.Distinct(StringComparer.Ordinal).ToArray());
        }

        await Exec("UPDATE updates.requests SET status = $2, error = coalesce($3, error), finished_at = now(), phase = NULL WHERE id = $1", ct, request.Id, status, error is null ? null : Short(error));
        await Progress(request.Id, status == "done" ? "Done" : "Failed", ct, version: plan.Path[^1], detail: error);
        await Exec("UPDATE updates.state SET scheduler_paused_until = NULL WHERE id = 1", ct);
        log($"Pedido {request.Id}: {status}{(error is null ? string.Empty : ". " + error)}");
    }

    // ---- Lo de cada nodo ----

    private async Task Work(StateRow state, CancellationToken ct)
    {
        var request = await Active(ct);
        if (request is not { Status: "running" })
        {
            await CatchUp(state, ct);
            return;
        }

        var plan = PlanOf(request);
        if (!plan.Nodes.Contains(options.Node))
        {
            return;
        }

        var me = Node(await Nodes(ct), options.Node);
        var marker = Marker(request);
        var version = plan.Path[request.Step];
        switch (request.Phase)
        {
            case "pull" when me.Pulled != marker && me.Pulled != marker + ":x":
                try
                {
                    await NodeChecks(ct);
                    var images = ComposeHost.ImagesFor(await host.Services(ct), ReleaseOf(state, version).Images, await PostgresMajor(ct));
                    await Keeping(
                        async () =>
                        {
                            await host.Pull(images.Values, ct);
                            return true;
                        },
                        ct);

                    // Lo que corre ahora es lo anterior de este paso: a eso vuelve si algo falla.
                    var (current, running) = host.Current();
                    await Exec(
                        "UPDATE updates.nodes SET pulled = $2, previous_version = $3, previous_images = $4, status = 'ok', detail = NULL WHERE name = $1",
                        ct, options.Node, marker, current?.Text(), Jsonb(ImageMap.ToJson(running)));
                    await Progress(request.Id, "Pulled", ct, node: options.Node, version: version);
                }
                catch (Exception ex) when (ex is UpdateException or IOException)
                {
                    await Exec("UPDATE updates.nodes SET pulled = $2, status = 'failed', detail = $3 WHERE name = $1", ct, options.Node, marker + ":x", Short(ex.Message));
                }

                break;

            case "nodes" when plan.Nodes[request.NodeIndex] == options.Node && me.Applied != marker && me.Applied != marker + ":x":
                await Keeping(() => ApplyHere(request, marker, version, ReleaseOf(state, version), me, ct), ct);
                break;

            case "rollback" when me.Applied == marker:
                await Keeping(
                    async () =>
                    {
                        await Revert(me, ct);
                        await Exec("UPDATE updates.nodes SET applied = $2, version = previous_version, images = previous_images WHERE name = $1", ct, options.Node, $"{request.Id.ToString(CultureInfo.InvariantCulture)}:back");
                        await Progress(request.Id, "RolledBack", ct, node: options.Node, version: me.PreviousVersion);
                        return true;
                    },
                    ct);
                break;
        }
    }

    /// <summary>Recrea los contenedores del nodo con los digests nuevos y espera listo, el período de prueba y el humo.</summary>
    private async Task<bool> ApplyHere(RequestRow request, string marker, string version, Release release, NodeRow me, CancellationToken ct)
    {
        await Exec("UPDATE updates.nodes SET status = 'applying', detail = NULL WHERE name = $1", ct, options.Node);
        var previous = ImageMap.FromJson(me.PreviousImages);
        var images = ComposeHost.ImagesFor(await host.Services(ct), release.Images, await PostgresMajor(ct));
        var postgresFirst = images.TryGetValue("postgres", out var postgres) && previous?.GetValueOrDefault("postgres") != postgres;
        try
        {
            host.Write(Versions.Parse(version), images);
            await host.Up(postgresFirst, ct);
            await WaitReady(ct);
            await Soak(ct);
            foreach (var url in options.SmokeUrls)
            {
                using var response = await http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new UpdateException($"La prueba de humo {url.AbsolutePath} dio {(int)response.StatusCode}.");
                }
            }
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            var logs = await host.Logs(ct);
            try
            {
                await Revert(me, ct);
            }
            catch (Exception revert) when (revert is UpdateException or HttpRequestException or IOException)
            {
                logs = $"No volvió solo: {revert.Message}. {logs}";
            }

            await Exec(
                "UPDATE updates.nodes SET applied = $2, status = 'failed', detail = $3, version = previous_version, images = previous_images WHERE name = $1",
                ct, options.Node, marker + ":x", Short($"{ex.Message} {logs}"));
            await Progress(request.Id, "NodeFailed", ct, node: options.Node, version: version, detail: ex.Message);
            return false;
        }

        await Exec(
            "UPDATE updates.nodes SET applied = $2, version = $3, images = $4, status = 'ok', detail = NULL WHERE name = $1",
            ct, options.Node, marker, version, Jsonb(ImageMap.ToJson(images)));
        await Progress(request.Id, "NodeApplied", ct, node: options.Node, version: version);
        return true;
    }

    /// <summary>Vuelve este nodo a los digests anteriores: el esquema nuevo los soporta (sección 7.4).</summary>
    private async Task Revert(NodeRow me, CancellationToken ct)
    {
        var previous = ImageMap.FromJson(me.PreviousImages);
        var (_, now) = host.Current();
        host.Write(Versions.TryParse(me.PreviousVersion, out var v) ? v : null, previous);
        await host.Up(previous?.GetValueOrDefault("postgres") is { } pg && now?.GetValueOrDefault("postgres") != pg, ct);
        await WaitReady(ct);
    }

    /// <summary>
    /// Un nodo que estuvo apagado mientras pasaba una versión, o mientras la instalación volvía atrás, se pone al día con
    /// los digests de la instalación, para arriba o para abajo: el esquema soporta la anterior (sección 7.4). Sin versión
    /// registrada todavía, vuelve a lo anterior solo el nodo que aplicó un paso de un pedido que falló y no llegó a volver.
    /// </summary>
    private async Task CatchUp(StateRow state, CancellationToken ct)
    {
        var (current, _) = host.Current();
        if (current is null)
        {
            return;
        }

        Version? target;
        Dictionary<string, string>? images;
        if (Versions.TryParse(state.Version, out var installed) && state.Images is not null)
        {
            if (current == installed)
            {
                return;
            }

            target = installed;
            images = ComposeHost.ImagesFor(await host.Services(ct), JsonSerializer.Deserialize<ReleaseImages>(state.Images, ReleaseChannel.Json)!, await PostgresMajor(ct));
        }
        else
        {
            var me = Node(await Nodes(ct), options.Node);
            if (me.Applied is null || me.Applied.EndsWith(":x", StringComparison.Ordinal) || me.Applied.EndsWith(":back", StringComparison.Ordinal))
            {
                return;
            }

            // Sin imágenes anteriores vuelve a las etiquetas del .env, como arrancó la instalación.
            target = Versions.TryParse(me.PreviousVersion, out var previous) ? previous : null;
            images = ImageMap.FromJson(me.PreviousImages);
        }

        log($"Este nodo corre {current.Text()} y la instalación {target?.Text() ?? "lo anterior"}: se pone al día.");
        if (images is not null)
        {
            await host.Pull(images.Values, ct);
        }

        host.Write(target, images);
        await host.Up(postgresFirst: false, ct);
        await WaitReady(ct);
        await Exec(
            "UPDATE updates.nodes SET version = $2, images = $3, applied = CASE WHEN $4 THEN applied ELSE applied || ':back' END, status = 'ok', detail = NULL WHERE name = $1",
            ct, options.Node, target?.Text(), Jsonb(ImageMap.ToJson(images)), state.Images is not null);
    }

    /// <summary>Espacio en disco, Docker y reloj de este nodo, antes de bajar nada.</summary>
    private async Task NodeChecks(CancellationToken ct)
    {
        if (Directory.Exists(options.DiskPath) && new DriveInfo(options.DiskPath).AvailableFreeSpace < options.MinFreeBytes)
        {
            throw new UpdateException($"Poco espacio libre en {options.DiskPath}.");
        }

        if (await host.DockerVersion(ct) is not { } docker || docker < options.MinDockerVersion)
        {
            throw new UpdateException($"Docker tiene que ser {options.MinDockerVersion} o posterior.");
        }

        var skew = (await DbNow(ct) - clock.GetUtcNow()).Duration();
        if (skew > TimeSpan.FromSeconds(5))
        {
            throw new UpdateException($"El reloj de este nodo difiere {skew.TotalSeconds:0} s del de la base.");
        }
    }

    private async Task WaitReady(CancellationToken ct)
    {
        var until = clock.GetUtcNow() + options.ReadyTimeout;
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(options.HealthUrl, ct);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            if (clock.GetUtcNow() > until)
            {
                throw new UpdateException($"El nodo no quedó listo en {options.ReadyTimeout.TotalSeconds:0} s.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), clock, ct);
        }
    }

    /// <summary>Período de prueba: el nodo tiene que seguir listo todo este rato.</summary>
    private async Task Soak(CancellationToken ct)
    {
        var until = clock.GetUtcNow() + options.SoakPeriod;
        while (clock.GetUtcNow() < until)
        {
            var wait = until - clock.GetUtcNow();
            await Task.Delay(wait < TimeSpan.FromSeconds(10) ? wait : TimeSpan.FromSeconds(10), clock, ct);
            using var response = await http.GetAsync(options.HealthUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"El nodo dejó de estar listo durante el período de prueba ({(int)response.StatusCode}).");
            }
        }
    }

    private async Task<DateTimeOffset> DbNow(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT now()");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return r.GetFieldValue<DateTimeOffset>(0);
    }

    private async Task<int?> PostgresMajor(CancellationToken ct) => await Scalar<int>("SELECT current_setting('server_version_num')::int / 10000", ct);

    private Release ReleaseOf(StateRow state, string version) =>
        Channel(state)?.Find(Versions.Parse(version)) ?? throw new UpdateException($"La versión {version} ya no está en el canal verificado.");

    private static string? CommonVersion(IReadOnlyDictionary<string, NodeRow> nodes)
    {
        var versions = nodes.Values.Where(n => n.Fresh).Select(n => n.Version).Distinct().ToList();
        return versions.Count == 1 ? versions[0] : null;
    }

    private static NodeRow Node(IReadOnlyDictionary<string, NodeRow> nodes, string name) =>
        nodes.TryGetValue(name, out var node) ? node : new NodeRow(name, null, null, null, null, false, null, null, null, null);

    private static PlanDoc PlanOf(RequestRow request) =>
        JsonSerializer.Deserialize<PlanDoc>(request.Plan ?? "null") ?? throw new UpdateException($"El pedido {request.Id} no tiene plan.");

    private static string Marker(RequestRow request) => string.Create(CultureInfo.InvariantCulture, $"{request.Id}:{request.Step}");

    private static string Short(string text) => text.Length <= 1900 ? text : text[..1900] + "…";

    private Task Progress(long id, string evt, CancellationToken ct, string? node = null, string? version = null, string? detail = null) =>
        Exec(
            """
            UPDATE updates.requests SET progress = progress || jsonb_build_array(jsonb_strip_nulls(jsonb_build_object(
                'at', now(), 'event', $2::text, 'node', $3::text, 'version', $4::text, 'detail', $5::text))) WHERE id = $1
            """,
            ct, id, evt, node, version, detail is null ? null : Short(detail));

    private async Task<byte[]> Download(Uri url, int max, CancellationToken ct)
    {
        // Un GET anónimo: sin versión instalada ni identificadores, tampoco en el User-Agent (sección 6).
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max)
            {
                throw new UpdateException($"{url} pasa el tope de {max} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string? Text(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? Time(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    private static NpgsqlParameter Jsonb(string? json) => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)json ?? DBNull.Value };

    private async Task<int> Exec(string sql, CancellationToken ct, params object?[] args)
    {
        await using var cmd = Command(sql, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<T> Scalar<T>(string sql, CancellationToken ct, params object?[] args)
    {
        await using var cmd = Command(sql, args);
        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private NpgsqlCommand Command(string sql, object?[] args)
    {
        var cmd = db.CreateCommand(sql);
        foreach (var arg in args)
        {
            cmd.Parameters.Add(arg as NpgsqlParameter ?? new NpgsqlParameter { Value = arg ?? DBNull.Value });
        }

        return cmd;
    }
}
