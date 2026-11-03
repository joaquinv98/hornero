using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Fiscal;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Importer.Import;

/// <summary>
/// Importa la copia de la base del sistema anterior al modelo propio (D-17). El mapeo sale de la estructura de la
/// copia del cliente. Conserva los IDs de cuentas, productos, servicios, pedidos, facturas, departamentos y tickets, y
/// los números de factura, de comprobante y de ticket. Nunca escribe en el origen. Corre sobre una base vacía.
/// </summary>
public sealed partial class SourceImport(BillingDbContext db, TextWriter log, IReadOnlyDictionary<string, ModuleMapping>? modules = null)
{
    private readonly IReadOnlyDictionary<string, ModuleMapping> modules = modules ?? new Dictionary<string, ModuleMapping>();

    private static readonly HashSet<string> Needed = new(StringComparer.OrdinalIgnoreCase)
    {
        "tblcurrencies", "tblclients", "tblcustomfields", "tblcustomfieldsvalues", "tblproductgroups", "tblproducts",
        "tblpricing", "tblhosting", "tblorders", "tblinvoices", "tblinvoiceitems", "tblaccounts", "mod_afip_invoices",
        "tbladdonmodules", "tblconfiguration", "tbldomains", "tbldomainpricing", "tbldomainsadditionalfields", "tblcancelrequests", "tblhostingconfigoptions",
        "tblproductconfigoptions", "tblproductconfigoptionssub", "tblbillingnotes", "tblbillingnoteitems",
        "tblbannedemails", "tblbannedips", "tblauthn_account_links", "tblauthn_config",
        "tblticketdepartments", "tbltickets", "tblticketreplies", "tblticketnotes", "tblticketpredefinedreplies", "tblticketpredefinedcats",
        "tblticketspamfilters", "tblticketescalations", "tblticketfeedback", "tblticketstatuses", "tbladmins",
        "tblknowledgebasecats", "tblknowledgebase", "tblknowledgebaselinks", "tblknowledgebasetags", "tblknowledgebase_images",
        "tblannouncements", "tbldownloadcats", "tbldownloads", "tblproduct_downloads", "tblnetworkissues",
        "tblproductconfiggroups", "tblproductconfiglinks", "tbladdons", "tblhostingaddons", "tblpromotions",
        "tblproduct_upgrade_products", "tblupgrades", "mod_pruebas_gratis",
    };

    private static readonly Dictionary<string, string> ExportLanguages = new() { ["1"] = "es", ["2"] = "en", ["3"] = "pt" };

