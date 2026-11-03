using System.Text.Json.Nodes;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Fiscal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin.Fiscal;

/// <summary>
/// La ficha del emisor (sección 2.11 del diseño fiscal): estado, certificado, puntos de venta, series, trabas de
/// producción y datos del impreso, con las acciones de probar de nuevo, renovar y completar con ARCA.
/// </summary>
[Authorize(Policy = StaffPermissions.FiscalEdit)]
public sealed class FiscalIndexModel(BillingDbContext db, FiscalSetupService setup, IOptions<FiscalOptions> fiscal) : PageModel
{
    public IssuerProfile? Issuer { get; private set; }

    /// <summary>El emisor del otro ambiente que está en preparación, si lo hay: el asistente sigue con él.</summary>
    public IssuerProfile? InSetup { get; private set; }

    public string Environment { get; private set; } = FiscalEnvironments.Homologation;

    public bool HasInstance { get; private set; }

    public FiscalCredential? Credential { get; private set; }

    public IReadOnlyList<FiscalCredential> Pending { get; private set; } = [];

    public FiscalTestReport? Report { get; private set; }

    public IReadOnlyList<FiscalSeries> Series { get; private set; } = [];

    public bool CoreProductionEnabled => fiscal.Value.ProductionEnabled;

    public DateOnly Today { get; private set; }

    /// <summary>ok, warn o danger, con la clave del texto que lo explica.</summary>
    public (string Tone, string Key) Status { get; private set; } = ("neutral", "Fiscal.Status.NotStarted");

    public string? Error { get; private set; }

    public string? Done => Request.Query["listo"];

    public async Task OnGet() => await Load();

    public async Task<IActionResult> OnPostTest(long id) => await Try(async () => await setup.Test(id), "prueba");

    public async Task<IActionResult> OnPostFill(long id) => await Try(async () => await setup.FillIssuerFromRegistry(id), "padron");

    /// <summary>"Pasar a producción": otro emisor sin series, y el asistente arranca en producción con sus datos.</summary>
    public async Task<IActionResult> OnPostNewEnvironment(long id)
    {
        try
        {
            await setup.StartNewIssuer(id);
            return Redirect($"/admin/fiscal/arca/1?ambiente={FiscalEnvironments.Production}");
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            await Load();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRenew(long credentialId)
    {
        try
        {
            var renewal = await setup.Renew(credentialId);
            return Redirect($"/admin/fiscal/arca/3?credencial={renewal.Id}");
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            await Load();
            return Page();
        }
    }

    private async Task<IActionResult> Try(Func<Task> work, string done)
    {
        try
        {
            await work();
            return Redirect("/admin/fiscal?listo=" + done);
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            await Load();
            return Page();
        }
    }

    private async Task Load()
    {
        Today = DateOnly.FromDateTime(DateTime.UtcNow);
        Issuer = await db.IssuerProfiles.AsNoTracking().SingleOrDefaultAsync(i => i.Active);
        InSetup = await setup.InSetup();
        if (Issuer is null)
        {
            return;
        }

        if (Issuer.ModuleInstanceId is { } instanceId && await db.ModuleInstances.AsNoTracking().SingleOrDefaultAsync(i => i.Id == instanceId) is { } instance)
        {
            HasInstance = true;
            Environment = FiscalSetupService.EnvironmentOf(instance);
        }

        Credential = await setup.ActiveCredential(Issuer.Id, Environment);
        Pending = await db.FiscalCredentials.AsNoTracking().Where(c => c.IssuerId == Issuer.Id && c.Environment == Environment && (c.Status == CredentialStatus.Pending || c.Status == CredentialStatus.Ready)).OrderBy(c => c.Id).ToListAsync();
        Report = FiscalTestReport.Parse(Issuer.LastTest);
        Series = await db.FiscalSeries.AsNoTracking().Where(s => s.IssuerId == Issuer.Id).OrderBy(s => s.PointOfSale).ThenBy(s => s.TypeCode).ToListAsync();
        Status = Credential is null ? ("warn", "Fiscal.Status.NoCertificate")
            : Credential.DaysLeft(Today) is { } days && days < 0 ? ("danger", "Fiscal.Status.Expired")
            : Report?.Ok == false ? ("danger", "Fiscal.Status.TestFailed")
            : Environment == FiscalEnvironments.Production && (!CoreProductionEnabled || Report?.AuthorizeEnabled == false) ? ("warn", "Fiscal.Status.Locked")
            : Credential.DaysLeft(Today) is < 30 ? ("warn", "Fiscal.Status.Expiring")
            : Issuer.SetupStep < 7 ? ("warn", "Fiscal.Status.Incomplete")
            : ("ok", "Fiscal.Status.Ready");
    }

    /// <summary>Qué dijo ARCA del punto de venta en la última prueba.</summary>
    public string PointState(int pointOfSale)
    {
        var known = JsonNode.Parse(Issuer?.KnownPointsOfSale ?? "[]") as JsonArray;
        var found = known?.FirstOrDefault(p => p?["id"]?.GetValue<int>() == pointOfSale);
        return found is null ? "unknown" : found["blocked"]?.GetValue<bool>() == true || found["retired_on"] is not null ? "blocked" : "ok";
    }
}
