using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Hestia;

/// <summary>
/// Las operaciones del módulo. Ninguna guarda nada entre pedidos: cada una lee el estado de Hestia, cambia solo lo que
/// falta y vuelve a leer. Por eso repetirla, en esta réplica o en otra, no repite el efecto. La excepción es un mutador
/// que Hestia sigue ejecutando sin haber respondido: para ese caso la operación queda en curso con el momento en que
/// salió, y la consulta solo relee hasta que pasa <see cref="SentWindow"/>.
/// </summary>
internal sealed class HestiaOperations(Func<string?, HttpClient> http, ILogger logger, TimeProvider time)
{
    /// <summary>Margen para contestar antes de que el núcleo deje de esperar.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(10);

    /// <summary>Las operaciones que una consulta (sección 7 del protocolo) retoma desde el estado de Hestia.</summary>
    private static readonly string[] Resumable = ["create", "suspend", "unsuspend", "terminate", "change_plan"];

    /// <summary>
    /// Cuánto tiempo después de un mutador sin respuesta la consulta solo relee. Hestia sigue ejecutando el comando
    /// aunque se corte la llamada, y un borrado grande tarda.
    /// </summary>
    private static readonly TimeSpan SentWindow = TimeSpan.FromMinutes(10);

    /// <summary>Lo que se agrega al ID de la operación cuando un mutador quedó sin respuesta, antes del momento en segundos Unix.</summary>
    private const string SentMark = "-sent-";

    // ---- Prueba de la instancia y validación ----

    /// <summary>Prueba la access key, la IP permitida y el certificado con dos lecturas. Devuelve los paquetes.</summary>
    public async Task<ModuleResponse> Test(ModuleCall call)
    {
        var hestia = Client(call, "test");
        if (await hestia.List("v-list-users").ConfigureAwait(false) is not JsonObject { Count: > 0 })
        {
            throw Fail.Unavailable("Hestia devolvió un inventario de usuarios vacío o ilegible.", "unreadable");
        }

        return ModuleResponse.Completed(new JsonObject { ["packages"] = Strings(await Packages(hestia).ConfigureAwait(false)) });
    }

    /// <summary>Los mismos datos que create, sin cambiar nada: formato del dominio, paquete existente y dominio libre.</summary>
    public async Task<ModuleResponse> Validate(ModuleCall call)
    {
        var context = Context(call);
        var package = Package(context);
        var domain = Names.Domain(context.Service?.Order.Field("domain").Text()) ?? throw Fail.InvalidInput(["domain"]);
        if (!Names.IsEmail(context.Account?.Email))
        {
            throw Fail.InvalidInput(["account.email"], "Hestia exige el correo de la cuenta (permiso account.contact).");
        }

        var hestia = Client(call, "validate");

        // Un paquete que no existe es un error del producto, no del cliente.
        if (!(await Packages(hestia).ConfigureAwait(false)).Contains(package, StringComparer.Ordinal))
        {
            throw Fail.InvalidConfig(["package"], $"El paquete {package} no existe en Hestia.");
        }

        if (await DomainOwner(hestia, domain).ConfigureAwait(false) is not null)
        {
            throw Fail.InvalidInput(["domain"], "El dominio ya está cargado en Hestia.", "domain_taken");
        }

        return ModuleResponse.Completed(new JsonObject());
    }

    // ---- Ciclo de vida ----

