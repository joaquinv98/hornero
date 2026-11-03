using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web.Pages.Admin.Tickets;

/// <summary>
/// Configuración, Ajustes de soporte: adjuntos, cierre automático y correo entrante. Se guardan en la base, así valen en
/// todos los nodos sin reiniciar. Dos personas a la vez no se pisan: guarda la que tenía la versión vigente.
/// </summary>
[Authorize(Policy = StaffPermissions.SupportSettings)]
public sealed class SupportSettingsModel(SupportSettings settings, IStringLocalizer<Texts> texts) : PageModel
{
    private const long Mb = 1024 * 1024;

    public List<(string Field, string Message)> Problems { get; } = [];

    [BindProperty(SupportsGet = true, Name = "guardado")]
    public bool Saved { get; set; }

    [BindProperty]
    public int Version { get; set; }

    [BindProperty]
    public string? AllowedExtensions { get; set; }

    [BindProperty]
    public int MaxAttachmentMb { get; set; }

    [BindProperty]
    public int MaxAttachments { get; set; }

    /// <summary>Horas esperando al cliente hasta el cierre automático. Vacío, nunca.</summary>
    [BindProperty]
    public int? AutoCloseHours { get; set; }

    [BindProperty]
    public int MaxEmailMb { get; set; }

    [BindProperty]
    public int MaxEmailsPerSenderPerHour { get; set; }

    /// <summary>Sin valor por defecto: una casilla sin marcar no viaja, y el valor tiene que quedar en falso.</summary>
    [BindProperty]
    public bool EmailCc { get; set; }

    public bool Invalid(string field) => Problems.Any(p => p.Field == field);

    public async Task OnGet()
    {
        var (options, version) = await settings.Current(HttpContext.RequestAborted);
        Version = version;
        AllowedExtensions = SupportUi.Extensions(options);
        (MaxAttachmentMb, MaxAttachments) = ((int)(options.MaxAttachmentBytes / Mb), options.MaxAttachments);
        AutoCloseHours = options.AutoCloseAfter is { } after ? (int)Math.Round(after.TotalHours) : null;
        (MaxEmailMb, MaxEmailsPerSenderPerHour, EmailCc) = ((int)(options.MaxEmailBytes / Mb), options.MaxEmailsPerSenderPerHour, options.EmailCc);
    }

    public async Task<IActionResult> OnPost()
    {
        var options = new SupportOptions
        {
            AllowedExtensions = string.Join(",", (AllowedExtensions ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(e => (e.StartsWith('.') ? e : "." + e).ToLowerInvariant()).Distinct()),
            MaxAttachmentBytes = MaxAttachmentMb * Mb,
            MaxAttachments = MaxAttachments,
            AutoCloseAfter = AutoCloseHours switch { null or 0 => null, var hours => TimeSpan.FromHours(hours.Value) },
            MaxEmailBytes = MaxEmailMb * Mb,
            MaxEmailsPerSenderPerHour = MaxEmailsPerSenderPerHour,
            EmailCc = EmailCc,
        };
        Problems.AddRange(SupportSettings.Problems(options).Select(key => (Field(key), texts[key].Value)));
        if (Problems.Count > 0)
        {
            return Page();
        }

        try
        {
            await settings.Save(options, Version, HttpContext.RequestAborted);
        }
        catch (InvalidOperationException)
        {
            Problems.Add((string.Empty, texts["SupportSettings.Conflict"]));
            return Page();
        }

        return Redirect("/admin/soporte/ajustes?guardado=true");
    }

    public static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>El campo del formulario de cada problema: los topes se cargan en MB y el cierre en horas.</summary>
    private static string Field(string key) => key switch
    {
        "SupportSettings.MaxAttachmentBytes" => nameof(MaxAttachmentMb),
        "SupportSettings.AutoCloseAfter" => nameof(AutoCloseHours),
        "SupportSettings.MaxEmailBytes" => nameof(MaxEmailMb),
        _ => key["SupportSettings.".Length..],
    };
}
