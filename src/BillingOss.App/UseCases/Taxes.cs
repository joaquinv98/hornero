using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Fiscal;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.UseCases;

/// <summary>
/// Lo que hace falta para resolver el impuesto de un renglón y los tributos de un comprobante: categorías, impuestos,
/// reglas vigentes, ajustes y la condición del emisor. Se carga una vez por operación.
/// </summary>
public sealed class TaxContext(IReadOnlyList<TaxCategory> categories, IReadOnlyList<Tax> taxes, IReadOnlyList<TaxRule> rules, TaxSettings settings, int issuerVatCondition)
{
    public IReadOnlyList<TaxCategory> Categories => categories;

    public IReadOnlyList<Tax> Taxes => taxes;

    public IReadOnlyList<TaxRule> Rules => rules;

    public TaxSettings Settings => settings;

    public int IssuerVatCondition => issuerVatCondition;

    public TaxCategory Category(string code) =>
        categories.FirstOrDefault(c => c.Code == code)
        ?? throw new InvalidOperationException($"No existe la categoría de impuesto {code}. Si no sabés, elegí servicios digitales y hosting.");

    /// <summary>El IVA de un renglón del producto para el receptor, en la fecha fiscal (la de emisión).</summary>
    public LineTax Line(Product product, ReceiverSnapshot receiver, long accountId, DateOnly on)
    {
        ArgumentNullException.ThrowIfNull(product);
        return Line(product.TaxCategory, receiver, accountId, on);
    }

    /// <summary>El impuesto de un renglón que no sale de un producto, como la renovación de un dominio.</summary>
    public LineTax Line(string category, ReceiverSnapshot receiver, long accountId, DateOnly on) =>
        TaxEngine.ResolveLine(Category(category), Receiver(receiver, accountId), rules, on, issuerVatCondition);

    public IReadOnlyList<DocumentTaxAmount> DocumentTaxes(Document doc, ReceiverSnapshot receiver, DateOnly on)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return TaxEngine.ComputeDocumentTaxes(taxes, rules, Receiver(receiver, doc.AccountId), doc.TaxedLines(), on);
    }

    public static TaxReceiver Receiver(ReceiverSnapshot receiver, long? accountId)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        return new TaxReceiver(receiver.Country, receiver.Province, receiver.VatCondition, accountId, receiver.ValidatedBusiness);
    }
}

public sealed record SimulationInput(string Country, string? Province, int VatCondition, string Category, decimal Price, Currency Currency, PriceMode Mode, bool ValidatedBusiness = false, long? AccountId = null);

public sealed record Simulation(string Letter, TaxTreatment Treatment, decimal Rate, string Reason, decimal Net, decimal Tax, IReadOnlyList<DocumentTaxAmount> Taxes, IReadOnlyList<string> Legends, decimal Total);