    /// <summary>
    /// Alta idempotente por estado: el usuario es el prefijo más el ID del servicio, así que un reintento encuentra lo
    /// que dejó el intento anterior y sigue desde ahí. Sin vuelta atrás: lo creado queda para que decida el staff.
    /// </summary>
    public Task<ModuleResponse> Create(ModuleCall call, string? held = null) => Guarded(call, "create", held, async run =>
    {
        var context = Context(call);
        var service = context.Service ?? throw Fail.InvalidInput(["service"]);
        var package = Package(context);
        var domain = Names.Domain(service.Order.Field("domain").Text() ?? service.ExternalIds.Field("domain").Text())
            ?? throw Fail.InvalidInput(["domain"]);
        var email = context.Account?.Email;
        if (!Names.IsEmail(email))
        {
            throw Fail.InvalidInput(["account.email"], "Hestia exige el correo de la cuenta (permiso account.contact).");
        }

        // La consulta de una operación larga no trae la contraseña: solo sirve una vez que la cuenta existe.
        var password = Password(call, required: call.Operation == "create");
        var hestia = Client(call, "create", password);
        var username = service.ExternalIds.Field("username").Text() ?? NewUsername(hestia.Settings.UsernamePrefix, context);
        if (!Names.IsUsername(username))
        {
            throw Fail.InvalidInput(["external_ids.username"]);
        }

        var account = new HestiaAccount(username, domain);
        var state = await AccountState.Read(hestia, account).ConfigureAwait(false);
        if (state.Presence == Presence.Unreadable)
        {
            throw Unreadable(state);
        }

        if (state.Presence == Presence.Absent)
        {
            if (password is null)
            {
                throw Fail.Unknown("La cuenta no está en Hestia y la consulta no trae la contraseña. Hay que repetir el alta con la misma clave.", "user_missing");
            }

            string[] args = Names.Display(context.Account?.Name) is { } name
                ? [username, password, email!, package, name]
                : [username, password, email!, package];
            var added = await run.Mutate(hestia, "v-add-user", args).ConfigureAwait(false);
            state = await AccountState.Read(hestia, account).ConfigureAwait(false);
            if (state.Presence != Presence.Present)
            {
                throw state.Presence == Presence.Unreadable ? Unreadable(state) : NotConverged(added, "La cuenta no quedó creada en Hestia.");
            }
        }
        else if (!state.OwnsDomain && !(state.Empty && string.Equals(state.User.Field("CONTACT").Text(), email, StringComparison.OrdinalIgnoreCase)))
        {
            // Una repetición se reconoce por el dominio, o por el correo en una cuenta vacía como la que deja un alta
            // cortada. Con otra cosa el usuario es de otro, o tiene sitios que un terminate de este servicio borraría.
            throw Fail.Rejected($"El usuario {username} ya existe en Hestia y no es de este servicio.", "user_taken", null);
        }

        // Desde acá la cuenta existe: si se acaba el tiempo, una consulta puede terminar el alta.
        run.CanPend = true;
        if (state.Suspended || state.Partial)
        {
            throw Fail.Conflict("La cuenta de Hestia ya existe y está suspendida o con recursos en otro estado.", state.Partial ? "partial" : "suspended");
        }

        var changed = false;

        // Una cuenta que dejó un alta anterior puede tener otro paquete, por ejemplo si el staff la repite con otro plan.
        Ack? packaged = null;
        if (state.Package != package)
        {
            changed = true;
            packaged = await run.Mutate(hestia, "v-change-user-package", username, package).ConfigureAwait(false);
        }

        var language = Names.Language(context.Account?.Language) ?? hestia.Settings.DefaultLanguage;
        if (state.User.Field("LANGUAGE").Text() != language)
        {
            // Una falla de idioma no hace fallar el alta: si Hestia rechaza el idioma se prueba el de la instancia, y si
            // no, queda un aviso.
            changed = true;
            var ack = await run.Mutate(hestia, "v-change-user-language", username, language).ConfigureAwait(false);
            if (ack.Refusal is not null && language != hestia.Settings.DefaultLanguage)
            {
                language = hestia.Settings.DefaultLanguage;
                await run.Mutate(hestia, "v-change-user-language", username, language).ConfigureAwait(false);
            }
        }

        Ack? addedDomain = null;
        if (!state.OwnsDomain)
        {
            changed = true;
            addedDomain = await run.Mutate(hestia, "v-add-domain", username, domain).ConfigureAwait(false);
        }

        if (changed)
        {
            state = await AccountState.Read(hestia, account).ConfigureAwait(false);
            if (state.Presence != Presence.Present)
            {
                throw state.Presence == Presence.Unreadable ? Unreadable(state) : Fail.Unknown("La cuenta desapareció de Hestia durante el alta.", "user_missing");
            }
        }

        if (!state.OwnsDomain)
        {
            throw addedDomain?.Refusal is { } text
                ? Fail.Rejected("Hestia no agregó el dominio a la cuenta.", "domain_rejected", text)
                : NotConverged(addedDomain ?? Ack.Done, "El dominio no quedó en la cuenta.");
        }

        if (state.Package != package)
        {
            throw NotConverged(packaged ?? Ack.Done, $"La cuenta no quedó con el paquete {package}.");
        }

        if (!state.Is(suspended: false))
        {
            throw Fail.Conflict("La cuenta quedó con recursos en otro estado.", "partial", new JsonObject { ["resources"] = Strings(state.Mixed) });
        }

        var warnings = new JsonArray();
        if (state.User.Field("LANGUAGE").Text() != language)
        {
            warnings.Add(Warning("language_not_applied", $"Hestia no aplicó el idioma {language} al panel."));
        }

        return Completed(account, "active", warnings);
    });

