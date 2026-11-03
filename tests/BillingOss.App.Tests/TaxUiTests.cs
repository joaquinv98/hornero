using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.Domain.Fiscal;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Las pantallas de Impuestos, la ficha fiscal de la cuenta y el asistente de ARCA, de punta a punta con un fisco falso.</summary>
[TestClass]
[DoNotParallelize] // Cada prueba levanta su host y su base: en paralelo agotan las conexiones del PostgreSQL compartido.
public sealed partial class TaxUiTests
{
    private const string Cuit = "30712345671";

    private static string Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString ?? string.Empty;

    /// <summary>Sesión del staff con el TOTP enrolado, y su clave para las acciones que piden el segundo factor.</summary>
    private static async Task<(Client Client, string Key)> Staff(UiHarness h, string email, params string[] permissions)
    {
        await h.Staff(email, permissions);
        var c = h.NewClient();
        (await c.Submit("/admin/ingresar", "/admin/ingresar", new() { ["Email"] = email, ["Password"] = UiHarness.Password })).Dispose();
        var html = await c.Html("/admin/ingresar/codigo");
        var key = KeyPattern().Match(html).Groups[1].Value.Replace(" ", string.Empty, StringComparison.Ordinal);
        (await c.Submit("/admin/ingresar/codigo", "/admin/ingresar/codigo", new() { ["Code"] = UiHarness.Totp(key) })).Dispose();
        return (c, key);
    }

    private static Task<HttpResponseMessage> Preset(Client c) =>
        c.Submit("/admin/impuestos/empezar", "/admin/impuestos/empezar", new() { ["IssuerCountry"] = "AR", ["IssuerVatCondition"] = "1", ["SellsAbroad"] = "true", ["PerceivesRg2126"] = "true" });

