using System.Globalization;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MimeKit;

namespace BillingOss.Web.Pages.Admin.Tickets;

/// <summary>
/// Alta y edición de un departamento con su casilla IMAP. La contraseña se cifra atada a la fila y al servidor (D-09) y
/// nunca vuelve al navegador: vacía conserva la guardada, y cambiar el servidor obliga a cargarla de nuevo. Cambiar de
/// casilla vuelve a leerla desde el principio: lo ya procesado no se repite por el Message-ID.
/// </summary>
[Authorize(Policy = StaffPermissions.SupportSettings)]
public sealed class DepartmentModel(BillingDbContext db, AuditLog audit, TimeProvider time, IStringLocalizer<Texts> texts) : PageModel
{
    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public string? Description { get; set; }

    [BindProperty]
    public string? Email { get; set; }

    [BindProperty]
    public string? StaffAlertEmail { get; set; }

    [BindProperty]
    public int SortOrder { get; set; }

    [BindProperty]
    public bool Hidden { get; set; }

    [BindProperty]
    public bool ClientsOnly { get; set; }

    /// <summary>Sin valor por defecto: una casilla sin marcar no viaja, y el valor tiene que quedar en falso.</summary>
    [BindProperty]
    public bool AutoReply { get; set; }

    [BindProperty]
    public bool RequestFeedback { get; set; }

    [BindProperty]
    public bool MailEnabled { get; set; }

    [BindProperty]
    public string? ImapHost { get; set; }

    [BindProperty]
    public int ImapPort { get; set; } = 993;

    [BindProperty]
    public MailSecurity ImapSecurity { get; set; }

    [BindProperty]
    public string? ImapUser { get; set; }

    /// <summary>Solo de ida: nunca se muestra.</summary>
    [BindProperty]
    public string? ImapPassword { get; set; }

    public bool HasPassword { get; private set; }

    public string? MailError { get; private set; }

    public bool IsNew { get; private set; }

    public List<(string Field, string Message)> Problems { get; } = [];

    public (bool Ok, string Text)? Test { get; private set; }

    /// <summary>Sin las llaves de cifrado este nodo no puede guardar ni usar contraseñas de casillas.</summary>
    public bool CanStoreSecrets => HttpContext.RequestServices.GetService<SecretBox>() is not null;

    public bool Invalid(string field) => Problems.Any(p => p.Field == field);

    public async Task<IActionResult> OnGet(long id)
    {
        IsNew = id == 0;
        if (IsNew)
        {
            AutoReply = true;
            return Page();
        }

        if (await db.Set<Department>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == id) is not { } d)
        {
            return NotFound();
        }

