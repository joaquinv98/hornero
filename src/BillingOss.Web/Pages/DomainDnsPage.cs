using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Domains;
using BillingOss.Modules.Protocol;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Pages;

/// <summary>
/// La zona DNS y los reenvíos de correo de un dominio, cuando el registrar los administra (registrar.dns y
/// registrar.email_forwarding). El portal y el admin usan la misma página y cambian quién ve el dominio y quién lo
/// cambia. Se edita en una tabla y se guarda entera: el registrar saca lo que no viene.
/// </summary>
public abstract class DomainDnsPage(BillingDbContext db, DomainService domains) : PageModel
{
    /// <summary>Filas vacías para agregar registros o reenvíos sin JavaScript.</summary>
    public const int BlankRows = 3;

    public sealed class RecordRow
    {
        public string? Name { get; set; }

        public string? Type { get; set; }

        public string? Value { get; set; }

        public int? Ttl { get; set; }

        public int? Priority { get; set; }

        public bool Remove { get; set; }

        public bool Blank => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(Value);
    }

    public sealed class ForwardRow
    {
        public string? Mailbox { get; set; }

        public string? To { get; set; }

        public bool Remove { get; set; }

        public bool Blank => string.IsNullOrWhiteSpace(Mailbox) && string.IsNullOrWhiteSpace(To);
    }

    public RegisteredDomain? Domain { get; private set; }

    public IReadOnlySet<string> Capabilities { get; private set; } = new HashSet<string>();

    /// <summary>La gestión de DNS o el reenvío con precio y sin contratar: no se leen ni se cambian, se contratan desde el dominio.</summary>
    public DomainOptionState? ZoneLocked { get; private set; }

    public DomainOptionState? ForwardsLocked { get; private set; }

    [BindProperty]
    public List<RecordRow> Records { get; set; } = [];

    protected BillingDbContext Db { get; } = db;

    [BindProperty]
    public List<ForwardRow> Forwards { get; set; } = [];

    /// <summary>Un cambio que todavía no llegó al registrar: se muestra lo pedido y no lo que había.</summary>
    public bool ZonePending { get; private set; }

    public bool ForwardsPending { get; private set; }

    /// <summary>El registrar no contestó la lectura: la tabla queda vacía y no se ofrece guardar.</summary>
    public bool ZoneUnavailable { get; private set; }

    public bool ForwardsUnavailable { get; private set; }

    public HashSet<int> InvalidRecords { get; } = [];

    public HashSet<int> InvalidForwards { get; } = [];

    public string? Notice { get; private set; }

    public string? Error { get; private set; }

    /// <summary>La zona y los reenvíos se cambian en un dominio vivo.</summary>
    public bool Live => Domain is { Status: DomainStatus.Active or DomainStatus.Grace };

    /// <summary>La ficha del dominio, para volver.</summary>
    public abstract string DomainUrl { get; }

    /// <summary>Si quien mira puede guardar cambios.</summary>
    public abstract bool CanChange { get; }

