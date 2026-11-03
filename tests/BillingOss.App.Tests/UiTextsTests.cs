using System.Text.RegularExpressions;
using System.Xml.Linq;
using BillingOss.App.Identity;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Affiliates;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Content;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Web;
using BillingOss.Web.Identity;
using BillingOss.Web.Pages.Admin.Catalog;
using Microsoft.AspNetCore.Identity;

namespace BillingOss.App.Tests;

/// <summary>
/// Control de los textos (D-13): es-AR y en tienen las mismas claves, y toda clave que usan las páginas existe. Sin
/// base de datos: corre siempre.
/// </summary>
[TestClass]
public sealed partial class UiTextsTests
{
    private static readonly string Web = FindWeb();

    /// <summary>Lo que se parece a una clave de texto pero es el nombre de un campo de formulario o de un modelo.</summary>
    private static readonly string[] FormPrefixes = ["Model.", "Input.", "Incident.", "Maintenance.", "Update.", "Retitle.", "Reschedule."];

    private static Dictionary<string, string> Read(string file) =>
        XDocument.Load(Path.Combine(Web, "Resources", file)).Root!.Elements("data")
            .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);

    [TestMethod]
    [DataRow("Texts")]
    [DataRow("SecurityTexts")]
    [DataRow("ContentLabels")]
    public void Textos_EsYEnTienenLasMismasClaves(string file)
    {
        var es = Read(file + ".resx");
        var en = Read(file + ".en.resx");

        CollectionAssert.AreEquivalent(es.Keys.ToList(), en.Keys.ToList());
        Assert.IsFalse(es.Values.Concat(en.Values).Any(string.IsNullOrWhiteSpace));
    }

    [TestMethod]
    public void Textos_TodaClaveQueUsanLasPaginasExiste()
    {
        // Los textos de los contenidos para clientes van aparte (ContentLabels), y sus claves también se usan fuera de las páginas.
        var keys = Read("Texts.resx").Keys.Concat(Read("SecurityTexts.resx").Keys).Concat(Read("ContentLabels.resx").Keys).ToHashSet();
        var used = new[] { "Pages", "Content" }.SelectMany(folder => Directory.EnumerateFiles(Path.Combine(Web, folder), "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cshtml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .SelectMany(f => KeyLiteral().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Where(k => !FormPrefixes.Any(prefix => k.StartsWith(prefix, StringComparison.Ordinal)))
            .ToHashSet();
        string[] statuses = ["Status.400", "Status.403", "Status.404", "Status.429"];

        // Las que se arman con el valor de un enum.
        var fromEnums = Names<AccountStatus>("AccountStatus").Concat(Names<DocumentKind>("DocumentKind")).Concat(Names<InvoiceStatus>("InvoiceStatus"))
            .Concat(Names<PaymentStatus>("PaymentStatus")).Concat(Names<ServiceStatus>("ServiceStatus"))
            .Concat(Names<QuoteStatus>("QuoteStatus")).Concat(Names<BillableItemStatus>("BillableItemStatus")).Concat(Names<TaxTreatment>("TaxTreatment"))
            .Concat(Names<ProductKind>("ProductKind")).Concat(Names<OptionKind>("OptionKind")).Concat(Names<DiscountKind>("DiscountKind"))
            .Concat(CatalogForm.Cycles.SelectMany(c => new[] { "Cycle." + c, "CycleOption." + c }))
            .Concat(Names<Domain.Catalog.CycleUnit>("CycleUnit"))
            .Concat(Names<DomainStatus>("DomainStatus")).Concat(Names<DomainStatus>("DomainHelp")).Concat(Names<DomainOperationStatus>("DomainOperationStatus"))
            .Concat(Names<Platform.WebhookDeliveryStatus>("WebhookStatus")).Concat(Platform.PublicEvents.All.Select(e => $"Event.{e.Internal}"))
            .Concat(Names<LoginResult>("LoginResult")).Concat(Names<LoginMethod>("LoginMethod")).Concat(Names<LoginAudience>("Logins"))
            .Concat(Names<BlockKind>("BlockKind")).Concat(Names<BlockSource>("BlockSource")).Concat(Names<ScreeningStatus>("Screening"))
            .Concat(Names<ScreeningStatus>("Review.Done"))
            .Concat(Names<Support.TicketStatus>("TicketStatus")).Concat(Names<Support.TicketStatus>("SupportStatus")).Concat(Names<Support.TicketPriority>("TicketPriority"))
            .Concat(Names<Support.MessageSource>("MessageSource")).Concat(Names<Support.MailSecurity>("MailSecurity")).Concat(Names<Support.InboundOutcome>("InboundOutcome"))
            .Concat(Names<Support.MailFilterKind>("MailFilterKind"))
            .Concat(new[] { "Support.FileType", "Support.FileSize", "Support.FileCount" })
            .Concat(Names<Domain.Usage.UsageCycle>("Usage.Cycle")).Concat(Names<Domain.Usage.UsageAggregation>("Usage.Aggregation"))
            .Concat(Names<AffiliateStatus>("AffiliateStatus")).Concat(Names<AffiliateStatus>("AffiliateMoveTo")).Concat(Names<CommissionKind>("CommissionKind"))
            .Concat(Names<CommissionStatus>("CommissionStatus")).Concat(Names<PayoutMethod>("PayoutMethod")).Concat(Names<PayoutStatus>("PayoutStatus"))
            .Concat(Names<ReferralSource>("ReferralSource"))
            .Concat(Enum.GetNames<ContentAudience>().Select(n => "Cms.Audience" + n)).Concat(Enum.GetNames<ComponentKind>().Select(n => "Cms.Kind" + n));

        // Los problemas de la política de contraseñas y de los ajustes, que se muestran con su clave.
        var all = new SecuritySettings { RequireUpper = true, RequireLower = true, RequireDigit = true, RequireSymbol = true };
        var passwords = PasswordRules.Problems(all, string.Empty, null, null, staff: false)
            .Concat(PasswordRules.Problems(all, new string('a', 200), null, null, staff: false))
            .Select(code => "Pwd." + code);
        var settings = new SecuritySettings
        {
            MinLength = 1, StaffMinLength = 0, AccountAttempts = 0, AccountMinutes = 0, StaffAccountAttempts = 0, StaffAccountMinutes = 0,
            IpAttempts = 0, IpWindowMinutes = 0, IpMinutes = 0,
            TwoFactorRoles = ["nadie"], KeepLoginDays = 0, FraudReviewScore = 101m, FraudEnabled = true, TrustedNetworks = ["no-es-red"],
            RegistrationIpLimit = 0, RegistrationIpHours = 0, RegistrationCurrency = "pesos",
            ChallengeProvider = "otro", ChallengeForms = ["baja"], ChallengeBits = 0,
        }.Problems();
        Assert.HasCount(20, settings);
        var turnstileKeys = new SecuritySettings { ChallengeProvider = SecuritySettings.ChallengeTurnstile }.Problems();
        CollectionAssert.AreEqual(new[] { "SecuritySettings.ChallengeKeys" }, turnstileKeys.ToArray());
        var challenge = SecuritySettings.ChallengeProviders.Select(p => "Challenge.Provider." + p).Concat(SecuritySettings.ChallengeFormNames.Select(f => "Challenge.Form." + f));
        fromEnums = fromEnums.Concat(passwords).Concat(settings).Concat(turnstileKeys).Concat(challenge);

        // Las que se arman con un valor del protocolo o de la página (Ui.Key).
        string[] operations = ["register", "transfer", "renew", "set_nameservers", "set_lock", "get_auth_code", "set_contacts", "set_dns", "set_email_forwards", "set_privacy", DomainJobs.AddOption];
        string[] views = ["fallidas", "pendientes", "gracia", "vencen", "vencidos", "sin_renovacion"];
        string[] sources = ["core", "registrar", "rdap", "dns", "none"];
        var fromValues = operations.Select(o => Ui.Key("DomainOperation", o))
            .Concat(DomainContacts.Fields.Select(f => Ui.Key("Contact", f)))
            .Concat(views.Select(v => Ui.Key("AdminDomainsView", v)))
            .Concat(sources.Select(s => Ui.Key("DomainLookupSource", s)))
            .Concat(DomainOptions.All.SelectMany(o => new[] { Ui.Key("DomainOption", o), Ui.Key("DomainOptionHelp", o) }));

        var missing = used.Concat(statuses).Concat(fromEnums).Concat(fromValues).Where(k => !keys.Contains(k)).Order().ToList();
        Assert.IsEmpty(missing, "Faltan claves: " + string.Join(", ", missing));
    }

    /// <summary>Un encabezado de tabla o una etiqueta de campo escritos a mano salen igual en inglés: van por clave.</summary>
    [TestMethod]
    public void Textos_LosEncabezadosYEtiquetasDeLasPaginas_NoLlevanLiterales()
    {
        var literals = Directory.EnumerateFiles(Path.Combine(Web, "Pages"), "*.cshtml", SearchOption.AllDirectories)
            .SelectMany(f => Literal().Matches(File.ReadAllText(f)).Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .ToList();
        Assert.IsEmpty(literals, string.Join(Environment.NewLine, literals));
    }

    [TestMethod]
    public void AvisosDeDominios_CadaUnoTieneSuPlantillaEnLosDosIdiomas()
    {
        var templates = Path.Combine(Web, "..", "BillingOss.App", "Templates");
        foreach (var notice in Enum.GetValues<DomainNotice>())
        {
            foreach (var culture in new[] { "es-AR", "en" })
            {
                var file = Path.Combine(templates, $"{DomainNotices.Template(notice)}.{culture}.liquid");
                Assert.IsTrue(File.Exists(file), $"Falta {file}.");
                var text = File.ReadAllText(file);
                Assert.IsFalse(string.IsNullOrWhiteSpace(text.Split('\n')[0]), $"La primera línea de {file} es el asunto.");
                Assert.AreEqual(Regex.Count(text, @"\{%\s*if\b"), Regex.Count(text, @"\{%\s*endif\s*%\}"), $"{file} tiene un if sin cerrar.");
                StringAssert.Contains(text, "{{ domain }}");
            }
        }
    }

    [TestMethod]
    public void HashImportado_SoloBcryptYElPropioVerifican()
    {
        var hasher = new ImportedPasswordHasher<object>();
        var own = hasher.HashPassword(new object(), "clave-de-prueba");

        Assert.AreEqual(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(new object(), own, "clave-de-prueba"));
        Assert.AreEqual(PasswordVerificationResult.SuccessRehashNeeded, hasher.VerifyHashedPassword(new object(), BCrypt.Net.BCrypt.HashPassword("clave-de-prueba", 4), "clave-de-prueba"));
        Assert.AreEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(new object(), BCrypt.Net.BCrypt.HashPassword("otra", 4), "clave-de-prueba"));
        Assert.AreEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(new object(), "$1$abc$formato-viejo", "clave-de-prueba"));
        Assert.AreEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(new object(), "esto no es base64!", "clave-de-prueba"));
    }

    /// <summary>
    /// El panel de actualizaciones arma claves con el estado, el motivo, el evento del avance, el estado del pedido, el
    /// origen y la severidad. Motivos y eventos salen del código que los produce, así uno nuevo sin texto no pasa.
    /// </summary>
    [TestMethod]
    public void Textos_DelPanelDeActualizaciones_CubrenTodoLoQueMuestra()
    {
        var src = Path.GetDirectoryName(Web)!;
        var reasons = Regex.Matches(File.ReadAllText(Path.Combine(src, "BillingOss.Updates", "Planning.cs")), "Result\\(UpdateState\\.\\w+, \"(\\w+)\"\\)").Select(m => m.Groups[1].Value).ToList();
        var events = Regex.Matches(File.ReadAllText(Path.Combine(src, "BillingOss.Updater", "Engine.cs")), "Progress\\([\\w.]+, \"(\\w+)\"").Select(m => m.Groups[1].Value).Concat(["Done", "Failed"]).ToList();
        Assert.HasCount(11, reasons);
        Assert.HasCount(12, events);
        var needed = Names<BillingOss.Updates.UpdateState>("Updates.State")
            .Concat(reasons.Select(r => "Updates.Reason." + r))
            .Concat(events.Select(e => "Updates.Event." + e))
            .Concat(new[] { "pending", "running", "done", "failed", "rejected" }.Select(s => "Updates.Status." + s))
            .Concat(new[] { "web", "console" }.Select(o => "Updates.Origin." + o))
            .Concat(new[] { "low", "medium", "high", "critical" }.Select(s => "Updates.Severity." + s));

        foreach (var file in (string[])["Texts.resx", "Texts.en.resx"])
        {
            var keys = Read(file).Keys.ToHashSet();
            var missing = needed.Where(k => !keys.Contains(k)).ToList();
            Assert.IsEmpty(missing, $"{file}: faltan {string.Join(", ", missing)}");
        }
    }

    private static IEnumerable<string> Names<T>(string prefix)
        where T : struct, Enum => Enum.GetNames<T>().Select(n => $"{prefix}.{n}");

    private static string FindWeb()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "BillingOss.Web");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("No se encontró src/BillingOss.Web.");
    }

    [GeneratedRegex("\"([A-Z][A-Za-z]+\\.[A-Z][A-Za-z0-9]+)\"")]
    private static partial Regex KeyLiteral();

    /// <summary>Texto con letras, sin ninguna expresión de Razor, como todo el contenido de un th o un label.</summary>
    [GeneratedRegex(@"<(th|label)\b[^>]*>[^<@]*\p{L}[^<]*</\1>")]
    private static partial Regex Literal();
}
