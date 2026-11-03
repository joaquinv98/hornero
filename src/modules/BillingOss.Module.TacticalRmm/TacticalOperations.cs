using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.TacticalRmm;

/// <summary>
/// Las operaciones del módulo. No guardan nada entre pedidos: todo sale del sobre y de Tactical, así que cualquier
/// réplica atiende cualquier pedido, incluida la consulta de una baja en curso. Las que cambian algo son
/// idempotentes por sí mismas (marca del alta, modo mantenimiento, baja que tolera el 404 y link vigente
/// reutilizado), así que una repetición que cae en otra réplica, sin el registro de idempotencia de la primera,
/// tampoco duplica nada.
/// Nunca llaman a accounts/users ni a accounts/roles: el módulo no crea ni toca usuarios del panel.
/// </summary>
internal sealed partial class TacticalOperations(TacticalApi api, ILogger logger, TimeSpan stepBudget)
{
    /// <summary>
    /// Tiempo de un paso de la baja forzada antes de dejar la operación en curso. Con un pedido de 15 segundos en
    /// vuelo y el borrado del cliente, el paso entra en los 90 segundos que declara el manifiesto.
    /// </summary>
    public static readonly TimeSpan DefaultStepBudget = TimeSpan.FromSeconds(30);

    /// <summary>Un link de instalación se reutiliza si le queda al menos este tiempo de vigencia.</summary>
    private static readonly TimeSpan MinimumLinkLife = TimeSpan.FromHours(1);

    /// <summary>
    /// Vida de una baja forzada, el plazo por defecto de una operación larga (sección 7 del protocolo). Pasado ese
    /// plazo la consulta da not_found y ya no desinstala nada, aunque llegue días después con otra operación.
    /// </summary>
    private static readonly TimeSpan OperationLife = TimeSpan.FromHours(1);

    public async Task<ModuleResponse> Test(ModuleCall call)
    {
        var settings = TacticalSettings.From(call);
        var clients = List(await api.Get(settings, "clients/", call.Aborted).ConfigureAwait(false), "clients/");

        // Las bajas y la vista cuentan agentes: sin ese permiso fallarían recién al usarse.
        _ = List(await api.Get(settings, "agents/?client=0", call.Aborted).ConfigureAwait(false), "agents/");
        return ModuleResponse.Completed(new JsonObject { ["clients"] = clients.Count });
    }

    /// <summary>
    /// Sin efectos. Controla el límite de agentes y que el nombre que usaría el alta no lo tenga otro cliente de
    /// Tactical, porque el alta reutiliza el cliente que ya tiene ese nombre.
    /// </summary>
    public async Task<ModuleResponse> Validate(ModuleCall call)
    {
        var settings = TacticalSettings.From(call);
        var service = ServiceRef.From(call);
        _ = ServiceRef.RequiredLimit(call, service);
        if (service.Id is > 0)
        {
            var found = await FindClient(settings, settings.ClientName(service.Id.Value), call.Aborted).ConfigureAwait(false);
            if (found is not null && ServiceRef.Positive(found.Field("id")) is { } foundId && foundId != ServiceRef.Positive(service.ExternalIds.Field("client_id")))
            {
                // Suele ser de un alta anterior de este servicio que terminó en error. Cambiar el prefijo lo dejaría
                // huérfano: hay que revisarlo y, si es del servicio, vincularlo o reintentar el alta, que lo reutiliza.
                throw Fail.Config(
                    ["client_name_prefix"],
                    "Ya hay un cliente en Tactical RMM con el nombre que usaría este servicio. Si es de este servicio, cargar su ID en external_ids.client_id o reintentar el alta, que lo reutiliza.",
                    "name_taken",
                    new JsonObject { ["client_id"] = foundId.ToString(CultureInfo.InvariantCulture) });
            }
        }

        return ModuleResponse.Completed(new JsonObject { ["valid"] = true });
    }