    public async Task<IActionResult> OnGet(long id)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        await ReadZone();
        await ReadForwards();
        return Page();
    }

    public async Task<IActionResult> OnPostZone(long id)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        if (!CanChange)
        {
            return Forbid(User.Identity!.AuthenticationType!);
        }

        var records = new List<DnsRecord>();
        for (var i = 0; i < Records.Count; i++)
        {
            var row = Records[i];
            if (row.Remove || row.Blank)
            {
                continue;
            }

            var record = new DnsRecord(row.Name ?? "@", row.Type ?? string.Empty, row.Value ?? string.Empty, row.Ttl ?? DnsRecords.DefaultTtl, row.Priority);
            if (DnsRecords.TryNormalize(Domain!.Name, [record], out _, out _))
            {
                records.Add(record);
            }
            else
            {
                InvalidRecords.Add(i);
            }
        }

        await Save(InvalidRecords.Count > 0, () => domains.SetDns(id, records, HttpContext.RequestAborted), "DomainDns.ZoneRequested");
        await ReadForwards();
        if (Error is null)
        {
            await ReadZone();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostForwards(long id)
    {
        if (!await Load(id))
        {
            return NotFound();
        }

        if (!CanChange)
        {
            return Forbid(User.Identity!.AuthenticationType!);
        }

        var forwards = new List<EmailForward>();
        for (var i = 0; i < Forwards.Count; i++)
        {
            var row = Forwards[i];
            if (row.Remove || row.Blank)
            {
                continue;
            }

            var forward = new EmailForward(row.Mailbox ?? string.Empty, row.To ?? string.Empty);
            if (EmailForwards.TryNormalize([forward], out _, out _))
            {
                forwards.Add(forward);
            }
            else
            {
                InvalidForwards.Add(i);
            }
        }

        await Save(InvalidForwards.Count > 0, () => domains.SetForwards(id, forwards, HttpContext.RequestAborted), "DomainDns.ForwardsRequested");
        await ReadZone();
        if (Error is null)
        {
            await ReadForwards();
        }

        return Page();
    }

    /// <summary>El dominio, si quien mira lo puede ver. Null es que no existe o es de otro: 404.</summary>
    protected abstract Task<RegisteredDomain?> Find(long id);

    private async Task Save(bool invalid, Func<Task> save, string notice)
    {
        if (invalid)
        {
            Error = "DomainDns.InvalidRows";
            return;
        }

        try
        {
            await save();
            Notice = notice;
        }
        catch (ValidationException)
        {
            // Lo que no se ve fila por fila: un CNAME junto con otros registros, en @, o demasiados.
            Error = "DomainDns.Conflict";
        }
    }

    private async Task<bool> Load(long id)
    {
        Domain = await Find(id);
        if (Domain is null)
        {
            return false;
        }

        Capabilities = await DomainJobs.Capabilities(Db, Domain.RegistrarInstanceId, HttpContext.RequestAborted);
        var options = await domains.Options(Domain, HttpContext.RequestAborted);
        ZoneLocked = options.FirstOrDefault(o => o.Option == DomainOptions.Dns && !o.Usable);
        ForwardsLocked = options.FirstOrDefault(o => o.Option == DomainOptions.EmailForwarding && !o.Usable);
        return true;
    }

    private async Task ReadZone()
    {
        if (!Capabilities.Contains("registrar.dns") || !Live || ZoneLocked is not null)
        {
            Records = [];
            return;
        }

        var pending = await Pending("set_dns", "records");
        ZonePending = pending is not null;
        IReadOnlyList<DnsRecord> current = [];
        if (pending is not null)
        {
            current = pending.Deserialize<List<DnsRecord>>(ModuleJson.Options) ?? [];
        }
        else
        {
            try
            {
                current = await domains.Dns(Domain!.Id, HttpContext.RequestAborted);
            }
            catch (ValidationException)
            {
                ZoneUnavailable = true;
            }
        }

        Records = [.. current.Select(r => new RecordRow { Name = r.Name, Type = r.Type, Value = r.Value, Ttl = r.Ttl, Priority = r.Priority }), .. Enumerable.Range(0, BlankRows).Select(_ => new RecordRow())];
    }

    private async Task ReadForwards()
    {
        if (!Capabilities.Contains("registrar.email_forwarding") || !Live || ForwardsLocked is not null)
        {
            Forwards = [];
            return;
        }

        var pending = await Pending("set_email_forwards", "forwards");
        ForwardsPending = pending is not null;
        IReadOnlyList<EmailForward> current = [];
        if (pending is not null)
        {
            current = pending.Deserialize<List<EmailForward>>(ModuleJson.Options) ?? [];
        }
        else
        {
            try
            {
                current = await domains.Forwards(Domain!.Id, HttpContext.RequestAborted);
            }
            catch (ValidationException)
            {
                ForwardsUnavailable = true;
            }
        }

        Forwards = [.. current.Select(f => new ForwardRow { Mailbox = f.Mailbox, To = f.To }), .. Enumerable.Range(0, BlankRows).Select(_ => new ForwardRow())];
    }

    /// <summary>Lo pedido en el último cambio que sigue en la cola, o null si no hay.</summary>
    private async Task<JsonNode?> Pending(string operation, string field)
    {
        var input = await Db.DomainOperations.AsNoTracking()
            .Where(o => o.DomainId == Domain!.Id && o.Operation == operation && o.Status == DomainOperationStatus.Pending)
            .OrderByDescending(o => o.Id).Select(o => o.Input).FirstOrDefaultAsync(HttpContext.RequestAborted);
        return input is null ? null : JsonNode.Parse(input)?[field];
    }
}

/// <summary>
/// Las opciones de un dominio para la parte compartida del portal y el admin: quién cambia y dónde se paga la factura de
/// una contratación (nulo si quien mira no paga desde acá).
/// </summary>
public sealed record DomainOptionsView(RegisteredDomain Domain, IReadOnlyList<DomainOptionState> Options, bool CanChange, string? InvoicePath);