    [TestMethod]
    public async Task Impuestos_ElPresetDejaElSimuladorComoLaTablaDelDiseño_YSinPermisoNoSeEntra()
    {
        await using var h = await UiHarness.Start();
        var (c, _) = await Staff(h, "fiscal@example.com", StaffPermissions.AccountsRead, StaffPermissions.TaxesEdit);
        using var _ = c;

        var before = await c.Html("/admin/impuestos");
        StringAssert.Contains(before, "Así cobrás hoy");
        StringAssert.Contains(before, "href=\"/admin/impuestos\"");

        using (var preset = await Preset(c))
        {
            Assert.AreEqual("/admin/impuestos?listo=preset", Location(preset));
        }

        var page = await c.Html("/admin/impuestos?listo=preset");
        StringAssert.Contains(page, "Las reglas quedaron cargadas");
        StringAssert.Contains(page, "9.527,56 ARS", "27 % de telecomunicaciones a un responsable inscripto.");
        StringAssert.Contains(page, "13.370,50 ARS", "La percepción de la RG 2126 al no categorizado.");
        StringAssert.Contains(page, "Percepción de IVA RG 2126 10,5 %");
        StringAssert.Contains(page, "exportación de servicios");
        Assert.IsFalse(page.Contains("No se puede calcular", StringComparison.Ordinal));

        var custom = await c.Html("/admin/impuestos?pais=AR&provincia=AR-B&condicion=5&categoria=digital&precio=12100&moneda=ARS&modo=TaxIncluded");
        StringAssert.Contains(custom, "Tu caso");
        StringAssert.Contains(custom, "2.100,00 ARS");
        StringAssert.Contains(custom, "Ley 27.743");

        // Sin el permiso, ni la sección ni el enlace del menú.
        var (reader, _) = await Staff(h, "lector@example.com", StaffPermissions.AccountsRead);
        using var __ = reader;
        using var denied = await reader.Get("/admin/impuestos");
        Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
        StringAssert.Contains(Location(denied), "/estado/403");
        Assert.IsFalse((await reader.Html("/admin")).Contains("href=\"/admin/impuestos\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Reglas_SeAgreganYSeCierran_IngresosBrutosSeConfigura_YLaMuestraDeArgentinaNoSeCambia()
    {
        await using var h = await UiHarness.Start();
        var (c, _) = await Staff(h, "fiscal@example.com", StaffPermissions.TaxesEdit);
        using var _ = c;
        (await Preset(c)).Dispose();

        using (var added = await c.Submit("/admin/impuestos/reglas", "/admin/impuestos/reglas?handler=AddRule", new() { ["taxCode"] = "iva", ["country"] = "uy", ["outcome"] = "Taxed", ["ratePercent"] = "22", ["validFrom"] = "2026-01-01" }))
        {
            Assert.AreEqual("/admin/impuestos/reglas?listo=regla", Location(added));
        }

        var rules = await c.Html("/admin/impuestos/reglas?country=UY");
        StringAssert.Contains(rules, "22 %");
        StringAssert.Contains(rules, "Cerrar hoy");
        var uruguay = await h.Query(db => db.TaxRules.SingleAsync(r => r.Country == "UY"));
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "taxes.rule.add" && a.Entity == $"tax_rule:{uruguay.Id}")));

        using (var regime = await c.Submit("/admin/impuestos/reglas", "/admin/impuestos/reglas?handler=Regime", new() { ["jurisdiction"] = "ar-b", ["regime"] = "ARBA régimen general", ["agentNumber"] = "30712345678", ["ratePercent"] = "3", ["taxBase"] = "Net", ["validFrom"] = "2026-01-01", ["excluded"] = "1" }))
        {
            Assert.AreEqual("/admin/impuestos/reglas?listo=iibb", Location(regime));
        }

        StringAssert.Contains(await c.Html("/admin/impuestos/reglas"), "ARBA régimen general");
        var simulated = await c.Html("/admin/impuestos?pais=AR&provincia=AR-B&condicion=5&categoria=digital&precio=12100&moneda=ARS&modo=TaxIncluded");
        StringAssert.Contains(simulated, "Percepción de ingresos brutos AR-B 3 %");
        StringAssert.Contains(simulated, "12.400,00 ARS");

        // Una alícuota mal escrita no pasa, y una regla cerrada hoy deja de verse entre las vigentes.
        using (var bad = await c.Submit("/admin/impuestos/reglas", "/admin/impuestos/reglas?handler=AddRule", new() { ["taxCode"] = "iva", ["country"] = "br", ["outcome"] = "Taxed", ["ratePercent"] = "veinte", ["validFrom"] = "2026-01-01" }))
        {
            Assert.AreEqual(HttpStatusCode.OK, bad.StatusCode);
            StringAssert.Contains(await bad.Content.ReadAsStringAsync(), "Escribí la alícuota como número");
        }

        (await c.Submit("/admin/impuestos/reglas", "/admin/impuestos/reglas?handler=Close", new() { ["id"] = uruguay.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) })).Dispose();
        Assert.IsFalse((await c.Html("/admin/impuestos/reglas?country=UY")).Contains("22 %", StringComparison.Ordinal));
        StringAssert.Contains(await c.Html("/admin/impuestos/reglas?country=UY&closed=true"), "22 %");

