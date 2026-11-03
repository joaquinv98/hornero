namespace BillingOss.Updates.Tests;

/// <summary>
/// Lo que importa para la seguridad en los trabajos de Forgejo y en Renovate (D-20, secciones 3 y 11). No los corre: mira
/// que digan lo que tienen que decir, así un cambio que saca una traba se ve en la CI.
/// </summary>
[TestClass]
public sealed class WorkflowTests
{
    [TestMethod]
    public void LaCi_RestauraBloqueada_MiraElModelo_CorreTodo_YFrenaConUnaCriticaConArreglo()
    {
        var ci = Repo.Read(".forgejo/workflows/ci.yml");

        foreach (var expected in (string[])[
            "pull_request:", "branches: [main, 'release/**']", "dotnet restore BillingOss.slnx --locked-mode",
            "has-pending-model-changes", "for proyecto in tests/*/*.csproj", "--severity CRITICAL --ignore-unfixed --exit-code 1",
            "BILLINGOSS_TEST_TEMPLATE: billing_oss_plantilla", "-- migrate"])
        {
            StringAssert.Contains(ci, expected);
        }

        Assert.DoesNotContain("secrets.", ci, "La CI corre el código del PR: no lleva secretos.");
    }

    [TestMethod]
    public void ElVigia_CorreCadaDia_ConLasTransitivas_LaAuditoriaEstrictaYTrivy()
    {
        var vigia = Repo.Read(".forgejo/workflows/vigia.yml");

        foreach (var expected in (string[])[
            "cron: '0 9 * * *'", "workflow_dispatch:", "-p:AuditPipeline=true", "package --vulnerable --include-transitive --format json",
            "docker://aquasec/trivy", "-- vigia", "--forgejo"])
        {
            StringAssert.Contains(vigia, expected);
        }
    }

    [TestMethod]
    public void Publicar_NoTieneLlaveDeFirma_YDejaSbomManifiestoYProveniencia()
    {
        var publicar = Repo.Read(".forgejo/workflows/publicar.yml");

        foreach (var expected in (string[])["tags: ['v*']", "attest:provenance=mode=max", "--format cyclonedx", "--severity CRITICAL --ignore-unfixed --exit-code 1", "-- manifiesto", "--self-contained"])
        {
            StringAssert.Contains(publicar, expected);
        }

        foreach (var forbidden in (string[])["cosign", "COSIGN", "sign-blob", "id-token"])
        {
            Assert.DoesNotContain(forbidden, publicar, $"publicar.yml no firma: {forbidden}.");
        }
    }

    /// <summary>
    /// Un runner con el socket de Docker del host en este repo sería root en la máquina que construye las versiones para
    /// quien empuje una rama (sección 11, regla 6): se construye con BuildKit sin raíz adentro del trabajo, en el mismo
    /// runner de contenedores que la CI, y Trivy lee del registro.
    /// </summary>
    [TestMethod]
    public void Publicar_NoCorreEnElHost_ConstruyeSinRaiz_YBorraLasCredencialesAntesDeSeguir()
    {
        var publicar = Repo.Read(".forgejo/workflows/publicar.yml");

        StringAssert.Contains(publicar, "runs-on: docker");
        StringAssert.Contains(publicar, "docker://moby/buildkit:");
        StringAssert.Contains(publicar, "-rootless");
        StringAssert.Contains(publicar, "buildctl-daemonless.sh build");
        StringAssert.Contains(publicar, "--image-src remote");
        Assert.IsLessThan(publicar.IndexOf("trivy image", StringComparison.Ordinal), publicar.IndexOf("rm -rf dist/docker", StringComparison.Ordinal), "Las credenciales se borran antes de correr herramientas de terceros.");
        foreach (var forbidden in (string[])["runs-on: publicar", "docker login", "docker run", "docker buildx", "docker build", "/var/run/docker.sock"])
        {
            Assert.DoesNotContain(forbidden, publicar, $"publicar.yml no usa el Docker del host: {forbidden}.");
        }
    }
}
