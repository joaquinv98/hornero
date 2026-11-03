using System.Text.Json.Nodes;

namespace BillingOss.Module.Hestia;

internal enum Presence
{
    /// <summary>El usuario existe y su registro es el pedido.</summary>
    Present,

    /// <summary>Ausencia probada: Hestia dijo que no existe y un inventario autenticado y no vacío no lo tiene.</summary>
    Absent,

    /// <summary>No se pudo probar nada. Ninguna operación cambia algo con este estado.</summary>
    Unreadable,
}

/// <summary>Lo que se leyó de una cuenta de Hestia.</summary>
internal sealed record Snapshot(Presence Presence, string? Problem, JsonObject? User, bool OwnsDomain, IReadOnlyList<string> Mixed, JsonObject Resources)
{
    public bool Suspended => User.Field("SUSPENDED").Text() == "yes";

    /// <summary>Algún recurso tiene un SUSPENDED distinto del usuario.</summary>
    public bool Partial => Mixed.Count > 0;

    public string? Package => User.Field("PACKAGE").Text();

    /// <summary>Sin dominios ni bases, como la deja v-add-user.</summary>
    public bool Empty => new[] { "web", "dns", "mail", "databases" }.All(kind => Resources[kind].Field("count").Text() == "0");

    /// <summary>La cuenta existe, es del servicio, está entera y en el estado pedido.</summary>
    public bool Is(bool suspended) => Presence == Presence.Present && OwnsDomain && !Partial && Suspended == suspended;

    public static Snapshot Absent() => new(Presence.Absent, null, null, false, [], []);

    public static Snapshot Unreadable(string problem) => new(Presence.Unreadable, problem, null, false, [], []);
}

/// <summary>
/// La lectura de estado verificada que usan get_status y todas las operaciones. Ninguna operación cambia algo sin
/// leer antes, y ningún cambio se da por hecho sin leer después.
/// </summary>
internal static class AccountState
{
    private static readonly (string Command, string Name)[] Resources =
    [
        ("v-list-web-domains", "web"),
        ("v-list-mail-domains", "mail"),
        ("v-list-dns-domains", "dns"),
        ("v-list-databases", "databases"),
        ("v-list-cron-jobs", "cron"),
    ];

    public static async Task<Snapshot> Read(HestiaClient hestia, HestiaAccount account)
    {
        var username = account.Username;
        JsonNode user;
        try
        {
            user = await hestia.List("v-list-user", username).ConfigureAwait(false);
        }
        catch (HestiaRefusal refusal) when (refusal.Text == $"Error: user {username} doesn't exist")
        {
            // El texto exacto de Hestia, y aun así no alcanza: se confirma con el inventario.
            return await Absence(hestia, username).ConfigureAwait(false);
        }
        catch (HestiaRefusal)
        {
            return Snapshot.Unreadable("user_refused");
        }

        if (user is JsonObject { Count: 0 })
        {
            return await Absence(hestia, username).ConfigureAwait(false);
        }

        if (user is not JsonObject { Count: 1 } one || one[username] is not JsonObject record || Flag(record) is not { } suspended)
        {
            return Snapshot.Unreadable("user_mismatch");
        }

        var mixed = new List<string>();
        var resources = new JsonObject();
        var owns = false;
        foreach (var (command, name) in Resources)
        {
            JsonNode node;
            try
            {
                node = await hestia.List(command, username).ConfigureAwait(false);
            }
            catch (HestiaRefusal)
            {
                return Snapshot.Unreadable($"{name}_refused");
            }

            var rows = Rows(node);
            if (rows is null || rows.Any(r => Flag(r) is null))
            {
                return Snapshot.Unreadable($"{name}_unexpected");
            }

            resources[name] = new JsonObject { ["count"] = rows.Count, ["suspended"] = rows.Count(r => Flag(r) == "yes") };
            if (rows.Any(r => Flag(r) != suspended))
            {
                mixed.Add(name);
            }

            // El dominio del servicio tiene que ser una clave exacta del web o del DNS: sin regex ni parecidos. Los
            // paquetes solo de DNS pueden no tener web.
            if (name is "web" or "dns" && node is JsonObject keyed && keyed.Any(p => string.Equals(p.Key, account.Domain, StringComparison.OrdinalIgnoreCase)))
            {
                owns = true;
            }
        }

        return new Snapshot(Presence.Present, null, record, owns, mixed, resources);
    }

    /// <summary>Ausente solo si el inventario no está vacío, todos sus registros son de usuarios y el pedido no figura.</summary>
    private static async Task<Snapshot> Absence(HestiaClient hestia, string username)
    {
        JsonNode inventory;
        try
        {
            inventory = await hestia.List("v-list-users").ConfigureAwait(false);
        }
        catch (HestiaRefusal)
        {
            return Snapshot.Unreadable("absence_unproven");
        }

        return inventory is JsonObject { Count: > 0 } users
            && users.All(u => u.Value is JsonObject r && Flag(r) is not null)
            && !users.Any(u => string.Equals(u.Key, username, StringComparison.OrdinalIgnoreCase))
            ? Snapshot.Absent()
            : Snapshot.Unreadable("absence_unproven");
    }

    /// <summary>Las filas de una lista de Hestia: un objeto por clave o un arreglo. null si no tiene esa forma.</summary>
    private static List<JsonObject>? Rows(JsonNode node)
    {
        IEnumerable<JsonNode?> items = node switch
        {
            JsonObject keyed => keyed.Select(p => p.Value),
            JsonArray list => list,
            _ => [],
        };
        var rows = items.OfType<JsonObject>().ToList();
        return node is JsonObject or JsonArray && rows.Count == items.Count() ? rows : null;
    }

    private static string? Flag(JsonObject row) => row["SUSPENDED"].Text() is "yes" or "no" ? row["SUSPENDED"].Text() : null;
}
