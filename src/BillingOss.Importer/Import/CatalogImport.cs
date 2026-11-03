using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Importer.Import;

/// <summary>
/// El catálogo completo del origen (diseno/catalogo.md): opciones configurables con su precio por moneda y ciclo,
/// complementos, cupones, la selección de cada servicio y los cambios de opciones que esperan el pago de su factura.
/// Conserva los IDs de grupos, opciones, valores y cupones, que la API compatible recibe por número. Los complementos
/// y sus servicios toman IDs nuevos, porque en el origen se numeran aparte y chocarían con los de productos y servicios.
/// </summary>
public sealed partial class SourceImport
{
    /// <summary>Columnas de precio y de cargo de alta de cada ciclo en el origen.</summary>
    private static readonly (string Price, string Setup, BillingCycle Cycle)[] PriceColumns =
    [
        ("monthly", "msetupfee", BillingCycle.Monthly),
        ("quarterly", "qsetupfee", new BillingCycle(CycleUnit.Month, 3)),
        ("semiannually", "ssetupfee", new BillingCycle(CycleUnit.Month, 6)),
        ("annually", "asetupfee", BillingCycle.Annually),
        ("biennially", "bsetupfee", new BillingCycle(CycleUnit.Year, 2)),
        ("triennially", "tsetupfee", new BillingCycle(CycleUnit.Year, 3)),
    ];

    private readonly HashSet<long> optionGroups = [];
    private readonly Dictionary<long, Option> options = [];
    private readonly Dictionary<long, OptionValue> optionValues = [];
    private readonly Dictionary<long, long> addonProducts = [];
    private readonly Dictionary<long, long> addonServices = [];
    private readonly Dictionary<long, long> upgradeServices = [];
    private readonly Dictionary<long, Coupon> coupons = [];
    private readonly Dictionary<string, long> couponCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, List<ChosenOption>> liveChoices = [];
    private readonly HashSet<long> priceless = [];
    private ILookup<long, long> productOptionGroups = Enumerable.Empty<long>().ToLookup(x => x);

