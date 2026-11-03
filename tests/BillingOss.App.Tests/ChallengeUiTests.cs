using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BillingOss.App.Identity;
using BillingOss.Domain.Orders;
using BillingOss.Web.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace BillingOss.App.Tests;

/// <summary>
/// Desafío anti robots por formulario (diseno/ingreso-clientes.md, sección 7): la prueba de trabajo propia, de un solo
/// uso en cualquier nodo, y Turnstile contra un validador falso. Los botones de baja y de arrepentimiento nunca lo piden.
/// </summary>
[TestClass]
[DoNotParallelize] // Cada prueba levanta su host y su base, como UiTests.
public sealed partial class ChallengeUiTests
{
    private const string Strong = "una frase larga para entrar";

    private static string Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString ?? string.Empty;

    private static async Task<string> Body(HttpResponseMessage response)
    {
        using (response)
        {
            return await response.Content.ReadAsStringAsync();
        }
    }

    /// <summary>Abre la página, resuelve la prueba de trabajo como el navegador y manda el formulario.</summary>
    private static async Task<(HttpResponseMessage Response, Dictionary<string, string> Sent)> SubmitSolved(Client c, string page, string action, Dictionary<string, string> fields)
    {
        var html = await c.Html(page);
        var work = Work().Match(html);
        Assert.IsTrue(work.Success, "La página tiene el desafío.");
        var token = WebUtility.HtmlDecode(work.Groups[1].Value);
        var bits = int.Parse(work.Groups[2].Value, CultureInfo.InvariantCulture);
        var nonce = 0;
        while (ChallengeGuard.LeadingZeroBits(token, nonce.ToString(CultureInfo.InvariantCulture)) < bits)
        {
            nonce++;
        }

        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(Antiforgery().Match(html).Groups[1].Value);
        fields[ChallengeGuard.TokenField] = token;
        fields[ChallengeGuard.NonceField] = nonce.ToString(CultureInfo.InvariantCulture);
        using var content = new FormUrlEncodedContent(fields);
        return (await c.Http.PostAsync(new Uri(action, UriKind.Relative), content), fields);
    }

    [TestMethod]
    public async Task PruebaDeTrabajo_EnElIngresoElRegistroYRecuperar_DeUnSoloUso_YNuncaEnLaBaja()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        await h.Settings(s => s with { ChallengeProvider = SecuritySettings.ChallengeWork, ChallengeForms = ["login", "register", "forgot"], ChallengeBits = 8 });
        using var c = h.NewClient();

        var login = await c.Html("/ingresar");
        StringAssert.Contains(login, "data-bits=\"8\"");
        StringAssert.Contains(login, "/js/challenge.js");
        StringAssert.Contains(await Body(await c.Login("titular@example.com")), "No pudimos comprobar que no sos un robot", "Sin resolverla no pasa.");

        var (entered, sent) = await SubmitSolved(c, "/ingresar", "/ingresar", new() { ["Email"] = "titular@example.com", ["Password"] = UiHarness.Password });
        Assert.AreEqual("/", Location(entered));
        entered.Dispose();

        // La misma solución no sirve dos veces, en ningún nodo.
        using (var other = h.NewClient())
        {
            var page = await other.Html("/ingresar");
            sent["__RequestVerificationToken"] = WebUtility.HtmlDecode(Antiforgery().Match(page).Groups[1].Value);
            using var content = new FormUrlEncodedContent(sent);
            StringAssert.Contains(await Body(await other.Http.PostAsync(new Uri("/ingresar", UriKind.Relative), content)), "No pudimos comprobar");
        }

        Assert.AreEqual(1, await h.Query(db => db.Set<ChallengeUse>().CountAsync()));

        using var newcomer = h.NewClient();
        StringAssert.Contains(await Body(await newcomer.Submit("/registro", "/registro", new() { ["Name"] = "Ana", ["Email"] = "ana@example.com", ["Currency"] = "ARS", ["Password"] = Strong, ["Confirm"] = Strong })), "No pudimos comprobar");
        var (registered, _) = await SubmitSolved(newcomer, "/registro", "/registro", new() { ["Name"] = "Ana", ["Email"] = "ana@example.com", ["Currency"] = "ARS", ["Password"] = Strong, ["Confirm"] = Strong });
        Assert.AreEqual(HttpStatusCode.Redirect, registered.StatusCode);
        registered.Dispose();

        using var forgetful = h.NewClient();
        StringAssert.Contains(await Body(await forgetful.Submit("/recuperar", "/recuperar", new() { ["Email"] = "titular@example.com" })), "No pudimos comprobar");
        var (recovered, _) = await SubmitSolved(forgetful, "/recuperar", "/recuperar", new() { ["Email"] = "titular@example.com" });
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
        recovered.Dispose();
        Assert.AreEqual(1, h.Mail.Sent.Count(m => m.Kind == "reset"));