/// <summary>Simulador del panel (sección 3.11 del diseño fiscal): un cliente tipo, un producto y qué paga, con el motivo de cada cosa.</summary>
public sealed class TaxSimulator(BillingDbContext db, BillingCore core)
{
    public async Task<Simulation> Run(SimulationInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var ctx = await core.Taxes(ct).ConfigureAwait(false);
        var on = core.Today();
        var category = ctx.Category(input.Category);
        var receiver = new TaxReceiver(input.Country, input.Province, input.VatCondition, input.AccountId, input.ValidatedBusiness);
        var line = TaxEngine.ResolveLine(category, receiver, ctx.Rules, on, ctx.IssuerVatCondition);
        var breakdown = TaxCalculator.Compute(input.Mode, [new TaxableLine("0", Rounding.Commercial(input.Price), line.Treatment, line.Rate)]);
        var taxes = TaxEngine.ComputeDocumentTaxes(ctx.Taxes, ctx.Rules, receiver, [new TaxedLine(category.Code, breakdown.Lines[0], line.Treatment)], on);
        var issuer = await db.IssuerProfiles.AsNoTracking().SingleOrDefaultAsync(i => i.Active, ct).ConfigureAwait(false);
        var letter = FiscalTypes.LetterFor(ctx.IssuerVatCondition, issuer?.ClassAConditions ?? "[1,6,13,16]", input.Country, input.VatCondition, breakdown.Tax > 0m);
        var legends = new[] { line.Legend }.Concat(taxes.Select(t => t.Legend)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        // El neto es el subtotal sin IVA: gravado, exento y no gravado. Un renglón exento también tiene neto.
        return new Simulation(letter, line.Treatment, line.Rate, line.Reason, breakdown.NetTaxed + breakdown.Exempt + breakdown.NotTaxed, breakdown.Tax, taxes, legends, breakdown.Total + taxes.Where(t => t.AddsToTotal).Sum(t => t.Amount));
    }
}

/// <summary>Las tres preguntas del asistente de impuestos y lo que cada respuesta siembra.</summary>
public sealed record TaxPreset(string IssuerCountry, int IssuerVatCondition, bool SellsAbroad, bool SellsToEuConsumers, bool SellsToUkConsumers, bool PerceivesRg2126, bool PaysGrossIncomeInCaba);

/// <summary>Una regla general nueva, con vigencia. Los campos nulos valen para cualquiera.</summary>
public sealed record TaxRuleInput(string TaxCode, string? Country, string? Province, int? VatCondition, bool? BusinessWithValidatedNumber, string? Category, TaxTreatment Outcome, decimal Rate, string? Reason, string? Legend, DateOnly ValidFrom, DateOnly? ValidTo = null, string? Note = null);

/// <summary>
/// Una regla de cuenta (sección 3.2 del diseño fiscal): gana siempre dentro de su alcance, con motivo obligatorio,
/// vigencia y, si la hay, la constancia adjunta con su número. Quien la acepta es el staff, y queda en la auditoría.
/// </summary>
public sealed record AccountRuleInput(long AccountId, string TaxCode, TaxTreatment Outcome, decimal Rate, string Reason, string? CertificateRef, DateOnly ValidFrom, DateOnly? ValidTo = null, string? Category = null, string? Note = null);

/// <summary>Algo que puede estar mal en la configuración impositiva, con un dato para mostrar.</summary>
public sealed record TaxWarning(string Code, string Detail);

/// <summary>
/// Presets y avisos de la sección "Impuestos" (3.11 del diseño fiscal). Sin reglas el cálculo falla, y eso no puede
/// ser lo primero que vea quien recién instala: cada respuesta deja un preset completo de categorías, impuestos y
/// reglas. Nada se edita: el preset cierra las reglas generales vigentes y abre las nuevas.
/// </summary>
public sealed class TaxSetupService(BillingDbContext db, BillingCore core, AuditLog audit)
{
    /// <summary>Alícuotas generales de IVA de la Unión Europea (sección 3.9 del diseño fiscal), vigentes al 2026-10-28.</summary>
    public static IReadOnlyDictionary<string, decimal> EuVatRates { get; } = new Dictionary<string, decimal>
    {
        ["AT"] = 0.20m, ["BE"] = 0.21m, ["BG"] = 0.20m, ["HR"] = 0.25m, ["CY"] = 0.19m, ["CZ"] = 0.21m, ["DK"] = 0.25m,
        ["EE"] = 0.24m, ["FI"] = 0.255m, ["FR"] = 0.20m, ["DE"] = 0.19m, ["GR"] = 0.24m, ["HU"] = 0.27m, ["IE"] = 0.23m,
        ["IT"] = 0.22m, ["LV"] = 0.21m, ["LT"] = 0.21m, ["LU"] = 0.17m, ["MT"] = 0.18m, ["NL"] = 0.21m, ["PL"] = 0.23m,
        ["PT"] = 0.23m, ["RO"] = 0.21m, ["SK"] = 0.23m, ["SI"] = 0.22m, ["ES"] = 0.21m, ["SE"] = 0.25m,
    };

    /// <summary>Códigos de alícuota de ARCA por tasa (FEParamGetTiposIva).</summary>
    public static IReadOnlyDictionary<decimal, int> ArcaVatIds { get; } = new Dictionary<decimal, int> { [0m] = 3, [0.105m] = 4, [0.21m] = 5, [0.27m] = 6, [0.05m] = 8, [0.025m] = 9 };

    public async Task ApplyPreset(TaxPreset preset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.IssuerCountry is not { Length: 2 })
        {
            throw new ValidationException("El país del emisor va en ISO 3166-1 de dos letras.");
        }

        if (!VatConditions.IssuerConditions.Contains(preset.IssuerVatCondition))
        {
            throw new ValidationException("La condición del emisor es responsable inscripto, monotributista o exento.");
        }

        var today = core.Today();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await EnsureCatalog(today, ct).ConfigureAwait(false);
        var settings = await core.Settings(ct).ConfigureAwait(false);
        settings.IssuerCountry = preset.IssuerCountry;
        var issuer = await db.IssuerProfiles.SingleOrDefaultAsync(i => i.Active, ct).ConfigureAwait(false);
        if (issuer is not null)
        {
            var exports = issuer.Exports || preset.SellsAbroad;
            if (issuer.VatCondition != preset.IssuerVatCondition || issuer.Exports != exports)
            {
                // La clase de comprobante sale de la condición y la E de la exportación: el mapa de tipos se rehace con
                // ellas, si no la emisión busca un tipo que el mapa no tiene. Las series que falten se crean al emitir.
                issuer.TypeMap = FiscalSetupService.DefaultTypeMap(preset.IssuerVatCondition, issuer.ClassAWithLegend, exports);
            }

            issuer.VatCondition = preset.IssuerVatCondition;
            issuer.Exports = exports;
        }

        // Las reglas generales vigentes se cierran hoy. Las de cuenta quedan, son del cliente, y las de los regímenes de
        // ingresos brutos también: no son del preset, se cargan y se cierran desde Percepciones.
        foreach (var rule in await ReplacedByPreset(settings, today).ToListAsync(ct).ConfigureAwait(false))
        {
            rule.ValidTo = today;
        }

