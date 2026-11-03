using BillingOss.Domain.Content;

namespace BillingOss.Domain.Tests.Content;

/// <summary>Textos por idioma, audiencia y direcciones cortas (secciones 2.1, 2.2 y 2.6 de diseno/contenidos.md).</summary>
[TestClass]
public sealed class ContentRulesTests
{
    private sealed record Item(ContentAudience Audience, long[] ProductIds, long[] GroupIds) : IAudienced;

    [TestMethod]
    public void Idiomas_SeNormalizanYElQueNoSeSoportaNoCuenta()
    {
        Assert.AreEqual("es-AR", ContentTexts.Canonical("es"));
        Assert.AreEqual("es-AR", ContentTexts.Canonical("es-ar"));
        Assert.AreEqual("en", ContentTexts.Canonical("en-US"));
        Assert.IsNull(ContentTexts.Canonical("fr"));
        Assert.IsNull(ContentTexts.Canonical(null));
        Assert.AreEqual("es-AR", ContentTexts.Reader("pt"));
        Assert.AreEqual("en", ContentTexts.Reader("en-GB"));
    }

    [TestMethod]
    public void Textos_ElLectorVeSuIdiomaSiNoElDeBaseSiNoElPrimero()
    {
        var both = new Dictionary<string, ContentText> { ["es-AR"] = new("Hola"), ["en"] = new("Hello") };
        var onlyEnglish = new Dictionary<string, ContentText> { ["en"] = new("Hello") };

        Assert.AreEqual("Hello", ContentTexts.Pick(both, "en")!.Title);
        Assert.AreEqual("Hola", ContentTexts.Pick(both, "es")!.Title);
        Assert.AreEqual("Hola", ContentTexts.Pick(both, "pt")!.Title);
        Assert.AreEqual("Hello", ContentTexts.Pick(onlyEnglish, "es-AR")!.Title);
        Assert.AreEqual("en", ContentTexts.PickWithLanguage(onlyEnglish, "es")!.Value.Language);
        Assert.IsNull(ContentTexts.Pick(new Dictionary<string, ContentText>(), "es"));
    }

    [TestMethod]
    public void Textos_IdaYVueltaPorJsonYValidacion()
    {
        var texts = new Dictionary<string, ContentText> { ["en"] = new("Hello", null, "Body"), ["es-AR"] = new("Hola", "Resumen") };

        var json = ContentTexts.Serialize(texts);
        var back = ContentTexts.Parse(json);

        Assert.IsLessThan(json.IndexOf("\"en\"", StringComparison.Ordinal), json.IndexOf("\"es-AR\"", StringComparison.Ordinal), "El idioma de base va primero.");
        Assert.AreEqual(new ContentText("Hola", "Resumen"), back["es-AR"]);
        Assert.AreEqual("Body", back["en"].Body);
        Assert.AreEqual("Hola", ContentTexts.Title(json, "es"));
        Assert.AreEqual(string.Empty, ContentTexts.Title(null, "es"));
        Assert.IsNull(ContentTexts.Error(texts));
        Assert.IsNotNull(ContentTexts.Error(new Dictionary<string, ContentText>()));
        Assert.IsNotNull(ContentTexts.Error(new Dictionary<string, ContentText> { ["fr"] = new("Salut") }));
        Assert.IsNotNull(ContentTexts.Error(new Dictionary<string, ContentText> { ["en"] = new("  ") }));
        Assert.IsNotNull(ContentTexts.Error(new Dictionary<string, ContentText> { ["en"] = new(new string('a', 201)) }));
        Assert.IsNotNull(ContentTexts.Error(new Dictionary<string, ContentText> { ["en"] = new("Sin cuerpo") }, bodyRequired: true));
    }

    [TestMethod]
    public void Textos_LimpiarDejaProlijoYUneLosIdiomasEquivalentes()
    {
        var clean = ContentTexts.Clean(new Dictionary<string, ContentText> { ["es"] = new("  Hola  ", " ", "  \n"), ["en"] = new("Hi", " resumen ", " cuerpo ") });

        Assert.AreEqual(new ContentText("Hola"), clean["es-AR"]);
        Assert.AreEqual(new ContentText("Hi", "resumen", "cuerpo"), clean["en"]);
    }

    [TestMethod]
    public void Audiencia_PublicaLaVenTodos_ClientesSoloConSesion()
    {
        var item = new Item(ContentAudience.Public, [], []);
        var customers = item with { Audience = ContentAudience.Customers };

        Assert.IsTrue(ContentViewer.Anonymous.CanSee(item));
        Assert.IsFalse(ContentViewer.Anonymous.CanSee(customers));
        Assert.IsTrue(new ContentViewer(true, [], []).CanSee(customers));
    }

    [TestMethod]
    public void Audiencia_DirigidaPideProductoOGrupo()
    {
        var targeted = new Item(ContentAudience.Targeted, [7, 9], [3]);
        var byProduct = new ContentViewer(true, [9], []);
        var byGroup = new ContentViewer(true, [], [3]);
        var neither = new ContentViewer(true, [1, 2], [4]);

        Assert.IsTrue(byProduct.CanSee(targeted));
        Assert.IsTrue(byGroup.CanSee(targeted));
        Assert.IsFalse(neither.CanSee(targeted));
        Assert.IsFalse(ContentViewer.Anonymous.CanSee(targeted));

        // Sin sesión no hay productos ni grupos, aunque se los pasen.
        Assert.IsFalse(new ContentViewer(false, [9], [3]).CanSee(targeted));
        CollectionAssert.AreEqual(new long[] { 1, 2 }, new ContentViewer(true, [2, 1, 2], []).ProductIds);
    }

    [TestMethod]
    public void Audiencia_DirigidaSinListasNoSePuedeGuardar()
    {
        Assert.IsNotNull(ContentViewer.Error(ContentAudience.Targeted, [], []));
        Assert.IsNull(ContentViewer.Error(ContentAudience.Targeted, [1], []));
        Assert.IsNull(ContentViewer.Error(ContentAudience.Targeted, [], [2]));
        Assert.IsNull(ContentViewer.Error(ContentAudience.Public, [], []));
    }

    [TestMethod]
    [DataRow("Cómo entrar por primera vez al panel", "como-entrar-por-primera-vez-al-panel")]
    [DataRow("  Año nuevo: ¡precios & ofertas!  ", "ano-nuevo-precios-ofertas")]
    [DataRow("2024", "n-2024")]
    [DataRow("", "item")]
    [DataRow("¿¿??", "item")]
    [DataRow("MAYÚSCULAS y Ñandú", "mayusculas-y-nandu")]
    public void Slugs_SinTildesEnMinusculaYConGuiones(string text, string expected)
    {
        Assert.AreEqual(expected, Slugs.From(text));
        Assert.IsTrue(Slugs.IsValid(expected));
    }

    [TestMethod]
    public void Slugs_TopeYNumeracion()
    {
        var long80 = Slugs.From(new string('a', 300) + " b");

        Assert.AreEqual(80, long80.Length);
        Assert.AreEqual("ayuda", Slugs.Numbered("ayuda", 1));
        Assert.AreEqual("ayuda-2", Slugs.Numbered("ayuda", 2));
        Assert.IsTrue(Slugs.Numbered(new string('a', 100), 12).Length <= Slugs.MaxLength);
        Assert.IsFalse(Slugs.IsValid("Con Mayúsculas"));
        Assert.IsFalse(Slugs.IsValid("12345"));
        Assert.IsFalse(Slugs.IsValid("doble--guion"));
        Assert.IsFalse(Slugs.IsValid(null));
    }
}