    /// <summary>
    /// Grupos, opciones, valores y sus precios, y qué grupos ofrece cada producto. Lo que va antes de la barra en el
    /// nombre del origen ("memory|RAM", "4096|4 GB") es la clave de módulo de la opción y el valor de módulo del valor.
    /// Un precio de -1 en el origen es un ciclo que no se ofrece. Los productos de pago único cobran sus opciones con la
    /// columna mensual, como su propio precio.
    /// </summary>
    private async Task OptionCatalog(Dictionary<long, Currency> currencies, CancellationToken ct)
    {
        foreach (var r in Rows("tblproductconfiggroups"))
        {
            var group = new OptionGroup { Name = Cut(Html(Str(r, "name")), 200) ?? $"Opciones {Long(r, "id")}", Description = Cut(Text(Str(r, "description")), 1000) };
            group.SetImportedId(Long(r, "id"));
            db.OptionGroups.Add(group);
            optionGroups.Add(group.Id);
        }

        await Flush(ct).ConfigureAwait(false);
        var links = Rows("tblproductconfiglinks").Select(r => (Group: Long(r, "gid"), Product: Long(r, "pid")))
            .Where(l => optionGroups.Contains(l.Group) && products.Contains(l.Product))
            .Distinct()
            .ToList();
        db.ProductOptionGroups.AddRange(links.Select(l => new ProductOptionGroup(l.Product, l.Group)));
        productOptionGroups = links.ToLookup(l => l.Product, l => l.Group);
        var groupProducts = links.ToLookup(l => l.Group, l => l.Product);

        var orphans = 0;
        foreach (var r in Rows("tblproductconfigoptions"))
        {
            var groupId = Long(r, "gid");
            if (!optionGroups.Contains(groupId))
            {
                orphans++;
                continue;
            }

            var (key, name) = Split(Html(Str(r, "optionname")));
            var kind = Str(r, "optiontype") switch { "2" => OptionKind.Radio, "3" => OptionKind.YesNo, "4" => OptionKind.Quantity, _ => OptionKind.Select };
            var option = new Option
            {
                GroupId = groupId,
                Name = Cut(name, 200) ?? $"Opción {Long(r, "id")}",
                Kind = kind,
                ModuleKey = ModuleKey(Long(r, "id"), key, groupProducts[groupId]),
                MinQuantity = kind == OptionKind.Quantity ? (int)Math.Max(0, Long(r, "qtyminimum")) : 0,
                MaxQuantity = kind == OptionKind.Quantity ? (int)Math.Max(0, Long(r, "qtymaximum")) : 0,
                SortOrder = (int)Long(r, "order"),
                Hidden = Long(r, "hidden") == 1,
            };
            option.SetImportedId(Long(r, "id"));
            db.Options.Add(option);
            options[option.Id] = option;
        }

        await Flush(ct).ConfigureAwait(false);
        foreach (var r in Rows("tblproductconfigoptionssub"))
        {
            if (!options.ContainsKey(Long(r, "configid")))
            {
                orphans++;
                continue;
            }

            var (moduleValue, name) = Split(Html(Str(r, "optionname")));
            var value = new OptionValue
            {
                OptionId = Long(r, "configid"),
                Name = Cut(name, 200) ?? $"Valor {Long(r, "id")}",
                ModuleValue = Cut(moduleValue, 200),
                SortOrder = (int)Long(r, "sortorder"),
                Hidden = Long(r, "hidden") == 1,
            };
            value.SetImportedId(Long(r, "id"));
            db.OptionValues.Add(value);
            optionValues[value.Id] = value;
        }

        await Flush(ct).ConfigureAwait(false);
        var oneTime = links.Where(l => Str(productRows[l.Product], "paytype") == "onetime").Select(l => l.Group).ToHashSet();
        var prices = new List<OptionPrice>();
        foreach (var r in Rows("tblpricing").Where(r => Str(r, "type") == "configoptions" && optionValues.ContainsKey(Long(r, "relid"))))
        {
            if (!currencies.TryGetValue(Long(r, "currency"), out var currency))
            {
                continue;
            }

            var value = optionValues[Long(r, "relid")];
            var offers = Offers(r, "recurring");
            if (oneTime.Contains(options[value.OptionId].GroupId))
            {
                offers = offers.Concat(Offers(r, "onetime"));
            }

            prices.AddRange(offers.Select(o => new OptionPrice(value.Id, currency, o.Cycle, o.Amount, o.Setup)));
        }

        var priced = prices.DistinctBy(p => (p.ValueId, p.Currency, p.CycleUnit, p.CycleCount)).ToList();
        db.OptionPrices.AddRange(priced);
        await Flush(ct).ConfigureAwait(false);

        if (priceless.Count(p => !productOptionGroups[p].Any()) is var bare and > 0)
        {
            Incidents.Add($"{bare} productos con precio 0 en todos sus ciclos y sin opciones configurables que lo den.");
        }

        var lists = options.Values.Count(o => o.SingleValue && optionValues.Values.Count(v => v.OptionId == o.Id) != 1);
        if (lists > 0)
        {
            Incidents.Add($"{lists} opciones de cantidad o de sí o no con más de un valor: cobran el primero.");
        }

        if (orphans > 0)
        {
            Incidents.Add($"{orphans} opciones o valores de un grupo u opción que ya no existe en el origen, no se importan.");
        }

        Total("grupos de opciones", optionGroups.Count);
        Total("opciones", options.Count);
        Total("valores de opciones", optionValues.Count);
        Total("precios de opciones", priced.Count);
        Total("productos con opciones", links.Select(l => l.Product).Distinct().Count());
        await log.WriteLineAsync($"Opciones: {optionGroups.Count} grupos, {options.Count} opciones, {optionValues.Count} valores").ConfigureAwait(false);
    }

