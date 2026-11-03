using System.Text.Json;
using BillingOss.Releases;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BillingOss.Updates.Tests;

/// <summary>
/// El vigía diario (D-20, paso A4) contra sistemas falsos: la salida de NuGet y de Trivy armadas a mano, la metadata de
/// .NET y la API de Forgejo servidas por un Kestrel local. Nada sale a la red.
/// </summary>
[TestClass]
public sealed class WatchTests
{
    private const string NuGet = """
        {
          "version": 1,
          "parameters": "--vulnerable --include-transitive",
          "projects": [
            {
              "path": "/repo/src/BillingOss.App/BillingOss.App.csproj",
              "frameworks": [
                {
                  "framework": "net10.0",
                  "topLevelPackages": [
                    { "id": "Paquete.Directo", "requestedVersion": "1.0.0", "resolvedVersion": "1.0.0",
                      "vulnerabilities": [ { "severity": "Moderate", "advisoryurl": "https://github.com/advisories/GHSA-aaaa-bbbb-cccc" } ] }
                  ],
                  "transitivePackages": [
                    { "id": "Paquete.Transitivo", "resolvedVersion": "2.1.0",
                      "vulnerabilities": [ { "severity": "Critical", "advisoryurl": "https://github.com/advisories/GHSA-dddd-eeee-ffff" } ] }
                  ]
                }
              ]
            },
            { "path": "/repo/src/BillingOss.Domain/BillingOss.Domain.csproj" }
          ]
        }
        """;

    private const string Trivy = """
        {
          "SchemaVersion": 2,
          "ArtifactName": "lineas",
          "Results": [
            { "Target": "main/src/BillingOss.App/packages.lock.json", "Type": "nuget",
              "Vulnerabilities": [ { "VulnerabilityID": "CVE-2026-1111", "VendorIDs": ["GHSA-dddd-eeee-ffff"], "PkgName": "Paquete.Transitivo",
                "InstalledVersion": "2.1.0", "FixedVersion": "2.1.1", "Severity": "HIGH", "PrimaryURL": "https://avd.example/cve-2026-1111" } ] },
            { "Target": "release-0.4/src/BillingOss.App/packages.lock.json", "Type": "nuget",
              "Vulnerabilities": [ { "VulnerabilityID": "CVE-2026-2222", "PkgName": "Otro.Paquete", "InstalledVersion": "1.0.0", "FixedVersion": "", "Severity": "LOW" } ] },
            { "Target": "otra-carpeta/packages.lock.json", "Type": "nuget",
              "Vulnerabilities": [ { "VulnerabilityID": "CVE-2026-3333", "PkgName": "Ajeno", "InstalledVersion": "1.0.0", "Severity": "CRITICAL" } ] },
            { "Target": "main/src/BillingOss.Web/packages.lock.json", "Type": "nuget" }
          ]
        }
        """;

    [TestMethod]
    public void NuGet_LeeDirectasYTransitivas_ConSuAvisoYSeveridad()
    {
        var findings = Watch.FromNuGet("main", NuGet);

        Assert.HasCount(2, findings);
        Assert.AreEqual(new Finding("main", "GHSA-aaaa-bbbb-cccc", "nuget", "Paquete.Directo", "1.0.0", "medium", null, ["https://github.com/advisories/GHSA-aaaa-bbbb-cccc"]).Key, findings[0].Key);
        Assert.AreEqual(("Paquete.Transitivo", "2.1.0", "critical"), (findings[1].Package, findings[1].Version, findings[1].Severity));
        Assert.IsEmpty(Watch.FromNuGet("main", """{"version": 1, "projects": []}"""));
    }

