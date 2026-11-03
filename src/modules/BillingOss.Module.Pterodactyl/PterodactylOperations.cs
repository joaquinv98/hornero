using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Module.Pterodactyl;

/// <summary>
/// Las operaciones del módulo (sección 9.1 del protocolo). No guardan nada entre pedidos: el server se encuentra
/// siempre por su external_id, la marca con el uuid del servicio (o el ID en los importados), así cualquier réplica
/// atiende cualquier llamada y una repetición converge aunque se haya perdido el registro de idempotencia.
/// </summary>
internal sealed class PterodactylOperations(PanelApi api)
{
    public const string SetPanelPassword = "set_panel_password";

    /// <summary>
    /// ponytail: una sola alta multipuerto por panel a la vez dentro de la réplica, como pide PTR-07. Entre réplicas
    /// quedan los bloques que no se pisan, el 422 del panel y la relectura de los puertos. Si las altas multipuerto
    /// simultáneas en varias réplicas se vuelven comunes, un lease por instancia en el núcleo.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PortGates = new(StringComparer.Ordinal);

    // ---- Prueba de la instancia ----

    /// <summary>Solo lecturas (PTR-05). Los permisos de escritura no se prueban llamando la acción.</summary>
    public async Task<ModuleResponse> Test(ModuleCall call)
    {
        var panel = Panel.From(call);
        var missing = new List<string>();
        var ct = call.Aborted;

        var nodes = await Probe(panel, "nodes?per_page=1", "nodes", missing, ct).ConfigureAwait(false);
        var node = First(nodes);
        if (nodes is not null && node is null)
        {
            throw Fail.InvalidConfig(["panel_url"], "El panel no tiene ningún nodo.");
        }

        // Sin permiso de allocations el alta devolvía null_resource sin dar error (2026-06-30): se detecta acá.
        if (node is not null)
        {
            await Probe(panel, $"nodes/{node}/allocations?per_page=1", "allocations", missing, ct).ConfigureAwait(false);
        }

        await Probe(panel, "users?per_page=1", "users", missing, ct).ConfigureAwait(false);
        await Probe(panel, "locations?per_page=1", "locations", missing, ct).ConfigureAwait(false);
        var nests = await Probe(panel, "nests?per_page=1", "nests", missing, ct).ConfigureAwait(false);
        if (First(nests) is { } nest)
        {
            await Probe(panel, $"nests/{nest}/eggs?per_page=1", "eggs", missing, ct).ConfigureAwait(false);
        }

        await Probe(panel, "servers?per_page=1", "servers", missing, ct).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            throw new ModuleException(
                ErrorCodes.InvalidConfig,
                $"La application_key no tiene permiso de lectura sobre: {string.Join(", ", missing)}.",
                details: new JsonObject { ["fields"] = Fail.Strings(["application_key"]), ["missing"] = Fail.Strings(missing) });
        }