    public Task<ModuleResponse> Suspend(ModuleCall call) => Toggle(call, "suspend", suspended: true, held: null);

    public Task<ModuleResponse> Unsuspend(ModuleCall call) => Toggle(call, "unsuspend", suspended: false, held: null);

    private Task<ModuleResponse> Toggle(ModuleCall call, string operation, bool suspended, string? held) => Guarded(call, operation, held, async run =>
    {
        var (hestia, account) = Open(call, operation);
        var state = await AccountState.Read(hestia, account).ConfigureAwait(false);
        Require(state);
        if (state.Suspended != suspended)
        {
            var ack = await run.Mutate(hestia, suspended ? "v-suspend-user" : "v-unsuspend-user", account.Username).ConfigureAwait(false);
            var after = await AccountState.Read(hestia, account).ConfigureAwait(false);
            if (!after.Is(suspended))
            {
                throw after.Presence == Presence.Unreadable ? Unreadable(after) : NotConverged(ack, "La cuenta no quedó en el estado pedido.");
            }
        }

        return Completed(account, suspended ? "suspended" : "active");
    });

    /// <summary>Borra sitio, correo, bases y DNS. Por eso exige la ausencia probada, antes para no mutar y después para terminar.</summary>
    public Task<ModuleResponse> Terminate(ModuleCall call, string? held = null) => Guarded(call, "terminate", held, async run =>
    {
        var (hestia, account) = Open(call, "terminate");
        var state = await AccountState.Read(hestia, account).ConfigureAwait(false);
        if (state.Presence != Presence.Absent)
        {
            // Un estado parcial no frena la baja: es justo lo que hay que borrar.
            Require(state, partialOk: true);
            var ack = await run.Mutate(hestia, "v-delete-user", account.Username).ConfigureAwait(false);
            var after = await AccountState.Read(hestia, account).ConfigureAwait(false);
            if (after.Presence != Presence.Absent)
            {
                throw after.Presence == Presence.Unreadable ? Unreadable(after) : NotConverged(ack, "La cuenta sigue en Hestia.");
            }
        }

        return Completed(account, "terminated");
    });

    /// <summary>Cambia el paquete y exige leerlo después. Hestia reaplica del paquete el shell, los nameservers y los backups.</summary>
    public Task<ModuleResponse> ChangePlan(ModuleCall call, string? held = null) => Guarded(call, "change_plan", held, async run =>
    {
        var package = Package(Context(call));
        var (hestia, account) = Open(call, "change_plan");
        var state = await AccountState.Read(hestia, account).ConfigureAwait(false);
        Require(state);
        var warnings = new JsonArray();
        if (state.Package != package)
        {
            var shell = state.User.Field("SHELL").Text();
            var ack = await run.Mutate(hestia, "v-change-user-package", account.Username, package).ConfigureAwait(false);
            state = await AccountState.Read(hestia, account).ConfigureAwait(false);
            if (state.Presence != Presence.Present || state.Package != package)
            {
                throw state.Presence == Presence.Unreadable ? Unreadable(state) : NotConverged(ack, $"La cuenta no quedó con el paquete {package}.");
            }

            if (state.User.Field("SHELL").Text() is var now && now != shell)
            {
                warnings.Add(Warning("shell_changed", $"Hestia aplicó el shell del paquete: pasó de {shell} a {now}."));
            }
        }

        // Hestia reaplica el paquete entero: la cuenta tiene que seguir entera y con el dominio, como en get_status.
        var (observed, reason) = Observe(state);
        if (observed is not ("active" or "suspended"))
        {
            throw Fail.Conflict(
                "Después del cambio de plan la cuenta quedó con recursos en otro estado o sin el dominio del servicio.",
                reason ?? "partial",
                new JsonObject { ["resources"] = Strings(state.Mixed) });
        }

        return Completed(account, observed, warnings);
    });