    [TestMethod]
    public void Trivy_CadaResultadoEsDeLaLineaDeSuCarpeta_YSeDeduplicaConNuGetPorGhsa()
    {
        var lines = new Dictionary<string, string> { ["main"] = "main", ["release-0.4"] = "release/0.4" };
        var trivy = Watch.FromTrivy(lines, Trivy);

        Assert.HasCount(2, trivy, "Lo que no es de ninguna línea no cuenta.");
        Assert.AreEqual("GHSA-dddd-eeee-ffff@main", trivy[0].Key);
        Assert.AreEqual(("CVE-2026-2222@release/0.4", "low", null), (trivy[1].Key, trivy[1].Severity, trivy[1].Fixed));

        var unique = Watch.Deduplicate(Watch.FromNuGet("main", NuGet).Concat(trivy));
        Assert.HasCount(3, unique);
        var merged = unique.Single(f => f.Id == "GHSA-dddd-eeee-ffff");
        Assert.AreEqual(("critical", "2.1.1", "nuget+trivy"), (merged.Severity, merged.Fixed, merged.Source));
        CollectionAssert.AreEquivalent(new[] { "https://github.com/advisories/GHSA-dddd-eeee-ffff", "https://avd.example/cve-2026-1111" }, merged.References.ToArray());

        // Con una sola línea, Trivy corrido sobre su carpeta es todo de ella.
        var single = Watch.FromTrivy(new Dictionary<string, string> { ["otra"] = "main" }, Trivy);
        Assert.HasCount(3, single);
        Assert.IsTrue(single.All(f => f.Line == "main"));
    }

    [TestMethod]
    public void ElRuntimeSaleDeLaEtiquetaExactaDelDockerfile()
    {
        Assert.AreEqual(new Version(10, 0, 14), Watch.Runtime(Repo.Read("deploy/Dockerfile")));
        Assert.AreEqual(new Version(10, 0, 9), Watch.Runtime("FROM x\nARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.9-noble-chiseled-extra\n"));
        Assert.ThrowsExactly<UpdateException>(() => Watch.Runtime("ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra\n"));
    }

    [TestMethod]
    public async Task UnRuntimeViejo_JuntaLasCveDeLasVersionesDeSeguridadQueLeFaltan()
    {
        await using var metadata = await DotnetMetadata();
        using var http = new HttpClient();

        var old = await Watch.FromDotnetMetadata("main", new Version(10, 0, 12), http, metadata.Url, CancellationToken.None);
        Assert.IsNotNull(old);
        Assert.AreEqual(("dotnet-runtime@main", "10.0.12", "10.0.15", "high"), (old.Key, old.Version, old.Fixed, old.Severity));
        CollectionAssert.AreEqual(new[] { "CVE-2026-2001", "CVE-2026-3001", "CVE-2026-3002" }, old.References.ToArray());

        var partial = await Watch.FromDotnetMetadata("main", new Version(10, 0, 13), http, metadata.Url, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "CVE-2026-3001", "CVE-2026-3002" }, partial!.References.ToArray());

        // Al día, o atrás solo en versiones sin seguridad, no hay hallazgo: eso lo trae el PR de Renovate.
        Assert.IsNull(await Watch.FromDotnetMetadata("main", new Version(10, 0, 15), http, metadata.Url, CancellationToken.None));
        Assert.IsNull(await Watch.FromDotnetMetadata("main", new Version(9, 0, 19), http, metadata.Url, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<UpdateException>(() => Watch.FromDotnetMetadata("main", new Version(11, 0, 1), http, metadata.Url, CancellationToken.None));
    }

    [TestMethod]
    public void PlazosObjetivo_DotNetEnDosDiasHabiles_AltaEnSiete_ElRestoSinFecha()
    {
        var friday = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        Finding Of(string source, string severity) => new("main", "x", source, "p", "1", severity, null, []);

        Assert.AreEqual(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero), Watch.Deadline(Of("dotnet", "high"), friday));
        Assert.AreEqual(friday.AddDays(7), Watch.Deadline(Of("nuget", "critical"), friday));
        Assert.AreEqual(friday.AddDays(7), Watch.Deadline(Of("trivy", "high"), friday));
        Assert.IsNull(Watch.Deadline(Of("nuget", "medium"), friday));
        Assert.AreEqual("medium", Watch.Severity("UNKNOWN"));
    }

