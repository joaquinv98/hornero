using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.Web.Identity;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>
/// Ingreso de clientes con Google y otros proveedores OpenID Connect (diseno/ingreso-clientes.md, secciones 3 a 5), de
/// punta a punta contra un proveedor falso: ida con PKCE, vuelta validada, vínculo confirmado y nunca solo, registro con
/// el correo verificado, segundo factor, desvincular y la pantalla del staff.
/// </summary>
[TestClass]
[DoNotParallelize] // Cada prueba levanta su host y su base, como UiTests.
public sealed partial class ExternalLoginUiTests
{
    private static string Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString ?? string.Empty;

    private static async Task<string> Body(HttpResponseMessage response)
    {
        using (response)
        {
            return await response.Content.ReadAsStringAsync();
        }
    }

    private static async Task<(UiHarness H, FakeOidcProvider Idp)> Start(bool google = true)
    {
        var idp = new FakeOidcProvider();
        var h = await UiHarness.Start(services: idp.Install);
        if (google)
        {
            await h.Use<ExternalLoginStore, long>(s => s.Save(ExternalProvider.GoogleKey, ExternalProviderKind.Google, new("Google", string.Empty, FakeOidcProvider.ClientId, FakeOidcProvider.ClientSecret, true), 0));
        }

        return (h, idp);
    }

    /// <summary>Del botón al proveedor y de vuelta, hasta la respuesta de /ingresar/externo.</summary>
    private static async Task<HttpResponseMessage> RoundTrip(Client c, FakeOidcProvider idp, string subject, string? email, bool verified = true, string page = "/ingresar", string action = "/ingresar?handler=External", string back = "/")
    {
        using (var start = await c.Submit(page, action, new() { ["provider"] = "google", ["ReturnUrl"] = back }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, start.StatusCode, await start.Content.ReadAsStringAsync());
            var authorization = start.Headers.Location!;
            Assert.AreEqual("accounts.google.com", authorization.Host);
            var callback = idp.Authorize(authorization, subject, email, verified);
            Assert.AreEqual("https://billing.example.com/ingresar/externo/google/vuelta", callback.GetLeftPart(UriPartial.Path), "La vuelta sale de Billing:PublicUrl.");
            using var returned = await c.Get(callback.PathAndQuery);
            Assert.AreEqual(HttpStatusCode.OK, returned.StatusCode);
            Assert.AreEqual("0;url=/ingresar/externo", returned.Headers.GetValues("Refresh").Single(), "Sigue con una navegación desde el propio sitio.");
        }

        return await c.Get("/ingresar/externo");
    }

    private static string Of(long id) => id.ToString(CultureInfo.InvariantCulture);

    [TestMethod]
    public async Task Google_RegistraConElCorreoVerificado_YDespuesEntraSinContrasena()
    {
        var (h, idp) = await Start();
        await using var _ = h;
        using var c = h.NewClient();
        var login = await c.Html("/ingresar");
        StringAssert.Contains(login, "Continuar con Google");
        StringAssert.Contains(await c.Html("/registro"), "Crear la cuenta con Google");

        var form = await Body(await RoundTrip(c, idp, "g-100", "nuevo@gmail.example", back: "/contratar/5"));
        StringAssert.Contains(form, "No hay una cuenta con el correo nuevo@gmail.example");
        StringAssert.Contains(form, "value=\"Ana Pérez\"", "El nombre viene del proveedor.");
        using (var created = await c.Submit("/ingresar/externo", "/ingresar/externo?handler=Register", new() { ["Name"] = "Ana Pérez", ["Company"] = "Ana SRL", ["Currency"] = "ARS" }))
        {
            Assert.AreEqual("/cuenta/datos-fiscales?nuevo=1&volver=%2Fcontratar%2F5", Location(created));
        }

        var user = await h.Query(db => db.Set<PortalUser>().SingleAsync(u => u.Email == "nuevo@gmail.example"));
        Assert.IsTrue(user.EmailConfirmed, "Lo verificó el proveedor.");
        Assert.IsNull(user.PasswordHash, "Sin contraseña: entra con Google o elige una con el enlace de recuperar.");
        Assert.AreEqual("Ana SRL", await h.Query(db => db.Accounts.Where(a => a.Email == "nuevo@gmail.example").Select(a => a.CompanyName).SingleAsync()));
        var link = await h.Query(db => db.Set<ExternalLogin>().SingleAsync());
        Assert.AreEqual(("g-100", user.Id, "nuevo@gmail.example"), (link.Subject, link.UserId, link.Email));
        Assert.IsFalse(h.Mail.Sent.Any(m => m.Kind == "verify"), "No hace falta el enlace de verificación.");
        CollectionAssert.AreEquivalent(new[] { AccessGuard.RegistrationAction, "portal.external_linked" }, await h.Query(db => db.Set<AuditEvent>().Where(e => e.Entity == $"user:{user.Id}").Select(e => e.Action).ToListAsync()));
        StringAssert.Contains(await c.Html("/cuenta/seguridad"), "Ingresás con un proveedor externo y no tenés contraseña");

        using var other = h.NewClient();
        using (var entered = await RoundTrip(other, idp, "g-100", "nuevo@gmail.example", back: "/facturas"))
        {
            Assert.AreEqual("/facturas", Location(entered));
        }

        Assert.AreEqual(HttpStatusCode.OK, (await other.Get("/facturas")).StatusCode);
        var events = await h.Query(db => db.Set<LoginEvent>().Where(e => e.UserId == user.Id).OrderBy(e => e.Id).ToListAsync());
        Assert.IsTrue(events.All(e => e.Method == LoginMethod.External && e.Result == LoginResult.Success));
        Assert.HasCount(2, events);
        Assert.IsNotNull(await h.Query(db => db.Set<ExternalLogin>().Select(l => l.LastUsedAt).SingleAsync()));
        Assert.AreEqual(2, idp.TokenRequests);
    }