    /// <summary>
    /// Crea el cliente de Tactical con su sitio. El nombre (prefijo más ID del servicio) es la marca del alta: si ya
    /// existe, es de un intento anterior y se reutiliza. Tactical no repite nombres de cliente, así que dos intentos
    /// simultáneos en dos réplicas dejan un solo cliente. El éxito se confirma buscando el cliente, no por el texto
    /// de la respuesta.
    /// </summary>
    public async Task<ModuleResponse> Create(ModuleCall call)
    {
        var settings = TacticalSettings.From(call);
        var service = ServiceRef.From(call);
        var name = settings.ClientName(service.ServiceId());
        _ = ServiceRef.RequiredLimit(call, service);

        // No se corta cuando el núcleo se desconecta: con el pedido ya enviado, abandonarlo dejaría el resultado
        // sin guardar. Si se termina, la SDK lo registra por la clave de idempotencia y el reintento lo recupera.
        var ct = CancellationToken.None;
        var client = await FindClient(settings, name, ct).ConfigureAwait(false);
        if (client is null)
        {
            var body = new JsonObject
            {
                ["client"] = new JsonObject { ["name"] = name },
                ["site"] = new JsonObject { ["name"] = settings.SiteName },
                ["custom_fields"] = new JsonArray(),
            };
            try
            {
                await api.Post(settings, "clients/", body, ct).ConfigureAwait(false);
            }
            catch (ModuleException ex) when (ex.Code == ErrorCodes.Rejected)
            {
                // Otro intento lo creó entre la búsqueda y el alta. Si no aparece, el rechazo es otro y se informa.
                client = await FindClient(settings, name, ct).ConfigureAwait(false);
                if (client is null)
                {
                    throw;
                }
            }

            client ??= await FindClient(settings, name, ct).ConfigureAwait(false)
                ?? throw Fail.Config(["api_key"], "Tactical RMM aceptó el alta pero la API key no ve el cliente.", "api_key_o_permisos");
        }

        var site = Site(client, settings.SiteName)
            ?? throw Fail.Unavailable("Tactical RMM devolvió el cliente sin sitios.");
        return ModuleResponse.Completed(new JsonObject
        {
            ["observed_state"] = "active",
            ["external_ids"] = new JsonObject
            {
                ["client_id"] = Id(client),
                ["site_id"] = Id(site),

                // Las operaciones siguientes controlan que el client_id siga siendo de este cliente.
                ["client_name"] = client.Field("name").Text(),
            },
        });
    }

    /// <summary>
    /// Suspende o reactiva con el modo mantenimiento de todos los agentes del cliente. Repetirlo no cambia nada.
    /// Primero lee el cliente, porque Tactical contesta bien aunque el cliente no exista, y controla que sea el del
    /// servicio. Al suspender borra además los links de instalación del cliente: un agente que se instala con uno
    /// entraría sin mantenimiento.
    /// </summary>
    public async Task<ModuleResponse> Maintenance(ModuleCall call, bool on)
    {
        var settings = TacticalSettings.From(call);
        var service = ServiceRef.From(call);
        var ct = CancellationToken.None;
        var client = await ServiceClient(settings, service, ct).ConfigureAwait(false);
        await api.Post(settings, "agents/maintenance/bulk/", new JsonObject { ["type"] = "Client", ["id"] = service.ClientId(), ["action"] = on }, ct)
            .ConfigureAwait(false);
        if (on)
        {
            await RemoveDeployments(settings, client, ct).ConfigureAwait(false);
        }

        return ModuleResponse.Completed(new JsonObject { ["observed_state"] = on ? "suspended" : "active" });
    }