    /// <summary>
    /// Los complementos del origen como productos que cuelgan de otros (los de su lista de productos), con su precio por
    /// moneda y ciclo. Un complemento "recurring" se vende en todos los ciclos que tienen precio, uno con ciclo fijo solo
    /// en ese y uno "free" en el ciclo gratis.
    /// </summary>
    private async Task Addons(Dictionary<long, Currency> currencies, CancellationToken ct)
    {
        if (!Rows("tbladdons").Any())
        {
            return;
        }

        // IDs nuevos, arriba de los productos importados.
        await db.Database.ExecuteSqlRawAsync(SequenceReset("catalog.products"), ct).ConfigureAwait(false);
        var created = new List<(long Source, Product Product, DumpRow Row)>();
        foreach (var r in Rows("tbladdons"))
        {
            var module = Blank(Str(r, "module"));
            var mapping = module is null ? null : modules.GetValueOrDefault(module);
            var product = new Product
            {
                Name = Cut(Html(Str(r, "name")), 200) ?? $"Complemento {Long(r, "id")}",
                Description = Cut(Text(Str(r, "description")), 4000),
                Addon = true,
                ParentProductIds = [.. (Str(r, "packages") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(p => long.TryParse(p, CultureInfo.InvariantCulture, out var id) ? id : 0).Where(products.Contains).Distinct()],
                Kind = module is null || mapping?.Manual == true ? ProductKind.Manual : ProductKind.Provisioned,
                Visible = Long(r, "hidden") == 0 && Long(r, "retired") == 0,
                AllowQuantity = Long(r, "allowqty") == 1,
                TaxCategory = Long(r, "tax") == 1 ? TaxCategories.Digital : TaxCategories.Untaxed,
                ModuleInstanceId = Instance(mapping),
                ModuleParams = module is null ? "{}" : JsonSerializer.Serialize(new { module }),
                SortOrder = (int)Long(r, "weight"),
                Activation = Activation(Str(r, "autoactivate")),
            };
            db.Products.Add(product);
            created.Add((Long(r, "id"), product, r));
        }

        await Flush(ct).ConfigureAwait(false);
        var plans = new Dictionary<(long, CycleUnit, int), Plan>();
        var prices = new List<(Plan Plan, Currency Currency, decimal Amount, decimal Setup)>();
        foreach (var (source, product, row) in created)
        {
            addonProducts[source] = product.Id;
            products.Add(product.Id);
            productRows[product.Id] = row;
            var kind = (Str(row, "billingcycle") ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "free" or "free account" => "free",
                "recurring" => "recurring",
                "onetime" or "one time" => "onetime",
                var other => LooseCycle(other) is { IsRecurring: true } fixedCycle ? fixedCycle.Code : "recurring",
            };
            foreach (var r in Rows("tblpricing").Where(r => Str(r, "type") == "addon" && Long(r, "relid") == source))
            {
                if (!currencies.TryGetValue(Long(r, "currency"), out var currency))
                {
                    continue;
                }

                foreach (var (cycle, amount, setup) in Offers(r, kind))
                {
                    var key = (product.Id, cycle.Unit, cycle.Count);
                    if (!plans.TryGetValue(key, out var plan))
                    {
                        plan = new Plan(product.Id, cycle);
                        plans[key] = plan;
                        db.Plans.Add(plan);
                    }

                    prices.Add((plan, currency, amount, setup));
                }
            }
        }

        await Flush(ct).ConfigureAwait(false);
        foreach (var (plan, currency, amount, setup) in prices.DistinctBy(p => (p.Plan.Id, p.Currency)))
        {
            db.PlanPrices.Add(new PlanPrice(plan.Id, currency, amount, setup, new DateOnly(2000, 1, 1)));
        }

        await Flush(ct).ConfigureAwait(false);
        Total("complementos", created.Count);
    }

    /// <summary>
    /// Los cupones del origen, con su ID. Nunca se amplía uno: si la lista de productos o de ciclos del origen no tiene
    /// nada que exista acá (por ejemplo, solo dominios), o si exigía otro producto contratado, entra inactivo. El monto
    /// fijo está en la moneda base del origen, que lo convertía al cobrar en otra: entra en esa moneda, así en otra no
    /// descuenta hasta que el staff lo revise, en lugar de descontar el mismo número.
    /// </summary>
    private async Task Coupons(CancellationToken ct)
    {
        var inactive = 0;
        var upgrades = 0;
        var fixedAmounts = 0;
        var baseCurrency = Rows("tblcurrencies").Where(r => Long(r, "default") == 1).Select(r => Blank(Str(r, "code"))?.ToUpperInvariant()).FirstOrDefault() is { } baseCode
            ? new Currency(baseCode)
            : (Currency?)null;
        foreach (var r in Rows("tblpromotions").OrderBy(r => Long(r, "id")))
        {
            var id = Long(r, "id");
            var code = Blank(Str(r, "code"));
            var kind = Str(r, "type") switch { "Percentage" => DiscountKind.Percent, "Fixed Amount" => DiscountKind.Fixed, _ => (DiscountKind?)null };
            var value = Dec(r, "value");
            if (code is null || code.Length > 100 || kind is null || value < 0m || (kind == DiscountKind.Percent && value > 100m))
            {
                Incidents.Add($"Cupón {id}: tipo {Str(r, "type")} con valor {value.ToString(CultureInfo.InvariantCulture)} o código sin equivalente, no se importa.");
                continue;
            }

            if (couponCodes.ContainsKey(code))
            {
                Incidents.Add($"Cupón {id}: el código {code} se repite sin distinguir mayúsculas, queda como {code}-{id}.");
                code = $"{code}-{id}";
            }

            var targets = List(Str(r, "appliesto"));
            var productIds = targets.Select(t => t.StartsWith('A') && long.TryParse(t.AsSpan(1), CultureInfo.InvariantCulture, out var addon)
                    ? addonProducts.GetValueOrDefault(addon)
                    : long.TryParse(t, CultureInfo.InvariantCulture, out var product) && products.Contains(product) ? product : 0)
                .Where(p => p > 0).Distinct().ToArray();
            var cycleNames = List(Str(r, "cycles"));
            var cycles = cycleNames.Select(LooseCycle).OfType<BillingCycle>().Select(c => c.Code).Distinct().ToArray();
            var requires = List(Str(r, "requires")).Count > 0;
            var narrowedToNothing = (targets.Count > 0 && productIds.Length == 0) || (cycleNames.Count > 0 && cycles.Length == 0);
            var uses = (int)Math.Max(0, Long(r, "uses"));
            var coupon = new Coupon
            {
                Code = code,
                Kind = kind.Value,
                Value = value,
                Currency = kind == DiscountKind.Fixed ? baseCurrency : null,
                Recurring = Long(r, "recurring") == 1,
                RecurringCycles = Long(r, "recurring") == 1 && Long(r, "recurfor") > 0 ? (int)Long(r, "recurfor") : null,
                ProductIds = productIds,
                Cycles = cycles,
                StartsOn = Date(r, "startdate"),
                ExpiresOn = Date(r, "expirationdate"),
                MaxUses = Long(r, "maxuses") > 0 ? (int)Math.Max(Long(r, "maxuses"), uses) : null,
                Uses = uses,
                OncePerAccount = Long(r, "onceperclient") == 1,
                NewAccountsOnly = Long(r, "newsignups") == 1,
                ExistingAccountsOnly = Long(r, "existingclient") == 1,
                OncePerOrder = Long(r, "applyonce") == 1,
                Active = !requires && !narrowedToNothing,
                Notes = Cut(Blank(Str(r, "notes")), 1000),
            };
            coupon.SetImportedId(id);
            db.Coupons.Add(coupon);
            coupons[id] = coupon;
            couponCodes[code] = id;
            inactive += coupon.Active ? 0 : 1;
            upgrades += Long(r, "upgrades") == 1 ? 1 : 0;
            fixedAmounts += coupon.Currency is null ? 0 : 1;
        }

        await Flush(ct).ConfigureAwait(false);
        if (fixedAmounts > 0)
        {
            Incidents.Add($"{fixedAmounts} cupones de monto fijo entran en {baseCurrency}, la moneda base del origen, que los convertía al cobrar en otra moneda: en otra moneda no descuentan hasta que el staff los revise.");
        }

        if (inactive > 0)
        {
            Incidents.Add($"{inactive} cupones entran inactivos: exigían otro producto contratado o valían solo para dominios o ciclos que no se importan.");
        }

        if (upgrades > 0)
        {
            Incidents.Add($"{upgrades} cupones también descontaban cambios de plan en el origen: acá los cambios no llevan cupón.");
        }

        Total("cupones", coupons.Count);
    }

    /// <summary>
    /// Lo que el servicio tiene elegido de cada opción: el valor de una lista y la cantidad de una opción de cantidad o
    /// de sí o no. Una fila cuyo valor ya no existe o es de otra opción no se importa.
    /// </summary>
    private List<ChosenOption> Choices(IEnumerable<DumpRow> selection, ref int skipped)
    {
        var chosen = new List<ChosenOption>();
        foreach (var r in selection)
        {
            if (!options.TryGetValue(Long(r, "configid"), out var option))
            {
                skipped++;
                continue;
            }

            var value = optionValues.GetValueOrDefault(Long(r, "optionid")) is { } v && v.OptionId == option.Id ? v
                : option.SingleValue ? optionValues.Values.Where(x => x.OptionId == option.Id).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).FirstOrDefault()
                : null;
            if (value is null || chosen.Any(c => c.Option.Id == option.Id))
            {
                skipped++;
                continue;
            }

            var quantity = option.Kind switch
            {
                OptionKind.Quantity => (int)Math.Max(0, Long(r, "qty")),
                OptionKind.YesNo => Long(r, "qty") > 0 ? 1 : 0,
                _ => 1,
            };
            chosen.Add(new ChosenOption(option, value, quantity, 0m, 0m));
        }

        return chosen;
    }

