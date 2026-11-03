using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BillingOss.App.Identity;
using BillingOss.App.UseCases;
using BillingOss.Domain.Affiliates;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>Enlace, portal, admin y API del programa de afiliados, de punta a punta.</summary>
[TestClass]
[DoNotParallelize] // Cada prueba levanta su host y su base: en paralelo agotan las conexiones del PostgreSQL compartido.
public sealed partial class AffiliateUiTests
{
    private static string Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString ?? string.Empty;

    /// <summary>Programa prendido con 10 % recurrente. La cuenta B es la afiliada.</summary>
    private static async Task<Affiliate> Program(UiHarness h, bool approval = false, bool join = true)
    {
        await h.Use<AffiliateService, bool>(async s =>
        {
            await s.UpdateProgram(true, approval, 60, 30, new Dictionary<string, decimal> { ["ARS"] = 5000m });
            await s.SetRule(null, null, CommissionKind.Percent, 10m, null, true);
            return true;
        });
        return join ? await h.Use<AffiliateService, Affiliate>(s => s.Join(h.AccountB)) : null!;
    }

    private static async Task<HttpResponseMessage> Click(Client c, string path, string? referrer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (referrer is not null)
        {
            request.Headers.Referrer = new Uri(referrer);
        }

        return await c.Http.SendAsync(request);
    }