    /// <summary>
    /// Borra el cliente de Tactical, nada más: ni usuarios ni roles. Un cliente que ya no existe es una baja hecha.
    /// Con agentes instalados no borra, salvo que el staff pida input.force: los equipos del cliente seguirían con
    /// el agente instalado y sin gestión. Con force desinstala los agentes de a tandas y la baja queda en curso.
    /// </summary>
    public async Task<ModuleResponse> Terminate(ModuleCall call)
    {
        var settings = TacticalSettings.From(call);
        var service = ServiceRef.From(call);
        var operationId = string.Create(
            CultureInfo.InvariantCulture, $"terminate-{service.ClientId()}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        return await TerminateStep(settings, service, call.Request.Input.Field("force").IsTrue(), operationId).ConfigureAwait(false);
    }

    /// <summary>
    /// Consulta de una operación larga. La única es la baja forzada, y su ID lleva el cliente y el inicio: la
    /// consulta sigue desinstalando con lo que manda el núcleo en el sobre, así que cualquier réplica la atiende sin
    /// haberla empezado. Un ID que no coincide con el servicio, o con más de una hora, da not_found y el núcleo
    /// repite la llamada original con la misma clave.
    /// </summary>
    public async Task<ModuleResponse> Poll(ModuleCall call, string operationId)
    {
        var settings = TacticalSettings.From(call);
        var service = ServiceRef.From(call);
        var match = TerminateIdPattern().Match(operationId);
        if (!match.Success
            || ServiceRef.Positive(service.ExternalIds.Field("client_id")) is not { } clientId
            || match.Groups[1].Value != clientId.ToString(CultureInfo.InvariantCulture)
            || (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))).Duration() > OperationLife)
        {
            throw new ModuleException(ErrorCodes.NotFound, "La operación no existe para este servicio.");
        }