    /// <summary>
    /// Los servicios de complementos del origen, colgados de su servicio padre, con IDs nuevos arriba de los
    /// importados. El período ya facturado se toma de los renglones de complemento, como en los servicios.
    /// </summary>
    private async Task AddonServices(CancellationToken ct)
    {
        if (!Rows("tblhostingaddons").Any())
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(SequenceReset("services.services"), ct).ConfigureAwait(false);
        var invoiced = Invoiced("Addon");
        var created = new List<(long Source, Service Service)>();
        foreach (var r in Rows("tblhostingaddons"))
        {
            var id = Long(r, "id");
            var accountId = Long(r, "userid");
            if (!accountCurrency.TryGetValue(accountId, out var currency) || !addonProducts.TryGetValue(Long(r, "addonid"), out var productId))
            {
                Incidents.Add($"Complemento contratado {id}: la cuenta {accountId} o el complemento {Long(r, "addonid")} no existe, no se importa.");
                continue;
            }

            var cycle = Cycle(Str(r, "billingcycle"));
            var quantity = (int)Math.Max(1, Long(r, "qty"));
            var registered = Date(r, "regdate");
            var paidThrough = Date(r, "nextduedate");
            var anchor = Anchor(registered, paidThrough ?? Date(r, "nextinvoicedate"));
            var nextDue = NextToBill(cycle, anchor, paidThrough, Date(r, "nextinvoicedate"), invoiced.Contains((id, paidThrough ?? default)));
            var service = new Service(accountId, productId, cycle, currency, Dec(r, "recurring") / quantity, quantity, Instant(registered) ?? now)
            {
                ParentServiceId = services.Contains(Long(r, "hostingid")) ? Long(r, "hostingid") : null,
                PaymentMethod = Blank(Str(r, "paymentmethod")),
            };
            service.ImportState(StatusOf(Str(r, "status")), anchor, nextDue, paidThrough, Instant(registered), Instant(Date(r, "termination_date")));
            db.Services.Add(service);
            created.Add((id, service));
        }

        await Flush(ct).ConfigureAwait(false);
        foreach (var (source, service) in created)
        {
            addonServices[source] = service.Id;
            services.Add(service.Id);
        }

        Total("complementos contratados", created.Count);
    }