    [TestMethod]
    public async Task Enlace_CuentaElClic_DejaLaCookie_YElClienteQueEntraQuedaAtribuido()
    {
        await using var h = await UiHarness.Start();
        var affiliate = await Program(h);
        await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();

        using (var first = await Click(c, $"/r/{affiliate.Code.ToUpperInvariant()}", "https://blog.example.net/nota?x=1"))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, first.StatusCode);
            Assert.AreEqual("/", Location(first));
            Assert.IsTrue(first.Headers.GetValues("Set-Cookie").Any(v => v.StartsWith("bo_ref=", StringComparison.Ordinal) && v.Contains("httponly", StringComparison.OrdinalIgnoreCase) && v.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase)));
        }

        (await Click(c, $"/r/{affiliate.Code}")).Dispose();
        using (var unknown = await Click(c, "/r/no-existe?ir=/afiliados"))
        {
            Assert.AreEqual("/afiliados", Location(unknown), "Un código inventado redirige igual, sin distinguirse de uno real.");
        }

        using (var outside = await Click(c, $"/r/{affiliate.Code}?ir=https://otro.example.com/"))
        {
            Assert.AreEqual("/", Location(outside), "El destino es siempre del propio sitio.");
        }

        var clicks = await h.Query(db => db.Set<AffiliateClick>().ToListAsync());
        Assert.HasCount(1, clicks, "El navegador que ya trae la cookie no suma otro clic.");
        Assert.AreEqual("blog.example.net", clicks[0].ReferrerHost);

        (await c.Login("titular@example.com")).Dispose();
        using (var home = await c.Get("/"))
        {
            Assert.IsTrue(home.Headers.GetValues("Set-Cookie").Any(v => v.StartsWith("bo_ref=;", StringComparison.Ordinal)), "La cookie se borra después de atribuir.");
        }

        var attribution = await h.Query(db => db.Set<AffiliateAttribution>().SingleAsync());
        Assert.AreEqual((h.AccountA, affiliate.Id, AttributionSource.Link), (attribution.AccountId, attribution.AffiliateId, attribution.Source));
    }

    [TestMethod]
    public async Task Portal_SeSuma_EsperaLaAprobacion_YCobraSuSaldo()
    {
        await using var h = await UiHarness.Start();
        await Program(h, approval: true, join: false);
        await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();

        StringAssert.Contains(await c.Html("/"), "href=\"/afiliados\"");
        var terms = await c.Html("/afiliados");
        StringAssert.Contains(terms, "Ganás el 10 % de lo que pagan");
        using (var join = await c.Submit("/afiliados", "/afiliados?handler=Join", []))
        {
            Assert.AreEqual("/afiliados", Location(join));
        }

        StringAssert.Contains(await c.Html("/afiliados"), "Tu solicitud está en revisión.");
        var affiliate = await h.Query(db => db.Set<Affiliate>().SingleAsync());
        await h.Use<AffiliateService, bool>(async s =>
        {
            await s.Decide(affiliate.Id, AffiliateStatus.Active);
            return true;
        });
        await h.Query(async db =>
        {
            db.Add(new AffiliateLedgerEntry(affiliate.Id, Currency.Ars, 6000m, LedgerKind.Import, DateTimeOffset.UtcNow));
            return await db.SaveChangesAsync();
        });

        var panel = await c.Html("/afiliados");
        StringAssert.Contains(panel, $"https://billing.example.com/r/{affiliate.Code}");
        StringAssert.Contains(panel, "6.000,00 ARS");

        async Task<string> Withdraw(string amount, string details)
        {
            using var response = await c.Submit("/afiliados", "/afiliados?handler=Withdraw", new() { ["Amount"] = amount, ["Currency"] = "ARS", ["Details"] = details });
            return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync() : response.StatusCode == HttpStatusCode.Redirect ? Location(response) : $"HTTP {(int)response.StatusCode}";
        }

        StringAssert.Contains(await Withdraw("1.500", "Alias prueba.ok"), "sin separador de miles");
        StringAssert.Contains(await Withdraw("6000,01", "Alias prueba.ok"), "pasa tu saldo disponible");
        StringAssert.Contains(await Withdraw("4999", "Alias prueba.ok"), "El retiro mínimo es de 5.000,00 ARS");
        StringAssert.Contains(await Withdraw("5000", " "), "Escribí los datos para la transferencia");
        Assert.AreEqual("/afiliados", await Withdraw("5000", "Alias prueba.ok"));
        StringAssert.Contains(await c.Html("/afiliados"), "Recibimos tu pedido de retiro.");

        using (var credit = await c.Submit("/afiliados", "/afiliados?handler=Credit", new() { ["Amount"] = "600,50", ["Currency"] = "ARS" }))
        {
            Assert.AreEqual("/afiliados", Location(credit));
        }

        Assert.AreEqual(600.50m, await h.Query(db => db.CreditBalances.Where(b => b.AccountId == h.AccountA).SumAsync(b => b.Balance)));
        var payouts = await h.Query(db => db.Set<AffiliatePayout>().OrderBy(p => p.Id).ToListAsync());
        Assert.AreEqual((PayoutMethod.Withdrawal, PayoutStatus.Requested, "Alias prueba.ok"), (payouts[0].Method, payouts[0].Status, payouts[0].Details));
        Assert.AreEqual((PayoutMethod.Credit, PayoutStatus.Paid), (payouts[1].Method, payouts[1].Status));
        StringAssert.Contains(await c.Html("/afiliados"), "399,50 ARS");

        using var foreign = await c.Get($"/afiliados?cuenta={h.AccountB}");
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode, "La cuenta ajena da lo mismo que una que no existe.");
    }

    [TestMethod]
    public async Task Portal_TecnicoSinCapacidad_NoVeElPrograma()
    {
        await using var h = await UiHarness.Start();
        await Program(h);
        await h.PortalUser("tecnico@example.com", h.AccountA, owner: false, capabilities: [AccountCapabilities.Services]);
        using var c = h.NewClient();
        (await c.Login("tecnico@example.com")).Dispose();

        using var page = await c.Get("/afiliados");
        Assert.AreEqual(HttpStatusCode.NotFound, page.StatusCode);
    }

    [TestMethod]
    public async Task Admin_AprobarYPagarUnRetiro_PideElPermiso()
    {
        await using var h = await UiHarness.Start();
        var affiliate = await Program(h, approval: true);
        await h.Staff("lector@example.com", StaffPermissions.AffiliatesRead);
        await h.Staff("admin@example.com", StaffPermissions.AffiliatesRead, StaffPermissions.AffiliatesManage, StaffPermissions.AccountsRead);
        await h.Staff("soporte@example.com", StaffPermissions.AccountsRead);

        using (var support = await StaffSession(h, "soporte@example.com"))
        {
            using var denied = await support.Get("/admin/afiliados");
            StringAssert.Contains(Location(denied), "/estado/403");
            Assert.IsFalse((await support.Html("/admin")).Contains("/admin/afiliados", StringComparison.Ordinal), "Sin el permiso no aparece en el menú.");
        }

        // Una sola sesión por persona: con el segundo factor ya activado, la página del código no vuelve a mostrar la clave.
        using var reader = await StaffSession(h, "lector@example.com");
        var list = await reader.Html("/admin/afiliados");
        StringAssert.Contains(list, "Pendientes de aprobación: 1");
        Assert.IsFalse(list.Contains("handler=Approve", StringComparison.Ordinal), "Sin el permiso de administrar no hay botones.");
        using (var forbidden = await reader.Submit("/admin/afiliados", $"/admin/afiliados?handler=Approve&id={affiliate.Id}", []))
        {
            StringAssert.Contains(Location(forbidden), "/estado/403");
        }

        Assert.AreEqual(AffiliateStatus.Pending, (await h.Query(db => db.Set<Affiliate>().SingleAsync())).Status);

        using var admin = await StaffSession(h, "admin@example.com");
        using (var approve = await admin.Submit("/admin/afiliados", $"/admin/afiliados?handler=Approve&id={affiliate.Id}", []))
        {
            Assert.AreEqual("/admin/afiliados", Location(approve));
        }

        Assert.AreEqual(AffiliateStatus.Active, (await h.Query(db => db.Set<Affiliate>().SingleAsync())).Status);
        await h.Query(async db =>
        {
            db.Add(new AffiliateLedgerEntry(affiliate.Id, Currency.Ars, 8000m, LedgerKind.Import, DateTimeOffset.UtcNow));
            return await db.SaveChangesAsync();
        });
        var payoutId = await h.Use<AffiliateService, long>(s => s.RequestWithdrawal(affiliate.Id, Currency.Ars, 7000m, "Alias afiliado.mp"));

        var queue = await admin.Html("/admin/afiliados/retiros");
        StringAssert.Contains(queue, "Alias afiliado.mp");
        StringAssert.Contains(queue, "7.000,00 ARS");
        using (var paid = await admin.Submit("/admin/afiliados/retiros", $"/admin/afiliados/retiros?handler=Paid&id={payoutId}", new() { ["reference"] = "TR-0001" }))
        {
            Assert.AreEqual("/admin/afiliados/retiros", Location(paid), $"HTTP {(int)paid.StatusCode}");
        }

        var payout = await h.Query(db => db.Set<AffiliatePayout>().SingleAsync());
        Assert.AreEqual((PayoutStatus.Paid, "TR-0001"), (payout.Status, payout.Reference));
        StringAssert.Contains(await admin.Html($"/admin/afiliados/{affiliate.Id}"), "TR-0001");

        // El staff pasa parte del saldo que queda al saldo a favor de la cuenta del afiliado.
        Assert.IsFalse((await reader.Html($"/admin/afiliados/{affiliate.Id}")).Contains("handler=Credit", StringComparison.Ordinal));
        StringAssert.Contains(await admin.Html($"/admin/afiliados/{affiliate.Id}"), "handler=Credit");
        using (var credit = await admin.Submit($"/admin/afiliados/{affiliate.Id}", $"/admin/afiliados/{affiliate.Id}?handler=Credit", new() { ["Amount"] = "400,25", ["Currency"] = "ARS" }))
        {
            Assert.AreEqual($"/admin/afiliados/{affiliate.Id}", Location(credit));
        }

        using (var tooMuch = await admin.Submit($"/admin/afiliados/{affiliate.Id}", $"/admin/afiliados/{affiliate.Id}?handler=Credit", new() { ["Amount"] = "600", ["Currency"] = "ARS" }))
        {
            StringAssert.Contains(await tooMuch.Content.ReadAsStringAsync(), "El saldo disponible es de 599.75 ARS");
        }

        Assert.AreEqual(400.25m, await h.Query(db => db.CreditBalances.Where(b => b.AccountId == h.AccountB).SumAsync(b => b.Balance)));
        StringAssert.Contains(await admin.Html("/admin/afiliados"), "7.400,25 ARS", "La lista muestra lo cobrado: el retiro y el saldo a favor.");

        using (var rule = await admin.Submit("/admin/afiliados/programa", "/admin/afiliados/programa?handler=Rule", new() { ["rule.ProductId"] = string.Empty, ["rule.Kind"] = "Percent", ["rule.Value"] = "7,5", ["rule.Recurring"] = "false" }))
        {
            Assert.AreEqual("/admin/afiliados/programa", Location(rule));
        }

        var programRule = await h.Query(db => db.Set<CommissionRule>().SingleAsync(r => r.ProductId == null && r.AffiliateId == null));
        Assert.AreEqual((7.5m, false), (programRule.Value, programRule.Recurring));
        StringAssert.Contains(await admin.Html("/admin/afiliados/programa"), "7,5 %, una sola vez");
        Assert.IsTrue(await h.Query(db => db.Set<BillingOss.App.Platform.AuditEvent>().AnyAsync(e => e.Action == "affiliates.payout_paid" && e.Actor.StartsWith("staff:"))));
    }

    [TestMethod]
    public async Task Programa_GuardaBonoAvisoYMaterial_YElPortalLosMuestraSinInterpretarElHtml()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("admin@example.com", StaffPermissions.AffiliatesRead, StaffPermissions.AffiliatesManage);
        await h.PortalUser("titular@example.com", h.AccountA);
        using var portal = h.NewClient();
        (await portal.Login("titular@example.com")).Dispose();
        Assert.IsFalse((await portal.Html("/")).Contains("href=\"/afiliados\"", StringComparison.Ordinal), "Con el programa apagado el portal no muestra el ítem.");

        using var admin = await StaffSession(h, "admin@example.com");
        using (var saved = await admin.Submit("/admin/afiliados/programa", "/admin/afiliados/programa?handler=Program", new()
        {
            ["Enabled"] = "true",
            ["RequiresApproval"] = "false",
            ["CookieDays"] = "60",
            ["HoldDays"] = "29",
            ["Minimums"] = "ARS 5000",
            ["Bonuses"] = "ARS 1000,50",
            ["PayoutNotifyEmail"] = "facturacion@example.com",
            ["Links"] = "<a href=\"{enlace}\">Hosting</a>\n---\nProbá {enlace}",
            ["MonthlyReport"] = "true",
        }))
        {
            Assert.AreEqual("/admin/afiliados/programa", Location(saved), $"HTTP {(int)saved.StatusCode}");
        }

        var program = await h.Use<AffiliateService, AffiliateProgram>(s => s.Program());
        Assert.AreEqual((1000.50m, "facturacion@example.com", true, 29), (program.SignupBonus(Currency.Ars), program.PayoutNotifyEmail, program.MonthlyReport, program.HoldDays));
        using (var invalid = await admin.Submit("/admin/afiliados/programa", "/admin/afiliados/programa?handler=Program", new()
        {
            ["Enabled"] = "true",
            ["CookieDays"] = "60",
            ["HoldDays"] = "29",
            ["PayoutNotifyEmail"] = "no es un correo",
        }))
        {
            StringAssert.Contains(await invalid.Content.ReadAsStringAsync(), "no es un correo válido");
        }

        StringAssert.Contains(await portal.Html("/afiliados"), "te acreditamos 1.000,50 ARS de bono");
        (await portal.Submit("/afiliados", "/afiliados?handler=Join", [])).Dispose();
        var link = $"https://billing.example.com/r/{(await h.Query(db => db.Set<Affiliate>().SingleAsync())).Code}";
        var panel = await portal.Html("/afiliados");
        StringAssert.Contains(panel, $"&lt;a href=&quot;{link}&quot;&gt;Hosting&lt;/a&gt;");
        Assert.DoesNotContain($"<a href=\"{link}\">", panel, "El material se muestra como texto para copiar, nunca como HTML.");
        StringAssert.Contains(panel, $"Probá {link}");
        StringAssert.Contains(panel, "1.000,50 ARS");
        StringAssert.Contains(await portal.Html("/"), "href=\"/afiliados\"");
    }

    [TestMethod]
    public async Task Api_ListaAtribuyeYResuelveRetiros_ConSusPermisos()
    {
        await using var w = await WebHarness.Start();
        var affiliate = await Program(w);
        var reader = await w.Key("affiliates:read");
        var writer = await w.Key("affiliates:read", "affiliates:write");

        using (var forbidden = await Send(w, HttpMethod.Post, "/api/v1/affiliates/attributions", reader, new { account_id = w.AccountId, code = affiliate.Code }))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var attributed = await Send(w, HttpMethod.Post, "/api/v1/affiliates/attributions", writer, new { account_id = w.AccountId, code = affiliate.Code.ToUpperInvariant() }))
        {
            Assert.AreEqual(HttpStatusCode.OK, attributed.StatusCode);
            Assert.IsTrue(JsonNode.Parse(await attributed.Content.ReadAsStringAsync())!["attributed"]!.GetValue<bool>());
        }

        using (var unknown = await Send(w, HttpMethod.Post, "/api/v1/affiliates/attributions", writer, new { account_id = w.AccountId, code = "nada" }))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        var list = JsonNode.Parse(await (await Send(w, HttpMethod.Get, "/api/v1/affiliates?status=active", reader)).Content.ReadAsStringAsync())!.AsArray();
        Assert.AreEqual(affiliate.Id, list.Single()!["id"]!.GetValue<long>());
        using (var suspend = await Send(w, HttpMethod.Post, $"/api/v1/affiliates/{affiliate.Id}/suspend", writer))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, suspend.StatusCode);
        }

        using (var invalid = await Send(w, HttpMethod.Post, $"/api/v1/affiliates/{affiliate.Id}/approve", writer))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, invalid.StatusCode, "Reactivar un suspendido vale.");
        }

        await w.Query(async db =>
        {
            db.Add(new AffiliateLedgerEntry(affiliate.Id, Currency.Ars, 9000m, LedgerKind.Import, DateTimeOffset.UtcNow));
            return await db.SaveChangesAsync();
        });
        long payoutId;
        await using (var scope = w.Services.CreateAsyncScope())
        {
            payoutId = await scope.ServiceProvider.GetRequiredService<AffiliateService>().RequestWithdrawal(affiliate.Id, Currency.Ars, 9000m, "Alias api.mp");
        }

        var pending = JsonNode.Parse(await (await Send(w, HttpMethod.Get, "/api/v1/affiliates/payouts?status=requested", reader)).Content.ReadAsStringAsync())!.AsArray();
        Assert.AreEqual(payoutId, pending.Single()!["id"]!.GetValue<long>());
        using (var rejected = await Send(w, HttpMethod.Post, $"/api/v1/affiliates/payouts/{payoutId}/reject", writer, new { reason = "El alias no existe" }))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, rejected.StatusCode);
        }

        using (var late = await Send(w, HttpMethod.Post, $"/api/v1/affiliates/payouts/{payoutId}/paid", writer, new { reference = "TR-9" }))
        {
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, late.StatusCode, "Un retiro rechazado no se paga.");
        }

        var detail = JsonNode.Parse(await (await Send(w, HttpMethod.Get, $"/api/v1/affiliates/{affiliate.Id}", reader)).Content.ReadAsStringAsync())!;
        Assert.AreEqual(9000m, detail["balances"]!.AsArray().Single()!["available"]!.GetValue<decimal>(), "El rechazo devolvió el importe.");
    }

    private static async Task<Affiliate> Program(WebHarness w)
    {
        await using var scope = w.Services.CreateAsyncScope();
        var s = scope.ServiceProvider.GetRequiredService<AffiliateService>();
        await s.UpdateProgram(true, false, 60, 30, new Dictionary<string, decimal>());
        var account = await scope.ServiceProvider.GetRequiredService<AccountService>().Create(new AccountInput("Afiliada", "afiliada@example.com", Currency.Ars));
        return await s.Join(account);
    }

    private static Task<HttpResponseMessage> Send(WebHarness w, HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return w.Client.SendAsync(request);
    }

    private static async Task<Client> StaffSession(UiHarness h, string email)
    {
        var c = h.NewClient();
        (await c.Submit("/admin/ingresar", "/admin/ingresar", new() { ["Email"] = email, ["Password"] = UiHarness.Password })).Dispose();
        var key = KeyPattern().Match(await c.Html("/admin/ingresar/codigo")).Groups[1].Value.Replace(" ", string.Empty, StringComparison.Ordinal);
        (await c.Submit("/admin/ingresar/codigo", "/admin/ingresar/codigo", new() { ["Code"] = UiHarness.Totp(key) })).Dispose();
        return c;
    }

    [GeneratedRegex("<code class=\"key\"[^>]*>([A-Z2-7 ]+)</code>")]
    private static partial Regex KeyPattern();
}
