using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Api;

/// <summary>Ajustes de la API compatible: viene apagada (D-12) y trae las equivalencias que llena el importador.</summary>
public sealed class CompatOptions
{
    public bool Enabled { get; set; }

    /// <summary>ID de moneda del sistema anterior a código ISO, por ejemplo {"1": "ARS"}.</summary>
    public Dictionary<string, string> Currencies { get; set; } = [];
}

/// <summary>
/// API compatible v0 (diseno/api-compatible.md): POST a /includes/api.php con formulario, credencial en identifier y
/// secret (o Bearer) y los 8 comandos de v0, traducidos a los mismos casos de uso que la API propia. Los mensajes
/// de error son propios.
/// </summary>
internal static partial class CompatApi
{
    private static readonly HashSet<string> Commands =
        ["GetClientsDetails", "GetClientsProducts", "GetInvoice", "AddClient", "UpdateClient", "AddOrder", "AddInvoicePayment", "AcceptOrder"];

    /// <summary>Columnas de precio por ciclo y de cargo de alta, como las leen las integraciones en GetProducts.</summary>
    private static readonly (string Price, string Setup, string Cycle)[] PriceColumns =
    [
        ("monthly", "msetupfee", "P1M"),
        ("quarterly", "qsetupfee", "P3M"),
        ("semiannually", "ssetupfee", "P6M"),
        ("annually", "asetupfee", "P1Y"),
        ("biennially", "bsetupfee", "P2Y"),
        ("triennially", "tsetupfee", "P3Y"),
    ];

    public const string Path = "/includes/api.php";

    public static void MapCompatApi(this WebApplication app) =>
        app.MapPost(Path, Handle).DisableAntiforgery().RequireRateLimiting(ApiV1.ClientLimiter);

    private static async Task<IResult> Handle(HttpContext http, IOptions<CompatOptions> options, CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            return Results.NotFound();
        }