    [TestMethod]
    public async Task ElVigia_AbreUnIssuePorHallazgo_FijaElEstado_AvisaLoVencido_YCierraLoResuelto()
    {
        var forgejo = new FakeForgejo();
        await using var server = await FakeServer.Start(forgejo.Map);
        using var http = new HttpClient { BaseAddress = server.At("api/v1/") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", "token-de-prueba");
        var issues = new ForgejoIssues(http, "hornero/billing-oss");
        forgejo.Pulls.Add((7, "Update .NET servicing"));
        forgejo.Pulls.Add((8, "Update dependency Paquete.Transitivo to v2.1.1"));
        var day = new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
        forgejo.Now = day;
        var runtime = new Finding("main", "dotnet-runtime", "dotnet", "Microsoft.NETCore.App y Microsoft.AspNetCore.App", "10.0.12", "high", "10.0.15", ["CVE-2026-3001"]);
        var package = new Finding("main", "GHSA-dddd-eeee-ffff", "nuget+trivy", "Paquete.Transitivo", "2.1.0", "critical", "2.1.1", ["https://github.com/advisories/GHSA-dddd-eeee-ffff"]);
        var minor = new Finding("main", "GHSA-aaaa-bbbb-cccc", "nuget", "Paquete.Directo", "1.0.0", "medium", null, []);

        var first = await issues.Sync([runtime, package, minor], ["main"], ["mantenedor", "otro"], day, CancellationToken.None);

        Assert.AreEqual((3, 0, 0, 0), (first.Created, first.Updated, first.Closed, first.Overdue));
        var status = forgejo.Issues.Single(i => i.Number == first.StatusIssue);
        Assert.IsTrue(status.Pinned);
        StringAssert.Contains(status.Body, "Última corrida: 2026-10-30T09:00:00Z");
        StringAssert.Contains(status.Body, "| main | 3 | 2 |");
        var runtimeIssue = forgejo.Issues.Single(i => i.Body.Contains("vigia:dotnet-runtime@main", StringComparison.Ordinal));
        StringAssert.Contains(runtimeIssue.Body, "#7 Update .NET servicing");
        StringAssert.Contains(runtimeIssue.Title, "dotnet-runtime");
        CollectionAssert.AreEquivalent(new[] { "vigia", "severidad/high", "linea/main" }, forgejo.LabelNames(runtimeIssue).ToArray());
        StringAssert.Contains(forgejo.Issues.Single(i => i.Body.Contains("GHSA-dddd", StringComparison.Ordinal)).Body, "#8 Update dependency Paquete.Transitivo");

        // Al otro día, lo mismo: nada nuevo ni repetido, y el estado con la fecha nueva.
        forgejo.Now = day.AddDays(1);
        var second = await issues.Sync([runtime, package, minor], ["main"], ["mantenedor", "otro"], day.AddDays(1), CancellationToken.None);
        Assert.AreEqual((0, 0, 0, 0), (second.Created, second.Updated, second.Closed, second.Overdue));
        Assert.HasCount(4, forgejo.Issues);
        StringAssert.Contains(status.Body, "Última corrida: 2026-10-31T09:00:00Z");

        // El martes venció el de .NET (2 días hábiles), y a los 8 días el crítico: etiqueta y mención, una sola vez.
        var later = day.AddDays(8);
        var third = await issues.Sync([runtime, package, minor], ["main"], ["mantenedor", "otro"], later, CancellationToken.None);
        Assert.AreEqual(2, third.Overdue);
        Assert.Contains("vencido", forgejo.LabelNames(runtimeIssue));
        StringAssert.Contains(runtimeIssue.Comments.Single(), "@mantenedor @otro");
        Assert.AreEqual(0, (await issues.Sync([runtime, package, minor], ["main"], ["mantenedor"], later, CancellationToken.None)).Overdue);

        // Salió el parche de .NET: el issue del runtime se cierra con un comentario. El estado queda abierto.
        var fourth = await issues.Sync([package, minor], ["main"], [], later.AddDays(1), CancellationToken.None);
        Assert.AreEqual(1, fourth.Closed);
        Assert.AreEqual("closed", runtimeIssue.State);
        StringAssert.Contains(runtimeIssue.Comments.Last(), "Ya no aparece");
        Assert.AreEqual("open", status.State);
        Assert.IsTrue(forgejo.Authorized.All(a => a == "token token-de-prueba"), "Todo pedido lleva el token del trabajo.");
    }

    [TestMethod]
    public async Task LaLineaDeComandos_ConUnRuntimeViejoPuestoAProposito_SaleConHallazgos()
    {
        await using var metadata = await DotnetMetadata();
        using var dir = new TempDir();
        dir.Write("lineas/prueba/vigia-nuget.json", """{"version": 1, "projects": []}""");
        dir.Write("lineas/prueba/deploy/Dockerfile", "ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled-extra\n");
        dir.Write("lineas/main/vigia-nuget.json", """{"version": 1, "projects": []}""");
        dir.Write("lineas/main/deploy/Dockerfile", Repo.Read("deploy/Dockerfile").Replace("10.0.14", "10.0.15", StringComparison.Ordinal));
        using var http = new HttpClient();

        using var conHallazgo = new StringWriter();
        var exit = await Cli.Run(["vigia", "--linea", $"prueba={dir.File("lineas/prueba")}", "--metadata", metadata.Url.ToString(), "--salida", dir.File("vigia.json")], http, conHallazgo, TimeProvider.System);
        Assert.AreEqual(1, exit, conHallazgo.ToString());
        StringAssert.Contains(conHallazgo.ToString(), "prueba: dotnet-runtime en Microsoft.NETCore.App y Microsoft.AspNetCore.App 10.0.12, high, arreglo 10.0.15");
        using var json = JsonDocument.Parse(File.ReadAllText(dir.File("vigia.json")));
        Assert.AreEqual("dotnet-runtime", json.RootElement.GetProperty("hallazgos")[0].GetProperty("id").GetString());

        using var limpio = new StringWriter();
        Assert.AreEqual(0, await Cli.Run(["vigia", "--linea", $"main={dir.File("lineas/main")}", "--metadata", metadata.Url.ToString()], http, limpio, TimeProvider.System), limpio.ToString());
        StringAssert.Contains(limpio.ToString(), "Sin hallazgos.");
        using var sinLinea = new StringWriter();
        Assert.AreEqual(1, await Cli.Run(["vigia"], http, sinLinea, TimeProvider.System));
    }

    /// <summary>Metadata de .NET inventada: 10.0.13 y 10.0.15 son de seguridad, 10.0.14 no.</summary>
    private static Task<FakeServer> DotnetMetadata() => FakeServer.Start(app =>
    {
        app.MapGet("/releases-index.json", (HttpContext http) => Http.Json(http, $$"""
            {"releases-index": [
              {"channel-version": "10.0", "latest-runtime": "10.0.15", "releases.json": "{{http.Request.Scheme}}://{{http.Request.Host}}/10.0/releases.json"},
              {"channel-version": "9.0", "latest-runtime": "9.0.20", "releases.json": "{{http.Request.Scheme}}://{{http.Request.Host}}/9.0/releases.json"}]}
            """));
        app.MapGet("/10.0/releases.json", (HttpContext http) => Http.Json(http, """
            {"channel-version": "10.0", "releases": [
              {"release-version": "10.0.15", "security": true, "cve-list": [{"cve-id": "CVE-2026-3002"}, {"cve-id": "CVE-2026-3001"}], "runtime": {"version": "10.0.15"}},
              {"release-version": "10.0.14", "security": false, "cve-list": [], "runtime": {"version": "10.0.14"}},
              {"release-version": "10.0.13", "security": true, "cve-list": [{"cve-id": "CVE-2026-2001"}], "runtime": {"version": "10.0.13"}},
              {"release-version": "10.0.12", "security": true, "cve-list": [{"cve-id": "CVE-2026-1001"}], "runtime": {"version": "10.0.12"}}]}
            """));
        app.MapGet("/9.0/releases.json", (HttpContext http) => Http.Json(http, """
            {"channel-version": "9.0", "releases": [{"release-version": "9.0.20", "security": false, "runtime": {"version": "9.0.20"}}]}
            """));
    });
}
