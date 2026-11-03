using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;

namespace BillingOss.Modules.Hosting;

/// <summary>
/// Las operaciones del protocolo, iguales para todos los paneles. Ninguna guarda nada entre pedidos: cada una lee la
/// cuenta en el panel, cambia solo lo que falta y vuelve a leer. Por eso repetirla, en esta réplica o en otra, no
/// repite el efecto. La excepción es un cambio que el panel sigue ejecutando sin haber respondido: para ese caso la
/// operación queda en curso con el momento en que salió, y la consulta solo relee hasta que pasa <see cref="SentWindow"/>.
/// </summary>
internal sealed class HostingOperations(PanelFactory panels, TimeProvider time)
{
    /// <summary>Margen para contestar antes de que el núcleo deje de esperar.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(10);

    /// <summary>Las operaciones que una consulta (sección 7 del protocolo) retoma desde el estado del panel.</summary>
    private static readonly string[] Resumable = ["create", "suspend", "unsuspend", "terminate", "change_plan"];

    /// <summary>
    /// Cuánto tiempo después de un cambio sin respuesta la consulta solo relee. El panel sigue ejecutando el cambio aunque
    /// se corte la llamada, y la baja de una cuenta grande tarda.
    /// </summary>
    private static readonly TimeSpan SentWindow = TimeSpan.FromMinutes(10);

    /// <summary>Lo que se agrega al ID de la operación cuando un cambio quedó sin respuesta, antes del momento en segundos Unix.</summary>
    private const string SentMark = "-sent-";

    // ---- Prueba de la instancia y validación ----

    /// <summary>Prueba las credenciales, el certificado y la IP permitida con una lectura. Devuelve los planes.</summary>
    public async Task<ModuleResponse> Test(ModuleCall call)
    {
        var plans = await Panel(call, "test").Plans().ConfigureAwait(false);
        return ModuleResponse.Completed(new JsonObject { ["plans"] = Strings(plans) });
    }

    /// <summary>
    /// Los datos del pedido sin cambiar nada: plan existente y dominio válido y libre. El correo se controla solo si viene,
    /// porque el pedido de alguien que todavía no tiene cuenta no lo trae: create sí lo exige.
    /// </summary>
    public async Task<ModuleResponse> Validate(ModuleCall call)
    {
        var context = Context(call);
        var plan = Plan(context);
        var domain = Names.Domain(context.Service?.Order.Field("domain").Text()) ?? throw Fail.InvalidInput(["domain"]);
        if (context.Account?.Email is not null)
        {
            Email(context);
        }
        var panel = Panel(call, "validate");

        // Un plan que no existe es un error del producto, no del cliente.
        if (!(await panel.Plans().ConfigureAwait(false)).Contains(plan, StringComparer.Ordinal))
        {
            throw Fail.InvalidConfig(["plan"], $"El plan {plan} no existe en el panel.");
        }

        if (await panel.DomainOwner(domain).ConfigureAwait(false) is not null)
        {
            throw Fail.InvalidInput(["domain"], "El dominio ya está cargado en el panel.", "domain_taken");
        }

        return ModuleResponse.Completed(new JsonObject());
    }

    // ---- Ciclo de vida ----