    /// <summary>
    /// Los cambios de opciones o de plan que esperan el pago de su factura en el origen pasan a cambios pendientes: si el
    /// cliente paga esa factura acá, el cambio se aplica, y si se anula, se cancela. Un servicio tiene a lo sumo uno, así
    /// que de varios pedidos sin pagar queda el último y los demás salen en el reporte.
    /// </summary>
    private async Task PendingChanges(CancellationToken ct)
    {
        var orderInvoices = Rows("tblorders").GroupBy(r => Long(r, "id")).ToDictionary(g => g.Key, g => Long(g.First(), "invoiceid"));
        var waiting = Rows("tblupgrades")
            .Where(u => Str(u, "status") == "Pending" && Str(u, "type") is "configoptions" or "package")
            .GroupBy(u => Long(u, "orderid"))
            .Select(g => (Order: g.Key, Rows: g.ToList(), Service: Long(g.First(), "relid"), Invoice: orderInvoices.GetValueOrDefault(g.Key)))
            .Where(p => liveChoices.ContainsKey(p.Service) && invoices.TryGetValue(p.Invoice, out var invoice) && invoice.SourceStatus == "Unpaid")
            .GroupBy(p => p.Service)
            .ToList();
        var older = 0;
        var changes = 0;
        foreach (var group in waiting)
        {
            var (_, rows, serviceId, invoiceId) = group.MaxBy(p => p.Order);
            older += group.Count() - 1;
            var service = await db.Services.AsNoTracking().SingleAsync(s => s.Id == serviceId, ct).ConfigureAwait(false);
            if (service.Status != ServiceStatus.Active)
            {
                continue;
            }

            var choices = liveChoices[serviceId].Select(c => c.Choice).ToList();
            var productId = service.ProductId;
            var cycle = service.Cycle;
            foreach (var u in rows)
            {
                if (Str(u, "type") == "package")
                {
                    // "producto,ciclo": las opciones que el producto nuevo no ofrece quedan afuera.
                    var target = (Str(u, "newvalue") ?? string.Empty).Split(',', 2, StringSplitOptions.TrimEntries);
                    productId = long.TryParse(target[0], CultureInfo.InvariantCulture, out var p) && products.Contains(p) ? p : 0;
                    cycle = target.Length > 1 && LooseCycle(target[1]) is { } c ? c : cycle;
                    choices = [.. choices.Where(x => options.TryGetValue(x.OptionId, out var o) && productOptionGroups[productId].Contains(o.GroupId))];
                }
                else if ((Str(u, "originalvalue") ?? string.Empty).Split("=>", 2) is [var optionText, _]
                    && long.TryParse(optionText, CultureInfo.InvariantCulture, out var optionId) && options.TryGetValue(optionId, out var option)
                    && long.TryParse(Str(u, "newvalue"), CultureInfo.InvariantCulture, out var next))
                {
                    choices.RemoveAll(x => x.OptionId == optionId);
                    choices.Add(option.SingleValue
                        ? new OptionChoice(optionId, optionValues.Values.Where(v => v.OptionId == optionId).OrderBy(v => v.SortOrder).ThenBy(v => v.Id).FirstOrDefault()?.Id, (int)next)
                        : new OptionChoice(optionId, next));
                }
            }

            if (productId == 0 || cycle.IsRecurring != service.Cycle.IsRecurring)
            {
                Incidents.Add($"Servicio {serviceId}: el cambio sin pagar del pedido {group.MaxBy(p => p.Order).Order} va a un producto que no existe o pasa entre un ciclo con renovación y uno sin ella (por ejemplo, de gratis a mensual): no se importa y, si se paga, lo hace el staff.");
                continue;
            }

            var recurring = rows.Sum(u => Dec(u, "new_recurring_amount"));
            var toUnitPrice = recurring > 0m ? recurring / service.Quantity : service.UnitPrice + (rows.Sum(u => Dec(u, "recurringchange")) / service.Quantity);
            var change = new ServiceChange(serviceId, service.ProductId, productId, cycle, service.UnitPrice, Math.Max(0m, toUnitPrice), JsonSerializer.Serialize(choices), rows.Sum(u => Dec(u, "amount")), Instant(Date(rows[0], "date")) ?? now)
            {
                BilledUntil = service.NextDueDate,
                DocumentId = invoiceId,
            };
            db.ServiceChanges.Add(change);
            changes++;
        }

        await Flush(ct).ConfigureAwait(false);
        if (older > 0)
        {
            Incidents.Add($"{older} pedidos de cambio sin pagar de servicios que tienen uno más nuevo: si el cliente paga uno de esos, el cambio lo hace el staff.");
        }

        Total("cambios esperando el pago", changes);
    }

