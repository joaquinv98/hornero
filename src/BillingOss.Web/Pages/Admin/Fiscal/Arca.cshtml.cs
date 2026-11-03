using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Fiscal;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin.Fiscal;

/// <summary>
/// "Conectar con ARCA" (sección 2.2 del diseño fiscal): siete pasos que se pueden cortar y retomar. Clave y pedido se
/// generan en el servidor, la guía dice qué pantalla de ARCA abrir, el certificado se valida sin llamar a ARCA, la
/// prueba no emite nada y activar pide el segundo factor. El asistente nunca prende las trabas de producción.
/// </summary>
[Authorize(Policy = StaffPermissions.FiscalEdit)]
public sealed class ArcaWizardModel(BillingDbContext db, FiscalSetupService setup, UserManager<StaffUser> users, IOptions<FiscalOptions> fiscal, IStringLocalizer<Texts> texts, SecuritySettingsStore security) : PageModel
{
    public const int Steps = 7;

    /// <summary>Filas de la guía (sección 2.3 del diseño fiscal), por ambiente. Los textos viven en los recursos como Guide.{ambiente}.{n}.</summary>
    public static IReadOnlyDictionary<string, int> GuideRows { get; } = new Dictionary<string, int> { [FiscalEnvironments.Homologation] = 5, [FiscalEnvironments.Production] = 5 };

    public sealed record KnownPoint(int Id, string Type, bool Blocked);

    public IssuerProfile Issuer { get; private set; } = null!;

    public int Step { get; private set; } = 1;

    public string Environment { get; private set; } = FiscalEnvironments.Homologation;

    public bool HasInstance { get; private set; }

    public IReadOnlyList<(ModuleInstance Instance, string Environment)> Instances { get; private set; } = [];

    public IReadOnlyList<ModuleInstallation> Installations { get; private set; } = [];

    /// <summary>Credenciales del ambiente que esperan el certificado o ya lo tienen validado.</summary>
    public IReadOnlyList<FiscalCredential> Credentials { get; private set; } = [];

    public FiscalCredential? Active { get; private set; }

    /// <summary>La credencial que se está trabajando: la de la consulta, o la última pendiente.</summary>
    public FiscalCredential? Selected { get; private set; }

    public IReadOnlyList<CertificateCheck>? Checks { get; private set; }

    public FiscalTestReport? Report { get; private set; }

    public SetupSummary? Summary { get; private set; }

    public IReadOnlyList<KnownPoint> KnownPoints { get; private set; } = [];

    public bool CoreProductionEnabled => fiscal.Value.ProductionEnabled;

    public string? Error { get; private set; }

    public string? Done => Request.Query["listo"];

    /// <summary>Servicios de ARCA que hay que autorizar al certificado, según lo elegido en el paso 1.</summary>
    public IReadOnlyList<string> Services => [.. new[] { "wsfe", Issuer.Exports ? "wsfex" : null, Issuer.UsesRegistry ? "ws_sr_constancia_inscripcion" : null }.OfType<string>()];

    /// <summary>La razón social sin acentos ni eñes, como la piden los ejemplos de ARCA.</summary>
    public static string Ascii(string text) => new(text.Normalize(NormalizationForm.FormD).Where(c => c < 128 && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());

    public async Task OnGet(int? paso, long? credencial, string? ambiente)
    {
        await Load(ambiente);
        Step = Math.Clamp(paso ?? Math.Min(Issuer.SetupStep + 1, Steps), 1, Steps);
        if (credencial is { } id)
        {
            Selected = Credentials.FirstOrDefault(c => c.Id == id) ?? Selected;
        }
    }

    public async Task<IActionResult> OnPostStart(int? paso, string environment, bool exports, bool usesRegistry, string? instance)
    {
        await Load(environment);
        long? instanceId = instance is ['i', ':', .. var i] && long.TryParse(i, CultureInfo.InvariantCulture, out var chosen) ? chosen : null;
        long? installationId = instance is ['n', ':', .. var n] && long.TryParse(n, CultureInfo.InvariantCulture, out var install) ? install : null;
        return await Try(1, () => setup.Start(Issuer.Id, environment, exports, usesRegistry, instanceId, installationId), 2);
    }

    public async Task<IActionResult> OnPostIssuer(int? paso, string taxId, string legalName, int vatCondition, bool classAWithLegend, string? address, string? grossIncomeNumber, DateOnly? activityStart)
    {
        await Load(null);
        var input = new IssuerInput(taxId.Trim().Replace("-", string.Empty, StringComparison.Ordinal), legalName, vatCondition, classAWithLegend, Blank(address), Blank(grossIncomeNumber), activityStart, Issuer.Exports, Issuer.UsesRegistry);
        return await Try(2, () => setup.SaveIssuer(Issuer.Id, input), 3);
    }

    public async Task<IActionResult> OnPostFillIssuer(int? paso)
    {
        await Load(null);
        return await Try(2, () => setup.FillIssuerFromRegistry(Issuer.Id), 2, "padron");
    }

    public async Task<IActionResult> OnPostCredential(int? paso, string alias, string subjectTaxId, string organization)
    {
        await Load(null);
        FiscalCredential? created = null;
        var result = await Try(3, async () => created = await setup.CreateCredential(Issuer.Id, Environment, alias.Trim(), subjectTaxId.Trim().Replace("-", string.Empty, StringComparison.Ordinal), organization), 3, "pedido");
        return created is null ? result : Redirect($"/admin/fiscal/arca/3?credencial={created.Id}&listo=pedido");
    }

    public async Task<IActionResult> OnPostImport(int? paso, IFormFile? certificate, IFormFile? key, string? password)
    {
        await Load(null);
        if (certificate is null)
        {
            return await Fail(3, texts["Arca.ImportNeedsCertificate"]);
        }

        FiscalCredential? imported = null;
        var result = await Try(3, async () => imported = await setup.ImportCredential(Issuer.Id, Environment, await Bytes(certificate), key is null ? null : await Bytes(key), Blank(password)), 5, "importado");
        return imported is null ? result : Redirect($"/admin/fiscal/arca/5?credencial={imported.Id}&listo=importado");
    }

    /// <summary>El pedido de certificado para bajar y subir a ARCA. No es secreto.</summary>
    public async Task<IActionResult> OnGetCsr(long id)
    {
        var credential = await db.FiscalCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id);
        return credential is null || credential.Csr.Length == 0 ? NotFound() : File(Encoding.ASCII.GetBytes(credential.Csr), "application/pkcs10", $"{credential.Alias}.csr");
    }