        // Precios y muestra: el modo por defecto cambia, lo que fija la ley no.
        (await c.Submit("/admin/impuestos/precios", "/admin/impuestos/precios?handler=PriceMode", new() { ["currency"] = "usd", ["mode"] = "TaxIncluded" })).Dispose();
        var prices = await c.Html("/admin/impuestos/precios");
        StringAssert.Contains(prices, "PRECIO SIN IMPUESTOS NACIONALES");
        Assert.AreEqual(BillingOss.Domain.Billing.PriceMode.TaxIncluded, (await h.Query(db => db.TaxSettings.SingleAsync())).DefaultPriceMode(BillingOss.Domain.Shared.Currency.Usd));
        using var blocked = await c.Submit("/admin/impuestos/precios", "/admin/impuestos/precios?handler=Display", new() { ["catalogDisplay"] = "final", ["businessMayViewNet"] = "true" });
        StringAssert.Contains(await blocked.Content.ReadAsStringAsync(), "Res. 4/2025");
    }

    [TestMethod]
    public async Task Arca_ElAsistenteDePuntaAPunta_ConUnFiscoFalso_GeneraElPedido_CargaElCertificado_PruebaYActiva()
    {
        await using var h = await UiHarness.Start();
        var fisco = new FakeFisco { Taxpayer = new JsonObject { ["tax_id"] = Cuit, ["name"] = "PROVEEDOR DE PRUEBA SA", ["suggested_vat_condition"] = 1, ["taxes"] = new JsonArray(), ["notes"] = new JsonArray() } };
        await using var module = await TestModule.Start(fisco.Definition(), work => h.Use<ModuleRegistry, long>(work), instanceConfig: null);
        var (c, key) = await Staff(h, "fiscal@example.com", StaffPermissions.FiscalEdit);
        using var _ = c;
        using var ca = new FakeCa(DateTimeOffset.UtcNow);

        StringAssert.Contains(await c.Html("/admin/fiscal"), "Todavía no hay un emisor");
        var step1 = await c.Html("/admin/fiscal/arca");
        StringAssert.Contains(step1, "Crear una instancia nueva en");
        StringAssert.Contains(step1, "aria-current=\"step\"><a href=\"/admin/fiscal/arca/1\"");

        using (var start = await c.Submit("/admin/fiscal/arca/1", "/admin/fiscal/arca/1?handler=Start", new() { ["environment"] = "homologation", ["exports"] = "true", ["usesRegistry"] = "true", ["instance"] = $"n:{module.InstallationId}" }))
        {
            Assert.AreEqual("/admin/fiscal/arca/2?listo=ok", Location(start));
        }

        using (var issuer = await c.Submit("/admin/fiscal/arca/2", "/admin/fiscal/arca/2?handler=Issuer", new() { ["taxId"] = "30-71234567-1", ["legalName"] = "Proveedor de prueba SA", ["vatCondition"] = "1", ["address"] = "Calle 1" }))
        {
            Assert.AreEqual("/admin/fiscal/arca/3?listo=ok", Location(issuer));
        }

        var instance = await h.Query(db => db.ModuleInstances.SingleAsync());
        Assert.AreEqual(Cuit, JsonNode.Parse(instance.Config)!["cuit"]!.GetValue<string>(), "La instancia nueva lleva la CUIT del emisor.");

        using (var request = await c.Submit("/admin/fiscal/arca/3", "/admin/fiscal/arca/3?handler=Credential", new() { ["alias"] = "billingoss", ["subjectTaxId"] = Cuit, ["organization"] = "Proveedor de prueba SA" }))
        {
            StringAssert.StartsWith(Location(request), "/admin/fiscal/arca/3?credencial=");
        }

        var credential = await h.Query(db => db.FiscalCredentials.SingleAsync());
        var step3 = await c.Html($"/admin/fiscal/arca/3?credencial={credential.Id}&listo=pedido");
        StringAssert.Contains(step3, "BEGIN CERTIFICATE REQUEST");
        StringAssert.Contains(step3, "no sale de acá");
        Assert.IsFalse(step3.Contains("PRIVATE KEY", StringComparison.Ordinal), "La clave privada nunca vuelve al navegador.");
        using (var csr = await c.Get($"/admin/fiscal/arca/3?handler=Csr&id={credential.Id}"))
        {
            Assert.AreEqual("application/pkcs10", csr.Content.Headers.ContentType!.MediaType);
            StringAssert.StartsWith(await csr.Content.ReadAsStringAsync(), "-----BEGIN CERTIFICATE REQUEST-----");
        }

        var guide = await c.Html("/admin/fiscal/arca/4");
        StringAssert.Contains(guide, "WSASS, Nuevo Certificado");
        StringAssert.Contains(guide, "<code>wsfe</code>");
        StringAssert.Contains(guide, "<code>wsfex</code>");
        StringAssert.Contains(guide, "<code>ws_sr_constancia_inscripcion</code>");
        StringAssert.Contains(guide, "3 días hábiles");
        (await c.Submit("/admin/fiscal/arca/4", "/admin/fiscal/arca/4?handler=GuideDone", [])).Dispose();

        // El certificado de otro pedido no pasa; el bueno queda validado y pide el segundo factor para activar.
        var (other, _) = ca.IssueOther($"CN=billingoss, SERIALNUMBER=CUIT {Cuit}, O=Proveedor, C=AR", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        using (var mismatch = await c.Submit("/admin/fiscal/arca/5", "/admin/fiscal/arca/5?handler=Certificate", new() { ["credentialId"] = credential.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["pem"] = other }))
        {
            StringAssert.Contains(await mismatch.Content.ReadAsStringAsync(), "es de otro pedido");
        }

        var pem = ca.Issue(credential.Csr, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        using (var loaded = await c.Submit("/admin/fiscal/arca/5", "/admin/fiscal/arca/5?handler=Certificate", new() { ["credentialId"] = credential.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["pem"] = pem }))
        {
            var html = await loaded.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "AC de prueba");
            StringAssert.Contains(html, "Activar la credencial");
        }

        using (var wrongCode = await c.Submit("/admin/fiscal/arca/5", "/admin/fiscal/arca/5?handler=Activate", new() { ["credentialId"] = credential.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["code"] = UiHarness.Totp(key) == "000000" ? "111111" : "000000" }))
        {
            Assert.AreEqual(HttpStatusCode.OK, wrongCode.StatusCode);
            Assert.AreEqual(CredentialStatus.Ready, (await h.Query(db => db.FiscalCredentials.SingleAsync())).Status);
        }

        using (var activated = await c.Submit("/admin/fiscal/arca/5", "/admin/fiscal/arca/5?handler=Activate", new() { ["credentialId"] = credential.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["code"] = UiHarness.Totp(key) }))
        {
            Assert.AreEqual("/admin/fiscal/arca/6?listo=activada", Location(activated));
        }

        var active = await h.Query(db => db.FiscalCredentials.SingleAsync());
        Assert.AreEqual(CredentialStatus.Active, active.Status);
        Assert.IsNull(active.PrivateKey);

        // La prueba falla por el punto de venta sin cargar, se elige de la lista de ARCA y pasa.
        (await c.Submit("/admin/fiscal/arca/6", "/admin/fiscal/arca/6?handler=Test", [])).Dispose();
        var failed = await c.Html("/admin/fiscal/arca/6?listo=prueba");
        StringAssert.Contains(failed, "Conectado a homologation");
        StringAssert.Contains(failed, "no está entre los que informa ARCA (3)");
        StringAssert.Contains(failed, "<option value=\"3\"");
        Assert.AreEqual(pem, fisco.CertificatesSeen.Last(), "La prueba usa el certificado activado.");
        (await c.Submit("/admin/fiscal/arca/6", "/admin/fiscal/arca/6?handler=Points", new() { ["pointOfSale"] = "3", ["exportPointOfSale"] = "12" })).Dispose();
        (await c.Submit("/admin/fiscal/arca/6", "/admin/fiscal/arca/6?handler=Test", [])).Dispose();
        var passed = await c.Html("/admin/fiscal/arca/6?listo=prueba");
        StringAssert.Contains(passed, "Todo bien");
        StringAssert.Contains(passed, "PROVEEDOR DE PRUEBA SA");

        var step7 = await c.Html("/admin/fiscal/arca/7");
        StringAssert.Contains(step7, "Fiscal__ProductionEnabled=true");
        StringAssert.Contains(step7, "Arca__Production=on");
        using (var finished = await c.Submit("/admin/fiscal/arca/7", "/admin/fiscal/arca/7?handler=Finish", []))
        {
            var html = await finished.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "quedó activo");
            StringAssert.Contains(html, "3-6 desde 0");
            StringAssert.Contains(html, "12-19 desde 0");
        }

        var card = await c.Html("/admin/fiscal");
        StringAssert.Contains(card, "Listo");
        StringAssert.Contains(card, "billingoss");
        StringAssert.Contains(card, "habilitado en ARCA");
        Assert.AreEqual(7, (await h.Query(db => db.IssuerProfiles.SingleAsync())).SetupStep);
        Assert.AreEqual(9, await h.Query(db => db.FiscalSeries.CountAsync()));
        CollectionAssert.IsSubsetOf(new[] { "fiscal.credential.create", "fiscal.credential.certificate", "fiscal.credential.activate", "fiscal.test", "fiscal.issuer.finish" }, await h.Query(db => db.Set<AuditEvent>().Select(a => a.Action).Distinct().ToListAsync()));

        // De homologación a producción: otro emisor con los mismos datos y el asistente en producción desde el paso 1.
        StringAssert.Contains(card, "Pasar a producción");
        using (var production = await c.Submit("/admin/fiscal", "/admin/fiscal?handler=NewEnvironment", new() { ["id"] = (await h.Query(db => db.IssuerProfiles.SingleAsync())).Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }))
        {
            Assert.AreEqual("/admin/fiscal/arca/1?ambiente=production", Location(production));
        }

        var productionStep1 = await c.Html("/admin/fiscal/arca/1?ambiente=production");
        StringAssert.Contains(productionStep1, "value=\"production\" checked");
        StringAssert.Contains(productionStep1, "clave fiscal de nivel 3");
        StringAssert.Contains(await c.Html("/admin/fiscal"), "emisor en preparación");
        Assert.AreEqual(2, await h.Query(db => db.IssuerProfiles.CountAsync()));
        Assert.AreEqual(1, await h.Query(db => db.IssuerProfiles.CountAsync(i => i.Active)));
    }

    [TestMethod]
    public async Task Cuenta_DatosFiscales_Condicion_ReglaDeCuentaConConstancia_YEmpresaGrande()
    {
        await using var h = await UiHarness.Start();
        var (c, _) = await Staff(h, "fiscal@example.com", StaffPermissions.AccountsRead, StaffPermissions.TaxesEdit);
        using var _ = c;
        var page = $"/admin/cuentas/{h.AccountA}";

        var before = await c.Html(page);
        StringAssert.Contains(before, "Datos fiscales");
        StringAssert.Contains(before, "Consumidor Final");

        using (var changed = await c.Submit(page, page + "?handler=Tax", new() { ["documentType"] = "Cuit", ["documentNumber"] = "30-71234567-1", ["legalName"] = "Cliente SA", ["vatCondition"] = "1", ["country"] = "ar", ["province"] = "ar-c" }))
        {
            Assert.AreEqual(page + "?listo=ficha#fiscal", Location(changed));
        }

        var after = await c.Html(page);
        StringAssert.Contains(after, "IVA Responsable Inscripto");
        StringAssert.Contains(after, "AR AR-C");
        Assert.AreEqual(2, await h.Query(db => db.TaxIdentities.CountAsync(t => t.AccountId == h.AccountA)), "La identidad fiscal es versionada.");

        using (var invalid = await c.Submit(page, page + "?handler=Tax", new() { ["documentType"] = "Cuit", ["documentNumber"] = Cuit, ["legalName"] = "Cliente SA", ["vatCondition"] = "2", ["country"] = "AR" }))
        {
            StringAssert.Contains(await invalid.Content.ReadAsStringAsync(), "no está en la tabla de ARCA");
        }

        // Sin la percepción de la RG 2126 la condición 7 no se ofrece ni se acepta, y la provincia es un código ISO 3166-2 de la lista.
        Assert.IsFalse(after.Contains("<option value=\"7\"", StringComparison.Ordinal), "La 7 no se ofrece sin la percepción.");
        StringAssert.Contains(after, "<option value=\"AR-B\"");
        using (var uncategorized = await c.Submit(page, page + "?handler=Tax", new() { ["documentType"] = "Cuit", ["documentNumber"] = Cuit, ["legalName"] = "Cliente SA", ["vatCondition"] = "7", ["country"] = "AR" }))
        {
            StringAssert.Contains(await uncategorized.Content.ReadAsStringAsync(), "RG 2126");
        }

        using (var mars = await c.Submit(page, page + "?handler=Tax", new() { ["documentType"] = "Cuit", ["documentNumber"] = Cuit, ["legalName"] = "Cliente SA", ["vatCondition"] = "1", ["country"] = "AR", ["province"] = "Marte" }))
        {
            StringAssert.Contains(await mars.Content.ReadAsStringAsync(), "ISO 3166-2");
        }

        using (var rule = await c.Submit(page, page + "?handler=Rule", new() { ["taxCode"] = "iva", ["outcome"] = "Exempt", ["reason"] = "exento_por_ley", ["certificateRef"] = "Constancia de exención 77", ["validFrom"] = "2026-01-01", ["validTo"] = "2027-01-01" }))
        {
            Assert.AreEqual(page + "?listo=regla#fiscal", Location(rule));
        }

        (await c.Submit(page, page + "?handler=LargeCompany", new() { ["largeCompany"] = "true" })).Dispose();
        var withRule = await c.Html(page);
        StringAssert.Contains(withRule, "Constancia de exención 77");
        StringAssert.Contains(withRule, "Quitar la marca de empresa grande");
        Assert.IsTrue((await h.Query(db => db.Accounts.SingleAsync(a => a.Id == h.AccountA))).LargeCompany);
        var saved = await h.Query(db => db.TaxRules.SingleAsync(r => r.AccountId == h.AccountA));
        Assert.AreEqual(("Constancia de exención 77", "exento_por_ley", new DateOnly(2027, 1, 1)), (saved.CertificateRef, saved.Reason, saved.ValidTo));

        // Quien solo lee ve los datos pero no las acciones, y un POST suyo se rechaza.
        var (reader, _) = await Staff(h, "lector@example.com", StaffPermissions.AccountsRead);
        using var __ = reader;
        var readOnly = await reader.Html(page);
        StringAssert.Contains(readOnly, "Constancia de exención 77");
        Assert.IsFalse(readOnly.Contains("handler=Rule", StringComparison.Ordinal));
        using var forbidden = await reader.Submit(page, page + "?handler=LargeCompany", new() { ["largeCompany"] = "false" });
        Assert.AreNotEqual(page + "?listo=grande#fiscal", Location(forbidden));
        Assert.IsTrue((await h.Query(db => db.Accounts.SingleAsync(a => a.Id == h.AccountA))).LargeCompany);
    }

    [TestMethod]
    public async Task Portal_DatosFiscales_ElClienteCompletaSuFicha_SinLaCondicion7_YNoTocaOtraCuenta()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();
        const string page = "/cuenta/datos-fiscales";

        var before = await c.Html(page);
        StringAssert.Contains(before, "Datos fiscales");
        StringAssert.Contains(before, "Consumidor Final");
        StringAssert.Contains(before, "Córdoba");
        Assert.IsFalse(before.Contains("<option value=\"7\"", StringComparison.Ordinal), "La 7 no se ofrece sin la percepción.");
        Assert.IsFalse(before.Contains("Cuenta ajena", StringComparison.Ordinal));

        var own = new Dictionary<string, string> { ["accountId"] = h.AccountA.ToString(System.Globalization.CultureInfo.InvariantCulture), ["documentType"] = "Cuit", ["documentNumber"] = "30-71234567-1", ["legalName"] = "Cliente SA", ["vatCondition"] = "1", ["country"] = "ar", ["province"] = "AR-X" };
        using (var changed = await c.Submit(page, page + "?handler=Tax", own))
        {
            Assert.AreEqual(page + "?listo=ficha", Location(changed));
        }

        var after = await c.Html(page + "?listo=ficha");
        StringAssert.Contains(after, "IVA Responsable Inscripto");
        StringAssert.Contains(after, "AR Córdoba");
        StringAssert.Contains(after, "Completar con ARCA");
        var identity = await h.Query(db => db.TaxIdentities.SingleAsync(t => t.AccountId == h.AccountA && t.ValidTo == null));
        Assert.AreEqual(("30712345671", "AR-X", 1), (identity.DocumentNumber, identity.Province, identity.VatCondition));
        Assert.AreEqual(2, await h.Query(db => db.TaxIdentities.CountAsync(t => t.AccountId == h.AccountA)), "La identidad fiscal es versionada.");
        Assert.IsTrue(await h.Query(db => db.Set<AuditEvent>().AnyAsync(a => a.Action == "portal.tax_identity")));

        using (var uncategorized = await c.Submit(page, page + "?handler=Tax", new(own) { ["vatCondition"] = "7" }))
        {
            StringAssert.Contains(await uncategorized.Content.ReadAsStringAsync(), "RG 2126");
        }

        // La cuenta ajena no existe para esta sesión.
        using var foreign = await c.Submit(page, page + "?handler=Tax", new(own) { ["accountId"] = h.AccountB.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.AreEqual(0, await h.Query(db => db.TaxIdentities.CountAsync(t => t.AccountId == h.AccountB)));
    }

    [GeneratedRegex("<code class=\"key\"[^>]*>([A-Z2-7 ]+)</code>")]
    private static partial Regex KeyPattern();
}
