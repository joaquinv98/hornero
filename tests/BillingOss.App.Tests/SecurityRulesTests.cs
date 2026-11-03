using System.Net;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Reglas de seguridad: política de contraseñas, redes, navegadores, listas, ajustes y retención.</summary>
[TestClass]
public sealed class SecurityRulesTests
{
    private static readonly SecuritySettings Defaults = new();

    [TestMethod]
    public async Task Retencion_BorraLosIngresosViejosYLasEntradasVencidasHaceMasDe30Dias()
    {
        await using var h = await CoreHarness.Start();
        var now = h.Clock.Now;
        var ip = IPAddress.Parse("203.0.113.7");
        await h.Use<SecuritySettingsStore, bool>(async s =>
        {
            await s.Save(Defaults with { KeepLoginDays = 30 }, 0);
            return true;
        });

        Task<long> BlockAt(DateTimeOffset at, string value, TimeSpan? lasts)
        {
            h.Clock.Now = at;
            return h.Use<AccessGuard, long>(g => g.Block(BlockKind.Ip, value, "prueba", lasts is { } l ? at + l : null, BlockSource.Staff));
        }

        Task<LoginEvent> LoginAt(DateTimeOffset at)
        {
            h.Clock.Now = at;
            return h.Use<AccessGuard, LoginEvent>(g => g.Record(LoginAudience.Portal, null, ip, null, LoginResult.UnknownUser, LoginMethod.Password));
        }

        await BlockAt(now.AddDays(-40), "198.51.100.1", TimeSpan.FromDays(1));
        var expiredRecently = await BlockAt(now.AddDays(-11), "198.51.100.2", TimeSpan.FromDays(1));
        var permanent = await BlockAt(now.AddDays(-400), "198.51.100.3", null);
        await LoginAt(now.AddDays(-31));
        var recent = await LoginAt(now.AddDays(-29));
        h.Clock.Now = now;

        // Vencida no cuenta, aunque todavía no se haya borrado.
        Assert.IsFalse(await h.Use<AccessGuard, bool>(g => g.IsBlocked(IPAddress.Parse("198.51.100.2"))));
        Assert.IsTrue(await h.Use<AccessGuard, bool>(g => g.IsBlocked(IPAddress.Parse("198.51.100.3"))));

        await h.Use<JobQueue, bool>(async q =>
        {
            await q.Enqueue(RetentionHandler.JobKind, new { });
            return true;
        });
        await h.RunJobs();

        CollectionAssert.AreEquivalent(new[] { expiredRecently, permanent }, await h.Query(db => db.Set<BlockedEntry>().Select(e => e.Id).ToArrayAsync()));
        CollectionAssert.AreEqual(new[] { recent.Id }, await h.Query(db => db.Set<LoginEvent>().Select(e => e.Id).ToArrayAsync()));
    }

    [TestMethod]
    public async Task LimitePorIp_EnIpv6CuentaLaRed64_YNuncaBloqueaUnaRedDeConfianza()
    {
        await using var h = await CoreHarness.Start();
        await h.Use<SecuritySettingsStore, bool>(async s =>
        {
            await s.Save(Defaults with { IpAttempts = 5, TrustedNetworks = ["198.51.100.0/24"] }, 0);
            return true;
        });

        Task Fail(string ip) => h.Use<AccessGuard, LoginEvent>(g => g.Record(LoginAudience.Portal, null, IPAddress.Parse(ip), null, LoginResult.UnknownUser, LoginMethod.Password));
        Task<bool> Blocked(string ip) => h.Use<AccessGuard, bool>(g => g.IsBlocked(IPAddress.Parse(ip)));

        // Cada falla desde otra dirección de la misma /64: por dirección no juntaría nunca el tope.
        for (var i = 1; i <= 5; i++)
        {
            await Fail($"2001:db8:1:2::{i}");
        }

        Assert.IsTrue(await Blocked("2001:db8:1:2:ffff::1"));
        Assert.IsFalse(await Blocked("2001:db8:1:3::1"));
        Assert.AreEqual("2001:db8:1:2::/64", await h.Query(db => db.Set<BlockedEntry>().Select(e => e.Value).SingleAsync()));

        for (var i = 0; i < 10; i++)
        {
            await Fail("198.51.100.7");
        }

        Assert.IsFalse(await Blocked("198.51.100.7"), "Una red de confianza no se bloquea por intentos.");
        Assert.IsTrue(Defaults with { TrustedNetworks = ["198.51.100.0/24"] } is var trusted && trusted.Trusted(IPAddress.Parse("::ffff:198.51.100.7")));
        CollectionAssert.AreEqual(new[] { "SecuritySettings.TrustedNetworks" }, (Defaults with { TrustedNetworks = ["no-es-red"] }).Problems().ToArray());
    }

