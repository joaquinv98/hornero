using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Registrars.Tests;

/// <summary>
/// Zona DNS, reenvíos de correo y protección de identidad (sección 9.2 del protocolo) contra los cuatro registrars, cada
/// uno con su API falsa sobre el mismo registro simulado.
/// </summary>
[TestClass]
public sealed class DnsScenarioTests
{
    private static JsonObject Record(string name, string type, string value, int? priority = null, int ttl = 3600)
    {
        var json = new JsonObject { ["name"] = name, ["type"] = type, ["value"] = value, ["ttl"] = ttl };
        if (priority is { } p)
        {
            json["priority"] = p;
        }

        return json;
    }

    /// <summary>La zona que pide el cliente: la escribe como en un panel, con nombres completos, mayúsculas y comillas.</summary>
    private static JsonObject Zone() => new()
    {
        ["records"] = new JsonArray(
            Record("@", "A", "203.0.113.10"),
            Record("WWW.ejemplo.com.", "a", "203.0.113.10"),
            Record("ejemplo.com", "MX", "Mail.Proveedor.ar.", priority: 10),
            Record("", "TXT", "\"v=spf1 mx -all\""),
            Record("v6", "AAAA", "2001:DB8::1"),
            Record("blog", "CNAME", "ghs.example.net", ttl: 300)),
    };

    private static string[] Text(IEnumerable<DnsRecord> records) =>
        [.. records.Select(r => $"{r.Name} {r.Type} {r.Value} {r.Ttl}{(r.Priority is { } p ? $" {p}" : string.Empty)}").Order(StringComparer.Ordinal)];

    private static readonly string[] Expected =
    [
        "@ A 203.0.113.10 3600",
        "@ MX mail.proveedor.ar 3600 10",
        "@ TXT v=spf1 mx -all 3600",
        "blog CNAME ghs.example.net 300",
        "v6 AAAA 2001:db8::1 3600",
        "www A 203.0.113.10 3600",
    ];

    [TestMethod]
    [DataRow("internetbs", true)]
    [DataRow("resellerclub", false)]
    [DataRow("openprovider", false)]
    [DataRow("namecheap", true)]
    public async Task Manifiesto_DeclaraDnsYProteccion_YElReenvioSoloDondeLaApiLoTiene(string registrar, bool forwarding)
    {
        await using var h = await RegistrarHost.Start(registrar);

        var (_, manifest) = await h.Send("/v0/manifest");

        var capabilities = manifest!["capabilities"]!.AsArray().Select(c => c!.ToString()).ToList();
        CollectionAssert.Contains(capabilities, "registrar.dns");
        CollectionAssert.Contains(capabilities, "registrar.privacy");
        Assert.AreEqual(forwarding, capabilities.Contains("registrar.email_forwarding"));
        Assert.AreEqual(120, manifest["timeouts"]!["set_dns"]!.GetValue<int>());
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task Zona_SeReemplazaEntera_SinTocarLosTiposQueNoSeAdministran_YRepetirlaNoCambiaNada(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);
        var domain = h.Registry.Add("ejemplo.com", h.Registry.Today.AddYears(1));
        domain.ZoneExists = true;
        domain.Records = [new DnsRecord("@", "A", "198.51.100.1"), new DnsRecord("@", "MX", "mx.viejo.example", Priority: 20), new DnsRecord("@", "CAA", "0 issue \"letsencrypt.org\"")];

        var (status, body) = await h.Send("/v0/registrar/set_dns", Zone(), "dom-1-set_dns-1");
        var writes = h.Registry.Writes;
        var (again, _) = await h.Send("/v0/registrar/set_dns", Zone(), "dom-1-set_dns-2");
        var (read, listed) = await h.Send("/v0/registrar/get_dns");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(HttpStatusCode.OK, again);
        CollectionAssert.AreEqual(Expected, Text(domain.Records.Where(r => r.Type != "CAA")));
        Assert.IsTrue(domain.Records.Any(r => r.Type == "CAA"), "Un tipo que no se administra queda como estaba.");
        Assert.AreEqual(writes, h.Registry.Writes, "La misma zona otra vez no cambia nada en el registrar.");
        Assert.AreEqual(HttpStatusCode.OK, read, listed?.ToJsonString());
        var records = listed!["result"]!["records"]!.Deserialize<List<DnsRecord>>(ModuleJson.Options)!;
        CollectionAssert.AreEqual(Expected, Text(records), "get_dns da la zona administrada, sin el CAA.");
        CollectionAssert.AreEqual(Text(records), Text(body!["result"]!["records"]!.Deserialize<List<DnsRecord>>(ModuleJson.Options)!), "set_dns devuelve la zona como quedó.");
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task Zona_UnRegistroInvalido_EsInvalidInputSinLlamarAlRegistrar(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);
        h.Registry.Add("ejemplo.com", h.Registry.Today.AddYears(1));

        var (ip, badIp) = await h.Send("/v0/registrar/set_dns", new JsonObject { ["records"] = new JsonArray(Record("@", "A", "999.1.1.1")) }, "dom-1-set_dns-1");
        var (apex, cname) = await h.Send("/v0/registrar/set_dns", new JsonObject { ["records"] = new JsonArray(Record("@", "CNAME", "otro.example.net")) }, "dom-1-set_dns-2");
        var (type, srv) = await h.Send("/v0/registrar/set_dns", new JsonObject { ["records"] = new JsonArray(Record("_sip._tcp", "SRV", "10 5 5060 sip.example.net")) }, "dom-1-set_dns-3");

        Assert.AreEqual((HttpStatusCode.UnprocessableEntity, HttpStatusCode.UnprocessableEntity, HttpStatusCode.UnprocessableEntity), (ip, apex, type));
        Assert.IsTrue(new[] { badIp, cname, srv }.All(b => RegistrarHost.Code(b) == ErrorCodes.InvalidInput));
        StringAssert.Contains(cname!["error"]!["message"]!.ToString(), "CNAME");
        Assert.AreEqual(0, h.Fake.Requests);
    }