    private static readonly Dictionary<string, int> VatConditions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Consumidor Final"] = 5,
        ["Responsable Inscripto"] = 1,
        ["Monotributista"] = 6,
        ["Exento"] = 4,
        ["Sujeto No Categorizado"] = 7,
        ["IVA No Alcanzado"] = 15,
        ["Cliente del Exterior"] = 9,
    };

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

    /// <summary>Registrars del origen que tienen módulo (BillingOss.Module.Registrars). Los demás van al registrar manual.</summary>
    private static readonly HashSet<string> RegistrarModules = new(StringComparer.Ordinal) { "internetbs", "resellerclub", "openprovider", "namecheap" };

    private readonly Dictionary<string, List<DumpRow>> rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, Currency> accountCurrency = [];
    private readonly Dictionary<long, (long Instance, string Params)> manualProducts = [];
    private long? manualInstance;
    private readonly HashSet<long> products = [];
    private readonly HashSet<long> services = [];
    private readonly HashSet<long> domains = [];
    private readonly Dictionary<long, (Currency Currency, string SourceStatus)> invoices = [];
    private readonly Dictionary<long, decimal> gross = [];
    private readonly Dictionary<long, decimal> noteCredits = [];
    private readonly Dictionary<long, decimal> invoiceAllocated = [];
    private readonly Dictionary<long, decimal> allocatedByInvoice = [];
    private readonly Dictionary<long, long> noteDocuments = [];
    private readonly Dictionary<DocumentKind, long> usedNoteNumbers = [];
    private readonly Dictionary<long, DumpRow> productRows = [];
    private readonly Dictionary<(long Field, long Relid), string> fieldValues = [];
    private readonly Dictionary<string, long> instances = new(StringComparer.Ordinal);
    private readonly HashSet<string> secretFields = new(StringComparer.Ordinal);
    private readonly DateTimeOffset now = DateTimeOffset.UtcNow;

    public List<string> Incidents { get; } = [];

    /// <summary>Dominios propios del proveedor: sus subdominios van a la instancia de subdominios propios, con la zona en external_ids.</summary>
    public IReadOnlyList<string> OwnZones { get; init; } = [];

    /// <summary>
    /// Día de la copia, en la zona del proveedor: lo que vencía hasta ese día y el origen no hizo es una acción que el
    /// origen dejó sin ejecutar. Por defecto, hoy. El paralelo pasa el día del volcado previo al ciclo.
    /// </summary>
    public DateOnly AsOf { get; init; } = Today();

    /// <summary>
    /// Modo sombra, para el paralelo: las instancias que nombra el mapa y no existen se crean sin URL, así los servicios
    /// quedan atados y ningún módulo real se llama. Si una ya existe con URL, la importación se frena.
    /// </summary>
    public bool ShadowInstances { get; init; }

    /// <summary>La carpeta de adjuntos del soporte del sistema anterior, copiada de su servidor. Sin ella, entran sin el archivo.</summary>
    public string? SupportFiles { get; init; }

    /// <summary>Lo que se importó, para la conciliación: sección y clave, con cantidad e importe.</summary>
    public Dictionary<(string Section, string Key), (long Count, decimal Amount)> Figures { get; } = [];

    /// <summary>Hoy en la zona del proveedor.</summary>
    public static DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone));

    public async Task Load(DumpReader dump, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dump);
        await foreach (var row in dump.ReadRowsAsync(ct).ConfigureAwait(false))
        {
            if (Needed.Contains(row.Table.Name) || AffiliateTables.Contains(row.Table.Name))
            {
                (rows.TryGetValue(row.Table.Name, out var list) ? list : rows[row.Table.Name] = []).Add(row);
            }
        }
    }

    public async Task Run(CancellationToken ct)
    {
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        var currencies = Rows("tblcurrencies").ToDictionary(r => Long(r, "id"), r => new Currency(Str(r, "code")!.ToUpperInvariant()));
        foreach (var r in Rows("tblcustomfieldsvalues"))
        {
            fieldValues[(Long(r, "fieldid"), Long(r, "relid"))] = Str(r, "value") ?? string.Empty;
        }

        foreach (var r in Rows("tblinvoices"))
        {
            gross[Long(r, "id")] = Dec(r, "subtotal") + Dec(r, "tax") + Dec(r, "tax2");
        }

        // Lo que las notas de crédito nativas del origen acreditaron a cada factura: la cabecera de la factura lo
        // necesita antes de que las notas existan como documentos.
        var itemInvoice = Rows("tblinvoiceitems").ToDictionary(i => Long(i, "id"), i => Long(i, "invoiceid"));
        var notes = Rows("tblbillingnotes").Where(r => Str(r, "status") != "draft" && Str(r, "note_type") == "credit").ToDictionary(r => Long(r, "id"), r => Dec(r, "total"));
        foreach (var g in Rows("tblbillingnoteitems").Where(i => Str(i, "type") == "invoice_item" && notes.ContainsKey(Long(i, "billingnote_id"))).GroupBy(i => Long(i, "billingnote_id")))
        {
            if (itemInvoice.GetValueOrDefault(Long(g.First(), "relid")) is > 0 and var invoiceId)
            {
                noteCredits[invoiceId] = noteCredits.GetValueOrDefault(invoiceId) + notes[g.Key];
            }
        }

        await Instances(ct).ConfigureAwait(false);
        await Accounts(currencies, ct).ConfigureAwait(false);
        await Catalog(currencies, ct).ConfigureAwait(false);
        await OptionCatalog(currencies, ct).ConfigureAwait(false);
        await Addons(currencies, ct).ConfigureAwait(false);
        await Coupons(ct).ConfigureAwait(false);
        await Services(ct).ConfigureAwait(false);
        await AddonServices(ct).ConfigureAwait(false);
        await Domains(currencies, ct).ConfigureAwait(false);
        await Invoices(currencies, ct).ConfigureAwait(false);
        await Notes(itemInvoice, ct).ConfigureAwait(false);
        await Orders(ct).ConfigureAwait(false);
        await PendingChanges(ct).ConfigureAwait(false);
        await AppliedCredit(ct).ConfigureAwait(false);
        await Payments(currencies, ct).ConfigureAwait(false);
        await Credit(ct).ConfigureAwait(false);
        await Fiscal(ct).ConfigureAwait(false);
        await Support(ct).ConfigureAwait(false);
        await Affiliates(currencies, ct).ConfigureAwait(false);
        await Contents(ct).ConfigureAwait(false);
        await Sequences(ct).ConfigureAwait(false);
        await Blocklists(ct).ConfigureAwait(false);
        await ExternalLogins(ct).ConfigureAwait(false);
        await log.WriteLineAsync($"Monedas del sistema anterior para la API compatible: {JsonSerializer.Serialize(currencies.ToDictionary(c => c.Key.ToString(CultureInfo.InvariantCulture), c => c.Value.Code))}").ConfigureAwait(false);
        await log.WriteLineAsync($"Ajustes equivalentes para la sección Billing: {BillingSettings().ToJsonString()}").ConfigureAwait(false);
        await log.WriteLineAsync($"Ajustes de seguridad equivalentes, para cargar en /admin/seguridad/ajustes: {SecuritySettingsReport().ToJsonString()}").ConfigureAwait(false);
    }

    private async Task Accounts(Dictionary<long, Currency> currencies, CancellationToken ct)
    {
        var fields = Rows("tblcustomfields").Where(r => Str(r, "type") == "client").ToDictionary(r => Str(r, "fieldname")!, r => Long(r, "id"));
        var docField = fields.GetValueOrDefault("CUIT/CUIL/DNI");
        var vatField = fields.GetValueOrDefault("Condicion ante el IVA");

        // El correo es único por cuenta sin distinguir mayúsculas. Si el origen lo repite, lo conserva la cuenta activa
        // más vieja y las demás quedan sin correo, con incidencia para que el staff decida.
        var keeper = Rows("tblclients")
            .Where(r => Blank(Str(r, "email")) is not null)
            .GroupBy(r => Str(r, "email")!.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => Str(r, "status") switch { "Active" => 0, "Inactive" => 1, _ => 2 }).ThenBy(r => Long(r, "id")).Select(r => Long(r, "id")).First(), StringComparer.Ordinal);

        // Marca de cortesía de un addon fiscal del origen: se busca por prefijo, como la busca el addon.
        var noFiscalField = fields.Where(f => f.Key.StartsWith("Sin comprobante fiscal", StringComparison.Ordinal)).Select(f => f.Value).FirstOrDefault();
        if (noFiscalField > 0)
        {
            Figures.TryAdd(("customfield_values", $"{noFiscalField}\ton"), (0, 0m));
        }

        var pending = 0;
        foreach (var r in Rows("tblclients"))
        {
            var id = Long(r, "id");
            var currency = currencies.GetValueOrDefault(Long(r, "currency"), Currency.Ars);
            var created = Instant(Date(r, "datecreated")) ?? now;
            var name = $"{Str(r, "firstname")} {Str(r, "lastname")}".Trim();
            var company = Blank(Str(r, "companyname"));
            var email = Str(r, "email")?.Trim() ?? string.Empty;
            if (keeper.TryGetValue(email.ToLowerInvariant(), out var owner) && owner != id)
            {
                Incidents.Add($"Cuenta {id}: tenía el mismo correo que la cuenta {owner}, queda sin correo hasta que el staff lo resuelva.");
                email = string.Empty;
            }

            var account = new Account(name.Length > 0 ? name : company ?? Blank(email) ?? $"Cuenta {id}", email, currency, created, Language(Str(r, "language")))
            {
                CompanyName = company,
                Phone = Blank(Str(r, "phonenumber")),
                Status = Str(r, "status") switch { "Active" => AccountStatus.Active, "Closed" => AccountStatus.Closed, _ => AccountStatus.Inactive },
                NoFiscalDocument = noFiscalField > 0 && fieldValues.GetValueOrDefault((noFiscalField, id)) == "on",
            };
            if (account.NoFiscalDocument)
            {
                Count("customfield_values", $"{noFiscalField}\ton");
            }

            account.SetImportedId(id);
            db.Accounts.Add(account);
            accountCurrency[id] = currency;
            Count("accounts_by_status", Str(r, "status") ?? string.Empty);

            var digits = new string((fieldValues.GetValueOrDefault((docField, id)) ?? Str(r, "tax_id") ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
            var country = Str(r, "country") is { Length: 2 } c ? c.ToUpperInvariant() : "AR";

            // Del exterior el número va tal cual como identificación tributaria de su país.
            var docType = country != "AR" ? (digits.Length > 0 ? TaxDocumentType.Foreign : TaxDocumentType.None)
                : digits.Length == 11 && TaxIdentity.IsValidCuit(digits) ? TaxDocumentType.Cuit
                : digits.Length is 7 or 8 ? TaxDocumentType.Dni
                : TaxDocumentType.None;
            // Del exterior la condición es siempre 9, como la puso el addon en sus comprobantes E, diga lo que diga el campo.
            var field = VatConditions.TryGetValue(fieldValues.GetValueOrDefault((vatField, id)) ?? string.Empty, out var code) ? code : (int?)null;
            var vat = country != "AR" ? 9 : field ?? 5;
            if (country != "AR" && field is { } other && other != 9)
            {
                Incidents.Add($"Cuenta {id}: del exterior ({country}) con el campo de condición de IVA en {other}, queda con 9 (no alcanzado).");
            }

            // La provincia va como código ISO 3166-2 (sección 3.1 del diseño fiscal): el texto libre del origen se
            // normaliza, y lo que no se reconoce queda tal cual, contado, para corregirlo desde la ficha. Solo se
            // cuenta: es texto que escribió el cliente y la conciliación va al log de cada importación.
            var state = Blank(Str(r, "state"));
            var province = country == "AR" ? ArgentinaProvinces.Code(state) : null;
            if (country == "AR" && state is not null && province is null)
            {
                Count("provinces_unmapped", "cuentas");
            }

            db.TaxIdentities.Add(new TaxIdentity(id, docType, docType == TaxDocumentType.None ? string.Empty : digits, company ?? account.Name, vat, country, created)
            {
                Province = province ?? state,
                City = Blank(Str(r, "city")),
                Street = Blank(string.Join(", ", new[] { Blank(Str(r, "address1")), Blank(Str(r, "address2")) }.OfType<string>())),
                PostalCode = Blank(Str(r, "postcode")),
            });
            if (digits.Length > 0 && docType == TaxDocumentType.None)
            {
                Incidents.Add($"Cuenta {id}: documento con forma desconocida ({digits.Length} dígitos), queda sin documento.");
            }

            if (++pending >= 1000)
            {
                await Flush(ct).ConfigureAwait(false);
                pending = 0;
            }
        }

        await Flush(ct).ConfigureAwait(false);
        var unmapped = Figures.Where(f => f.Key.Section == "provinces_unmapped").Sum(f => f.Value.Count);
        if (unmapped > 0)
        {
            Incidents.Add($"{unmapped} cuentas de Argentina con una provincia que no es un código ISO 3166-2: quedan con el texto del origen y se corrigen desde la ficha (Impuestos las avisa).");
        }

        await log.WriteLineAsync($"Cuentas: {accountCurrency.Count}").ConfigureAwait(false);
    }

    private async Task Catalog(Dictionary<long, Currency> currencies, CancellationToken ct)
    {
        // Los grupos conservan su ID, su orden y si se muestran.
        var groups = new HashSet<long>();
        foreach (var r in Rows("tblproductgroups"))
        {
            var group = new ProductGroup
            {
                Name = Html(Str(r, "name")) ?? $"Grupo {Long(r, "id")}",
                Headline = Html(Blank(Str(r, "headline"))),
                Visible = Long(r, "hidden") == 0,
                SortOrder = (int)Long(r, "order"),
            };
            group.SetImportedId(Long(r, "id"));
            db.ProductGroups.Add(group);
            groups.Add(group.Id);
        }

        await Flush(ct).ConfigureAwait(false);
        var kinds = new Dictionary<long, string>();
        var hiddenGroups = Rows("tblproductgroups").Where(r => Long(r, "hidden") == 1).Select(r => Long(r, "id")).ToHashSet();
        var sourceProducts = Rows("tblproducts").Select(r => Long(r, "id")).ToHashSet();
        var upgradesTo = Rows("tblproduct_upgrade_products")
            .Where(r => sourceProducts.Contains(Long(r, "upgrade_product_id")))
            .ToLookup(r => Long(r, "product_id"), r => Long(r, "upgrade_product_id"));

        // Los productos sin módulo van a la instancia manual del núcleo, que abre tareas para el staff. También los
        // servicios de un servidor que el mapa marca como manual.
        long? manual = Rows("tblproducts").Any(r => Blank(Str(r, "servertype")) is not { } m || modules.GetValueOrDefault(m)?.Manual == true) || modules.Values.Any(m => m.Manual)
            ? await EnsureInstance("servicios-manuales", ct).ConfigureAwait(false)
            : null;
        manualInstance = manual;
        foreach (var r in Rows("tblproducts"))
        {
            var id = Long(r, "id");
            var module = Blank(Str(r, "servertype"));

            // El producto toma la entrada general de su servertype en el mapa de módulos. Los que no tienen módulo, o
            // que el mapa marca como manuales, van al módulo manual del núcleo, que abre tareas para el staff.
            var mapping = module is null ? null : modules.GetValueOrDefault(module);
            var isManual = module is null || mapping?.Manual == true;
            var paytype = Str(r, "paytype") ?? "recurring";
            var product = new Product
            {
                Name = Html(Str(r, "name")) ?? $"Producto {id}",
                Description = Cut(Text(Str(r, "description")), 4000),
                GroupId = groups.Contains(Long(r, "gid")) ? Long(r, "gid") : null,
                UpgradeProductIds = [.. upgradesTo[id].Distinct()],
                OptionChanges = Long(r, "configoptionsupgrade") == 1,
                SortOrder = (int)Long(r, "order"),
                AllowQuantity = Long(r, "allowqty") == 1,
                Stock = Long(r, "stockcontrol") == 1 ? (int)Math.Max(0, Long(r, "qty")) : null,
                Kind = isManual ? ProductKind.Manual : ProductKind.Provisioned,
                // Un producto visible dentro de un grupo oculto tampoco está a la venta.
                Visible = Long(r, "hidden") == 0 && Long(r, "retired") == 0 && !hiddenGroups.Contains(Long(r, "gid")),
                TaxCategory = Long(r, "tax") == 1 ? TaxCategories.Digital : TaxCategories.Untaxed,
                ModuleInstanceId = isManual ? manual : Instance(mapping),
                ModuleParams = (isManual ? ManualParams(r, paytype) : ProductParams(r, mapping)).ToJsonString(),
                TerminationDays = Long(r, "autoterminatedays") is var days && days > 0 ? (int)days : null,

                // Lo que va al módulo manual se crea al pagar o al pedir: el módulo ya decide si abre una tarea para el staff.
                Activation = isManual ? (Str(r, "autosetup") == "order" ? ProductActivation.OnOrder : ProductActivation.OnPayment) : Activation(Str(r, "autosetup")),
            };
            product.SetImportedId(id);
            db.Products.Add(product);
            products.Add(id);
            productRows[id] = r;
            kinds[id] = paytype;
            if (isManual)
            {
                manualProducts[id] = (manual!.Value, product.ModuleParams);
            }
        }

        await Flush(ct).ConfigureAwait(false);

        var plans = new Dictionary<(long, CycleUnit, int), Plan>();
        var prices = new List<(Plan Plan, Currency Currency, decimal Amount, decimal Setup)>();
        foreach (var r in Rows("tblpricing").Where(r => Str(r, "type") == "product" && products.Contains(Long(r, "relid"))))
        {
            var productId = Long(r, "relid");
            if (!currencies.TryGetValue(Long(r, "currency"), out var currency))
            {
                continue;
            }

            foreach (var (cycle, amount, setup) in Offers(r, kinds[productId] is "free" or "onetime" ? kinds[productId] : "recurring"))
            {
                var key = (productId, cycle.Unit, cycle.Count);
                if (!plans.TryGetValue(key, out var plan))
                {
                    plan = new Plan(productId, cycle);
                    plans[key] = plan;
                    db.Plans.Add(plan);
                }

                prices.Add((plan, currency, amount, setup));
            }
        }

        await Flush(ct).ConfigureAwait(false);
        foreach (var (plan, currency, amount, setup) in prices.DistinctBy(p => (p.Plan.Id, p.Currency)))
        {
            // El origen publica precios con IVA incluido: cada precio y cada servicio lo congelan (sección 3.4 del diseño fiscal).
            db.PlanPrices.Add(new PlanPrice(plan.Id, currency, amount, setup, new DateOnly(2000, 1, 1), PriceMode.TaxIncluded));
        }

        // El precio de la mayoría de estos productos sale de sus opciones configurables: el reporte avisa de los que no tienen.
        priceless.UnionWith(prices.GroupBy(p => p.Plan.ProductId).Where(g => g.All(p => p.Amount <= 0m)).Select(g => g.Key));

        db.TaxRules.Add(new TaxRule { Country = "AR", Outcome = TaxTreatment.Taxed, Rate = 0.21m, ValidFrom = new DateOnly(2000, 1, 1) });
        db.TaxRules.Add(new TaxRule { Outcome = TaxTreatment.Exempt, ValidFrom = new DateOnly(2000, 1, 1) });
        await Flush(ct).ConfigureAwait(false);
        await log.WriteLineAsync($"Productos: {products.Count}, planes: {plans.Count}").ConfigureAwait(false);
    }

    private async Task Services(CancellationToken ct)
    {
        // Períodos que el origen ya facturó, aunque la factura siga impaga o se haya anulado: renglón de servicio
        // con el vencimiento del período. Sin facturación continua, el origen no adelanta la próxima fecha a facturar
        // al facturar: la adelanta recién al cobrar, y evita repetir por este renglón.
        // Solo cuentan las facturas sin cobrar o anuladas: un renglón pagado en el vencimiento es un vencimiento que el
        // origen no avanzó al cobrar, y se avisa.
        var invoiceStatus = Rows("tblinvoices").ToDictionary(r => Long(r, "id"), r => Str(r, "status") ?? "Unpaid");
        var invoiced = Invoiced("Hosting");
        var paidAtDue = Rows("tblinvoiceitems")
            .Where(i => Str(i, "type") == "Hosting" && Date(i, "duedate") is not null && invoiceStatus.GetValueOrDefault(Long(i, "invoiceid"), "Unpaid") == "Paid")
            .Select(i => (Service: Long(i, "relid"), Due: Date(i, "duedate")!.Value))
            .ToHashSet();

        // Solicitudes de baja del origen: la última por servicio.
        var cancelRequests = Rows("tblcancelrequests")
            .GroupBy(r => Long(r, "relid"))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => DateTimeValue(r, "date")).ThenByDescending(r => Long(r, "id")).First());
        var selections = Rows("tblhostingconfigoptions").GroupBy(r => Long(r, "relid")).ToDictionary(g => g.Key, g => g.ToList());

        // Las pruebas gratis del origen están en una tabla propia. El servicio que la tuvo guarda su fin, así la
        // cuenta no pide otra del mismo producto: una por cuenta y producto del servicio, la última, como pide el índice.
        var hosting = Rows("tblhosting").ToDictionary(r => Long(r, "id"), r => (Account: Long(r, "userid"), Product: Long(r, "packageid")));
        var trials = Rows("mod_pruebas_gratis")
            .Where(t => hosting.ContainsKey(Long(t, "serviceid")) && Date(t, "vence_en") is not null)
            .GroupBy(t => hosting[Long(t, "serviceid")])
            .Select(g => g.MaxBy(t => (Date(t, "vence_en")!.Value, Long(t, "id")))!)
            .ToDictionary(t => Long(t, "serviceid"), t => Date(t, "vence_en")!.Value);
        var skippedChoices = 0;
        var outOfRange = 0;
        var chosenRows = 0;
        var defaultTermination = BillingSettings()["TerminationDays"]?.GetValue<int>();
        var pending = 0;
        var paidRows = 0;
        var limitedPromos = 0;
        var held = new List<(long Id, string Reason, string Status)>();
        var bound = 0;
        var unbound = new Dictionary<string, int>(StringComparer.Ordinal);
        Figures.TryAdd(("services_anchor", "alta_en_otro_dia"), (0, 0m));
        Figures.TryAdd(("services_anchor", "fuera_del_vencimiento"), (0, 0m));
        var servers = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        foreach (var r in Rows("tblhosting"))
        {
            var id = Long(r, "id");
            var accountId = Long(r, "userid");
            var productId = Long(r, "packageid");
            if (!accountCurrency.TryGetValue(accountId, out var currency) || !products.Contains(productId))
            {
                Incidents.Add($"Servicio {id}: la cuenta {accountId} o el producto {productId} no existe, no se importa.");
                continue;
            }

            var cycle = Cycle(Str(r, "billingcycle"));
            var quantity = (int)Math.Max(1, Long(r, "qty"));
            var registered = Date(r, "regdate");

            // El próximo período a facturar es nextinvoicedate y lo pagado llega hasta nextduedate. Si el período que
            // empieza en nextduedate ya tiene renglón facturado, el próximo a facturar es el siguiente.
            var paidThrough = Date(r, "nextduedate");
            var anchor = Anchor(registered, paidThrough ?? Date(r, "nextinvoicedate"));
            var nextDue = NextToBill(cycle, anchor, paidThrough, Date(r, "nextinvoicedate"), paidThrough is { } paid && invoiced.Contains((id, paid)));

            var sourceStatus = Str(r, "domainstatus") ?? "Pending";
            Count("services_by_status", sourceStatus, Dec(r, "amount"));
            if (cycle.IsRecurring && sourceStatus is "Active" or "Suspended" && paidThrough is { } dueDate && paidAtDue.Contains((id, dueDate)))
            {
                paidRows++;
            }

            // Baja pedida y no ejecutada: a fin de período queda programada para el vencimiento, e inmediata para el día
            // que se pidió. El servicio queda en su estado del origen, porque el servidor sigue andando, y no se renueva.
            DateOnly? cancelAt = null;
            string? overdue = null;
            if (sourceStatus is "Active" or "Suspended" && cancelRequests.TryGetValue(id, out var request))
            {
                var type = Str(request, "type") ?? string.Empty;
                Count("cancel_requests_pending_by_type", type);
                var requested = DateTimeValue(request, "date") is { } at ? DateOnly.FromDateTime(at) : AsOf;
                cancelAt = type == "Immediate" ? requested : paidThrough;
                if (cancelAt <= AsOf)
                {
                    overdue = type == "Immediate"
                        ? $"Baja inmediata pedida el {requested:yyyy-MM-dd} que el sistema anterior no ejecutó."
                        : $"Baja a fin de período pedida para el {cancelAt:yyyy-MM-dd} que el sistema anterior no ejecutó.";
                }
            }

            // El importe del origen viene con la promoción recurrente adentro. Una de porcentaje para siempre queda como el
            // descuento del servicio sobre el precio lleno: cobra lo mismo y una opción que se agregue también lo lleva. Una
            // por períodos queda con el importe neto, porque no se sabe cuántos le quedan.
            var promo = coupons.GetValueOrDefault(Long(r, "promoid"));
            decimal? forever = promo is { Kind: DiscountKind.Percent, Recurring: true, RecurringCycles: null, Value: > 0m and < 100m } ? promo.Value : null;
            if (promo is { Recurring: true, RecurringCycles: not null } && sourceStatus is "Active" or "Suspended")
            {
                limitedPromos++;
            }

            var suspendReason = Blank(Str(r, "suspendreason"));
            var service = new Service(accountId, productId, cycle, currency, Dec(r, "amount") / quantity / (1m - ((forever ?? 0m) / 100m)), quantity, Instant(registered) ?? now, PriceMode.TaxIncluded)
            {
                Label = Blank(Str(r, "domain")),
                PaymentMethod = Blank(Str(r, "paymentmethod")),
                CancelAt = cancelAt,
                // Sin motivo (o con el de la mora del origen) es una suspensión por mora y la levanta el pago. Con motivo propio, el staff.
                SuspendReason = sourceStatus == "Suspended" && suspendReason is not null && suspendReason != "Overdue on Payment" ? suspendReason : null,
                NoAutoSuspend = Long(r, "overideautosuspend") == 1 || Date(r, "overidesuspenduntil") is { } until && until > AsOf,
                CouponId = promo is null ? null : Long(r, "promoid"),
                DiscountPercent = forever,
                TrialEndsOn = trials.TryGetValue(id, out var trialEnd) ? trialEnd : null,
            };
            if (manualProducts.TryGetValue(productId, out var manual))
            {
                // Los manuales no tienen nada externo que identificar: el staff carga su referencia al cerrar la tarea.
                service.ModuleInstanceId = manual.Instance;
                service.ModuleParams = manual.Params;
                service.ExternalIds = "{}";
            }

            service.SetImportedId(id);

            // Baja por mora que ya vencía en la copia y el origen no hizo. Como la pedida que vencía, queda retenida en
            // una tarea de revisión del staff: billing-oss no la ejecuta sola el primer día.
            var terminationDays = Long(productRows[productId], "autoterminatedays") is var days && days > 0 ? (int)days : defaultTermination;
            if (overdue is null && cancelAt is null && sourceStatus is "Active" or "Suspended" && cycle.IsRecurring && Dec(r, "amount") > 0m
                && paidThrough is { } from && terminationDays is { } termination && from.AddDays(termination) <= AsOf)
            {
                overdue = $"Baja por mora que vencía el {from.AddDays(termination):yyyy-MM-dd} y el sistema anterior no ejecutó.";
            }

            if (overdue is not null)
            {
                held.Add((id, overdue, sourceStatus));
            }

            // Los vivos quedan atados a la instancia de módulo del mapa, con sus parámetros e IDs externos. Los
            // terminados quedan como historia, sin vínculo.
            var module = Blank(Str(productRows[productId], "servertype"));
            if (sourceStatus is "Active" or "Suspended" or "Pending" && module is not null)
            {
                var server = Long(r, "server");
                var mapping = modules.GetValueOrDefault($"{module}:{server}") ?? modules.GetValueOrDefault(module);
                if (Instance(mapping) is { } instance)
                {
                    service.ModuleInstanceId = instance;
                    service.ModuleParams = ProductParams(productRows[productId], mapping).ToJsonString();
                    service.ExternalIds = ExternalIds(r, id, productId, mapping!).ToJsonString();
                    bound++;
                    if (!modules.ContainsKey($"{module}:{server}"))
                    {
                        (servers.TryGetValue(module, out var set) ? set : servers[module] = []).Add(server);
                    }
                }
                else if (mapping?.Manual == true && manualInstance is { } manualId)
                {
                    // Un servidor que el mapa marca como manual: sus servicios van al módulo manual, como un producto sin módulo.
                    service.ModuleInstanceId = manualId;
                    service.ModuleParams = ManualParams(productRows[productId], Str(productRows[productId], "paytype") ?? "recurring").ToJsonString();
                    service.ExternalIds = "{}";
                }
                else
                {
                    unbound[module] = unbound.GetValueOrDefault(module) + 1;
                }
            }

            // Lo elegido de cada opción queda en el servicio. En los vivos además va a los parámetros del módulo, cada
            // opción en su clave, como en un pedido nuevo.
            var chosen = selections.TryGetValue(id, out var selection) ? Choices(selection, ref skippedChoices) : [];
            db.ServiceOptions.AddRange(chosen.Select(c => new ServiceOption(id, c.Option.Id, c.Value.Id, c.Quantity)));
            chosenRows += chosen.Count;
            if (sourceStatus is "Active" or "Suspended" or "Pending")
            {
                service.ModuleParams = BillingOss.Domain.Catalog.OptionCatalog.ModuleParams(service.ModuleParams, chosen);
                liveChoices[id] = chosen;
                outOfRange += chosen.Count(c => c.Option.Kind == OptionKind.Quantity && (c.Quantity < c.Option.MinQuantity || (c.Option.MaxQuantity > 0 && c.Quantity > c.Option.MaxQuantity)));
            }

            service.ImportState(
                sourceStatus switch
                {
                    "Active" => ServiceStatus.Active,
                    "Suspended" => ServiceStatus.Suspended,
                    "Terminated" or "Completed" => ServiceStatus.Terminated,
                    "Cancelled" or "Fraud" => ServiceStatus.Cancelled,
                    _ => ServiceStatus.Pending,
                },
                anchor,
                nextDue,
                paidThrough,
                Instant(registered),
                Instant(Date(r, "termination_date")));
            AnchorFigures(service, registered);

            // Para la conciliación: los vivos por mes de vencimiento, y los que ya tienen facturado sin cobrar el período
            // que empieza en el vencimiento (el próximo a facturar quedó después de lo pagado).
            if (service.Status is ServiceStatus.Active or ServiceStatus.Suspended)
            {
                Count("services_alive_by_due_month", paidThrough?.ToString("yyyy-MM", CultureInfo.InvariantCulture) ?? "sin_vencimiento", Dec(r, "amount"));
                if (cycle.IsRecurring && service.NextDueDate > service.PaidThrough)
                {
                    Count("services_billed_unpaid", service.Status.ToString());
                }
            }
            db.Services.Add(service);
            services.Add(id);
            if (++pending >= 1000)
            {
                await Flush(ct).ConfigureAwait(false);
                pending = 0;
            }
        }

        await Flush(ct).ConfigureAwait(false);
        Total("opciones elegidas en servicios", chosenRows);
        if (skippedChoices > 0)
        {
            Incidents.Add($"{skippedChoices} opciones elegidas en servicios con un valor que ya no existe o es de otra opción, no se importan.");
        }

        if (outOfRange > 0)
        {
            Incidents.Add($"{outOfRange} cantidades elegidas en servicios vivos fuera del rango de su opción: un cambio de opciones de esos servicios tiene que corregirlas.");
        }

        if (paidRows > 0)
        {
            Incidents.Add($"{paidRows} servicios vivos con un renglón pagado en su vencimiento: el origen no avanzó el vencimiento al cobrar, se les vuelve a facturar ese período.");
        }

        if (limitedPromos > 0)
        {
            Incidents.Add($"{limitedPromos} servicios vivos con una promoción recurrente por períodos quedan con el importe neto del origen, sin descuento propio: no se sabe cuántos períodos le quedan, y cuando termine el staff los pasa al precio lleno.");
        }

        var trialRows = Rows("mod_pruebas_gratis").ToList();
        if (trialRows.Count > 0)
        {
            var trialProducts = trialRows.Select(t => Long(t, "packageid")).Distinct().Order().ToList();
            Incidents.Add($"{trialRows.Count} pruebas gratis del origen, de {trialRows.Select(t => Long(t, "clientid")).Distinct().Count()} cuentas y {trialProducts.Count} productos ({string.Join(", ", trialProducts)}): el servicio que la tuvo guarda su fin, una por cuenta y producto, así la cuenta no pide otra. Los días de prueba de esos productos los carga el staff.");
            if (trialRows.Count(t => !services.Contains(Long(t, "serviceid"))) is > 0 and var lost)
            {
                Incidents.Add($"{lost} pruebas gratis son de servicios que no se importan: esas cuentas pueden pedir otra del mismo producto.");
            }
        }

        Total("pruebas gratis", trials.Keys.Count(services.Contains));

        // Bajas que ya vencían en la copia y el origen no ejecutó: una tarea de revisión del staff por servicio. Mientras
        // está abierta la mora no da de baja ese servicio, así el paralelo queda alineado con el origen y el corte no
        // ejecuta ninguna sin que el staff la mire.
        if (held.Count > 0)
        {
            var review = await EnsureInstance("servicios-manuales", ct).ConfigureAwait(false);
            foreach (var (serviceId, reason, status) in held)
            {
                db.StaffTasks.Add(new StaffTask
                {
                    ModuleInstanceId = review,
                    Kind = StaffTask.Review,
                    Operation = "terminate",
                    IdempotencyKey = string.Create(CultureInfo.InvariantCulture, $"import:service:{serviceId}:terminate"),
                    Subject = StaffTask.ReviewSubject(serviceId),
                    Details = new JsonObject
                    {
                        ["reason"] = reason,
                        ["source_status"] = status,
                        ["instructions"] = "Completar: billing-oss sigue con la baja según sus reglas. Rechazar: la baja pedida queda sin efecto. Una baja por mora no se rechaza: se regulariza la deuda y se completa la tarea.",
                    }.ToJsonString(),
                    Status = StaffTaskStatus.Open,
                    CreatedAt = now,
                });
            }

            await Flush(ct).ConfigureAwait(false);
            Incidents.Add($"{held.Count} bajas que vencían al {AsOf:yyyy-MM-dd} y el origen no ejecutó quedan retenidas en una tarea de revisión del staff, sin ejecutarse solas (servicios {string.Join(", ", held.Select(h => h.Id))}).");
        }

        foreach (var (module, count) in unbound.OrderBy(u => u.Key, StringComparer.Ordinal))
        {
            Incidents.Add($"{count} servicios vivos de {module} quedan sin instancia de módulo: sus operaciones fallan a la vista hasta vincularlos (mapa de módulos).");
        }

        foreach (var (module, set) in servers.Where(s => s.Value.Count > 1))
        {
            Incidents.Add($"Servicios de {module} en {set.Count} servidores del origen ({string.Join(", ", set.Order())}) atados a una sola instancia: revisar el mapa con claves {module}:servidor.");
        }

        // Los renglones de cambio de plan del origen apuntan al pedido de cambio, que dice de qué servicio es.
        foreach (var u in Rows("tblupgrades").Where(u => Str(u, "type") is "configoptions" or "package" or "service" && services.Contains(Long(u, "relid"))))
        {
            upgradeServices[Long(u, "id")] = Long(u, "relid");
        }

        await log.WriteLineAsync($"Servicios: {services.Count}, atados a una instancia de módulo: {bound}").ConfigureAwait(false);
    }

    /// <summary>
    /// Agregados del anclaje de los vivos recurrentes, para la conciliación: cuántos tienen el alta en otro día que el
    /// vencimiento (el anclaje no puede salir del alta) y cuántos quedaron con un período que no cae en el día de su
    /// vencimiento. Los segundos tienen que ser cero: el período siguiente sale corrido respecto del origen.
    /// </summary>
    private void AnchorFigures(Service service, DateOnly? registered)
    {
        if (service.PaidThrough is not { } paid)
        {
            return;
        }

        static bool Fits(int anchor, DateOnly date) => Math.Min(anchor, DateTime.DaysInMonth(date.Year, date.Month)) == date.Day;
        if (registered is { } reg && !Fits(reg.Day, paid))
        {
            Count("services_anchor", "alta_en_otro_dia");
        }

        if (!Fits(service.AnchorDay, paid) || service.NextDueDate is { } next && !Fits(service.AnchorDay, next))
        {
            Count("services_anchor", "fuera_del_vencimiento");
        }
    }

    /// <summary>Instancias de módulo que nombra el mapa. Las carga el staff en la base destino, nunca el importador.</summary>
    private async Task Instances(CancellationToken ct)
    {
        var names = modules.Values.Select(m => m.Instance).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
        {
            return;
        }

        var found = await (from i in db.ModuleInstances
                           join m in db.ModuleInstallations on i.InstallationId equals m.Id
                           where names.Contains(i.Name)
                           select new { i.Name, i.Id, m.BaseUrl }).ToListAsync(ct).ConfigureAwait(false);
        if (ShadowInstances)
        {
            // En modo sombra ningún servicio puede quedar atado a un módulo de verdad.
            if (found.FirstOrDefault(f => f.BaseUrl.Length > 0) is { } real)
            {
                throw new InvalidOperationException($"Modo sombra: la instancia {real.Name} ya existe con URL y el paralelo no se ata a un módulo real.");
            }

            foreach (var name in names.Where(n => found.All(f => f.Name != n)))
            {
                var installation = new ModuleInstallation { Name = $"sombra-{name}", BaseUrl = string.Empty, Manifest = """{"id":"sombra","kinds":["provisioning"]}""", CreatedAt = now };
                db.ModuleInstallations.Add(installation);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                var instance = new ModuleInstance { InstallationId = installation.Id, Name = name, CreatedAt = now };
                db.ModuleInstances.Add(instance);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                found.Add(new { instance.Name, instance.Id, installation.BaseUrl });
            }

            Incidents.Add($"Modo sombra: {names.Count} instancias de módulo sin URL ({string.Join(", ", names)}). Sus operaciones nunca llegan a un módulo.");
        }

        foreach (var name in names)
        {
            var ids = found.Where(f => f.Name == name).Select(f => f.Id).ToList();
            if (ids.Count == 1)
            {
                instances[name] = ids[0];
            }
            else
            {
                Incidents.Add($"Instancia de módulo {name}: {(ids.Count == 0 ? "no existe" : "está repetida")} en la base destino, lo que la nombra queda sin vincular.");
            }
        }
    }

    private long? Instance(ModuleMapping? mapping) =>
        mapping?.Instance is { } name && instances.TryGetValue(name, out var id) ? id : null;

    /// <summary>
    /// Parámetros del módulo manual: se activa solo al pagarse si el origen lo aprovisionaba al pago, y una venta única
    /// no tiene nada que cortar.
    /// </summary>
    private static JsonObject ManualParams(DumpRow product, string paytype) => new()
    {
        ["activation"] = Str(product, "autosetup") == "payment" ? "on_payment" : "staff",
        ["lifecycle"] = paytype == "onetime" ? "state_only" : "staff",
    };

    /// <summary>
    /// Parámetros del módulo: los del mapa, sacados de las columnas del producto (con params vacío, ninguno). Sin mapa o
    /// sin params, los del origen tal cual.
    /// </summary>
    private static JsonObject ProductParams(DumpRow product, ModuleMapping? mapping)
    {
        if (mapping?.Params is { } map)
        {
            return new JsonObject(map
                .Select(p => KeyValuePair.Create(p.Key, Param(product, p.Value)))
                .Where(p => p.Value is not null));
        }

        var parameters = new JsonObject { ["module"] = Blank(Str(product, "servertype")) };
        for (var i = 1; i <= 24; i++)
        {
            if (Blank(Str(product, $"configoption{i}")) is { } v)
            {
                parameters[$"configoption{i}"] = v;
            }
        }

        return parameters;
    }

    /// <summary>
    /// Un parámetro: "columna" va como texto y no va si está vacía. "entero:columna" y "booleano:columna" convierten,
    /// con 0 y falso para el vacío, como lo tomaba el módulo anterior ("on" es verdadero).
    /// </summary>
    private static JsonNode? Param(DumpRow product, string source) => source.Split(':', 2) switch
    {
        ["entero", var column] => (JsonNode)(long.TryParse(Blank(Str(product, column)), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : 0L),
        ["booleano", var column] => (JsonNode)(Blank(Str(product, column))?.ToUpperInvariant() is "ON" or "1" or "YES" or "TRUE"),
        _ => Blank(Str(product, source)),
    };

    /// <summary>
    /// IDs externos de un servicio según el mapa: una columna del servicio, o "campo:nombre" para un campo de producto
    /// (el nombre antes de la barra, o su ID). Los campos con forma de secreto nunca van: quedan como incidencia.
    /// </summary>
    private JsonObject ExternalIds(DumpRow service, long serviceId, long productId, ModuleMapping mapping)
    {
        var ids = new JsonObject();
        foreach (var (key, source) in mapping.ExternalIds)
        {
            string? value;
            if (source.StartsWith("campo:", StringComparison.Ordinal))
            {
                var name = source["campo:".Length..];
                var field = Rows("tblcustomfields").FirstOrDefault(f => Str(f, "type") == "product" && Long(f, "relid") is var p && (p == productId || p == 0)
                    && ((Str(f, "fieldname") ?? string.Empty).Split('|')[0].Trim() == name || Str(f, "id") == name));
                if (field is not null && (Str(field, "fieldtype") == "password" || SecretName().IsMatch(name)))
                {
                    if (secretFields.Add(name))
                    {
                        Incidents.Add($"Campo de producto {name}: tiene forma de secreto y no va a external_ids. Se carga cifrado aparte.");
                    }

                    continue;
                }

                value = field is null ? null : fieldValues.GetValueOrDefault((Long(field, "id"), serviceId));
            }
            else
            {
                value = Str(service, source);
            }

            if (Blank(value) is { } v)
            {
                ids[key] = v;
            }
        }

        foreach (var (key, value) in mapping.Services.GetValueOrDefault(serviceId) ?? [])
        {
            ids[key] = value;
        }

        return ids;
    }

    /// <summary>
    /// Dominios con su ID, estado, precio congelado de renovación y la instancia de registrar que los atiende. Los que
    /// no tienen registrar con API van al registrar manual: .ar a NIC Argentina, los subdominios propios a la zona
    /// propia y el resto según su registrar de origen. El próximo período sale de nextduedate, que en los dominios
    /// es el vencimiento: nextinvoicedate no se actualiza.
    /// </summary>
    private async Task Domains(Dictionary<long, Currency> currencies, CancellationToken ct)
    {
        // Renovaciones ya facturadas, aunque estén impagas: renglón de dominio con el vencimiento del período. Una
        // anulada vuelve a estar por facturar, como en el núcleo con la nota de crédito: si no, el dominio quedaría sin
        // factura abierta y sin forma de renovarse.
        var cancelled = Rows("tblinvoices").Where(i => Str(i, "status") is "Cancelled" or "Refunded").Select(i => Long(i, "id")).ToHashSet();
        var invoiced = Rows("tblinvoiceitems")
            .Where(i => Str(i, "type") == "Domain" && Date(i, "duedate") is not null && !cancelled.Contains(Long(i, "invoiceid")))
            .Select(i => (Long(i, "relid"), Date(i, "duedate")!.Value))
            .ToHashSet();
        var source = Rows("tbldomains").Where(r =>
        {
            var ok = accountCurrency.ContainsKey(Long(r, "userid"));
            if (!ok)
            {
                Incidents.Add($"Dominio {Long(r, "id")}: la cuenta {Long(r, "userid")} no existe, no se importa.");
            }

            return ok;
        }).ToList();
        foreach (var name in source.Select(r => RegistrarFor(r).Instance).Distinct(StringComparer.Ordinal))
        {
            await EnsureInstance(name, ct).ConfigureAwait(false);
        }

        var liveNames = new HashSet<string>(StringComparer.Ordinal);
        var overdue = new List<(long Id, DateOnly PaidThrough)>();
        foreach (var r in source)
        {
            var id = Long(r, "id");
            var accountId = Long(r, "userid");
            var (instance, zone) = RegistrarFor(r);
            var years = (int)Math.Max(1, Long(r, "registrationperiod"));
            var sourceStatus = Str(r, "status") ?? "Pending";
            var status = DomainState(sourceStatus);
            var name = Str(r, "domain") ?? $"dominio-{id}";
            if (status is DomainStatus.Pending or DomainStatus.Active or DomainStatus.Grace && !liveNames.Add(name.Trim().ToLowerInvariant()))
            {
                // Un nombre vive en una sola cuenta: la copia repetida entra cancelada para conservar su historia.
                Incidents.Add($"Dominio {id}: el nombre ya está vivo en otro dominio importado, entra cancelado.");
                status = DomainStatus.Cancelled;
            }

            // Lo pago llega hasta nextduedate. Si ese período ya tiene renglón facturado, el próximo es el siguiente.
            var paidThrough = Date(r, "nextduedate");
            var nextDue = paidThrough is { } paid && invoiced.Contains((id, paid)) ? paid.AddYears(years) : paidThrough;
            var domain = new RegisteredDomain(accountId, name, instances[instance], accountCurrency[accountId], Dec(r, "recurringamount"), Instant(Date(r, "registrationdate")) ?? now)
            {
                RegisteredOn = Date(r, "registrationdate"),
                ExpiresOn = Date(r, "expirydate"),
                AutoRenew = Long(r, "donotrenew") != 1,
                DnsManagement = Long(r, "dnsmanagement") == 1,
                EmailForwarding = Long(r, "emailforwarding") == 1,
                IdProtection = Long(r, "idprotection") == 1,
                PaymentMethod = Blank(Str(r, "paymentmethod")),
                ExternalIds = zone is null ? "{}" : new JsonObject { ["zone"] = zone }.ToJsonString(),

                // Como los servicios: los precios del origen se toman con IVA incluido.
                PriceMode = PriceMode.TaxIncluded,
            };
            domain.SetImportedId(id);
            domain.ImportState(status, years, nextDue, paidThrough);
            db.Domains.Add(domain);
            domains.Add(id);
            Count("domains_by_status", sourceStatus);
            Count("domains_by_instance", instance);
            if (domain.Status is DomainStatus.Active or DomainStatus.Grace && paidThrough < AsOf)
            {
                overdue.Add((id, paidThrough!.Value));
            }
        }

        await Flush(ct).ConfigureAwait(false);

        // Vivos en el origen con lo pago ya vencido: billing-oss los pasa a gracia y, pasada la gracia, a vencidos en su
        // primera corrida, porque ningún registrar sincroniza todavía. El origen cambia el estado por su cuenta.
        if (overdue.Count > 0)
        {
            var grace = new BillingOptions().DomainGraceDays;
            var expired = overdue.Count(d => d.PaidThrough.AddDays(grace) <= AsOf);
            Incidents.Add($"{overdue.Count} dominios vivos con el vencimiento pasado al {AsOf:yyyy-MM-dd} que el origen no dio por vencidos: {expired} pasaron la gracia de {grace} días y la primera corrida los da por vencidos, el resto queda en gracia. Decidir antes del corte (dominios {string.Join(", ", overdue.Select(d => d.Id))}).");
        }
        var orphans = Rows("tbldomainsadditionalfields").Count(r => !domains.Contains(Long(r, "domainid")));
        if (orphans > 0)
        {
            Incidents.Add($"{orphans} campos adicionales de dominios que ya no existen: se descartan.");
        }

        await TldPrices(currencies, ct).ConfigureAwait(false);
        var byInstance = Figures.Where(f => f.Key.Section == "domains_by_instance").OrderBy(f => f.Key.Key, StringComparer.Ordinal).Select(f => $"{f.Key.Key} {f.Value.Count}");
        await log.WriteLineAsync($"Dominios: {domains.Count} ({string.Join(", ", byInstance)})").ConfigureAwait(false);
    }

    /// <summary>El estado propio de un dominio según el estado del origen.</summary>
    internal static DomainStatus DomainState(string? status) => status switch
    {
        "Active" => DomainStatus.Active,
        "Grace" or "Redemption" => DomainStatus.Grace,
        "Expired" => DomainStatus.Expired,
        "Pending" or "Pending Registration" or "Pending Transfer" => DomainStatus.Pending,
        _ => DomainStatus.Cancelled,
    };

    /// <summary>Instancia de registrar de un dominio y, si es subdominio de una zona propia, esa zona.</summary>
    private (string Instance, string? Zone) RegistrarFor(DumpRow r)
    {
        var name = (Str(r, "domain") ?? string.Empty).Trim().ToLowerInvariant();
        var zone = OwnZones.FirstOrDefault(z => name == z || name.EndsWith($".{z}", StringComparison.Ordinal));
        return Blank(Str(r, "registrar"))?.ToLowerInvariant() switch
        {
            // Los registrars con módulo propio (sección 12 del protocolo). Un registrar sin módulo se atiende a mano.
            { } api when RegistrarModules.Contains(api) => (api, null),
            { } other => ($"{other}-manual", null),
            null when zone is not null => ("subdominios-propios-manual", zone),
            null when name.EndsWith(".ar", StringComparison.Ordinal) => ("nic-ar-manual", null),
            null => ("sin-registrar", null),
        };
    }

    /// <summary>
    /// Precios por TLD, instancia, moneda y años. Las columnas de cargo de alta y de precio del origen son, en orden,
    /// de 1 a 10 años, lo que se ve en que crecen de forma lineal. -1 es que no se ofrece. Las de 3 años en las
    /// columnas trienales vienen en 0.00 sin uso y no se leen. La transferencia es solo a 1 año. Las filas de precio
    /// sin extensión se descartan.
    /// </summary>
    private async Task TldPrices(Dictionary<long, Currency> currencies, CancellationToken ct)
    {
        string[] byYears = ["msetupfee", "qsetupfee", "ssetupfee", "asetupfee", "bsetupfee", "monthly", "quarterly", "semiannually", "annually", "biennially"];
        var extensions = Rows("tbldomainpricing").ToDictionary(r => Long(r, "id"), r => (Tld: Str(r, "extension") ?? string.Empty, Registrar: Blank(Str(r, "autoreg"))?.ToLowerInvariant()));
        var prices = new Dictionary<(long Extension, Currency Currency, int Years), TldPrice>();
        var orphans = 0;
        foreach (var r in Rows("tblpricing").Where(r => Str(r, "type") is "domainregister" or "domainrenew" or "domaintransfer"))
        {
            if (!extensions.TryGetValue(Long(r, "relid"), out var extension))
            {
                orphans++;
                continue;
            }

            if (!currencies.TryGetValue(Long(r, "currency"), out var currency))
            {
                continue;
            }

            var type = Str(r, "type");
            for (var years = 1; years <= (type == "domaintransfer" ? 1 : byYears.Length); years++)
            {
                if (Price(r, byYears[years - 1]) is not { } amount)
                {
                    continue;
                }

                var key = (Long(r, "relid"), currency, years);
                if (!prices.TryGetValue(key, out var price))
                {
                    var instance = await EnsureInstance(extension.Registrar ?? "sin-registrar", ct).ConfigureAwait(false);
                    price = new TldPrice(extension.Tld, instance, currency, years, new DateOnly(2000, 1, 1));
                    prices[key] = price;
                    db.TldPrices.Add(price);
                }

                _ = type switch
                {
                    "domainregister" => price.Register = amount,
                    "domainrenew" => price.Renew = amount,
                    _ => price.Transfer = amount,
                };
            }
        }

        // Las opciones del dominio: una fila por moneda con la gestión de DNS, el reenvío y la protección en las tres
        // primeras columnas, en el orden de sus marcas en los dominios. En la copia solo la primera tiene precio, que es
        // también la marca más usada. Sin precio la opción es gratis: no hace falta la fila.
        string[] options = [DomainOptions.Dns, DomainOptions.EmailForwarding, DomainOptions.Privacy];
        foreach (var r in Rows("tblpricing").Where(r => Str(r, "type") == "domainaddons"))
        {
            if (!currencies.TryGetValue(Long(r, "currency"), out var currency))
            {
                continue;
            }

            for (var i = 0; i < options.Length; i++)
            {
                if (Price(r, byYears[i]) is > 0m and var amount)
                {
                    db.DomainOptionPrices.Add(new DomainOptionPrice(options[i], currency, new DateOnly(2000, 1, 1)) { Price = amount });
                }
            }
        }

        // Qué opciones ofrece cada extensión, por sus marcas de gestión de DNS, reenvío y protección: solo una marca en 0
        // la apaga. Con las tres no hace falta la fila, la extensión ofrece todas. En la copia ninguna ofrece la protección.
        static bool On(DumpRow r, string column) => Str(r, column) != "0";
        var hiding = Rows("tbldomainpricing").Where(r => !On(r, "dnsmanagement") || !On(r, "emailforwarding") || !On(r, "idprotection"))
            .GroupBy(r => (Str(r, "extension") ?? string.Empty).Trim().ToLowerInvariant(), StringComparer.Ordinal).Where(g => g.Key.StartsWith('.'));
        foreach (var g in hiding)
        {
            var r = g.First();
            db.TldOptions.Add(new TldOptions(g.Key) { Dns = On(r, "dnsmanagement"), EmailForwarding = On(r, "emailforwarding"), Privacy = On(r, "idprotection") });
        }

        await Flush(ct).ConfigureAwait(false);
        var priced = prices.Keys.Select(k => k.Extension).Distinct().Count();
        if (orphans > 0)
        {
            Incidents.Add($"{orphans} precios de dominio sin extensión: se descartan.");
        }

        if (extensions.Count > priced)
        {
            Incidents.Add($"{extensions.Count - priced} extensiones sin ningún precio ofrecido.");
        }

        await log.WriteLineAsync($"Precios por TLD: {priced} extensiones con precio, {prices.Count} filas").ConfigureAwait(false);
    }

    /// <summary>
    /// La instancia de módulo con ese nombre: la que nombra el mapa si ya existe en la base destino o, si no, una que
    /// se crea la primera vez. Las de internetbs quedan sin URL hasta que se despliegue su módulo. Las demás son del
    /// módulo manual del núcleo.
    /// </summary>
    private async Task<long> EnsureInstance(string name, CancellationToken ct)
    {
        if (instances.TryGetValue(name, out var id))
        {
            return id;
        }

        if (RegistrarModules.Contains(name))
        {
            // Queda sin URL hasta que se despliega el módulo, que al registrarse trae su manifiesto completo.
            var manifest = new JsonObject { ["id"] = name, ["kinds"] = new JsonArray("registrar") }.ToJsonString();
            var installation = new ModuleInstallation { Name = name, BaseUrl = string.Empty, Manifest = manifest, CreatedAt = now };
            db.ModuleInstallations.Add(installation);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var instance = new ModuleInstance { InstallationId = installation.Id, Name = name, CreatedAt = now };
            db.ModuleInstances.Add(instance);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            id = instance.Id;
        }
        else
        {
            var config = name switch
            {
                "servicios-manuales" => new JsonObject { ["queue"] = "servicios" },
                "nic-ar-manual" => new JsonObject { ["queue"] = "dominios", ["registry"] = "nic.ar", ["grace_days"] = 45 },
                "subdominios-propios-manual" => new JsonObject { ["queue"] = "dominios", ["registry"] = "own_dns" },
                "sin-registrar" => new JsonObject { ["queue"] = "dominios", ["registry"] = "none" },
                _ => new JsonObject { ["queue"] = "dominios", ["registry"] = "manual_registrar" },
            };
            id = await ManualModule.AddInstance(db, name, config, now, ct).ConfigureAwait(false);
        }

        instances[name] = id;
        return id;
    }

    /// <summary>Precio de una columna, o nulo si no se ofrece (-1) o la columna no existe en esta versión del origen.</summary>
    private static decimal? Price(DumpRow row, string column) =>
        decimal.TryParse(Str(row, column), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0m ? v : null;

    private async Task Invoices(Dictionary<long, Currency> currencies, CancellationToken ct)
    {
        var items = Rows("tblinvoiceitems").GroupBy(r => Long(r, "invoiceid")).ToDictionary(g => g.Key, g => g.ToList());

        // Lo cobrado de cada factura, neto de lo devuelto o revertido y solo en su moneda: un pago en otra moneda no
        // se suma como si fuera de esta.
        var paid = Rows("tblaccounts")
            .Where(r => Long(r, "invoiceid") > 0 && IsCash(r) && accountCurrency.TryGetValue(Long(r, "userid"), out var c) && MoveCurrency(r, c, currencies, this.gross.GetValueOrDefault(Long(r, "invoiceid"))) == c)
            .GroupBy(r => Long(r, "invoiceid")).ToDictionary(g => g.Key, g => g.Sum(r => Dec(r, "amountin") - Dec(r, "amountout")));
        var applied = AppliedBalance();
        var afip = Rows("mod_afip_invoices").Where(r => Long(r, "voucher_type") is 1 or 6 or 19)
            .GroupBy(r => Long(r, "invoice_id")).ToDictionary(g => g.Key, g => g.First());
        var adjusted = 0;
        var drafts = 0;
        var unbalanced = 0;
        var mismatched = new List<long>();
        var pending = 0;
        foreach (var r in Rows("tblinvoices"))
        {
            var id = Long(r, "id");
            var status = Str(r, "status") ?? "Unpaid";
            var accountId = Long(r, "userid");
            if (status == "Draft")
            {
                drafts++;
                continue;
            }

            if (!accountCurrency.TryGetValue(accountId, out var currency))
            {
                Incidents.Add($"Factura {id}: la cuenta {accountId} no existe, no se importa.");
                continue;
            }

            var tax = Dec(r, "tax") + Dec(r, "tax2");
            var gross = this.gross[id];
            var amounts = Amounts(gross, tax, Dec(r, "taxrate"), currency, afip.GetValueOrDefault(id));
            // Las notas de crédito nativas acreditan lo suyo. Una anulada o reembolsada sin nota conserva el crédito sintético.
            var notes = Math.Min(gross, noteCredits.GetValueOrDefault(id));
            var (allocated, credited) = status switch
            {
                "Paid" => (gross - notes, notes),
                "Cancelled" or "Refunded" => (0m, gross),
                _ => (Math.Max(0m, Math.Min(gross - notes, paid.GetValueOrDefault(id) + Math.Max(Dec(r, "credit"), applied.GetValueOrDefault(id).Amount))), notes),
            };
            invoiceAllocated[id] = allocated;
            var number = long.TryParse(Str(r, "invoicenum"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : id;
            var doc = Document.FromImport(id, DocumentKind.Invoice, accountId, currency, number, Date(r, "date") ?? new DateOnly(2000, 1, 1),
                Date(r, "duedate") ?? Date(r, "date") ?? new DateOnly(2000, 1, 1), amounts, allocated, credited, "{}", now, status == "Refunded" ? gross : 0m, status);

            var lines = (items.GetValueOrDefault(id) ?? []).Select(item =>
            {
                var relid = Long(item, "relid");
                var type = Str(item, "type") ?? string.Empty;
                // Un renglón de cambio apunta al pedido de cambio y uno de complemento al complemento contratado, no al servicio.
                var serviceId = type switch
                {
                    "Hosting" or "Setup" or "PromoHosting" when services.Contains(relid) => relid,
                    "Upgrade" when upgradeServices.TryGetValue(relid, out var upgraded) => upgraded,
                    "Addon" when addonServices.TryGetValue(relid, out var addon) => addon,
                    _ => (long?)null,
                };
                var domainId = type.StartsWith("Domain", StringComparison.Ordinal) && domains.Contains(relid) ? relid : (long?)null;
                return new SourceLine(LineKindOf(type), Str(item, "description") ?? type, Dec(item, "amount"), Long(item, "taxed") == 1, serviceId, domainId);
            });
            if (ImportLines(doc, amounts, lines))
            {
                adjusted++;
            }

            if (!Balanced(doc))
            {
                unbalanced++;
            }

            // El estado que se deriva tiene que ser el del origen: si no, algo de lo importado no cierra.
            var derived = doc.Status;
            if (!(status switch
            {
                "Paid" => derived == InvoiceStatus.Paid,
                "Cancelled" or "Refunded" => derived == InvoiceStatus.Cancelled,
                _ => derived is InvoiceStatus.Issued or InvoiceStatus.PartiallyPaid,
            }))
            {
                mismatched.Add(id);
            }

            db.Documents.Add(doc);
            invoices[id] = (currency, status);
            Count("invoices_by_status_currency", $"{status}\t{currency.Code}", gross);
            if (++pending >= 500)
            {
                await Flush(ct).ConfigureAwait(false);
                pending = 0;
            }
        }

        await Flush(ct).ConfigureAwait(false);
        if (drafts > 0)
        {
            Incidents.Add($"{drafts} facturas en borrador del origen no se importan: un borrador no es un documento emitido.");
        }

        if (unbalanced > 0)
        {
            Incidents.Add($"{unbalanced} facturas cuyas líneas no dan los importes de la cabecera por redondeo: una nota de crédito armada con sus líneas puede no cerrar.");
        }

        if (mismatched.Count > 0)
        {
            Incidents.Add($"{mismatched.Count} facturas con estado derivado distinto del que tenían en el origen: {string.Join(", ", mismatched.Take(10))}.");
        }

        await log.WriteLineAsync($"Facturas: {invoices.Count}, con línea de ajuste: {adjusted}").ConfigureAwait(false);
    }

    private sealed record SourceLine(LineKind Kind, string Description, decimal Amount, bool Taxed, long? ServiceId, long? DomainId = null);

    /// <summary>
    /// Las líneas de un documento importado, con el tratamiento de su cabecera, que es la verdad (D-17). Cada línea va
    /// al balde que su origen pide (gravado o no) si la cabecera lo tiene, si no al balde mayor de la cabecera. Lo que
    /// no cierra en cada balde va en una línea de ajuste con ese mismo tratamiento. Así una nota de crédito armada con
    /// las líneas cierra contra la cabecera. Devuelve si hizo falta ajustar.
    /// </summary>
    private static bool ImportLines(Document doc, TaxBreakdown header, IEnumerable<SourceLine> lines)
    {
        List<(TaxTreatment Treatment, decimal Rate, decimal Expected)> buckets =
            [.. header.Aliquots.Select(a => (TaxTreatment.Taxed, a.Rate, a.Net + a.Tax)), (TaxTreatment.NotTaxed, 0m, header.NotTaxed), (TaxTreatment.Exempt, 0m, header.Exempt)];
        var main = buckets.MaxBy(b => b.Expected);
        var taxed = header.Aliquots.Count == 1 && buckets[0].Expected > 0m ? buckets[0] : main;
        var notTaxed = header.NotTaxed > 0m ? buckets[header.Aliquots.Count] : main;
        var actual = new Dictionary<(TaxTreatment, decimal), decimal>();
        foreach (var line in lines)
        {
            var (treatment, rate, _) = line.Taxed ? taxed : notTaxed;
            doc.ImportLine(line.Kind, line.Description, line.Amount, treatment, rate, line.ServiceId, line.DomainId);
            actual[(treatment, rate)] = actual.GetValueOrDefault((treatment, rate)) + line.Amount;
        }

        var adjusted = false;
        foreach (var (treatment, rate, expected) in buckets)
        {
            if (expected != actual.GetValueOrDefault((treatment, rate)))
            {
                doc.ImportLine(LineKind.RoundingAdjustment, "Ajuste de importación", expected - actual.GetValueOrDefault((treatment, rate)), treatment, rate, null);
                adjusted = true;
            }
        }

        return adjusted;
    }

    /// <summary>Si el cálculo de las líneas da los importes de la cabecera, balde por balde.</summary>
    private static bool Balanced(Document doc)
    {
        try
        {
            var computed = doc.Compute();
            var issued = doc.IssuedBreakdown();
            return computed.Exempt == issued.Exempt && computed.NotTaxed == issued.NotTaxed
                && computed.Aliquots.Where(a => a.Net != 0m || a.Tax != 0m).SequenceEqual(issued.Aliquots.Where(a => a.Net != 0m || a.Tax != 0m));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Importes de una factura. Si tiene comprobante en la misma moneda, el reparto de IVA sale del comprobante,
    /// que es el que vio el fisco. Si no, de la cabecera.
    /// </summary>
    private static TaxBreakdown Amounts(decimal gross, decimal tax, decimal taxRate, Currency currency, DumpRow? fiscal)
    {
        var fiscalCurrency = fiscal is null ? null : Str(fiscal, "moneda") switch { "PES" => "ARS", "DOL" => "USD", var m => m };
        if (fiscal is not null && fiscalCurrency == currency.Code && Math.Abs(Dec(fiscal, "monto_total") - gross) <= 0.05m)
        {
            if (Long(fiscal, "voucher_type") == 19)
            {
                return new TaxBreakdown([], [], gross, 0m);
            }

            var net = Dec(fiscal, "monto_neto");
            var vat = Dec(fiscal, "monto_iva");
            return new TaxBreakdown(vat > 0m ? [new AliquotTotal(0.21m, net, vat)] : [], [], 0m, gross - (vat > 0m ? net + vat : 0m));
        }

        return tax > 0m
            ? new TaxBreakdown([new AliquotTotal(taxRate / 100m, gross - tax, tax)], [], 0m, 0m)
            : new TaxBreakdown([], [], 0m, gross);
    }

    /// <summary>
    /// Un movimiento de tblaccounts que es plata que entró o salió. Desde WHMCS 9 la tabla también lleva la contrapartida
    /// de las notas de crédito nativas y los movimientos de saldo a favor, que no son cobros.
    /// </summary>
    private static bool IsCash(DumpRow r) => !IsBalanceMove(r) && Str(r, "type") != "invoice_billing_adjustment_credit";

    /// <summary>
    /// Saldo a favor aplicado a una factura (invoice_credit_applied, en amountin) o quitado de ella (invoice_credit_removed,
    /// en amountout). No es plata: entra como imputación de saldo, neta de lo quitado.
    /// </summary>
    internal static bool IsBalanceMove(DumpRow r) => Str(r, "type") is "invoice_credit_applied" or "invoice_credit_removed";

    /// <summary>
    /// Las notas nativas que el origen abrió por un movimiento de saldo (de crédito al aplicarlo, de débito al quitarlo),
    /// con el tipo del movimiento. No son documentos. Una nota con comprobante fiscal siempre es un documento.
    /// </summary>
    internal static Dictionary<long, string> BalanceNotes(IEnumerable<DumpRow> moves, IEnumerable<DumpRow> fiscal)
    {
        var withVoucher = fiscal.Select(r => Long(r, "native_note_id")).Where(n => n > 0).ToHashSet();
        return moves.Where(r => Long(r, "billingnoteid") is var note && note > 0 && !withVoucher.Contains(note) && IsBalanceMove(r))
            .GroupBy(r => Long(r, "billingnoteid"))
            .ToDictionary(g => g.Key, g => Str(g.First(), "type")!);
    }

    /// <summary>Saldo aplicado a cada factura según los movimientos del origen: lo aplicado menos lo quitado, con la fecha del primero.</summary>
    private Dictionary<long, (decimal Amount, DateTimeOffset At)> AppliedBalance() => Rows("tblaccounts")
        .Where(r => Long(r, "invoiceid") > 0 && IsBalanceMove(r))
        .GroupBy(r => Long(r, "invoiceid"))
        .ToDictionary(g => g.Key, g => (g.Sum(r => Dec(r, "amountin") - Dec(r, "amountout")), g.Min(r => Instant(DateTimeValue(r, "date")) ?? now)));

    /// <summary>
    /// Notas de crédito y de débito nativas del origen como documentos, con la factura del primer renglón como original.
    /// No conservan su ID (el espacio de IDs es el de las facturas): la secuencia arranca arriba de ellas. Las que abrió
    /// un movimiento de saldo no son documentos: ese saldo entra como imputación (<see cref="AppliedCredit"/>).
    /// </summary>
    private async Task Notes(Dictionary<long, long> itemInvoice, CancellationToken ct)
    {
        var notes = Rows("tblbillingnotes").Where(r => Str(r, "status") != "draft").OrderBy(r => Long(r, "id")).ToList();
        if (notes.Count == 0)
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(SequenceReset("billing.documents"), ct).ConfigureAwait(false);
        var items = Rows("tblbillingnoteitems").GroupBy(r => Long(r, "billingnote_id")).ToDictionary(g => g.Key, g => g.ToList());
        var balance = BalanceNotes(Rows("tblaccounts"), Rows("mod_afip_invoices"));
        var created = new List<(long SourceId, Document Doc)>();
        foreach (var r in notes)
        {
            var id = Long(r, "id");
            var accountId = Long(r, "client_id");
            if (!accountCurrency.TryGetValue(accountId, out var currency))
            {
                Incidents.Add($"Nota {id}: la cuenta {accountId} no existe, no se importa.");
                continue;
            }

            var kind = Str(r, "note_type") == "debit" ? DocumentKind.DebitNote : DocumentKind.CreditNote;
            var lines = items.GetValueOrDefault(id) ?? [];
            var related = lines.Where(i => Str(i, "type") == "invoice_item").Select(i => itemInvoice.GetValueOrDefault(Long(i, "relid"))).FirstOrDefault(invoices.ContainsKey);
            if (related > 0)
            {
                currency = invoices[related].Currency;
            }

            var total = Dec(r, "total");
            var number = long.TryParse(Str(r, "custom_number"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : id;
            if (balance.TryGetValue(id, out var move))
            {
                // Su número quedó usado en el origen: la numeración propia de ese tipo sigue arriba de él.
                usedNoteNumbers[kind] = Math.Max(usedNoteNumbers.GetValueOrDefault(kind), number);
                Count("billing_notes_by_type_currency", $"{Str(r, "note_type")}:{move}\t{currency.Code}", total);
                continue;
            }

            var amounts = Amounts(total, Dec(r, "tax") + Dec(r, "tax2"), Dec(r, "taxrate"), currency, null);
            var date = Date(r, "date_issued") ?? new DateOnly(2000, 1, 1);
            var doc = Document.FromImport(0, kind, accountId, currency, number, date, date, amounts, 0m, 0m, "{}", now, sourceStatus: Str(r, "status"), relatedDocumentId: related > 0 ? related : null);
            ImportLines(doc, amounts, lines.Select(i => new SourceLine(LineKind.Manual, Str(i, "description") ?? kind.ToString(), Dec(i, "amount"), Long(i, "taxed") == 1, null)));
            db.Documents.Add(doc);
            created.Add((id, doc));
            Count("billing_notes_by_type_currency", $"{Str(r, "note_type")}\t{currency.Code}", total);
        }

        await Flush(ct).ConfigureAwait(false);
        foreach (var (sourceId, doc) in created)
        {
            noteDocuments[sourceId] = doc.Id;
        }

        await log.WriteLineAsync($"Notas nativas: {created.Count}").ConfigureAwait(false);
    }

    /// <summary>
    /// Saldo a favor que el origen aplicó a facturas (tblaccounts de tipo invoice_credit_applied, menos lo quitado con
    /// invoice_credit_removed, o el crédito de la cabecera): no es plata que entró, es una imputación de saldo. El saldo
    /// de la cuenta ya está neto de esos consumos, así que cada uno entra con su contrapartida y el libro suma lo mismo
    /// que el saldo.
    /// </summary>
    private async Task AppliedCredit(CancellationToken ct)
    {
        var applied = AppliedBalance();
        var count = 0;
        foreach (var r in Rows("tblinvoices"))
        {
            var id = Long(r, "id");
            if (!invoices.TryGetValue(id, out var invoice))
            {
                continue;
            }

            var rows = applied.GetValueOrDefault(id);
            var amount = Math.Min(Math.Max(Dec(r, "credit"), rows.Amount), invoiceAllocated.GetValueOrDefault(id) - allocatedByInvoice.GetValueOrDefault(id));
            if (amount <= 0m)
            {
                continue;
            }

            var at = rows.Amount > 0m ? rows.At : Instant(Date(r, "date")) ?? now;
            var accountId = Long(r, "userid");
            db.CreditEntries.Add(new CreditEntry(accountId, invoice.Currency, amount, "Saldo importado, aplicado en el origen", at, documentId: id));
            var consumed = new CreditEntry(accountId, invoice.Currency, -amount, "Consumo de saldo importado", at, documentId: id);
            db.CreditEntries.Add(consumed);
            await Flush(ct).ConfigureAwait(false);
            db.Allocations.Add(new Allocation(id, amount, 1m, at, creditEntryId: consumed.Id));
            allocatedByInvoice[id] = allocatedByInvoice.GetValueOrDefault(id) + amount;
            count++;
        }

        await Flush(ct).ConfigureAwait(false);
        await log.WriteLineAsync($"Facturas con saldo aplicado en el origen: {count}").ConfigureAwait(false);
    }

    private async Task Orders(CancellationToken ct)
    {
        var count = 0;
        var fraud = new List<(long Order, long Account, DateTimeOffset At)>();
        foreach (var r in Rows("tblorders"))
        {
            var accountId = Long(r, "userid");
            if (!accountCurrency.TryGetValue(accountId, out var currency))
            {
                continue;
            }

            var order = new Order(accountId, currency, Instant(DateTimeValue(r, "date")) ?? now)
            {
                Status = Str(r, "status") switch { "Active" => OrderStatus.Completed, "Cancelled" or "Fraud" => OrderStatus.Cancelled, _ => OrderStatus.Pending },
                InvoiceId = invoices.ContainsKey(Long(r, "invoiceid")) ? Long(r, "invoiceid") : null,
                CouponId = Blank(Str(r, "promocode")) is { } code && couponCodes.TryGetValue(code, out var coupon) ? coupon : null,
            };
            order.SetImportedId(Long(r, "id"));
            db.Orders.Add(order);
            if (Str(r, "status") == "Fraud")
            {
                fraud.Add((order.Id, accountId, order.PlacedAt));
            }

            if (++count % 1000 == 0)
            {
                await Flush(ct).ConfigureAwait(false);
            }
        }

        await Flush(ct).ConfigureAwait(false);

        // Los marcados como fraude en el origen quedan como evaluaciones rechazadas: se ven en la revisión y en la
        // seguridad de la ficha, como los rechazados acá (diseno/fraude-y-acceso.md, sección 3.2).
        foreach (var (orderId, accountId, at) in fraud)
        {
            var check = new FraudCheck(orderId, accountId, [new FraudReason("import.fraud", "Marcado como fraude en el sistema anterior.")], null, null, null, at);
            check.Decide(approve: false, "import", null, at);
            db.Set<FraudCheck>().Add(check);
        }

        await Flush(ct).ConfigureAwait(false);
        await log.WriteLineAsync($"Pedidos: {count}, {fraud.Count} marcados como fraude").ConfigureAwait(false);
    }

    private async Task Payments(Dictionary<long, Currency> currencies, CancellationToken ct)
    {
        var seen = new HashSet<(string, string)>();
        var outs = 0;
        var payments = 0;
        var converted = 0;
        var doubtful = 0;
        var refunded = 0;
        var batch = new List<(Payment Payment, long InvoiceId, DateTimeOffset At, decimal Allocated, decimal Rate)>();

        // Lo devuelto de cada pago: una salida que apunta al pago (refundid) o, si no apunta, una por el mismo importe
        // de la misma factura.
        var refunds = Rows("tblaccounts").Where(r => Dec(r, "amountout") > 0m && IsCash(r) && Long(r, "refundid") > 0)
            .GroupBy(r => Long(r, "refundid")).ToDictionary(g => g.Key, g => g.Sum(r => Dec(r, "amountout")));
        var outsByInvoice = Rows("tblaccounts").Where(r => Dec(r, "amountout") > 0m && IsCash(r) && Long(r, "refundid") <= 0 && Long(r, "invoiceid") > 0)
            .GroupBy(r => Long(r, "invoiceid")).ToDictionary(g => g.Key, g => g.Select(r => Dec(r, "amountout")).ToList());
        foreach (var r in Rows("tblaccounts"))
        {
            var accountId = Long(r, "userid");
            if (Dec(r, "amountout") > 0m)
            {
                outs++;
            }

            if (!accountCurrency.TryGetValue(accountId, out var clientCurrency))
            {
                continue;
            }

            // La línea base cuenta todos los movimientos con la moneda que dice el origen. Pagos son solo los que traen
            // entrada de dinero, y van en la moneda en que entraron.
            var amount = Dec(r, "amountin");
            Count("payments_by_currency", currencies.GetValueOrDefault(Long(r, "currency"), clientCurrency).Code, amount);
            Count("payments_by_gateway", Str(r, "gateway") ?? string.Empty, amount);
            if (amount <= 0m)
            {
                continue;
            }

            // Desde WHMCS 9 la tabla lleva movimientos que no son cobros: la contrapartida de una nota de crédito
            // nativa (ya está en la factura como acreditado) y el saldo a favor aplicado (entra como imputación de saldo).
            Count("payments_by_type", Blank(Str(r, "type")) ?? "(sin tipo)", amount);
            if (!IsCash(r))
            {
                continue;
            }

            var currency = MoveCurrency(r, clientCurrency, currencies, gross.GetValueOrDefault(Long(r, "invoiceid")));
            if (currency != clientCurrency)
            {
                converted++;
            }
            else if (!currencies.ContainsKey(Long(r, "currency")) && gross.GetValueOrDefault(Long(r, "invoiceid")) is > 0m and var g && amount >= 1.5m * g)
            {
                doubtful++;
            }

            var channel = $"import:{Blank(Str(r, "gateway")) ?? "manual"}";
            var external = Blank(Str(r, "transid")) ?? $"tx-{Long(r, "id")}";
            if (!seen.Add((channel, external)))
            {
                external = $"{external}#{Long(r, "id")}";
                seen.Add((channel, external));
            }

            var at = Instant(DateTimeValue(r, "date")) ?? now;
            var invoiceId = Long(r, "invoiceid");
            var back = refunds.GetValueOrDefault(Long(r, "id"));
            if (back == 0m && outsByInvoice.TryGetValue(invoiceId, out var outsOfInvoice) && outsOfInvoice.Remove(amount))
            {
                back = amount;
            }

            back = Math.Min(back, amount);
            var kept = amount - back;

            // La imputación se decide acá, con la cabecera ya importada: nunca por encima de lo que la cabecera dice
            // imputado, y una factura anulada o reembolsada no recibe nada. En otra moneda se imputa lo que debía la
            // factura, en su moneda, con la cotización que da el pago.
            var allocation = 0m;
            var rate = 1m;
            if (kept > 0m && invoices.TryGetValue(invoiceId, out var invoice))
            {
                var room = invoiceAllocated.GetValueOrDefault(invoiceId) - allocatedByInvoice.GetValueOrDefault(invoiceId);
                allocation = Math.Max(0m, invoice.Currency == currency ? Math.Min(kept, room) : room);
                rate = invoice.Currency == currency || allocation == 0m ? 1m : Math.Round(allocation / kept, 8);
                allocatedByInvoice[invoiceId] = allocatedByInvoice.GetValueOrDefault(invoiceId) + allocation;
            }

            var status = back >= amount ? PaymentStatus.Refunded : back > 0m ? PaymentStatus.PartiallyRefunded : PaymentStatus.Approved;
            var applied = invoices.TryGetValue(invoiceId, out var inv) && inv.Currency != currency ? (allocation > 0m ? kept : 0m) : allocation;
            var payment = Payment.FromImport(accountId, channel, external, currency, amount, Dec(r, "fees"), at, status, applied);
            if (back > 0m)
            {
                payment.AddRefunded(back);
                refunded++;
            }

            db.Payments.Add(payment);
            batch.Add((payment, invoiceId, at, allocation, rate));
            payments++;
            if (batch.Count >= 1000)
            {
                await SavePayments(batch, ct).ConfigureAwait(false);
            }
        }

        await SavePayments(batch, ct).ConfigureAwait(false);
        if (converted > 0)
        {
            Incidents.Add($"{converted} pagos en pesos de facturas en otra moneda (el origen no dice la moneda): se importan en ARS e imputan lo que debía la factura con la cotización implícita.");
        }

        if (doubtful > 0)
        {
            Incidents.Add($"{doubtful} pagos sin moneda en el origen superan entre 1,5 y 10 veces su factura: quedan en la moneda de la cuenta, revisarlos.");
        }

        // Control por documento, para la conciliación: las filas nunca suman más que lo imputado en la cabecera.
        var over = allocatedByInvoice.Count(a => a.Value > invoiceAllocated.GetValueOrDefault(a.Key));
        Figures[("documents_control", "imputaciones_sobre_cabecera")] = (over, 0m);
        if (over > 0)
        {
            Incidents.Add($"{over} facturas con imputaciones por encima de lo imputado en su cabecera.");
        }

        if (refunded > 0)
        {
            Incidents.Add($"{refunded} pagos con una salida por el mismo importe en el origen se importan devueltos, sin imputación.");
        }

        if (outs > refunded)
        {
            Incidents.Add($"{outs - refunded} movimientos de salida (reembolsos y retiros) sin pago que los explique quedan solo en el origen.");
        }

        await log.WriteLineAsync($"Pagos: {payments}").ConfigureAwait(false);
    }

    private async Task SavePayments(List<(Payment Payment, long InvoiceId, DateTimeOffset At, decimal Allocated, decimal Rate)> batch, CancellationToken ct)
    {
        await Flush(ct).ConfigureAwait(false);
        foreach (var (payment, invoiceId, at, allocated, rate) in batch)
        {
            if (allocated > 0m)
            {
                db.Allocations.Add(new Allocation(invoiceId, allocated, rate, at, paymentId: payment.Id));
            }
        }

        await Flush(ct).ConfigureAwait(false);
        batch.Clear();
    }

    /// <summary>
    /// La moneda en que entró un movimiento. Con currency en 0 el origen no la dice y vale la de la cuenta, salvo un
    /// importe de 10 veces o más el bruto de su factura en otra moneda: eso son pesos que cobró una pasarela local. La
    /// usan la importación y el paralelo, que repite los cobros del día.
    /// </summary>
    internal static Currency MoveCurrency(DumpRow r, Currency account, Dictionary<long, Currency> currencies, decimal invoiceGross)
    {
        ArgumentNullException.ThrowIfNull(currencies);
        if (currencies.TryGetValue(Long(r, "currency"), out var stated))
        {
            return stated;
        }

        // ponytail: umbral fijo de 10 veces, vale mientras el dólar cueste más de 10 pesos. Si no, comparar con la
        // cotización del mes.
        return account != Currency.Ars && currencies.ContainsValue(Currency.Ars) && invoiceGross > 0m && Dec(r, "amountin") >= 10m * invoiceGross
            ? Currency.Ars
            : account;
    }

    private async Task Credit(CancellationToken ct)
    {
        foreach (var r in Rows("tblclients").Where(r => Dec(r, "credit") > 0m))
        {
            var id = Long(r, "id");
            var currency = accountCurrency[id];
            var amount = Dec(r, "credit");
            db.CreditBalances.Add(new CreditBalance(id, currency) { Balance = amount });
            db.CreditEntries.Add(new CreditEntry(id, currency, amount, "Saldo importado", now));
            Count("credit_balance_by_currency", currency.Code, amount);
        }

        await Flush(ct).ConfigureAwait(false);
    }

    private async Task Fiscal(CancellationToken ct)
    {
        var source = Rows("mod_afip_invoices").ToList();
        var addon = Rows("tbladdonmodules").Where(r => Str(r, "module") == "afip").ToDictionary(r => Str(r, "setting") ?? string.Empty, r => Str(r, "value") ?? string.Empty);
        if (source.Count == 0 && addon.Count == 0)
        {
            return;
        }

        // Los ajustes del addon fiscal mandan. Sin addon, el punto de venta sale de los comprobantes emitidos.
        var issuer = new IssuerProfile
        {
            // Solo dígitos: el CUIT viaja crudo en los comprobantes asociados y el fisco lo exige numérico de 11.
            TaxId = Digits(Blank(addon.GetValueOrDefault("cuit")) ?? source.GroupBy(r => Str(r, "cuit_emisor") ?? string.Empty).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()),
            LegalName = IssuerProfile.ImportedName,
            PointOfSale = Number(addon, "punto_venta") ?? (int)source.Where(r => Str(r, "ws_service") == "wsfe").GroupBy(r => Long(r, "punto_venta")).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(),
            ExportPointOfSale = Number(addon, "punto_venta_export") ?? source.Where(r => Str(r, "ws_service") == "wsfex").GroupBy(r => Long(r, "punto_venta")).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault(),
            TypeMap = """{"A":{"Invoice":1,"DebitNote":2,"CreditNote":3},"B":{"Invoice":6,"DebitNote":7,"CreditNote":8},"E":{"Invoice":19,"DebitNote":20,"CreditNote":21}}""",
            Moment = addon.GetValueOrDefault("fiscalization_trigger") == "on_paid" ? EmissionMoment.AtFirstPayment : EmissionMoment.AtIssue,
            Settings = IssuerSettingsFrom(addon).ToJson(),
            Active = false,
        };
        AddonIncidents(addon);
        db.IssuerProfiles.Add(issuer);
        await Flush(ct).ConfigureAwait(false);
        await Orphans(source, ct).ConfigureAwait(false);

        var keys = new HashSet<(int, int, long)>();
        foreach (var r in source)
        {
            var invoiceId = Long(r, "invoice_id");
            var key = ((int)Long(r, "punto_venta"), (int)Long(r, "voucher_type"), Long(r, "voucher_number"));
            if (!invoices.ContainsKey(invoiceId) || !keys.Add(key))
            {
                Incidents.Add($"Comprobante {key.Item2}-{key.Item1}-{key.Item3}: la factura {invoiceId} no existe o el número está repetido.");
                continue;
            }

            // El comprobante de una nota nativa cuelga de la nota, no de la factura que corrige.
            db.FiscalDocuments.Add(new FiscalDocument
            {
                DocumentId = noteDocuments.TryGetValue(Long(r, "native_note_id"), out var noteDocument) ? noteDocument : invoiceId,
                IssuerId = issuer.Id,
                PointOfSale = key.Item1,
                TypeCode = key.Item2,
                Number = key.Item3,
                Date = Date(r, "fecha_emision") ?? new DateOnly(2000, 1, 1),
                AuthorizationCode = Str(r, "cae") ?? string.Empty,
                AuthorizationDue = Date(r, "cae_vto"),
                Payload = new JsonObject
                {
                    ["imported"] = true,
                    ["doc_type"] = Long(r, "doc_tipo"),
                    ["doc_number"] = Str(r, "doc_nro"),
                    ["vat_condition"] = Long(r, "condicion_iva_receptor_id"),
                    ["currency"] = Str(r, "moneda"),
                    // Cotización 1 en moneda extranjera es el marcador del sistema anterior para "sin cotización".
                    ["exchange_rate"] = Str(r, "moneda") != "PES" && Dec(r, "cotizacion") == 1m ? null : Str(r, "cotizacion"),
                    ["same_currency_payment"] = Str(r, "can_mis_mon_ext") == "S",
                    ["total"] = Str(r, "monto_total"),
                    ["net"] = Str(r, "monto_neto"),
                    ["vat"] = Str(r, "monto_iva"),
                }.ToJsonString(),
                Imported = true,
                CreatedAt = now,
            });
            Count("fiscal_by_type", $"{key.Item2}\t{Str(r, "moneda")}", Dec(r, "monto_total"));
        }

        await Flush(ct).ConfigureAwait(false);

        // La numeración sigue donde quedó: la serie arranca en el último número importado de cada punto de venta y tipo.
        foreach (var g in source.GroupBy(r => ((int)Long(r, "punto_venta"), (int)Long(r, "voucher_type"))))
        {
            db.FiscalSeries.Add(new FiscalSeries(issuer.Id, g.Key.Item1, g.Key.Item2)
            {
                LastNumber = g.Max(r => Long(r, "voucher_number")),
                LastDate = g.Max(r => Date(r, "fecha_emision")),
            });
        }

        await Flush(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Comprobantes autorizados cuya factura ya no está en el origen. Son registros legales y no se pierden: cada
    /// uno queda colgado de un documento importado de reemplazo, anulado, con el ID de la factura original.
    /// </summary>
    private async Task Orphans(List<DumpRow> source, CancellationToken ct)
    {
        var created = 0;
        foreach (var g in source.Where(r => !invoices.ContainsKey(Long(r, "invoice_id"))).GroupBy(r => Long(r, "invoice_id")))
        {
            var first = g.OrderBy(r => Long(r, "id")).First();
            var accountId = Long(first, "client_id");
            var currency = Str(first, "moneda") == "DOL" ? Currency.Usd : Currency.Ars;
            if (g.Key <= 0 || accountId <= 0)
            {
                Incidents.Add($"Comprobantes de la factura {g.Key}: sin factura ni cuenta de origen, no se importan.");
                continue;
            }

            if (!accountCurrency.ContainsKey(accountId))
            {
                // La cuenta se borró en el origen: queda una de reemplazo, cerrada, para no perder el registro fiscal.
                var placeholder = new Account($"Cuenta borrada en el origen {accountId}", string.Empty, currency, now) { Status = AccountStatus.Closed };
                placeholder.SetImportedId(accountId);
                db.Accounts.Add(placeholder);
                accountCurrency[accountId] = currency;
                Incidents.Add($"Cuenta {accountId}: ya no estaba en el origen y tenía comprobantes, quedó una de reemplazo cerrada.");
            }

            var total = Dec(first, "monto_total");
            var amounts = Long(first, "voucher_type") is 19 or 21
                ? new TaxBreakdown([], [], total, 0m)
                : new TaxBreakdown(Dec(first, "monto_iva") > 0m ? [new AliquotTotal(0.21m, Dec(first, "monto_neto"), Dec(first, "monto_iva"))] : [], [], 0m,
                    total - (Dec(first, "monto_iva") > 0m ? Dec(first, "monto_neto") + Dec(first, "monto_iva") : 0m));
            var date = Date(first, "fecha_emision") ?? new DateOnly(2000, 1, 1);
            var doc = Document.FromImport(g.Key, DocumentKind.Invoice, accountId, currency, g.Key, date, date, amounts, 0m, total, "{}", now, sourceStatus: "Orphan");
            ImportLines(doc, amounts, [new SourceLine(LineKind.Manual, "Comprobante sin factura en el origen", total, amounts.Aliquots.Count > 0, null)]);
            db.Documents.Add(doc);
            invoices[g.Key] = (currency, "Orphan");
            created++;
        }

        await Flush(ct).ConfigureAwait(false);
        if (created > 0)
        {
            Incidents.Add($"{created} facturas ya no estaban en el origen pero tenían comprobante: quedaron como documentos de reemplazo anulados.");
        }
    }

    /// <summary>Tablas con IDs conservados: sus secuencias tienen que arrancar arriba del máximo importado.</summary>
    private static readonly string[] SequenceResets =
    [
        .. new[]
        {
            "accounts.accounts", "catalog.products", "catalog.product_groups", "catalog.option_groups", "catalog.options",
            "catalog.option_values", "catalog.coupons", "services.services", "orders.orders", "billing.documents", "domains.domains",
            "support.departments", "support.tickets", "affiliates.affiliates", "affiliates.referrals",
            "content.categories", "content.articles", "content.announcements", "content.downloads", "content.status_events",
        }.Select(SequenceReset),
    ];

    private static string SequenceReset(string table) =>
        $"SELECT setval(pg_get_serial_sequence('{table}', 'id'), GREATEST((SELECT COALESCE(max(id), 0) FROM {table}), 1))";

    /// <summary>Las secuencias y la numeración comercial arrancan arriba del máximo importado (D-17).</summary>
    /// <summary>
    /// Las listas de bloqueo del sistema anterior (diseno/fraude-y-acceso.md, sección 4): los dominios de correo y las
    /// IPs con su vencimiento. Una IP ya vencida no se trae. Lo que no es un dominio o una IP válida queda contado en el
    /// log y no se trae.
    /// </summary>
    private async Task Blocklists(CancellationToken ct)
    {
        const string Imported = "Importado del sistema anterior.";
        var now = DateTimeOffset.UtcNow;
        var entries = new Dictionary<(BlockKind, string), BlockedEntry>();
        var skipped = 0;
        foreach (var r in Rows("tblbannedemails"))
        {
            Add(BlockKind.Domain, Str(r, "domain"), Imported, null);
        }

        foreach (var r in Rows("tblbannedips"))
        {
            var expires = Instant(DateTimeValue(r, "expires"));
            if (expires is not { } until || until > now)
            {
                var reason = Blank(Str(r, "reason")) ?? Imported;
                Add(BlockKind.Ip, Str(r, "ip"), reason.Length > 300 ? reason[..300] : reason, expires);
            }
        }

        db.Set<BlockedEntry>().AddRange(entries.Values);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await log.WriteLineAsync($"Listas de bloqueo: {entries.Count} entradas, {skipped} sin un dominio o una IP válida.").ConfigureAwait(false);

        void Add(BlockKind kind, string? value, string reason, DateTimeOffset? expires)
        {
            try
            {
                var (normalized, network) = AccessGuard.Normalize(kind, value ?? string.Empty);
                entries.TryAdd((kind, normalized), new BlockedEntry
                {
                    Kind = kind, Value = normalized, Network = network, Reason = reason, Source = BlockSource.Staff,
                    CreatedBy = "import", CreatedAt = now, ExpiresAt = expires,
                });
            }
            catch (ValidationException)
            {
                skipped++;
            }
        }
    }

    /// <summary>
    /// Los vínculos de ingreso con Google del sistema anterior (diseno/ingreso-clientes.md, sección 6): cada fila de
    /// tblauthn_account_links del proveedor de Google entra a identity.external_logins con su identificador y el correo de
    /// su metadata, atada al usuario importado con su mismo ID. El proveedor Google queda cargado y apagado, con el ID del
    /// cliente del origen si tiene la forma de uno: el secreto no se importa y se carga en el panel. Un vínculo cuyo
    /// usuario no está en la base no entra y queda contado.
    /// </summary>
    private async Task ExternalLogins(CancellationToken ct)
    {
        const string Source = "google_signin";
        var links = Rows("tblauthn_account_links").ToList();
        var config = Rows("tblauthn_config").Where(r => Str(r, "provider") == Source)
            .GroupBy(r => Str(r, "setting") ?? string.Empty).ToDictionary(g => g.Key, g => Blank(Str(g.Last(), "value")));
        if (links.Count == 0 && config.Count == 0)
        {
            return;
        }

        var google = await db.Set<ExternalProvider>().SingleOrDefaultAsync(p => p.Key == ExternalProvider.GoogleKey, ct).ConfigureAwait(false);
        if (google is null)
        {
            google = new ExternalProvider
            {
                Key = ExternalProvider.GoogleKey, Kind = ExternalProviderKind.Google, Name = "Google", Authority = ExternalProvider.GoogleAuthority,
                ClientId = config.GetValueOrDefault("ClientId") is { } clientId && GoogleClientId().IsMatch(clientId) ? clientId : string.Empty,
                Enabled = false, Version = 1, UpdatedAt = now, UpdatedBy = "import",
            };
            db.Add(google);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var users = (await db.Set<PortalUser>().AsNoTracking().Select(u => u.Id).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        var imported = new Dictionary<string, ExternalLogin>(StringComparer.Ordinal);
        var withoutUser = 0;
        var others = 0;
        foreach (var r in links)
        {
            if (Str(r, "provider") != Source)
            {
                others++;
                continue;
            }

            var userId = Long(r, "user_id");
            if (Blank(Str(r, "remote_user_id")) is not { Length: <= 255 } subject || !users.Contains(userId))
            {
                withoutUser++;
                continue;
            }

            imported.TryAdd(subject, new ExternalLogin
            {
                ProviderId = google.Id, Subject = subject, UserId = userId, Email = MetadataEmail(Str(r, "metadata")),
                CreatedAt = Instant(DateTimeValue(r, "created_at")) ?? now,
            });
        }

        db.Set<ExternalLogin>().AddRange(imported.Values);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (withoutUser > 0)
        {
            Incidents.Add($"{withoutUser} vínculos de Google sin su usuario importado o sin identificador: esos clientes confirman el vínculo una vez, con su contraseña o con el enlace al correo.");
        }

        await log.WriteLineAsync($"Vínculos de ingreso con Google: {imported.Count} importados, {withoutUser} sin su usuario y {others} de otros proveedores. El proveedor Google queda apagado: cargá el secreto en /admin/seguridad/proveedores y agregá la dirección de vuelta nueva en el cliente de Google.").ConfigureAwait(false);
    }

    /// <summary>El correo de la metadata del vínculo, un JSON del origen. Null si no está o no se entiende.</summary>
    private static string? MetadataEmail(string? metadata)
    {
        try
        {
            return metadata is { Length: > 0 } && JsonNode.Parse(metadata) is JsonObject json && json["email"] is JsonValue value
                && value.TryGetValue<string>(out var email) && email.Trim() is { Length: > 3 and <= 320 } trimmed && trimmed.Contains('@', StringComparison.Ordinal) ? trimmed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9]+-[a-z0-9]+\.apps\.googleusercontent\.com$")]
    private static partial System.Text.RegularExpressions.Regex GoogleClientId();

    private async Task Sequences(CancellationToken ct)
    {
        foreach (var sql in SequenceResets)
        {
            await db.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
        }

        foreach (var kind in new[] { "Invoice", "CreditNote", "DebitNote" })
        {
            var used = usedNoteNumbers.GetValueOrDefault(Enum.Parse<DocumentKind>(kind));
            await db.Database.ExecuteSqlAsync(
                $"UPDATE billing.counters SET next = GREATEST((SELECT COALESCE(max(number), 0) FROM billing.documents WHERE kind = {kind}), {used}) + 1 WHERE kind = {kind}", ct).ConfigureAwait(false);
        }
    }

    private async Task Flush(CancellationToken ct)
    {
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    private void Count(string section, string key, decimal amount = 0m)
    {
        var current = Figures.GetValueOrDefault((section, key));
        Figures[(section, key)] = (current.Count + 1, current.Amount + amount);
    }

    private IEnumerable<DumpRow> Rows(string table) => rows.TryGetValue(table, out var list) ? list : [];

    /// <summary>
    /// Los ajustes de facturación del sistema anterior traducidos a la configuración propia, para cargarlos a mano.
    /// Las reglas que el origen tenía en código propio (por ejemplo anular impagas) no están acá.
    /// </summary>
    public JsonObject BillingSettings() => BillingSettings(Rows("tblconfiguration"));

    /// <summary>
    /// Los ajustes de seguridad del panel que equivalen a los del sistema anterior (diseno/fraude-y-acceso.md, sección 2):
    /// los minutos de bloqueo por intentos y el antifraude que no evalúa a los clientes existentes. Solo trae lo que el
    /// origen tiene.
    /// </summary>
    public JsonObject SecuritySettingsReport() => SecuritySettingsReport(Rows("tblconfiguration"));

    /// <summary>Lo mismo, a partir de las filas de la tabla de configuración de cualquier volcado.</summary>
    public static JsonObject SecuritySettingsReport(IEnumerable<DumpRow> configuration)
    {
        var config = configuration.GroupBy(r => Str(r, "setting") ?? string.Empty).ToDictionary(g => g.Key, g => Str(g.Last(), "value")?.Trim() ?? string.Empty);
        var settings = new JsonObject();
        if (int.TryParse(config.GetValueOrDefault("InvalidLoginBanLength"), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
        {
            settings["AccountMinutes"] = minutes;
            settings["StaffAccountMinutes"] = minutes;
            settings["IpMinutes"] = minutes;
        }

        if (config.ContainsKey("SkipFraudForExisting"))
        {
            settings["FraudSkipExisting"] = config["SkipFraudForExisting"] == "on";
        }

        return settings;
    }

    /// <summary>Lo mismo, a partir de las filas de la tabla de configuración de cualquier volcado.</summary>
    public static JsonObject BillingSettings(IEnumerable<DumpRow> configuration)
    {
        var config = configuration.GroupBy(r => Str(r, "setting") ?? string.Empty).ToDictionary(g => g.Key, g => Str(g.Last(), "value")?.Trim() ?? string.Empty);
        int? Days(string key) => int.TryParse(config.GetValueOrDefault(key), CultureInfo.InvariantCulture, out var n) ? n : null;
        var lead = Days("CreateInvoiceDaysBefore");
        var byMonths = new JsonObject();
        foreach (var (months, key) in new[] { (1, "Monthly"), (3, "Quarterly"), (6, "SemiAnnually"), (12, "Annually"), (24, "Biennially"), (36, "Triennially") })
        {
            if (Days($"CreateInvoiceDaysBefore{key}") is { } d && d != lead)
            {
                byMonths[months.ToString(CultureInfo.InvariantCulture)] = d;
            }
        }

        var settings = new JsonObject
        {
            ["RenewalLeadDays"] = lead,
            ["RenewalLeadDaysByMonths"] = byMonths,
            ["ContinuousInvoicing"] = config.GetValueOrDefault("ContinuousInvoiceGeneration") == "on",
            ["InvoiceDueDays"] = Days("OrderDaysGrace"),
            ["DomainRenewalLeadDays"] = Days("CreateDomainInvoiceDaysBefore") ?? lead,
        };
        if (config.GetValueOrDefault("AutoSuspension") == "on")
        {
            settings["GraceDays"] = Days("AutoSuspensionDays");
        }

        if (config.GetValueOrDefault("AutoTermination") == "on")
        {
            settings["TerminationDays"] = Days("AutoTerminationDays");
        }

        return settings;
    }

    /// <summary>Exportación y moneda extranjera según el addon fiscal: mapa de países, forma de pago, idioma y medios que cancelan en la misma moneda.</summary>
    private static IssuerSettings IssuerSettingsFrom(Dictionary<string, string> addon)
    {
        var countries = new Dictionary<string, ExportCountry>();
        foreach (var (key, value) in Lines(addon.GetValueOrDefault("export_country_map")))
        {
            var parts = value.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var code) && code > 0
                && long.TryParse(Digits(parts[1]), CultureInfo.InvariantCulture, out var taxId))
            {
                countries[key.ToUpperInvariant()] = new ExportCountry(code, taxId);
            }
        }

        var methods = new Dictionary<string, bool>();
        foreach (var (key, value) in Lines(addon.GetValueOrDefault("export_payment_currency_mode")))
        {
            if (value.ToUpperInvariant() is "S" or "N")
            {
                methods[key.ToLowerInvariant()] = value.ToUpperInvariant() == "S";
            }
        }

        var defaultCountry = Number(addon, "export_default_dst_cmp") is { } defaultCode and > 0
            && long.TryParse(Digits(addon.GetValueOrDefault("export_default_cuit_pais_cliente")), CultureInfo.InvariantCulture, out var defaultTaxId)
            ? new ExportCountry(defaultCode, defaultTaxId)
            : null;
        return new IssuerSettings(
            countries,
            defaultCountry,
            Blank(addon.GetValueOrDefault("export_forma_pago")),
            ExportLanguages.GetValueOrDefault(addon.GetValueOrDefault("export_idioma_cbte")?.Trim() ?? "1", "es"),
            Number(addon, "export_unidad_medida"),
            methods,
            addon.GetValueOrDefault("export_default_payment_mode")?.Trim().ToUpperInvariant() == "S");
    }

    /// <summary>Opciones del addon que el núcleo no reproduce: quedan como incidencias para revisar antes de emitir.</summary>
    private void AddonIncidents(Dictionary<string, string> addon)
    {
        if (addon.Count == 0)
        {
            return;
        }

        string Value(string key) => addon.GetValueOrDefault(key)?.Trim() ?? string.Empty;
        void Check(bool supported, string text)
        {
            if (!supported)
            {
                Incidents.Add($"Addon fiscal: {text}");
            }
        }

        Check(Value("iva_condicion") is "" or "Responsable Inscripto", $"el emisor es {Value("iva_condicion")} y el núcleo emite A, B y E.");
        Check(Value("punto_venta_nc") is var nc && (nc.Length == 0 || nc == Value("punto_venta")), "las notas usan otro punto de venta que las facturas y el núcleo usa uno solo.");
        Check(Value("punto_venta_export_nc") is var enc && (enc.Length == 0 || enc == Value("punto_venta_export")), "las notas de exportación usan otro punto de venta que las facturas.");
        Check(Lines(Value("export_currency_map")).All(l => l.Value.EndsWith("|auto", StringComparison.OrdinalIgnoreCase)), "hay monedas con cotización fija y el núcleo la pide al fisco.");
        Check(Value("local_foreign_currency_mode") is "" or "wsfe_foreign_currency", "convierte a pesos las facturas locales en moneda extranjera y el núcleo las emite en esa moneda.");
        Check(Value("export_tipo_expo") is "" or "2", "el tipo de exportación no es servicios.");
        Check(Value("export_obs_comerciales").Length == 0 && Value("export_actividad_id").Length == 0, "usa observaciones comerciales o actividades en exportación.");
        Check(Value("export_forma_pago").Length > 0, "no tiene forma de pago de exportación y la factura E la exige.");
        Check(Value("cf_identification_threshold").Length == 0, "fija un umbral propio para identificar al consumidor final.");
    }

    private static IEnumerable<(string Key, string Value)> Lines(string? raw) =>
        (raw ?? string.Empty).Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(['=', ':'], 2))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0)
            .Select(p => (p[0].Trim(), p[1].Trim()));

    private static int? Number(Dictionary<string, string> addon, string key) =>
        int.TryParse(addon.GetValueOrDefault(key)?.Trim(), CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string Digits(string? text) => new((text ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

    private static LineKind LineKindOf(string type) => type switch
    {
        "Hosting" or "Domain" or "DomainRegister" or "DomainTransfer" or "Addon" => LineKind.Renewal,
        "Setup" => LineKind.Setup,
        "Upgrade" => LineKind.PlanChange,
        "LateFee" => LineKind.LateFee,
        _ => LineKind.Manual,
    };

    private static BillingCycle Cycle(string? text) => text switch
    {
        "Quarterly" => new BillingCycle(CycleUnit.Month, 3),
        "Semi-Annually" => new BillingCycle(CycleUnit.Month, 6),
        "Annually" => BillingCycle.Annually,
        "Biennially" => new BillingCycle(CycleUnit.Year, 2),
        "Triennially" => new BillingCycle(CycleUnit.Year, 3),
        "Free Account" => BillingCycle.Free,
        "One Time" => BillingCycle.OneTime,
        _ => BillingCycle.Monthly,
    };

    private static string Language(string? source) => source switch
    {
        "english" => "en",
        "italian" => "it",
        "portuguese-br" or "portuguese-pt" => "pt",
        _ => "es",
    };

    /// <summary>Valor de texto de una columna, o nulo si la columna no existe: otras versiones del origen traen menos.</summary>
    internal static string? Str(DumpRow row, string column) =>
        row.Columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)) ? row[column] as string : null;

    [System.Text.RegularExpressions.GeneratedRegex("pass|token|secret|key|licen|clave", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SecretName();

    internal static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Los nombres del catálogo del origen vienen con entidades HTML ("IP&amp;#039;s"): se guardan como texto.</summary>
    private static string? Html(string? value) => value is null ? null : System.Net.WebUtility.HtmlDecode(value).Trim();

    internal static long Long(DumpRow row, string column) =>
        long.TryParse(Str(row, column), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : 0;

    internal static decimal Dec(DumpRow row, string column) =>
        decimal.TryParse(Str(row, column), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    internal static DateOnly? Date(DumpRow row, string column) =>
        Str(row, column) is { Length: >= 10 } s && !s.StartsWith("0000", StringComparison.Ordinal)
        && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static DateTime? DateTimeValue(DumpRow row, string column) =>
        Str(row, column) is { Length: >= 19 } s && !s.StartsWith("0000", StringComparison.Ordinal)
        && DateTime.TryParseExact(s[..19], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Las fechas del origen están en hora de Argentina: se pasan a instantes UTC.</summary>
    private static DateTimeOffset? Instant(DateOnly? date) =>
        date is { } d ? Instant(d.ToDateTime(TimeOnly.MinValue)) : null;

    private static DateTimeOffset? Instant(DateTime? local) =>
        local is { } l ? new DateTimeOffset(DateTime.SpecifyKind(l, DateTimeKind.Unspecified), Zone.GetUtcOffset(l)).ToUniversalTime() : null;
}