    /// <summary>Cifra propia del catálogo para la conciliación, que la muestra sin compararla.</summary>
    private void Total(string key, long count) => Figures[("catalog", key)] = (count, 0m);

    /// <summary>Precios de una fila de tblpricing según cómo se vende: "free", "onetime", "recurring" o un ciclo fijo.</summary>
    private static IEnumerable<(BillingCycle Cycle, decimal Amount, decimal Setup)> Offers(DumpRow r, string kind) => kind switch
    {
        "free" => [(BillingCycle.Free, 0m, 0m)],
        "onetime" => Dec(r, "monthly") >= 0 ? [(BillingCycle.OneTime, Dec(r, "monthly"), Math.Max(0m, Dec(r, "msetupfee")))] : [],
        "recurring" => PriceColumns.Where(c => Dec(r, c.Price) >= 0).Select(c => (c.Cycle, Dec(r, c.Price), Math.Max(0m, Dec(r, c.Setup)))),
        _ => PriceColumns.Where(c => c.Cycle.Code == kind && Dec(r, c.Price) >= 0).Select(c => (c.Cycle, Dec(r, c.Price), Math.Max(0m, Dec(r, c.Setup)))),
    };

    /// <summary>
    /// La clave de módulo de una opción: lo que va antes de la barra en su nombre, o lo que diga el mapa de módulos para
    /// el servertype de sus productos. Si dos servertypes la mapean distinto, queda la del origen y sale en el reporte.
    /// </summary>
    private string? ModuleKey(long optionId, string? sourceKey, IEnumerable<long> productIds)
    {
        if (sourceKey is null)
        {
            return null;
        }

        var mapped = productIds.Select(p => Blank(Str(productRows[p], "servertype"))).OfType<string>().Distinct(StringComparer.Ordinal)
            .Select(t => modules.GetValueOrDefault(t)?.Options.GetValueOrDefault(sourceKey))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (mapped.Count > 1)
        {
            Incidents.Add($"Opción {optionId}: el mapa de módulos le da claves distintas ({string.Join(", ", mapped)}), queda {sourceKey}.");
        }

        var key = mapped.Count == 1 ? Blank(mapped[0]) : sourceKey;
        return key is not null && BillingOss.Domain.Catalog.Option.IsValidModuleKey(key) ? key : null;
    }

