using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BillingOss.App.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BillingOss.Web.Pages.Access;

/// <summary>
/// Confirma con el enlace del correo el vínculo de un proveedor externo (diseno/ingreso-clientes.md, sección 5.2). No
/// pide sesión ni la abre: después se ingresa con el proveedor. El GET solo muestra el botón, así un lector de correo que
/// abre los enlaces no vincula nada.
/// </summary>
public sealed class ExternalConfirmModel(UserManager<PortalUser> users, ExternalLoginStore external, IDataProtectionProvider protection) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string T { get; set; } = string.Empty;

    public string? Provider { get; private set; }

    public bool Done { get; private set; }

    public string? Error { get; private set; }

    public async Task OnGet()
    {
        if (await Read() is not { } read)
        {
            Error = "External.MailLinkInvalid";
            return;
        }

        Provider = read.Provider.Name;
    }

    public async Task<IActionResult> OnPost()
    {
        if (await Read() is not { } read)
        {
            Error = "External.MailLinkInvalid";
            return Page();
        }

        Provider = read.Provider.Name;
        var (link, user, _) = read;
        if (!await external.Add(link.Provider, link.Subject, user.Id, link.Email, "email"))
        {
            Error = "External.LinkedElsewhere";
            return Page();
        }

        Done = true;
        return Page();
    }

    /// <summary>El enlace vigente, con el usuario habilitado y el mismo sello, y el proveedor prendido.</summary>
    private async Task<(ExternalModel.MailLink Link, PortalUser User, ExternalProvider Provider)?> Read()
    {
        ExternalModel.MailLink? link;
        try
        {
            link = JsonSerializer.Deserialize<ExternalModel.MailLink>(protection.CreateProtector(ExternalModel.MailLinkPurpose).ToTimeLimitedDataProtector().Unprotect(T));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }

        if (link is null || await users.FindByIdAsync(link.User.ToString(CultureInfo.InvariantCulture)) is not { DisabledAt: null } user || user.SecurityStamp != link.Stamp)
        {
            return null;
        }

        var provider = (await external.Providers()).FirstOrDefault(p => p.Id == link.Provider && p.Enabled);
        return provider is null ? null : (link, user, provider);
    }
}