    /// <summary>La única acción: change_password, con la contraseña que generó y guardó el núcleo.</summary>
    public Task<ModuleResponse> RunAction(ModuleCall call) => Guarded(call, "run_action", null, async run =>
    {
        var action = Inputs.Read(call.Input<OperationInput>).Action;
        if (action != "change_password")
        {
            throw new ModuleException(ErrorCodes.NotSupported, $"Acción no soportada: {action}.");
        }

        var password = Password(call, required: true)!;
        var (hestia, account) = Open(call, "run_action", password);
        Require(await AccountState.Read(hestia, account).ConfigureAwait(false), partialOk: true);
        var ack = await run.Mutate(hestia, "v-change-user-password", account.Username, password).ConfigureAwait(false);
        if (ack.Ok)
        {
            return ModuleResponse.Completed(new JsonObject { ["action"] = "change_password" });
        }

        // La contraseña no se puede leer: sin el OK no hay cómo confirmar. Repetir con la misma es seguro.
        throw ack.Lost
            ? Fail.Unknown("Hestia no respondió al cambio de contraseña. Repetir con la misma clave y la misma contraseña es seguro.", "no_reply")
            : Fail.Rejected("Hestia rechazó la contraseña nueva.", "password_rejected", ack.Refusal);
    });

    // ---- Estado y vista ----

    public async Task<ModuleResponse> GetStatus(ModuleCall call)
    {
        var (hestia, account) = Open(call, "get_status");
        var state = await AccountState.Read(hestia, account).ConfigureAwait(false);
        var (observed, reason) = Observe(state);
        var details = Details(state);
        if (reason is not null)
        {
            details["reason"] = reason;
        }

        return ModuleResponse.Completed(new JsonObject { ["observed_state"] = observed, ["details"] = details });
    }