    [TestMethod]
    [DataRow("internetbs", true)]
    [DataRow("resellerclub", false)]
    [DataRow("openprovider", false)]
    [DataRow("namecheap", true)]
    public async Task Reenvios_SeReemplazanYSeLeen_ODanNotSupported(string registrar, bool supported)
    {
        await using var h = await RegistrarHost.Start(registrar);
        var domain = h.Registry.Add("ejemplo.com", h.Registry.Today.AddYears(1));
        domain.Forwards = [new EmailForward("viejo", "viejo@example.net"), new EmailForward("info", "antes@example.net")];
        var input = new JsonObject
        {
            ["forwards"] = new JsonArray(
                new JsonObject { ["mailbox"] = "Info", ["to"] = "ana@example.net" },
                new JsonObject { ["mailbox"] = "ventas", ["to"] = "ventas@example.org" }),
        };

        var (status, body) = await h.Send("/v0/registrar/set_email_forwards", input, "dom-1-set_email_forwards-1");
        var (read, listed) = await h.Send("/v0/registrar/get_email_forwards");
        var (invalid, error) = await h.Send("/v0/registrar/set_email_forwards", new JsonObject { ["forwards"] = new JsonArray(new JsonObject { ["mailbox"] = "a@b", ["to"] = "x@example.net" }) }, "dom-1-set_email_forwards-2");

        if (!supported)
        {
            Assert.AreEqual((HttpStatusCode.NotImplemented, ErrorCodes.NotSupported), (status, RegistrarHost.Code(body)));
            Assert.AreEqual(ErrorCodes.NotSupported, RegistrarHost.Code(listed));
            return;
        }

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        CollectionAssert.AreEquivalent(new[] { "info ana@example.net", "ventas ventas@example.org" }, domain.Forwards.Select(f => $"{f.Mailbox} {f.To}").ToArray());
        Assert.AreEqual(HttpStatusCode.OK, read);
        Assert.AreEqual("[{\"mailbox\":\"info\",\"to\":\"ana@example.net\"},{\"mailbox\":\"ventas\",\"to\":\"ventas@example.org\"}]", listed!["result"]!["forwards"]!.ToJsonString());
        Assert.AreEqual((HttpStatusCode.UnprocessableEntity, ErrorCodes.InvalidInput), (invalid, RegistrarHost.Code(error)));
        if (registrar == "namecheap")
        {
            Assert.AreEqual("FWD", domain.EmailType, "Sin MX propios, el correo pasa a reenvío para que llegue.");
        }
    }

    [TestMethod]
    [DataRow("internetbs")]
    [DataRow("resellerclub")]
    [DataRow("openprovider")]
    [DataRow("namecheap")]
    public async Task Proteccion_SePrendeYSeApaga_LaMismaOtraVezNoFalla_YLaSincronizacionLaVe(string registrar)
    {
        await using var h = await RegistrarHost.Start(registrar);
        var domain = h.Registry.Add("ejemplo.com", h.Registry.Today.AddYears(1));

        var (on, body) = await h.Send("/v0/registrar/set_privacy", new JsonObject { ["enabled"] = true }, "dom-1-set_privacy-1");
        var (twice, _) = await h.Send("/v0/registrar/set_privacy", new JsonObject { ["enabled"] = true }, "dom-1-set_privacy-2");
        var (_, info) = await h.Send("/v0/registrar/get_domain");
        Assert.IsTrue(domain.Privacy);
        var (off, _) = await h.Send("/v0/registrar/set_privacy", new JsonObject { ["enabled"] = false }, "dom-1-set_privacy-3");
        var (invalid, error) = await h.Send("/v0/registrar/set_privacy", new JsonObject { ["enabled"] = "sí" }, "dom-1-set_privacy-4");

        Assert.AreEqual(HttpStatusCode.OK, on, body?.ToJsonString());
        Assert.IsTrue(body!["result"]!["enabled"]!.GetValue<bool>());
        Assert.AreEqual(HttpStatusCode.OK, twice);
        Assert.IsTrue(info!["result"]!["privacy"]!.GetValue<bool>());
        Assert.AreEqual(HttpStatusCode.OK, off);
        Assert.IsFalse(domain.Privacy);
        Assert.AreEqual((HttpStatusCode.UnprocessableEntity, ErrorCodes.InvalidInput), (invalid, RegistrarHost.Code(error)));
    }