    [TestMethod]
    public void ContrasenaGenerada_CumpleLaPoliticaMasEstricta()
    {
        var strict = Defaults with { RequireUpper = true, RequireLower = true, RequireDigit = true, RequireSymbol = true, MinLength = 30, StaffMinLength = 40 };
        for (var i = 0; i < 200; i++)
        {
            var password = PasswordRules.Generate(strict, staff: true);
            Assert.HasCount(40, password);
            Assert.IsEmpty(PasswordRules.Problems(strict, password, "staff@example.com", "Ejemplo", staff: true), password);
        }

        Assert.HasCount(20, PasswordRules.Generate(Defaults, staff: true));
    }

    [TestMethod]
    public void Listas_UnTipoQueNoExisteSeRechaza() =>
        Assert.ThrowsExactly<ValidationException>(() => AccessGuard.Normalize((BlockKind)99, "example.com"));

    [TestMethod]
    [DataRow("una frase larga y rara", true)]
    [DataRow("corta", false)]
    [DataRow("argentina2026", false)]
    [DataRow("Password!123", false)]
    [DataRow("1234567890", false)]
    [DataRow("aaaaaaaaaaaa", false)]
    [DataRow("abababababab", false)]
    [DataRow("qwertyuiop", false)]
    [DataRow("84920175638291", true)]
    [DataRow("abcdefghijkl", false)]
    public void Contrasenas_LargoYComunes(string password, bool ok) =>
        Assert.AreEqual(ok, PasswordRules.Problems(Defaults, password, null, null, staff: false).Count == 0, password);

    [TestMethod]
    public void Contrasenas_NoContienenElCorreoNiLaMarca()
    {
        Assert.Contains("PasswordCommon", PasswordRules.Problems(Defaults, "soy-juanperez-y-listo", "JuanPerez@example.com", null, staff: false));
        Assert.Contains("PasswordCommon", PasswordRules.Problems(Defaults, "mi clave de Ejemplo 2026", null, "Ejemplo", staff: false));
        Assert.IsEmpty(PasswordRules.Problems(Defaults, "mi clave de Ejemplo 2026", null, null, staff: false));
        Assert.IsEmpty(PasswordRules.Problems(Defaults with { RejectCommon = false }, "argentina2026", null, null, staff: false));
    }

    [TestMethod]
    public void Contrasenas_ElStaffTieneSuPropioMinimoYLaComposicionEsOpcional()
    {
        Assert.IsEmpty(PasswordRules.Problems(Defaults, "frase rara", null, null, staff: false));
        Assert.Contains("PasswordTooShort", PasswordRules.Problems(Defaults, "frase rara", null, null, staff: true));

        var strict = Defaults with { RequireUpper = true, RequireLower = true, RequireDigit = true, RequireSymbol = true };
        CollectionAssert.AreEquivalent(
            new[] { "PasswordRequiresUpper", "PasswordRequiresDigit", "PasswordRequiresNonAlphanumeric" },
            PasswordRules.Problems(strict, "solominusculaslargas", null, null, staff: false).ToArray());
        Assert.IsEmpty(PasswordRules.Problems(strict, "Una Frase Rara #42", null, null, staff: false));
        Assert.Contains("PasswordTooLong", PasswordRules.Problems(Defaults, new string('x', 120) + "abcdefghij", null, null, staff: false));
    }

