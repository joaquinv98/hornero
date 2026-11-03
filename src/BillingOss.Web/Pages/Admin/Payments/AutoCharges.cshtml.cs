using System.Globalization;
using System.Text;
using BillingOss.App;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Pages.Admin.Payments;

/// <summary>
/// Facturación, Cobros automáticos (sección 10.2 del diseño de interfaz): los débitos con medio guardado que inició el
/// comercio, con su estado, filtro, paginado y CSV, más las cuentas con el débito prendido y las que lo perdieron por
/// una disputa (sección 4.5 del diseño de cobros, "avisa al staff").
/// </summary>
[Authorize(Policy = StaffPermissions.PaymentsManage)]
public sealed class AutoChargesModel(BillingDbContext db, IOptions<BillingOptions> options, TimeProvider time, IStringLocalizer<Texts> l) : PageModel
{
    private const int PageSize = 50;

    /// <summary>Estados del filtro, con la clave del texto de cada uno.</summary>
    public static IReadOnlyList<(string Value, string Text)> Filters { get; } =
    [
        ("", "AutoCharges.FilterAll"), ("en-curso", "AutoCharges.Open"), ("inciertos", "AutoCharges.Uncertain"),
        ("rechazados", "AutoCharges.Failed"), ("cobrados", "AutoCharges.Paid"),
    ];

    /// <param name="State">Clave del texto del estado.</param>
    /// <param name="Tone">Tono de la marca de estado.</param>
    /// <param name="Detail">Clave del texto que explica qué sigue, o null.</param>
    /// <param name="Code">Lo que dijo la pasarela (el código del rechazo), solo para el staff.</param>
    public sealed record Row(long Id, string At, long AccountId, string AccountName, long DocumentId, long? Number, string Method, decimal Amount, Currency Currency, string State, string Tone, string? Detail, string? Code);

    public sealed record Stop(string At, long AccountId, string AccountName);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public IReadOnlyList<Stop> Stopped { get; private set; } = [];

    public int AccountsOn { get; private set; }

    public int SubscriptionsOn { get; private set; }

    public string Filter { get; private set; } = string.Empty;

    public int PageNumber { get; private set; } = 1;

    public bool HasMore { get; private set; }

    public async Task<IActionResult> OnGet(string? estado, int pagina = 1, bool csv = false)
    {
        var ct = HttpContext.RequestAborted;
        Filter = Filters.Any(f => f.Value == estado) ? estado! : string.Empty;
        PageNumber = Math.Clamp(pagina, 1, 100_000);
        var query = Filtered(db.PaymentAttempts.AsNoTracking().Where(a => a.Initiator == AttemptInitiator.Merchant), Filter)
            .OrderByDescending(a => a.Id);

        if (csv)
        {
            // ponytail: tope de 10.000 filas en la exportación. Paginarla si un proveedor llega a tanto.
            var all = await Build(await query.Take(10_000).ToListAsync(ct), ct);
            return File(Encoding.UTF8.GetBytes("﻿" + Csv(all)), "text/csv", "cobros-automaticos.csv");
        }

        var attempts = await query.Skip((PageNumber - 1) * PageSize).Take(PageSize + 1).ToListAsync(ct);
        HasMore = attempts.Count > PageSize;
        Rows = await Build([.. attempts.Take(PageSize)], ct);

        AccountsOn = await db.SavedPaymentMethods.AsNoTracking().Where(m => m.AutoCharge && m.Status == SavedMethodStatus.Active).Select(m => m.AccountId).Distinct().CountAsync(ct);
        SubscriptionsOn = await db.GatewaySubscriptions.AsNoTracking().CountAsync(s => s.Status == SubscriptionStatus.Active, ct);

        var since = time.GetUtcNow().AddDays(-90);
        var stops = await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Action == "payments.autocharge_stopped" && e.CreatedAt >= since)
            .OrderByDescending(e => e.Id).Take(50).Select(e => new { e.CreatedAt, e.Entity }).ToListAsync(ct);
        var stopAccounts = stops.Select(s => AccountOf(s.Entity)).OfType<long>().Distinct().ToList();
        var stopNames = await db.Accounts.AsNoTracking().Where(a => stopAccounts.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        Stopped = [.. stops.Where(s => AccountOf(s.Entity) is not null).Select(s =>
        {
            var account = AccountOf(s.Entity)!.Value;
            return new Stop(Local(s.CreatedAt), account, stopNames.GetValueOrDefault(account, string.Empty));
        })];
        return Page();
    }

    /// <summary>El enlace a otra página de la lista, con el mismo filtro.</summary>
    public string Link(int page, bool csv = false)
    {
        var q = new List<string>();
        if (Filter.Length > 0)
        {
            q.Add("estado=" + Uri.EscapeDataString(Filter));
        }

        if (page > 1)
        {
            q.Add(string.Create(CultureInfo.InvariantCulture, $"pagina={page}"));
        }

        if (csv)
        {
            q.Add("csv=true");
        }

        return "/admin/cobros/automaticos" + (q.Count > 0 ? "?" + string.Join('&', q) : string.Empty);
    }