        if (!http.Request.HasFormContentType || http.Request.QueryString.HasValue)
        {
            return Error("El pedido va como formulario y sin query.", 400);
        }

        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(ct);
        }
        catch (Exception ex) when (ex is BadHttpRequestException or InvalidDataException)
        {
            // Más de 1 MiB, cortado, o fuera de los topes de FormOptions (1024 campos, nombres de 2048 caracteres).
            return Error("No se pudo leer el formulario: es demasiado grande o está mal armado.", ex is BadHttpRequestException bad ? bad.StatusCode : 400);
        }
        var action = form["action"].ToString();
        if (form["responsetype"].ToString() is { Length: > 0 } rt && rt != "json")
        {
            return Error("Solo se responde en JSON.");
        }

        var key = await Authenticate(http, form, ct);
        if (key is null)
        {
            return Error("Credencial inválida.", 403);
        }

        if (!http.RequestServices.GetRequiredService<ApiKeyLimiter>().TryAcquire(key))
        {
            return Error("La credencial pasó su tope de pedidos por minuto.", 429);
        }

        if (!Commands.Contains(action) && !AddedCommands.ContainsKey(action))
        {
            return Error($"Comando no soportado: {action}.");
        }

        if (!key.Allows($"compat:{action}"))
        {
            return Error($"La credencial no tiene permiso para {action}.", 403);
        }

        Actor.Current = new Actor(ActorKind.Api, ApiKeyService.Describe(key), http.Connection.RemoteIpAddress);
        var result = await Run(action, http.RequestServices, form, options.Value, ct);
        if (!action.StartsWith("Get", StringComparison.Ordinal))
        {
            await ApiAudit.Record(http, $"compat {action}", result, Target(form));
        }

        return result;
    }

    /// <summary>Solo los campos del formulario que nombran el objetivo: nunca datos personales ni la credencial.</summary>
    private static JsonObject? Target(IFormCollection form)
    {
        var target = new JsonObject();
        foreach (var name in new[] { "clientid", "invoiceid", "orderid", "serviceid", "accountid", "transid", "gateway" })
        {
            if (form[name].ToString() is { Length: > 0 } value)
            {
                target[name] = value;
            }
        }

        return target.Count > 0 ? target : null;
    }

    private static async Task<IResult> Run(string action, IServiceProvider sp, IFormCollection form, CompatOptions options, CancellationToken ct)
    {
        try
        {
            return action switch
            {
                "GetClientsDetails" => await GetClientsDetails(sp, form, ct),
                "GetClientsProducts" => await GetClientsProducts(sp, form, ct),
                "GetInvoice" => await GetInvoice(sp, form, ct),
                "AddClient" => await AddClient(sp, form, options, ct),
                "UpdateClient" => await UpdateClient(sp, form, ct),
                "AddOrder" => await AddOrder(sp, form, ct),
                "AddInvoicePayment" => await AddInvoicePayment(sp, form, ct),
                "AcceptOrder" => await AcceptOrder(sp, form, ct),
                _ => await AddedCommands[action](sp, form, ct),
            };
        }
        catch (Exception ex) when (PublicErrors.IsOwn(ex) && ex is ValidationException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException)
        {
            return Error(ex.Message);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or JsonException)
        {
            // Un número, un base64 o un JSON del formulario que no se pudo leer. El texto del framework no sale.
            return Error("Hay un dato con formato inválido.");
        }
    }

    private static async Task<ApiKey?> Authenticate(HttpContext http, IFormCollection form, CancellationToken ct)
    {
        var keys = http.RequestServices.GetRequiredService<ApiKeyService>();
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.Ordinal) && ApiKeyService.TryParseToken(header[7..], out var p, out var s))
        {
            return await keys.Authenticate(p, s, http.Connection.RemoteIpAddress, ct);
        }

        var identifier = form["identifier"].ToString();
        var secret = form["secret"].ToString();
        return identifier.Length > 0 && secret.Length > 0
            ? await keys.Authenticate(identifier, secret, http.Connection.RemoteIpAddress, ct)
            : null;
    }

    private static async Task<IResult> GetClientsDetails(IServiceProvider sp, IFormCollection form, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BillingDbContext>();
        var email = form["email"].ToString();
        var account = form["clientid"].ToString() is { Length: > 0 } cid
            ? await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == long.Parse(cid, CultureInfo.InvariantCulture), ct)
            : email.Length == 0 ? null
            : await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Email.ToLower() == email.ToLower(), ct);
        if (account is null)
        {
            return Error("No existe la cuenta.");
        }

        var tax = await db.TaxIdentities.AsNoTracking().SingleOrDefaultAsync(t => t.AccountId == account.Id && t.ValidTo == null, ct);
        var (first, last) = SplitName(account.Name);
        return Success(new JsonObject
        {
            ["userid"] = account.Id,
            ["client_id"] = account.Id,
            ["clientid"] = account.Id,
            ["id"] = account.Id,
            ["firstname"] = first,
            ["lastname"] = last,
            ["fullname"] = account.Name,
            ["companyname"] = account.CompanyName ?? string.Empty,
            ["email"] = account.Email,
            ["address1"] = tax?.Street ?? string.Empty,
            ["city"] = tax?.City ?? string.Empty,
            ["state"] = tax?.Province ?? string.Empty,
            ["postcode"] = tax?.PostalCode ?? string.Empty,
            ["country"] = tax?.Country ?? string.Empty,
            ["currency_code"] = account.Currency.Code,
            ["status"] = account.Status.ToString(),
        });
    }

    private static async Task<IResult> GetClientsProducts(IServiceProvider sp, IFormCollection form, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BillingDbContext>();
        var query = db.Services.AsNoTracking().AsQueryable();
        if (form["clientid"].ToString() is { Length: > 0 } cid)
        {
            var id = long.Parse(cid, CultureInfo.InvariantCulture);
            query = query.Where(s => s.AccountId == id);
        }

        if (form["serviceid"].ToString() is { Length: > 0 } sid)
        {
            var id = long.Parse(sid, CultureInfo.InvariantCulture);
            query = query.Where(s => s.Id == id);
        }

        var start = Int(form, "limitstart", 0);
        var limit = Int(form, "limitnum", 25);
        var total = await query.CountAsync(ct);
        var page = await query.OrderBy(s => s.Id).Skip(start).Take(limit).ToListAsync(ct);
        var productIds = page.Select(s => s.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, Group = db.ProductGroups.Where(g => g.Id == p.GroupId).Select(g => g.Name).FirstOrDefault() })
            .ToDictionaryAsync(p => p.Id, ct);
        var list = new JsonArray([.. page.Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s.Id,
            ["clientid"] = s.AccountId,
            ["pid"] = s.ProductId,
            ["name"] = products.GetValueOrDefault(s.ProductId)?.Name ?? string.Empty,
            ["groupname"] = products.GetValueOrDefault(s.ProductId)?.Group ?? string.Empty,
            ["domain"] = s.Label ?? string.Empty,
            ["status"] = ServiceStatusText(s.Status),
            ["billingcycle"] = CycleText(s.Cycle),
            ["recurringamount"] = Money(s.RecurringAmount),
            ["regdate"] = Date(DateOnly.FromDateTime(s.CreatedAt.UtcDateTime)),
            ["nextduedate"] = s.NextDueDate is { } d ? Date(d) : string.Empty,
        })]);
        return Success(new JsonObject
        {
            ["totalresults"] = total,
            ["startnumber"] = start,
            ["numreturned"] = page.Count,
            ["products"] = new JsonObject { ["product"] = list },
        });
    }

    private static async Task<IResult> GetInvoice(IServiceProvider sp, IFormCollection form, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BillingDbContext>();
        var id = long.Parse(form["invoiceid"].ToString(), CultureInfo.InvariantCulture);
        var d = await db.Documents.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id && x.Kind == DocumentKind.Invoice, ct);
        if (d is null)
        {
            return Error("No existe la factura.");
        }

        var paidOn = d.Status == InvoiceStatus.Paid
            ? await db.Allocations.AsNoTracking().Where(a => a.DocumentId == id).MaxAsync(a => (DateTimeOffset?)a.CreatedAt, ct)
            : null;
        return Success(new JsonObject
        {
            ["invoiceid"] = d.Id,
            ["invoicenum"] = d.Number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["userid"] = d.AccountId,
            ["date"] = d.IssueDate is { } issue ? Date(issue) : string.Empty,
            ["duedate"] = Date(d.DueDate),
            ["datepaid"] = paidOn is { } p ? p.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty,
            ["subtotal"] = Money(d.NetTaxed + d.Exempt + d.NotTaxed),
            ["tax"] = Money(d.Tax),
            ["total"] = Money(d.Total),
            ["status"] = InvoiceStatusText(d),
            ["items"] = new JsonObject
            {
                ["item"] = new JsonArray([.. d.Lines.Select(l => (JsonNode)new JsonObject
                {
                    ["id"] = l.Id,
                    ["description"] = l.Description,
                    ["amount"] = Money(l.Amount),
                })]),
            },
        });
    }

    /// <summary>
    /// El catálogo con precios por moneda y ciclo y las opciones con los suyos. Un ciclo que no se vende va con -1.00,
    /// que es lo que filtran las integraciones.
    /// </summary>
    private static async Task<IResult> GetProducts(IServiceProvider sp, IFormCollection form, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BillingDbContext>();
        long? pid = Optional(form, "pid") is { } p ? long.Parse(p, CultureInfo.InvariantCulture) : null;
        long? gid = Optional(form, "gid") is { } g ? long.Parse(g, CultureInfo.InvariantCulture) : null;
        var products = (await CatalogQueries.Products(db, sp.GetRequiredService<BillingCore>().Today(), pid is { } id ? [id] : null, ct))
            .Where(x => gid is null || x.GroupId == gid)
            .ToList();
        var currencies = products.SelectMany(x => x.Prices.Concat(x.Options.SelectMany(o => o.Values.SelectMany(v => v.Prices))))
            .Select(x => x.Currency).Distinct().Order(StringComparer.Ordinal).ToList();
        var list = new JsonArray([.. products.Select(x => (JsonNode)new JsonObject
        {
            ["pid"] = x.Id,
            ["gid"] = x.GroupId ?? 0,
            ["name"] = x.Name,
            ["description"] = x.Description ?? string.Empty,
            ["groupname"] = x.GroupName ?? string.Empty,
            ["hidden"] = !x.Visible,
            ["pricing"] = Pricing(x.Prices, currencies),
            ["configoptions"] = new JsonObject
            {
                ["configoption"] = new JsonArray([.. x.Options.Select(o => (JsonNode)new JsonObject
                {
                    ["id"] = o.Id,
                    ["name"] = o.Name,
                    ["type"] = o.Kind switch { OptionKind.Radio => "2", OptionKind.YesNo => "3", OptionKind.Quantity => "4", _ => "1" },
                    ["options"] = new JsonObject
                    {
                        ["option"] = new JsonArray([.. o.Values.Select(v => (JsonNode)new JsonObject { ["id"] = v.Id, ["name"] = v.Name, ["pricing"] = Pricing(v.Prices, currencies) })]),
                    },
                })]),
            },
        })]);
        return Success(new JsonObject { ["totalresults"] = products.Count, ["products"] = new JsonObject { ["product"] = list } });
    }

    /// <summary>Precio y cargo de alta de cada ciclo por moneda. El pago único y el gratis van en la primera columna.</summary>
    private static JsonObject Pricing(IReadOnlyList<PriceView> prices, IEnumerable<string> currencies)
    {
        var pricing = new JsonObject();
        foreach (var currency in currencies)
        {
            var node = new JsonObject();
            foreach (var (column, setup, cycle) in PriceColumns)
            {
                var hit = prices.FirstOrDefault(p => p.Currency == currency && (p.Cycle == cycle || (cycle == "P1M" && p.Cycle is "onetime" or "free")));
                node[setup] = hit is null ? "-1.00" : Money(hit.SetupFee);
                node[column] = hit is null ? "-1.00" : Money(hit.Amount);
            }

            pricing[currency] = node;
        }

        return pricing;
    }

    private static async Task<IResult> AddClient(IServiceProvider sp, IFormCollection form, CompatOptions options, CancellationToken ct)
    {
        var currency = options.Currencies.TryGetValue(form["currency"].ToString(), out var code) ? code : "ARS";
        var id = await sp.GetRequiredService<AccountService>().Create(new AccountInput(
            $"{form["firstname"]} {form["lastname"]}".Trim(),
            form["email"].ToString(),
            new Currency(currency),
            Phone: Optional(form, "phonenumber"),
            CompanyName: Optional(form, "companyname")), ct);
        return Success(new JsonObject { ["clientid"] = id });
    }

    private static async Task<IResult> UpdateClient(IServiceProvider sp, IFormCollection form, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BillingDbContext>();
        var id = long.Parse(form["clientid"].ToString(), CultureInfo.InvariantCulture);
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw new KeyNotFoundException("No existe la cuenta.");
        var (first, last) = SplitName(account.Name);
        await sp.GetRequiredService<AccountService>().Update(id, new AccountInput(
            $"{Optional(form, "firstname") ?? first} {Optional(form, "lastname") ?? last}".Trim(),
            Optional(form, "email") ?? account.Email,
            account.Currency,
            account.Language,
            Optional(form, "phonenumber") ?? account.Phone,
            Optional(form, "companyname") ?? account.CompanyName), ct);
        return Success(new JsonObject { ["clientid"] = id });
    }

    private static async Task<IResult> AddOrder(IServiceProvider sp, IFormCollection form, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BillingDbContext>();
        var accountId = long.Parse(form["clientid"].ToString(), CultureInfo.InvariantCulture);
        var items = new List<OrderItem>();
        foreach (var (pid, i) in Indexed(form, "pid").Select((p, i) => (p, i)))
        {
            var productId = long.Parse(pid, CultureInfo.InvariantCulture);
            var cycle = ParseCycle(Indexed(form, "billingcycle").ElementAtOrDefault(i) ?? "monthly");
            var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == productId && p.CycleUnit == cycle.Unit && p.CycleCount == cycle.Count && p.Active, ct)
                ?? throw new KeyNotFoundException($"El producto {productId} no tiene plan {cycle.Unit}.");
            items.Add(new OrderItem(
                plan.Id,
                Options: await Choices(db, Decode(Indexed(form, "configoptions").ElementAtOrDefault(i)), ct),
                OrderData: Decode(Indexed(form, "customfields").ElementAtOrDefault(i))));
        }

        var placed = await sp.GetRequiredService<OrderService>().PlaceOrder(accountId, items, ct, paymentMethod: Optional(form, "paymentmethod"));
        return Success(new JsonObject
        {
            ["orderid"] = placed.OrderId,
            ["invoiceid"] = placed.InvoiceId is { } inv ? inv : string.Empty,
            ["productids"] = string.Join(',', placed.ServiceIds),
        });
    }

    private static async Task<IResult> AddInvoicePayment(IServiceProvider sp, IFormCollection form, CancellationToken ct)
    {
        var db = sp.GetRequiredService<BillingDbContext>();
        var id = long.Parse(form["invoiceid"].ToString(), CultureInfo.InvariantCulture);
        var invoice = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.Kind == DocumentKind.Invoice, ct)
            ?? throw new KeyNotFoundException("No existe la factura.");
        var channel = $"compat:{form["gateway"]}";
        var transactionId = form["transid"].ToString().Trim() is { Length: > 0 } t ? t : throw new ValidationException("Falta transid: sin él no se puede evitar acreditar dos veces.");
        if (await db.Payments.AsNoTracking().AnyAsync(p => p.Channel == channel && p.ExternalId == transactionId, ct))
        {
            // El mismo pago informado de nuevo: ya entró, no se vuelve a calcular nada.
            return Success([]);
        }

        if (invoice.Status == InvoiceStatus.Cancelled)
        {
            throw new InvalidOperationException("La factura está anulada.");
        }

        var amount = Optional(form, "amount") is { } a ? decimal.Parse(a, CultureInfo.InvariantCulture) : invoice.Pending;
        if (amount > invoice.Pending)
        {
            throw new ValidationException("El monto supera el saldo de la factura.");
        }

        await sp.GetRequiredService<PaymentService>().Record(new PaymentInput(
            invoice.AccountId,
            channel,
            transactionId,
            invoice.Currency,
            amount,
            PaymentStatus.Approved,
            [id]), ct);
        return Success([]);
    }

    private static async Task<IResult> AcceptOrder(IServiceProvider sp, IFormCollection form, CancellationToken ct)
    {
        var id = long.Parse(form["orderid"].ToString(), CultureInfo.InvariantCulture);
        await sp.GetRequiredService<OrderService>().Accept(id, Bool(form, "autosetup", true), ct);
        return Success([]);
    }

    /// <summary>El servicio de los comandos Module*: accountid, que es como lo llama WHMCS, o serviceid.</summary>
    private static long ServiceId(IFormCollection form) =>
        long.Parse(Optional(form, "accountid") ?? form["serviceid"].ToString(), CultureInfo.InvariantCulture);

    private static JsonHttpResult<JsonObject> Success(JsonObject body)
    {
        body["result"] = "success";
        return TypedResults.Json(body);
    }

    internal static JsonHttpResult<JsonObject> Error(string message, int status = 200) =>
        TypedResults.Json(new JsonObject { ["result"] = "error", ["message"] = message }, statusCode: status);

    private static (string First, string Last) SplitName(string name)
    {
        var i = name.IndexOf(' ', StringComparison.Ordinal);
        return i < 0 ? (name, string.Empty) : (name[..i], name[(i + 1)..]);
    }

    private static string ServiceStatusText(ServiceStatus s) => s switch
    {
        ServiceStatus.Active => "Active",
        ServiceStatus.Suspended => "Suspended",
        ServiceStatus.Terminated => "Terminated",
        ServiceStatus.Cancelled => "Cancelled",
        _ => "Pending",
    };

    private static string CycleText(BillingCycle c) => c.Unit switch
    {
        CycleUnit.Month when c.Count == 1 => "monthly",
        CycleUnit.Month when c.Count == 3 => "quarterly",
        CycleUnit.Month when c.Count == 6 => "semiannually",
        CycleUnit.Year when c.Count == 1 => "annually",
        CycleUnit.Year when c.Count == 2 => "biennially",
        CycleUnit.Year when c.Count == 3 => "triennially",
        CycleUnit.Free => "free",
        CycleUnit.OneTime => "onetime",
        _ => string.Create(CultureInfo.InvariantCulture, $"{c.Count} {c.Unit}"),
    };

    private static BillingCycle ParseCycle(string text) => text.ToLowerInvariant() switch
    {
        "monthly" => BillingCycle.Monthly,
        "quarterly" => new BillingCycle(CycleUnit.Month, 3),
        "semiannually" => new BillingCycle(CycleUnit.Month, 6),
        "annually" => BillingCycle.Annually,
        "biennially" => new BillingCycle(CycleUnit.Year, 2),
        "triennially" => new BillingCycle(CycleUnit.Year, 3),
        "free" => BillingCycle.Free,
        "onetime" => BillingCycle.OneTime,
        _ => throw new ValidationException($"Ciclo desconocido: {text}."),
    };

    /// <summary>
    /// configoptions es un mapa de ID de opción a lo elegido: el ID del valor en una lista y el número en
    /// una opción de cantidad o de sí o no. Los IDs son los del sistema anterior, que la importación conserva.
    /// </summary>
    private static async Task<IReadOnlyList<OptionChoice>> Choices(BillingDbContext db, string json, CancellationToken ct)
    {
        var map = JsonNode.Parse(json) as JsonObject ?? [];
        var ids = map.Select(p => long.Parse(p.Key, CultureInfo.InvariantCulture)).ToList();
        var kinds = await db.Options.AsNoTracking().Where(o => ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Kind, ct);
        return [.. map.Select(p =>
        {
            var id = long.Parse(p.Key, CultureInfo.InvariantCulture);
            var value = long.Parse(p.Value?.ToString() ?? string.Empty, CultureInfo.InvariantCulture);
            return kinds.GetValueOrDefault(id, OptionKind.Select) is OptionKind.Quantity or OptionKind.YesNo
                ? new OptionChoice(id, Quantity: checked((int)value))
                : new OptionChoice(id, value);
        })];
    }

    /// <summary>configoptions y customfields llegan en base64, de JSON o de un arreglo PHP serializado.</summary>
    private static string Decode(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "{}";
        }

        var text = Encoding.UTF8.GetString(Convert.FromBase64String(value));
        if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
        {
            return JsonNode.Parse(text)!.ToJsonString();
        }

        return JsonSerializer.Serialize(PhpSerialized.ParseFlatArray(text));
    }

    /// <summary>Lee pid[0], pid[1] y también el valor suelto pid.</summary>
    private static List<string> Indexed(IFormCollection form, string name)
    {
        var values = form.Keys.Where(k => k.StartsWith(name + "[", StringComparison.Ordinal))
            .OrderBy(k => int.Parse(k[(name.Length + 1)..^1], CultureInfo.InvariantCulture))
            .Select(k => form[k].ToString())
            .ToList();
        if (values.Count == 0 && form[name].ToString() is { Length: > 0 } single)
        {
            values.Add(single);
        }

        return values;
    }

    private static string? Optional(IFormCollection form, string name) =>
        form[name].ToString() is { Length: > 0 } v ? v : null;

    private static int Int(IFormCollection form, string name, int fallback) =>
        int.TryParse(form[name].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static bool Bool(IFormCollection form, string name, bool fallback) => form[name].ToString().ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        "" => fallback,
        var other => throw new ValidationException($"Booleano inválido en {name}: {other}."),
    };

    private static string Money(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Parser mínimo de arreglos PHP serializados (diseño propio): solo claves y valores enteros o de texto, sin
/// anidamiento. Cualquier otra cosa da error. No se instancia nada.
/// </summary>
public static class PhpSerialized
{
    public static Dictionary<string, string> ParseFlatArray(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Los largos de s:N:"..." van en bytes: se convierte una sola vez y se avanza por posición en bytes.
        var bytes = Encoding.UTF8.GetBytes(text);
        var pos = 0;
        Expect(bytes, ref pos, "a:"u8);
        var count = ReadInt(bytes, ref pos, (byte)':');
        Expect(bytes, ref pos, "{"u8);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var key = ReadScalar(bytes, ref pos);
            result[key] = ReadScalar(bytes, ref pos);
        }

        Expect(bytes, ref pos, "}"u8);
        return result;
    }

    private static string ReadScalar(byte[] bytes, ref int pos)
    {
        if (pos + 2 > bytes.Length)
        {
            throw new FormatException("Arreglo serializado truncado.");
        }

        var type = (char)bytes[pos];
        pos += 2;
        switch (type)
        {
            case 'i':
                return ReadInt(bytes, ref pos, (byte)';').ToString(CultureInfo.InvariantCulture);
            case 's':
                var length = ReadInt(bytes, ref pos, (byte)':');
                Expect(bytes, ref pos, "\""u8);
                if (length < 0 || length > bytes.Length - pos)
                {
                    throw new FormatException("Cadena serializada truncada.");
                }

                var value = Encoding.UTF8.GetString(bytes, pos, length);
                pos += length;
                Expect(bytes, ref pos, "\";"u8);
                return value;
            default:
                throw new FormatException($"Tipo no soportado en el arreglo serializado: {type}.");
        }
    }

    private static int ReadInt(byte[] bytes, ref int pos, byte end)
    {
        var length = bytes.AsSpan(pos, Math.Min(12, bytes.Length - pos)).IndexOf(end);
        if (length < 0 || !int.TryParse(bytes.AsSpan(pos, length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException("Número serializado inválido.");
        }

        pos += length + 1;
        return value;
    }

    private static void Expect(byte[] bytes, ref int pos, ReadOnlySpan<byte> token)
    {
        if (!bytes.AsSpan(pos).StartsWith(token))
        {
            throw new FormatException($"Se esperaba '{Encoding.ASCII.GetString(token)}' en la posición {pos}.");
        }

        pos += token.Length;
    }
}
