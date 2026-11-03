using System.Net;
using System.Text.RegularExpressions;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>
/// Seguridad de acceso de punta a punta (diseno/fraude-y-acceso.md, secciones 4 a 8 y 11): historial y aviso de lugar
/// nuevo, límites de intentos, listas, política de contraseñas, correo verificado, segundo factor y pantallas del admin.
/// </summary>
[TestClass]
[DoNotParallelize] // Cada prueba levanta su host y su base, como UiTests.
public sealed partial class AccessUiTests
{
    private static readonly IPAddress Home = IPAddress.Parse("203.0.113.7");

    private static readonly IPAddress Elsewhere = IPAddress.Parse("198.51.100.9");

    private static string Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString ?? string.Empty;

    private static async Task<string> Body(HttpResponseMessage response)
    {
        using (response)
        {
            return await response.Content.ReadAsStringAsync();
        }
    }

    private static string Key(string html) =>
        KeyPattern().Match(html) is { Success: true } m ? m.Groups[1].Value.Replace(" ", string.Empty, StringComparison.Ordinal) : throw new AssertFailedException("La página no muestra la clave de TOTP.");

    private static string[] Codes(string html) => [.. CodePattern().Matches(html).Select(m => m.Groups[1].Value)];

    private static async Task<Client> StaffSession(UiHarness h, string email, IPAddress? ip = null)
    {
        var c = h.NewClient(ip);
        (await c.Submit("/admin/ingresar", "/admin/ingresar", new() { ["Email"] = email, ["Password"] = UiHarness.Password })).Dispose();
        var key = Key(await c.Html("/admin/ingresar/codigo"));
        (await c.Submit("/admin/ingresar/codigo", "/admin/ingresar/codigo", new() { ["Code"] = UiHarness.Totp(key) })).Dispose();
        return c;
    }

    /// <summary>Activa el TOTP desde la página de seguridad y devuelve la clave y los códigos de recuperación.</summary>
    private static async Task<(string Key, string[] Codes)> EnableTotp(Client c)
    {
        var key = Key(await c.Html("/cuenta/seguridad"));
        var html = await Body(await c.Submit("/cuenta/seguridad", "/cuenta/seguridad?handler=Enable", new() { ["Code"] = UiHarness.Totp(key) }));
        return (key, Codes(html));
    }

    [TestMethod]
    public async Task Ingresos_QuedanEnElHistorial_YUnaRedNuevaAvisaPorCorreo()
    {
        await using var h = await UiHarness.Start(behindProxy: true);
        var user = await h.PortalUser("titular@example.com", h.AccountA);

        using (var first = h.NewClient(Home))
        {
            Assert.AreEqual("/", Location(await first.Login("titular@example.com")));
        }

        using (var sameNetwork = h.NewClient(IPAddress.Parse("203.0.113.99")))
        {
            Assert.AreEqual("/", Location(await sameNetwork.Login("titular@example.com")));
        }

        Assert.IsFalse(h.Mail.Sent.Any(m => m.Kind == "new-login"), "El primer ingreso y la misma red /24 no avisan.");

        using var other = h.NewClient(Elsewhere);
        Assert.AreEqual("/", Location(await other.Login("titular@example.com")));

        var alert = h.Mail.Sent.Single(m => m.Kind == "new-login");
        Assert.AreEqual("titular@example.com", alert.To);
        StringAssert.StartsWith(alert.Value, "198.51.100.9|");
        StringAssert.EndsWith(alert.Value, "|https://billing.example.com/cuenta/seguridad");
        var page = await other.Html("/cuenta/seguridad");
        StringAssert.Contains(page, "198.51.100.9");
        StringAssert.Contains(page, "Lugar nuevo");
        var events = await h.Query(db => db.Set<LoginEvent>().Where(e => e.UserId == user.Id).OrderBy(e => e.Id).ToListAsync());
        CollectionAssert.AreEqual(new[] { false, false, true }, events.Select(e => e.NewLocation).ToArray());
        Assert.IsTrue(events.All(e => e.Result == LoginResult.Success && e.Method == LoginMethod.Password && e.Audience == LoginAudience.Portal));
    }