    private static IQueryable<PaymentAttempt> Filtered(IQueryable<PaymentAttempt> attempts, string filter) => filter switch
    {
        "en-curso" => attempts.Where(a => a.Status == AttemptStatus.Open && (a.Outcome == null || !a.Outcome.StartsWith("uncertain:"))),
        "inciertos" => attempts.Where(a => a.Status == AttemptStatus.Open && a.Outcome != null && a.Outcome.StartsWith("uncertain:")),
        "rechazados" => attempts.Where(a => a.Status == AttemptStatus.Failed),
        "cobrados" => attempts.Where(a => a.Status == AttemptStatus.Paid),
        _ => attempts,
    };

    private async Task<IReadOnlyList<Row>> Build(IReadOnlyList<PaymentAttempt> attempts, CancellationToken ct)
    {
        var accountIds = attempts.Select(a => a.AccountId).OfType<long>().Distinct().ToList();
        var documentIds = attempts.Select(a => a.DocumentIds[0]).Distinct().ToList();
        var methodIds = attempts.Select(a => a.SavedMethodId).OfType<long>().Distinct().ToList();
        var names = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var numbers = await db.Documents.AsNoTracking().Where(d => documentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Number, ct);
        var methods = await db.SavedPaymentMethods.AsNoTracking().Where(m => methodIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        var gateways = await db.GatewaySetups.AsNoTracking().ToDictionaryAsync(s => s.ModuleInstanceId, s => s.DisplayName, ct);
        return [.. attempts.Select(a =>
        {
            var (state, tone, detail, code) = StateOf(a);
            var method = a.SavedMethodId is { } m && methods.TryGetValue(m, out var saved) ? AutoChargeService.Describe(saved) : string.Empty;
            var gateway = gateways.GetValueOrDefault(a.ModuleInstanceId);
            var account = a.AccountId ?? 0;
            return new Row(a.Id, Local(a.CreatedAt), account, names.GetValueOrDefault(account, string.Empty), a.DocumentIds[0], numbers.GetValueOrDefault(a.DocumentIds[0]),
                gateway is { Length: > 0 } ? $"{method} · {gateway}" : method, a.Amount, a.Currency, state, tone, detail, code);
        })];
    }

    /// <summary>Qué pasó con un débito, en palabras del staff. El código del rechazo solo lo ve el staff.</summary>
    public static (string State, string Tone, string? Detail, string? Code) StateOf(PaymentAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var outcome = attempt.Outcome;
        return attempt.Status switch
        {
            AttemptStatus.Paid => ("AutoCharges.Paid", "ok", null, null),
            AttemptStatus.Open when outcome?.StartsWith("uncertain:", StringComparison.Ordinal) == true => ("AutoCharges.Uncertain", "warn", "AutoCharges.UncertainHelp", outcome["uncertain:".Length..]),
            AttemptStatus.Open => ("AutoCharges.Open", "info", null, null),
            _ when outcome == "not_needed" => ("AutoCharges.NotNeeded", "neutral", null, null),
            _ when outcome == "changed" => ("AutoCharges.Changed", "neutral", null, null),
            _ when outcome == "uncertain_expired" => ("AutoCharges.UncertainExpired", "warn", "AutoCharges.UncertainExpiredHelp", null),
            _ => ("AutoCharges.Failed", "danger", attempt.Retry switch
            {
                "never" => "AutoCharges.RetryNever",
                "customer_action" => "AutoCharges.RetryCustomer",
                _ => "AutoCharges.RetryLater",
            }, outcome?.StartsWith("rejected:", StringComparison.Ordinal) == true ? outcome["rejected:".Length..] : outcome),
        };
    }

    private string Local(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone)).ToString("g", CultureInfo.CurrentCulture);

    private static long? AccountOf(string? entity) =>
        entity is { } e && e.StartsWith("account:", StringComparison.Ordinal) && long.TryParse(e.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    private string Csv(IReadOnlyList<Row> rows)
    {
        var sb = new StringBuilder("id,fecha,cuenta,nombre,factura,medio,importe,moneda,estado,detalle,codigo\r\n");
        foreach (var r in rows)
        {
            sb.Append(string.Join(
                ',',
                r.Id.ToString(CultureInfo.InvariantCulture),
                Cell(r.At),
                r.AccountId.ToString(CultureInfo.InvariantCulture),
                Cell(r.AccountName),
                r.Number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Cell(r.Method),
                r.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                r.Currency.Code,
                Cell(l[r.State]),
                Cell(r.Detail is null ? string.Empty : l[r.Detail]),
                Cell(r.Code ?? string.Empty))).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>Una celda de CSV: entre comillas si hace falta, y sin fórmulas para la planilla (un nombre que empieza con =).</summary>
    private static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