        // Disposición 954/2025: la baja no pasa por ningún desafío, aunque estén todos los formularios.
        await h.Settings(s => s with { ChallengeForms = SecuritySettings.ChallengeFormNames });
        using var leaving = h.NewClient();
        (await leaving.Submit("/baja", "/baja?handler=Start", new() { ["Email"] = "cliente@example.com" })).Dispose();
        Assert.AreEqual(1, await h.Query(db => db.Set<CancellationRequest>().CountAsync()));
    }

    [TestMethod]
    public async Task Turnstile_ValidaConSuServidor_ConLaClaveCifrada()
    {
        var verifier = new FakeTurnstile();
        await using var h = await UiHarness.Start(services: s => s.PostConfigure<HttpClientFactoryOptions>(ChallengeGuard.HttpClientName, o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = verifier)));
        await h.PortalUser("titular@example.com", h.AccountA);
        await h.Staff("seguridad@example.com", StaffPermissions.SecurityManage);

        using (var admin = h.NewClient())
        {
            (await admin.Submit("/admin/ingresar", "/admin/ingresar", new() { ["Email"] = "seguridad@example.com", ["Password"] = UiHarness.Password })).Dispose();
            var key = TotpKey().Match(await admin.Html("/admin/ingresar/codigo")).Groups[1].Value.Replace(" ", string.Empty, StringComparison.Ordinal);
            (await admin.Submit("/admin/ingresar/codigo", "/admin/ingresar/codigo", new() { ["Code"] = UiHarness.Totp(key) })).Dispose();
            var settings = new Dictionary<string, string>
            {
                ["Version"] = "0", ["Settings.MinLength"] = "10", ["Settings.StaffMinLength"] = "12", ["Settings.AccountAttempts"] = "5", ["Settings.AccountMinutes"] = "15",
                ["Settings.IpAttempts"] = "20", ["Settings.IpWindowMinutes"] = "15", ["Settings.IpMinutes"] = "30", ["Settings.KeepLoginDays"] = "365", ["ReviewScore"] = "50",
                ["Settings.RegistrationEnabled"] = "true", ["Settings.RegistrationIpLimit"] = "5", ["Settings.RegistrationIpHours"] = "24", ["Settings.RegistrationCurrency"] = "ARS",
                ["Settings.ChallengeProvider"] = SecuritySettings.ChallengeTurnstile, ["Settings.ChallengeForms"] = "login", ["Settings.ChallengeBits"] = "16",
                ["Settings.ChallengeSiteKey"] = "clave-publica",
            };
            StringAssert.Contains(await Body(await admin.Submit("/admin/seguridad/ajustes", "/admin/seguridad/ajustes", new(settings))), "Turnstile necesita la clave pública y la secreta");
            settings["ChallengeSecretText"] = FakeTurnstile.Secret;
            var saved = await Body(await admin.Submit("/admin/seguridad/ajustes", "/admin/seguridad/ajustes", new(settings)));
            StringAssert.Contains(saved, "quedaron guardados");
            Assert.IsFalse(saved.Contains(FakeTurnstile.Secret, StringComparison.Ordinal), "La clave secreta no vuelve a la pantalla.");
            var stored = await h.Use<SecuritySettingsStore, SecuritySettings>(s => s.Get());
            Assert.IsFalse(Encoding.UTF8.GetString(Convert.FromBase64String(stored.ChallengeSecret!)).Contains(FakeTurnstile.Secret, StringComparison.Ordinal), "Cifrada por campo.");

            // Guardar otra vez sin escribirla deja la que estaba.
            settings["Version"] = "1";
            settings.Remove("ChallengeSecretText");
            StringAssert.Contains(await Body(await admin.Submit("/admin/seguridad/ajustes", "/admin/seguridad/ajustes", new(settings))), "quedaron guardados");
            Assert.AreEqual(stored.ChallengeSecret, (await h.Use<SecuritySettingsStore, SecuritySettings>(s => s.Get())).ChallengeSecret);
        }

        using var c = h.NewClient();
        using (var page = await c.Get("/ingresar"))
        {
            StringAssert.Contains(await page.Content.ReadAsStringAsync(), "data-sitekey=\"clave-publica\"");
            StringAssert.Contains(page.Headers.GetValues("Content-Security-Policy").Single(), "script-src 'self' https://challenges.cloudflare.com");
        }

        StringAssert.Contains(await Body(await c.Submit("/ingresar", "/ingresar", new() { ["Email"] = "titular@example.com", ["Password"] = UiHarness.Password, [ChallengeGuard.TurnstileField] = "token-malo" })), "No pudimos comprobar");
        Assert.AreEqual("/", Location(await c.Submit("/ingresar", "/ingresar", new() { ["Email"] = "titular@example.com", ["Password"] = UiHarness.Password, [ChallengeGuard.TurnstileField] = FakeTurnstile.Good })));
        Assert.AreEqual(2, verifier.Calls);
        StringAssert.Contains((await c.Get("/registro")).Headers.GetValues("Content-Security-Policy").Single(), "default-src 'self'; img-src", "Solo cambia la política de las páginas que lo piden.");
    }

    /// <summary>El validador de Turnstile, de mentira: acepta un solo token con la clave secreta correcta.</summary>
    private sealed class FakeTurnstile : HttpMessageHandler
    {
        public const string Secret = "secreto-de-turnstile";

        public const string Good = "token-bueno";

        public int Calls { get; private set; }

        protected override void Dispose(bool disposing)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.AreEqual(ChallengeGuard.TurnstileVerify, request.RequestUri);
            var form = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            var ok = form["secret"] == Secret && form["response"] == Good;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ok ? """{"success":true}""" : """{"success":false,"error-codes":["invalid-input-response"]}""", Encoding.UTF8, "application/json") };
        }
    }

    [GeneratedRegex("name=\"Challenge\" value=\"([^\"]+)\" data-bits=\"([0-9]+)\"")]
    private static partial Regex Work();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex Antiforgery();

    [GeneratedRegex("<code class=\"key\"[^>]*>([A-Z2-7 ]+)</code>")]
    private static partial Regex TotpKey();
}