        var actor = Actor.Current.Id;
        var rules = new List<TaxRule>();
        if (VatConditions.IssuesClassC(preset.IssuerVatCondition))
        {
            rules.Add(new TaxRule { Country = preset.IssuerCountry, Outcome = TaxTreatment.NotTaxed, Reason = TaxReasons.Monotax, ValidFrom = today, CreatedBy = actor });
        }
        else
        {
            rules.Add(new TaxRule { Country = preset.IssuerCountry, Outcome = TaxTreatment.Taxed, Rate = 0.21m, ValidFrom = today, CreatedBy = actor });
            foreach (var condition in new[] { VatConditions.ResponsableInscripto, VatConditions.Monotributo, VatConditions.MonotributistaSocial, VatConditions.MonotributoPromovido })
            {
                rules.Add(new TaxRule { Country = preset.IssuerCountry, Category = TaxCategories.Telecom, VatCondition = condition, Outcome = TaxTreatment.Taxed, Rate = 0.27m, ValidFrom = today, CreatedBy = actor });
            }

            if (preset.PerceivesRg2126)
            {
                rules.Add(new TaxRule { TaxCode = Tax.VatPerceptionRg2126, Country = preset.IssuerCountry, VatCondition = VatConditions.NoCategorizado, Outcome = TaxTreatment.Taxed, Rate = 0.105m, Reason = TaxReasons.Perception, ValidFrom = today, CreatedBy = actor });
                rules.Add(new TaxRule { TaxCode = Tax.VatPerceptionRg2126, Country = preset.IssuerCountry, VatCondition = VatConditions.NoCategorizado, Category = TaxCategories.Telecom, Outcome = TaxTreatment.Taxed, Rate = 0.135m, Reason = TaxReasons.Perception, ValidFrom = today, CreatedBy = actor });
            }

            if (preset.PaysGrossIncomeInCaba)
            {
                // Solo se informa, desde el 01/01/2027 (Res. 169/AGIP/2026 y 339/AGIP/2026). La alícuota la confirma el contador.
                rules.Add(new TaxRule { TaxCode = "isib_caba", Country = "AR", Province = "AR-C", VatCondition = VatConditions.ConsumidorFinal, Outcome = TaxTreatment.Taxed, Rate = 0m, Reason = TaxReasons.Informational, Legend = "ALÍCUOTA ISIB CABA", ValidFrom = new DateOnly(2027, 1, 1), CreatedBy = actor });
            }
        }

        // El resto del mundo: exportación de servicios, exenta (Ley de IVA, arts. 1 y 43).
        rules.Add(new TaxRule { Outcome = TaxTreatment.Exempt, Reason = TaxReasons.Export, ValidFrom = today, CreatedBy = actor });

        if (preset.SellsToEuConsumers)
        {
            foreach (var (country, rate) in EuVatRates)
            {
                rules.Add(new TaxRule { Country = country, Outcome = TaxTreatment.Taxed, Rate = rate, ValidFrom = today, CreatedBy = actor });
                rules.Add(new TaxRule { Country = country, BusinessWithValidatedNumber = true, Outcome = TaxTreatment.Exempt, Reason = TaxReasons.ReverseCharge, Legend = "Reverse charge, article 196 of Directive 2006/112/EC", ValidFrom = today, CreatedBy = actor });
            }
        }

        if (preset.SellsToUkConsumers)
        {
            rules.Add(new TaxRule { Country = "GB", Outcome = TaxTreatment.Taxed, Rate = 0.20m, ValidFrom = today, CreatedBy = actor });
            rules.Add(new TaxRule { Country = "GB", BusinessWithValidatedNumber = true, Outcome = TaxTreatment.Exempt, Reason = TaxReasons.ReverseCharge, Legend = "Reverse charge, VAT Notice 741A", ValidFrom = today, CreatedBy = actor });
        }