    /// <summary>Renglones de un tipo en facturas sin cobrar, en gestión de cobro o anuladas: períodos ya facturados.</summary>
    private HashSet<(long Relid, DateOnly Due)> Invoiced(string type)
    {
        var status = Rows("tblinvoices").ToDictionary(r => Long(r, "id"), r => Str(r, "status") ?? "Unpaid");
        return [.. Rows("tblinvoiceitems")
            .Where(i => Str(i, "type") == type && Date(i, "duedate") is not null
                && status.GetValueOrDefault(Long(i, "invoiceid"), "Unpaid") is "Unpaid" or "Collections" or "Cancelled")
            .Select(i => (Long(i, "relid"), Date(i, "duedate")!.Value))];
    }

    /// <summary>
    /// El anclaje es el día del vencimiento vigente: el origen factura ese día aunque el alta haya sido otro. El día del
    /// alta queda solo si el vencimiento cayó en el último día de un mes más corto (un 31 en 30).
    /// </summary>
    private static int Anchor(DateOnly? registered, DateOnly? due) => due is { } d
        ? registered is { } reg && reg.Day > d.Day && d.Day == DateTime.DaysInMonth(d.Year, d.Month) ? reg.Day : d.Day
        : registered?.Day ?? 1;

    /// <summary>El próximo período a facturar: el siguiente si el que empieza en lo pagado ya tiene renglón facturado.</summary>
    private static DateOnly? NextToBill(BillingCycle cycle, int anchor, DateOnly? paidThrough, DateOnly? nextInvoice, bool paidPeriodInvoiced)
    {
        var next = nextInvoice ?? paidThrough;
        return cycle.IsRecurring && paidThrough is { } paid && next <= paid && paidPeriodInvoiced
            ? BillingSchedule.PeriodStartingAt(paid, cycle, anchor).End
            : next;
    }

