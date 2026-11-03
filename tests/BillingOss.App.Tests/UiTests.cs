using System.Net;
using System.Text.RegularExpressions;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>Portal y admin de punta a punta (D-10 y D-13): ingreso, segundo factor, permisos y antiforgery.</summary>
[TestClass]
[DoNotParallelize] // Cada prueba levanta su host y su base: en paralelo agotan las conexiones del PostgreSQL compartido.
public sealed partial class UiTests
{
    internal static string Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString ?? string.Empty;

    private static string Key(string html) =>
        KeyPattern().Match(html) is { Success: true } m ? m.Groups[1].Value.Replace(" ", string.Empty, StringComparison.Ordinal) : throw new AssertFailedException("La página no muestra la clave de TOTP.");

    [TestMethod]
    public async Task Portal_IngresaYVeLoSuyo_LoAjenoDa404()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();

        using (var anonymous = await c.Get("/facturas"))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, anonymous.StatusCode);
            StringAssert.StartsWith(Location(anonymous), "https://localhost/ingresar?ReturnUrl=");
        }

        using (var login = await c.Login("titular@example.com"))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
            Assert.AreEqual("/", Location(login));
        }

        var list = await c.Html("/facturas");
        StringAssert.Contains(list, $"/facturas/{h.InvoiceA}\"");
        Assert.IsFalse(list.Contains($"/facturas/{h.InvoiceB}\"", StringComparison.Ordinal));
        StringAssert.Contains(await c.Html($"/facturas/{h.InvoiceA}"), "12.100,00 ARS");
        StringAssert.Contains(await c.Html("/servicios"), "VPS 2 GB");

        using var foreign = await c.Get($"/facturas/{h.InvoiceB}");
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
        using var missing = await c.Get("/facturas/999999");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [TestMethod]
    public async Task DetrasDeUnProxyQueTerminaTls_LaInterfazAndaYVeAlCliente()
    {
        // El proxy le habla a la aplicación por HTTP: sin creerle X-Forwarded-Proto, el antiforgery y las cookies con
        // SecurePolicy Always darían 500.
        await using var h = await UiHarness.Start(behindProxy: true);
        await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();

        using (var anonymous = await c.Get("/facturas"))
        {
            StringAssert.StartsWith(Location(anonymous), "https://localhost/ingresar?ReturnUrl=");
        }

        using (var login = await c.Login("titular@example.com"))
        {
            Assert.AreEqual("/", Location(login));
        }

        StringAssert.Contains(await c.Html($"/facturas/{h.InvoiceA}"), "12.100,00 ARS");
        (await c.Submit("/baja", "/baja?handler=Start", new() { ["Email"] = "cliente@example.com" })).Dispose();
        Assert.AreEqual(UiHarness.ClientIp.ToString(), await h.Query(db => db.Set<CancellationRequest>().Select(r => r.Ip).SingleAsync()));
        Assert.IsTrue(await h.Query(db => db.DataProtectionKeys.AnyAsync()), "Las cookies y el antiforgery usan el anillo de llaves de la base.");
    }

    [TestMethod]
    public async Task Portal_BajaElPdfDeLoSuyo_LoAjenoDa404()
    {
        var gotenberg = new FakeGotenberg();
        await using var h = await UiHarness.Start(services: s => s.AddSingleton(gotenberg.Client()));
        await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();

        using (var anonymous = await c.Get($"/facturas/{h.InvoiceA}/pdf"))
        {
            StringAssert.StartsWith(Location(anonymous), "https://localhost/ingresar?ReturnUrl=");
        }

        (await c.Login("titular@example.com")).Dispose();
        StringAssert.Contains(await c.Html($"/facturas/{h.InvoiceA}"), $"href=\"/facturas/{h.InvoiceA}/pdf\"");
        using (var own = await c.Get($"/facturas/{h.InvoiceA}/pdf"))
        {
            Assert.AreEqual(HttpStatusCode.OK, own.StatusCode);
            Assert.AreEqual("application/pdf", own.Content.Headers.ContentType!.MediaType);
            Assert.IsTrue(own.Headers.CacheControl is { Private: true, NoStore: true });
        }

        using var foreign = await c.Get($"/facturas/{h.InvoiceB}/pdf");
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.AreEqual(1, gotenberg.Calls);
    }

    [TestMethod]
    public async Task Portal_CapacidadFaltante_DaLoMismoQueLoAjeno()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("tecnico@example.com", h.AccountA, owner: false, capabilities: [AccountCapabilities.Services]);
        using var c = h.NewClient();
        (await c.Login("tecnico@example.com")).Dispose();

        StringAssert.Contains(await c.Html("/servicios"), "VPS 2 GB");
        using var invoice = await c.Get($"/facturas/{h.InvoiceA}");
        Assert.AreEqual(HttpStatusCode.NotFound, invoice.StatusCode);
    }

    [TestMethod]
    public async Task Portal_CincoIntentosMalos_BloqueanAunConLaContraseñaBuena()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();

        for (var i = 0; i < 5; i++)
        {
            using var bad = await c.Login("titular@example.com", "otra-clave-cualquiera");
            Assert.AreEqual(HttpStatusCode.OK, bad.StatusCode);
        }

        using var good = await c.Login("titular@example.com");
        Assert.AreEqual(HttpStatusCode.OK, good.StatusCode);
        StringAssert.Contains(await good.Content.ReadAsStringAsync(), "15 minutos");
        using var after = await c.Get("/facturas");
        Assert.AreEqual(HttpStatusCode.Redirect, after.StatusCode);
    }

    [TestMethod]
    public async Task Ingreso_TopePorIp_Da429()
    {
        await using var h = await UiHarness.Start();
        using var c = h.NewClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 21; i++)
        {
            using var response = await c.Login($"nadie{i}@example.com", "no-importa-la-clave");
            statuses.Add(response.StatusCode);
        }

        Assert.IsTrue(statuses.Take(20).All(s => s == HttpStatusCode.OK));
        Assert.AreEqual(HttpStatusCode.TooManyRequests, statuses[20]);
    }

    [TestMethod]
    public async Task Portal_HashBcryptImportado_EntraYQuedaElHashPropio()
    {
        await using var h = await UiHarness.Start();
        var imported = BCrypt.Net.BCrypt.HashPassword(UiHarness.Password, BCrypt.Net.BCrypt.GenerateSalt(10, 'y'));
        StringAssert.StartsWith(imported, "$2y$10$");
        var user = await h.PortalUser("importado@example.com", h.AccountA, importedHash: imported);
        using var c = h.NewClient();

        using (var wrong = await c.Login("importado@example.com", "no-es-la-clave"))
        {
            Assert.AreEqual(HttpStatusCode.OK, wrong.StatusCode);
        }

        using var login = await c.Login("importado@example.com");

        Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
        var hash = await h.Query(db => db.Set<PortalUser>().Where(u => u.Id == user.Id).Select(u => u.PasswordHash).SingleAsync());
        Assert.IsFalse(hash!.StartsWith('$'), "El hash bcrypt se tiene que reemplazar por el de Identity en el primer ingreso.");
        using var again = h.NewClient();
        Assert.AreEqual(HttpStatusCode.Redirect, (await again.Login("importado@example.com")).StatusCode);
    }

    [TestMethod]
    public async Task Portal_ConSegundoFactor_NoEntraSinElCodigo()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();

        var setup = await c.Html("/cuenta/seguridad");
        var key = Key(setup);
        using (var enabled = await c.Submit("/cuenta/seguridad", "/cuenta/seguridad?handler=Enable", new() { ["Code"] = UiHarness.Totp(key) }))
        {
            Assert.AreEqual(HttpStatusCode.OK, enabled.StatusCode);
            StringAssert.Contains(await enabled.Content.ReadAsStringAsync(), "Códigos de recuperación");
        }

        var seed = await h.Query(db => db.Set<PortalUser>().Select(u => u.AuthenticatorKey).SingleAsync());
        Assert.IsFalse(System.Text.Encoding.ASCII.GetString(seed!).Contains(key, StringComparison.Ordinal), "La semilla se guarda cifrada.");

        using var second = h.NewClient();
        using (var password = await second.Login("titular@example.com"))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, password.StatusCode);
            StringAssert.StartsWith(Location(password), "/ingresar/codigo");
        }

        using (var notYet = await second.Get("/facturas"))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, notYet.StatusCode);
        }

        using (var wrong = await second.Submit("/ingresar/codigo", "/ingresar/codigo", new() { ["Code"] = "000000" == UiHarness.Totp(key) ? "111111" : "000000" }))
        {
            Assert.AreEqual(HttpStatusCode.OK, wrong.StatusCode);
        }

        using var right = await second.Submit("/ingresar/codigo", "/ingresar/codigo", new() { ["Code"] = UiHarness.Totp(key) });
        Assert.AreEqual(HttpStatusCode.Redirect, right.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await second.Get("/facturas")).StatusCode);
    }

    [TestMethod]
    public async Task Portal_ConSegundoFactorDelSistemaAnterior_SoloEnrolaOSaleHastaActivarlo()
    {
        await using var h = await UiHarness.Start();
        var user = await h.PortalUser("titular@example.com", h.AccountA);
        await h.Query(db => db.Set<PortalUser>().Where(u => u.Id == user.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.TwoFactorRequired, true)));
        using var c = h.NewClient();

        using (var login = await c.Login("titular@example.com"))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
            Assert.AreEqual("/cuenta/seguridad", Location(login));
        }

        // Con la contraseña sola no se ve nada del portal, tampoco el PDF: la persona confiaba en su segundo factor.
        foreach (var path in new[] { "/", "/facturas", $"/facturas/{h.InvoiceA}", $"/facturas/{h.InvoiceA}/pdf", "/servicios" })
        {
            using var blocked = await c.Get(path);
            Assert.AreEqual(HttpStatusCode.Redirect, blocked.StatusCode, path);
            Assert.AreEqual("/cuenta/seguridad", Location(blocked), path);
        }

        // Salir sí se puede, y al volver sigue igual.
        using (var logout = await c.Submit("/cuenta/seguridad", "/salir", new()))
        {
            Assert.AreEqual("/ingresar", Location(logout));
        }

        Assert.AreEqual("/cuenta/seguridad", Location(await c.Login("titular@example.com")));
        var setup = await c.Html("/cuenta/seguridad");
        StringAssert.Contains(setup, "sistema anterior");
        var key = Key(setup);
        using (var enabled = await c.Submit("/cuenta/seguridad", "/cuenta/seguridad?handler=Enable", new() { ["Code"] = UiHarness.Totp(key) }))
        {
            Assert.AreEqual(HttpStatusCode.OK, enabled.StatusCode);
        }

        StringAssert.Contains(await c.Html("/facturas"), $"/facturas/{h.InvoiceA}\"");

        // Mientras sea obligatorio no se da de baja: ni con el formulario, que no está, ni con un pedido armado a mano.
        var security = await c.Html("/cuenta/seguridad");
        Assert.IsFalse(security.Contains("handler=Disable", StringComparison.Ordinal), "La página ofrece desactivar un factor obligatorio.");
        StringAssert.Contains(security, "sistema anterior, así que no se puede desactivar");
        using var disable = await c.Submit("/cuenta/seguridad", "/cuenta/seguridad?handler=Disable", new() { ["Code"] = UiHarness.Totp(key) });
        Assert.AreEqual(HttpStatusCode.OK, disable.StatusCode);
        StringAssert.Contains(await disable.Content.ReadAsStringAsync(), "no se puede desactivar");
        Assert.IsTrue(await h.Query(db => db.Set<PortalUser>().Select(u => u.TwoFactorEnabled).SingleAsync()));
    }

    [TestMethod]
    public async Task Staff_SinTotp_TieneQueEnrolarloAntesDeEntrar()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("staff@example.com", StaffPermissions.AccountsRead);
        using var c = h.NewClient();

        using (var password = await c.Submit("/admin/ingresar", "/admin/ingresar", new() { ["Email"] = "staff@example.com", ["Password"] = UiHarness.Password }))
        {
            Assert.AreEqual("/admin/ingresar/codigo", Location(password));
        }

        var key = Key(await c.Html("/admin/ingresar/codigo"));
        using (var blocked = await c.Get("/admin"))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, blocked.StatusCode);
            StringAssert.StartsWith(Location(blocked), "https://localhost/admin/ingresar");
        }

        using (var enrolled = await c.Submit("/admin/ingresar/codigo", "/admin/ingresar/codigo", new() { ["Code"] = UiHarness.Totp(key) }))
        {
            StringAssert.Contains(await enrolled.Content.ReadAsStringAsync(), "Códigos de recuperación");
        }

        StringAssert.Contains(await c.Html($"/admin?q={h.AccountA}"), "Cliente de prueba");
        StringAssert.Contains(await c.Html($"/admin/cuentas/{h.AccountA}"), "VPS 2 GB");

        // La segunda vez ya no muestra la clave: pide el código.
        using var other = h.NewClient();
        (await other.Submit("/admin/ingresar", "/admin/ingresar", new() { ["Email"] = "staff@example.com", ["Password"] = UiHarness.Password })).Dispose();
        Assert.IsFalse(KeyPattern().IsMatch(await other.Html("/admin/ingresar/codigo")));
        using var code = await other.Submit("/admin/ingresar/codigo", "/admin/ingresar/codigo", new() { ["Code"] = UiHarness.Totp(key) });
        Assert.AreEqual("/admin", Location(code));
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "staff.2fa_enabled")));
    }

    [TestMethod]
    public async Task SinTls_LaInterfazAvisaQueHaceFaltaHttpsOElProxy_EnVezDe500()
    {
        await using var h = await UiHarness.Start();
        using var c = h.NewClient(plainHttp: true);

        using var login = await c.Get("/ingresar");
        using var health = await c.Get("/health/live");

        Assert.AreEqual(HttpStatusCode.BadRequest, login.StatusCode);
        var text = await login.Content.ReadAsStringAsync();
        StringAssert.Contains(text, "HTTPS");
        StringAssert.Contains(text, "ForwardedHeaders:KnownProxies");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode, "La salud no depende de TLS.");
    }

    [TestMethod]
    public async Task PaginaDeEstado_AbiertaDirecto_RespondeConSuCodigo()
    {
        await using var h = await UiHarness.Start();
        using var c = h.NewClient();

        using var notFound = await c.Get("/estado/404");
        using var forbidden = await c.Get("/estado/403");
        using var notAnError = await c.Get("/estado/200");

        Assert.AreEqual(HttpStatusCode.NotFound, notFound.StatusCode);
        StringAssert.Contains(await notFound.Content.ReadAsStringAsync(), "No encontramos lo que buscás.");
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, notAnError.StatusCode, "Un código que no es de error no se repite tal cual.");
    }

    [TestMethod]
    public async Task Staff_SinPermiso_NoVeLasCuentas()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("soporte@example.com");
        using var c = await StaffSession(h, "soporte@example.com");

        using var response = await c.Get($"/admin/cuentas/{h.AccountA}");

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        StringAssert.Contains(Location(response), "/estado/403");
    }

    [TestMethod]
    public async Task Portal_LaCookieDelClienteNoAbreElAdmin()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();

        using var admin = await c.Get("/admin");

        Assert.AreEqual(HttpStatusCode.Redirect, admin.StatusCode);
        StringAssert.StartsWith(Location(admin), "https://localhost/admin/ingresar");
    }

    [TestMethod]
    public async Task VerComoCliente_MuestraElAvisoYQuedaEnLaAuditoria()
    {
        await using var h = await UiHarness.Start();
        var staff = await h.Staff("admin@example.com", StaffPermissions.AccountsRead, StaffPermissions.Impersonate);
        using var c = await StaffSession(h, "admin@example.com");

        using (var start = await c.Submit($"/admin/cuentas/{h.AccountA}", $"/admin/cuentas/{h.AccountA}?handler=Impersonate", []))
        {
            Assert.AreEqual("/", Location(start));
        }

        var home = await c.Html("/");
        StringAssert.Contains(home, "Estás viendo el portal como la cuenta");
        StringAssert.Contains(await c.Html($"/facturas/{h.InvoiceA}"), "12.100,00 ARS");
        Assert.AreEqual(HttpStatusCode.NotFound, (await c.Get($"/facturas/{h.InvoiceB}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await c.Get("/cuenta/seguridad")).StatusCode);

        using (var end = await c.Submit("/", "/salir", []))
        {
            Assert.AreEqual($"/admin/cuentas/{h.AccountA}", Location(end));
        }

        var audit = await h.Query(db => db.Set<AuditEvent>().Where(a => a.Action.StartsWith("portal.impersonate")).OrderBy(a => a.Id).ToListAsync());
        CollectionAssert.AreEqual(new[] { "portal.impersonate", "portal.impersonate_end" }, audit.Select(a => a.Action).ToArray());
        Assert.IsTrue(audit.All(a => a.Actor == $"staff:{staff.Id}" && a.Entity == $"account:{h.AccountA}"));
    }

    [TestMethod]
    public async Task VerComoCliente_SinElPermiso_NoSePuede()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("lector@example.com", StaffPermissions.AccountsRead);
        using var c = await StaffSession(h, "lector@example.com");

        Assert.IsFalse((await c.Html($"/admin/cuentas/{h.AccountA}")).Contains("handler=Impersonate", StringComparison.Ordinal));
        using var attempt = await c.Submit($"/admin/cuentas/{h.AccountA}", $"/admin/cuentas/{h.AccountA}?handler=Impersonate", []);

        StringAssert.Contains(Location(attempt), "/estado/403");
        Assert.AreEqual(0, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "portal.impersonate")));
    }

    [TestMethod]
    public async Task Antiforgery_UnPostSinToken_SeRechaza()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();
        _ = await c.Html("/ingresar");

        using var portal = new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = "titular@example.com", ["Password"] = UiHarness.Password });
        using var portalResponse = await c.Http.PostAsync(new Uri("/ingresar", UriKind.Relative), portal);
        using var admin = new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = "x@example.com", ["Password"] = "y" });
        using var adminResponse = await c.Http.PostAsync(new Uri("/admin/ingresar", UriKind.Relative), admin);
        using var baja = new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = "cliente@example.com" });
        using var bajaResponse = await c.Http.PostAsync(new Uri("/baja?handler=Start", UriKind.Relative), baja);

        Assert.AreEqual(HttpStatusCode.BadRequest, portalResponse.StatusCode, "portal");
        StringAssert.Contains(await portalResponse.Content.ReadAsStringAsync(), "El formulario venció");
        Assert.AreEqual(HttpStatusCode.BadRequest, adminResponse.StatusCode, "admin");
        Assert.AreEqual(HttpStatusCode.BadRequest, bajaResponse.StatusCode, "baja");
        Assert.IsTrue(h.Mail.Sent.IsEmpty);
    }

    [TestMethod]
    public async Task Baja_SinIniciarSesion_DaConstanciaEnPantallaYPorCorreo()
    {
        await using var h = await UiHarness.Start();
        using var c = h.NewClient();

        var page = await c.Html("/baja");
        StringAssert.Contains(page, "Botón de baja de servicio");
        StringAssert.Contains(page, "href=\"/arrepentimiento\"");

        using (var unknown = await c.Submit("/baja", "/baja?handler=Start", new() { ["Email"] = "nadie@example.com" }))
        {
            StringAssert.Contains(await unknown.Content.ReadAsStringAsync(), "Si el correo es de una cuenta");
        }

        Assert.IsTrue(h.Mail.Sent.IsEmpty, "Un correo que no es de ninguna cuenta no recibe nada.");

        using var started = await c.Submit("/baja", "/baja?handler=Start", new() { ["Email"] = "Cliente@Example.com", ["Detail"] = "El VPS de pruebas" });
        var step = await started.Content.ReadAsStringAsync();
        var reference = ReferencePattern().Match(step).Groups[1].Value;
        var code = h.Mail.Last("code");

        using (var wrong = await c.Submit("/baja", "/baja?handler=Confirm", new() { ["Reference"] = reference, ["Code"] = code == "000000" ? "111111" : "000000" }))
        {
            StringAssert.Contains(await wrong.Content.ReadAsStringAsync(), "no es correcto");
        }

        using var confirmed = await c.Submit("/baja", "/baja?handler=Confirm", new() { ["Reference"] = reference, ["Code"] = code });
        var receipt = h.Mail.Last("receipt");
        StringAssert.StartsWith(receipt, "BAJA-");
        StringAssert.Contains(await confirmed.Content.ReadAsStringAsync(), receipt);
        var request = await h.Query(db => db.Set<CancellationRequest>().SingleAsync());
        CollectionAssert.AreEqual(new[] { h.AccountA }, request.AccountIds);
        Assert.AreEqual(ConsumerRequestKind.Cancellation, request.Kind);
        Assert.AreEqual(2, request.Attempts);
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "consumer.cancellation")));
    }

    [TestMethod]
    public async Task Recuperar_ConElEnlaceDelCorreo_CambiaLaContraseña()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();

        (await c.Submit("/recuperar", "/recuperar", new() { ["Email"] = "titular@example.com" })).Dispose();
        (await c.Submit("/recuperar", "/recuperar", new() { ["Email"] = "nadie@example.com" })).Dispose();

        var link = new Uri(h.Mail.Sent.Single(m => m.Kind == "reset").Value);
        Assert.AreEqual("billing.example.com", link.Host);
        var path = link.PathAndQuery;
        var query = System.Web.HttpUtility.ParseQueryString(link.Query);
        using var reset = await c.Submit(path, path, new() { ["U"] = query["u"]!, ["T"] = query["t"]!, ["Password"] = "clave-nueva-larga", ["Confirm"] = "clave-nueva-larga" });
        StringAssert.Contains(await reset.Content.ReadAsStringAsync(), "Listo");

        using var old = h.NewClient();
        Assert.AreEqual(HttpStatusCode.OK, (await old.Login("titular@example.com")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Redirect, (await old.Login("titular@example.com", "clave-nueva-larga")).StatusCode);
    }

    internal static async Task<Client> StaffSession(UiHarness h, string email)
    {
        var c = h.NewClient();
        (await c.Submit("/admin/ingresar", "/admin/ingresar", new() { ["Email"] = email, ["Password"] = UiHarness.Password })).Dispose();
        var key = Key(await c.Html("/admin/ingresar/codigo"));
        (await c.Submit("/admin/ingresar/codigo", "/admin/ingresar/codigo", new() { ["Code"] = UiHarness.Totp(key) })).Dispose();
        return c;
    }

    [GeneratedRegex("<code class=\"key\"[^>]*>([A-Z2-7 ]+)</code>")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("name=\"Reference\" value=\"([a-z0-9]+)\"")]
    private static partial Regex ReferencePattern();
}