    /// <summary>Bloques de texto plano para el portal. Nunca lleva contraseñas: el enlace va al login de Hestia, sin credenciales.</summary>
    public async Task<ModuleResponse> View(ModuleCall call)
    {
        var spanish = Names.Language(Context(call).Account?.Language) is not "en";
        string T(string es, string en) => spanish ? es : en;

        var (hestia, account) = Open(call, "view");
        var state = await AccountState.Read(hestia, account).ConfigureAwait(false);
        var (observed, _) = Observe(state);
        var blocks = new JsonArray(observed switch
        {
            "active" => Status("ok", T("Activo", "Active")),
            "suspended" => Status("warning", T("Suspendido", "Suspended")),
            "not_found" => Status("error", T("La cuenta no está en el panel", "The account is not in the panel")),
            _ => Status("warning", T("En revisión", "Under review")),
        });

        // Con un dominio ajeno, por ejemplo un mapeo mal importado, la cuenta puede ser de otro cliente: ni su uso ni el enlace.
        if (state.User is { } user && state.OwnsDomain)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "fields",
                ["items"] = new JsonArray(
                    Pair(T("Usuario del panel", "Panel user"), account.Username),
                    Pair(T("Dominio", "Domain"), account.Domain),
                    Pair(T("Paquete", "Package"), user["PACKAGE"].Text()),
                    Pair(T("Disco", "Disk"), Usage(user["U_DISK"].Text(), user["DISK_QUOTA"].Text(), spanish)),
                    Pair(T("Transferencia", "Bandwidth"), Usage(user["U_BANDWIDTH"].Text(), user["BANDWIDTH"].Text(), spanish))),
            });
            blocks.Add(new JsonObject { ["type"] = "link", ["label"] = T("Entrar al panel", "Open the panel"), ["url"] = hestia.Settings.LoginUrl });
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
                    "El hosting tiene una parte en otro estado. El equipo de soporte lo va a revisar.",
                    "Part of the hosting is in a different state. The support team will review it."),
            });
        }

        return ModuleResponse.Completed(new JsonObject { ["blocks"] = blocks });
    }

    // ---- Operaciones largas ----

    /// <summary>
    /// Retoma la operación desde el estado de Hestia con el sobre que manda el núcleo. No hay nada guardado: cualquier
    /// réplica puede contestar la consulta. Si el ID dice que un mutador quedó sin respuesta hace menos de
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
        "suspend" => Toggle(call, "suspend", suspended: true, held),
        "unsuspend" => Toggle(call, "unsuspend", suspended: false, held),
        "terminate" => Terminate(call, held),
        _ => ChangePlan(call, held),
    };

    /// <summary>
    /// Corre una operación con efecto. Si se acaba el tiempo y la operación se puede retomar, responde pending. Si falla
    /// una lectura después de mandar un cambio, el resultado es incierto. Con <paramref name="held"/> no se manda ningún
    /// mutador: es una consulta dentro de la ventana de uno que quedó sin respuesta.
    /// </summary>
    private async Task<ModuleResponse> Guarded(ModuleCall call, string operation, string? held, Func<Run, Task<ModuleResponse>> work)
    {
        var run = new Run(time, held is not null) { CanPend = operation is not ("create" or "run_action") };
        try
        {
            return await work(run).ConfigureAwait(false);
        }
        catch (Exception ex) when (held is not null && ex is HeldBack or OutOfTime or HestiaRefusal or ModuleException)
        {
            // Dentro de la ventana, todo lo que no sea el estado pedido es la misma operación en curso: con el comando
            // a medio ejecutar, la cuenta puede verse incompleta. Pasada la ventana se corre entera y ahí se informa.
            return ModuleResponse.Pending(held, 15);
        }
        catch (Exception ex) when (run.CanPend && run.LostAt is { } sent
            && ex is OutOfTime or HestiaRefusal or ModuleException { Code: ErrorCodes.OutcomeUnknown or ErrorCodes.UpstreamUnavailable })
        {
            // Un mutador quedó sin respuesta y el estado todavía no lo confirma. Hestia puede seguir ejecutándolo, así
            // que mandarlo de nuevo ahora duplicaría el efecto: la consulta solo relee hasta que pase la ventana.
            return ModuleResponse.Pending(string.Create(CultureInfo.InvariantCulture, $"{OperationId(operation, call)}{SentMark}{sent}"), 15);
        }
        catch (OutOfTime) when (run.CanPend)
        {
            return ModuleResponse.Pending(OperationId(operation, call), 15);
        }
        catch (OutOfTime) when (run.Mutated)
        {
            throw Fail.Unknown("Se terminó el tiempo después de mandar un cambio a Hestia.", "out_of_time");
        }
        catch (OutOfTime)
        {
            throw Fail.Unavailable("Se terminó el tiempo antes de cambiar nada en Hestia.", "out_of_time");
        }
        catch (ModuleException ex) when (run.Mutated && ex.Code == ErrorCodes.UpstreamUnavailable)
        {
            throw Fail.Unknown($"{ex.Message} El cambio pudo haberse aplicado.", "unreadable_after_change");
        }
        catch (HestiaRefusal refusal) when (run.Mutated)
        {
            throw Fail.Unknown($"Hestia respondió un error inesperado después de un cambio: {refusal.Text}", "unexpected_refusal");
        }
    }

    private static string OperationId(string operation, ModuleCall call) =>
        string.Create(CultureInfo.InvariantCulture, $"{operation}-svc{ServiceId(Context(call))}");

    // ---- Ayudas ----

    private HestiaClient Client(ModuleCall call, string operation, string? hidden = null)
    {
        var settings = Settings.From(call);
        var budget = new Budget(time, TimeSpan.FromSeconds(HestiaModule.Timeouts.GetValueOrDefault(operation, 30)) - Margin);
        return new HestiaClient(http(settings.Fingerprint), settings, budget, logger, [hidden]);
    }

    /// <summary>La cuenta de un servicio ya creado o importado: external_ids.username y external_ids.domain.</summary>
    private (HestiaClient Hestia, HestiaAccount Account) Open(ModuleCall call, string operation, string? hidden = null)
    {
        var hestia = Client(call, operation, hidden);
        var ids = Context(call).Service?.ExternalIds;
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

        return bad.Count > 0 ? throw Fail.InvalidInput(bad) : (hestia, new HestiaAccount(username!, domain!));
    }

    private static CallContext Context(ModuleCall call) => Inputs.Read(call.Context<CallContext>);

    private static long ServiceId(CallContext context) => context.Service?.Id ?? throw Fail.InvalidInput(["service.id"]);

    /// <summary>
    /// El usuario de una cuenta nueva, que es su marca externa en Hestia (sección 6 del protocolo): el prefijo, el ID
    /// del servicio y, si el núcleo manda el uuid, cuatro caracteres en base32 del SHA-256 del uuid. El mismo servicio
    /// da siempre el mismo usuario, así una repetición sin registro de idempotencia encuentra la cuenta en la consulta
    /// previa, y con el uuid no choca con el mismo ID de otra instalación que comparte el panel.
    /// </summary>
    private static string NewUsername(string prefix, CallContext context)
    {
        var id = ServiceId(context).ToString(CultureInfo.InvariantCulture);
        if (context.Service?.Uuid is not { } text)
        {
            return prefix + id;
        }

        if (!Guid.TryParseExact(text, "D", out var uuid) || uuid == Guid.Empty)
        {
            throw Fail.InvalidInput(["service.uuid"]);
        }

        // Los primeros 20 bits del hash en base32 (RFC 4648, en minúsculas): letras y dígitos que Hestia acepta.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(uuid.ToString("D")));
        var bits = (hash[0] << 12) | (hash[1] << 4) | (hash[2] >> 4);
        const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        return prefix + id + string.Concat(Enumerable.Range(0, 4).Select(i => Alphabet[(bits >> (15 - (5 * i))) & 31]));
    }

    /// <summary>El paquete del producto. Si no sirve es un error del producto: invalid_config.</summary>
    private static string Package(CallContext context)
    {
        var package = context.Service?.Params.Field("package").Text();
        return Names.IsPackage(package) ? package! : throw Fail.InvalidConfig(["package"], "El producto no tiene un paquete de Hestia válido.");
    }

    private static string? Password(ModuleCall call, bool required)
    {
        var password = Inputs.Read(call.Input<OperationInput>).Credentials.Field("password").Text();
        return (password is null && !required) || Names.IsPassword(password) ? password : throw Fail.InvalidInput(["credentials.password"]);
    }

    /// <summary>El usuario dueño del dominio en Hestia, o null si nadie lo tiene.</summary>
    private static async Task<string?> DomainOwner(HestiaClient hestia, string domain)
    {
        string text;
        try
        {
            text = await hestia.Text("v-search-domain-owner", domain).ConfigureAwait(false);
        }
        catch (HestiaRefusal refusal) when ((refusal.Status == 404 && refusal.Blank)
            || refusal.Text.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase)
            || refusal.Text.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var owner = text.Split('\n')[0].Trim();
        if (owner.Length == 0 || owner == "OK")
        {
            return null;
        }

        return Names.IsUsername(owner) ? owner : throw Fail.Unavailable("Hestia respondió algo inesperado al buscar el dueño del dominio.", "unreadable");
    }

    private static async Task<List<string>> Packages(HestiaClient hestia) =>
        await hestia.List("v-list-user-packages").ConfigureAwait(false) is JsonObject list
            ? [.. list.Select(p => p.Key)]
            : throw Fail.Unavailable("Hestia devolvió una lista de paquetes ilegible.", "unreadable");

    private static ModuleException Unreadable(Snapshot state) =>
        Fail.Unavailable($"No se pudo verificar el estado de la cuenta en Hestia ({state.Problem}).", state.Problem);

    /// <summary>Las condiciones para cambiar una cuenta existente.</summary>
    private static void Require(Snapshot state, bool partialOk = false)
    {
        if (state.Presence == Presence.Unreadable)
        {
            throw Unreadable(state);
        }

        if (state.Presence == Presence.Absent)
        {
            throw Fail.NotFound("La cuenta no existe en Hestia. La ausencia se comprobó con el inventario de usuarios.");
        }

        if (!state.OwnsDomain)
        {
            throw Fail.InvalidInput(["external_ids.domain"], "La cuenta de Hestia no tiene el dominio del servicio.", "domain_mismatch");
        }

        if (state.Partial && !partialOk)
        {
            throw Fail.Conflict(
                $"La cuenta tiene recursos en otro estado: {string.Join(", ", state.Mixed)}.",
                "partial",
                new JsonObject { ["resources"] = Strings(state.Mixed) });
        }
    }

    /// <summary>El cambio no llegó al estado pedido. El texto de Hestia nunca alcanza como prueba de éxito.</summary>
    private static ModuleException NotConverged(Ack ack, string message) =>
        ack.Refusal is { } text ? Fail.Rejected($"{message} Hestia rechazó el cambio.", "upstream_rejected", text)
        : ack.Lost ? Fail.Unknown($"{message} Hestia no respondió al cambio.", "no_reply")
        : Fail.Unknown($"{message} Hestia respondió OK pero el estado no cambió.", "not_converged");

    private static (string State, string? Reason) Observe(Snapshot state) => state.Presence switch
    {
        Presence.Absent => ("not_found", null),
        Presence.Unreadable => ("unknown", state.Problem),
        _ when !state.OwnsDomain => ("unknown", "domain_mismatch"),
        _ when state.Partial => ("unknown", "partial"),
        _ => (state.Suspended ? "suspended" : "active", null),
    };

    /// <summary>Paquete, suspensión por recurso, disco y transferencia, para la conciliación.</summary>
    private static JsonObject Details(Snapshot state)
    {
        var details = new JsonObject();
        if (state.User is { } user)
        {
            details["package"] = user["PACKAGE"].Text();
            details["suspended"] = state.Suspended;
            details["resources"] = state.Resources.DeepClone();
            details["mixed"] = Strings(state.Mixed);
            details["disk"] = new JsonObject { ["used_mb"] = user["U_DISK"].Text(), ["quota_mb"] = user["DISK_QUOTA"].Text() };
            details["bandwidth"] = new JsonObject { ["used_mb"] = user["U_BANDWIDTH"].Text(), ["limit_mb"] = user["BANDWIDTH"].Text() };
        }

        return details;
    }

    private static ModuleResponse Completed(HestiaAccount account, string observed, JsonArray? warnings = null)
    {
        var result = new JsonObject
        {
            ["observed_state"] = observed,
            ["external_ids"] = new JsonObject { ["username"] = account.Username, ["domain"] = account.Domain },
        };
        if (warnings is { Count: > 0 })
        {
            result["warnings"] = warnings;
        }

        return ModuleResponse.Completed(result);
    }

    private static string Usage(string? used, string? limit, bool spanish) =>
        $"{used ?? "?"} MB {(spanish ? "de" : "of")} {(limit == "unlimited" ? (spanish ? "ilimitado" : "unlimited") : $"{limit ?? "?"} MB")}";

    private static JsonObject Status(string level, string text) => new() { ["type"] = "status", ["level"] = level, ["text"] = text };

    private static JsonObject Pair(string label, string? value) => new() { ["label"] = label, ["value"] = value };

    private static JsonObject Warning(string code, string message) => new() { ["code"] = code, ["message"] = message };

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    /// <summary>Lo que pasó en una corrida: si ya se mandó un cambio y si una consulta puede retomarla.</summary>
    /// <param name="held">Una consulta dentro de la ventana de un mutador sin respuesta: no se manda ningún otro.</param>
    private sealed class Run(TimeProvider time, bool held)
    {
        public bool CanPend { get; set; }

        public bool Mutated { get; private set; }

        /// <summary>Cuándo salió el último mutador que quedó sin respuesta, en segundos Unix.</summary>
        public long? LostAt { get; private set; }

        public async Task<Ack> Mutate(HestiaClient hestia, string command, params string[] args)
        {
            if (held)
            {
                throw new HeldBack();
            }

            hestia.EnsureRoom();
            var sent = time.GetUtcNow().ToUnixTimeSeconds();
            var ack = await hestia.Mutate(command, args).ConfigureAwait(false);

            // Recién acá: si no se pudo conectar, Mutate tira la excepción y el pedido no salió.
            Mutated = true;
            if (ack.Lost)
            {
                LostAt = sent;
            }

            return ack;
        }
    }

    /// <summary>Falta un cambio, y el mutador anterior puede seguir corriendo en Hestia.</summary>
    private sealed class HeldBack() : Exception("Falta un cambio y el anterior puede seguir corriendo en Hestia.");
}