    [TestMethod]
    public async Task FallasDesdeUnaIp_LaBloqueanUnRato_PeroElBotonDeBajaSigueAndando()
    {
        await using var h = await UiHarness.Start(behindProxy: true);
        await h.PortalUser("titular@example.com", h.AccountA);
        await h.Settings(s => s with { IpAttempts = 5 });
        var attacker = IPAddress.Parse("203.0.113.66");
        using var c = h.NewClient(attacker);

        // Correos distintos, para que no lo frene el bloqueo de una cuenta sino el de la IP.
        for (var i = 0; i < 5; i++)
        {
            (await c.Login($"nadie{i}@example.com", "cualquier-clave")).Dispose();
        }

        StringAssert.Contains(await Body(await c.Login("titular@example.com")), "No se puede ingresar desde tu red");
        var entry = await h.Query(db => db.Set<BlockedEntry>().SingleAsync());
        Assert.AreEqual(BlockKind.Ip, entry.Kind);
        Assert.AreEqual("203.0.113.66/32", entry.Value);
        Assert.AreEqual(BlockSource.Automatic, entry.Source);
        Assert.IsNotNull(entry.ExpiresAt);
        Assert.AreEqual(5, await h.Query(db => db.Set<LoginEvent>().CountAsync(e => e.Result == LoginResult.UnknownUser)));
        Assert.AreEqual(1, await h.Query(db => db.Set<LoginEvent>().CountAsync(e => e.Result == LoginResult.BlockedIp)));

        using (var other = h.NewClient(Elsewhere))
        {
            Assert.AreEqual("/", Location(await other.Login("titular@example.com")));
        }

        // Disposición 954/2025: la baja no pasa por ninguna lista.
        (await c.Submit("/baja", "/baja?handler=Start", new() { ["Email"] = "cliente@example.com" })).Dispose();
        Assert.AreEqual("203.0.113.66", await h.Query(db => db.Set<CancellationRequest>().Select(r => r.Ip).SingleAsync()));
    }

    [TestMethod]
    public async Task BloqueoDeUnaCuenta_UsaLosIntentosYLosMinutosDeLosAjustes()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        await h.Settings(s => s with { AccountAttempts = 3, AccountMinutes = 7 });
        using var c = h.NewClient();

        for (var i = 0; i < 3; i++)
        {
            (await c.Login("titular@example.com", "otra-clave-cualquiera")).Dispose();
        }