        (Name, Description, Email, StaffAlertEmail, SortOrder, Hidden, ClientsOnly, AutoReply, RequestFeedback) = (d.Name, d.Description, d.Email, d.StaffAlertEmail, d.SortOrder, d.Hidden, d.ClientsOnly, d.AutoReply, d.RequestFeedback);
        (MailEnabled, ImapHost, ImapPort, ImapSecurity, ImapUser) = (d.MailEnabled, d.ImapHost, d.ImapPort, d.ImapSecurity, d.ImapUser);
        (HasPassword, MailError) = (d.ImapPassword is not null, d.MailError);
        return Page();
    }

    public async Task<IActionResult> OnPost(long id)
    {
        IsNew = id == 0;
        var department = IsNew ? new Department { CreatedAt = time.GetUtcNow() } : await db.Set<Department>().SingleOrDefaultAsync(d => d.Id == id);
        if (department is null)
        {
            return NotFound();
        }

        (HasPassword, MailError) = (department.ImapPassword is not null, department.MailError);
        var host = Blank(ImapHost);
        var user = Blank(ImapUser);
        var mailboxChanged = !string.Equals(department.ImapHost, host, StringComparison.OrdinalIgnoreCase) || !string.Equals(department.ImapUser, user, StringComparison.OrdinalIgnoreCase);

        // El servidor es el host, el puerto y el cifrado: la guardada no viaja a otro puerto ni sin el cifrado con que se cargó.
        var serverChanged = !SameServer(department, host);
        var newPassword = string.IsNullOrEmpty(ImapPassword) ? null : ImapPassword;
        Validate(passwordMissing: MailEnabled && newPassword is null && (department.ImapPassword is null || serverChanged));
        if (newPassword is not null && !CanStoreSecrets)
        {
            Problems.Add((nameof(ImapPassword), texts["SupportAdmin.NoKeys"]));
        }

        if (Problems.Count > 0)
        {
            return Page();
        }

        department.Name = Name!.Trim();
        department.Description = Blank(Description);
        department.Email = Blank(Email)?.ToLowerInvariant();
        department.StaffAlertEmail = Blank(StaffAlertEmail)?.ToLowerInvariant();
        department.SortOrder = SortOrder;
        department.Hidden = Hidden;
        department.ClientsOnly = ClientsOnly;
        department.AutoReply = AutoReply;
        department.RequestFeedback = RequestFeedback;
        department.MailEnabled = MailEnabled;
        department.ImapHost = host;
        department.ImapPort = ImapPort;
        department.ImapSecurity = ImapSecurity;
        department.ImapUser = user;
        if (mailboxChanged)
        {
            (department.ImapUidValidity, department.ImapLastUid, department.MailError) = (0, 0, null);
        }

        await using var tx = await db.Database.BeginTransactionAsync();
        if (IsNew)
        {
            db.Add(department);
        }

        await db.SaveChangesAsync();

        // La contraseña va atada al ID de la fila: se cifra cuando la fila ya existe. La vieja no sirve con otro servidor.
        if (newPassword is not null)
        {
            department.ImapPassword = MailPollHandler.Protect(HttpContext.RequestServices.GetRequiredService<SecretBox>(), department, newPassword);
        }
        else if (serverChanged)
        {
            department.ImapPassword = null;
        }

        await db.SaveChangesAsync();
        await audit.Record("support.department.save", string.Create(CultureInfo.InvariantCulture, $"department:{department.Id}"), new
        {
            name = department.Name,
            mail = department.MailEnabled,
            host = department.ImapHost,
            password_changed = newPassword is not null,
            feedback = department.RequestFeedback,
        });
        await tx.CommitAsync();
        return Redirect("/admin/soporte/departamentos");
    }

    /// <summary>Prueba la casilla con lo que está en el formulario, sin guardar nada.</summary>
    public async Task<IActionResult> OnPostTest(long id)
    {
        IsNew = id == 0;
        var stored = IsNew ? null : await db.Set<Department>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == id);
        if (!IsNew && stored is null)
        {
            return NotFound();
        }

        (HasPassword, MailError) = (stored?.ImapPassword is not null, stored?.MailError);
        var host = Blank(ImapHost);
        var user = Blank(ImapUser);
        var password = !string.IsNullOrEmpty(ImapPassword) ? ImapPassword
            : stored?.ImapPassword is { } blob && SameServer(stored, host) && HttpContext.RequestServices.GetService<SecretBox>() is { } secrets
                ? secrets.Decrypt(blob, "support.departments", "imap_password", stored.Id, stored.ImapHost)
                : null;
        if (host is null || user is null || password is null)
        {
            Test = (false, texts["SupportAdmin.TestIncomplete"]);
            return Page();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var client = await MailPollHandler.Connect(host, ImapPort, ImapSecurity, user, password, timeout.Token);
            Test = (true, texts["SupportAdmin.TestOk", client.Inbox.Count]);
            await client.DisconnectAsync(true, timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || timeout.IsCancellationRequested)
        {
            Test = (false, texts["SupportAdmin.TestFailed", ex.Message]);
        }

        return Page();
    }

    private void Validate(bool passwordMissing)
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 200)
        {
            Problems.Add((nameof(Name), texts["SupportAdmin.NameRequired"]));
        }

        foreach (var (field, value) in new[] { (nameof(Email), Email), (nameof(StaffAlertEmail), StaffAlertEmail) })
        {
            if (Blank(value) is { } address && !MailboxAddress.TryParse(address, out _))
            {
                Problems.Add((field, texts["SupportAdmin.BadEmail", address]));
            }
        }

        if (MailEnabled && (Blank(ImapHost) is null || Blank(ImapUser) is null || ImapPort is < 1 or > 65535))
        {
            Problems.Add((nameof(ImapHost), texts["SupportAdmin.MailIncomplete"]));
        }

        if (passwordMissing)
        {
            Problems.Add((nameof(ImapPassword), texts["SupportAdmin.PasswordAgain"]));
        }
    }

    private bool SameServer(Department stored, string? host) =>
        string.Equals(stored.ImapHost, host, StringComparison.OrdinalIgnoreCase) && stored.ImapPort == ImapPort && stored.ImapSecurity == ImapSecurity;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