    [TestMethod]
    public async Task Google_ConUnUsuarioExistente_PideLaContrasena_YNuncaVinculaSolo()
    {
        var (h, idp) = await Start();
        await using var _ = h;
        var user = await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();

        var confirm = await Body(await RoundTrip(c, idp, "g-200", "titular@example.com"));
        StringAssert.Contains(confirm, "Ya tenés un usuario con el correo titular@example.com");
        Assert.AreEqual(0, await h.Query(db => db.Set<ExternalLogin>().CountAsync()), "Nunca se vincula solo.");
        Assert.AreEqual(HttpStatusCode.Redirect, (await c.Get("/facturas")).StatusCode, "Todavía no entró.");

        StringAssert.Contains(await Body(await c.Submit("/ingresar/externo", "/ingresar/externo?handler=Password", new() { ["Password"] = "no-es-la-clave" })), "La contraseña no es correcta");
        Assert.AreEqual(LoginResult.BadPassword, await h.Query(db => db.Set<LoginEvent>().Where(e => e.UserId == user.Id).Select(e => e.Result).SingleAsync()));
        using (var linked = await c.Submit("/ingresar/externo", "/ingresar/externo?handler=Password", new() { ["Password"] = UiHarness.Password }))
        {
            Assert.AreEqual("/", Location(linked));
        }

        Assert.AreEqual(HttpStatusCode.OK, (await c.Get("/facturas")).StatusCode);
        var link = await h.Query(db => db.Set<ExternalLogin>().SingleAsync());
        Assert.AreEqual(user.Id, link.UserId);
        var details = await h.Query(db => db.Set<AuditEvent>().Where(e => e.Action == "portal.external_linked").Select(e => e.Details).SingleAsync());
        StringAssert.Contains(details, "password");

        // La cookie del viaje no sirve dos veces.
        Assert.AreEqual("/ingresar", Location(await c.Get("/ingresar/externo")));
    }

