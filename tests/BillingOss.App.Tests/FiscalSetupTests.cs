using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Fiscal;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Una autoridad certificante de mentira que firma los pedidos del asistente, como hace ARCA.</summary>
internal sealed class FakeCa : IDisposable
{
    private readonly X509Certificate2 ca;

    public FakeCa(DateTimeOffset now)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=AC de prueba, O=ARCA falsa, C=AR", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

        // Vale desde mucho antes: las pruebas emiten certificados ya vencidos.
        ca = request.CreateSelfSigned(now.AddYears(-10), now.AddYears(10));
    }

    /// <summary>Firma el pedido tal cual, con su sujeto y su clave pública.</summary>
    public string Issue(string csrPem, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var request = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, signerSignaturePadding: RSASignaturePadding.Pkcs1);
        using var cert = request.Create(ca, notBefore, notAfter, RandomNumberGenerator.GetBytes(8));
        return cert.ExportCertificatePem();
    }

    /// <summary>Un certificado con la clave pública del pedido pero otro sujeto: la misma clave, otra CUIT o alias.</summary>
    public string IssueWithSubject(string csrPem, string subject, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var original = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, signerSignaturePadding: RSASignaturePadding.Pkcs1);
        var request = new CertificateRequest(new X500DistinguishedName(subject), original.PublicKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.Create(ca, notBefore, notAfter, RandomNumberGenerator.GetBytes(8));
        return cert.ExportCertificatePem();
    }

    /// <summary>Un certificado de otro par de claves, como el que ARCA daría para otro pedido.</summary>
    public (string Certificate, string Key) IssueOther(string subject, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.Create(ca, notBefore, notAfter, RandomNumberGenerator.GetBytes(8));
        return (cert.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    public void Dispose() => ca.Dispose();
}

/// <summary>Alta guiada de ARCA (sección 2 del diseño fiscal), contra un fisco falso y una autoridad certificante falsa.</summary>
[TestClass]
public sealed class FiscalSetupTests
{
    private const string Cuit = "30712345671";

    /// <summary>El núcleo con el fisco falso y un emisor con una instancia de homologación cuyos secretos son los del certificado viejo.</summary>
    private static async Task<(CoreHarness H, TestModule Module, FakeFisco Fisco, long IssuerId)> Start(string environment = "homologation", bool productionEnabled = false)
    {
        var box = TestModule.NewBox();
        var h = await CoreHarness.Start(box, fiscal: o => o.ProductionEnabled = productionEnabled);
        var fisco = new FakeFisco();
        var module = await TestModule.Start(fisco.Definition(), h, box, $$"""{"cuit":"{{Cuit}}","environment":"{{environment}}"}""", """{"certificate_pem":"CERT-VIEJO","private_key_pem":"KEY-VIEJA"}""");
        long issuerId = 0;
        await h.Db(async db =>
        {
            var issuer = new IssuerProfile
            {
                TaxId = Cuit,
                LegalName = "Proveedor de prueba",
                ModuleInstanceId = module.InstanceId,
                PointOfSale = 3,
                TypeMap = FiscalSetupService.DefaultTypeMap(VatConditions.ResponsableInscripto, false, false),
            };
            db.IssuerProfiles.Add(issuer);
            await db.SaveChangesAsync();
            issuerId = issuer.Id;
        });
        return (h, module, fisco, issuerId);
    }

    private static Task<FiscalCredential> Create(CoreHarness h, long issuerId, string environment = "homologation", string alias = "billingoss") =>
        h.Use<FiscalSetupService, FiscalCredential>(s => s.CreateCredential(issuerId, environment, alias, Cuit, "Proveedor de prueba SA"));

    private static Task<IReadOnlyList<CertificateCheck>> Load(CoreHarness h, long credentialId, string pem) =>
        h.Use<FiscalSetupService, IReadOnlyList<CertificateCheck>>(s => s.LoadCertificate(credentialId, Encoding.UTF8.GetBytes(pem)));

    private static Task<string?> InstanceCertificate(CoreHarness h, long instanceId) =>
        h.Use<ModuleRegistry, string?>(async r => (await r.Resolve(instanceId, CancellationToken.None)).Data.Config["certificate_pem"]?.GetValue<string>());

    [TestMethod]
    public async Task CredencialNueva_GeneraLaClaveYElPedidoConElSujetoDeArca_YLaClaveQuedaCifradaEnLaFila()
    {
        var (h, module, _, issuerId) = await Start();
        await using var _ = h;
        await using var __ = module;

        var credential = await Create(h, issuerId);

        Assert.AreEqual(CredentialStatus.Pending, credential.Status);
        var request = CertificateRequest.LoadSigningRequestPem(credential.Csr, HashAlgorithmName.SHA256, signerSignaturePadding: RSASignaturePadding.Pkcs1);
        var subject = request.SubjectName.Format(false);
        StringAssert.Contains(subject, "C=AR");
        StringAssert.Contains(subject, "O=Proveedor de prueba SA");
        StringAssert.Contains(subject, "CN=billingoss");
        StringAssert.Contains(subject, $"SERIALNUMBER=CUIT {Cuit}");
        Assert.AreEqual(2048, request.PublicKey.GetRSAPublicKey()!.KeySize);

        var stored = await h.Query(db => db.FiscalCredentials.SingleAsync(c => c.Id == credential.Id));
        Assert.IsNotNull(stored.PrivateKey);
        Assert.IsFalse(Encoding.UTF8.GetString(stored.PrivateKey).Contains("PRIVATE KEY", StringComparison.Ordinal), "La clave se guarda cifrada, nunca en claro.");
        Assert.IsTrue(await h.Query(db => db.Set<AuditEvent>().AnyAsync(a => a.Action == "fiscal.credential.create")));

        await Assert.ThrowsExactlyAsync<ValidationException>(() => Create(h, issuerId, alias: "Billing OSS"));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Create(h, issuerId, environment: "staging"));
    }

    [TestMethod]
    public async Task CargarElCertificado_ValidaLocalmente_CadaCasoConSuMensaje()
    {
        var (h, module, _, issuerId) = await Start();
        await using var _ = h;
        await using var __ = module;
        using var ca = new FakeCa(h.Clock.Now);
        var now = h.Clock.Now;
        var credential = await Create(h, issuerId);

        // Esto no es un certificado.
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Load(h, credential.Id, "esto no es un certificado"));

        // De otro pedido: la clave pública no es la guardada.
        var (other, _) = ca.IssueOther($"CN=billingoss, SERIALNUMBER=CUIT {Cuit}, O=Proveedor, C=AR", now.AddDays(-1), now.AddYears(2));
        var mismatch = await Load(h, credential.Id, other);
        Assert.AreEqual("key_mismatch", mismatch.Single(c => c.Level == CheckLevel.Error).Code);
        Assert.AreEqual(CredentialStatus.Pending, (await h.Query(db => db.FiscalCredentials.SingleAsync(c => c.Id == credential.Id))).Status);

        // Vencido.
        var expired = await Load(h, credential.Id, ca.Issue(credential.Csr, now.AddYears(-3), now.AddDays(-1)));
        Assert.AreEqual("expired", expired.Single(c => c.Level == CheckLevel.Error).Code);

        // De otra CUIT y otro alias: avisa y no frena (en homologación es lo normal), y vence en 20 días.
        var warned = await Load(h, credential.Id, ca.IssueWithSubject(credential.Csr, "CN=otroalias, SERIALNUMBER=CUIT 20000000001, O=Proveedor, C=AR", now.AddDays(-1), now.AddDays(20)));
        Assert.IsFalse(warned.Any(c => c.Level == CheckLevel.Error));
        CollectionAssert.AreEquivalent(new[] { "cuit_other", "alias_other", "expires_soon" }, warned.Where(c => c.Level == CheckLevel.Warning).Select(c => c.Code).ToList());
        StringAssert.Contains(warned.Single(c => c.Code == "cuit_other").Message, $"vas a facturar con la {Cuit}");

        // El bueno: queda lista, con sus metadatos.
        var ok = await Load(h, credential.Id, ca.Issue(credential.Csr, now.AddDays(-1), now.AddYears(2)));
        Assert.IsFalse(ok.Any(c => c.Level != CheckLevel.Ok));
        var ready = await h.Query(db => db.FiscalCredentials.SingleAsync(c => c.Id == credential.Id));
        Assert.AreEqual(CredentialStatus.Ready, ready.Status);
        Assert.AreEqual(Cuit, ready.CertificateTaxId);
        Assert.AreEqual("billingoss", ready.CertificateAlias);
        Assert.AreEqual(64, ready.Thumbprint!.Length);
        Assert.AreEqual(731, ready.DaysLeft(DateOnly.FromDateTime(now.UtcDateTime)));
        StringAssert.Contains(ready.CertificateIssuer!, "AC de prueba");
    }

    [TestMethod]
    public async Task Activar_ReemplazaLosSecretosDeLaInstancia_RetiraLaAnterior_YBorraLaClaveDeLaFila()
    {
        var (h, module, fisco, issuerId) = await Start();
        await using var _ = h;
        await using var __ = module;
        using var ca = new FakeCa(h.Clock.Now);
        var first = await Create(h, issuerId);
        var pem = ca.Issue(first.Csr, h.Clock.Now.AddDays(-1), h.Clock.Now.AddYears(2));
        await Load(h, first.Id, pem);

        await h.Use<FiscalSetupService, bool>(async s =>
        {
            await s.Activate(first.Id);
            return true;
        });

        var active = await h.Query(db => db.FiscalCredentials.SingleAsync(c => c.Id == first.Id));
        Assert.AreEqual(CredentialStatus.Active, active.Status);
        Assert.IsNull(active.PrivateKey, "La clave deja la fila: queda en los secretos de la instancia.");
        Assert.AreEqual(pem, await InstanceCertificate(h, module.InstanceId));
        Assert.IsTrue(await h.Query(db => db.Set<AuditEvent>().AnyAsync(a => a.Action == "fiscal.credential.activate")));

        // La emisión usa el certificado nuevo.
        await FiscalFlowTests.Order(h);
        await h.RunJobs();
        Assert.AreEqual(pem, fisco.CertificatesSeen.Last());

        // Una segunda credencial activada retira a la primera: hay una sola activa por emisor y ambiente.
        var second = await Create(h, issuerId);
        await Load(h, second.Id, ca.Issue(second.Csr, h.Clock.Now.AddDays(-1), h.Clock.Now.AddYears(2)));
        await h.Use<FiscalSetupService, bool>(async s =>
        {
            await s.Activate(second.Id);
            return true;
        });
        var statuses = await h.Query(db => db.FiscalCredentials.OrderBy(c => c.Id).Select(c => c.Status).ToListAsync());
        CollectionAssert.AreEqual(new[] { CredentialStatus.Retired, CredentialStatus.Active }, statuses);

        // Sin certificado validado no se activa, y una credencial de producción no entra en la instancia de homologación.
        var pending = await Create(h, issuerId);
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<FiscalSetupService, bool>(async s => { await s.Activate(pending.Id); return true; }));
        var production = await Create(h, issuerId, environment: "production");
        await Load(h, production.Id, ca.Issue(production.Csr, h.Clock.Now.AddDays(-1), h.Clock.Now.AddYears(2)));
        var wrong = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<FiscalSetupService, bool>(async s => { await s.Activate(production.Id); return true; }));
        StringAssert.Contains(wrong.Message, "homologation");
    }

    [TestMethod]
    public async Task Activar_ConSecretosCargadosAMano_PruebaLaCandidataConUnLoginNuevo_YSiNoPasaNoTocaNada()
    {
        // La instancia emite con un certificado cargado por consola y sin fila de credencial activa (el caso de una
        // migración): la primera credencial del asistente no pisa esos secretos sin probar antes (sección 2.8).
        var (h, module, fisco, issuerId) = await Start();
        await using var _ = h;
        await using var __ = module;
        using var ca = new FakeCa(h.Clock.Now);
        var candidate = await Create(h, issuerId);
        var pem = ca.Issue(candidate.Csr, h.Clock.Now.AddDays(-1), h.Clock.Now.AddYears(2));
        await Load(h, candidate.Id, pem);
        fisco.PointsOfSale = new JsonArray();

        var ex = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<FiscalSetupService, FiscalTestReport?>(s => s.Activate(candidate.Id)));

        StringAssert.Contains(ex.Message, "no pasó la prueba");
        Assert.AreEqual("CERT-VIEJO", await InstanceCertificate(h, module.InstanceId), "Los secretos que funcionan siguen ahí.");
        Assert.AreEqual(CredentialStatus.Ready, (await h.Query(db => db.FiscalCredentials.SingleAsync())).Status);
        Assert.IsTrue(fisco.TestCalls.Single()["fresh_login"]!.GetValue<bool>());
        Assert.AreEqual(pem, fisco.CertificatesSeen.Single(), "La prueba lleva la candidata sin guardarla.");

        // Arreglado el punto de venta, activar prueba de nuevo y recién entonces reemplaza.
        fisco.PointsOfSale = new JsonArray(new JsonObject { ["id"] = 3, ["emission_type"] = "CAE", ["blocked"] = false });
        var report = await h.Use<FiscalSetupService, FiscalTestReport?>(s => s.Activate(candidate.Id));
        Assert.IsTrue(report!.Ok);
        Assert.AreEqual(pem, await InstanceCertificate(h, module.InstanceId));
        Assert.AreEqual(CredentialStatus.Active, (await h.Query(db => db.FiscalCredentials.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task Activar_SinSecretosEnLaInstancia_ReemplazaSinProbar()
    {
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        var fisco = new FakeFisco();
        await using var module = await TestModule.Start(fisco.Definition(), h, box, $$"""{"cuit":"{{Cuit}}","environment":"homologation"}""");
        long issuerId = 0;
        await h.Db(async db =>
        {
            var issuer = new IssuerProfile { TaxId = Cuit, LegalName = "Proveedor de prueba", ModuleInstanceId = module.InstanceId, PointOfSale = 3, TypeMap = FiscalSetupService.DefaultTypeMap(VatConditions.ResponsableInscripto, false, false) };
            db.IssuerProfiles.Add(issuer);
            await db.SaveChangesAsync();
            issuerId = issuer.Id;
        });
        using var ca = new FakeCa(h.Clock.Now);
        var first = await Create(h, issuerId);
        var pem = ca.Issue(first.Csr, h.Clock.Now.AddDays(-1), h.Clock.Now.AddYears(2));
        await Load(h, first.Id, pem);

        var report = await h.Use<FiscalSetupService, FiscalTestReport?>(s => s.Activate(first.Id));

        Assert.IsNull(report, "Sin secretos no hay nada que proteger: no se prueba.");
        Assert.IsEmpty(fisco.TestCalls);
        Assert.AreEqual(pem, await InstanceCertificate(h, module.InstanceId));
    }

    [TestMethod]
    public async Task PasarAProduccion_CreaOtroEmisorSinSeries_YAlTerminarReemplazaAlDeHomologacion()
    {
        var (h, module, fisco, issuerId) = await Start();
        await using var _ = h;
        await using var __ = module;
        var production = new FakeFisco();
        await using var productionModule = await TestModule.Start(production.Definition(), h, TestModule.NewBox(), $$"""{"cuit":"{{Cuit}}","environment":"production"}""", """{"certificate_pem":"CERT-PROD","private_key_pem":"KEY-PROD"}""");
        await h.Use<FiscalSetupService, FiscalTestReport>(s => s.Test(issuerId));
        await h.Use<FiscalSetupService, SetupSummary>(s => s.Finish(issuerId));
        await h.Db(async db =>
        {
            (await db.IssuerProfiles.SingleAsync()).Address = "Calle 1";
            await db.SaveChangesAsync();
        });

        var clone = await h.Use<FiscalSetupService, IssuerProfile>(s => s.StartNewIssuer(issuerId));

        Assert.AreNotEqual(issuerId, clone.Id);
        Assert.AreEqual((Cuit, "Proveedor de prueba", "Calle 1", false, 0, null), (clone.TaxId, clone.LegalName, clone.Address, clone.Active, clone.SetupStep, clone.ModuleInstanceId));
        Assert.IsFalse(await h.Query(db => db.FiscalSeries.AnyAsync(s => s.IssuerId == clone.Id)), "Sin series: la numeración de homologación no se arrastra.");
        Assert.AreEqual(clone.Id, (await h.Use<FiscalSetupService, IssuerProfile>(s => s.EnsureIssuer())).Id, "El asistente sigue con el emisor en preparación.");
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<FiscalSetupService, IssuerProfile>(s => s.StartNewIssuer(issuerId)));

        // Mientras tanto el de homologación sigue activo y emitiendo.
        await FiscalFlowTests.Order(h);
        await h.RunJobs();
        Assert.AreEqual(1, fisco.Authorized);

        await h.Use<FiscalSetupService, bool>(async s => { await s.Start(clone.Id, "production", false, false, productionModule.InstanceId, null); return true; });
        await h.Use<FiscalSetupService, FiscalTestReport>(s => s.Test(clone.Id));
        var summary = await h.Use<FiscalSetupService, SetupSummary>(s => s.Finish(clone.Id));

        Assert.AreEqual("production", summary.Environment);
        var active = await h.Query(db => db.IssuerProfiles.SingleAsync(i => i.Active));
        Assert.AreEqual(clone.Id, active.Id);
        Assert.AreEqual(2, await h.Query(db => db.IssuerProfiles.CountAsync()));
        Assert.IsTrue(await h.Query(db => db.FiscalSeries.AnyAsync(s => s.IssuerId == issuerId)), "El emisor de homologación conserva sus series.");

        // Uno de producción no pasa a otro emisor, porque lo que espera comprobante es real, y uno ya reemplazado tampoco.
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<FiscalSetupService, IssuerProfile>(s => s.StartNewIssuer(clone.Id)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<FiscalSetupService, IssuerProfile>(s => s.StartNewIssuer(issuerId)));
    }

    [TestMethod]
    public async Task EmisorDelImportador_NoQuedaEnPreparacion_YNoFrenaElPaseAProduccion()
    {
        var (h, module, _, issuerId) = await Start();
        await using var _ = h;
        await using var __ = module;
        await h.Use<FiscalSetupService, FiscalTestReport>(s => s.Test(issuerId));
        await h.Use<FiscalSetupService, SetupSummary>(s => s.Finish(issuerId));

        // Como lo deja el importador: inactivo, sin asistente, con la numeración del sistema anterior.
        await h.Db(async db =>
        {
            var imported = new IssuerProfile { TaxId = Cuit, LegalName = "Emisor importado", PointOfSale = 2, Active = false };
            db.IssuerProfiles.Add(imported);
            await db.SaveChangesAsync();
            db.FiscalSeries.Add(new FiscalSeries(imported.Id, 2, 6) { LastNumber = 900 });
            await db.SaveChangesAsync();
        });

        Assert.IsNull(await h.Use<FiscalSetupService, IssuerProfile?>(s => s.InSetup()));
        Assert.AreEqual(issuerId, (await h.Use<FiscalSetupService, IssuerProfile>(s => s.EnsureIssuer())).Id, "El asistente sigue con el activo, no con el del importador.");
        var clone = await h.Use<FiscalSetupService, IssuerProfile>(s => s.StartNewIssuer(issuerId));
        Assert.AreEqual(clone.Id, (await h.Use<FiscalSetupService, IssuerProfile?>(s => s.InSetup()))?.Id);
    }

    [TestMethod]
    public async Task Corte_SinEmisorActivo_ElAsistenteSigueConElDelImportador_ComparaSuNumeracion_YConservaSuMomentoDeEmision()
    {
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        var fisco = new FakeFisco { Offset = 901 };
        await using var module = await TestModule.Start(fisco.Definition(), h, box, $$"""{"cuit":"{{Cuit}}","environment":"production"}""", """{"certificate_pem":"CERT-PROD","private_key_pem":"KEY-PROD"}""");

        // Como lo deja el importador en la base nueva del corte: inactivo, sin asistente, con la numeración y el momento de emisión del sistema anterior.
        long importedId = 0;
        await h.Db(async db =>
        {
            var imported = new IssuerProfile { TaxId = Cuit, LegalName = "Emisor importado", PointOfSale = 3, TypeMap = FiscalSetupService.DefaultTypeMap(VatConditions.ResponsableInscripto, false, false), Moment = EmissionMoment.AtFirstPayment, Active = false };
            db.IssuerProfiles.Add(imported);
            await db.SaveChangesAsync();
            db.FiscalSeries.Add(new FiscalSeries(imported.Id, 3, 6) { LastNumber = 900 });
            await db.SaveChangesAsync();
            importedId = imported.Id;
        });

        Assert.AreEqual(importedId, (await h.Use<FiscalSetupService, IssuerProfile>(s => s.EnsureIssuer())).Id, "Sin emisor activo, el asistente sigue con el del importador.");
        await h.Use<FiscalSetupService, bool>(async s => { await s.Start(importedId, "production", false, false, module.InstanceId, null); return true; });

        // ARCA tiene un comprobante más que el último importado: la prueba lo marca y no deja activar.
        var drift = await h.Use<FiscalSetupService, FiscalTestReport>(s => s.Test(importedId));
        Assert.IsFalse(drift.Items.Single(i => i.Step == "series").Ok);
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<FiscalSetupService, SetupSummary>(s => s.Finish(importedId)));

        fisco.Offset = 900;
        Assert.IsTrue((await h.Use<FiscalSetupService, FiscalTestReport>(s => s.Test(importedId))).Ok);
        await h.Use<FiscalSetupService, SetupSummary>(s => s.Finish(importedId));

        var active = await h.Query(db => db.IssuerProfiles.SingleAsync(i => i.Active));
        Assert.AreEqual((importedId, EmissionMoment.AtFirstPayment), (active.Id, active.Moment), "El del importador queda activo con su momento de emisión.");
        Assert.AreEqual(1, await h.Query(db => db.IssuerProfiles.CountAsync()), "No queda otro emisor vacío.");
        Assert.AreEqual(900L, (await h.Query(db => db.FiscalSeries.SingleAsync(s => s.IssuerId == importedId && s.TypeCode == 6))).LastNumber);
    }

    [TestMethod]
    public async Task Staging_SinEmisorActivo_EnHomologacion_PruebaConOtroEmisor_YElDelImportadorQuedaParaElCorte()
    {
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        await using var module = await TestModule.Start(new FakeFisco().Definition(), h, box, $$"""{"cuit":"{{Cuit}}","environment":"homologation"}""", """{"certificate_pem":"CERT-HOMO","private_key_pem":"KEY-HOMO"}""");

        // La copia importada en el staging: el emisor del importador, inactivo, con la numeración real del sistema anterior.
        long importedId = 0;
        await h.Db(async db =>
        {
            var imported = new IssuerProfile { TaxId = Cuit, LegalName = "Emisor importado", PointOfSale = 3, TypeMap = FiscalSetupService.DefaultTypeMap(VatConditions.ResponsableInscripto, false, false), Moment = EmissionMoment.AtFirstPayment, Active = false };
            db.IssuerProfiles.Add(imported);
            await db.SaveChangesAsync();
            db.FiscalSeries.Add(new FiscalSeries(imported.Id, 3, 6) { LastNumber = 900 });
            await db.SaveChangesAsync();
            importedId = imported.Id;
        });

        // Esa numeración es real: no pasa a una instancia de homologación.
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<FiscalSetupService, bool>(async s => { await s.AssignInstance(importedId, module.InstanceId); return true; }));

        await h.Use<FiscalSetupService, bool>(async s => { await s.Start(importedId, "homologation", false, false, module.InstanceId, null); return true; });
        var trial = await h.Use<FiscalSetupService, IssuerProfile>(s => s.EnsureIssuer());
        Assert.AreNotEqual(importedId, trial.Id, "En homologación el asistente sigue con otro emisor.");
        Assert.AreEqual((module.InstanceId, EmissionMoment.AtFirstPayment, 3), (trial.ModuleInstanceId, trial.Moment, trial.PointOfSale), "Con los datos y los ajustes del importado.");
        Assert.IsTrue((await h.Use<FiscalSetupService, FiscalTestReport>(s => s.Test(trial.Id))).Ok);
        await h.Use<FiscalSetupService, SetupSummary>(s => s.Finish(trial.Id));

        var imported = await h.Query(db => db.IssuerProfiles.SingleAsync(i => i.Id == importedId));
        Assert.AreEqual((false, (long?)null), (imported.Active, imported.ModuleInstanceId), "El del importador queda inactivo y sin instancia para el corte.");
        Assert.AreEqual(900L, (await h.Query(db => db.FiscalSeries.SingleAsync(s => s.IssuerId == importedId))).LastNumber);
        Assert.AreEqual(trial.Id, (await h.Query(db => db.IssuerProfiles.SingleAsync(i => i.Active))).Id);
    }

    [TestMethod]
    public async Task PasarAProduccion_LoEmitidoEnHomologacion_NoSaleConElEmisorDeProduccion_NiSuNotaAsociaElComprobanteDePrueba()
    {
        var (h, module, fisco, issuerId) = await Start(productionEnabled: true);
        await using var _ = h;
        await using var __ = module;
        var production = new FakeFisco();
        await using var productionModule = await TestModule.Start(production.Definition(), h, TestModule.NewBox(), $$"""{"cuit":"{{Cuit}}","environment":"production"}""", """{"certificate_pem":"CERT-PROD","private_key_pem":"KEY-PROD"}""");
        await h.Use<FiscalSetupService, FiscalTestReport>(s => s.Test(issuerId));
        await h.Use<FiscalSetupService, SetupSummary>(s => s.Finish(issuerId));

        // En homologación: una factura con comprobante de prueba y otra que espera el primer cobro.
        var tested = await FiscalFlowTests.Order(h);
        await h.RunJobs();
        await h.Db(async db =>
        {
            (await db.IssuerProfiles.SingleAsync(i => i.Id == issuerId)).Moment = EmissionMoment.AtFirstPayment;
            await db.SaveChangesAsync();
        });
        var unpaid = await FiscalFlowTests.Order(h);
        await h.RunJobs();
        Assert.AreEqual(1, fisco.Authorized);

        h.Clock.Now = h.Clock.Now.AddMinutes(5);
        var clone = await h.Use<FiscalSetupService, IssuerProfile>(s => s.StartNewIssuer(issuerId));
        await h.Use<FiscalSetupService, bool>(async s => { await s.Start(clone.Id, "production", false, false, productionModule.InstanceId, null); return true; });
        await h.Use<FiscalSetupService, FiscalTestReport>(s => s.Test(clone.Id));
        await h.Use<FiscalSetupService, SetupSummary>(s => s.Finish(clone.Id));

        // Ya en producción: la nota de la factura de prueba, una factura nueva y el barrido de fin de mes.
        await h.Use<CreditService, long>(s => s.IssueCreditNote(tested.InvoiceId!.Value, [new CreditLine("Anulación", 1, 12100m, TaxTreatment.Taxed, 0.21m)], "Prueba de homologación"));
        var fresh = await FiscalFlowTests.Order(h);
        h.Clock.Now = new DateTimeOffset(2026, 11, 30, 15, 0, 0, TimeSpan.Zero);
        await h.Tick();
        await h.RunJobs();

        var emitted = await h.Query(db => db.FiscalDocuments.Where(f => f.IssuerId == clone.Id).Select(f => f.DocumentId).ToListAsync());
        CollectionAssert.Contains(emitted, fresh.InvoiceId!.Value, "Lo nuevo sale con el emisor de producción.");
        CollectionAssert.DoesNotContain(emitted, unpaid.InvoiceId!.Value, "La factura de homologación sin comprobante no sale en producción.");
        Assert.IsFalse(await h.Query(db => db.FiscalEmissions.AnyAsync(e => e.IssuerId == clone.Id && e.DocumentId == unpaid.InvoiceId)));
        var note = await h.Query(db => db.FiscalEmissions.SingleAsync(e => e.IssuerId == clone.Id && e.DocumentId != fresh.InvoiceId && db.Documents.Any(d => d.Id == e.DocumentId && d.RelatedDocumentId == tested.InvoiceId)));
        Assert.AreEqual(EmissionStatus.Rejected, note.Status, "La nota no asocia en producción el comprobante de homologación.");
        StringAssert.Contains(note.LastError, "no tiene comprobante fiscal");
        Assert.AreEqual(emitted.Count, production.Authorized);
    }

    [TestMethod]
    public async Task PruebaSinEmitir_ComponeLasConsultas_GuardaElResultado_YDiceQueArreglar()
    {
        var (h, module, fisco, issuerId) = await Start();
        await using var _ = h;
        await using var __ = module;
        await h.Db(async db =>
        {
            var issuer = await db.IssuerProfiles.SingleAsync();
            issuer.Exports = true;
            issuer.ExportPointOfSale = 12;
            issuer.UsesRegistry = true;
            await db.SaveChangesAsync();
        });
        fisco.Taxpayer = new JsonObject { ["tax_id"] = Cuit, ["name"] = "PROVEEDOR DE PRUEBA SA", ["suggested_vat_condition"] = 1, ["taxes"] = new JsonArray(), ["notes"] = new JsonArray() };

        var report = await h.Use<FiscalSetupService, FiscalTestReport>(s => s.Test(issuerId));

        Assert.IsTrue(report.Ok, string.Join("; ", report.Items.Where(i => !i.Ok).Select(i => i.Message)));
        CollectionAssert.AreEquivalent(new[] { "connection", "services", "points_of_sale", "export_point_of_sale", "series", "receiver_conditions", "taxpayer" }, report.Items.Select(i => i.Step).ToList());
        Assert.IsTrue(report.AuthorizeEnabled);
        Assert.IsTrue(fisco.TestCalls.Single()["fresh_login"]!.GetValue<bool>(), "La prueba pide un ticket nuevo.");
        Assert.AreEqual(13, report.TaxTypes![0]!["id"]!.GetValue<int>());
        var issuerAfter = await h.Query(db => db.IssuerProfiles.SingleAsync());
        Assert.IsNotNull(issuerAfter.LastTestAt);
        Assert.IsTrue(FiscalTestReport.Parse(issuerAfter.LastTest)!.Ok);
        Assert.AreEqual(3, JsonNode.Parse(issuerAfter.KnownPointsOfSale)![0]!["id"]!.GetValue<int>());

        // El punto de venta no está en la lista de ARCA, una serie corrida y una condición que el sistema no conoce.
        fisco.PointsOfSale = new JsonArray(new JsonObject { ["id"] = 4, ["emission_type"] = "CAE", ["blocked"] = false });
        fisco.ReceiverConditions.Add(new JsonObject { ["id"] = 99, ["description"] = "Nueva", ["voucher_class"] = "B" });
        fisco.Offset = 5;
        await h.Db(async db =>
        {
            db.FiscalSeries.Add(new FiscalSeries(issuerId, 3, 6) { LastNumber = 2 });
            await db.SaveChangesAsync();
        });

        var failed = await h.Use<FiscalSetupService, FiscalTestReport>(s => s.Test(issuerId));

        Assert.IsFalse(failed.Ok);
        var points = failed.Items.Single(i => i.Step == "points_of_sale");
        Assert.IsFalse(points.Ok);
        StringAssert.Contains(points.Message, "no está entre los que informa ARCA (4)");
        StringAssert.Contains(points.Remedy!, "3 días hábiles");
        var series = failed.Items.Single(i => i.Step == "series");
        Assert.IsFalse(series.Ok);
        StringAssert.Contains(series.Message, "ARCA tiene 5 y el núcleo 2");
        Assert.IsFalse(failed.Items.Single(i => i.Step == "receiver_conditions").Ok);

        // El padrón sugiere 1, 6 o 4: un monotributista social (13) con monotributo en la constancia pasa, porque la clase C es la misma.
        fisco.PointsOfSale = new JsonArray(new JsonObject { ["id"] = 3, ["emission_type"] = "CAE", ["blocked"] = false });
        fisco.Taxpayer["suggested_vat_condition"] = 6;
        await h.Db(async db =>
        {
            (await db.IssuerProfiles.SingleAsync()).VatCondition = VatConditions.MonotributistaSocial;
            await db.SaveChangesAsync();
        });
        var social = await h.Use<FiscalSetupService, FiscalTestReport>(s => s.Test(issuerId));
        Assert.IsTrue(social.Items.Single(i => i.Step == "taxpayer").Ok, social.Items.Single(i => i.Step == "taxpayer").Message);
        fisco.Taxpayer["suggested_vat_condition"] = 1;
        var otherClass = await h.Use<FiscalSetupService, FiscalTestReport>(s => s.Test(issuerId));
        Assert.IsFalse(otherClass.Items.Single(i => i.Step == "taxpayer").Ok);
        StringAssert.Contains(otherClass.Items.Single(i => i.Step == "taxpayer").Message, "cambia la clase");
    }

    [TestMethod]
    public async Task PruebaSinEmitir_SiElModuloNoResponde_ElPuntoFallaConSuRemedio_YLaEsperaDeWsaa()
    {
        var (h, module, _, issuerId) = await Start();
        await using var _ = h;
        await module.DisposeAsync();

        var report = await h.Use<FiscalSetupService, FiscalTestReport>(s => s.Test(issuerId));

        Assert.IsFalse(report.Ok);
        var connection = report.Items.Single();
        Assert.AreEqual("connection", connection.Step);
        StringAssert.Contains(connection.Remedy!, "NTP");
    }

    [TestMethod]
    public async Task Renovar_CreaOtraCredencialConElMismoSujeto_YActivarlaLaPruebaConUnLoginNuevo()
    {
        var (h, module, fisco, issuerId) = await Start();
        await using var _ = h;
        await using var __ = module;
        using var ca = new FakeCa(h.Clock.Now);
        var current = await Create(h, issuerId);
        var oldPem = ca.Issue(current.Csr, h.Clock.Now.AddDays(-1), h.Clock.Now.AddDays(20));
        await Load(h, current.Id, oldPem);
        await h.Use<FiscalSetupService, bool>(async s => { await s.Activate(current.Id); return true; });

        var renewal = await h.Use<FiscalSetupService, FiscalCredential>(s => s.Renew(current.Id));
        Assert.AreEqual(current.Alias, renewal.Alias);
        Assert.AreEqual(current.SubjectTaxId, renewal.SubjectTaxId);
        Assert.AreNotEqual(current.Csr, renewal.Csr, "Otra clave, otro pedido.");
        var newPem = ca.Issue(renewal.Csr, h.Clock.Now.AddDays(-1), h.Clock.Now.AddYears(2));
        await Load(h, renewal.Id, newPem);

        // Mientras tanto la vieja sigue emitiendo.
        Assert.AreEqual(oldPem, await InstanceCertificate(h, module.InstanceId));

        var report = await h.Use<FiscalSetupService, FiscalTestReport>(s => s.ActivateRenewal(renewal.Id));

        Assert.IsTrue(report.Ok);
        // La primera activación ya probó (la instancia tenía secretos); la renovación prueba otra vez, con el certificado nuevo.
        Assert.HasCount(2, fisco.TestCalls);
        Assert.IsTrue(fisco.TestCalls.Last()["fresh_login"]!.GetValue<bool>());
        Assert.AreEqual(newPem, fisco.CertificatesSeen.Last(), "La prueba lleva el certificado nuevo sin guardarlo.");
        Assert.AreEqual(newPem, await InstanceCertificate(h, module.InstanceId));
        var state = await h.Use<ModuleRegistry, JsonNode?>(async r => (await r.Resolve(module.InstanceId, CancellationToken.None)).Data.State);
        Assert.AreEqual("T-fresh-2", state!["ticket"]!.GetValue<string>(), "El estado que devolvió la prueba de la renovación (la segunda con login nuevo) queda con los secretos nuevos.");
        var statuses = await h.Query(db => db.FiscalCredentials.OrderBy(c => c.Id).Select(c => c.Status).ToListAsync());
        CollectionAssert.AreEqual(new[] { CredentialStatus.Retired, CredentialStatus.Active }, statuses);
    }

    [TestMethod]
    public async Task Renovar_SiLaCredencialNuevaNoPasaLaPrueba_NoSeTocaNada()
    {
        var (h, module, fisco, issuerId) = await Start();
        await using var _ = h;
        await using var __ = module;
        using var ca = new FakeCa(h.Clock.Now);
        var renewal = await Create(h, issuerId);
        await Load(h, renewal.Id, ca.Issue(renewal.Csr, h.Clock.Now.AddDays(-1), h.Clock.Now.AddYears(2)));
        fisco.PointsOfSale = new JsonArray();

        var ex = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<FiscalSetupService, FiscalTestReport>(s => s.ActivateRenewal(renewal.Id)));

        StringAssert.Contains(ex.Message, "no pasó la prueba");
        Assert.AreEqual("CERT-VIEJO", await InstanceCertificate(h, module.InstanceId));
        Assert.AreEqual(CredentialStatus.Ready, (await h.Query(db => db.FiscalCredentials.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task AvisosDeVencimiento_A30_15_7_Y1Dia_UnaSolaVezCadaUno_DesdeElPlanificador()
    {
        var (h, module, _, issuerId) = await Start();
        await using var _ = h;
        await using var __ = module;
        using var ca = new FakeCa(h.Clock.Now);
        var credential = await Create(h, issuerId);
        await Load(h, credential.Id, ca.Issue(credential.Csr, h.Clock.Now.AddDays(-1), h.Clock.Now.AddDays(20)));

        async Task<int> Notices()
        {
            await h.Tick();
            await h.RunJobs();
            return await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "fiscal.certificate.expiring"));
        }

        Assert.AreEqual(1, await Notices(), "A 20 días ya pasó el umbral de 30.");
        Assert.AreEqual(1, await Notices(), "El mismo día no se repite.");
        h.Clock.Now = h.Clock.Now.AddDays(6);
        Assert.AreEqual(2, await Notices(), "A 14 días, el de 15.");
        h.Clock.Now = h.Clock.Now.AddDays(7);
        Assert.AreEqual(3, await Notices(), "A 7 días.");
        h.Clock.Now = h.Clock.Now.AddDays(6);
        Assert.AreEqual(4, await Notices(), "A 1 día.");
        h.Clock.Now = h.Clock.Now.AddDays(3);
        Assert.AreEqual(4, await Notices(), "Vencido: los cuatro avisos ya se dieron.");
        var stored = await h.Query(db => db.FiscalCredentials.SingleAsync());
        CollectionAssert.AreEquivalent(new[] { 30, 15, 7, 1 }, JsonSerializer.Deserialize<int[]>(stored.NoticesSent)!);
        var last = await h.Query(db => db.Set<AuditEvent>().Where(a => a.Action == "fiscal.certificate.expiring").OrderBy(a => a.Id).LastAsync());
        Assert.AreEqual(1, JsonNode.Parse(last.Details!)!["threshold"]!.GetValue<int>(), "El último aviso es el de 1 día (jsonb reacomoda el JSON, por eso se lee y no se compara texto).");
    }

    [TestMethod]
    public async Task Padron_CompletaLaFichaDelCliente_ConFuenteYFecha_YLaDelEmisor()
    {
        var (h, module, fisco, issuerId) = await Start();
        await using var _ = h;
        await using var __ = module;
        fisco.Taxpayer = new JsonObject
        {
            ["tax_id"] = "20111111112",
            ["name"] = "PEREZ JUAN",
            ["person_type"] = "FISICA",
            ["address"] = new JsonObject { ["street"] = "AV SIEMPREVIVA 742", ["city"] = "CORDOBA", ["postal_code"] = "5000", ["province"] = "CORDOBA" },
            ["taxes"] = new JsonArray(new JsonObject { ["id"] = 20, ["description"] = "MONOTRIBUTO" }),
            ["monotax_category"] = "D",
            ["suggested_vat_condition"] = 6,
            ["blocked"] = false,
            ["notes"] = new JsonArray(),
        };

        var info = await h.Use<FiscalSetupService, TaxpayerInfo>(s => s.LookupTaxpayer(issuerId, "20111111112"));
        Assert.AreEqual("PEREZ JUAN", info.Name);
        Assert.AreEqual(6, info.SuggestedVatCondition);
        Assert.AreEqual("D", info.MonotaxCategory);

        await h.Use<AccountService, bool>(async s => { await s.FillFromRegistry(h.AccountId, info, confirmedCondition: null); return true; });

        var identities = await h.Query(db => db.TaxIdentities.Where(t => t.AccountId == h.AccountId).OrderBy(t => t.Id).ToListAsync());
        Assert.HasCount(2, identities);
        Assert.IsNotNull(identities[0].ValidTo, "La ficha anterior se cierra: la identidad fiscal es versionada.");
        var current = identities[1];
        Assert.AreEqual("20111111112", current.DocumentNumber);
        Assert.AreEqual(TaxDocumentType.Cuit, current.DocumentType);
        Assert.AreEqual(VatConditions.Monotributo, current.VatCondition);
        Assert.AreEqual("arca", current.Source);
        Assert.AreEqual(h.Clock.Now, current.VerifiedAt);
        Assert.AreEqual("CORDOBA", current.City);

        // La condición es una sugerencia: quien carga puede confirmar otra. Una que no está en la tabla de ARCA no entra.
        await h.Use<AccountService, bool>(async s => { await s.FillFromRegistry(h.AccountId, info, confirmedCondition: VatConditions.MonotributistaSocial); return true; });
        Assert.AreEqual(VatConditions.MonotributistaSocial, (await h.Query(db => db.TaxIdentities.SingleAsync(t => t.AccountId == h.AccountId && t.ValidTo == null))).VatCondition);
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<AccountService, bool>(async s => { await s.FillFromRegistry(h.AccountId, info, confirmedCondition: 2); return true; }));

        // Para el emisor precarga razón social y domicilio. Ingresos brutos e inicio de actividades no vienen.
        fisco.Taxpayer["tax_id"] = Cuit;
        fisco.Taxpayer["name"] = "PROVEEDOR DE PRUEBA SA";
        await h.Use<FiscalSetupService, TaxpayerInfo>(s => s.FillIssuerFromRegistry(issuerId));
        var issuer = await h.Query(db => db.IssuerProfiles.SingleAsync());
        Assert.AreEqual("PROVEEDOR DE PRUEBA SA", issuer.LegalName);
        Assert.AreEqual("AV SIEMPREVIVA 742, CORDOBA, CORDOBA, 5000", issuer.Address);
        Assert.IsNull(issuer.GrossIncomeNumber);

        fisco.Taxpayer = null;
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<FiscalSetupService, TaxpayerInfo>(s => s.LookupTaxpayer(issuerId, "20111111112")));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<FiscalSetupService, TaxpayerInfo>(s => s.LookupTaxpayer(issuerId, "20111111113")));
    }

    [TestMethod]
    public async Task HomologacionYProduccion_NoCompartenEmisor_NiUnaInstanciaCambiaDeAmbiente()
    {
        var (h, module, _, issuerId) = await Start();
        await using var _ = h;
        await using var __ = module;
        await using var production = await TestModule.Start(new FakeFisco().Definition(), h, TestModule.NewBox(), """{"cuit":"30712345678","environment":"production"}""");

        // Sin series ni comprobantes el emisor puede cambiar de instancia y de ambiente.
        await h.Use<FiscalSetupService, bool>(async s => { await s.AssignInstance(issuerId, production.InstanceId); return true; });
        await h.Use<FiscalSetupService, bool>(async s => { await s.AssignInstance(issuerId, module.InstanceId); return true; });

        await h.Db(async db =>
        {
            db.FiscalSeries.Add(new FiscalSeries(issuerId, 3, 6) { LastNumber = 10 });
            await db.SaveChangesAsync();
        });
        var ex = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<FiscalSetupService, bool>(async s => { await s.AssignInstance(issuerId, production.InstanceId); return true; }));
        StringAssert.Contains(ex.Message, "ya tiene series o comprobantes de homologation");

        var config = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<ModuleRegistry, bool>(async r => { await r.ChangeConfig(module.InstanceId, """{"cuit":"30712345678","environment":"production"}"""); return true; }));
        StringAssert.Contains(config.Message, "no se cambia");
        await h.Use<ModuleRegistry, bool>(async r => { await r.ChangeConfig(module.InstanceId, """{"cuit":"30712345678","environment":"homologation","extra":1}"""); return true; });
    }

    [TestMethod]
    public async Task ImportarUnCertificadoPropio_ConSuClaveEnPem_QuedaListo_YSinClaveNoEntra()
    {
        var (h, module, _, issuerId) = await Start();
        await using var _ = h;
        await using var __ = module;
        using var ca = new FakeCa(h.Clock.Now);
        var (pem, key) = ca.IssueOther($"CN=viejo, SERIALNUMBER=CUIT {Cuit}, O=Proveedor, C=AR", h.Clock.Now.AddDays(-1), h.Clock.Now.AddYears(1));

        var credential = await h.Use<FiscalSetupService, FiscalCredential>(s => s.ImportCredential(issuerId, "homologation", Encoding.UTF8.GetBytes(pem), Encoding.UTF8.GetBytes(key), null));

        Assert.AreEqual(CredentialStatus.Ready, credential.Status);
        Assert.AreEqual("viejo", credential.Alias);
        Assert.AreEqual(Cuit, credential.CertificateTaxId);
        Assert.IsNotNull(credential.PrivateKey);

        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<FiscalSetupService, FiscalCredential>(s => s.ImportCredential(issuerId, "homologation", Encoding.UTF8.GetBytes(pem), Encoding.UTF8.GetBytes("no es una clave"), null)));
    }

    [TestMethod]
    public async Task Terminar_CreaLasSeriesConElUltimoNumeroDeArca_AvisaSiElPuntoYaTieneComprobantes_YMuestraLasTrabas()
    {
        var (h, module, fisco, issuerId) = await Start();
        await using var _ = h;
        await using var __ = module;
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<FiscalSetupService, SetupSummary>(s => s.Finish(issuerId)), "Sin prueba pasada no se activa.");
        fisco.Offset = 40;
        await h.Use<FiscalSetupService, FiscalTestReport>(s => s.Test(issuerId));

        var summary = await h.Use<FiscalSetupService, SetupSummary>(s => s.Finish(issuerId));

        Assert.AreEqual("homologation", summary.Environment);
        CollectionAssert.AreEquivalent(new[] { "3-1 desde 40", "3-2 desde 40", "3-3 desde 40", "3-6 desde 40", "3-7 desde 40", "3-8 desde 40" }, summary.SeriesCreated.ToList());
        Assert.HasCount(6, summary.Warnings);
        StringAssert.Contains(summary.Warnings[0], "otro sistema lo usa");
        Assert.IsFalse(summary.CoreProductionEnabled);
        Assert.IsTrue(summary.ModuleAuthorizeEnabled);
        var issuer = await h.Query(db => db.IssuerProfiles.SingleAsync());
        Assert.AreEqual(7, issuer.SetupStep);
        Assert.AreEqual(40L, (await h.Query(db => db.FiscalSeries.SingleAsync(s => s.TypeCode == 6))).LastNumber);

        // La próxima factura sigue la numeración de ARCA.
        await FiscalFlowTests.Order(h);
        await h.RunJobs();
        Assert.AreEqual(41L, (await h.Query(db => db.FiscalDocuments.SingleAsync())).Number);
    }

    [TestMethod]
    public async Task DatosDelEmisor_ValidaLaCuitYLaCondicion_YArmaElMapaDeTipos()
    {
        var (h, module, _, issuerId) = await Start();
        await using var _ = h;
        await using var __ = module;

        await h.Use<FiscalSetupService, bool>(async s => { await s.SaveIssuer(issuerId, new IssuerInput(Cuit, "Proveedor SA", VatConditions.Monotributo, Exports: true, Address: "Calle 1", GrossIncomeNumber: "Exento")); return true; });
        var issuer = await h.Query(db => db.IssuerProfiles.SingleAsync());
        Assert.AreEqual(VatConditions.Monotributo, issuer.VatCondition);
        Assert.AreEqual(2, issuer.SetupStep);
        var map = JsonNode.Parse(issuer.TypeMap)!.AsObject();
        CollectionAssert.AreEquivalent(new[] { "C", "E" }, map.Select(e => e.Key).ToList());
        Assert.AreEqual(11, map["C"]!["Invoice"]!.GetValue<int>());

        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<FiscalSetupService, bool>(async s => { await s.SaveIssuer(issuerId, new IssuerInput("30712345670", "Proveedor SA", VatConditions.ResponsableInscripto)); return true; }));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<FiscalSetupService, bool>(async s => { await s.SaveIssuer(issuerId, new IssuerInput(Cuit, "Proveedor SA", VatConditions.ConsumidorFinal)); return true; }));
        Assert.AreEqual(51, JsonNode.Parse(FiscalSetupService.DefaultTypeMap(VatConditions.ResponsableInscripto, true, false))!["A"]!["Invoice"]!.GetValue<int>());
    }
}
