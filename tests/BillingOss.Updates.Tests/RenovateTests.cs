using System.Text.Json;

namespace BillingOss.Updates.Tests;

/// <summary>
/// Las reglas de Renovate que importan para la seguridad (D-20, secciones 3.1 y 11). La sintaxis completa la valida
/// renovate-config-validator: acá se mira lo que no puede cambiar sin que se note.
/// </summary>
[TestClass]
public sealed class RenovateTests
{
    [TestMethod]
    public void Renovate_FusionaSoloParchesEnMain_NuncaMajorsDeDotNetNiPostgres_NiCosign()
    {
        using var config = Json5(Repo.Read("renovate.json5"));
        var root = config.RootElement;
        var rules = root.GetProperty("packageRules").EnumerateArray().ToList();
        static string[] List(JsonElement rule, string name) => rule.TryGetProperty(name, out var value) ? [.. value.EnumerateArray().Select(v => v.GetString()!)] : [];

        CollectionAssert.IsSubsetOf(new[] { "config:recommended", "docker:pinDigests", "helpers:pinGitHubActionDigests" }, List(root, "extends"));
        Assert.AreEqual("3 days", root.GetProperty("minimumReleaseAge").GetString());
        Assert.IsTrue(root.GetProperty("osvVulnerabilityAlerts").GetBoolean());
        CollectionAssert.AreEqual(new[] { "main", "/^release\\/.*/" }, List(root, "baseBranchPatterns"));

        var automerge = rules.Where(r => r.TryGetProperty("automerge", out var a) && a.GetBoolean()).ToList();
        Assert.HasCount(1, automerge);
        CollectionAssert.AreEqual(new[] { "main" }, List(automerge[0], "matchBaseBranches"));
        CollectionAssert.AreEquivalent(new[] { "patch", "digest" }, List(automerge[0], "matchUpdateTypes"));
        Assert.IsTrue(rules.IndexOf(automerge[0]) < rules.FindIndex(r => List(r, "matchPackageNames").Contains("/cosign/") && !r.GetProperty("automerge").GetBoolean()), "La regla de cosign va después y gana.");

        var majors = rules.Single(r => List(r, "matchUpdateTypes").SequenceEqual(["major"]));
        CollectionAssert.AreEquivalent(new[] { "mcr.microsoft.com/dotnet/**", "postgres" }, List(majors, "matchPackageNames"));
        Assert.IsFalse(majors.GetProperty("enabled").GetBoolean());
        var release = rules.Single(r => List(r, "matchBaseBranches").Contains("/^release\\/.*/"));
        CollectionAssert.AreEquivalent(new[] { "minor", "major" }, List(release, "matchUpdateTypes"));
        Assert.IsFalse(release.GetProperty("enabled").GetBoolean());
        Assert.HasCount(2, rules.Where(r => r.TryGetProperty("groupName", out var g) && g.GetString() == ".NET servicing"));
    }

    /// <summary>JSON5 con claves entre comillas: alcanza con permitir comentarios y comas finales.</summary>
    private static JsonDocument Json5(string text) =>
        JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
}