    [TestMethod]
    public async Task OpenProvider_SinZona_LaCreaConLosRegistros_YDespuesLaCambiaEnUnSoloPedido()
    {
        await using var h = await RegistrarHost.Start("openprovider");
        var domain = h.Registry.Add("ejemplo.com", h.Registry.Today.AddYears(1));

        var (_, empty) = await h.Send("/v0/registrar/get_dns");
        var (created, body) = await h.Send("/v0/registrar/set_dns", Zone(), "dom-1-set_dns-1");
        var (changed, _) = await h.Send("/v0/registrar/set_dns", new JsonObject { ["records"] = new JsonArray(Record("@", "A", "203.0.113.20")) }, "dom-1-set_dns-2");

        Assert.AreEqual(0, empty!["result"]!["records"]!.AsArray().Count);
        Assert.AreEqual(HttpStatusCode.OK, created, body?.ToJsonString());
        Assert.AreEqual(HttpStatusCode.OK, changed);
        Assert.IsTrue(domain.ZoneExists);
        Assert.AreEqual("@ A 203.0.113.20 3600", Text(domain.Records).Single());
        Assert.AreEqual(2, h.Registry.Writes);
    }

    [TestMethod]
    public async Task Namecheap_SinSuscripcionDeProteccion_EsRechazoConSuMotivo()
    {
        await using var h = await RegistrarHost.Start("namecheap");
        h.Registry.Add("ejemplo.com", h.Registry.Today.AddYears(1)).PrivacySubscription = false;

        var (status, body) = await h.Send("/v0/registrar/set_privacy", new JsonObject { ["enabled"] = true }, "dom-1-set_privacy-1");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("no_privacy_subscription", body!["error"]!["details"]!["upstream"]!["reason"]!.ToString());
    }

    [TestMethod]
    public async Task Namecheap_MxPropiosPasanElCorreoAModoMx_YSinEllosDejaDeSerlo()
    {
        await using var h = await RegistrarHost.Start("namecheap");
        var domain = h.Registry.Add("ejemplo.com", h.Registry.Today.AddYears(1));

        await h.Send("/v0/registrar/set_dns", Zone(), "dom-1-set_dns-1");
        Assert.AreEqual("MX", domain.EmailType);
        await h.Send("/v0/registrar/set_dns", new JsonObject { ["records"] = new JsonArray(Record("@", "A", "203.0.113.10")) }, "dom-1-set_dns-2");

        Assert.AreEqual("NONE", domain.EmailType);
        Assert.AreEqual("@ A 203.0.113.10 3600", Text(domain.Records).Single());
    }

    [TestMethod]
    [DataRow(" ejemplo.com ", "@")]
    [DataRow("Mail.Ejemplo.Com.", "mail")]
    [DataRow("_dmarc", "_dmarc")]
    [DataRow("*.dev", "*.dev")]
    public void Registros_ElNombreQuedaRelativoALaZona(string name, string expected)
    {
        Assert.IsTrue(DnsRecords.TryNormalize("ejemplo.com", [new DnsRecord(name, "TXT", "x")], out var clean, out var error), error);
        Assert.AreEqual(expected, clean.Single().Name);
    }

    [TestMethod]
    [DataRow("A", "1.2.3")]
    [DataRow("A", "01.2.3.4")]
    [DataRow("AAAA", "1.2.3.4")]
    [DataRow("MX", "no es un host")]
    [DataRow("TXT", "")]
    [DataRow("NS", "ns1.example.net")]
    public void Registros_ValoresQueNoCorrespondenAlTipo_NoPasan(string type, string value) =>
        Assert.IsFalse(DnsRecords.TryNormalize("ejemplo.com", [new DnsRecord("www", type, value)], out _, out _));

    [TestMethod]
    public void Registros_UnCnameNoConviveConOtrosEnElMismoNombre_YElTtlTieneTope()
    {
        Assert.IsFalse(DnsRecords.TryNormalize("ejemplo.com", [new DnsRecord("www", "CNAME", "otro.example.net"), new DnsRecord("www", "TXT", "x")], out _, out var mixed));
        Assert.IsFalse(DnsRecords.TryNormalize("ejemplo.com", [new DnsRecord("www", "A", "1.2.3.4", Ttl: 30)], out _, out _));
        Assert.IsTrue(DnsRecords.TryNormalize("ejemplo.com", [new DnsRecord("www", "A", "1.2.3.4"), new DnsRecord("WWW", "A", "1.2.3.4")], out var deduped, out _));

        StringAssert.Contains(mixed, "CNAME");
        Assert.AreEqual(1, deduped.Count);
    }
}