    [TestMethod]
    public async Task Google_ConfirmarPorCorreo_ConElEnlaceDeUnaHora()
    {
        var (h, idp) = await Start();
        await using var _ = h;
        var user = await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();
        (await RoundTrip(c, idp, "g-300", "titular@example.com")).Dispose();
        StringAssert.Contains(await Body(await c.Submit("/ingresar/externo", "/ingresar/externo?handler=Mail", [])), "Te mandamos un enlace a tu correo");

        var sent = h.Mail.Sent.Single(m => m.Kind == "external-link");
        Assert.AreEqual("titular@example.com", sent.To);
        var mailed = new Uri(sent.Value);
        Assert.AreEqual("billing.example.com", mailed.Host);
        using var reader = h.NewClient();
        StringAssert.Contains(await reader.Html(mailed.PathAndQuery), "Confirmar el vínculo");
        Assert.AreEqual(0, await h.Query(db => db.Set<ExternalLogin>().CountAsync()), "Abrir el enlace no vincula: hace falta el botón.");
        var token = System.Web.HttpUtility.ParseQueryString(mailed.Query)["t"]!;
        StringAssert.Contains(await Body(await reader.Submit(mailed.PathAndQuery, "/ingresar/externo/confirmar", new() { ["T"] = token + "x" })), "no es válido");
        StringAssert.Contains(await Body(await reader.Submit(mailed.PathAndQuery, "/ingresar/externo/confirmar", new() { ["T"] = token })), "Ya podés ingresar con Google");
        Assert.AreEqual(user.Id, await h.Query(db => db.Set<ExternalLogin>().Select(l => l.UserId).SingleAsync()));
        Assert.AreEqual(HttpStatusCode.Redirect, (await reader.Get("/facturas")).StatusCode, "Confirmar no abre una sesión.");

        using var back = h.NewClient();
        Assert.AreEqual("/", Location(await RoundTrip(back, idp, "g-300", "titular@example.com")));

        // Otro pedido de enlace, y la contraseña cambia antes de usarlo: el sello lo invalida.
        using var third = h.NewClient();
        (await RoundTrip(third, idp, "g-301", "titular@example.com")).Dispose();
        (await third.Submit("/ingresar/externo", "/ingresar/externo?handler=Mail", [])).Dispose();
        var stale = new Uri(h.Mail.Sent.Last(m => m.Kind == "external-link").Value);
        await h.Use<Microsoft.AspNetCore.Identity.UserManager<PortalUser>, bool>(async users =>
            (await users.UpdateSecurityStampAsync((await users.FindByEmailAsync("titular@example.com"))!)).Succeeded);
        StringAssert.Contains(await reader.Html(stale.PathAndQuery), "no es válido");
        Assert.AreEqual(1, await h.Query(db => db.Set<ExternalLogin>().CountAsync()));
    }

    [TestMethod]
    public async Task Google_SinCorreoVerificado_NoVinculaNiRegistra()
    {
        var (h, idp) = await Start();
        await using var _ = h;
        await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();

        StringAssert.Contains(await Body(await RoundTrip(c, idp, "g-400", "titular@example.com", verified: false)), "no confirmó que el correo es tuyo");
        StringAssert.Contains(await Body(await RoundTrip(c, idp, "g-401", "nadie@example.com", verified: false)), "no confirmó que el correo es tuyo");
        Assert.AreEqual(0, await h.Query(db => db.Set<ExternalLogin>().CountAsync()));
        Assert.AreEqual(1, await h.Query(db => db.Set<PortalUser>().CountAsync()));
    }

    [TestMethod]
    public async Task Google_ElSegundoFactorPropioSigueValiendo()
    {
        var (h, idp) = await Start();
        await using var _ = h;
        var user = await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();
        var key = TotpKey().Match(await c.Html("/cuenta/seguridad")).Groups[1].Value.Replace(" ", string.Empty, StringComparison.Ordinal);
        (await c.Submit("/cuenta/seguridad", "/cuenta/seguridad?handler=Enable", new() { ["Code"] = UiHarness.Totp(key) })).Dispose();
        await h.Use<ExternalLoginStore, bool>(s => s.Add(1, "g-500", user.Id, "titular@example.com", "prueba"));

        using var other = h.NewClient();
        using (var step = await RoundTrip(other, idp, "g-500", "titular@example.com", back: "/facturas"))
        {
            Assert.AreEqual("/ingresar/codigo?ReturnUrl=%2Ffacturas", Location(step));
        }

        Assert.AreEqual(HttpStatusCode.Redirect, (await other.Get("/facturas")).StatusCode, "Sin el código no entra.");
        using (var code = await other.Submit("/ingresar/codigo", "/ingresar/codigo?ReturnUrl=%2Ffacturas", new() { ["Code"] = UiHarness.Totp(key) }))
        {
            Assert.AreEqual("/facturas", Location(code));
        }

        Assert.AreEqual(LoginMethod.Totp, await h.Query(db => db.Set<LoginEvent>().Where(e => e.UserId == user.Id).OrderByDescending(e => e.Id).Select(e => e.Method).FirstAsync()));
    }