    [TestMethod]
    public void Redes_SeNormalizanYElLugarEsLaRed24O48()
    {
        Assert.AreEqual("203.0.113.0/24", Networks.Parse("203.0.113.77/24")!.Value.ToString());
        Assert.AreEqual("203.0.113.7/32", Networks.Parse(" ::ffff:203.0.113.7 ")!.Value.ToString());
        Assert.AreEqual("2001:db8::/32", Networks.Parse("2001:db8:1:2::5/32")!.Value.ToString());
        Assert.IsNull(Networks.Parse("16.0.0.0/4"), "Una red más grande que /8 no se acepta.");
        Assert.IsNull(Networks.Parse("no-es-ip"));
        Assert.IsNull(Networks.Parse("203.0.113.7/33"));

        Assert.AreEqual("203.0.113.0/24", Networks.Location(IPAddress.Parse("203.0.113.200")).ToString());
        Assert.AreEqual("2001:db8:1::/48", Networks.Location(IPAddress.Parse("2001:db8:1:2:3::9")).ToString());
        Assert.AreEqual("203.0.113.0/24", Networks.Location(IPAddress.Parse("::ffff:203.0.113.200")).ToString());
    }

    [TestMethod]
    public void Listas_ValidanYNormalizanElValor()
    {
        Assert.AreEqual("alguien@example.com", AccessGuard.Normalize(BlockKind.Email, " Alguien@Example.COM ").Value);
        Assert.AreEqual("example.com", AccessGuard.Normalize(BlockKind.Domain, "@Example.com").Value);
        Assert.AreEqual("203.0.113.0/24", AccessGuard.Normalize(BlockKind.Ip, "203.0.113.9/24").Value);
        Assert.ThrowsExactly<ValidationException>(() => AccessGuard.Normalize(BlockKind.Email, "sin-arroba.example.com"));
        Assert.ThrowsExactly<ValidationException>(() => AccessGuard.Normalize(BlockKind.Email, "a@b@example.com"));
        Assert.ThrowsExactly<ValidationException>(() => AccessGuard.Normalize(BlockKind.Domain, "localhost"));
        Assert.ThrowsExactly<ValidationException>(() => AccessGuard.Normalize(BlockKind.Ip, "999.1.1.1"));
    }

    [TestMethod]
    public void Navegadores_SeDescribenCorto()
    {
        Assert.AreEqual("Chrome / Windows", Devices.Describe("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36"));
        Assert.AreEqual("Edge / Windows", Devices.Describe("Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/130.0 Safari/537.36 Edg/130.0"));
        Assert.AreEqual("Safari / iOS", Devices.Describe("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1"));
        Assert.AreEqual("Firefox / Linux", Devices.Describe("Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0"));
        Assert.AreEqual("?", Devices.Describe(null));
    }

    [TestMethod]
    public void Ajustes_DeFabricaSonValidos_YElFraudeNecesitaInstancia()
    {
        Assert.IsEmpty(Defaults.Problems());
        CollectionAssert.AreEqual(new[] { "SecuritySettings.FraudInstanceId" }, (Defaults with { FraudEnabled = true }).Problems().ToArray());
        CollectionAssert.AreEqual(new[] { "SecuritySettings.StaffMinLength" }, (Defaults with { MinLength = 14, StaffMinLength = 12 }).Problems().ToArray());
        CollectionAssert.AreEqual(new[] { "SecuritySettings.FraudReviewScore" }, (Defaults with { FraudReviewScore = 50.555m }).Problems().ToArray());
    }

    [TestMethod]
    public void ContextoDeFraude_SoloSaleDeUnClienteConUsuario()
    {
        var ip = IPAddress.Parse("203.0.113.7");
        Assert.AreEqual(new FraudContext(ip, null, 12), FraudContext.FromActor(new App.Platform.Actor(App.Platform.ActorKind.Customer, "user:12", ip)));
        Assert.IsNull(FraudContext.FromActor(new App.Platform.Actor(App.Platform.ActorKind.Customer, "public", ip)));
        Assert.IsNull(FraudContext.FromActor(new App.Platform.Actor(App.Platform.ActorKind.Staff, "staff:1", ip)));
        Assert.IsNull(FraudContext.FromActor(new App.Platform.Actor(App.Platform.ActorKind.Api, "apikey:1:bk", ip)));
    }
}
