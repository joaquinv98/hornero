using System.Net;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.Domain.Accounts;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>
/// Registro de clientes en el portal (diseno/ingreso-clientes.md, sección 2): cuenta y usuario titular en una
/// transacción, verificación del correo con su plantilla, datos fiscales y vuelta al pedido, con el ajuste que lo apaga,
/// las listas de bloqueo y el tope de altas por IP.
/// </summary>
[TestClass]
[DoNotParallelize] // Cada prueba levanta su host y su base, como UiTests.
public sealed class RegistrationUiTests
{
    private static readonly IPAddress Home = IPAddress.Parse("203.0.113.7");

    private const string Strong = "una frase larga para entrar";

    private static string Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString ?? string.Empty;

    private static async Task<string> Body(HttpResponseMessage response)
    {
        using (response)
        {
            return await response.Content.ReadAsStringAsync();
        }
    }

    private static Task<HttpResponseMessage> Register(Client c, string email, string name = "Ana Pérez", string password = Strong, string? back = null) =>
        c.Submit("/registro", "/registro", new()
        {
            ["Name"] = name,
            ["Company"] = string.Empty,
            ["Email"] = email,
            ["Phone"] = "+54 11 5555-0000",
            ["Currency"] = "ARS",
            ["Password"] = password,
            ["Confirm"] = password,
            ["ReturnUrl"] = back ?? "/",
        });