    [TestMethod]
    public async Task Vuelta_ConStateAjenoOTokenMalo_NoEntra()
    {
        var (h, idp) = await Start();
        await using var _ = h;
        var user = await h.PortalUser("titular@example.com", h.AccountA);
        await h.Use<ExternalLoginStore, bool>(s => s.Add(1, "g-600", user.Id, "titular@example.com", "prueba"));

        // La vuelta en otro navegador que el de la ida: sin su cookie no hay state que comparar.
        using (var c = h.NewClient())
        using (var start = await c.Submit("/ingresar", "/ingresar?handler=External", new() { ["provider"] = "google" }))
        using (var stranger = h.NewClient())
        {
            var callback = idp.Authorize(start.Headers.Location!, "g-600", "titular@example.com");
            StringAssert.Contains(await Body(await stranger.Get(callback.PathAndQuery)), "venció o empezó en otro navegador");
            var forged = Regex.Replace(callback.PathAndQuery, "state=[^&]+", "state=otro");
            StringAssert.Contains(await Body(await c.Get(forged)), "venció o empezó en otro navegador");
        }

        // Tokens que el núcleo rechaza: otra llave, otro nonce, otro destinatario y vencido.
        Action<Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor>[] tampers =
        [
            d => d.SigningCredentials = new(new Microsoft.IdentityModel.Tokens.RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048)) { KeyId = "k1" }, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256),
            d => d.Claims["nonce"] = "otro",
            d => d.Audience = "otro-cliente",
            d =>
            {
                d.IssuedAt = d.NotBefore = DateTime.UtcNow.AddHours(-2);
                d.Expires = DateTime.UtcNow.AddHours(-1);
            },
        ];
        foreach (var tamper in tampers)
        {
            idp.Tamper = tamper;
            using var c = h.NewClient();
            using var start = await c.Submit("/ingresar", "/ingresar?handler=External", new() { ["provider"] = "google" });
            var callback = idp.Authorize(start.Headers.Location!, "g-600", "titular@example.com");
            StringAssert.Contains(await Body(await c.Get(callback.PathAndQuery)), "No se pudo completar el ingreso");
            Assert.AreEqual("/ingresar", Location(await c.Get("/ingresar/externo")));
        }

        idp.Tamper = null;

        Assert.IsFalse(await h.Query(db => db.Set<LoginEvent>().AnyAsync(e => e.Result == LoginResult.Success)));
    }

    [TestMethod]
    public async Task DesdeLaSeguridad_VinculaConLaContrasena_YDesvincula()
    {
        var (h, idp) = await Start();
        await using var _ = h;
        var user = await h.PortalUser("titular@example.com", h.AccountA);
        await h.PortalUser("otro@example.com", h.AccountB);
        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();
        StringAssert.Contains(await c.Html("/cuenta/seguridad"), "Vincular Google");

        // Una cuenta de Google con otro correo, que vincula el usuario de la sesión.
        var confirm = await Body(await RoundTrip(c, idp, "g-700", "personal@gmail.example", page: "/cuenta/seguridad", action: "/cuenta/seguridad?handler=Link"));
        StringAssert.Contains(confirm, "Para vincular tu cuenta de Google con tu usuario");
        using (var done = await c.Submit("/ingresar/externo", "/ingresar/externo?handler=Password", new() { ["Password"] = UiHarness.Password }))
        {
            Assert.AreEqual("/cuenta/seguridad?vinculado=1", Location(done));
        }

        var page = await c.Html("/cuenta/seguridad?vinculado=1");
        StringAssert.Contains(page, "personal@gmail.example");
        StringAssert.Contains(page, "quedó vinculado");

        // La misma cuenta de Google no se vincula a otro usuario.
        using (var other = h.NewClient())
        {
            (await other.Login("otro@example.com")).Dispose();
            StringAssert.Contains(await Body(await RoundTrip(other, idp, "g-700", "personal@gmail.example", page: "/cuenta/seguridad", action: "/cuenta/seguridad?handler=Link")), "ya está vinculada a otro usuario");
        }

        var link = await h.Query(db => db.Set<ExternalLogin>().SingleAsync());
        StringAssert.Contains(await Body(await c.Submit("/cuenta/seguridad", $"/cuenta/seguridad?handler=Unlink&id={link.Id}", [])), "Se quitó el vínculo");
        Assert.AreEqual(0, await h.Query(db => db.Set<ExternalLogin>().CountAsync()));
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(e => e.Action == "portal.external_unlinked" && e.Entity == $"user:{user.Id}")));
    }

    [TestMethod]
    public async Task Admin_CargaGoogleConElSecretoCifrado_LoPrueba_YQuitaVinculos()
    {
        var (h, idp) = await Start(google: false);
        await using var _ = h;
        await h.Staff("lector@example.com", StaffPermissions.AccountsRead);
        await h.Staff("seguridad@example.com", StaffPermissions.AccountsRead, StaffPermissions.SecurityManage);
        var user = await h.PortalUser("titular@example.com", h.AccountA);

        using var reader = await AccessUiTestsSession(h, "lector@example.com");
        StringAssert.Contains(Location(await reader.Get("/admin/seguridad/proveedores")), "/estado/403");

        using var admin = await AccessUiTestsSession(h, "seguridad@example.com");
        var empty = await admin.Html("/admin/seguridad/proveedores");
        StringAssert.Contains(empty, "https://billing.example.com/ingresar/externo/google/vuelta");
        StringAssert.Contains(empty, "Todavía no hay un secreto cargado");
        StringAssert.Contains(await Body(await admin.Submit("/admin/seguridad/proveedores", "/admin/seguridad/proveedores?handler=Save", new()
        {
            ["key"] = "google", ["kind"] = "Google", ["version"] = "0", ["name"] = "Google", ["clientId"] = FakeOidcProvider.ClientId, ["enabled"] = "true",
        })), "hacen falta el ID del cliente y el secreto");
        var saved = await Body(await admin.Submit("/admin/seguridad/proveedores", "/admin/seguridad/proveedores?handler=Save", new()
        {
            ["key"] = "google", ["kind"] = "Google", ["version"] = "0", ["name"] = "Google", ["clientId"] = FakeOidcProvider.ClientId,
            ["clientSecret"] = FakeOidcProvider.ClientSecret, ["enabled"] = "true",
        }));
        StringAssert.Contains(saved, "El proveedor quedó guardado");
        StringAssert.Contains(saved, "Hay un secreto guardado y cifrado");
        Assert.IsFalse(saved.Contains(FakeOidcProvider.ClientSecret, StringComparison.Ordinal), "El secreto no vuelve a la pantalla.");
        var row = await h.Query(db => db.Set<ExternalProvider>().SingleAsync());
        Assert.AreEqual(ExternalProvider.GoogleAuthority, row.Authority);
        Assert.IsFalse(Encoding.UTF8.GetString(row.ClientSecret!).Contains(FakeOidcProvider.ClientSecret, StringComparison.Ordinal), "Cifrado por campo.");
        StringAssert.Contains(await Body(await admin.Submit("/admin/seguridad/proveedores", "/admin/seguridad/proveedores?handler=Save", new()
        {
            ["key"] = "google", ["kind"] = "Google", ["version"] = "0", ["name"] = "Google", ["clientId"] = "x", ["enabled"] = "true",
        })), "Otra persona cambió este proveedor");

        StringAssert.Contains(await Body(await admin.Submit("/admin/seguridad/proveedores", "/admin/seguridad/proveedores?handler=Test", new() { ["key"] = "google" })), "El proveedor reconoció el ID del cliente y el secreto");
        await h.Use<ExternalLoginStore, long>(s => s.Save("google", ExternalProviderKind.Google, new("Google", string.Empty, FakeOidcProvider.ClientId, "otro-secreto", true), 1));
        StringAssert.Contains(await Body(await admin.Submit("/admin/seguridad/proveedores", "/admin/seguridad/proveedores?handler=Test", new() { ["key"] = "google" })), "rechazó el ID del cliente o el secreto");
        await h.Use<ExternalLoginStore, long>(s => s.Save("google", ExternalProviderKind.Google, new("Google", string.Empty, FakeOidcProvider.ClientId, FakeOidcProvider.ClientSecret, true), 2));

        // Otro proveedor OpenID Connect con su dirección: la vuelta lleva su clave.
        StringAssert.Contains(await Body(await admin.Submit("/admin/seguridad/proveedores", "/admin/seguridad/proveedores?handler=Save", new()
        {
            ["key"] = "ingreso-propio", ["kind"] = "Oidc", ["version"] = "0", ["name"] = "Ingreso propio", ["authority"] = "http://idp.example/realms/clientes",
            ["clientId"] = FakeOidcProvider.ClientId, ["clientSecret"] = FakeOidcProvider.ClientSecret, ["enabled"] = "true",
        })), "tiene que ser HTTPS");
        StringAssert.Contains(await Body(await admin.Submit("/admin/seguridad/proveedores", "/admin/seguridad/proveedores?handler=Save", new()
        {
            ["key"] = "ingreso-propio", ["kind"] = "Oidc", ["version"] = "0", ["name"] = "Ingreso propio", ["authority"] = "https://idp.example/realms/clientes",
            ["clientId"] = FakeOidcProvider.ClientId, ["clientSecret"] = FakeOidcProvider.ClientSecret, ["enabled"] = "true",
        })), "https://billing.example.com/ingresar/externo/ingreso-propio/vuelta");
        StringAssert.Contains(await Body(await admin.Submit("/admin/seguridad/proveedores", "/admin/seguridad/proveedores?handler=Save", new()
        {
            ["key"] = "ingreso-propio", ["kind"] = "Oidc", ["version"] = "1", ["name"] = "Ingreso propio", ["authority"] = "https://otro.example/realms/clientes",
            ["clientId"] = FakeOidcProvider.ClientId, ["enabled"] = "true",
        })), "Volvé a cargar el secreto");
        using (var customer = h.NewClient())
        {
            var login = await customer.Html("/ingresar");
            StringAssert.Contains(login, "Continuar con Ingreso propio");
            using var start = await customer.Submit("/ingresar", "/ingresar?handler=External", new() { ["provider"] = "ingreso-propio" });
            Assert.AreEqual("https://idp.example/realms/clientes/o/auth", start.Headers.Location!.GetLeftPart(UriPartial.Path));
            var callback = idp.Authorize(start.Headers.Location!, "p-1", "titular@example.com");
            (await customer.Get(callback.PathAndQuery)).Dispose();
            StringAssert.Contains(await customer.Html("/ingresar/externo"), "Ya tenés un usuario con el correo titular@example.com");
        }

        var actions = await h.Query(db => db.Set<AuditEvent>().Where(e => e.Action == "security.external_provider").Select(e => e.Details!).ToListAsync());
        Assert.HasCount(4, actions);
        Assert.IsFalse(actions.Any(d => d.Contains(FakeOidcProvider.ClientSecret, StringComparison.Ordinal) || d.Contains("otro-secreto", StringComparison.Ordinal)), "La auditoría nombra lo que cambió, nunca los valores.");

        // El staff ve el vínculo en la seguridad de la ficha y lo saca.
        var google = await h.Query(db => db.Set<ExternalProvider>().Where(p => p.Key == "google").Select(p => p.Id).SingleAsync());
        await h.Use<ExternalLoginStore, bool>(s => s.Add(google, "g-800", user.Id, "titular@gmail.example", "prueba"));
        var link = await h.Query(db => db.Set<ExternalLogin>().SingleAsync());
        StringAssert.Contains(await admin.Html($"/admin/cuentas/{h.AccountA}/seguridad"), "titular@gmail.example");
        StringAssert.Contains(await Body(await admin.Submit($"/admin/cuentas/{h.AccountA}/seguridad", $"/admin/cuentas/{h.AccountA}/seguridad?handler=Unlink&userId={Of(user.Id)}&linkId={Of(link.Id)}", [])), "Se quitó el vínculo");
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(e => e.Action == "security.external_unlinked")));
        StringAssert.Contains(Location(await reader.Submit($"/admin/cuentas/{h.AccountA}/seguridad", $"/admin/cuentas/{h.AccountA}/seguridad?handler=Unlink&userId={Of(user.Id)}&linkId=1", [])), "/estado/403");
    }

    /// <summary>Una sesión del staff con su segundo factor, como en AccessUiTests.</summary>
    private static async Task<Client> AccessUiTestsSession(UiHarness h, string email)
    {
        var c = h.NewClient();
        (await c.Submit("/admin/ingresar", "/admin/ingresar", new() { ["Email"] = email, ["Password"] = UiHarness.Password })).Dispose();
        var key = TotpKey().Match(await c.Html("/admin/ingresar/codigo")).Groups[1].Value.Replace(" ", string.Empty, StringComparison.Ordinal);
        (await c.Submit("/admin/ingresar/codigo", "/admin/ingresar/codigo", new() { ["Code"] = UiHarness.Totp(key) })).Dispose();
        return c;
    }

    [GeneratedRegex("<code class=\"key\"[^>]*>([A-Z2-7 ]+)</code>")]
    private static partial Regex TotpKey();
}