        return await TerminateStep(settings, service, force: true, operationId).ConfigureAwait(false);
    }

    /// <summary>
    /// Un paso de la baja. El avance queda en Tactical (los agentes que ya no están), no en el módulo: repetir un
    /// paso, en la misma réplica o en otra, sigue desde donde quedó. Tactical saca el agente de su base en el momento
    /// y le manda la orden de desinstalarse.
    /// </summary>
    private async Task<ModuleResponse> TerminateStep(TacticalSettings settings, ServiceRef service, bool force, string operationId)
    {
        // No se corta cuando el núcleo se desconecta, por lo mismo que el alta.
        var ct = CancellationToken.None;
        var started = Stopwatch.GetTimestamp();
        var terminated = ModuleResponse.Completed(new JsonObject { ["observed_state"] = "terminated" });
        var clientId = service.ClientId();

        List<JsonObject> agents;
        try
        {
            (_, agents) = await ClientAndAgents(settings, service, ct).ConfigureAwait(false);
        }
        catch (ModuleException ex) when (ex.Code == ErrorCodes.NotFound)
        {
            return terminated;
        }

        if (agents.Count > 0 && !force)
        {
            // Es una regla del módulo, no de Tactical: por eso invalid_input sobre force y no rejected.
            throw Fail.Input(
                ["force"],
                $"El cliente todavía tiene {agents.Count} agentes instalados. Hay que desinstalarlos antes de la baja, o pedirla con force.",
                new JsonObject { ["reason"] = "agents_installed", ["agents"] = agents.Count });
        }

        for (var i = 0; i < agents.Count; i++)
        {
            // Cada paso desinstala al menos uno, así la baja avanza aunque Tactical conteste lento.
            if (i > 0 && Stopwatch.GetElapsedTime(started) > stepBudget)
            {
                return ModuleResponse.Pending(operationId, retryAfter: 15);
            }

            // El agent_id entra en la ruta: solo letras, dígitos, guion y guion bajo.
            if (agents[i].Field("agent_id").Text() is not { } agentId || !AgentIdPattern().IsMatch(agentId))
            {
                throw Fail.Unavailable("Tactical RMM devolvió un agente con un agent_id inválido.");
            }

            try
            {
                await api.Delete(settings, $"agents/{agentId}/", ct).ConfigureAwait(false);
            }
            catch (ModuleException ex) when (ex.Code == ErrorCodes.NotFound)
            {
                // Lo desinstaló otro paso, o el staff a mano.
            }
        }

        try
        {
            await api.Delete(settings, $"clients/{clientId}/", ct).ConfigureAwait(false);
        }
        catch (ModuleException ex) when (ex.Code == ErrorCodes.NotFound)
        {
            // Lo borró otro intento entre la lectura y el borrado.
        }
        catch (ModuleException ex) when (force && ex.Code == ErrorCodes.Rejected)
        {
            // Tactical no borra un cliente con agentes: se instaló uno durante la baja. El paso siguiente lo desinstala.
            return ModuleResponse.Pending(operationId, retryAfter: 15);
        }

        return terminated;
    }

    /// <summary>
    /// Tactical no tiene cupo de agentes por cliente, así que el cambio de plan no lo toca. Solo controla que el
    /// límite nuevo no quede por debajo de los agentes instalados.
    /// </summary>
    public async Task<ModuleResponse> ChangePlan(ModuleCall call)
    {
        var settings = TacticalSettings.From(call);
        var service = ServiceRef.From(call);
        var limit = ServiceRef.RequiredLimit(call, service);
        var (_, agents) = await ClientAndAgents(settings, service, CancellationToken.None).ConfigureAwait(false);
        if (agents.Count > limit)
        {
            throw Fail.Input(
                ["params.agent_limit"],
                $"El cliente tiene {agents.Count} agentes instalados y el plan nuevo permite {limit}.",
                new JsonObject { ["reason"] = "below_installed_agents", ["agents"] = agents.Count });
        }

        return ModuleResponse.Completed(new JsonObject { ["agents"] = agents.Count, ["agent_limit"] = limit });
    }

    /// <summary>
    /// Estado observado: suspended si hay agentes y todos están en mantenimiento, active en cualquier otro caso.
    /// Con 0 agentes la suspensión no se puede observar y da active con agents en 0.
    /// </summary>
    public async Task<ModuleResponse> GetStatus(ModuleCall call)
    {
        var settings = TacticalSettings.From(call);
        var service = ServiceRef.From(call);
        List<JsonObject> agents;
        try
        {
            (_, agents) = await ClientAndAgents(settings, service, call.Aborted).ConfigureAwait(false);
        }
        catch (ModuleException ex) when (ex.Code == ErrorCodes.NotFound)
        {
            return ModuleResponse.Completed(new JsonObject { ["observed_state"] = "not_found" });
        }

        var maintenance = agents.Count(a => a.Field("maintenance_mode").IsTrue());
        var limit = service.AgentLimit;
        return ModuleResponse.Completed(new JsonObject
        {
            ["observed_state"] = agents.Count > 0 && maintenance == agents.Count ? "suspended" : "active",
            ["details"] = new JsonObject
            {
                ["agents"] = agents.Count,
                ["agents_in_maintenance"] = maintenance,
                ["agent_limit"] = limit,
                ["agent_limit_exceeded"] = limit is { } max && agents.Count > max,
            },
        });
    }

    /// <summary>
    /// Bloques de texto plano para el portal: agentes contra el límite, sitio, aviso si se pasa y el link de
    /// instalación vigente. No crea nada: si no hay link vigente, ofrece la acción.
    /// </summary>
    public async Task<ModuleResponse> View(ModuleCall call)
    {
        var settings = TacticalSettings.From(call);
        var service = ServiceRef.From(call);
        var en = service.English;
        var (client, agents) = await ClientAndAgents(settings, service, call.Aborted).ConfigureAwait(false);
        var siteId = ServiceRef.Positive(service.ExternalIds.Field("site_id"));
        var siteName = siteId is null ? null : ClientSite(client, service).Field("name").Text();
        var limit = service.AgentLimit;
        var exceeded = limit is { } max && agents.Count > max;
        var suspended = service.Status == "suspended";

        var fields = new JsonArray(Field(en ? "Installed agents" : "Agentes instalados", limit is null
            ? agents.Count.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{agents.Count} {(en ? "of" : "de")} {limit}")));
        if (siteName is not null)
        {
            fields.Add(Field(en ? "Site" : "Sitio", siteName));
        }

        var blocks = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "status",
                ["level"] = exceeded || suspended ? "warning" : "ok",
                ["text"] = suspended ? (en ? "Suspended" : "Suspendido") : exceeded ? (en ? "Over the limit" : "Excede el límite") : (en ? "Active" : "Activo"),
            },
            new JsonObject { ["type"] = "fields", ["items"] = fields },
        };
        if (exceeded)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "notice",
                ["level"] = "warning",
                ["text"] = en
                    ? $"There are {agents.Count} agents installed and the plan includes {limit}. Change the plan or remove agents."
                    : $"Hay {agents.Count} agentes instalados y el plan incluye {limit}. Cambiá de plan o desinstalá agentes.",
            });
        }

        if (!suspended && siteId is { } site)
        {
            try
            {
                var link = await CurrentLink(settings, site, call.Aborted).ConfigureAwait(false);
                blocks.Add(link is null
                    ? new JsonObject { ["type"] = "actions", ["items"] = new JsonArray(TacticalRmmModule.InstallerLink) }
                    : new JsonObject
                    {
                        ["type"] = "link",
                        ["label"] = en ? "Agent installer" : "Instalador del agente",
                        ["url"] = link.Value.Url,
                        ["expires_at"] = Instant(link.Value.Expires),
                    });
            }
            catch (ModuleException ex) when (ex.Code == ErrorCodes.InvalidConfig)
            {
                // Sin permiso de deployments la acción también fallaría: ni link ni botón.
                logger.LogWarning("Tactical RMM: la vista sale sin link de instalación ni acción ({Cause})", ex.Details?["cause"]?.ToString());
            }
        }

        return ModuleResponse.Completed(new JsonObject { ["blocks"] = blocks });
    }

    /// <summary>
    /// Acción installer_link: devuelve un link de instalación del sitio del servicio. Reutiliza uno vigente del
    /// mismo tipo y si no hay, crea un deployment en Tactical con el vencimiento de la instancia. Repetirla después,
    /// en cualquier réplica, devuelve el mismo link mientras siga vigente. Dos pedidos simultáneos pueden dejar dos
    /// deployments del sitio, los dos válidos.
    /// </summary>
    public async Task<ModuleResponse> RunAction(ModuleCall call)
    {
        var settings = TacticalSettings.From(call);
        var service = ServiceRef.From(call);
        var input = call.Request.Input;
        if (input.Field("action").Text() != TacticalRmmModule.InstallerLink)
        {
            throw Fail.Input(["action"]);
        }

        var type = input.Field("params").Field("agent_type").Text() ?? "workstation";
        if (type is not ("workstation" or "server"))
        {
            throw Fail.Input(["params.agent_type"]);
        }

        // Un agente instalado durante la suspensión entra sin mantenimiento: no se dan links mientras dure.
        if (service.Status == "suspended")
        {
            throw Fail.Input(["service.status"], "El servicio está suspendido: no se generan links de instalación.");
        }

        var siteId = service.SiteId();
        var ct = CancellationToken.None;
        _ = ClientSite(await ServiceClient(settings, service, ct).ConfigureAwait(false), service);
        var link = await CurrentLink(settings, siteId, ct, type).ConfigureAwait(false);
        if (link is null)
        {
            var expires = DateTimeOffset.UtcNow.AddDays(settings.DeployExpiryDays);
            await api.Post(settings, "clients/deployments/", new JsonObject
            {
                ["site"] = siteId,
                ["expires"] = expires.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'+0000'", CultureInfo.InvariantCulture),
                ["agenttype"] = type,
                ["goarch"] = "amd64",
                ["power"] = 0,
                ["ping"] = 0,
                ["rdp"] = 0,
            }, ct).ConfigureAwait(false);
            link = await CurrentLink(settings, siteId, ct, type).ConfigureAwait(false)
                ?? throw Fail.Config(["api_key"], "Tactical RMM aceptó el deployment pero la API key no lo ve.", "api_key_o_permisos");
        }

        return ModuleResponse.Completed(new JsonObject { ["url"] = link.Value.Url, ["expires_at"] = Instant(link.Value.Expires) });
    }

    // ---- Lecturas de Tactical ----

    private async Task<JsonObject?> FindClient(TacticalSettings settings, string name, CancellationToken ct)
    {
        var clients = List(await api.Get(settings, "clients/", ct).ConfigureAwait(false), "clients/");
        return clients.OfType<JsonObject>()
            .Where(c => c.Field("name").Text() == name && ServiceRef.Positive(c.Field("id")) is not null)
            .MinBy(c => ServiceRef.Positive(c.Field("id")));
    }

    /// <summary>
    /// El cliente de Tactical del servicio. Un cliente que no existe sale como not_found. Un client_id viejo o mal
    /// cargado puede apuntar al cliente de otra empresa (los IDs de Tactical se reiniciaron con su reinstalación):
    /// si el nombre no es el del servicio, invalid_input antes de tocar nada.
    /// </summary>
    private async Task<JsonObject> ServiceClient(TacticalSettings settings, ServiceRef service, CancellationToken ct)
    {
        var client = await api.Get(settings, $"clients/{service.ClientId()}/", ct).ConfigureAwait(false) as JsonObject
            ?? throw Fail.Unavailable("Tactical RMM devolvió un cliente que no es un objeto.");
        if (client.Field("name").Text() != service.ClientName(settings))
        {
            throw Fail.Input(
                ["external_ids.client_id"],
                "El cliente de Tactical RMM con ese client_id no es el de este servicio: tiene otro nombre. Revisar external_ids.client_id y external_ids.client_name.",
                new JsonObject { ["reason"] = "client_mismatch" });
        }

        return client;
    }

    /// <summary>
    /// El cliente del servicio y sus agentes. Los agentes se filtran también por el nombre del cliente, por si el
    /// filtro del servidor no se aplicara: con agentes de otro cliente, una baja forzada los desinstalaría y un cambio
    /// de plan se frenaría sin motivo.
    /// </summary>
    private async Task<(JsonObject Client, List<JsonObject> Agents)> ClientAndAgents(TacticalSettings settings, ServiceRef service, CancellationToken ct)
    {
        var client = await ServiceClient(settings, service, ct).ConfigureAwait(false);
        var name = client.Field("name").Text();
        var agents = List(await api.Get(settings, $"agents/?client={service.ClientId()}", ct).ConfigureAwait(false), "agents/")
            .OfType<JsonObject>()
            .Where(a => name is not null && a.Field("client_name").Text() == name)
            .ToList();
        return (client, agents);
    }

    /// <summary>
    /// Borra los links de instalación de los sitios del cliente. Repetirlo no cambia nada. Sin permiso de
    /// deployments no los borra y queda en el log, igual que la vista sale sin link.
    /// </summary>
    private async Task RemoveDeployments(TacticalSettings settings, JsonObject client, CancellationToken ct)
    {
        var sites = (client.Field("sites") as JsonArray)?.Select(s => ServiceRef.Positive(s.Field("id"))).OfType<long>().ToHashSet() ?? [];
        try
        {
            foreach (var d in List(await api.Get(settings, "clients/deployments/", ct).ConfigureAwait(false), "clients/deployments/").OfType<JsonObject>())
            {
                if (ServiceRef.Positive(d.Field("site_id") ?? d.Field("site")) is not { } site || !sites.Contains(site)
                    || ServiceRef.Positive(d.Field("id")) is not { } id)
                {
                    continue;
                }

                try
                {
                    await api.Delete(settings, $"clients/deployments/{id}/", ct).ConfigureAwait(false);
                }
                catch (ModuleException ex) when (ex.Code == ErrorCodes.NotFound)
                {
                    // Lo borró otro intento.
                }
            }
        }
        catch (ModuleException ex) when (ex.Code == ErrorCodes.InvalidConfig)
        {
            logger.LogWarning("Tactical RMM: la suspensión no borró los links de instalación ({Cause})", ex.Details?["cause"]?.ToString());
        }
    }

    /// <summary>
    /// El sitio del servicio dentro de su cliente. Con un site_id mal importado el link de instalación sería de un
    /// sitio de otra empresa: invalid_input antes de mostrar o crear nada.
    /// </summary>
    private static JsonObject ClientSite(JsonObject client, ServiceRef service)
    {
        var siteId = service.SiteId();
        return (client.Field("sites") as JsonArray)?.OfType<JsonObject>().FirstOrDefault(s => ServiceRef.Positive(s.Field("id")) == siteId)
            ?? throw Fail.Input(
                ["external_ids.site_id"],
                "El sitio de external_ids.site_id no es del cliente de Tactical RMM de este servicio.",
                new JsonObject { ["reason"] = "site_mismatch" });
    }

    /// <summary>
    /// El link vigente del sitio: el deployment con más vencimiento por delante, o null si no hay. Sin el permiso de
    /// deployments da invalid_config.
    /// </summary>
    private async Task<(string Url, DateTimeOffset Expires)?> CurrentLink(TacticalSettings settings, long siteId, CancellationToken ct, string? type = null)
    {
        var deployments = await api.Get(settings, "clients/deployments/", ct).ConfigureAwait(false);
        var limit = DateTimeOffset.UtcNow + MinimumLinkLife;
        (string Url, DateTimeOffset Expires)? best = null;
        foreach (var d in List(deployments, "clients/deployments/").OfType<JsonObject>())
        {
            if (ServiceRef.Positive(d.Field("site_id") ?? d.Field("site")) != siteId
                || (type is not null && d.Field("mon_type").Text() != type)
                || !DateTimeOffset.TryParse(d.Field("expiry").Text(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires)
                || expires <= limit
                || (best is { } current && current.Expires >= expires))
            {
                continue;
            }

            // El uid entra en la URL que va al navegador. Tactical lo genera como UUID y su ruta pública solo acepta eso.
            if (!Guid.TryParse(d.Field("uid").Text(), out var uid))
            {
                logger.LogWarning("Tactical RMM: se descartó un deployment con un uid inválido");
                continue;
            }

            best = (new Uri(settings.ApiUrl, $"clients/{uid:D}/deploy/").AbsoluteUri, expires);
        }

        return best;
    }

    // ---- Ayudas ----

    private static JsonArray List(JsonNode node, string what) =>
        node as JsonArray ?? throw Fail.Unavailable($"Tactical RMM devolvió algo que no es una lista en {what}.");

    private static JsonObject? Site(JsonObject client, string name)
    {
        var sites = (client.Field("sites") as JsonArray)?.OfType<JsonObject>().Where(s => ServiceRef.Positive(s.Field("id")) is not null).ToList() ?? [];
        return sites.FirstOrDefault(s => s.Field("name").Text() == name) ?? sites.FirstOrDefault();
    }

    /// <summary>Los IDs externos van como texto (sección 6 del protocolo).</summary>
    private static string Id(JsonObject node) => ServiceRef.Positive(node.Field("id"))!.Value.ToString(CultureInfo.InvariantCulture);

    private static JsonObject Field(string label, string value) => new() { ["label"] = label, ["value"] = value };

    private static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex AgentIdPattern();

    /// <summary>terminate-{client_id}-{inicio en segundos Unix}. Los topes de dígitos evitan desbordes al leerlo.</summary>
    [GeneratedRegex("^terminate-([0-9]{1,18})-([0-9]{1,11})$")]
    private static partial Regex TerminateIdPattern();
}
