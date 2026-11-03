using System.Net;
using System.Text.RegularExpressions;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.Module.OneTrial;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingOss.App.Tests;

/// <summary>Las pantallas de Configuración, API y webhooks, y de Complementos, Extensiones, de punta a punta.</summary>
[TestClass]
[DoNotParallelize] // Como UiTests: cada prueba levanta su host y su base.
public sealed partial class IntegrationsUiTests
{
    private const string Webhooks = "/admin/configuracion/webhooks";
    private const string Extensions = "/admin/complementos/extensiones";

    [TestMethod]
    public async Task Pantallas_SoloConElPermisoDeIntegraciones()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("lector@example.com", StaffPermissions.AccountsRead);
        await h.Staff("integra@example.com", StaffPermissions.Integrations);
        using var reader = await StaffSession(h, "lector@example.com");
        using var integrator = await StaffSession(h, "integra@example.com");

        foreach (var page in new[] { Webhooks, Extensions })
        {
            using var denied = await reader.Get(page);
            Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode, page);
            StringAssert.Contains(denied.Headers.Location?.OriginalString, "/estado/403");
            StringAssert.Contains(await integrator.Html(page), "<h1>", page);
        }

        Assert.IsFalse((await reader.Html("/admin")).Contains(Webhooks, StringComparison.Ordinal), "El menú no muestra lo que no puede abrir.");
        StringAssert.Contains(await integrator.Html("/admin/configuracion/webhooks"), $"href=\"{Extensions}\"");
    }

    [TestMethod]
    public async Task Webhook_AltaMuestraElSecretoUnaSolaVezYLaFichaLoAdministra()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("integra@example.com", StaffPermissions.Integrations);
        using var c = await StaffSession(h, "integra@example.com");

        using var created = await c.Submit(Webhooks, $"{Webhooks}?handler=Webhook", new() { ["Destination"] = "https://hooks.example.com/billing", ["Description"] = "n8n de cobranzas", ["Events"] = "invoice.paid" });
        var html = await created.Content.ReadAsStringAsync();
        var secret = SecretPattern().Match(html).Groups[1].Value;
        StringAssert.StartsWith(secret, "whsec_");
        Assert.IsTrue(created.Headers.CacheControl?.NoStore, "La respuesta con el secreto no se guarda en ningún caché.");

        var id = await h.Query(db => db.Set<WebhookEndpoint>().Select(e => e.Id).SingleAsync());
        var list = await c.Html(Webhooks);
        var detail = await c.Html($"{Webhooks}/{id}");
        StringAssert.Contains(list, "https://hooks.example.com/billing");
        Assert.IsFalse(list.Contains(secret, StringComparison.Ordinal) || detail.Contains(secret, StringComparison.Ordinal), "El secreto no vuelve a la página.");

        // Un error de validación dice qué hacer y no pierde lo cargado.
        using (var invalid = await c.Submit($"{Webhooks}/{id}", $"{Webhooks}/{id}?handler=Save", new() { ["Destination"] = "http://otro.example.com/hook", ["Events"] = "invoice.paid", ["Active"] = "true" }))
        {
            var text = await invalid.Content.ReadAsStringAsync();
            StringAssert.Contains(text, "role=\"alert\"");
            StringAssert.Contains(text, "value=\"http://otro.example.com/hook\"");
        }

        using (var test = await c.Submit($"{Webhooks}/{id}", $"{Webhooks}/{id}?handler=Test", []))
        {
            StringAssert.Contains(await test.Content.ReadAsStringAsync(), "webhook.test");
        }

        using (var rotated = await c.Submit($"{Webhooks}/{id}", $"{Webhooks}/{id}?handler=Rotate", []))
        {
            var other = SecretPattern().Match(await rotated.Content.ReadAsStringAsync()).Groups[1].Value;
            StringAssert.StartsWith(other, "whsec_");
            Assert.AreNotEqual(secret, other);
        }

        using (var off = await c.Submit($"{Webhooks}/{id}", $"{Webhooks}/{id}?handler=Save", new() { ["Destination"] = "https://hooks.example.com/v2", ["Events"] = "service.suspended" }))
        {
            Assert.AreEqual(HttpStatusCode.OK, off.StatusCode);
        }

        var endpoint = await h.Query(db => db.Set<WebhookEndpoint>().SingleAsync());
        Assert.AreEqual("https://hooks.example.com/v2", endpoint.Url);
        CollectionAssert.AreEqual(new[] { "service.suspended" }, endpoint.Events);
        Assert.IsFalse(endpoint.Active, "Sin la casilla queda apagado.");

        using (var deleted = await c.Submit($"{Webhooks}/{id}", $"{Webhooks}/{id}?handler=Delete", []))
        {
            Assert.AreEqual(Webhooks, deleted.Headers.Location?.OriginalString);
        }

        Assert.AreEqual(0, await h.Query(db => db.Set<WebhookEndpoint>().CountAsync()));
        var actions = await h.Query(db => db.Set<AuditEvent>().Where(a => a.Action.StartsWith("webhooks.")).Select(a => a.Action).ToListAsync());
        CollectionAssert.AreEquivalent(new[] { "webhooks.create", "webhooks.test", "webhooks.rotate", "webhooks.update", "webhooks.delete" }, actions);
    }

    [TestMethod]
    public async Task Llave_SeEmiteDesdeElPanelSeMuestraUnaVezYSeRevoca()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("integra@example.com", StaffPermissions.Integrations);
        using var c = await StaffSession(h, "integra@example.com");

        var form = await c.Html(Webhooks);
        StringAssert.Contains(form, "value=\"accounts:read\"", "Ofrece los permisos de la API propia.");
        StringAssert.Contains(form, "value=\"compat:GetClientsDetails\"", "Y los comandos de la compatible.");

        // Un error dice qué hacer y no pierde lo cargado.
        using (var invalid = await c.Submit(Webhooks, $"{Webhooks}?handler=Key", new() { ["KeyName"] = "n8n", ["Scopes"] = "accounts:read", ["AllowedIps"] = "no-es-una-ip" }))
        {
            var text = await invalid.Content.ReadAsStringAsync();
            StringAssert.Contains(text, "role=\"alert\"");
            StringAssert.Contains(text, ">no-es-una-ip</textarea>");
        }

        using (var unknown = await c.Submit(Webhooks, $"{Webhooks}?handler=Key", new() { ["KeyName"] = "n8n", ["Scopes"] = "todo:todo" }))
        {
            StringAssert.Contains(await unknown.Content.ReadAsStringAsync(), "Permiso desconocido");
        }

        using var issued = await c.Submit(Webhooks, $"{Webhooks}?handler=Key", new() { ["KeyName"] = "n8n de cobranzas", ["Scopes"] = "accounts:read", ["RequestsPerMinute"] = "30" });
        var token = SecretPattern().Match(await issued.Content.ReadAsStringAsync()).Groups[1].Value;
        StringAssert.StartsWith(token, "bk_");
        Assert.IsTrue(issued.Headers.CacheControl?.NoStore, "La respuesta con la llave no se guarda en ningún caché.");
        var secret = token.Split('.')[1];
        Assert.IsFalse((await c.Html(Webhooks)).Contains(secret, StringComparison.Ordinal), "El secreto no vuelve a la página.");

        using var api = h.NewClient();
        async Task<HttpStatusCode> Read()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/accounts/{h.AccountA}");
            request.Headers.Authorization = new("Bearer", token);
            using var response = await api.Http.SendAsync(request);
            return response.StatusCode;
        }

        Assert.AreEqual(HttpStatusCode.OK, await Read());
        var key = await h.Query(db => db.ApiKeys.SingleAsync());
        Assert.AreEqual("n8n de cobranzas", key.Name);
        Assert.AreEqual(30, key.RequestsPerMinute);

        using (var revoked = await c.Submit(Webhooks, $"{Webhooks}?handler=Revoke&keyId={key.Id}", []))
        {
            StringAssert.Contains(await revoked.Content.ReadAsStringAsync(), "role=\"status\"");
        }

        Assert.AreEqual(HttpStatusCode.Unauthorized, await Read(), "Revocada no pasa más.");
        var audit = await h.Query(db => db.Set<AuditEvent>().Where(a => a.Action.StartsWith("apikeys.")).ToListAsync());
        CollectionAssert.AreEquivalent(new[] { "apikeys.issue", "apikeys.revoke" }, audit.Select(a => a.Action).ToArray());
        Assert.IsFalse(audit.Any(a => (a.Details ?? string.Empty).Contains(secret, StringComparison.Ordinal)), "El secreto no queda en la auditoría.");
    }

    [TestMethod]
    public async Task Extension_AltaDesdeElPanelYGanchosSegunElManifiesto()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("integra@example.com", StaffPermissions.Integrations);
        using var c = await StaffSession(h, "integra@example.com");
        await using var module = await TestModule.Serve(OneTrialModule.Create(NullLogger.Instance));

        using (var wrong = await c.Submit(Extensions, Extensions, new() { ["Name"] = "Prueba única", ["ModuleUrl"] = module.Url, ["SigningSecret"] = "no-es-el-secreto-del-modulo", ["Config"] = """{ "trial_product_ids": [2] }""" }))
        {
            var text = await wrong.Content.ReadAsStringAsync();
            StringAssert.Contains(text, "rechazó la firma");
            Assert.IsFalse(text.Contains("trial_product_ids", StringComparison.Ordinal), "La configuración no vuelve al navegador.");
        }

        using var added = await c.Submit(Extensions, Extensions, new() { ["Name"] = "Prueba única", ["ModuleUrl"] = module.Url, ["SigningSecret"] = module.SigningSecret, ["Config"] = """{ "trial_product_ids": [2] }""" });
        var detail = added.Headers.Location?.OriginalString ?? throw new AssertFailedException(await added.Content.ReadAsStringAsync());
        StringAssert.StartsWith(detail, $"{Extensions}/");
        var page = await c.Html(detail);
        StringAssert.Contains(page, "one-trial");
        StringAssert.Contains(page, "name=\"ValidateOrders\"");

        using (var saved = await c.Submit(detail, $"{detail}?handler=Save", new() { ["Events"] = "service.activated", ["ValidateOrders"] = "true", ["Required"] = "true" }))
        {
            StringAssert.Contains(await saved.Content.ReadAsStringAsync(), "role=\"status\"");
        }

        var hooks = await h.Query(db => db.Set<ExtensionHook>().OrderBy(x => x.Event).ToListAsync());
        CollectionAssert.AreEqual(new[] { "order.validate", "service.activated" }, hooks.Select(x => x.Event).ToArray());
        Assert.IsTrue(hooks.Single(x => x.Event == PublicEvents.ValidateOrder).Required);
        StringAssert.Contains(await c.Html(Extensions), "Prueba única");

        using (var removed = await c.Submit(detail, $"{detail}?handler=Remove", []))
        {
            Assert.AreEqual(Extensions, removed.Headers.Location?.OriginalString);
        }

        Assert.AreEqual(0, await h.Query(db => db.Set<ExtensionHook>().CountAsync()));
        Assert.AreEqual(0, await h.Query(db => db.ModuleInstallations.CountAsync()));
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

    [GeneratedRegex("<code class=\"key\" data-secret>([^<]+)</code>")]
    private static partial Regex SecretPattern();
}