        db.TaxRules.AddRange(rules);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("taxes.preset", null, new { preset.IssuerCountry, preset.IssuerVatCondition, rules = rules.Count }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Cuántas reglas generales cierra el preset si se aplica hoy: Empezar lo avisa antes.</summary>
    public async Task<int> PresetReplaces(CancellationToken ct = default) =>
        await ReplacedByPreset(await core.Settings(ct).ConfigureAwait(false), core.Today()).CountAsync(ct).ConfigureAwait(false);

    private IQueryable<TaxRule> ReplacedByPreset(TaxSettings settings, DateOnly today)
    {
        var regimes = settings.Regimes().Select(r => r.TaxCode).ToList();
        return GeneralFrom(today).Where(r => !regimes.Contains(r.TaxCode));
    }

    /// <summary>
    /// Las reglas generales que siguen vigentes en esa fecha o después, también las que ya tenían fin. Al reemplazarlas
    /// se cierran todas: una que quede vigente empata con la nueva de los mismos filtros y el cálculo falla.
    /// </summary>
    private IQueryable<TaxRule> GeneralFrom(DateOnly on) => db.TaxRules.Where(r => r.AccountId == null && (r.ValidTo == null || r.ValidTo > on));

    /// <summary>Categorías e impuestos de fábrica, si faltan. La migración los siembra: esto los repone.</summary>
    public async Task EnsureCatalog(DateOnly today, CancellationToken ct)
    {
        var existing = await db.TaxCategories.Select(c => c.Code).ToListAsync(ct).ConfigureAwait(false);
        foreach (var category in FactoryCategories(today).Where(c => !existing.Contains(c.Code)))
        {
            db.TaxCategories.Add(category);
        }

        var taxes = await db.Taxes.Select(t => t.Code).ToListAsync(ct).ConfigureAwait(false);
        foreach (var tax in FactoryTaxes(today).Where(t => !taxes.Contains(t.Code)))
        {
            db.Taxes.Add(tax);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public static IReadOnlyList<TaxCategory> FactoryCategories(DateOnly from) =>
    [
        new() { Code = TaxCategories.Digital, Name = "Servicios digitales y hosting", Explanation = "Hosting, VPS, game servers, bare metal y abonos. La alícuota general.", ValidFrom = from },
        new() { Code = TaxCategories.Telecom, Name = "Telecomunicaciones", Explanation = "Telefonía y enlaces, si el contador los ubica acá: 27 % a responsables inscriptos y monotributistas fuera de una vivienda.", ValidFrom = from },
        new() { Code = TaxCategories.Exempt, Name = "Exento por ley", Explanation = "Lo que la Ley de IVA exime. Va como exento ante el fisco.", Treatment = TaxTreatment.Exempt, Reason = TaxReasons.ExemptByLaw, ValidFrom = from },
        new() { Code = TaxCategories.Untaxed, Name = "No gravado", Explanation = "Lo que no es una operación gravada. Va como no gravado ante el fisco.", Treatment = TaxTreatment.NotTaxed, Reason = TaxReasons.Untaxed, ValidFrom = from },
    ];

    public static IReadOnlyList<Tax> FactoryTaxes(DateOnly from) =>
    [
        new() { Code = Tax.Vat, Name = "IVA", Level = TaxLevel.Line, Base = TaxBase.Net, ValidFrom = from },
        new() { Code = Tax.VatPerceptionRg2126, Name = "Percepción de IVA RG 2126", Level = TaxLevel.Document, Base = TaxBase.NetPlusLineTaxes, FiscalCode = 13, Group = "Percepciones", ValidFrom = from },
        new() { Code = "isib_caba", Name = "Alícuota ISIB CABA", Level = TaxLevel.Document, Base = TaxBase.Net, AddsToTotal = false, Group = "Leyendas", ValidFrom = from },
    ];

    /// <summary>Si hoy rige una regla general de la percepción de IVA de la RG 2126: sin ella la ficha no acepta la condición 7.</summary>
    public Task<bool> Rg2126Active(CancellationToken ct = default)
    {
        var today = core.Today();
        return db.TaxRules.AnyAsync(r => r.TaxCode == Tax.VatPerceptionRg2126 && r.AccountId == null && r.ValidFrom <= today && (r.ValidTo == null || r.ValidTo > today), ct);
    }

    /// <summary>Lo que puede estar mal, con un enlace para arreglarlo (sección 3.11 del diseño fiscal).</summary>
    public async Task<IReadOnlyList<TaxWarning>> Warnings(CancellationToken ct = default)
    {
        var today = core.Today();
        var ctx = await core.Taxes(ct).ConfigureAwait(false);

        // Solo las reglas vigentes hoy: una cerrada ya no cobra nada.
        var current = ctx.Rules.Where(r => r.ValidFrom <= today && (r.ValidTo is null || today < r.ValidTo)).ToList();
        var vat = current.Where(r => r.TaxCode == Tax.Vat && r.AccountId == null).ToList();
        var result = new List<TaxWarning>();

        if (VatConditions.IssuesClassC(ctx.IssuerVatCondition) && vat.Any(r => r.Outcome == TaxTreatment.Taxed && r.Rate > 0m))
        {
            result.Add(new TaxWarning("monotributista_con_iva", vat.Count(r => r.Outcome == TaxTreatment.Taxed && r.Rate > 0m).ToString(CultureInfo.InvariantCulture)));
        }

        var telecom = await db.Products.CountAsync(p => p.TaxCategory == TaxCategories.Telecom, ct).ConfigureAwait(false);
        if (telecom > 0 && !vat.Any(r => r.Category == TaxCategories.Telecom && r.Rate == 0.27m))
        {
            result.Add(new TaxWarning("telecom_sin_27", telecom.ToString(CultureInfo.InvariantCulture)));
        }

        var countries = await db.TaxIdentities.Where(t => t.ValidTo == null && t.Country != ctx.Settings.IssuerCountry).Select(t => t.Country).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var uncovered = countries.Where(c => !vat.Any(r => r.Country == c)).Order(StringComparer.Ordinal).ToList();
        if (uncovered.Count > 0)
        {
            result.Add(new TaxWarning("pais_sin_regla", string.Join(", ", uncovered)));
        }

        var uncategorized = await db.TaxIdentities.CountAsync(t => t.ValidTo == null && t.VatCondition == VatConditions.NoCategorizado, ct).ConfigureAwait(false);
        if (uncategorized > 0 && !current.Any(r => r.TaxCode == Tax.VatPerceptionRg2126 && r.AccountId == null))
        {
            result.Add(new TaxWarning("no_categorizado_sin_percepcion", uncategorized.ToString(CultureInfo.InvariantCulture)));
        }

        // Una provincia que no es un código ISO 3166-2 no coincide con ninguna regla por subdivisión ni con un régimen de ingresos brutos.
        var provinces = await db.TaxIdentities.Where(t => t.ValidTo == null && t.Country == "AR" && t.Province != null && t.Province != "")
            .GroupBy(t => t.Province!).Select(g => new { Province = g.Key, Count = g.Count() }).ToListAsync(ct).ConfigureAwait(false);
        var unknown = provinces.Where(p => !ArgentinaProvinces.IsCode(p.Province)).Sum(p => p.Count);
        if (unknown > 0)
        {
            result.Add(new TaxWarning("provincia_fuera_de_tabla", unknown.ToString(CultureInfo.InvariantCulture)));
        }

        var soon = today.AddDays(30);
        var expiring = await db.TaxRules.Where(r => r.AccountId != null && r.ValidTo != null && r.ValidTo <= soon && r.ValidTo > today).CountAsync(ct).ConfigureAwait(false);
        if (expiring > 0)
        {
            result.Add(new TaxWarning("regla_de_cuenta_por_vencer", expiring.ToString(CultureInfo.InvariantCulture)));
        }

        var credentials = await db.FiscalCredentials.Where(c => c.Status == CredentialStatus.Active).ToListAsync(ct).ConfigureAwait(false);
        foreach (var credential in credentials.Where(c => c.DaysLeft(today) is <= 30))
        {
            result.Add(new TaxWarning("certificado_por_vencer", string.Create(CultureInfo.InvariantCulture, $"{credential.Alias}: {credential.DaysLeft(today)}")));
        }

        var issuer = await db.IssuerProfiles.AsNoTracking().SingleOrDefaultAsync(i => i.Active, ct).ConfigureAwait(false);
        if (issuer?.LastTest is { } lastTest && JsonNode.Parse(lastTest)?["vat_rates"] is JsonArray rates && rates.Count > 0)
        {
            var arcaRates = rates.Where(r => r?["valid_to"] is null || DateOnly.Parse(r!["valid_to"]!.GetValue<string>(), CultureInfo.InvariantCulture) > today)
                .Select(r => r!["id"]!.GetValue<int>()).ToHashSet();
            var stale = vat.Where(r => r.Outcome == TaxTreatment.Taxed && ArcaVatIds.TryGetValue(r.Rate, out var id) && !arcaRates.Contains(id)).Select(r => r.Rate).Distinct().ToList();
            if (stale.Count > 0)
            {
                result.Add(new TaxWarning("alicuota_no_vigente_en_arca", string.Join(", ", stale.Select(r => r.ToString("P1", CultureInfo.InvariantCulture)))));
            }
        }

        return result;
    }

    /// <summary>Una regla general nueva. Nada se edita: para cambiar una vigente se la cierra y se abre otra.</summary>
    public async Task<TaxRule> AddRule(TaxRuleInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await ValidateRule(input.TaxCode, input.Category, input.Rate, input.ValidFrom, input.ValidTo, ct).ConfigureAwait(false);
        if (input.Country is { Length: not 2 })
        {
            throw new ValidationException("El país va en ISO 3166-1 de dos letras, o vacío para cualquiera.");
        }

        if (input.VatCondition is { } condition && !VatConditions.IsKnown(condition))
        {
            throw new ValidationException($"La condición frente al IVA {condition} no está en la tabla de ARCA.");
        }

        var rule = new TaxRule
        {
            TaxCode = input.TaxCode,
            Country = input.Country?.ToUpperInvariant(),
            Province = input.Province?.ToUpperInvariant(),
            VatCondition = input.VatCondition,
            BusinessWithValidatedNumber = input.BusinessWithValidatedNumber,
            Category = input.Category,
            Outcome = input.Outcome,
            Rate = input.Outcome == TaxTreatment.Taxed ? input.Rate : 0m,
            Reason = input.Reason,
            Legend = input.Legend,
            Note = input.Note,
            ValidFrom = input.ValidFrom,
            ValidTo = input.ValidTo,
            CreatedBy = Actor.Current.Id,
        };
        await EnsureNoOverlap(rule, ct).ConfigureAwait(false);
        db.TaxRules.Add(rule);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("taxes.rule.add", $"tax_rule:{rule.Id}", new { input.TaxCode, input.Country, input.Province, input.VatCondition, input.Category, input.Outcome, input.Rate, input.ValidFrom }, ct).ConfigureAwait(false);
        return rule;
    }

    /// <summary>
    /// Dos reglas del mismo impuesto con los mismos filtros y vigencias que se solapan empatan, y con empate el cálculo
    /// falla para todos los receptores que alcanzan: la nueva no entra mientras la otra siga vigente en esas fechas.
    /// </summary>
    private async Task EnsureNoOverlap(TaxRule rule, CancellationToken ct)
    {
        var same = await db.TaxRules.Where(r => r.TaxCode == rule.TaxCode && r.AccountId == rule.AccountId && r.Country == rule.Country && r.Province == rule.Province
            && r.VatCondition == rule.VatCondition && r.BusinessWithValidatedNumber == rule.BusinessWithValidatedNumber && r.Category == rule.Category).ToListAsync(ct).ConfigureAwait(false);
        var overlapping = same.FirstOrDefault(r => r.ValidFrom < (rule.ValidTo ?? DateOnly.MaxValue) && rule.ValidFrom < (r.ValidTo ?? DateOnly.MaxValue));
        if (overlapping is not null)
        {
            var until = overlapping.ValidTo is { } to ? string.Create(CultureInfo.InvariantCulture, $" hasta el {to:dd/MM/yyyy}") : string.Empty;
            throw new ValidationException(string.Create(CultureInfo.InvariantCulture, $"Ya hay una regla de {rule.TaxCode} con los mismos filtros, vigente desde el {overlapping.ValidFrom:dd/MM/yyyy}{until} (regla {overlapping.Id}): cerrala primero y después agregá la nueva."));
        }
    }

    /// <summary>
    /// Una regla de cuenta: la exención con constancia, la exclusión de una percepción o el cliente del exterior que usa
    /// el servicio en el país. El motivo es obligatorio y la constancia queda guardada para mostrarla ante una inspección.
    /// </summary>
    public async Task<TaxRule> AddAccountRule(AccountRuleInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Reason))
        {
            throw new ValidationException("Una regla de cuenta lleva motivo: por qué este cliente tributa distinto.");
        }

        if (!await db.Accounts.AnyAsync(a => a.Id == input.AccountId, ct).ConfigureAwait(false))
        {
            throw new ValidationException($"No existe la cuenta {input.AccountId}.");
        }

        await ValidateRule(input.TaxCode, input.Category, input.Rate, input.ValidFrom, input.ValidTo, ct).ConfigureAwait(false);
        var rule = new TaxRule
        {
            TaxCode = input.TaxCode,
            AccountId = input.AccountId,
            Category = input.Category,
            Outcome = input.Outcome,
            Rate = input.Outcome == TaxTreatment.Taxed ? input.Rate : 0m,
            Reason = input.Reason.Trim(),
            CertificateRef = string.IsNullOrWhiteSpace(input.CertificateRef) ? null : input.CertificateRef.Trim(),
            Note = input.Note,
            ValidFrom = input.ValidFrom,
            ValidTo = input.ValidTo,
            CreatedBy = Actor.Current.Id,
        };
        await EnsureNoOverlap(rule, ct).ConfigureAwait(false);
        db.TaxRules.Add(rule);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("taxes.account_rule.add", $"account:{input.AccountId}", new { rule.Id, input.TaxCode, input.Outcome, input.Rate, input.Reason, input.CertificateRef, input.ValidFrom, input.ValidTo }, ct).ConfigureAwait(false);
        return rule;
    }

    /// <summary>Cierra una regla en la fecha dada. Lo emitido no se toca: cada renglón guarda la alícuota que usó.</summary>
    public async Task CloseRule(long ruleId, DateOnly on, CancellationToken ct = default)
    {
        var rule = await db.TaxRules.SingleOrDefaultAsync(r => r.Id == ruleId, ct).ConfigureAwait(false)
            ?? throw new ValidationException($"No existe la regla {ruleId}.");
        if (on < rule.ValidFrom)
        {
            throw new ValidationException("Una regla no se cierra antes de empezar.");
        }

        if (rule.ValidTo is { } current && current <= on)
        {
            return;
        }

        rule.ValidTo = on;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("taxes.rule.close", $"tax_rule:{ruleId}", new { on, rule.AccountId }, ct).ConfigureAwait(false);
    }

    /// <summary>Una categoría propia, además de las de fábrica. El código es estable: minúsculas, números y guion bajo.</summary>
    public async Task<TaxCategory> AddCategory(string code, string name, string explanation, TaxTreatment treatment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code is not { Length: >= 2 and <= 40 } || !code.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            throw new ValidationException("El código de la categoría lleva minúsculas, números y guion bajo, hasta 40 caracteres.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ValidationException("Falta el nombre de la categoría.");
        }

        if (await db.TaxCategories.AnyAsync(c => c.Code == code, ct).ConfigureAwait(false))
        {
            throw new ValidationException($"Ya existe la categoría {code}.");
        }

        var category = new TaxCategory
        {
            Code = code,
            Name = name.Trim(),
            Explanation = explanation?.Trim() ?? string.Empty,
            Treatment = treatment,
            Reason = treatment switch { TaxTreatment.Exempt => TaxReasons.ExemptByLaw, TaxTreatment.NotTaxed => TaxReasons.Untaxed, _ => null },
            ValidFrom = core.Today(),
        };
        db.TaxCategories.Add(category);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("taxes.category.add", $"tax_category:{code}", new { name, treatment }, ct).ConfigureAwait(false);
        return category;
    }

    /// <summary>El modo de carga por defecto de una moneda. Solo lo usan los precios nuevos: los servicios conservan el suyo.</summary>
    public async Task SetDefaultPriceMode(Currency currency, PriceMode mode, CancellationToken ct = default)
    {
        var settings = await core.Settings(ct).ConfigureAwait(false);
        settings.SetDefaultPriceMode(currency, mode);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("taxes.price_mode", null, new { currency = currency.Code, mode }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cómo se muestra el precio al visitante (sección 3.5 del diseño fiscal). Para un emisor de Argentina la Res. 4/2025
    /// SIC fija el precio final con el precio sin impuestos nacionales en letra chica: el panel lo bloquea y lo explica.
    /// </summary>
    public async Task SetDisplay(string catalogDisplay, bool businessMayViewNet, CancellationToken ct = default)
    {
        if (!TaxSettings.CatalogDisplays.Contains(catalogDisplay))
        {
            throw new ValidationException($"La muestra del catálogo es una de: {string.Join(", ", TaxSettings.CatalogDisplays)}.");
        }

        var settings = await core.Settings(ct).ConfigureAwait(false);
        if (settings.IssuerCountry == "AR" && catalogDisplay != "final_with_net")
        {
            throw new ValidationException("En Argentina el catálogo muestra el precio final y, en letra más chica, el precio sin impuestos nacionales (Res. 4/2025 SIC, art. 2). No se cambia.");
        }

        settings.CatalogDisplay = catalogDisplay;
        settings.BusinessMayViewNet = businessMayViewNet;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("taxes.display", null, new { catalogDisplay, businessMayViewNet }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Un régimen de ingresos brutos donde el proveedor es agente de percepción (sección 3.8 del diseño fiscal): crea o
    /// repone el impuesto de comprobante de la jurisdicción con el código 7 de ARCA, cierra sus reglas vigentes y abre
    /// una por la subdivisión del receptor, más una exclusión por cada condición que el régimen deja afuera.
    /// </summary>
    public async Task SetGrossIncomeRegime(GrossIncomeRegime regime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(regime);
        if (regime.Jurisdiction is not { Length: 4 } || !regime.Jurisdiction.StartsWith("AR-", StringComparison.Ordinal))
        {
            throw new ValidationException("La jurisdicción va como código ISO 3166-2, por ejemplo AR-B o AR-C.");
        }

        if (string.IsNullOrWhiteSpace(regime.AgentNumber) || string.IsNullOrWhiteSpace(regime.Regime))
        {
            throw new ValidationException("Falta el régimen o el número de agente.");
        }

        if (regime.Rate is < 0m or > 1m)
        {
            throw new ValidationException("La alícuota va entre 0 y 1, por ejemplo 0.03 para el 3 %.");
        }

        if (regime.ValidTo is { } until && until <= regime.ValidFrom)
        {
            throw new ValidationException("La vigencia termina después de empezar.");
        }

        if (regime.ExcludedConditions?.Where(c => !VatConditions.IsKnown(c)).ToList() is { Count: > 0 } unknown)
        {
            throw new ValidationException($"La condición frente al IVA {unknown[0]} no está en la tabla de ARCA.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var settings = await core.Settings(ct).ConfigureAwait(false);
        var tax = await db.Taxes.SingleOrDefaultAsync(t => t.Code == regime.TaxCode, ct).ConfigureAwait(false);
        if (tax is null)
        {
            tax = new Tax { Code = regime.TaxCode, Name = $"Percepción de ingresos brutos {regime.Jurisdiction}", Level = TaxLevel.Document, FiscalCode = GrossIncomeRegime.FiscalCode, Group = "Percepciones", ValidFrom = regime.ValidFrom };
            db.Taxes.Add(tax);
        }

        // La base es del impuesto, no de la regla: uno con fecha futura y otra base la cambiaría ya para el que sigue
        // percibiendo hasta ese día.
        if (tax.Base != regime.Base && regime.ValidFrom > core.Today()
            && await GeneralFrom(core.Today()).AnyAsync(r => r.TaxCode == regime.TaxCode && r.ValidFrom < regime.ValidFrom, ct).ConfigureAwait(false))
        {
            throw new ValidationException("El régimen que sigue percibiendo calcula sobre otra base, y la base es del impuesto: uno con otra base se carga el día que empieza.");
        }

        // El impuesto rige desde el primer régimen: uno nuevo con fecha futura no corta al vigente antes de empezar.
        tax.Base = regime.Base;
        tax.ValidFrom = regime.ValidFrom < tax.ValidFrom ? regime.ValidFrom : tax.ValidFrom;
        tax.ValidTo = regime.ValidTo;
        // Se cierran las que sigan vigentes cuando empieza el nuevo, también las que ya tenían fin: dos reglas del
        // mismo régimen vigentes a la vez empatan y el cálculo falla para toda la jurisdicción.
        foreach (var rule in await GeneralFrom(regime.ValidFrom).Where(r => r.TaxCode == regime.TaxCode).ToListAsync(ct).ConfigureAwait(false))
        {
            rule.ValidTo = regime.ValidFrom;
        }

        var actor = Actor.Current.Id;
        db.TaxRules.Add(new TaxRule { TaxCode = regime.TaxCode, Country = "AR", Province = regime.Jurisdiction, Outcome = TaxTreatment.Taxed, Rate = regime.Rate, Reason = TaxReasons.Perception, ValidFrom = regime.ValidFrom, ValidTo = regime.ValidTo, CreatedBy = actor });
        foreach (var condition in regime.ExcludedConditions ?? [])
        {
            db.TaxRules.Add(new TaxRule { TaxCode = regime.TaxCode, Country = "AR", Province = regime.Jurisdiction, VatCondition = condition, Outcome = TaxTreatment.NotTaxed, Reason = TaxReasons.Untaxed, ValidFrom = regime.ValidFrom, ValidTo = regime.ValidTo, CreatedBy = actor });
        }

        settings.SetRegimes(settings.Regimes().Where(r => r.Jurisdiction != regime.Jurisdiction).Append(regime));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("taxes.gross_income", $"tax:{regime.TaxCode}", new { regime.Jurisdiction, regime.Regime, regime.AgentNumber, regime.Rate, regime.ValidFrom, regime.ValidTo }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Deja de percibir en una jurisdicción desde la fecha dada: cierra el régimen y sus reglas generales.</summary>
    public async Task CloseGrossIncomeRegime(string jurisdiction, DateOnly on, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jurisdiction);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var settings = await core.Settings(ct).ConfigureAwait(false);
        var regime = settings.Regimes().FirstOrDefault(r => r.Jurisdiction == jurisdiction)
            ?? throw new ValidationException($"No hay un régimen de ingresos brutos para {jurisdiction}.");
        settings.SetRegimes(settings.Regimes().Select(r => r.Jurisdiction == jurisdiction ? r with { ValidTo = on } : r));
        foreach (var rule in await GeneralFrom(on).Where(r => r.TaxCode == regime.TaxCode).ToListAsync(ct).ConfigureAwait(false))
        {
            rule.ValidTo = on;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.Record("taxes.gross_income.close", $"tax:{regime.TaxCode}", new { jurisdiction, on }, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task ValidateRule(string taxCode, string? category, decimal rate, DateOnly validFrom, DateOnly? validTo, CancellationToken ct)
    {
        if (!await db.Taxes.AnyAsync(t => t.Code == taxCode, ct).ConfigureAwait(false))
        {
            throw new ValidationException($"No existe el impuesto {taxCode}.");
        }

        if (category is not null && !await db.TaxCategories.AnyAsync(c => c.Code == category, ct).ConfigureAwait(false))
        {
            throw new ValidationException($"No existe la categoría {category}.");
        }

        if (rate is < 0m or > 1m)
        {
            throw new ValidationException("La alícuota va entre 0 y 1, por ejemplo 0.21 para el 21 %.");
        }

        if (validTo is { } until && until <= validFrom)
        {
            throw new ValidationException("La vigencia termina después de empezar.");
        }
    }
}

/// <summary>Resultado de validar un número de IVA: el número de consulta es la constancia ante una inspección.</summary>
public sealed record VatNumberCheck(bool Valid, string? Name, string? Address, string? RequestId, DateTimeOffset CheckedAt);

/// <summary>Validación de un número de IVA de la Unión Europea (VIES) o del Reino Unido (HMRC).</summary>
public interface IVatNumberValidator
{
    Task<VatNumberCheck> Check(string countryCode, string vatNumber, CancellationToken ct);
}

/// <summary>
/// Cliente de VIES por su API REST pública. Con el número propio del consultante (RequesterCountry y RequesterNumber)
/// VIES devuelve un número de consulta que sirve de constancia (Reglamento 282/2011, art. 18). Las pruebas usan un
/// VIES falso: este cliente solo corre contra el real con sus variables de entorno.
/// </summary>
public sealed class ViesClient(HttpClient http, TimeProvider time, string? requesterCountry = null, string? requesterNumber = null) : IVatNumberValidator
{
    public static Uri Endpoint { get; } = new("https://ec.europa.eu/taxation_customs/vies/rest-api/check-vat-number");

    public async Task<VatNumberCheck> Check(string countryCode, string vatNumber, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(countryCode);
        ArgumentNullException.ThrowIfNull(vatNumber);
        var body = new JsonObject { ["countryCode"] = countryCode.ToUpperInvariant(), ["vatNumber"] = vatNumber.Trim() };
        if (requesterCountry is { Length: 2 } && requesterNumber is { Length: > 0 })
        {
            body["requesterMemberStateCode"] = requesterCountry;
            body["requesterNumber"] = requesterNumber;
        }

        using var response = await http.PostAsJsonAsync(Endpoint, body, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false) ?? [];
        if (result["userError"]?.GetValue<string>() is { } error && error is not ("VALID" or "INVALID"))
        {
            throw new InvalidOperationException($"VIES no pudo validar el número: {error}.");
        }

        return new VatNumberCheck(
            result["valid"]?.GetValue<bool>() == true,
            Clean(result["name"]?.GetValue<string>()),
            Clean(result["address"]?.GetValue<string>()),
            result["requestIdentifier"]?.GetValue<string>(),
            time.GetUtcNow());
    }

    private static string? Clean(string? value) => value is null or "---" ? null : value.Trim();
}
