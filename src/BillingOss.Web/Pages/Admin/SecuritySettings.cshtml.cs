using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.UseCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Admin;

/// <summary>Ajustes de seguridad y fraude (diseno/fraude-y-acceso.md, sección 2). Guardar no pisa los cambios de otra persona.</summary>
[Authorize(Policy = StaffPermissions.SecurityManage)]
public sealed class SecuritySettingsModel(SecuritySettingsStore store, Identity.ChallengeGuard challenge) : PageModel
{
    [BindProperty]
    public SecuritySettings Settings { get; set; } = new();

    /// <summary>
    /// El umbral va como texto y se lee con punto o coma: el binder lo leería con la cultura del pedido, y en es-AR el
    /// punto es separador de miles.
    /// </summary>
    [BindProperty]
    public string ReviewScore { get; set; } = string.Empty;

    /// <summary>
    /// Los roles van aparte: dentro de <see cref="Settings"/> el binder no llena la lista de solo lectura que trae de
    /// fábrica y quedaría vacía.
    /// </summary>
    [BindProperty(Name = "Settings.TwoFactorRoles")]
    public string[] TwoFactorRoles { get; set; } = [];

    /// <summary>Los formularios con desafío anti robots, aparte por lo mismo que los roles.</summary>
    [BindProperty(Name = "Settings.ChallengeForms")]
    public string[] ChallengeForms { get; set; } = [];

    /// <summary>La clave secreta del desafío, que se cifra al guardar. Vacía deja la que estaba: nunca vuelve a la pantalla.</summary>
    [BindProperty]
    public string? ChallengeSecretText { get; set; }

    /// <summary>Las redes de confianza, una por línea.</summary>
    [BindProperty]
    public string Trusted { get; set; } = string.Empty;

    [BindProperty]
    public int Version { get; set; }

    public IReadOnlyList<(long Id, string Name)> Instances { get; private set; } = [];

    public IReadOnlyList<string> Problems { get; private set; } = [];

    public string? Error { get; private set; }

    public bool Saved { get; private set; }

    public async Task OnGet()
    {
        (Settings, Version) = await store.Current();
        ReviewScore = Settings.FraudReviewScore.ToString("0.##", CultureInfo.InvariantCulture);
        Trusted = string.Join(Environment.NewLine, Settings.TrustedNetworks);
        Instances = await store.FraudInstances();
    }

    public async Task<IActionResult> OnPost()
    {
        Instances = await store.FraudInstances();
        var parsed = decimal.TryParse(ReviewScore?.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var score);
        // Cada red se guarda normalizada. Lo que no se entiende queda tal cual, para que la validación lo marque.
        var trusted = (Trusted ?? string.Empty).Split(['\n', '\r', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => Networks.Parse(t)?.ToString() ?? t).Distinct();
        var stored = (await store.Current()).Settings.ChallengeSecret;
        string? secret;
        try
        {
            secret = string.IsNullOrWhiteSpace(ChallengeSecretText) ? stored : challenge.SealSecret(ChallengeSecretText.Trim());
        }
        catch (InvalidOperationException)
        {
            Error = "Providers.NoKeys";
            return Page();
        }

        Settings = Settings with
        {
            FraudReviewScore = parsed ? score : -1m, TwoFactorRoles = [.. TwoFactorRoles.Distinct()], TrustedNetworks = [.. trusted],
            ChallengeForms = [.. ChallengeForms.Distinct()], ChallengeSecret = secret,
        };
        Problems = Settings.Problems();
        if (Problems.Count > 0)
        {
            return Page();
        }

        try
        {
            await store.Save(Settings, Version);
        }
        catch (InvalidOperationException)
        {
            Error = "Settings.Conflict";
            return Page();
        }
        catch (ValidationException)
        {
            Error = "Settings.NotFraudInstance";
            return Page();
        }

        await OnGet();
        Saved = true;
        return Page();
    }
}
