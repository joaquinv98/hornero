using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BillingOss.Updates.Tests;

/// <summary>
/// Paso A1 de D-20: un aviso de seguridad sobre un paquete del grafo no traba el build normal y sí el de auditoría.
/// Corre sin red: el paquete sale de una carpeta local y el aviso, inventado, de una fuente de avisos falsa.
/// </summary>
[TestClass]
public sealed class AuditTests
{
    private const string Package = "Ejemplo.Aviso.Prueba";

    [TestMethod]
    public async Task ConUnAvisoConocido_ElBuildNormalPasaYElDeAuditoriaFalla()
    {
        using var dir = new TempDir();
        await using var avisos = await FakeServer.Start(app =>
        {
            app.MapGet("/v3/index.json", (HttpContext http) => Http.Json(http, $$"""
                {"version":"3.0.0","resources":[{"@id":"{{new Uri(http.Request.Scheme + "://" + http.Request.Host + "/v3/vulnerabilidades/index.json")}}","@type":"VulnerabilityInfo/6.7.0"}]}
                """));
            app.MapGet("/v3/vulnerabilidades/index.json", (HttpContext http) => Http.Json(http, $$"""
                [{"@name":"base","@id":"{{http.Request.Scheme}}://{{http.Request.Host}}/v3/vulnerabilidades/base.json","@updated":"2026-10-28T00:00:00Z","comment":"prueba"},
                 {"@name":"update","@id":"{{http.Request.Scheme}}://{{http.Request.Host}}/v3/vulnerabilidades/update.json","@updated":"2026-10-28T00:00:00Z","comment":"prueba"}]
                """));
            app.MapGet("/v3/vulnerabilidades/base.json", (HttpContext http) => Http.Json(http, $$"""
                {"{{Package.ToLowerInvariant()}}":[{"severity":2,"url":"https://avisos.example/AVISO-PRUEBA-1","versions":"[1.0.0, 1.0.1)"}]}
                """));
            app.MapGet("/v3/vulnerabilidades/update.json", (HttpContext http) => Http.Json(http, "{}"));
        });

        CreatePackage(dir.File("fuente"), Package, "1.0.0");
        dir.Write("Directory.Build.props", $"""<Project><Import Project="{Repo.File("Directory.Build.props")}" /></Project>""");
        dir.Write("Directory.Packages.props", $"""
            <Project><ItemGroup><PackageVersion Include="{Package}" Version="1.0.0" /></ItemGroup></Project>
            """);
        dir.Write("nuget.config", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources><clear /><add key="local" value="{dir.File("fuente")}" /></packageSources>
              <auditSources><clear /><add key="avisos" value="{avisos.At("v3/index.json")}" allowInsecureConnections="true" /></auditSources>
            </configuration>
            """);
        dir.Write("Prueba/Prueba.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="{Package}" /></ItemGroup></Project>
            """);
        var environment = new Dictionary<string, string>
        {
            ["NUGET_PACKAGES"] = dir.File("paquetes"),
            ["NUGET_HTTP_CACHE_PATH"] = dir.File("cache-http"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
        };

        var normal = await Dotnet.Run(dir.Path, environment, "restore", "Prueba/Prueba.csproj");
        var auditoria = await Dotnet.Run(dir.Path, environment, "restore", "Prueba/Prueba.csproj", "-p:AuditPipeline=true", "--force");

        Assert.AreEqual(0, normal.ExitCode, normal.Output);
        StringAssert.Contains(normal.Output, "warning NU1903", normal.Output);
        Assert.AreNotEqual(0, auditoria.ExitCode, auditoria.Output);
        StringAssert.Contains(auditoria.Output, "error NU1903", auditoria.Output);
    }

    [TestMethod]
    public async Task LaAuditoriaMiraTambienLasTransitivas()
    {
        var (exitCode, output) = await Dotnet.Run(Repo.Root, new Dictionary<string, string>(), "msbuild", "src/BillingOss.Domain/BillingOss.Domain.csproj", "-getProperty:NuGetAuditMode", "-getProperty:NuGetAudit");

        Assert.AreEqual(0, exitCode, output);
        StringAssert.Contains(output, "\"NuGetAuditMode\": \"all\"", output);
        StringAssert.Contains(output, "\"NuGetAudit\": \"true\"", output);
    }

    /// <summary>Un .nupkg mínimo: solo el nuspec, sin ensamblados.</summary>
    private static void CreatePackage(string folder, string id, string version)
    {
        Directory.CreateDirectory(folder);
        using var zip = ZipFile.Open(Path.Combine(folder, $"{id}.{version}.nupkg"), ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry($"{id}.nuspec").Open());
        writer.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata><id>{id}</id><version>{version}</version><authors>billing-oss</authors><description>Paquete de prueba con un aviso inventado.</description></metadata>
            </package>
            """);
    }
}