        return ModuleResponse.Completed(new JsonObject
        {
            ["nodes"] = nodes.Field("meta").Field("pagination").Field("total").Int(),
        });
    }

    // ---- Validación sin efectos ----

    public async Task<ModuleResponse> Validate(ModuleCall call)
    {
        var panel = Panel.From(call);
        var context = CallContext.Read(call);
        var plan = Plan.From(context.Service?.Params);
        var order = plan.Order(context.Service?.Order);
        var egg = await Egg(panel, plan, order.EggId, call.Aborted).ConfigureAwait(false);

        // Cada dato del pedido y cada variable de puerto tiene que existir en el egg: si no, el alta no los aplicaría.
        var bad = plan.OrderFields.Select(f => f.Key!).Where(k => k != "egg_id" && !Exists(egg, k)).Select(k => $"params.order_fields.{k}")
            .Concat((plan.Ports?.Variables.Keys ?? []).Where(k => !Exists(egg, k)).Select(k => $"params.extra_ports.variables.{k}"))
            .ToList();
        return bad.Count > 0
            ? throw Fail.InvalidInput(bad, $"El egg {egg.Id} no tiene estas variables: {string.Join(", ", bad)}.")
            : ModuleResponse.Completed(new JsonObject());
    }

    // ---- Alta ----

    /// <summary>
    /// Alta (PTR-06). Si el server ya existe, no crea nada y converge. Termina apenas existe el server, también
    /// mientras el panel instala.
    /// </summary>
    public async Task<ModuleResponse> Create(ModuleCall call)
    {
        var panel = Panel.From(call);
        var context = CallContext.Read(call);
        var plan = Plan.From(context.Service?.Params);

        // La consulta previa: el server con la marca del servicio. Los datos del pedido se controlan solo al crear: un
        // server que ya existe converge aunque el producto ya no ofrezca lo que eligió el cliente.
        var server = await Find(panel, context, call.Aborted).ConfigureAwait(false) is { } found
            ? Owned(found, await AccountUser(panel, context.Account, call.Aborted).ConfigureAwait(false))
            : await New(panel, context, plan, plan.Order(context.Service?.Order), call.Aborted).ConfigureAwait(false);
        if (plan.Ports is { } ports)
        {
            // Releído: el panel valida que las allocations estén libres antes de su transacción, así que otra alta
            // simultánea pudo quedarse con alguno de los puertos después del POST. EnsurePorts completa lo que falte.
            server = await Find(panel, context, call.Aborted).ConfigureAwait(false) ?? server;
            server = await EnsurePorts(panel, server, ports, call.Aborted).ConfigureAwait(false);
        }

        return Created(server);
    }

    private async Task<PanelServer> New(Panel panel, CallContext context, Plan plan, OrderValues order, CancellationToken ct)
    {
        var service = context.ServiceId;
        var user = await User(panel, context.Account, ct).ConfigureAwait(false);
        var egg = await Egg(panel, plan, order.EggId, ct).ConfigureAwait(false);
        var environment = Package.CreateEnvironment(plan, egg, order);
        var gate = plan.Ports is null ? null : PortGates.GetOrAdd(panel.Api.AbsoluteUri, _ => new SemaphoreSlim(1, 1));
        if (gate is not null)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var body = new JsonObject
                {
                    ["external_id"] = context.Mark,
                    ["name"] = plan.ServerName ?? $"Servicio {service.ToString(CultureInfo.InvariantCulture)}",
                    ["user"] = user,
                    ["egg"] = egg.Id,
                    ["docker_image"] = egg.DefaultImage,
                    ["startup"] = plan.Startup ?? egg.Startup,
                    ["limits"] = new JsonObject
                    {
                        ["memory"] = plan.MemoryMb,
                        ["swap"] = plan.SwapMb,
                        ["disk"] = plan.DiskMb,
                        ["io"] = plan.IoWeight,
                        ["cpu"] = plan.CpuPercent,
                    },

                    // Siempre enteros: un databases null en el alta es "sin límite" y después el cambio de plan daba distinto.
                    ["feature_limits"] = new JsonObject
                    {
                        ["databases"] = plan.Databases,
                        ["allocations"] = plan.Allocations,
                        ["backups"] = plan.Backups,
                    },
                    ["oom_disabled"] = plan.OomDisabled,
                    ["start_on_completion"] = true,
                };

                try
                {
                    var variables = new Dictionary<string, string>(environment, StringComparer.Ordinal);
                    if (plan.Ports is { } ports)
                    {
                        // Los puertos se eligen antes del alta: sin lugar no se crea nada.
                        var block = await PickPorts(panel, plan, ports, service + attempt, ct).ConfigureAwait(false)
                            ?? throw Fail.Rejected("no_free_ports", $"No hay {ports.Count + 1} puertos libres en una misma IP de un nodo de la location {plan.LocationId}.");
                        body["allocation"] = new JsonObject
                        {
                            ["default"] = block[0].Id,
                            ["additional"] = new JsonArray(block.Skip(1).Select(a => (JsonNode?)a.Id).ToArray()),
                        };
                        foreach (var (name, port) in Ports.Assign(block, ports))
                        {
                            variables[name] = port;
                        }
                    }
                    else
                    {
                        body["deploy"] = new JsonObject
                        {
                            ["locations"] = new JsonArray(plan.LocationId),
                            ["dedicated_ip"] = false,
                            ["port_range"] = new JsonArray(),
                        };
                    }

                    body["environment"] = new JsonObject(variables.Select(v => KeyValuePair.Create(v.Key, (JsonNode?)v.Value)));
                    var (status, json) = await api.Send(panel, HttpMethod.Post, "servers?include=allocations", body, CancellationToken.None).ConfigureAwait(false);
                    var created = status == 404 ? throw Fail.InvalidInput(["params.egg_id"], "El panel no encontró el egg o el usuario del alta.") : PanelServer.Parse(json);
                    return created.ExternalId == body["external_id"]!.GetValue<string>()
                        ? created
                        : throw Fail.Malformed("el server creado no tiene el external_id del servicio");
                }
                catch (ModuleException ex) when (ex.Code == ErrorCodes.InvalidInput && Fail.Fields(ex).Contains("external_id"))
                {
                    // El external_id es único en el panel: otra réplica creó este mismo server al mismo tiempo, con el mismo
                    // usuario. Si es de otro usuario, es un server ajeno con el mismo ID.
                    return Owned(
                        await Find(panel, context, CancellationToken.None).ConfigureAwait(false)
                            ?? throw Fail.Unavailable("El panel dice que el server ya existe pero no lo encuentra por su external_id."),
                        user);
                }
                catch (ModuleException ex) when (ex.Code == ErrorCodes.InvalidInput && Fail.Fields(ex).Any(f => f.StartsWith("allocation.", StringComparison.Ordinal)))
                {
                    // Otra alta tomó alguno de los puertos entre la lectura y el POST: se vuelve a elegir, hasta tres veces.
                    // Después es un estado pasajero del nodo, no un dato inválido del servicio.
                    if (attempt >= 2)
                    {
                        throw new ModuleException(
                            ErrorCodes.Conflict,
                            $"Otras altas tomaron los puertos elegidos en {attempt + 1} intentos seguidos. Se vuelve a intentar.",
                            30,
                            new JsonObject { ["upstream"] = new JsonObject { ["reason"] = "ports_taken" } });
                    }
                }
                catch (ModuleException ex) when (ex.Code == ErrorCodes.Rejected && Fail.HttpStatus(ex) == 400)
                {
                    // El 400 NoViableNodeException lo da el POST con deploy, o nodes/deployable al elegir los puertos.
                    throw Fail.Rejected(
                        "no_deployable_node",
                        $"Ningún nodo de la location {plan.LocationId} tiene lugar para el server. {ex.Message}",
                        new JsonObject { ["http_status"] = 400 });
                }
            }
        }
        finally
        {
            gate?.Release();
        }
    }

    /// <summary>Un bloque de puertos libres en el primer nodo de la location con lugar para el server.</summary>
    private async Task<IReadOnlyList<Alloc>?> PickPorts(Panel panel, Plan plan, PortsPlan ports, long seed, CancellationToken ct)
    {
        var query = string.Create(CultureInfo.InvariantCulture, $"nodes/deployable?memory={plan.MemoryMb}&disk={plan.DiskMb}&location_ids%5B%5D={plan.LocationId}");
        foreach (var node in await api.List(panel, query, ct).ConfigureAwait(false))
        {
            var id = node.Field("id").Int() ?? throw Fail.Malformed("nodo sin id");
            var allocations = await api.List(panel, $"nodes/{id}/allocations", ct).ConfigureAwait(false);
            if (Ports.Pick(allocations.Select(Alloc.Parse), ports, seed) is { } block)
            {
                return block;
            }
        }

        return null;
    }

    /// <summary>
    /// Que el server tenga sus puertos adicionales y las variables apuntando a ellos (PTR-07). Completa lo que falte,
    /// por ejemplo en un alta repetida o en un server viejo que quedó con un solo puerto.
    /// </summary>
    private async Task<PanelServer> EnsurePorts(Panel panel, PanelServer server, PortsPlan ports, CancellationToken ct)
    {
        var primary = server.Primary ?? throw Fail.Malformed("server sin su allocation principal");
        var extras = server.Allocations.Where(a => a.Id != primary.Id && a.Ip == primary.Ip).ToList();
        var wanted = new Dictionary<string, int>(StringComparer.Ordinal);
        var add = new List<Alloc>();
        List<Alloc>? free = null;
        async Task<List<Alloc>> Free() => free ??= (await api.List(panel, $"nodes/{server.Node}/allocations", ct).ConfigureAwait(false))
            .Select(Alloc.Parse).Where(a => !a.Assigned && a.Ip == primary.Ip).OrderBy(a => a.Port).ToList();

        if (ports.Contiguous)
        {
            // Contiguos: el puerto k es el principal más k.
            for (var k = 1; k <= ports.Count; k++)
            {
                var port = primary.Port + k;
                if (extras.All(a => a.Port != port))
                {
                    add.Add((await Free().ConfigureAwait(false)).FirstOrDefault(a => a.Port == port)
                        ?? throw Fail.Rejected("no_free_ports", $"Falta el puerto {port} del bloque contiguo y no está libre."));
                }
            }

            foreach (var (name, offset) in ports.Variables)
            {
                wanted[name] = primary.Port + offset;
            }
        }
        else
        {
            // Una variable que ya apunta a un puerto propio se respeta. Las demás toman los que queden, en orden.
            var used = new HashSet<int>();
            foreach (var (name, _) in ports.Variables.OrderBy(v => v.Value))
            {
                if (int.TryParse(server.Environment.GetValueOrDefault(name), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                    && extras.Any(a => a.Port == port) && used.Add(port))
                {
                    wanted[name] = port;
                }
            }

            if (extras.Count < ports.Count)
            {
                var more = (await Free().ConfigureAwait(false)).Take(ports.Count - extras.Count).ToList();
                add.AddRange(more.Count == ports.Count - extras.Count
                    ? more
                    : throw Fail.Rejected("no_free_ports", $"Faltan {ports.Count - extras.Count} puertos adicionales y no hay libres en {primary.Ip}."));
            }

            var pool = new Queue<int>(extras.Concat(add).Select(a => a.Port).Where(p => !used.Contains(p)).Order());
            foreach (var (name, _) in ports.Variables.OrderBy(v => v.Value).Where(v => !wanted.ContainsKey(v.Key)))
            {
                wanted[name] = pool.Dequeue();
            }
        }

        if (add.Count > 0)
        {
            var current = Current(server) with { Allocations = Math.Max(server.AllocationLimit ?? 0, ports.Count + 1) };
            server = await Build(panel, server, current, add.Select(a => a.Id)).ConfigureAwait(false);

            // El panel saltea sin error las allocations que otro server tomó entre la lectura y el PATCH. Las variables
            // no se tocan sin sus puertos: el reintento vuelve a leer los libres y a elegir.
            var missing = add.Count(a => server.Allocations.All(owned => owned.Id != a.Id));
            if (missing > 0)
            {
                throw new ModuleException(
                    ErrorCodes.Conflict,
                    $"El panel no sumó {missing} de los puertos adicionales del server {server.Id}: otro server los tomó. Se vuelven a elegir en el reintento.",
                    15,
                    new JsonObject { ["upstream"] = new JsonObject { ["reason"] = "ports_taken" } });
            }
        }

        var environment = server.Environment.ToDictionary(e => e.Key, e => e.Value ?? string.Empty, StringComparer.Ordinal);
        var changed = false;
        foreach (var (name, port) in wanted)
        {
            var text = port.ToString(CultureInfo.InvariantCulture);
            changed |= environment.GetValueOrDefault(name) != text;
            environment[name] = text;
        }

        return changed
            ? await Startup(panel, server, server.Startup, server.Egg, server.Image, environment).ConfigureAwait(false)
            : server;
    }

    /// <summary>
    /// El resultado del alta apenas existe el server (PTR-06): la instalación se ve en view y get_status. Así no queda
    /// una operación larga que la SDK guarda como respuesta de la clave y el núcleo consulta sin plazo, aunque el server
    /// se borre o la instalación no termine nunca. Una instalación que ya falló la tiene que ver el staff. Sin la
    /// allocation principal no termina: el alta del 2026-06-30 quedó sin dirección.
    /// </summary>
    private static ModuleResponse Created(PanelServer server) =>
        server.InstallFailed ? throw Fail.Rejected("install_failed", $"La instalación del server {server.Id} falló en el panel. Revisarla y reinstalar desde el admin del panel.")
        : server.Primary is null ? throw Fail.Malformed("server sin su allocation principal")
        : ModuleResponse.Completed(new JsonObject { ["observed_state"] = server.Observed, ["external_ids"] = server.Ids() });

    /// <summary>
    /// Un server que ya existe con el external_id del servicio se adopta solo si su dueño es el usuario del panel de la
    /// cuenta. Si no, es de otro cliente: por ejemplo un ID que WHMCS asignó durante el paralelo, o un server cargado
    /// a mano. Darlo por propio le mostraría al cliente un server ajeno, y una baja lo borraría.
    /// </summary>
    private static PanelServer Owned(PanelServer server, int? accountUser) =>
        server.User == accountUser
            ? server
            : throw Fail.Rejected(
                "external_id_taken",
                $"El server {server.Id} del panel tiene el external_id de este servicio pero es del usuario {server.User}, que no es el de la cuenta. Lo tiene que revisar el staff.");

    /// <summary>
    /// El usuario del panel de la cuenta, sin crearlo: por external_id y si no por correo exacto. Null si no tiene.
    /// </summary>
    private async Task<int?> AccountUser(Panel panel, AccountContext? account, CancellationToken ct)
    {
        var id = AccountId(account);
        if (await api.Get(panel, $"users/external/{id.ToString(CultureInfo.InvariantCulture)}", ct).ConfigureAwait(false) is { } found)
        {
            return UserId(found.Field("attributes"));
        }

        return Email(account) is { } email ? await ByEmail(panel, email, id, ct).ConfigureAwait(false) : null;
    }

    /// <summary>
    /// El usuario del panel de la cuenta y si no tiene, uno nuevo sin contraseña, así el panel le manda al cliente el
    /// enlace para definirla (PTR-06).
    /// </summary>
    private async Task<int> User(Panel panel, AccountContext? account, CancellationToken ct)
    {
        if (await AccountUser(panel, account, ct).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        var id = AccountId(account);
        var email = Email(account) ?? throw Fail.InvalidInput(["context.account.email"]);

        // Una sola palabra: el apellido la repite, porque el panel exige los dos.
        var words = (account!.Name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var first = words.Length > 0 ? words[0] : "Cliente";
        var last = words.Length > 1 ? string.Join(' ', words[1..]) : first;
        var body = new JsonObject
        {
            ["external_id"] = id.ToString(CultureInfo.InvariantCulture),
            ["email"] = email,
            ["username"] = RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 8),
            ["first_name"] = first,
            ["last_name"] = last,
        };
        try
        {
            var (_, json) = await api.Send(panel, HttpMethod.Post, "users", body, CancellationToken.None).ConfigureAwait(false);
            return UserId(json.Field("attributes"));
        }
        catch (ModuleException ex) when (ex.Code == ErrorCodes.InvalidInput)
        {
            // El correo y el external_id son únicos en el panel: otra alta de la misma cuenta lo creó al mismo tiempo.
            if (await AccountUser(panel, account, CancellationToken.None).ConfigureAwait(false) is { } raced)
            {
                return raced;
            }

            throw;
        }
    }

    /// <summary>
    /// El filtro por correo del panel es parcial: se toma solo la coincidencia exacta. Un usuario con el external_id de
    /// otra cuenta no se adopta aunque tenga el correo: la cuenta vieja lo conserva porque el módulo no cambia correos
    /// en el panel, y el server nuevo le quedaría a otro cliente con las variables secretas del pedido a la vista.
    /// </summary>
    private async Task<int?> ByEmail(Panel panel, string email, long account, CancellationToken ct)
    {
        var users = await api.List(panel, $"users?filter%5Bemail%5D={Uri.EscapeDataString(email)}", ct).ConfigureAwait(false);
        if (users.FirstOrDefault(u => string.Equals(u.Field("email").Text(), email, StringComparison.OrdinalIgnoreCase)) is not { } user)
        {
            return null;
        }

        return user.Field("external_id").Text() is not { Length: > 0 } owner || owner == account.ToString(CultureInfo.InvariantCulture)
            ? UserId(user)
            : throw Fail.Rejected(
                "email_of_other_account",
                $"El correo de la cuenta lo tiene en el panel el usuario {UserId(user)}, que es de otra cuenta. Lo tiene que revisar el staff.");
    }

    private static long AccountId(AccountContext? account) =>
        account?.Id is long id && id > 0 ? id : throw Fail.InvalidInput(["context.account.id"]);

    private static string? Email(AccountContext? account) =>
        account?.Email is { Length: > 0 } e && e.Contains('@', StringComparison.Ordinal) ? e.Trim() : null;

    private static int UserId(JsonNode? user) => user.Field("id").Int() ?? throw Fail.Malformed("usuario sin id");

    // ---- Suspensión, reactivación y baja ----

    public Task<ModuleResponse> Suspend(ModuleCall call) => Toggle(call, suspend: true);

    public Task<ModuleResponse> Unsuspend(ModuleCall call) => Toggle(call, suspend: false);

    /// <summary>PTR-08: si ya está en el estado pedido, no escribe.</summary>
    private async Task<ModuleResponse> Toggle(ModuleCall call, bool suspend)
    {
        var panel = Panel.From(call);
        var server = await Existing(panel, call).ConfigureAwait(false);
        if (server.IsSuspended != suspend)
        {
            var (status, _) = await api.Send(panel, HttpMethod.Post, $"servers/{server.Id}/{(suspend ? "suspend" : "unsuspend")}", null, CancellationToken.None).ConfigureAwait(false);
            if (status == 404)
            {
                throw Fail.NotFound("El server del servicio no existe en el panel.");
            }
        }

        return ModuleResponse.Completed(new JsonObject
        {
            ["observed_state"] = suspend ? "suspended" : "active",
            ["external_ids"] = server.Ids(),
        });
    }

    /// <summary>PTR-09: sin server ya convergió. No usa el borrado forzado: con el nodo caído el 5xx se reintenta.</summary>
    public async Task<ModuleResponse> Terminate(ModuleCall call)
    {
        var panel = Panel.From(call);
        if (await Find(panel, CallContext.Read(call), call.Aborted).ConfigureAwait(false) is { } server)
        {
            // 204 y 404 son los dos el server borrado.
            await api.Send(panel, HttpMethod.Delete, $"servers/{server.Id}", null, CancellationToken.None).ConfigureAwait(false);
        }

        return ModuleResponse.Completed(new JsonObject { ["observed_state"] = "terminated" });
    }

    // ---- Cambio de plan y estado ----

    /// <summary>
    /// PTR-10: si el paquete ya coincide no escribe. No reinstala, no reinicia y no toca los puertos adicionales. La
    /// location no se cambia por la API: lo que se pudo aplicar queda aplicado y el traslado lo hace el staff.
    /// </summary>
    public async Task<ModuleResponse> ChangePlan(ModuleCall call)
    {
        var panel = Panel.From(call);
        var context = CallContext.Read(call);
        var plan = Plan.From(context.Service?.Params);
        var (stored, bad) = plan.StoredEgg(context.Service?.Order);
        if (bad)
        {
            throw Fail.InvalidInput(["order.variables.egg_id"]);
        }

        var server = await Existing(panel, call).ConfigureAwait(false);
        var egg = await Egg(panel, plan, stored, call.Aborted).ConfigureAwait(false);
        var desired = Package.Desired(plan, egg, server);

        if (desired.Differences(server).Count > 0)
        {
            // Primero startup, que es lo que el panel valida contra las reglas del egg: si lo rechaza, los límites quedan
            // como estaban y el server no queda a medias.
            try
            {
                server = await Startup(panel, server, desired.Startup, desired.Egg, desired.Image, desired.Environment).ConfigureAwait(false);
            }
            catch (ModuleException ex) when (ex.Code == ErrorCodes.OutcomeUnknown)
            {
                // Si el PATCH de startup no confirma, decide la relectura.
                server = await Find(panel, context, CancellationToken.None).ConfigureAwait(false)
                    ?? throw Fail.NotFound("El server del servicio no existe en el panel.");
            }

            server = await Build(panel, server, desired, []).ConfigureAwait(false);
            var left = desired.Differences(server);
            if (left.Count > 0)
            {
                throw new ModuleException(
                    ErrorCodes.Rejected,
                    $"El panel no aplicó todo el cambio de plan: {string.Join(", ", left)}.",
                    details: new JsonObject { ["upstream"] = new JsonObject { ["reason"] = "not_applied" }, ["differences"] = Fail.Strings(left) });
            }
        }

        if (await Location(panel, server, call.Aborted).ConfigureAwait(false) != plan.LocationId)
        {
            throw new ModuleException(
                ErrorCodes.Rejected,
                $"El server está en otra location que la del plan ({plan.LocationId}) y la API del panel no traslada servers. Lo traslada el staff desde el panel.",
                details: new JsonObject { ["upstream"] = new JsonObject { ["reason"] = "requires_transfer" }, ["differences"] = Fail.Strings(["location_id"]) });
        }

        return ModuleResponse.Completed(new JsonObject { ["observed_state"] = server.Observed, ["external_ids"] = server.Ids() });
    }

    /// <summary>
    /// PTR-11: estado observado, IDs externos (así se adoptan los servicios importados) y si el paquete coincide con
    /// los parámetros. Un error nunca se toma como ausencia.
    /// </summary>
    public async Task<ModuleResponse> GetStatus(ModuleCall call)
    {
        var panel = Panel.From(call);
        var context = CallContext.Read(call);
        var server = await Find(panel, context, call.Aborted).ConfigureAwait(false);
        if (server is null)
        {
            return ModuleResponse.Completed(new JsonObject { ["observed_state"] = "not_found" });
        }

        var result = new JsonObject { ["observed_state"] = server.Observed, ["external_ids"] = server.Ids() };

        // Sin parámetros completos, como un servicio recién importado, igual se devuelven el estado y los IDs.
        Plan plan;
        try
        {
            plan = Plan.From(context.Service?.Params);
        }
        catch (ModuleException ex) when (ex.Code == ErrorCodes.InvalidInput)
        {
            return ModuleResponse.Completed(result);
        }

        // Un egg del pedido que no sirve es una diferencia, no un motivo para callar la comparación.
        var (stored, bad) = plan.StoredEgg(context.Service?.Order);
        var json = bad ? null : await api.Get(panel, EggPath(plan, stored), call.Aborted).ConfigureAwait(false);
        var differences = json is null ? ["egg_id"] : Package.Desired(plan, PanelEgg.Parse(json), server).Differences(server);
        if (await Location(panel, server, call.Aborted).ConfigureAwait(false) != plan.LocationId)
        {
            differences.Add("location_id");
        }

        result["package_matches"] = differences.Count == 0;
        result["differences"] = Fail.Strings(differences);
        return ModuleResponse.Completed(result);
    }

    // ---- Vista y acciones ----

    /// <summary>
    /// PTR-12: dirección, estado y enlace al panel, en texto plano. Con el panel caído la vista no falla: muestra un
    /// aviso y la dirección guardada.
    /// </summary>
    public async Task<ModuleResponse> View(ModuleCall call)
    {
        var panel = Panel.From(call);
        var context = CallContext.Read(call);
        var en = context.Account?.English == true;
        var saved = context.Service?.ExternalIds;
        PanelServer? server = null;
        var down = false;
        try
        {
            server = await Find(panel, context, call.Aborted).ConfigureAwait(false);
        }
        catch (ModuleException ex) when (ErrorCodes.IsRetryable(ex.Code))
        {
            down = true;
        }

        var blocks = new JsonArray();
        if (down)
        {
            blocks.Add(Notice("warning", en ? "The game panel is not available right now. Try again in a few minutes." : "El panel de juegos no está disponible en este momento. Probá de nuevo en unos minutos."));
        }
        else if (server is null)
        {
            blocks.Add(Notice("error", en ? "The server does not exist in the game panel." : "El servidor no existe en el panel de juegos."));
        }
        else
        {
            var (level, es, english) = server switch
            {
                { IsSuspended: true } => ("warning", "Suspendido", "Suspended"),
                { Installing: true } => ("warning", "Instalando", "Installing"),
                { InstallFailed: true } => ("error", "La instalación falló", "Installation failed"),
                _ => ("ok", "Activo", "Active"),
            };
            blocks.Add(new JsonObject { ["type"] = "status", ["level"] = level, ["text"] = en ? english : es });
        }

        // Lo guardado se muestra solo con el panel caído: sin server, la dirección vieja confundiría al cliente.
        if ((down ? saved.Field("address").Text() : server?.Primary?.Address) is { Length: > 0 } address)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "fields",
                ["items"] = new JsonArray(new JsonObject { ["label"] = en ? "Address" : "Dirección", ["value"] = address }),
            });
        }

        var identifier = down ? saved.Field("identifier").Text() : server?.Identifier;
        var url = identifier is { Length: > 0 } && identifier.All(char.IsAsciiLetterOrDigit)
            ? new Uri(panel.Web, $"server/{identifier}")
            : panel.Web;
        blocks.Add(new JsonObject { ["type"] = "link", ["label"] = en ? "Go to panel" : "Ir al panel", ["url"] = url.ToString() });
        return ModuleResponse.Completed(new JsonObject { ["blocks"] = blocks });
    }

    /// <summary>
    /// PTR-13: cambia la contraseña del usuario del panel con la que genera el núcleo. El usuario es uno por cliente,
    /// así que vale para todos sus servers.
    /// </summary>
    public async Task<ModuleResponse> RunAction(ModuleCall call)
    {
        var input = Inputs.Read<ActionInput>(call);
        if (input.Action != SetPanelPassword)
        {
            throw new ModuleException(ErrorCodes.NotSupported, $"Acción no soportada: {input.Action}.");
        }

        var password = input.Credentials?.GetValueOrDefault("panel_password") is { Length: >= 8 and <= 191 } p
            ? p
            : throw Fail.InvalidInput(["credentials.panel_password"]);
        var panel = Panel.From(call);
        var account = AccountId(CallContext.Read(call).Account);
        var server = await Existing(panel, call).ConfigureAwait(false);
        var user = (await api.Get(panel, $"users/{server.User}", call.Aborted).ConfigureAwait(false))?.Field("attributes")
            ?? throw Fail.NotFound("El usuario del panel del server no existe.");

        // El dueño del server puede ser un administrador (el staff se lo asignó para revisarlo) o un usuario de otra
        // cuenta. La contraseña nueva la ve el cliente: nunca se le cambia a ellos.
        if (user.Field("root_admin").Bool() == true
            || (user.Field("external_id").Text() is { Length: > 0 } owner && owner != account.ToString(CultureInfo.InvariantCulture)))
        {
            throw Fail.Rejected(
                "foreign_user",
                $"El dueño del server en el panel (usuario {server.User}) es un administrador o es de otra cuenta: no se le cambia la contraseña. Lo tiene que revisar el staff.");
        }

        // El panel pide los datos del usuario completos en cada cambio: se mandan los que tiene, más la contraseña.
        var body = new JsonObject
        {
            ["email"] = user.Field("email").Text(),
            ["username"] = user.Field("username").Text(),
            ["first_name"] = user.Field("first_name").Text(),
            ["last_name"] = user.Field("last_name").Text(),
            ["password"] = password,
        };
        var (status, _) = await api.Send(panel, HttpMethod.Patch, $"users/{server.User}", body, CancellationToken.None).ConfigureAwait(false);
        return status == 404
            ? throw Fail.NotFound("El usuario del panel del server no existe.")
            : ModuleResponse.Completed(new JsonObject { ["user_id"] = server.User.ToString(CultureInfo.InvariantCulture) });
    }

    // ---- Lecturas y escrituras comunes ----

    /// <summary>
    /// El server del servicio por su marca externa, el external_id: primero el uuid del servicio y después el ID, que es
    /// la marca de los importados y de los creados antes del uuid. Null solo si el panel contesta 404 a todas.
    /// </summary>
    private async Task<PanelServer?> Find(Panel panel, CallContext context, CancellationToken ct)
    {
        foreach (var mark in context.Marks)
        {
            if (await api.Get(panel, $"servers/external/{mark}?include=allocations", ct).ConfigureAwait(false) is not { } json)
            {
                continue;
            }

            var server = PanelServer.Parse(json);
            return server.ExternalId == mark ? server : throw Fail.Malformed("el server encontrado tiene otro external_id");
        }

        return null;
    }

    private async Task<PanelServer> Existing(Panel panel, ModuleCall call) =>
        await Find(panel, CallContext.Read(call), call.Aborted).ConfigureAwait(false)
        ?? throw Fail.NotFound("El server del servicio no existe en el panel.");

    private static string EggPath(Plan plan, int? egg) =>
        string.Create(CultureInfo.InvariantCulture, $"nests/{plan.NestId}/eggs/{egg ?? plan.EggId}?include=variables");

    /// <summary>El egg del servicio: el que eligió el cliente en el pedido, si el producto lo ofrece, o el del producto.</summary>
    private async Task<PanelEgg> Egg(Panel panel, Plan plan, int? egg, CancellationToken ct) =>
        PanelEgg.Parse(await api.Get(panel, EggPath(plan, egg), ct).ConfigureAwait(false)
            ?? throw Fail.InvalidInput(["params.egg_id"], $"El panel no tiene el egg {egg ?? plan.EggId} en el nest {plan.NestId}."));

    /// <summary>La location del server: no está en el server sino en su nodo.</summary>
    private async Task<int> Location(Panel panel, PanelServer server, CancellationToken ct) =>
        (await api.Get(panel, $"nodes/{server.Node.ToString(CultureInfo.InvariantCulture)}", ct).ConfigureAwait(false)).Field("attributes").Field("location_id").Int()
        ?? throw Fail.Malformed("nodo del server sin location_id");

    private static bool Exists(PanelEgg egg, string key) => egg.Variables.Any(v => v.Env == key || v.Name == key);

    private static string? First(JsonNode? list) =>
        (list.Field("data") as JsonArray)?.FirstOrDefault().Field("attributes").Field("id").Int()?.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Una lectura de la prueba de la instancia. Un 403 es un permiso que falta y se sigue con las demás. Un 401 es
    /// la clave rechazada y corta todo.
    /// </summary>
    private async Task<JsonNode?> Probe(Panel panel, string path, string permission, List<string> missing, CancellationToken ct)
    {
        try
        {
            return await api.Get(panel, path, ct).ConfigureAwait(false) ?? throw Fail.InvalidConfig(["panel_url"], $"El panel no tiene {PanelApi.Route(path)}.");
        }
        catch (ModuleException ex) when (ex.Code == ErrorCodes.InvalidConfig && Fail.HttpStatus(ex) == 403)
        {
            missing.Add(permission);
            return null;
        }
    }

    /// <summary>El paquete actual del server, para escribir el build sin cambiar nada más que lo que se agrega.</summary>
    private static Package Current(PanelServer s) => new(
        s.Memory, s.Swap, s.Disk, s.Io, s.Cpu, s.OomDisabled, s.Databases ?? 0, s.AllocationLimit ?? 0, s.Backups ?? 0,
        s.Nest, s.Egg, s.Startup, s.Image, new Dictionary<string, string>());

    /// <summary>PATCH build: límites y allocations. La allocation principal es siempre la actual.</summary>
    private async Task<PanelServer> Build(Panel panel, PanelServer server, Package package, IEnumerable<int> addAllocations)
    {
        var body = new JsonObject
        {
            ["allocation"] = server.Allocation,
            ["limits"] = new JsonObject
            {
                ["memory"] = package.Memory,
                ["swap"] = package.Swap,
                ["disk"] = package.Disk,
                ["io"] = package.Io,
                ["cpu"] = package.Cpu,
            },
            ["oom_disabled"] = package.OomDisabled,
            ["feature_limits"] = new JsonObject
            {
                ["databases"] = package.Databases,
                ["allocations"] = package.Allocations,
                ["backups"] = package.Backups,
            },
            ["add_allocations"] = new JsonArray(addAllocations.Select(id => (JsonNode?)id).ToArray()),
        };
        return await Write(panel, $"servers/{server.Id}/build?include=allocations", body).ConfigureAwait(false);
    }

    /// <summary>PATCH startup: variables, startup, egg e imagen. Nunca reinstala: skip_scripts va en false pero no hay reinstalación.</summary>
    private Task<PanelServer> Startup(Panel panel, PanelServer server, string startup, int egg, string image, IReadOnlyDictionary<string, string> environment) =>
        Write(panel, $"servers/{server.Id}/startup?include=allocations", new JsonObject
        {
            ["startup"] = startup,
            ["environment"] = new JsonObject(environment.Select(e => KeyValuePair.Create(e.Key, (JsonNode?)e.Value))),
            ["egg"] = egg,
            ["image"] = image,
            ["skip_scripts"] = false,
        });

    private async Task<PanelServer> Write(Panel panel, string path, JsonObject body)
    {
        var (status, json) = await api.Send(panel, HttpMethod.Patch, path, body, CancellationToken.None).ConfigureAwait(false);
        return status == 404 ? throw Fail.NotFound("El server del servicio no existe en el panel.") : PanelServer.Parse(json);
    }

    private static JsonObject Notice(string level, string text) => new() { ["type"] = "notice", ["level"] = level, ["text"] = text };
}
