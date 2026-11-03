using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.Extensions.Logging;

namespace BillingOss.Module.Proxmox;

/// <summary>Esperas del módulo. Las de producción salen de los timeouts del manifiesto, con margen.</summary>
internal sealed record ProxmoxOptions(TimeSpan PollInterval, TimeSpan LongWait, TimeSpan ShortWait)
{
    public static ProxmoxOptions Default { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(40));
}

/// <summary>
/// Las operaciones del módulo. No guardan nada entre pedidos: el estado de un alta vive en Proxmox (la marca en la
/// descripción de la VM, su configuración y su firewall) y cada llamada lo lee antes de actuar. Por eso varias réplicas
/// detrás de un balanceador no duplican nada, y una consulta que cae en otra réplica sigue donde quedó la anterior.
/// </summary>
internal sealed partial class Operations(
    Func<X509Certificate2Collection?, HttpMessageHandler> handlers,
    HttpClient gateway,
    TimeProvider time,
    ILogger logger,
    ProxmoxOptions options)
{
    private const string MarkerPrefix = "billing-oss";
    private const string IpSet = "ipfilter-net0";
    private const int ReinstallWait = 1800;

    /// <summary>
    /// VMID alternativo cuando el ID del servicio está ocupado o reservado, por ejemplo por una plantilla. Es fijo por
    /// servicio para que una repetición encuentre el clon que ya arrancó, y queda arriba del rango de nextid de Proxmox.
    /// </summary>
    private const int FallbackOffset = 1_000_000;

    /// <summary>VMID de la reserva de la MAC durante una reinstalación: 2.000.000 más el ID del servicio.</summary>
    private const int ReserveOffset = 2_000_000;

    private static readonly string[] Recommended =
    [
        "VM.Allocate", "VM.Clone", "VM.Config.CPU", "VM.Config.Memory", "VM.Config.Disk", "VM.Config.Network",
        "VM.Config.Cloudinit", "VM.Config.Options", "VM.PowerMgmt",
    ];

    // ---- Instancia y validación ----

    public async Task<ModuleResponse> Test(ModuleCall call)
    {
        using var op = Open(call, options.ShortWait, service: false);
        if (await op.Pve.Get("/cluster/resources", "type=vm", TimeSpan.FromSeconds(20)).ConfigureAwait(false) is not JsonArray vms)
        {
            throw Errors.Unavailable("Proxmox devolvió un inventario inválido.");
        }

        var permissions = (await op.Pve.Get("/access/permissions", "path=%2Fvms").ConfigureAwait(false)).Field("/vms");
        if (permissions.Field("VM.Audit").Text() != "1")
        {
            throw Errors.Config("vm_audit_missing", "El token necesita VM.Audit heredado en /vms: sin eso el inventario de MACs sale filtrado sin error.", ["token_id"]);
        }

        if (op.Config.LegacyAclCleanup)
        {
            // Quitar la ACL pide Permissions.Modify en /vms, y ver los usuarios client pide Sys.Audit o User.Modify en
            // /access/groups: sin eso la suspensión fallaría o no vería al usuario.
            var groups = (await op.Pve.Get("/access/permissions", "path=%2Faccess%2Fgroups").ConfigureAwait(false)).Field("/access/groups");
            if (permissions.Field("Permissions.Modify").Text() != "1" || (groups.Field("Sys.Audit").Text() != "1" && groups.Field("User.Modify").Text() != "1"))
            {
                throw Errors.Config("legacy_acl_permissions", "Con legacy_acl_cleanup el token necesita Permissions.Modify en /vms y Sys.Audit o User.Modify en /access/groups.", ["token_id", "legacy_acl_cleanup"]);
            }
        }

        return ModuleResponse.Completed(new JsonObject
        {
            ["vms"] = vms.Count,
            ["missing_privileges"] = Strings(Recommended.Where(p => permissions.Field(p).Text() != "1")),
        });
    }

    /// <summary>
    /// Sin efectos y sin reservar red. Valida los recursos y la política de mínimos, y contra Proxmox la plantilla, el
    /// disco de la plantilla, los bridges de todas las entradas del pool, sus grupos de aislamiento y el pool de consola.
    /// </summary>
    public async Task<ModuleResponse> Validate(ModuleCall call)
    {
        using var op = Open(call, options.ShortWait, service: false);
        var input = call.Request.Input;
        var p = ProductParams.Read(input["params"] ?? call.Request.Context["service"].Field("params"));

        // Cambiar el sistema operativo es reinstalar, nunca un cambio de plan.
        if (input["current_params"].Field("template_id").Int() is { } current && current != p.TemplateId)
        {
            throw Errors.Input("template_id", "reinstall_required");
        }

        if (p.Violations() is { } violations)
        {
            throw Errors.Input(violations);
        }

        var resources = await op.Pve.Resources().ConfigureAwait(false);
        await CheckTemplate(op, p, resources).ConfigureAwait(false);
        await CheckPlacement(op, p, op.Config.Pool.Select(e => e.Bridge).Distinct()).ConfigureAwait(false);
        return ModuleResponse.Completed(new JsonObject { ["valid"] = true });
    }

    // ---- Alta ----

    public async Task<ModuleResponse> Create(ModuleCall call)
    {
        using var op = Open(call, options.LongWait);
        var password = Password(call);
        var p = Params(op);
        var marker = Marker(op, call.IdempotencyKey!);
        var resources = await op.Pve.Resources().ConfigureAwait(false);
        var vmid = await ChooseVmid(op, resources, marker).ConfigureAwait(false);
        return await Drive(op, new Plan("create", vmid, marker, password, null), p).ConfigureAwait(false);
    }

    /// <summary>
    /// Consulta de una operación larga. El operation_id trae todo lo que no está en el sobre (VMID y marca), así que
    /// cualquier réplica la puede seguir y nunca se "pierde".
    /// </summary>
    public async Task<ModuleResponse> Poll(ModuleCall call, string id)
    {
        if (RebootId().Match(id) is { Success: true } reboot)
        {
            // La tarea del reinicio viaja en el operation_id: se espera esa misma tarea y nunca se lanza otra.
            using var rebootOp = Open(call, options.ShortWait);
            var upid = Encoding.UTF8.GetString(Convert.FromHexString(reboot.Groups[2].Value));
            if (!Pve.IsTask(upid, "qmreboot", reboot.Groups[1].Value))
            {
                throw new ModuleException(ErrorCodes.NotFound, "Operación desconocida.");
            }

            return await rebootOp.Pve.Finish(JsonValue.Create(upid), rebootOp.Deadline, "reboot").ConfigureAwait(false) is null
                ? ModuleResponse.Pending(id, 15)
                : ModuleResponse.Completed(new JsonObject { ["power"] = "running" });
        }

        var match = OperationId().Match(id);
        if (!match.Success)
        {
            throw new ModuleException(ErrorCodes.NotFound, "Operación desconocida.");
        }

        if (match.Groups[4].Success)
        {
            return await Terminate(call).ConfigureAwait(false);
        }

        var kind = match.Groups[1].Value;
        using var op = Open(call, options.LongWait);
        var p = Params(op);
        var marker = ServicePrefix(op.Svc) + match.Groups[3].Value;
        var strict = kind == "reinstall" ? StrictNet(op) : null;
        var vmid = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return await Drive(op, new Plan(kind, vmid, marker, null, strict), p).ConfigureAwait(false);
    }

    // ---- Suspensión, reactivación y baja ----

    /// <summary>
    /// Quita la ACL heredada y revoca la consola antes de tocar la VM, así un error de permisos falla con la VM como
    /// estaba. Después apaga en seco si hace falta y deja onboot en 0 como último paso.
    /// </summary>
    public async Task<ModuleResponse> Suspend(ModuleCall call)
    {
        using var op = Open(call, options.ShortWait);
        var (vm, config) = await Owned(op).ConfigureAwait(false);
        NotLocked(vm, config);
        await CleanLegacyAcl(op, vm).ConfigureAwait(false);
        await ConsoleGateway.Revoke(gateway, op.Config, op.Svc.ServiceId, time, logger).ConfigureAwait(false);
        if (await Running(op, vm).ConfigureAwait(false) && !await Power(op, vm, "stop").ConfigureAwait(false))
        {
            throw Errors.Unknown("La VM se sigue apagando en Proxmox.");
        }

        if (config["onboot"].Text() != "0")
        {
            await op.Pve.Put($"{Pve.VmPath(vm.Node, vm.Vmid)}/config", ("onboot", "0")).ConfigureAwait(false);
        }

        return ModuleResponse.Completed(new JsonObject { ["observed_state"] = "suspended", ["external_ids"] = new JsonObject { ["node"] = vm.Node } });
    }

    /// <summary>Prende la VM si hace falta y pone onboot en 1. No vuelve a dar ACL heredadas.</summary>
    public async Task<ModuleResponse> Unsuspend(ModuleCall call)
    {
        using var op = Open(call, options.ShortWait);
        var (vm, config) = await Owned(op).ConfigureAwait(false);
        NotLocked(vm, config);
        if (!await Running(op, vm).ConfigureAwait(false) && !await Power(op, vm, "start").ConfigureAwait(false))
        {
            throw Errors.Unknown("La VM se sigue prendiendo en Proxmox.");
        }

        if (config["onboot"].Text() != "1")
        {
            await op.Pve.Put($"{Pve.VmPath(vm.Node, vm.Vmid)}/config", ("onboot", "1")).ConfigureAwait(false);
        }

        return ModuleResponse.Completed(new JsonObject { ["observed_state"] = "active", ["external_ids"] = new JsonObject { ["node"] = vm.Node } });
    }

    /// <summary>
    /// Borra solo la VM del servicio. Sin VMID (un alta que no terminó) busca la VM con el nombre del servicio y una
    /// marca de este módulo para el servicio. Que no exista es una baja hecha, pero solo con VM.Audit en /vms.
    /// </summary>
    public async Task<ModuleResponse> Terminate(ModuleCall call)
    {
        using var op = Open(call, options.ShortWait);
        var pve = op.Pve;
        await pve.RequireAudit().ConfigureAwait(false);
        var resources = await pve.Resources().ConfigureAwait(false);
        VmRef? vm = null;
        JsonObject? config = null;
        if (op.Svc.Vmid is { } vmid)
        {
            vm = resources.FirstOrDefault(r => r.Vmid == vmid);
        }
        else
        {
            // Los dos VMID donde clona el alta se miran por su configuración, porque el nombre del inventario llega tarde.
            var prefix = ServicePrefix(op.Svc);
            var slots = Slots(op.Svc);
            foreach (var candidate in resources.Where(r => slots.Contains(r.Vmid) || r.Name == VmName(op.Svc.ServiceId)))
            {
                var candidateConfig = await pve.Config(candidate).ConfigureAwait(false);
                if (slots.Contains(candidate.Vmid) && candidateConfig["lock"].Text() == "clone")
                {
                    // Un clon en curso todavía no tiene la marca: puede ser el del alta. Se consulta de nuevo.
                    return ModuleResponse.Pending($"terminate-{candidate.Vmid.ToString(CultureInfo.InvariantCulture)}", 15);
                }

                if (VmConfig.Description(candidateConfig).StartsWith(prefix, StringComparison.Ordinal))
                {
                    (vm, config) = (candidate, candidateConfig);
                    break;
                }
            }
        }

        if (vm is null)
        {
            await Release(op, resources).ConfigureAwait(false);
            return ModuleResponse.Completed(new JsonObject { ["observed_state"] = "terminated" });
        }

        config ??= await pve.Config(vm).ConfigureAwait(false);
        var pending = ModuleResponse.Pending($"terminate-{vm.Vmid.ToString(CultureInfo.InvariantCulture)}", 15);
        if (config["lock"] is not null)
        {
            // Un clon o un borrado en curso: se consulta de nuevo.
            return pending;
        }

        if (op.Svc.Vmid is not null && !IsOwner(op.Svc, vm, config))
        {
            throw Errors.Review("ownership", $"La VM {vm.Vmid} no tiene la marca ni el nombre de este servicio. No se borra.");
        }

        // Proxmox no pone lock mientras apaga ni mientras borra los discos: la tarea de un intento anterior que sigue
        // corriendo se sigue y no se lanza otra, que fallaría esperando el lock.
        if (await pve.ActiveTask(vm).ConfigureAwait(false) is { } active)
        {
            return active.Type == "qmdestroy" && await Destroyed(op, vm, config, active.Upid).ConfigureAwait(false) is { } done
                ? await Released(op, resources, done).ConfigureAwait(false)
                : pending;
        }

        await ConsoleGateway.Revoke(gateway, op.Config, op.Svc.ServiceId, time, logger).ConfigureAwait(false);
        if (await Running(op, vm).ConfigureAwait(false) && !await Power(op, vm, "stop").ConfigureAwait(false))
        {
            return pending;
        }

        // El DELETE de Proxmox borra también las ACL de /vms/{vmid}: no hace falta quitar la heredada.
        var task = await pve.Delete(Pve.VmPath(vm.Node, vm.Vmid)).ConfigureAwait(false);
        return await Destroyed(op, vm, config, task).ConfigureAwait(false) is { } terminated
            ? await Released(op, resources, terminated).ConfigureAwait(false)
            : pending;
    }

    private async Task<ModuleResponse> Released(Op op, List<VmRef> resources, ModuleResponse done)
    {
        await Release(op, resources).ConfigureAwait(false);
        return done;
    }

    /// <summary>
    /// Reserva la MAC de una reinstalación antes de borrar la VM: una VM vacía, sin discos, apagada, con onboot 0, la NIC
    /// sin enlace y la MAC en net0, en el VMID 2.000.000 más el servicio. Todas las réplicas la ven en el inventario de
    /// MACs como una MAC ganada, así ninguna alta la toma entre el borrado y el clon. Se borra apenas la VM nueva
    /// reclama la MAC, y la baja borra la que haya quedado de una reinstalación que no terminó.
    /// </summary>
    private async Task Reserve(Op op, NetEntry net, string node, List<VmRef> resources)
    {
        var vmid = ReserveVmid(op);
        if (vmid == 0)
        {
            logger.LogWarning("La reinstalación del servicio {Service} sigue sin reservar la MAC: el VMID de la reserva está fuera de rango o en reserved_vmids", op.Svc.ServiceId);
            return;
        }

        if (resources.FirstOrDefault(r => r.Vmid == vmid) is { } existing)
        {
            // La de un intento anterior de esta reinstalación se usa. Otra VM en ese VMID frena antes de borrar nada.
            var config = await op.Pve.Config(existing).ConfigureAwait(false);
            if (VmConfig.Description(config) != ReserveMark(op.Svc) || VmConfig.MacOf(config["net0"].Text()) != net.Mac)
            {
                throw Errors.Review("reserve_vmid_taken", $"El VMID {vmid} de la reserva de la MAC está ocupado por otra VM.");
            }

            return;
        }

        var path = Pve.VmPath(node, vmid);
        var task = await op.Pve.Post(
            path[..path.LastIndexOf('/')],
            ("vmid", vmid.ToString(CultureInfo.InvariantCulture)),
            ("name", $"rsv{op.Svc.ServiceId.ToString(CultureInfo.InvariantCulture)}"),
            ("description", ReserveMark(op.Svc)),
            ("net0", $"virtio={net.Mac.ToUpperInvariant()},bridge={net.Bridge},link_down=1"),
            ("onboot", "0")).ConfigureAwait(false);
        if (!await op.Pve.WaitIfTask(task, op.Deadline, "reservar la MAC").ConfigureAwait(false))
        {
            throw Errors.Busy("in_progress", "La reserva de la MAC se sigue creando.");
        }
    }

    /// <summary>Borra la reserva de la MAC del servicio si hay una. Si no se puede, queda para la próxima llamada o el staff.</summary>
    private async Task Release(Op op, List<VmRef> resources)
    {
        if (resources.FirstOrDefault(r => r.Vmid == ReserveVmid(op)) is not { } reserve)
        {
            return;
        }

        try
        {
            if (VmConfig.Description(await op.Pve.Config(reserve).ConfigureAwait(false)) == ReserveMark(op.Svc)
                && !await op.Pve.Wait(await op.Pve.Delete(Pve.VmPath(reserve.Node, reserve.Vmid)).ConfigureAwait(false), op.Deadline, "borrar la reserva de la MAC").ConfigureAwait(false))
            {
                logger.LogWarning("La reserva de la MAC del servicio {Service} se sigue borrando", op.Svc.ServiceId);
            }
        }
        catch (ModuleException ex)
        {
            logger.LogWarning("No se pudo borrar la reserva de la MAC del servicio {Service}: {Code}", op.Svc.ServiceId, ex.Code);
        }
    }

    /// <summary>El VMID de la reserva de la MAC del servicio, o 0 si queda fuera de rango o en reserved_vmids.</summary>
    private static int ReserveVmid(Op op) =>
        ReserveOffset + op.Svc.ServiceId is var vmid && vmid <= Pve.MaxVmid && !op.Config.IsReserved((int)vmid) ? (int)vmid : 0;

    /// <summary>La descripción de la reserva: billing-oss-reserva, el ID y el uuid del servicio si el núcleo lo manda.</summary>
    private static string ReserveMark(ServiceCtx svc) =>
        $"{VmConfig.ReservePrefix}{svc.ServiceId.ToString(CultureInfo.InvariantCulture)}" + (svc.Uuid is { } uuid ? ":" + uuid : string.Empty);

    /// <summary>
    /// Espera el borrado. Una tarea que falla con la VM ya borrada es la segunda de dos (la otra borró): se da por hecha.
    /// Un borrado con WARNINGS dejó algo en el storage, por ejemplo un disco que no se pudo borrar: la baja se completa y
    /// el resultado trae los volúmenes que tenía la VM para que el staff los revise. Null si sigue corriendo.
    /// </summary>
    private async Task<ModuleResponse?> Destroyed(Op op, VmRef vm, JsonObject config, JsonNode? task)
    {
        if (await Finished(op, vm, task, "borrar la VM").ConfigureAwait(false) is not { } exit)
        {
            return null;
        }

        var result = new JsonObject { ["observed_state"] = "terminated" };
        if (exit != "OK")
        {
            var volumes = Volumes(config);
            logger.LogWarning("El borrado de la VM {Vmid} terminó con avisos ({Exit}). Revisar en el storage: {Volumes}", vm.Vmid, op.Pve.Scrub(exit), string.Join(", ", volumes));
            result["details"] = new JsonObject
            {
                ["reason"] = "destroy_warnings",
                ["exitstatus"] = op.Pve.Scrub(exit),
                ["volumes"] = Strings(volumes),
            };
        }

        return ModuleResponse.Completed(result);
    }

    /// <summary>La tarea de borrado terminada, o null si sigue. Si falla pero la VM ya no está, la borró otra tarea.</summary>
    private static async Task<string?> Finished(Op op, VmRef vm, JsonNode? task, string what)
    {
        try
        {
            return await op.Pve.Finish(task, op.Deadline, what).ConfigureAwait(false);
        }
        catch (ModuleException ex) when (ex.Code is ErrorCodes.Rejected or ErrorCodes.Conflict)
        {
            if ((await op.Pve.Resources().ConfigureAwait(false)).All(r => r.Vmid != vm.Vmid))
            {
                return "OK";
            }

            throw;
        }
    }

    /// <summary>Los volúmenes de los discos de la VM (scsi, virtio, sata, ide, efidisk, tpmstate y unused), sin los CD.</summary>
    private static List<string> Volumes(JsonObject config) =>
        [.. config.Where(c => DiskKey().IsMatch(c.Key))
            .Select(c => c.Value.Text() ?? string.Empty)
            .Where(v => !v.Contains("media=cdrom", StringComparison.Ordinal))
            .Select(v => v.Split(',')[0])
            .Where(v => v.Contains(':', StringComparison.Ordinal))];

    // ---- Acciones ----

    public Task<ModuleResponse> RunAction(ModuleCall call) => call.Request.Input["action"].Text() switch
    {
        "start" => PowerAction(call, "start"),
        "stop" => PowerAction(call, "stop"),
        "reboot" => PowerAction(call, "reboot"),
        "reinstall" => Reinstall(call),
        "revoke_console" => RevokeConsole(call),
        _ => throw Errors.Input("action", "unknown"),
    };

    /// <summary>Start no hace nada si ya está prendida y stop si ya está apagada. El nodo sale siempre del cluster.</summary>
    private async Task<ModuleResponse> PowerAction(ModuleCall call, string action)
    {
        using var op = Open(call, options.ShortWait);
        RequireActive(op);
        var (vm, config) = await Owned(op).ConfigureAwait(false);
        NotLocked(vm, config);
        var running = await Running(op, vm).ConfigureAwait(false);
        if (action == "reboot")
        {
            // Una VM apagada no cambia sola: es definitivo. Un conflict lo reintentaría el núcleo, y si el cliente la
            // prende mientras tanto la reiniciaría sin que nadie lo pida.
            if (!running)
            {
                throw Errors.Rejected("vm_stopped", "La VM está apagada: no hay nada que reiniciar.");
            }

            // Nada en Proxmox dice que un reinicio ya se hizo. Si la tarea sigue al vencer el plazo, la operación sigue por
            // consulta con la tarea en el operation_id, y la SDK guarda ese pending para la clave: una repetición no
            // reinicia otra vez.
            var task = await Launch(op, vm, "reboot").ConfigureAwait(false);
            if (await op.Pve.Finish(task, op.Deadline, "reboot").ConfigureAwait(false) is null)
            {
                var hex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(task.Text() ?? string.Empty));
                return ModuleResponse.Pending($"reboot-{vm.Vmid.ToString(CultureInfo.InvariantCulture)}-{hex}", 15);
            }
        }
        else if ((action == "start" ? (running ? null : "start") : (running ? "stop" : null)) is { } verb
            && !await Power(op, vm, verb).ConfigureAwait(false))
        {
            throw Errors.Unknown("La tarea sigue en curso en Proxmox.");
        }

        return ModuleResponse.Completed(new JsonObject
        {
            ["power"] = action == "stop" ? "stopped" : "running",
            ["external_ids"] = new JsonObject { ["node"] = vm.Node },
        });
    }

    private async Task<ModuleResponse> RevokeConsole(ModuleCall call)
    {
        using var op = Open(call, options.ShortWait);
        await ConsoleGateway.Revoke(gateway, op.Config, op.Svc.ServiceId, time, logger).ConfigureAwait(false);
        return ModuleResponse.Completed(new JsonObject { ["revoked"] = op.Config.ConsoleOrigin is not null });
    }

    /// <summary>
    /// Borra la VM y la vuelve a clonar con el mismo VMID, la misma MAC y la misma IP, que salen de external_ids y no
    /// del pool. Una reinstalación nueva exige 30 minutos desde la anterior. Si se corta, la repetición con la misma
    /// clave encuentra la marca nueva en la VM y sigue.
    /// </summary>
    private async Task<ModuleResponse> Reinstall(ModuleCall call)
    {
        using var op = Open(call, options.LongWait);
        RequireActive(op);
        var password = Password(call);
        var p = Params(op);
        var vmid = op.Svc.Vmid ?? throw Errors.Busy("create_incomplete", "El servicio no tiene VMID: el alta no terminó.");
        var strict = StrictNet(op);
        var marker = Marker(op, call.IdempotencyKey!);
        var pve = op.Pve;
        var resources = await pve.Resources().ConfigureAwait(false);
        var vm = resources.FirstOrDefault(r => r.Vmid == vmid);
        if (vm is null)
        {
            // Ya se borró en un intento anterior, o la borraron a mano: con permisos completos se crea de nuevo.
            await pve.RequireAudit().ConfigureAwait(false);
        }
        else
        {
            var config = await pve.Config(vm).ConfigureAwait(false);
            // Un clon en curso (lock clone, con la configuración temporal o ya con la marca) lo sigue Drive, que verifica la
            // marca cuando termina.
            var mine = VmConfig.Description(config) == marker || config["lock"].Text() == "clone";
            if (!mine)
            {
                await Teardown(op, p, vm, config, strict, resources).ConfigureAwait(false);
            }
        }

        return await Drive(op, new Plan("reinstall", vmid, marker, password, strict), p).ConfigureAwait(false);
    }

    /// <summary>La parte de la reinstalación que borra: todo se verifica antes, porque después no hay vuelta atrás.</summary>
    private async Task Teardown(Op op, ProductParams p, VmRef vm, JsonObject config, NetEntry strict, List<VmRef> resources)
    {
        var pve = op.Pve;
        if (!IsOwner(op.Svc, vm, config))
        {
            throw Errors.Review("ownership", $"La VM {vm.Vmid} no tiene la marca ni el nombre de este servicio.");
        }

        NotLocked(vm, config);
        if (op.Svc.Id("last_reinstall_at") is { } last && long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var lastAt))
        {
            var remaining = lastAt + ReinstallWait - time.GetUtcNow().ToUnixTimeSeconds();
            if (remaining > 0)
            {
                throw Errors.Busy("reinstall_wait", $"Faltan {(remaining + 59) / 60} minutos para poder reinstalar.", (int)remaining);
            }
        }

        if (VmConfig.MacOf(config["net0"].Text()) != strict.Mac)
        {
            throw Errors.Review("network_mismatch", "La MAC de la VM no coincide con external_ids. Hay que refrescarlos con get_status antes de reinstalar.");
        }

        await CheckTemplate(op, p, resources).ConfigureAwait(false);
        await CheckPlacement(op, p, [strict.Bridge]).ConfigureAwait(false);
        if ((await pve.Macs(resources, vm.Vmid, ReserveVmid(op)).ConfigureAwait(false)).Any(h => h.Mac == strict.Mac))
        {
            throw Errors.Review("mac_in_use", "Otra VM del cluster tiene la MAC de este servicio.");
        }

        await ConsoleGateway.Revoke(gateway, op.Config, op.Svc.ServiceId, time, logger).ConfigureAwait(false);
        if (await Running(op, vm).ConfigureAwait(false) && !await Power(op, vm, "stop").ConfigureAwait(false))
        {
            throw Errors.Busy("in_progress", "La VM se sigue apagando para reinstalar.");
        }

        await Reserve(op, strict, vm.Node, resources).ConfigureAwait(false);
        var task = await pve.ActiveTask(vm).ConfigureAwait(false) is { Type: "qmdestroy" } active
            ? active.Upid
            : await pve.Delete(Pve.VmPath(vm.Node, vm.Vmid)).ConfigureAwait(false);
        if (await Finished(op, vm, task, "borrar la VM para reinstalar").ConfigureAwait(false) is not { } exit)
        {
            throw Errors.Busy("in_progress", "La VM vieja se sigue borrando.");
        }

        if (exit != "OK")
        {
            logger.LogWarning("El borrado de la VM {Vmid} para reinstalar terminó con avisos ({Exit}). Revisar en el storage: {Volumes}", vm.Vmid, pve.Scrub(exit), string.Join(", ", Volumes(config)));
        }
    }

    // ---- Estado, vista y consola ----

    /// <summary>
    /// onboot sigue al estado del servicio y no a si la VM está prendida: onboot 1 es activo aunque el cliente la haya
    /// apagado, onboot 0 y apagada es suspendido, y onboot 0 prendida es un desvío para el staff.
    /// </summary>
    public async Task<ModuleResponse> GetStatus(ModuleCall call)
    {
        using var op = Open(call, options.ShortWait);
        var pve = op.Pve;
        await pve.RequireAudit().ConfigureAwait(false);
        var resources = await pve.Resources().ConfigureAwait(false);
        var vm = op.Svc.Vmid is { } vmid ? resources.FirstOrDefault(r => r.Vmid == vmid) : null;
        if (vm is null)
        {
            return ModuleResponse.Completed(new JsonObject
            {
                ["observed_state"] = "not_found",
                ["details"] = new JsonObject { ["reason"] = op.Svc.Vmid is null ? "no_vmid" : "vm_missing" },
            });
        }

        var config = await pve.Config(vm).ConfigureAwait(false);
        if (!IsOwner(op.Svc, vm, config))
        {
            return ModuleResponse.Completed(new JsonObject
            {
                ["observed_state"] = "unknown",
                ["details"] = new JsonObject { ["reason"] = "ownership", ["node"] = vm.Node },
            });
        }

        var onboot = config["onboot"].Text() == "1";
        var net0 = config["net0"].Text();
        var drift = new List<string>();
        var ids = new JsonObject
        {
            ["vmid"] = vm.Vmid.ToString(CultureInfo.InvariantCulture),
            ["node"] = vm.Node,
            ["name"] = config["name"].Text() ?? string.Empty,
            ["mac"] = VmConfig.MacOf(net0) ?? string.Empty,
            ["bridge"] = VmConfig.NetParam(net0, "bridge") ?? string.Empty,
            ["mtu"] = VmConfig.NetParam(net0, "mtu") ?? string.Empty,
        };

        if (VmConfig.Description(config) is var description && description.StartsWith(ServicePrefix(op.Svc), StringComparison.Ordinal))
        {
            // La marca que tiene la VM, por ejemplo la de una reinstalación que falló después de clonar y nunca llegó al
            // núcleo: así external_ids vuelve a coincidir.
            ids["marker"] = description;
        }

        var fw = $"{Pve.VmPath(vm.Node, vm.Vmid)}/firewall";
        var firewall = await pve.Get($"{fw}/options").ConfigureAwait(false);
        var sets = await pve.Get($"{fw}/ipset").ConfigureAwait(false) as JsonArray ?? [];
        var entries = sets.Any(s => s.Field("name").Text() == IpSet)
            ? await pve.Get($"{fw}/ipset/{IpSet}").ConfigureAwait(false) as JsonArray ?? []
            : [];
        if (entries.Count == 1 && entries[0].Field("cidr").Text() is { } cidr && !entries[0].Field("nomatch").Bool())
        {
            ids["private_ip"] = cidr.Split('/')[0];
        }

        if (!firewall.Field("enable").Bool())
        {
            drift.Add("firewall_off");
        }

        if (firewall.Field("macfilter").Text() == "0")
        {
            drift.Add("macfilter_off");
        }

        if (firewall.Field("ipfilter").Bool() != (entries.Count == 1))
        {
            drift.Add("ipfilter_mismatch");
        }

        // Sin firewall=1 en net0 Proxmox no aplica a la NIC ni macfilter ni ipfilter ni el grupo de aislamiento.
        if (VmConfig.NetParam(net0, "firewall") != "1")
        {
            drift.Add("nic_firewall_off");
        }

        // Una IP privada conocida (external_ids o el pool para esa MAC) que el ipset no tiene, o con ipfilter apagado.
        var mac = VmConfig.MacOf(net0);
        if ((op.Svc.Id("private_ip") ?? op.Config.Pool.FirstOrDefault(e => e.Mac == mac)?.PrivateIp) is { } known
            && (!firewall.Field("ipfilter").Bool() || !entries.Any(e => e.Field("cidr").Text()?.Split('/')[0] == known && !e.Field("nomatch").Bool())))
        {
            drift.Add("ipfilter_missing");
        }

        if (op.Svc.Params.Field("isolation_group_prefix").Text() is { Length: > 0 } prefix)
        {
            var rules = await pve.Get($"{fw}/rules").ConfigureAwait(false) as JsonArray ?? [];
            var group = prefix + ids["bridge"].Text();
            if (!rules.Any(r => r.Field("type").Text() == "group" && r.Field("action").Text() == group && r.Field("enable").Bool()))
            {
                drift.Add("isolation_missing");
            }
        }

        if ((op.Svc.Status == "active" && !onboot) || (op.Svc.Status == "suspended" && onboot))
        {
            drift.Add("onboot_mismatch");
        }

        return ModuleResponse.Completed(new JsonObject
        {
            ["observed_state"] = onboot ? "active" : vm.Running ? "unknown" : "suspended",
            ["details"] = new JsonObject
            {
                ["power"] = vm.Status,
                ["onboot"] = onboot,
                ["node"] = vm.Node,
                ["marker"] = op.Svc.Id("marker") is not null,
                ["drift"] = Strings(drift),
            },
            ["external_ids"] = ids,
        });
    }

    /// <summary>Bloques de texto para el portal. Si Proxmox no responde, un aviso sin detalles y la página carga igual.</summary>
    public async Task<ModuleResponse> View(ModuleCall call)
    {
        using var op = Open(call, options.ShortWait);
        var es = op.Svc.Spanish;
        string T(string spanish, string english) => es ? spanish : english;
        var staff = call.Request.Input["audience"].Text() == "staff";
        var blocks = new JsonArray();
        if (op.Svc.Vmid is not { } vmid)
        {
            blocks.Add(Block("notice", "info", T("El VPS se está creando.", "The VPS is being created.")));
            return ModuleResponse.Completed(new JsonObject { ["blocks"] = blocks });
        }

        try
        {
            var resources = await op.Pve.Resources().ConfigureAwait(false);
            var vm = resources.FirstOrDefault(r => r.Vmid == vmid) ?? throw Errors.Unavailable("La VM no está en el cluster.");
            var config = await op.Pve.Config(vm).ConfigureAwait(false);
            if (!IsOwner(op.Svc, vm, config))
            {
                throw Errors.Unavailable("La VM no es del servicio.");
            }

            var status = await op.Pve.Get($"{Pve.VmPath(vm.Node, vm.Vmid)}/status/current").ConfigureAwait(false);
            var running = status.Field("status").Text() == "running";
            var templateId = op.Svc.Params.Field("template_id").Int();
            var system = resources.FirstOrDefault(r => r.Vmid == templateId)?.Name ?? string.Empty;
            var cpu = Math.Round((status.Field("cpu").Dec() ?? 0) * 100, 1).ToString(CultureInfo.InvariantCulture);
            var used = ((long)((status.Field("mem").Dec() ?? 0) / 1048576)).ToString(CultureInfo.InvariantCulture);
            var total = ((long)((status.Field("maxmem").Dec() ?? 0) / 1048576)).ToString(CultureInfo.InvariantCulture);

            blocks.Add(Block("status", running ? "ok" : "warning", running ? T("Encendido", "Running") : T("Apagado", "Stopped")));
            var fields = new JsonArray
            {
                Field(T("IP pública", "Public IP"), op.Svc.Id("public_ip") ?? string.Empty),
                Field(T("Usuario", "User"), op.Svc.Id("username") ?? string.Empty),
                Field(T("Sistema operativo", "Operating system"), system),
                Field("CPU", $"{cpu} %"),
                Field("RAM", $"{used} / {total} MB"),
            };
            if (staff)
            {
                fields.Add(Field("VMID", vm.Vmid.ToString(CultureInfo.InvariantCulture)));
                fields.Add(Field(T("Nodo", "Node"), vm.Node));
                fields.Add(Field(T("Marca", "Marker"), op.Svc.Id("marker") is null ? T("ausente", "absent") : T("presente", "present")));
            }

            blocks.Add(new JsonObject { ["type"] = "fields", ["items"] = fields });

            // Solo acciones del manifiesto y solo con el servicio activo, como exigen run_action y la consola. La consola
            // no es una acción: es la operación sso, y el núcleo la ofrece por la capacidad.
            if (op.Svc.Status == "active")
            {
                blocks.Add(new JsonObject { ["type"] = "actions", ["items"] = Strings(["start", "stop", "reboot", "reinstall"]) });
            }
        }
        catch (ModuleException ex)
        {
            logger.LogWarning("Vista del servicio {Service} sin datos de Proxmox: {Code}", op.Svc.ServiceId, ex.Code);
            blocks.Add(Block("notice", "error", T("No pudimos consultar el estado del VPS. Probá de nuevo en unos minutos.", "We couldn't check the VPS status. Please try again in a few minutes.")));
        }

        blocks.Add(Block("notice", "info", T("Para cambiar el plan, abrí un ticket desde Soporte.", "To change your plan, open a ticket from Support.")));
        return ModuleResponse.Completed(new JsonObject { ["blocks"] = blocks });
    }

    /// <summary>URL de la consola noVNC con un grant de 60 segundos en el fragmento. Solo con la VM prendida.</summary>
    public async Task<ModuleResponse> Sso(ModuleCall call)
    {
        using var op = Open(call, options.ShortWait);
        if (!op.Svc.Params.Field("console_enabled").Bool())
        {
            throw Errors.Rejected("console_disabled", "La consola está desactivada en el producto.");
        }

        RequireActive(op);
        if (op.Config.ConsoleOrigin is not { } origin)
        {
            throw Errors.Config("console_not_configured", "La instancia no tiene gateway de consola.", ["console_origin"]);
        }

        var (vm, _) = await Owned(op).ConfigureAwait(false);
        if (!await Running(op, vm).ConfigureAwait(false))
        {
            throw Errors.Busy("vm_stopped", "La VM está apagada. Hay que prenderla antes de abrir la consola.", 30);
        }

        var iat = time.GetUtcNow().ToUnixTimeSeconds();
        var grant = ConsoleGateway.Grant(op.Config.ConsoleSecret!, op.Config.ConsoleServerKey, op.Svc.ServiceId, vm.Node, vm.Vmid, iat);
        return ModuleResponse.Completed(new JsonObject
        {
            ["url"] = $"{origin.GetLeftPart(UriPartial.Authority)}/#{grant}",
            ["expires_at"] = DateTimeOffset.FromUnixTimeSeconds(iat + ConsoleGateway.Lifetime).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        });
    }

    // ---- El alta paso a paso ----

    /// <summary>Lo que hay que dejar armado: el VMID, la marca, la contraseña si la hay y la red estricta de una reinstalación.</summary>
    private sealed record Plan(string Operation, int Vmid, string Marker, string? Password, NetEntry? Strict)
    {
        public ModuleResponse Later() => ModuleResponse.Pending($"{Operation}-{Vmid.ToString(CultureInfo.InvariantCulture)}-{Marker[(Marker.LastIndexOf(':') + 1)..]}", 15);
    }

    /// <summary>
    /// Lleva la VM al estado pedido desde lo que encuentra en Proxmox: clonar, esperar el clon, configurar, reclamar
    /// la MAC, firewall, arrancar y onboot. Cada paso mira antes si ya está hecho. Antes de cargar la contraseña, una
    /// espera vence como conflict para que el núcleo repita la llamada con la contraseña. Después vence como pending.
    /// </summary>
    private async Task<ModuleResponse> Drive(Op op, Plan plan, ProductParams p)
    {
        var pve = op.Pve;
        while (true)
        {
            var resources = await pve.Resources().ConfigureAwait(false);
            var vm = resources.FirstOrDefault(r => r.Vmid == plan.Vmid);
            if (vm is null)
            {
                if (plan.Password is null)
                {
                    throw Errors.Review("vm_missing", $"La VM {plan.Vmid} desapareció en medio de la operación.");
                }

                await Clone(op, plan, p, resources).ConfigureAwait(false);
                continue;
            }

            var config = await pve.Config(vm).ConfigureAwait(false);
            var description = VmConfig.Description(config);
            if (vm.Type == "qemu" && config["lock"].Text() == "clone")
            {
                // El clon todavía corre. Proxmox escribe primero una configuración temporal sin nombre ni marca ("qmclone
                // temporary file") y después de cada disco la configuración nueva con la marca, siempre con lock clone. La
                // marca se verifica cuando termina.
                if (time.GetUtcNow() >= op.Deadline)
                {
                    throw Errors.Busy("in_progress", "El clon sigue en curso en Proxmox.", 60);
                }

                await pve.Pause().ConfigureAwait(false);
                continue;
            }

            if (vm.Type != "qemu" || description != plan.Marker)
            {
                throw Errors.Review("marker_mismatch", $"La VM {vm.Vmid} no tiene la marca de esta operación. No se adopta.");
            }

            if (config["ciuser"].Text() != ClientUser(op) && config["onboot"].Text() != "0")
            {
                // El clon copia onboot de la plantilla. Sin configurar (sin firewall y con una MAC al azar) no tiene que
                // arrancar sola con el nodo si el alta no sigue: no hace falta la contraseña para apagarlo.
                await pve.Put($"{Pve.VmPath(vm.Node, vm.Vmid)}/config", ("onboot", "0")).ConfigureAwait(false);
                config = await pve.Config(vm).ConfigureAwait(false);
            }

            return await Configure(op, plan, p, vm, config).ConfigureAwait(false);
        }
    }

    private async Task Clone(Op op, Plan plan, ProductParams p, List<VmRef> resources)
    {
        await CheckTemplate(op, p, resources).ConfigureAwait(false);
        NetEntry entry;
        if (plan.Strict is { } strict)
        {
            // La reserva del propio servicio no es un rival: es la MAC que esta reinstalación viene a reclamar.
            var holders = await op.Pve.Macs(resources, plan.Vmid, ReserveVmid(op)).ConfigureAwait(false);
            if (holders.Any(h => h.Mac == strict.Mac && h.Committed))
            {
                throw Errors.Review("mac_in_use", "Otra VM prendida tiene la MAC de este servicio.");
            }

            entry = strict;
        }
        else
        {
            entry = FirstFree(op, await op.Pve.Macs(resources, plan.Vmid).ConfigureAwait(false));
        }

        await CheckPlacement(op, p, [entry.Bridge]).ConfigureAwait(false);
        var form = new List<(string, string)>
        {
            ("newid", plan.Vmid.ToString(CultureInfo.InvariantCulture)),
            ("name", VmName(op.Svc.ServiceId)),
            ("full", "1"),
            ("target", p.Node),
            ("description", plan.Marker),
        };
        if (p.ConsolePool is { } pool)
        {
            form.Add(("pool", pool));
        }

        var task = await op.Pve.Post($"{Pve.VmPath(p.Node, p.TemplateId)}/clone", [.. form]).ConfigureAwait(false);
        if (!await op.Pve.Wait(task, op.Deadline, "clonar la plantilla").ConfigureAwait(false))
        {
            throw Errors.Busy("in_progress", "El clon sigue en curso en Proxmox.", 60);
        }
    }

    private async Task<ModuleResponse> Configure(Op op, Plan plan, ProductParams p, VmRef vm, JsonObject config)
    {
        var pve = op.Pve;
        var path = Pve.VmPath(vm.Node, vm.Vmid);
        var user = ClientUser(op);
        if (config["ciuser"].Text() != user)
        {
            if (plan.Password is null)
            {
                throw Errors.Review("guest_config_lost", $"La VM {vm.Vmid} no tiene la configuración del invitado y la consulta no trae la contraseña.");
            }

            if (await Running(op, vm).ConfigureAwait(false) && !await Power(op, vm, "stop").ConfigureAwait(false))
            {
                throw Errors.Busy("in_progress", "La VM se sigue apagando.");
            }

            if (!await Resize(op, vm, p.DiskGb).ConfigureAwait(false))
            {
                throw Errors.Busy("in_progress", "El disco se sigue agrandando.");
            }

            var resources = await pve.Resources().ConfigureAwait(false);
            var entry = plan.Strict ?? FirstFree(op, await pve.Macs(resources, vm.Vmid).ConfigureAwait(false));

            // onboot va en 0 hasta el final: hay plantillas que lo traen en 1, y una VM a medio armar no tiene que
            // arrancar sola con el nodo y sin firewall.
            await pve.Put(
                $"{path}/config",
                ("cores", p.Cores.ToString(CultureInfo.InvariantCulture)),
                ("memory", p.MemoryMb.ToString(CultureInfo.InvariantCulture)),
                ("ciuser", user),
                ("cipassword", plan.Password),
                ("agent", "1"),
                ("onboot", "0"),
                ("net0", Net(entry, p))).ConfigureAwait(false);
            config = await pve.Config(vm).ConfigureAwait(false);
        }

        // Desde acá no hace falta la contraseña: si se vence el plazo, la operación sigue por consulta.
        NetEntry net;
        if (await Running(op, vm).ConfigureAwait(false))
        {
            // Solo se arranca después de ganar la MAC y armar el firewall: si ya corre, eso ya pasó.
            net = EntryOf(op, plan, config) ?? throw Errors.Review("network_unknown", $"La VM {vm.Vmid} corre con una MAC que no es la del servicio.");
        }
        else
        {
            if (await Claim(op, plan, p, vm, config).ConfigureAwait(false) is not { } claimed)
            {
                return plan.Later();
            }

            net = claimed;
            if (plan.Strict is not null)
            {
                // La VM nueva ya tiene la MAC en net0, que todas las réplicas ven: la reserva ya no hace falta.
                await Release(op, await pve.Resources().ConfigureAwait(false)).ConfigureAwait(false);
            }

            if (!await Resize(op, vm, p.DiskGb).ConfigureAwait(false))
            {
                return plan.Later();
            }

            await Firewall(op, vm, net, p).ConfigureAwait(false);
            if (!await Power(op, vm, "start").ConfigureAwait(false))
            {
                return plan.Later();
            }
        }

        await pve.Put($"{path}/config", ("onboot", "1")).ConfigureAwait(false);
        var windows = (config["ostype"].Text() ?? string.Empty) is var os && (os.StartsWith("win", StringComparison.Ordinal) || os.StartsWith("w2k", StringComparison.Ordinal));
        var ids = new JsonObject
        {
            ["vmid"] = vm.Vmid.ToString(CultureInfo.InvariantCulture),
            ["node"] = vm.Node,
            ["name"] = VmName(op.Svc.ServiceId),
            ["marker"] = plan.Marker,
            ["mac"] = net.Mac,
            ["public_ip"] = net.PublicIp,
            ["private_ip"] = net.PrivateIp ?? string.Empty,
            ["bridge"] = net.Bridge,
            ["mtu"] = net.Mtu?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,

            // En Windows el usuario es el administrador de la plantilla, como dice el correo de bienvenida.
            ["username"] = windows ? "Administrador" : user,
        };
        if (plan.Operation == "reinstall")
        {
            ids["last_reinstall_at"] = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }

        return ModuleResponse.Completed(new JsonObject
        {
            ["observed_state"] = "active",
            ["label"] = $"Host_{net.PublicIp.Replace('.', '-').Replace(':', '-')}{p.HostnameSuffix}",
            ["external_ids"] = ids,
        });
    }

    /// <summary>
    /// Reclama la MAC sin un lock compartido: el reclamo es la MAC escrita en net0, que todas las réplicas ven en
    /// Proxmox. Se escribe primero y se revisa el cluster después, y solo se sigue si nadie más la tiene. Entre dos que
    /// la escribieron, al menos uno ve al otro. Cede el que ve a un rival prendido o con onboot, o con VMID menor. Si
    /// solo ve rivales con VMID mayor que todavía no la ganaron, espera a que cedan. Devuelve null si venció el plazo.
    /// </summary>
    private async Task<NetEntry?> Claim(Op op, Plan plan, ProductParams p, VmRef vm, JsonObject config)
    {
        var pve = op.Pve;
        var path = Pve.VmPath(vm.Node, vm.Vmid);
        while (true)
        {
            var resources = await pve.Resources().ConfigureAwait(false);

            // La reserva del propio servicio no es un rival: es la MAC que esta reinstalación viene a reclamar.
            var holders = plan.Strict is null
                ? await pve.Macs(resources, vm.Vmid).ConfigureAwait(false)
                : await pve.Macs(resources, vm.Vmid, ReserveVmid(op)).ConfigureAwait(false);
            if (EntryOf(op, plan, config) is not { } entry)
            {
                // net0 sin una MAC válida para el servicio: cedió la anterior o es un clon recién configurado de otra forma.
                entry = plan.Strict ?? FirstFree(op, holders);
                await pve.Put($"{path}/config", ("net0", Net(entry, p))).ConfigureAwait(false);
                config = await pve.Config(vm).ConfigureAwait(false);
                continue;
            }

            var rivals = holders.Where(h => h.Mac == entry.Mac).ToList();
            if (rivals.Count == 0)
            {
                return entry;
            }

            if (rivals.Any(r => r.Committed || r.Vmid < vm.Vmid))
            {
                if (plan.Strict is not null && rivals.Any(r => r.Committed))
                {
                    throw Errors.Review("mac_in_use", "Otra VM prendida o con onboot tiene la MAC de este servicio.");
                }

                // Cede: suelta la MAC para que el rival siga. Un alta toma otra entrada en la vuelta siguiente.
                await pve.Put($"{path}/config", ("delete", "net0")).ConfigureAwait(false);
                if (plan.Strict is not null)
                {
                    throw Errors.Busy("mac_contended", "Otra alta reclama la MAC de este servicio. Se reintenta.", 10);
                }

                config = await pve.Config(vm).ConfigureAwait(false);
                continue;
            }

            if (time.GetUtcNow() >= op.Deadline)
            {
                return null;
            }

            await pve.Pause().ConfigureAwait(false);
        }
    }

    /// <summary>La entrada de red que tiene la VM en net0, si es la del servicio: la estricta o una del pool.</summary>
    private static NetEntry? EntryOf(Op op, Plan plan, JsonObject config)
    {
        var mac = VmConfig.MacOf(config["net0"].Text());
        return mac is null ? null
            : plan.Strict is { } strict ? (strict.Mac == mac ? strict : null)
            : op.Config.Pool.FirstOrDefault(e => e.Mac == mac);
    }

    private static NetEntry FirstFree(Op op, IEnumerable<MacHolder> holders)
    {
        var used = holders.Select(h => h.Mac).ToHashSet(StringComparer.Ordinal);
        return op.Config.Pool.FirstOrDefault(e => !used.Contains(e.Mac))
            ?? throw Errors.Config("pool_exhausted", "No quedan entradas libres en el pool de red de la instancia.", ["network_pool"]);
    }

    private static string Net(NetEntry entry, ProductParams p) =>
        $"virtio={entry.Mac.ToUpperInvariant()},bridge={entry.Bridge},firewall=1"
        + (entry.Mtu is { } mtu ? $",mtu={mtu.ToString(CultureInfo.InvariantCulture)}" : string.Empty)
        + $",rate={p.Rate}";

    /// <summary>scsi0 al tamaño exacto, sin achicar nunca y verificándolo. Devuelve false si la tarea sigue al vencer el plazo.</summary>
    private static async Task<bool> Resize(Op op, VmRef vm, int targetGb)
    {
        var pve = op.Pve;
        var current = VmConfig.DiskGb(await pve.Config(vm).ConfigureAwait(false))
            ?? throw Errors.Review("disk_unreadable", $"No se pudo leer el tamaño de scsi0 de la VM {vm.Vmid}.");
        if (current > targetGb)
        {
            throw Errors.Input("disk_gb", "shrink_unsupported", (int)Math.Ceiling(current));
        }

        if (current == targetGb)
        {
            return true;
        }

        var task = await pve.Put($"{Pve.VmPath(vm.Node, vm.Vmid)}/resize", ("disk", "scsi0"), ("size", $"{targetGb.ToString(CultureInfo.InvariantCulture)}G")).ConfigureAwait(false);
        if (!await pve.WaitIfTask(task, op.Deadline, "agrandar el disco").ConfigureAwait(false))
        {
            return false;
        }

        return VmConfig.DiskGb(await pve.Config(vm).ConfigureAwait(false)) == targetGb
            ? true
            : throw Errors.Review("disk_mismatch", $"El disco de la VM {vm.Vmid} no quedó del tamaño contratado.");
    }

    /// <summary>
    /// Firewall con anti-spoofing: el ipset ipfilter-net0 con la IP privada se arma y se verifica antes de prender
    /// ipfilter, porque con ipfilter y el ipset vacío Proxmox descarta todo lo que sale de la VM. Después las opciones y
    /// el grupo de aislamiento. Si algo falla, la VM no arranca.
    /// </summary>
    private static async Task Firewall(Op op, VmRef vm, NetEntry net, ProductParams p)
    {
        var pve = op.Pve;
        var fw = $"{Pve.VmPath(vm.Node, vm.Vmid)}/firewall";
        var ipfilter = "0";
        if (net.PrivateIp is { } ip)
        {
            bool Same(JsonNode? e) => e.Field("cidr").Text() is { } cidr && !e.Field("nomatch").Bool()
                && (cidr == ip || cidr == ip + (ip.Contains(':', StringComparison.Ordinal) ? "/128" : "/32"));

            var sets = await pve.Get($"{fw}/ipset").ConfigureAwait(false) as JsonArray ?? throw Errors.Unavailable("Proxmox devolvió una lista de ipsets inválida.");
            if (!sets.Any(s => s.Field("name").Text() == IpSet))
            {
                await pve.Post($"{fw}/ipset", ("name", IpSet)).ConfigureAwait(false);
            }

            var set = $"{fw}/ipset/{IpSet}";
            var entries = await pve.Get(set).ConfigureAwait(false) as JsonArray ?? throw Errors.Unavailable("Proxmox devolvió un ipset inválido.");
            if (entries.Any(e => !Same(e)))
            {
                throw Errors.Review("ipfilter_unexpected", $"El ipset {IpSet} de la VM {vm.Vmid} tiene entradas que no son del servicio.");
            }

            if (entries.Count == 0)
            {
                await pve.Post(set, ("cidr", ip)).ConfigureAwait(false);
            }

            entries = await pve.Get(set).ConfigureAwait(false) as JsonArray ?? [];
            if (entries.Count != 1 || !Same(entries[0]))
            {
                throw Errors.Review("ipfilter_unverified", $"No se pudo verificar el ipset {IpSet} de la VM {vm.Vmid}.");
            }

            ipfilter = "1";
        }

        await pve.Put(
            $"{fw}/options",
            ("enable", "1"),
            ("policy_in", "ACCEPT"),
            ("policy_out", "ACCEPT"),
            ("macfilter", "1"),
            ("ipfilter", ipfilter),
            ("ndp", "1"),
            ("radv", "0")).ConfigureAwait(false);

        if (p.IsolationGroupPrefix.Length > 0)
        {
            var group = p.IsolationGroupPrefix + net.Bridge;
            var rules = await pve.Get($"{fw}/rules").ConfigureAwait(false) as JsonArray ?? throw Errors.Unavailable("Proxmox devolvió reglas inválidas.");
            var rule = rules.FirstOrDefault(r => r.Field("type").Text() == "group" && r.Field("action").Text() == group);
            if (rule is null)
            {
                await pve.Post($"{fw}/rules", ("type", "group"), ("action", group), ("enable", "1")).ConfigureAwait(false);
            }
            else if (!rule.Field("enable").Bool())
            {
                var pos = rule.Field("pos").Int() ?? throw Errors.Unavailable("Proxmox devolvió una regla sin posición.");
                await pve.Put($"{fw}/rules/{pos.ToString(CultureInfo.InvariantCulture)}", ("enable", "1")).ConfigureAwait(false);
            }
        }
    }

    // ---- Comprobaciones ----

    /// <summary>La plantilla existe en el nodo, es plantilla y su disco no es más grande que el contratado.</summary>
    private static async Task CheckTemplate(Op op, ProductParams p, List<VmRef> resources)
    {
        var template = resources.FirstOrDefault(r => r.Vmid == p.TemplateId && r.Type == "qemu");
        if (template is null || template.Node != p.Node)
        {
            throw Errors.Input("template_id", "not_found");
        }

        // template y el disco salen de la configuración: el inventario los completa con lo que publica pvestatd, que en un
        // nodo trabado no llega y deja template en 0.
        var config = await op.Pve.Config(template).ConfigureAwait(false);
        if (config["template"].Text() is not ("1" or "true"))
        {
            throw Errors.Input("template_id", "not_template");
        }

        var size = VmConfig.DiskGb(config) ?? throw Errors.Input("template_id", "disk_unreadable");
        if (p.DiskGb < size)
        {
            throw Errors.Input("disk_gb", "template_disk", (int)Math.Ceiling(size));
        }
    }

    /// <summary>Que existan en el nodo los bridges, sus grupos de aislamiento y el pool de consola.</summary>
    private static async Task CheckPlacement(Op op, ProductParams p, IEnumerable<string> bridges)
    {
        var pve = op.Pve;
        if (p.ConsolePool is { } pool)
        {
            var pools = await pve.Get("/pools").ConfigureAwait(false) as JsonArray ?? [];
            if (!pools.Any(x => x.Field("poolid").Text() == pool))
            {
                throw Errors.Config("console_pool_missing", "El pool de consola del producto no existe en Proxmox.");
            }
        }

        var interfaces = await pve.Get($"/nodes/{p.Node}/network").ConfigureAwait(false) as JsonArray ?? [];
        var present = interfaces
            .Where(i => i.Field("type").Text() is "bridge" or "OVSBridge")
            .Select(i => i.Field("iface").Text())
            .ToHashSet(StringComparer.Ordinal);
        var needed = bridges.ToList();
        if (needed.FirstOrDefault(b => !present.Contains(b)) is { } missing)
        {
            throw Errors.Config("bridge_missing", $"El bridge {missing} no existe en el nodo {p.Node}.", ["network_pool"]);
        }

        if (p.IsolationGroupPrefix.Length > 0)
        {
            var groups = (await pve.Get("/cluster/firewall/groups").ConfigureAwait(false) as JsonArray ?? [])
                .Select(g => g.Field("group").Text())
                .ToHashSet(StringComparer.Ordinal);
            if (needed.FirstOrDefault(b => !groups.Contains(p.IsolationGroupPrefix + b)) is { } bridge)
            {
                throw Errors.Config("isolation_group_missing", $"No existe el grupo de aislamiento {p.IsolationGroupPrefix}{bridge}.");
            }
        }
    }

    /// <summary>
    /// La consulta previa al alta y la elección del VMID. Los dos VMID donde clona el alta (el ID del servicio y el
    /// alternativo) se miran por su configuración, que es el dato real: si uno tiene la marca de esta alta es el de un
    /// intento anterior, y si tiene un clon en curso se espera a que termine. Recién después se elige el primero libre y
    /// fuera de reserved_vmids. Una VM con el nombre del servicio que no tiene la marca frena todo: puede ser otra alta
    /// del mismo servicio o un VPS importado.
    /// </summary>
    private static async Task<int> ChooseVmid(Op op, List<VmRef> resources, string marker)
    {
        var name = VmName(op.Svc.ServiceId);
        var slots = Slots(op.Svc);
        int? free = null;
        foreach (var candidate in slots)
        {
            if (op.Config.IsReserved(candidate))
            {
                continue;
            }

            var vm = resources.FirstOrDefault(r => r.Vmid == candidate);
            if (vm is null)
            {
                free ??= candidate;
                continue;
            }

            var config = await op.Pve.Config(vm).ConfigureAwait(false);
            if (VmConfig.Description(config) == marker)
            {
                return candidate;
            }

            if (config["lock"].Text() == "clone")
            {
                // Con la configuración temporal todavía no se sabe de quién es el clon: se espera a que tenga la marca.
                throw Errors.Busy("in_progress", $"Hay un clon en curso en la VM {candidate}.", 60);
            }

            if (config["name"].Text() == name)
            {
                throw Errors.Review("service_vm_exists", $"Ya hay una VM {name} ({candidate}) sin la marca de esta alta. No se crea otra.");
            }
        }

        // El resto del cluster por el nombre del inventario: los VPS importados con VMID viejos.
        foreach (var vm in resources.Where(r => r.Name == name && !slots.Contains(r.Vmid)))
        {
            if ((await op.Pve.Config(vm).ConfigureAwait(false))["name"].Text() == name)
            {
                throw Errors.Review("service_vm_exists", $"Ya hay una VM {name} ({vm.Vmid}) sin la marca de esta alta. No se crea otra.");
            }
        }

        return free ?? throw Errors.Review("vmid_unavailable", "Ni el ID del servicio ni el VMID alternativo están libres.");
    }

    /// <summary>Los VMID donde puede clonar el alta del servicio: su ID y el alternativo, dentro del rango de Proxmox.</summary>
    private static int[] Slots(ServiceCtx svc) =>
        [.. new[] { svc.ServiceId, FallbackOffset + svc.ServiceId }.Where(v => v is >= Pve.MinVmid and <= Pve.MaxVmid).Select(v => (int)v)];

    /// <summary>
    /// La VM del servicio, verificada: por la marca si external_ids la tiene, y si no por el nombre vm más el ID del
    /// servicio (los importados sin marca).
    /// </summary>
    private static async Task<(VmRef Vm, JsonObject Config)> Owned(Op op)
    {
        var vmid = op.Svc.Vmid ?? throw Errors.Busy("create_incomplete", "El servicio no tiene VMID: el alta no terminó.");
        var vm = (await op.Pve.Resources().ConfigureAwait(false)).FirstOrDefault(r => r.Vmid == vmid);
        if (vm is null)
        {
            await op.Pve.RequireAudit().ConfigureAwait(false);
            throw Errors.Review("vm_missing", $"La VM {vmid} no está en el cluster.");
        }

        var config = await op.Pve.Config(vm).ConfigureAwait(false);
        if (!IsOwner(op.Svc, vm, config))
        {
            throw Errors.Review("ownership", $"La VM {vmid} no tiene la marca ni el nombre de este servicio.");
        }

        return (vm, config);
    }

    /// <summary>
    /// Si la VM es del servicio, con su configuración y no con el inventario. Con marca en external_ids, la marca. También
    /// la de una operación posterior del mismo servicio si la VM tiene su nombre: una reinstalación que falló después de
    /// clonar deja la marca nueva y el núcleo nunca la recibió. Sin marca (los importados), el nombre.
    /// </summary>
    private static bool IsOwner(ServiceCtx svc, VmRef vm, JsonObject config)
    {
        if (vm.Type != "qemu")
        {
            return false;
        }

        var description = VmConfig.Description(config);
        if (svc.Id("marker") is { } marker && description == marker)
        {
            return true;
        }

        return config["name"].Text() == VmName(svc.ServiceId)
            && (svc.Id("marker") is null || description.StartsWith(ServicePrefix(svc), StringComparison.Ordinal));
    }

    /// <summary>
    /// Con un lock en la configuración (un backup, una migración) Proxmox acepta stop, start o el borrado y la tarea
    /// falla adentro: se contesta conflict antes de tocar la VM.
    /// </summary>
    private static void NotLocked(VmRef vm, JsonObject config)
    {
        if (config["lock"].Text() is { Length: > 0 } name)
        {
            throw Errors.Locked($"La VM {vm.Vmid} tiene el lock {name} en Proxmox.");
        }
    }

    private static void RequireActive(Op op)
    {
        if (op.Svc.Status != "active")
        {
            throw Errors.Rejected("service_not_active", "El servicio no está activo.");
        }
    }

    private static async Task<bool> Running(Op op, VmRef vm) =>
        (await op.Pve.Get($"{Pve.VmPath(vm.Node, vm.Vmid)}/status/current").ConfigureAwait(false)).Field("status").Text() == "running";

    /// <summary>status/start, stop o reboot sobre el nodo actual. Stop es el apagado inmediato, como en el módulo anterior.</summary>
    private static async Task<bool> Power(Op op, VmRef vm, string verb) =>
        await op.Pve.Wait(await Launch(op, vm, verb).ConfigureAwait(false), op.Deadline, verb).ConfigureAwait(false);

    /// <summary>
    /// Lanza status/{verb}, o toma la misma tarea de un intento anterior que sigue corriendo: una segunda tarea esperaría
    /// el lock de la VM y fallaría. Con otra tarea de la VM en curso contesta conflict sin lanzar nada.
    /// </summary>
    private static async Task<JsonNode?> Launch(Op op, VmRef vm, string verb)
    {
        if (await op.Pve.ActiveTask(vm).ConfigureAwait(false) is { } active)
        {
            return active.Type == "qm" + verb
                ? active.Upid
                : throw Errors.Busy("task_running", $"La VM {vm.Vmid} tiene otra tarea en curso en Proxmox ({active.Type}).", 30);
        }

        return await op.Pve.Post($"{Pve.VmPath(vm.Node, vm.Vmid)}/status/{verb}").ConfigureAwait(false);
    }

    /// <summary>
    /// Quita la ACL PVEVMUser del usuario client más el ID de cuenta, que dejaron versiones viejas. No crea ni borra
    /// usuarios. Si el token no puede listar usuarios, no ve ninguno y la ACL queda.
    /// </summary>
    private static async Task CleanLegacyAcl(Op op, VmRef vm)
    {
        if (!op.Config.LegacyAclCleanup)
        {
            return;
        }

        var user = $"client{op.Svc.AccountId.ToString(CultureInfo.InvariantCulture)}@pve";
        var users = await op.Pve.Get("/access/users").ConfigureAwait(false) as JsonArray ?? [];
        if (users.Any(u => u.Field("userid").Text() == user))
        {
            await op.Pve.Put(
                "/access/acl",
                ("path", $"/vms/{vm.Vmid.ToString(CultureInfo.InvariantCulture)}"),
                ("users", user),
                ("roles", "PVEVMUser"),
                ("propagate", "0"),
                ("delete", "1")).ConfigureAwait(false);
        }
    }

    // ---- Entradas ----

    /// <summary>
    /// La red de una reinstalación sale de external_ids: hay VPS con IPs fuera del pool que igual tienen que reinstalar.
    /// Lo que falte (bridge, mtu o IP privada, por ejemplo en un importado sin ipset) se completa con la entrada del pool
    /// de la misma MAC y la misma IP pública, así la VM nueva no queda sin el filtro de origen.
    /// </summary>
    private static NetEntry StrictNet(Op op)
    {
        var svc = op.Svc;
        var mac = svc.Id("mac")?.ToLowerInvariant();
        var ip = svc.Id("public_ip");
        var pool = op.Config.Pool.FirstOrDefault(e => e.Mac == mac && e.PublicIp == ip);
        var bridge = svc.Id("bridge") ?? pool?.Bridge;
        var mtuText = svc.Id("mtu") ?? pool?.Mtu?.ToString(CultureInfo.InvariantCulture);
        var privateIp = svc.Id("private_ip") ?? pool?.PrivateIp;
        int? mtu = int.TryParse(mtuText, NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : null;
        if (mac is null || !Rules.Mac().IsMatch(mac) || ip is null || !Rules.IsIp(ip) || bridge is null || !Rules.Bridge().IsMatch(bridge)
            || (mtuText is not null && mtu is not (>= 576 and <= 9000)) || (privateIp is not null && !Rules.IsIp(privateIp)))
        {
            throw Errors.Review("external_ids_incomplete", "Faltan la MAC, la IP o el bridge en external_ids. Hay que refrescarlos con get_status antes de reinstalar.");
        }

        return new NetEntry(mac, ip, bridge, mtu, privateIp);
    }

    private static string Password(ModuleCall call)
    {
        var password = call.Request.Input["credentials"].Field("guest_password").Text();
        return string.IsNullOrEmpty(password) || password.Length > 256 || password.Any(char.IsControl)
            ? throw Errors.Input("credentials.guest_password", "missing")
            : password;
    }

    private static ProductParams Params(Op op)
    {
        var p = ProductParams.Read(op.Svc.Params);
        return p.Violations() is { } violations ? throw Errors.Input(violations) : p;
    }

    /// <summary>
    /// La marca externa que queda en la descripción de la VM: el ID y el uuid del servicio, y un hash de la instancia y de
    /// la clave de idempotencia. La misma alta da siempre la misma marca, así una repetición que cae en otra réplica, o que
    /// llega después de perder el registro de idempotencia, reconoce su VM. Otra generación da otra.
    /// </summary>
    private static string Marker(Op op, string key)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{op.InstanceId}\n{key}")));
        return ServicePrefix(op.Svc) + hash[..48];
    }

    /// <summary>El comienzo de toda marca del servicio: billing-oss, el ID y el uuid si el núcleo lo manda.</summary>
    private static string ServicePrefix(ServiceCtx svc) =>
        $"{MarkerPrefix}:{svc.ServiceId.ToString(CultureInfo.InvariantCulture)}:" + (svc.Uuid is { } uuid ? uuid + ":" : string.Empty);

    private static string VmName(long serviceId) => $"vm{serviceId.ToString(CultureInfo.InvariantCulture)}";

    private static string ClientUser(Op op) => $"client{op.Svc.AccountId.ToString(CultureInfo.InvariantCulture)}";

    private static JsonObject Block(string type, string level, string text) => new() { ["type"] = type, ["level"] = level, ["text"] = text };

    private static JsonObject Field(string label, string value) => new() { ["label"] = label, ["value"] = value };

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    /// <summary>create y reinstall llevan VMID y marca, terminate solo el VMID.</summary>
    [GeneratedRegex("^(?:(create|reinstall)-([0-9]{3,9})-([0-9a-f]{48})|(terminate)-[0-9]{3,9})$")]
    private static partial Regex OperationId();

    /// <summary>reboot lleva el VMID y el UPID de su tarea en hexadecimal.</summary>
    [GeneratedRegex("^reboot-([0-9]{3,9})-((?:[0-9a-f]{2}){20,100})$")]
    private static partial Regex RebootId();

    [GeneratedRegex(@"^(?:scsi|virtio|sata|ide|unused)\d+$|^efidisk0$|^tpmstate0$")]
    private static partial Regex DiskKey();

    // ---- Llamada ----

    private Op Open(ModuleCall call, TimeSpan wait, bool service = true)
    {
        var config = InstanceConfig.Read(call.Request.Instance.Config);
        var svc = service ? ServiceCtx.Read(call.Request.Context) : null;
        var http = new HttpClient(handlers(config.Ca), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        return new Op(config, svc, new Pve(config, http, logger, time, options.PollInterval), http, time.GetUtcNow() + wait, call.Request.Instance.Id);
    }

    /// <summary>Lo de una llamada: configuración, contexto, cliente de Proxmox y plazo. Se descarta al terminar.</summary>
    private sealed class Op(InstanceConfig config, ServiceCtx? svc, Pve pve, HttpClient http, DateTimeOffset deadline, string instanceId) : IDisposable
    {
        public InstanceConfig Config { get; } = config;

        public ServiceCtx Svc => svc ?? throw new InvalidOperationException("La operación no trae servicio.");

        public Pve Pve { get; } = pve;

        public DateTimeOffset Deadline { get; } = deadline;

        public string InstanceId { get; } = instanceId;

        public void Dispose() => http.Dispose();
    }
}