    /// <summary>
    /// Alta idempotente por estado, con la consulta previa: el usuario sale del uuid del servicio, así que un reintento
    /// encuentra lo que dejó el intento anterior y sigue desde ahí, y un dominio que ya está en otra cuenta frena el alta
    /// antes de crear nada. Sin vuelta atrás: lo creado queda para que decida el staff.
    /// </summary>
    public Task<ModuleResponse> Create(ModuleCall call, string? held = null) => Guarded(call, "create", held, async run =>
    {
        var context = Context(call);
        var service = context.Service ?? throw Fail.InvalidInput(["service"]);
        var uuid = Uuid(context);
        var plan = Plan(context);
        var domain = Names.Domain(service.Order.Field("domain").Text() ?? service.ExternalIds.Field("domain").Text())
            ?? throw Fail.InvalidInput(["domain"]);
        var email = Email(context);

        // La consulta de una operación larga no trae la contraseña: solo sirve una vez que la cuenta está completa.
        var password = Password(call, required: call.Operation == "create");
        var panel = Panel(call, "create", password);
        var username = service.ExternalIds.Field("username").Text() ?? Usernames.For(panel.UsernamePrefix, uuid);
        if (!Names.IsUsername(username))
        {
            throw Fail.InvalidInput(["external_ids.username"]);
        }

        var account = new PanelAccount(username, domain);
        var state = await panel.Read(account).ConfigureAwait(false);
        if (!state.Exists)
        {
            // Consulta previa: un dominio que ya está en el panel es de otro, salvo que su cuenta tenga la marca de este
            // servicio (un alta anterior con otro usuario, en los paneles que guardan la marca).
            var owner = await panel.DomainOwner(domain).ConfigureAwait(false);
            if (owner is not null)
            {
                if (string.Equals(owner, username, StringComparison.OrdinalIgnoreCase))
                {
                    throw Fail.Unavailable("El panel dice que el dominio es de la cuenta pero no la encuentra.", "inconsistent");
                }

                var other = panel.KeepsMarker && Names.IsUsername(owner)
                    ? await panel.Read(new PanelAccount(owner, domain)).ConfigureAwait(false)
                    : AccountState.Absent;
                if (!other.OwnsDomain || !Marked(other, uuid))
                {
                    throw Fail.Rejected("El dominio ya está en otra cuenta del panel. No se creó nada.", "domain_taken", null);
                }

                (account, state) = (new PanelAccount(owner, domain), other);
            }
        }

        if (state.Exists && (Foreign(state, uuid) || (!state.OwnsDomain && !Marked(state, uuid))))
        {
            // Un usuario con otro dominio u otra marca es de otro servicio: un terminate de este lo borraría.
            throw Fail.Rejected($"El usuario {account.Username} ya existe en el panel y no es de este servicio.", "user_taken", null);
        }

        if (!state.Exists || !state.OwnsDomain)
        {
            // El alta, o lo que le falta a la que dejó un intento anterior con la marca de este servicio.
            if (password is null)
            {
                throw Fail.Unknown("La cuenta no está completa en el panel y la consulta no trae la contraseña. Hay que repetir el alta con la misma clave.", "account_incomplete");
            }

            var name = Names.Display(context.Account?.Name);
            var created = await run.Mutate(() => panel.Create(new NewAccount(account.Username, domain, plan, password, email, name, uuid))).ConfigureAwait(false);
            state = await panel.Read(account).ConfigureAwait(false);
            if (!state.Exists || !state.OwnsDomain)
            {
                throw NotConverged(created, "La cuenta no quedó creada en el panel con el dominio del servicio.");
            }
        }

        // Desde acá la cuenta está completa: si se acaba el tiempo, una consulta puede terminar el alta.
        run.CanPend = true;
        if (state.Suspended)
        {
            throw Fail.Review("La cuenta del servicio ya existe en el panel y está suspendida.", "suspended");
        }

        // Una cuenta que dejó un alta anterior puede tener otro plan, por ejemplo si el staff la repite con otro.
        if (!string.Equals(state.Plan, plan, StringComparison.Ordinal))
        {
            var changed = await run.Mutate(() => panel.ChangePlan(account, plan)).ConfigureAwait(false);
            state = await panel.Read(account).ConfigureAwait(false);
            if (!state.Exists || state.Plan != plan)
            {
                throw NotConverged(changed, $"La cuenta no quedó con el plan {plan}.");
            }
        }

        return Completed(account, "active");
    });

    public Task<ModuleResponse> Suspend(ModuleCall call, string? held = null) => Toggle(call, "suspend", suspended: true, held);

    public Task<ModuleResponse> Unsuspend(ModuleCall call, string? held = null) => Toggle(call, "unsuspend", suspended: false, held);