    public async Task<IActionResult> OnPostGuideDone(int? paso)
    {
        await Load(null);
        return await Try(4, () => setup.SetStep(Issuer.Id, Math.Max(Issuer.SetupStep, 4)), 5);
    }

    public async Task<IActionResult> OnPostCertificate(int? paso, long credentialId, IFormFile? file, string? pem, string? password)
    {
        await Load(null);
        var data = file is not null ? await Bytes(file) : Encoding.UTF8.GetBytes(pem ?? string.Empty);
        if (data.Length == 0)
        {
            return await Fail(5, texts["Arca.CertificateNeeded"]);
        }

        try
        {
            Checks = await setup.LoadCertificate(credentialId, data, Blank(password));
            await Load(null);
            Step = 5;
            Selected = Credentials.FirstOrDefault(c => c.Id == credentialId) ?? Selected;
            return Page();
        }
        catch (ValidationException ex)
        {
            return await Fail(5, ex.Message);
        }
    }

    /// <summary>Activar reemplaza los secretos de la instancia: pide el segundo factor y queda en la auditoría.</summary>
    public async Task<IActionResult> OnPostActivate(int? paso, long credentialId, string code)
    {
        await Load(null);
        var user = await users.GetUserAsync(User);
        // Confirmación con un código de la app, no un ingreso: una falla cuenta para el bloqueo y no va al historial.
        if (user is null || !await TotpSetup.Confirm(users, user, Ui.StaffTotp, code, await security.Get()))
        {
            return await Fail(5, texts["Code.Failed"]);
        }

        return await Try(5, async () =>
        {
            // Con secretos ya cargados en la instancia, activar prueba la candidata antes de reemplazarlos.
            await setup.Activate(credentialId);
            await setup.SetStep(Issuer.Id, Math.Max(Issuer.SetupStep, 5));
        }, 6, "activada");
    }

    public async Task<IActionResult> OnPostPoints(int? paso, int pointOfSale, int? exportPointOfSale)
    {
        await Load(null);
        return await Try(6, () => setup.SetPointsOfSale(Issuer.Id, pointOfSale, Issuer.Exports ? exportPointOfSale : null), 6, "puntos");
    }

    public async Task<IActionResult> OnPostTest(int? paso)
    {
        await Load(null);
        FiscalTestReport? report = null;
        var result = await Try(6, async () =>
        {
            report = await setup.Test(Issuer.Id);
            if (report.Ok)
            {
                await setup.SetStep(Issuer.Id, Math.Max(Issuer.SetupStep, 6));
            }
        }, 6, "prueba");
        return result;
    }

    public async Task<IActionResult> OnPostFinish(int? paso)
    {
        await Load(null);
        try
        {
            Summary = await setup.Finish(Issuer.Id);
            await Load(null);
            Step = 7;
            return Page();
        }
        catch (ValidationException ex)
        {
            return await Fail(7, ex.Message);
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<byte[]> Bytes(IFormFile file)
    {
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        return stream.ToArray();
    }

    private async Task<IActionResult> Try(int step, Func<Task> work, int next, string done = "ok")
    {
        try
        {
            await work();
            return Redirect($"/admin/fiscal/arca/{next}?listo={done}");
        }
        catch (ValidationException ex)
        {
            return await Fail(step, ex.Message);
        }
    }

    private async Task<IActionResult> Fail(int step, string error)
    {
        Error = error;
        await Load(null);
        Step = step;
        return Page();
    }

    private async Task Load(string? environment)
    {
        Issuer = await setup.EnsureIssuer();
        Instances = await setup.FiscalInstances();
        Installations = await setup.FiscalInstallations();
        var assigned = Instances.FirstOrDefault(i => i.Instance.Id == Issuer.ModuleInstanceId);
        if (assigned.Instance is not null)
        {
            HasInstance = true;
            Environment = assigned.Environment;
        }
        else if (FiscalEnvironments.IsValid(environment))
        {
            Environment = environment!;
        }

        Credentials = await db.FiscalCredentials.AsNoTracking().Where(c => c.IssuerId == Issuer.Id && c.Environment == Environment && (c.Status == CredentialStatus.Pending || c.Status == CredentialStatus.Ready)).OrderBy(c => c.Id).ToListAsync();
        Selected = Credentials.LastOrDefault();
        Active = await setup.ActiveCredential(Issuer.Id, Environment);
        Report = FiscalTestReport.Parse(Issuer.LastTest);
        KnownPoints = [.. ((JsonNode.Parse(Issuer.KnownPointsOfSale) as JsonArray) ?? []).Select(p => new KnownPoint(p!["id"]!.GetValue<int>(), p["emission_type"]?.ToString() ?? string.Empty, p["blocked"]?.GetValue<bool>() == true || p["retired_on"] is not null))];
    }
}