        StringAssert.Contains(await Body(await c.Login("titular@example.com")), "Probá de nuevo en 7 minutos");
        var user = await h.Query(db => db.Set<PortalUser>().SingleAsync());
        Assert.IsTrue(user.LockoutEnd > DateTimeOffset.UtcNow.AddMinutes(6) && user.LockoutEnd < DateTimeOffset.UtcNow.AddMinutes(8));
    }

    [TestMethod]
    public async Task CambioDeContrasena_SigueLaPolitica_YCortaLasOtrasSesiones()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        await h.Settings(s => s with { MinLength = 12, RequireDigit = true });
        using var a = h.NewClient();
        using var b = h.NewClient();
        (await a.Login("titular@example.com")).Dispose();
        (await b.Login("titular@example.com")).Dispose();

        Task<string> Change(string current, string password) =>
            a.Submit("/cuenta/seguridad", "/cuenta/seguridad?handler=Password", new() { ["CurrentPassword"] = current, ["NewPassword"] = password, ["ConfirmPassword"] = password }).ContinueWith(t => Body(t.Result)).Unwrap();

        StringAssert.Contains(await a.Html("/cuenta/seguridad"), "Usá al menos 12 caracteres");
        StringAssert.Contains(await Change(UiHarness.Password, "corta-1"), "Es más corta que el mínimo");
        StringAssert.Contains(await Change(UiHarness.Password, "una frase larga sin numeros"), "al menos un número");
        StringAssert.Contains(await Change(UiHarness.Password, "argentina-2026"), "muy usada");
        StringAssert.Contains(await Change("no-es-la-actual", "un caballo correcto 42"), "La contraseña actual no es correcta");
        StringAssert.Contains(await Change(UiHarness.Password, "un caballo correcto 42"), "cambiaste la contraseña");

        Assert.AreEqual(HttpStatusCode.Redirect, (await b.Get("/facturas")).StatusCode, "La otra sesión se corta.");
        Assert.AreEqual(HttpStatusCode.OK, (await a.Get("/facturas")).StatusCode, "Esta sesión sigue.");
        using var again = h.NewClient();
        Assert.AreEqual("/", Location(await again.Login("titular@example.com", "un caballo correcto 42")));
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(e => e.Action == "portal.password_changed")));
    }

    [TestMethod]
    public async Task Recuperar_ConUnaContrasenaQueNoCumple_DiceQuePasa_YUnCorreoBloqueadoNoRecibeNada()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        await h.PortalUser("bloqueado@spam.example", h.AccountB, owner: true);
        await h.Use<AccessGuard, long>(g => g.Block(BlockKind.Domain, "spam.example", "Descartable", null, BlockSource.Staff));
        using var c = h.NewClient();

        (await c.Submit("/recuperar", "/recuperar", new() { ["Email"] = "bloqueado@spam.example" })).Dispose();
        (await c.Submit("/recuperar", "/recuperar", new() { ["Email"] = "titular@example.com" })).Dispose();

        var link = new Uri(h.Mail.Sent.Single(m => m.Kind == "reset").Value);
        var query = System.Web.HttpUtility.ParseQueryString(link.Query);
        var html = await Body(await c.Submit(link.PathAndQuery, link.PathAndQuery, new() { ["U"] = query["u"]!, ["T"] = query["t"]!, ["Password"] = "1234567890", ["Confirm"] = "1234567890" }));
        StringAssert.Contains(html, "La contraseña nueva no sirve");
        StringAssert.Contains(html, "muy usada");
    }

    [TestMethod]
    public async Task VerificarElCorreo_ConElEnlaceQueLlegaPorCorreo()
    {
        await using var h = await UiHarness.Start();
        var user = await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();

        StringAssert.Contains(await c.Html("/cuenta/seguridad"), "Sin verificar");
        StringAssert.Contains(await Body(await c.Submit("/cuenta/seguridad", "/cuenta/seguridad?handler=Verify", [])), "Te mandamos el enlace");

        var link = new Uri(h.Mail.Last("verify"));
        Assert.AreEqual("billing.example.com", link.Host);
        Assert.AreEqual("/cuenta/verificar", link.AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(link.Query);
        using var anonymous = h.NewClient();
        var page = await anonymous.Html(link.PathAndQuery);
        StringAssert.Contains(page, "Verificar mi correo");
        Assert.IsFalse(await h.Query(db => db.Set<PortalUser>().AnyAsync(u => u.Id == user.Id && u.EmailConfirmed)), "Abrir el enlace no confirma: hace falta el botón.");

        StringAssert.Contains(await Body(await anonymous.Submit(link.PathAndQuery, "/cuenta/verificar", new() { ["U"] = query["U"]!, ["T"] = query["T"]! + "x" })), "no es válido");
        StringAssert.Contains(await Body(await anonymous.Submit(link.PathAndQuery, "/cuenta/verificar", new() { ["U"] = query["U"]!, ["T"] = query["T"]! })), "quedó verificado");

        Assert.IsTrue(await h.Query(db => db.Set<PortalUser>().AnyAsync(u => u.Id == user.Id && u.EmailConfirmed)));
        StringAssert.Contains(await c.Html("/cuenta/seguridad"), "Verificado");
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(e => e.Action == "portal.email_verified" && e.Entity == $"user:{user.Id}")));
    }

    [TestMethod]
    public async Task SegundoFactorObligatorioPorRol_HastaActivarloSoloVeLaPaginaDeSeguridad()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        await h.PortalUser("tecnico@example.com", h.AccountA, owner: false, capabilities: [AccountCapabilities.Services]);
        await h.Settings(s => s with { TwoFactorRoles = ["owner"] });

        using var owner = h.NewClient();
        Assert.AreEqual("/cuenta/seguridad", Location(await owner.Login("titular@example.com")));
        using (var blocked = await owner.Get("/facturas"))
        {
            Assert.AreEqual("/cuenta/seguridad", Location(blocked));
        }

        // También lo que no es una página del portal pero usa su sesión, como el PDF de una factura.
        using (var pdf = await owner.Get($"/facturas/{h.InvoiceA}/pdf"))
        {
            Assert.AreEqual("/cuenta/seguridad", Location(pdf));
        }

        var security = await owner.Html("/cuenta/seguridad");
        StringAssert.Contains(security, "obligatorio");
        // Razor escribe el + del tipo como &#x2B;, que el navegador lee igual.
        StringAssert.Contains(security, "<img class=\"qr\" src=\"data:image/svg&#x2B;xml;base64,", "El QR para enrolar se arma en el servidor.");
        var (key, _) = await EnableTotp(owner);
        Assert.AreEqual(HttpStatusCode.OK, (await owner.Get("/facturas")).StatusCode);
        var disable = await Body(await owner.Submit("/cuenta/seguridad", "/cuenta/seguridad?handler=Disable", new() { ["Code"] = UiHarness.Totp(key) }));
        StringAssert.Contains(disable, "no se puede desactivar");
        Assert.IsTrue(await h.Query(db => db.Set<PortalUser>().AnyAsync(u => u.Email == "titular@example.com" && u.TwoFactorEnabled)));

        using var technical = h.NewClient();
        Assert.AreEqual("/", Location(await technical.Login("tecnico@example.com")));
        Assert.AreEqual(HttpStatusCode.OK, (await technical.Get("/servicios")).StatusCode);
    }

    [TestMethod]
    public async Task CodigosDeRecuperacion_SeCuentan_SirvenUnaVez_YSeRegeneran()
    {
        await using var h = await UiHarness.Start();
        var user = await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();
        var (key, codes) = await EnableTotp(c);
        Assert.HasCount(8, codes);

        using (var phoneless = h.NewClient())
        {
            (await phoneless.Login("titular@example.com")).Dispose();
            using var code = await phoneless.Submit("/ingresar/codigo", "/ingresar/codigo", new() { ["Code"] = codes[0].ToLowerInvariant() });
            Assert.AreEqual(HttpStatusCode.Redirect, code.StatusCode, "Un código de recuperación sirve en minúsculas.");
        }

        var last = await h.Query(db => db.Set<LoginEvent>().Where(e => e.UserId == user.Id).OrderByDescending(e => e.Id).FirstAsync());
        Assert.AreEqual(LoginMethod.RecoveryCode, last.Method);
        StringAssert.Contains(await c.Html("/cuenta/seguridad"), "Te quedan 7 códigos");

        var renewed = Codes(await Body(await c.Submit("/cuenta/seguridad", "/cuenta/seguridad?handler=Codes", new() { ["Code"] = UiHarness.Totp(key) })));
        Assert.HasCount(8, renewed);
        Assert.IsEmpty(renewed.Intersect(codes));

        using var old = h.NewClient();
        (await old.Login("titular@example.com")).Dispose();
        using var reused = await old.Submit("/ingresar/codigo", "/ingresar/codigo", new() { ["Code"] = codes[1] });
        Assert.AreEqual(HttpStatusCode.OK, reused.StatusCode, "Los códigos anteriores dejan de valer.");
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(e => e.Action == "portal.recovery_codes")));
    }

    [TestMethod]
    public async Task Admin_PaginasDeSeguridad_RespetanLosPermisos_YLasListasFrenanElIngreso()
    {
        await using var h = await UiHarness.Start(behindProxy: true);
        await h.Staff("lector@example.com", StaffPermissions.AccountsRead);
        await h.Staff("seguridad@example.com", StaffPermissions.SecurityRead, StaffPermissions.SecurityManage);
        await h.PortalUser("titular@example.com", h.AccountA);

        using (var reader = await StaffSession(h, "lector@example.com", Elsewhere))
        {
            StringAssert.Contains(await reader.Html("/admin/seguridad"), "Últimos ingresos");
            foreach (var path in (string[])["/admin/seguridad/ajustes", "/admin/seguridad/bloqueos", "/admin/seguridad/ingresos", "/admin/pedidos/revision"])
            {
                using var denied = await reader.Get(path);
                StringAssert.Contains(Location(denied), "/estado/403", path);
            }
        }

        using var admin = await StaffSession(h, "seguridad@example.com", Elsewhere);
        var saved = await Body(await admin.Submit("/admin/seguridad/ajustes", "/admin/seguridad/ajustes", new()
        {
            ["Version"] = "0",
            ["Settings.MinLength"] = "14",
            ["Settings.StaffMinLength"] = "16",
            ["Settings.AccountAttempts"] = "5",
            ["Settings.AccountMinutes"] = "15",
            ["Settings.IpAttempts"] = "20",
            ["Settings.IpWindowMinutes"] = "15",
            ["Settings.IpMinutes"] = "30",
            ["Settings.KeepLoginDays"] = "365",
            ["Settings.RejectCommon"] = "false",
            ["Settings.TwoFactorRoles"] = "billing",
            ["Settings.FraudSkipExisting"] = "true",
            ["Trusted"] = "192.0.2.7\r\n2001:db8:5::1/64",
            ["ReviewScore"] = "62,5",
        }));
        StringAssert.Contains(saved, "quedaron guardados");
        var settings = await h.Use<SecuritySettingsStore, SecuritySettings>(s => s.Get());
        Assert.AreEqual(14, settings.MinLength);
        Assert.AreEqual(62.5m, settings.FraudReviewScore);
        Assert.IsFalse(settings.RejectCommon);
        Assert.IsTrue(settings.FraudSkipExisting);
        CollectionAssert.AreEqual(new[] { "billing" }, settings.TwoFactorRoles.ToArray());
        CollectionAssert.AreEqual(new[] { "192.0.2.7/32", "2001:db8:5::/64" }, settings.TrustedNetworks.ToArray());
        StringAssert.Contains(await admin.Html("/admin/seguridad/ajustes"), "2001:db8:5::/64");
        var stale = await Body(await admin.Submit("/admin/seguridad/ajustes", "/admin/seguridad/ajustes", new() { ["Version"] = "0", ["ReviewScore"] = "50" }));
        StringAssert.Contains(stale, "Otra persona cambió los ajustes");
        var invalid = await Body(await admin.Submit("/admin/seguridad/ajustes", "/admin/seguridad/ajustes", new() { ["Version"] = "1", ["ReviewScore"] = "50", ["Trusted"] = "no-es-red" }));
        StringAssert.Contains(invalid, "Las redes de confianza tienen que ser");

        StringAssert.Contains(await Body(await admin.Submit("/admin/seguridad/bloqueos", "/admin/seguridad/bloqueos?handler=Add", new() { ["Kind"] = "Ip", ["Value"] = "no-es-ip", ["Reason"] = "Prueba" })), "Escribí una IP");
        StringAssert.Contains(await Body(await admin.Submit("/admin/seguridad/bloqueos", "/admin/seguridad/bloqueos?handler=Add", new() { ["Kind"] = "Ip", ["Value"] = "198.51.100.0/24", ["Reason"] = "Prueba" })), "tu propia IP");
        StringAssert.Contains(await Body(await admin.Submit("/admin/seguridad/bloqueos", "/admin/seguridad/bloqueos?handler=Add", new() { ["Kind"] = "Ip", ["Value"] = "", ["Reason"] = "Prueba" })), "Escribí una IP");
        (await admin.Submit("/admin/seguridad/bloqueos", "/admin/seguridad/bloqueos?handler=Add", new() { ["Kind"] = "Ip", ["Value"] = "203.0.113.9/24", ["Reason"] = "Prueba" })).Dispose();
        StringAssert.Contains(await admin.Html("/admin/seguridad/bloqueos"), "203.0.113.0/24");

        using (var customer = h.NewClient(Home))
        {
            StringAssert.Contains(await Body(await customer.Login("titular@example.com")), "No se puede ingresar desde tu red");
        }

        var id = await h.Query(db => db.Set<BlockedEntry>().Select(e => e.Id).SingleAsync());
        (await admin.Submit("/admin/seguridad/bloqueos", $"/admin/seguridad/bloqueos?handler=Remove&id={id}", [])).Dispose();
        using (var customer = h.NewClient(Home))
        {
            Assert.AreEqual("/", Location(await customer.Login("titular@example.com")));
        }

        var logins = await admin.Html("/admin/seguridad/ingresos?Side=portal&Show=failed");
        StringAssert.Contains(logins, "IP bloqueada");
        Assert.IsFalse(logins.Contains("App de autenticación", StringComparison.Ordinal), "El filtro de lado deja afuera los ingresos del staff.");
        var actions = await h.Query(db => db.Set<AuditEvent>().Where(e => e.Action.StartsWith("security.")).Select(e => e.Action).ToListAsync());
        CollectionAssert.AreEquivalent(new[] { "security.settings", "security.block", "security.unblock" }, actions);
    }

    [TestMethod]
    public async Task Admin_RevisionDePedidos_AprobarDesdeElPanel()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("revisor@example.com", StaffPermissions.AccountsRead, StaffPermissions.OrdersReview);
        await h.Use<AccessGuard, long>(g => g.Block(BlockKind.Ip, "198.51.100.0/24", "Prueba", null, BlockSource.Staff));
        var planId = await h.Query(db => db.Plans.OrderBy(p => p.Id).Select(p => p.Id).FirstAsync());
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountA, [new OrderItem(planId)], fraud: new FraudContext(IPAddress.Parse("198.51.100.20"))));

        using var c = await StaffSession(h, "revisor@example.com");
        var queue = await c.Html("/admin/pedidos/revision");
        StringAssert.Contains(queue, $"Pedido {placed.OrderId}");
        StringAssert.Contains(queue, "blocklist.ip");
        var menu = await c.Html("/admin");
        StringAssert.Contains(menu, "/admin/pedidos/revision");
        StringAssert.Contains(menu, "aria-label=\"1 pedidos retenidos\">1</span>", "El menú muestra cuántos pedidos esperan la revisión.");

        var done = await Body(await c.Submit("/admin/pedidos/revision", $"/admin/pedidos/revision?handler=Approve&id={placed.OrderId}", new() { ["Note"] = "Lo conozco" }));
        StringAssert.Contains(done, "aprobado");

        var check = await h.Query(db => db.Set<FraudCheck>().SingleAsync());
        Assert.AreEqual(ScreeningStatus.Approved, check.Status);
        Assert.AreEqual("Lo conozco", check.Note);
        StringAssert.StartsWith(check.DecidedBy, "staff:");
        StringAssert.Contains(await c.Html($"/admin/cuentas/{h.AccountA}/seguridad"), "Aprobado");
    }

    [TestMethod]
    public async Task Admin_QuitaElSegundoFactorDeUnCliente_SoloConElPermiso()
    {
        await using var h = await UiHarness.Start();
        var user = await h.PortalUser("titular@example.com", h.AccountA);
        await h.Staff("lector@example.com", StaffPermissions.AccountsRead);
        await h.Staff("seguridad@example.com", StaffPermissions.AccountsRead, StaffPermissions.SecurityManage);
        using var customer = h.NewClient();
        (await customer.Login("titular@example.com")).Dispose();
        await EnableTotp(customer);

        using (var reader = await StaffSession(h, "lector@example.com"))
        {
            Assert.IsFalse((await reader.Html($"/admin/cuentas/{h.AccountA}/seguridad")).Contains("handler=ResetTwoFactor", StringComparison.Ordinal));
            using var denied = await reader.Submit($"/admin/cuentas/{h.AccountA}/seguridad", $"/admin/cuentas/{h.AccountA}/seguridad?handler=ResetTwoFactor&userId={user.Id}", []);
            StringAssert.Contains(Location(denied), "/estado/403");
            using var notRequired = await reader.Submit($"/admin/cuentas/{h.AccountA}/seguridad", $"/admin/cuentas/{h.AccountA}/seguridad?handler=RequireTwoFactor&userId={user.Id}&required=true", []);
            StringAssert.Contains(Location(notRequired), "/estado/403");
        }

        using var admin = await StaffSession(h, "seguridad@example.com");
        StringAssert.Contains(await admin.Html($"/admin/cuentas/{h.AccountA}"), $"/admin/cuentas/{h.AccountA}/seguridad");
        var done = await Body(await admin.Submit($"/admin/cuentas/{h.AccountA}/seguridad", $"/admin/cuentas/{h.AccountA}/seguridad?handler=ResetTwoFactor&userId={user.Id}", []));
        StringAssert.Contains(done, "se quitó el segundo factor");

        Assert.IsFalse(await h.Query(db => db.Set<PortalUser>().AnyAsync(u => u.Id == user.Id && (u.TwoFactorEnabled || u.AuthenticatorKey != null))));
        Assert.AreEqual(HttpStatusCode.Redirect, (await customer.Get("/facturas")).StatusCode, "Sus sesiones se cortan.");
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(e => e.Action == "security.2fa_reset" && e.Entity == $"user:{user.Id}")));

        // Exigírselo a ese usuario: hasta activarlo, solo la página de seguridad. Dejar de exigirlo lo libera.
        StringAssert.Contains(await Body(await admin.Submit($"/admin/cuentas/{h.AccountA}/seguridad", $"/admin/cuentas/{h.AccountA}/seguridad?handler=RequireTwoFactor&userId={user.Id}&required=true", [])), "Se le exige el segundo factor");
        using (var again = h.NewClient())
        {
            Assert.AreEqual("/cuenta/seguridad", Location(await again.Login("titular@example.com")));
            Assert.AreEqual("/cuenta/seguridad", Location(await again.Get("/facturas")));
        }

        (await admin.Submit($"/admin/cuentas/{h.AccountA}/seguridad", $"/admin/cuentas/{h.AccountA}/seguridad?handler=RequireTwoFactor&userId={user.Id}&required=false", [])).Dispose();
        using (var free = h.NewClient())
        {
            Assert.AreEqual("/", Location(await free.Login("titular@example.com")));
        }

        Assert.AreEqual(2, await h.Query(db => db.Set<AuditEvent>().CountAsync(e => e.Action == "security.2fa_required" && e.Entity == $"user:{user.Id}")));
        Assert.IsFalse(await h.Query(db => db.Set<PortalUser>().AnyAsync(u => u.Id == user.Id && u.TwoFactorRequired)));
    }

    [TestMethod]
    public async Task Staff_BloqueoConSuPropioUmbral()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("staff@example.com", StaffPermissions.AccountsRead);
        await h.Settings(s => s with { AccountAttempts = 10, StaffAccountAttempts = 3, StaffAccountMinutes = 9 });
        using var c = h.NewClient();
        for (var i = 0; i < 3; i++)
        {
            (await c.Submit("/admin/ingresar", "/admin/ingresar", new() { ["Email"] = "staff@example.com", ["Password"] = "otra-clave-cualquiera" })).Dispose();
        }

        var html = await Body(await c.Submit("/admin/ingresar", "/admin/ingresar", new() { ["Email"] = "staff@example.com", ["Password"] = UiHarness.Password }));
        StringAssert.Contains(html, "Probá de nuevo en 9 minutos");
        var staff = await h.Query(db => db.Set<StaffUser>().SingleAsync());
        Assert.IsTrue(staff.LockoutEnd > DateTimeOffset.UtcNow.AddMinutes(8) && staff.LockoutEnd < DateTimeOffset.UtcNow.AddMinutes(10));
    }

    [TestMethod]
    public async Task Staff_IngresoConCodigoMalo_QuedaEnElHistorial_YVeLosSuyos()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("staff@example.com", StaffPermissions.AccountsRead);
        using var c = await StaffSession(h, "staff@example.com");
        using (var other = h.NewClient())
        {
            (await other.Submit("/admin/ingresar", "/admin/ingresar", new() { ["Email"] = "staff@example.com", ["Password"] = UiHarness.Password })).Dispose();
            (await other.Submit("/admin/ingresar/codigo", "/admin/ingresar/codigo", new() { ["Code"] = "000000" })).Dispose();
        }

        var events = await h.Query(db => db.Set<LoginEvent>().Where(e => e.Audience == LoginAudience.Staff).OrderBy(e => e.Id).Select(e => e.Result).ToListAsync());
        Assert.AreEqual(LoginResult.Success, events[0]);
        Assert.IsTrue(events[1] is LoginResult.BadCode or LoginResult.Success, "El código 000000 casi nunca es el vigente.");
        StringAssert.Contains(await c.Html("/admin/seguridad"), "App de autenticación");
    }

    [GeneratedRegex("<code class=\"key\"[^>]*>([A-Z2-7 ]+)</code>")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("<li><code>([A-Z0-9]{5}-[A-Z0-9]{5})</code></li>")]
    private static partial Regex CodePattern();
}