    private Task<ModuleResponse> Toggle(ModuleCall call, string operation, bool suspended, string? held) => Guarded(call, operation, held, async run =>
    {
        var (panel, account, uuid) = Open(call, operation);
        var state = await panel.Read(account).ConfigureAwait(false);
        Require(state, uuid);
        if (state.Suspended != suspended)
        {
            var ack = await run.Mutate(() => suspended ? panel.Suspend(account) : panel.Unsuspend(account)).ConfigureAwait(false);
            state = await panel.Read(account).ConfigureAwait(false);
            if (!state.Exists || state.Suspended != suspended)
            {
                throw NotConverged(ack, "La cuenta no quedó en el estado pedido.");
            }
        }

        return Completed(account, suspended ? "suspended" : "active");
    });

    /// <summary>Borra sitio, correo y bases. Por eso exige que la cuenta sea del servicio, y después la ausencia probada.</summary>
    public Task<ModuleResponse> Terminate(ModuleCall call, string? held = null) => Guarded(call, "terminate", held, async run =>
    {
        var (panel, account, uuid) = Open(call, "terminate");
        var state = await panel.Read(account).ConfigureAwait(false);
        if (state.Exists)
        {
            // Una cuenta con la marca del servicio es suya aunque no tenga el dominio: un alta de Plesk cortada a la mitad.
            Require(state, uuid, markedIsEnough: true);
            var ack = await run.Mutate(() => panel.Delete(account)).ConfigureAwait(false);
            state = await panel.Read(account).ConfigureAwait(false);
            if (state.Exists)
            {
                throw NotConverged(ack, "La cuenta sigue en el panel.");
            }
        }

        return Completed(account, "terminated");
    });

    /// <summary>Cambia el plan con el del producto y exige leerlo después.</summary>
    public Task<ModuleResponse> ChangePlan(ModuleCall call, string? held = null) => Guarded(call, "change_plan", held, async run =>
    {
        var plan = Plan(Context(call));
        var (panel, account, uuid) = Open(call, "change_plan");
        var state = await panel.Read(account).ConfigureAwait(false);
        Require(state, uuid);
        if (!string.Equals(state.Plan, plan, StringComparison.Ordinal))
        {
            var ack = await run.Mutate(() => panel.ChangePlan(account, plan)).ConfigureAwait(false);
            state = await panel.Read(account).ConfigureAwait(false);
            if (!state.Exists || state.Plan != plan)
            {
                throw NotConverged(ack, $"La cuenta no quedó con el plan {plan}.");
            }
        }

        return Completed(account, state.Suspended ? "suspended" : "active");
    });

    /// <summary>La única acción: change_password, con la contraseña que generó y guardó el núcleo.</summary>
    public Task<ModuleResponse> RunAction(ModuleCall call) => Guarded(call, "run_action", null, async run =>
    {
        var action = Input(call).Action;
        if (action != "change_password")
        {
            throw new ModuleException(ErrorCodes.NotSupported, $"Acción no soportada: {action}.");
        }

        var password = Password(call, required: true)!;
        var (panel, account, uuid) = Open(call, "run_action", password);
        Require(await panel.Read(account).ConfigureAwait(false), uuid);
        var ack = await run.Mutate(() => panel.ChangePassword(account, password)).ConfigureAwait(false);
        if (ack.Ok)
        {
            return ModuleResponse.Completed(new JsonObject { ["action"] = "change_password" });
        }

        // Una contraseña no se puede leer: sin el OK no hay cómo confirmar. Repetir con la misma es seguro.
        throw ack.Lost
            ? Fail.Unknown("El panel no respondió al cambio de contraseña. Repetir con la misma clave y la misma contraseña es seguro.", "no_reply")
            : Fail.Rejected("El panel rechazó la contraseña nueva.", "password_rejected", ack.Refusal);
    });

    // ---- Estado, vista, acceso directo y uso ----

    public async Task<ModuleResponse> GetStatus(ModuleCall call)
    {
        var (panel, account, uuid) = Open(call, "get_status");
        var state = await panel.Read(account).ConfigureAwait(false);
        var (observed, reason) = Observe(state, uuid);
        var details = new JsonObject();
        if (state.Exists)
        {
            details["plan"] = state.Plan;
            details["suspended"] = state.Suspended;
        }

        if (reason is not null)
        {
            details["reason"] = reason;
        }

        return ModuleResponse.Completed(new JsonObject { ["observed_state"] = observed, ["details"] = details });
    }