    [TestMethod]
    public async Task Registro_CreaCuentaYTitular_VerificaElCorreo_PideLosDatosFiscales_YSigueAlPedido()
    {
        await using var h = await UiHarness.Start(behindProxy: true);
        using var c = h.NewClient(Home);

        var login = await c.Html("/ingresar?ReturnUrl=%2Fcontratar%2F5");
        StringAssert.Contains(login, "href=\"/registro?ReturnUrl=%2Fcontratar%2F5\"");
        StringAssert.Contains(await c.Html("/registro?ReturnUrl=%2Fcontratar%2F5"), "Crear la cuenta");

        using (var created = await Register(c, "ana@example.com", back: "/contratar/5"))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, created.StatusCode);
            Assert.AreEqual("/cuenta/datos-fiscales?nuevo=1&volver=%2Fcontratar%2F5", Location(created));
        }

        var user = await h.Query(db => db.Set<PortalUser>().SingleAsync(u => u.Email == "ana@example.com"));
        Assert.IsFalse(user.EmailConfirmed, "El correo se verifica con el enlace.");
        var account = await h.Query(db => db.Accounts.SingleAsync(a => a.Email == "ana@example.com"));
        Assert.AreEqual("Ana Pérez", account.Name);
        Assert.AreEqual("ARS", account.Currency.Code);
        Assert.AreEqual("+54 11 5555-0000", account.Phone);
        Assert.IsTrue(await h.Query(db => db.Set<AccountUser>().AnyAsync(m => m.AccountId == account.Id && m.UserId == user.Id && m.IsOwner)));
        var audit = await h.Query(db => db.Set<AuditEvent>().SingleAsync(e => e.Action == AccessGuard.RegistrationAction));
        Assert.AreEqual($"user:{user.Id}", audit.Entity);
        Assert.AreEqual(Home, audit.Ip);
        Assert.AreEqual(LoginMethod.Password, (await h.Query(db => db.Set<LoginEvent>().SingleAsync(e => e.UserId == user.Id))).Method);

        // Entró: los datos fiscales de la cuenta nueva, con el formulario abierto y el paso para seguir al pedido.
        var tax = await c.Html("/cuenta/datos-fiscales?nuevo=1&volver=%2Fcontratar%2F5");
        StringAssert.Contains(tax, "Tu cuenta está lista");
        StringAssert.Contains(tax, "href=\"/contratar/5\"");
        StringAssert.Contains(tax, "<details open=\"open\">");
        using (var saved = await c.Submit("/cuenta/datos-fiscales?nuevo=1&volver=%2Fcontratar%2F5", "/cuenta/datos-fiscales?handler=Tax", new()
        {
            ["accountId"] = account.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["documentType"] = "Dni",
            ["documentNumber"] = "30123456",
            ["legalName"] = "Ana Pérez",
            ["vatCondition"] = "5",
            ["country"] = "AR",
            ["province"] = "AR-B",
            ["volver"] = "/contratar/5",
        }))
        {
            Assert.AreEqual("/contratar/5", Location(saved), "Guardar los datos sigue al pedido.");
        }

        Assert.AreEqual(VatConditions.ConsumidorFinal, await h.Query(db => db.TaxIdentities.Where(t => t.AccountId == account.Id && t.ValidTo == null).Select(t => t.VatCondition).SingleAsync()));

        // El correo de verificación usa la plantilla de verificación, con el enlace armado con la URL pública.
        var link = new Uri(h.Mail.Sent.Single(m => m.Kind == "verify" && m.To == "ana@example.com").Value);
        Assert.AreEqual("billing.example.com", link.Host);
        var query = System.Web.HttpUtility.ParseQueryString(link.Query);
        StringAssert.Contains(await Body(await c.Submit(link.PathAndQuery, "/cuenta/verificar", new() { ["U"] = query["U"]!, ["T"] = query["T"]! })), "quedó verificado");
        Assert.IsTrue(await h.Query(db => db.Set<PortalUser>().AnyAsync(u => u.Id == user.Id && u.EmailConfirmed)));

        // Con la sesión abierta, el registro manda de vuelta.
        Assert.AreEqual("/contratar/5", Location(await c.Get("/registro?ReturnUrl=%2Fcontratar%2F5")));
        using var again = h.NewClient(Home);
        Assert.AreEqual("/", Location(await again.Login("ana@example.com", Strong)));
    }

    [TestMethod]
    public async Task Registro_RespetaLaPolitica_NoRepiteCorreos_YNoDejaElegirOtraMoneda()
    {
        await using var h = await UiHarness.Start(behindProxy: true);
        using var c = h.NewClient(Home);

        var weak = await Body(await Register(c, "debil@example.com", password: "argentina-2026"));
        StringAssert.Contains(weak, "La contraseña nueva no sirve");
        StringAssert.Contains(weak, "muy usada");

        StringAssert.Contains(await Body(await Register(c, "ajena@example.com")), "Ya hay una cuenta con ese correo", "Una cuenta del staff sin usuario también cuenta.");
        await h.PortalUser("titular@example.com", h.AccountA);
        StringAssert.Contains(await Body(await Register(c, "TITULAR@example.com")), "Ya hay una cuenta con ese correo");
        StringAssert.Contains(await Body(await c.Submit("/registro", "/registro", new() { ["Name"] = "Ana", ["Email"] = "otra@example.com", ["Currency"] = "EUR", ["Password"] = Strong, ["Confirm"] = Strong })), "Elegí una de las monedas");
        StringAssert.Contains(await Body(await c.Submit("/registro", "/registro", new() { ["Name"] = "Ana", ["Email"] = "otra@example.com", ["Currency"] = "ARS", ["Password"] = Strong, ["Confirm"] = Strong + "x" })), "no coinciden");

        Assert.AreEqual(0, await h.Query(db => db.Set<AuditEvent>().CountAsync(e => e.Action == AccessGuard.RegistrationAction)));
        Assert.AreEqual(1, await h.Query(db => db.Set<PortalUser>().CountAsync()), "Nada quedó a medias.");
        Assert.AreEqual(2, await h.Query(db => db.Accounts.CountAsync()));
    }

    [TestMethod]
    public async Task Registro_ApagadoListasDeBloqueoYTopePorIp()
    {
        await using var h = await UiHarness.Start(behindProxy: true);
        await h.Settings(s => s with { RegistrationIpLimit = 2, TrustedNetworks = ["198.51.100.0/24"] });
        await h.Use<AccessGuard, long>(g => g.Block(BlockKind.Domain, "descartable.example", "Casillas descartables", null, BlockSource.Staff));
        await h.Use<AccessGuard, long>(g => g.Block(BlockKind.Ip, "192.0.2.66", "Abuso", null, BlockSource.Staff));

        using (var blocked = h.NewClient(IPAddress.Parse("192.0.2.66")))
        {
            StringAssert.Contains(await Body(await Register(blocked, "uno@example.com")), "No se puede crear una cuenta desde tu red");
        }

        using var c = h.NewClient(Home);
        StringAssert.Contains(await Body(await Register(c, "alguien@descartable.example")), "No se puede crear una cuenta con ese correo");

        // Tope por IP: dos altas en la ventana, la tercera no. En IPv6 cuenta la red /64.
        Assert.AreEqual(HttpStatusCode.Redirect, (await Register(c, "uno@example.com")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Redirect, (await Register(h.NewClient(Home), "dos@example.com")).StatusCode);
        StringAssert.Contains(await Body(await Register(h.NewClient(Home), "tres@example.com")), "ya se crearon varias cuentas");
        Assert.AreEqual(HttpStatusCode.Redirect, (await Register(h.NewClient(IPAddress.Parse("2001:db8:1:2::10")), "cuatro@example.com")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Redirect, (await Register(h.NewClient(IPAddress.Parse("2001:db8:1:2::11")), "cinco@example.com")).StatusCode);
        StringAssert.Contains(await Body(await Register(h.NewClient(IPAddress.Parse("2001:db8:1:2:aaaa::1")), "seis@example.com")), "ya se crearon varias cuentas");

        // Las redes de confianza no tienen tope.
        for (var i = 0; i < 3; i++)
        {
            Assert.AreEqual(HttpStatusCode.Redirect, (await Register(h.NewClient(IPAddress.Parse("198.51.100.20")), $"oficina{i}@example.com")).StatusCode);
        }

        // Apagado: ni el formulario ni el enlace del ingreso.
        await h.Settings(s => s with { RegistrationEnabled = false });
        using var closed = h.NewClient(IPAddress.Parse("203.0.113.200"));
        StringAssert.Contains(await closed.Html("/registro"), "El registro de cuentas nuevas está cerrado");
        Assert.IsFalse((await closed.Html("/ingresar")).Contains("href=\"/registro", StringComparison.Ordinal));
        // El formulario cerrado no tiene token: se toma el del ingreso, que vale igual.
        StringAssert.Contains(await Body(await closed.Submit("/ingresar", "/registro", new()
        {
            ["Name"] = "Siete", ["Email"] = "siete@example.com", ["Currency"] = "ARS", ["Password"] = Strong, ["Confirm"] = Strong,
        })), "El registro de cuentas nuevas está cerrado");
        Assert.AreEqual(7, await h.Query(db => db.Set<PortalUser>().CountAsync()));
    }

    [TestMethod]
    public async Task Registro_DosAltasALaVezDesdeLaMismaRed_NoPasanElTope()
    {
        await using var h = await UiHarness.Start(behindProxy: true);
        await h.Settings(s => s with { RegistrationIpLimit = 1 });
        var clients = Enumerable.Range(0, 4).Select(_ => h.NewClient(Home)).ToList();

        // Cada uno abre el formulario antes, así los envíos salen juntos.
        var results = await Task.WhenAll(clients.Select((c, i) => Register(c, $"a-la-vez{i}@example.com")));

        Assert.AreEqual(1, results.Count(r => r.StatusCode == HttpStatusCode.Redirect), "El lock de la red deja pasar una sola.");
        Assert.AreEqual(1, await h.Query(db => db.Set<PortalUser>().CountAsync()));
        foreach (var c in clients)
        {
            c.Dispose();
        }
    }
}