    private static ServiceStatus StatusOf(string? source) => source switch
    {
        "Active" => ServiceStatus.Active,
        "Suspended" => ServiceStatus.Suspended,
        "Terminated" or "Completed" => ServiceStatus.Terminated,
        "Cancelled" or "Fraud" => ServiceStatus.Cancelled,
        _ => ServiceStatus.Pending,
    };

    /// <summary>Un ciclo del origen escrito de cualquier forma ("monthly", "Semi-Annually", "One Time"), o nulo si no es de servicios.</summary>
    private static BillingCycle? LooseCycle(string? text) => (text ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant() switch
    {
        "MONTHLY" => BillingCycle.Monthly,
        "QUARTERLY" => new BillingCycle(CycleUnit.Month, 3),
        "SEMIANNUALLY" => new BillingCycle(CycleUnit.Month, 6),
        "ANNUALLY" => BillingCycle.Annually,
        "BIENNIALLY" => new BillingCycle(CycleUnit.Year, 2),
        "TRIENNIALLY" => new BillingCycle(CycleUnit.Year, 3),
        "ONETIME" => BillingCycle.OneTime,
        "FREE" or "FREEACCOUNT" => BillingCycle.Free,
        _ => null,
    };

    /// <summary>"técnico|visible" en sus dos partes. Sin barra no hay parte técnica.</summary>
    private static (string? Technical, string? Display) Split(string? text)
    {
        if (Blank(text) is not { } value)
        {
            return (null, null);
        }

        var bar = value.IndexOf('|', StringComparison.Ordinal);
        if (bar < 0)
        {
            return (null, value);
        }

        var technical = Blank(value[..bar]);
        return (technical, Blank(value[(bar + 1)..]) ?? technical);
    }

    private static List<string> List(string? text) => [.. (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// Cuándo se aprovisiona lo que se pide: "order" al pedir, "payment" al pagar, y "on" (al aceptar el pedido) o vacío
    /// (nunca solo) quedan para el staff, que lo acepta. Un origen sin la columna queda al pagar, lo de siempre.
    /// </summary>
    internal static ProductActivation Activation(string? setup) => setup?.Trim().ToLowerInvariant() switch
    {
        null or "payment" => ProductActivation.OnPayment,
        "order" => ProductActivation.OnOrder,
        _ => ProductActivation.Manual,
    };

    private static string? Cut(string? text, int max) => text is null || text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");

    /// <summary>Texto sin HTML: los cortes de línea y de párrafo quedan como saltos, las etiquetas y las entidades no.</summary>
    private static string? Text(string? html)
    {
        if (Blank(html) is not { } text)
        {
            return null;
        }

        text = System.Net.WebUtility.HtmlDecode(Tags().Replace(Breaks().Replace(text, "\n"), string.Empty));
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        return Blank(string.Join('\n', lines));
    }

    [GeneratedRegex(@"<br\s*/?>|</p\s*>|</li\s*>|</div\s*>|</h\d\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();
}