    /// <summary>Bloques de texto plano para el portal. Nunca lleva contraseñas: el enlace va al login del panel, sin credenciales.</summary>
    public async Task<ModuleResponse> View(ModuleCall call)
    {
        var english = Names.IsEnglish(Context(call).Account?.Language);
        string T(string es, string en) => english ? en : es;

        var (panel, account, uuid) = Open(call, "view");
        var state = await panel.Read(account).ConfigureAwait(false);
        var (observed, _) = Observe(state, uuid);
        var blocks = new JsonArray(observed switch
        {
            "active" => Status("ok", T("Activo", "Active")),
            "suspended" => Status("warning", T("Suspendido", "Suspended")),
            "not_found" => Status("error", T("La cuenta no está en el panel", "The account is not in the panel")),
            _ => Status("warning", T("En revisión", "Under review")),
        });

        // Con un dominio o una marca ajenos la cuenta puede ser de otro cliente: ni su plan ni el enlace.
        if (observed is "active" or "suspended")
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "fields",
                ["items"] = new JsonArray(
                    Pair(T("Usuario del panel", "Panel user"), account.Username),
                    Pair(T("Dominio", "Domain"), account.Domain),
                    Pair(T("Plan", "Plan"), state.Plan)),
            });
            blocks.Add(new JsonObject { ["type"] = "link", ["label"] = T("Entrar al panel", "Open the panel"), ["url"] = panel.LoginUrl.AbsoluteUri });
            if (observed == "active")
            {
                blocks.Add(new JsonObject { ["type"] = "actions", ["items"] = new JsonArray("change_password") });
            }
        }

        if (observed == "unknown")
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "notice",
                ["level"] = "warning",
                ["text"] = T(
                    "No pudimos confirmar el estado del hosting. El equipo de soporte lo va a revisar.",
                    "We could not confirm the state of the hosting. The support team will review it."),
            });
        }

        return ModuleResponse.Completed(new JsonObject { ["blocks"] = blocks });
    }

    /// <summary>
    /// URL de acceso directo al panel de la cuenta, de un solo uso o de vida corta. Solo para una cuenta activa del
    /// servicio, y solo con https al host del panel.
    /// </summary>
    public async Task<ModuleResponse> Sso(ModuleCall call)
    {
        var clientIp = Input(call).ClientIp;
        if (clientIp is not null && !IPAddress.TryParse(clientIp, out _))
        {
            throw Fail.InvalidInput(["client_ip"]);
        }

        var (panel, account, uuid) = Open(call, "sso");
        var state = await panel.Read(account).ConfigureAwait(false);
        Require(state, uuid);
        if (state.Suspended)
        {
            throw Fail.Conflict("La cuenta está suspendida: el panel no da acceso directo.", "suspended");
        }

        var link = await panel.Sso(account, clientIp).ConfigureAwait(false);
        if (link.Url.Scheme != Uri.UriSchemeHttps || !string.Equals(link.Url.Host, panel.LoginUrl.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail.InvalidConfig(["host"], "El panel devolvió un acceso directo a otro host o sin https. host tiene que ser el nombre con que el panel se publica.");
        }

        var result = new JsonObject { ["url"] = link.Url.AbsoluteUri };
        if (link.ExpiresAt is { } expires)
        {
            result["expires_at"] = expires.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        return ModuleResponse.Completed(result);
    }

    /// <summary>
    /// Las lecturas del uso medido que pide el núcleo (sección 9.1 del protocolo): el disco ocupado y la transferencia del
    /// mes, en GB de 1024 MiB y como texto decimal. Una cuenta suspendida también se lee, porque su disco sigue ocupado.
    /// Una medida que no se pudo leer no va, y tampoco lo que el módulo no declara. No guarda nada.
    /// </summary>
    public async Task<ModuleResponse> Usage(ModuleCall call)
    {
        var wanted = Input(call).Metrics ?? throw Fail.InvalidInput(["metrics"], "Falta la lista de métricas.");
        var (panel, account, uuid) = Open(call, "usage");
        Require(await panel.Read(account).ConfigureAwait(false), uuid);
        var usage = await panel.Usage(account).ConfigureAwait(false);
        var readings = new Dictionary<string, decimal?>(StringComparer.Ordinal)
        {
            [HostingModule.DiskMetric] = usage.DiskMib,
            [HostingModule.TransferMetric] = usage.TransferMib,
        };

        var metrics = new JsonObject();
        foreach (var id in wanted.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            if (readings.GetValueOrDefault(id) is { } mib)
            {
                metrics[id] = Math.Round(mib / 1024, 6, MidpointRounding.AwayFromZero).ToString("0.######", CultureInfo.InvariantCulture);
            }
        }

        return ModuleResponse.Completed(new JsonObject { ["metrics"] = metrics });
    }

    // ---- Operaciones largas ----

    /// <summary>
    /// Retoma la operación desde el estado del panel con el sobre que manda el núcleo. No hay nada guardado: cualquier
    /// réplica puede contestar la consulta. Si el ID dice que un cambio quedó sin respuesta hace menos de
    /// <see cref="SentWindow"/>, solo se relee: si el estado ya llegó termina, y si no, sigue en curso con el mismo ID.
    /// </summary>
    public Task<ModuleResponse> Poll(ModuleCall call, string operationId)
    {
        foreach (var operation in Resumable)
        {
            var prefix = OperationId(operation, call);
            var rest = operationId.StartsWith(prefix, StringComparison.Ordinal) ? operationId[prefix.Length..] : null;
            if (rest == string.Empty)
            {
                return Resume(operation, call, held: null);
            }

            if (rest is not null && rest.StartsWith(SentMark, StringComparison.Ordinal)
                && long.TryParse(rest[SentMark.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var sent))
            {
                var waiting = Math.Abs(time.GetUtcNow().ToUnixTimeSeconds() - sent) < SentWindow.TotalSeconds;
                return Resume(operation, call, waiting ? operationId : null);
            }
        }

        throw new ModuleException(ErrorCodes.NotFound, "No hay una operación con ese ID para este servicio.");
    }

    private Task<ModuleResponse> Resume(string operation, ModuleCall call, string? held) => operation switch
    {
        "create" => Create(call, held),
        "suspend" => Suspend(call, held),
        "unsuspend" => Unsuspend(call, held),
        "terminate" => Terminate(call, held),
        _ => ChangePlan(call, held),
    };

    /// <summary>
    /// Corre una operación con efecto. Si se acaba el tiempo y la operación se puede retomar, responde pending. Si falla
    /// una lectura después de mandar un cambio, el resultado es incierto. Con <paramref name="held"/> no se manda ningún
    /// cambio: es una consulta dentro de la ventana de uno que quedó sin respuesta.
    /// </summary>
    private async Task<ModuleResponse> Guarded(ModuleCall call, string operation, string? held, Func<Run, Task<ModuleResponse>> work)
    {
        var run = new Run(time, held is not null) { CanPend = operation is not ("create" or "run_action") };
        try
        {
            return await work(run).ConfigureAwait(false);
        }
        catch (Exception ex) when (held is not null && ex is HeldBack or OutOfTimeException or ModuleException)
        {
            // Dentro de la ventana, todo lo que no sea el estado pedido es la misma operación en curso: con el cambio a
            // medio ejecutar, la cuenta puede verse incompleta. Pasada la ventana se corre entera y ahí se informa.
            return ModuleResponse.Pending(held, 15);
        }
        catch (Exception ex) when (run.CanPend && run.LostAt is { } sent
            && ex is OutOfTimeException or ModuleException { Code: ErrorCodes.OutcomeUnknown or ErrorCodes.UpstreamUnavailable })
        {
            // Un cambio quedó sin respuesta y el estado todavía no lo confirma. El panel puede seguir ejecutándolo, así
            // que mandarlo de nuevo ahora duplicaría el efecto: la consulta solo relee hasta que pase la ventana.
            return ModuleResponse.Pending(string.Create(CultureInfo.InvariantCulture, $"{OperationId(operation, call)}{SentMark}{sent}"), 15);
        }
        catch (OutOfTimeException) when (run.CanPend)
        {
            return ModuleResponse.Pending(OperationId(operation, call), 15);
        }
        catch (OutOfTimeException) when (run.Mutated)
        {
            throw Fail.Unknown("Se terminó el tiempo después de mandar un cambio al panel.", "out_of_time");
        }
        catch (OutOfTimeException)
        {
            throw Fail.Unavailable("Se terminó el tiempo antes de cambiar nada en el panel.", "out_of_time");
        }
        catch (ModuleException ex) when (run.Mutated && ex.Code == ErrorCodes.UpstreamUnavailable)
        {
            throw Fail.Unknown($"{ex.Message} El cambio pudo haberse aplicado.", "unreadable_after_change");
        }
    }

    private static string OperationId(string operation, ModuleCall call) => $"{operation}-{Uuid(Context(call))}";

    // ---- Ayudas ----

    private IHostingPanel Panel(ModuleCall call, string operation, string? hidden = null) =>
        panels(call, new Budget(time, TimeSpan.FromSeconds(HostingModule.Timeouts.GetValueOrDefault(operation, 30)) - Margin), [hidden]);

    /// <summary>La cuenta de un servicio ya creado o importado: external_ids.username y external_ids.domain.</summary>
    private (IHostingPanel Panel, PanelAccount Account, string Uuid) Open(ModuleCall call, string operation, string? hidden = null)
    {
        var panel = Panel(call, operation, hidden);
        var context = Context(call);
        var ids = context.Service?.ExternalIds;
        var username = ids.Field("username").Text();
        var domain = Names.Domain(ids.Field("domain").Text());
        var bad = new List<string>();
        if (!Names.IsUsername(username))
        {
            bad.Add("external_ids.username");
        }

        if (domain is null)
        {
            bad.Add("external_ids.domain");
        }

        return bad.Count > 0 ? throw Fail.InvalidInput(bad) : (panel, new PanelAccount(username!, domain!), Uuid(context));
    }

    private static CallContext Context(ModuleCall call) => Parse(call.Context<CallContext>, "context");

    private static OperationInput Input(ModuleCall call) => Parse(call.Input<OperationInput>, "input");

    private static T Parse<T>(Func<T> read, string root)
    {
        try
        {
            return read();
        }
        catch (JsonException ex)
        {
            throw Fail.InvalidInput([ex.Path is { } path ? root + path.TrimStart('$') : root]);
        }
    }

    private static string Uuid(CallContext context) => Names.Uuid(context.Service?.Uuid) ?? throw Fail.InvalidInput(["service.uuid"]);

    /// <summary>El plan del producto. Si no sirve es un error del producto: invalid_config.</summary>
    private static string Plan(CallContext context)
    {
        var plan = context.Service?.Params.Field("plan").Text();
        return Names.IsPlan(plan) ? plan! : throw Fail.InvalidConfig(["plan"], "El producto no tiene un plan del panel válido.");
    }

    private static string Email(CallContext context) =>
        Names.IsEmail(context.Account?.Email) ? context.Account!.Email! : throw Fail.InvalidInput(["account.email"], "El panel exige el correo de la cuenta (permiso account.contact).");

    private static string? Password(ModuleCall call, bool required)
    {
        var password = Input(call).Credentials.Field("password").Text();
        return (password is null && !required) || Names.IsPassword(password) ? password : throw Fail.InvalidInput(["credentials.password"]);
    }

    private static bool Same(string? a, string? b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>La cuenta tiene la marca de este servicio.</summary>
    private static bool Marked(AccountState state, string uuid) => Same(state.Marker, uuid);

    /// <summary>
    /// La cuenta tiene la marca de otro servicio. Sin marca (cuentas importadas, paneles sin dónde guardarla) decide el
    /// dominio, y también con un valor que no es un uuid: es el ID que dejó otro sistema, por ejemplo el de facturación
    /// anterior, y no dice de qué servicio es.
    /// </summary>
    private static bool Foreign(AccountState state, string uuid) => Names.Uuid(state.Marker) is { } marker && marker != uuid;

    /// <summary>Las condiciones para cambiar una cuenta existente: que esté y que sea del servicio.</summary>
    /// <param name="markedIsEnough">La marca del servicio alcanza como prueba, aunque a la cuenta le falte el dominio.</param>
    private static void Require(AccountState state, string uuid, bool markedIsEnough = false)
    {
        if (!state.Exists)
        {
            throw Fail.NotFound("La cuenta no existe en el panel.");
        }

        if (Foreign(state, uuid))
        {
            throw Fail.Review("La cuenta del panel tiene la marca de otro servicio. No se tocó nada.", "marker_mismatch");
        }

        if (!state.OwnsDomain && !(markedIsEnough && Marked(state, uuid)))
        {
            throw Fail.Review("La cuenta del panel no tiene el dominio del servicio. No se tocó nada.", "domain_mismatch");
        }
    }

    /// <summary>El cambio no llegó al estado pedido. El texto del panel nunca alcanza como prueba de éxito.</summary>
    private static ModuleException NotConverged(Ack ack, string message) =>
        ack.Refusal is { } text ? Fail.Rejected($"{message} El panel rechazó el cambio.", "upstream_rejected", text)
        : ack.Lost ? Fail.Unknown($"{message} El panel no respondió al cambio.", "no_reply")
        : Fail.Unknown($"{message} El panel respondió OK pero el estado no cambió.", "not_converged");

    private static (string State, string? Reason) Observe(AccountState state, string uuid) => state switch
    {
        { Exists: false } => ("not_found", null),
        _ when Foreign(state, uuid) => ("unknown", "marker_mismatch"),
        { OwnsDomain: false } => ("unknown", "domain_mismatch"),
        { Suspended: true } => ("suspended", null),
        _ => ("active", null),
    };

    private static ModuleResponse Completed(PanelAccount account, string observed) => ModuleResponse.Completed(new JsonObject
    {
        ["observed_state"] = observed,
        ["external_ids"] = new JsonObject { ["username"] = account.Username, ["domain"] = account.Domain },
    });

    /// <summary>Los bloques llevan la forma común del núcleo y de los demás módulos: el color en level y las listas en items.</summary>
    private static JsonObject Status(string level, string text) => new() { ["type"] = "status", ["level"] = level, ["text"] = text };

    private static JsonObject Pair(string label, string? value) => new() { ["label"] = label, ["value"] = value };

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    /// <summary>Lo que pasó en una corrida: si ya se mandó un cambio y si una consulta puede retomarla.</summary>
    /// <param name="held">Una consulta dentro de la ventana de un cambio sin respuesta: no se manda ningún otro.</param>
    private sealed class Run(TimeProvider time, bool held)
    {
        public bool CanPend { get; set; }

        public bool Mutated { get; private set; }

        /// <summary>Cuándo salió el último cambio que quedó sin respuesta, en segundos Unix.</summary>
        public long? LostAt { get; private set; }

        public async Task<Ack> Mutate(Func<Task<Ack>> change)
        {
            if (held)
            {
                throw new HeldBack();
            }

            var sent = time.GetUtcNow().ToUnixTimeSeconds();
            var ack = await change().ConfigureAwait(false);

            // Recién acá: si no se pudo conectar o no quedaba tiempo, el panel tira la excepción y el pedido no salió.
            Mutated = true;
            if (ack.Lost)
            {
                LostAt = sent;
            }

            return ack;
        }
    }

    /// <summary>Falta un cambio, y el anterior puede seguir corriendo en el panel.</summary>
    private sealed class HeldBack() : Exception("Falta un cambio y el anterior puede seguir corriendo en el panel.");
}
