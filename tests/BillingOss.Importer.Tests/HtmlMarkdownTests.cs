using BillingOss.Domain.Content;
using BillingOss.Importer.Import;

namespace BillingOss.Importer.Tests;

/// <summary>El paso del HTML del sistema anterior al Markdown acotado de los contenidos (sección 11 de diseno/contenidos.md). Sin base de datos.</summary>
[TestClass]
public sealed class HtmlMarkdownTests
{
    [TestMethod]
    public void Bloques_ParrafosTitulosListasCitasYReglas()
    {
        var html = "<h1>Título</h1><p>Un <b>párrafo</b> con <i>énfasis</i> y <code>código</code>.</p>" +
                   "<ul><li>Uno</li><li>Dos<ul><li>Dos A</li></ul></li></ul><ol><li>Primero</li><li>Segundo</li></ol>" +
                   "<blockquote><p>Cita</p></blockquote><hr><p>Fin</p>";

        var markdown = HtmlMarkdown.ToMarkdown(html);

        Assert.AreEqual(
            "# Título\n\nUn **párrafo** con *énfasis* y `código`.\n\n- Uno\n- Dos\n    - Dos A\n\n1. Primero\n2. Segundo\n\n> Cita\n\n---\n\nFin",
            markdown);
    }

    [TestMethod]
    public void EnlacesEImagenes_ConserveLaDireccionYContaLasImagenes()
    {
        var result = HtmlMarkdown.Convert("<p>Mirá <a href=\"https://example.com/a?b=1&amp;c=2\" target=\"_blank\">el panel</a> y <img src=\"/img/logo.png\" alt=\"Logo [azul]\"> y <a name=\"ancla\">sin destino</a>.</p>");

        Assert.AreEqual("Mirá [el panel](https://example.com/a?b=1&c=2) y ![Logo azul](/img/logo.png) y sin destino.", result.Markdown);
        Assert.AreEqual(1, result.Images);
        Assert.AreEqual(1, result.Links);
    }

    [TestMethod]
    public void NoDejaPasarHtmlNiScripts_YEscapaLoQueElMarkdownTomariaComoMarca()
    {
        var markdown = HtmlMarkdown.ToMarkdown("<p onclick=\"x()\">Precio: 2 * 3 &lt;script&gt;alert(1)&lt;/script&gt; [no es enlace](http://a.b) _no_ `no`</p><script>alert(2)</script><style>p{}</style><p>1. no es lista</p><p># ni título</p><p>- ni viñeta</p>");

        Assert.DoesNotContain("alert(2)", markdown);
        Assert.DoesNotContain("p{}", markdown);
        StringAssert.Contains(markdown, "2 \\* 3 \\<script\\>alert(1)\\</script\\>");
        StringAssert.Contains(markdown, "\\[no es enlace\\](http://a.b) \\_no\\_ \\`no\\`");
        StringAssert.Contains(markdown, "1\\. no es lista");
        StringAssert.Contains(markdown, "\\# ni título");
        StringAssert.Contains(markdown, "\\- ni viñeta");

        // Lo que sale se convierte a HTML sin etiquetas propias: el texto queda como texto.
        var html = Markdown.ToHtml(markdown);
        Assert.DoesNotContain("<script", html);
        StringAssert.Contains(html, "&lt;script&gt;alert(1)&lt;/script&gt;");
        Assert.DoesNotContain("<a ", html);
        Assert.DoesNotContain("<h", html);
    }

    [TestMethod]
    public void Tablas_ConEncabezadoYCeldasConBarras()
    {
        var markdown = HtmlMarkdown.ToMarkdown("<table><tr><th>Plan</th><th>Precio</th></tr><tr><td>A | B</td><td><b>10</b></td></tr><tr><td>C</td></tr></table>");

        Assert.AreEqual("| Plan | Precio |\n| --- | --- |\n| A \\| B | **10** |\n| C |  |", markdown);
        StringAssert.Contains(Markdown.ToHtml(markdown), "<table>");
    }

    [TestMethod]
    public void BloquesDeCodigo_ConservanElTextoTalCual()
    {
        var markdown = HtmlMarkdown.ToMarkdown("<p>Ejecutá:</p><pre><code>sudo apt update\n  * dos espacios &amp; &lt;etiqueta&gt;\n```\n</code></pre><p>Listo.</p>");

        Assert.AreEqual("Ejecutá:\n\n```\nsudo apt update\n  * dos espacios & <etiqueta>\n'''\n```\n\nListo.", markdown);
    }

    [TestMethod]
    public void Enfasis_LosEspaciosQuedanAfuera_YUnoVacioNoDejaMarcas()
    {
        Assert.AreEqual("Hola **mundo** cruel", HtmlMarkdown.ToMarkdown("<p>Hola <b>mundo </b>cruel</p>"));
        Assert.AreEqual("Hola mundo", HtmlMarkdown.ToMarkdown("<p>Hola <b></b>mundo</p>"));
        Assert.AreEqual("a *b* c", HtmlMarkdown.ToMarkdown("<p>a<i> b </i>c</p>"));
    }

    [TestMethod]
    public void LineasYEntidades_SaltosDeLineaEspaciosYComillas()
    {
        Assert.AreEqual("Línea uno\\\nLínea dos", HtmlMarkdown.ToMarkdown("<p>Línea uno<br>Línea dos</p>"));
        Assert.AreEqual("Ñandú & «comillas» no", HtmlMarkdown.ToMarkdown("<div>Ñandú &amp; &laquo;comillas&raquo; &nbsp;no</div>"));
        Assert.AreEqual(string.Empty, HtmlMarkdown.ToMarkdown(null));
        Assert.AreEqual(string.Empty, HtmlMarkdown.ToMarkdown("  <p> </p> <div></div> "));
    }

    [TestMethod]
    public void ConEntradaHostil_NoSeCuelgaNiPasaDelMaximo()
    {
        var nested = string.Concat(Enumerable.Repeat("<ul><li>", 500)) + "fondo" + string.Concat(Enumerable.Repeat("</li></ul>", 500));
        var broken = string.Concat(Enumerable.Repeat("<a href=\"x", 2000)) + string.Concat(Enumerable.Repeat("<b>", 2000)) + "<!-- sin cierre";
        var huge = "<p>" + new string('x', Markdown.MaxLength + 5000) + "</p>";

        Assert.IsNotNull(HtmlMarkdown.ToMarkdown(nested));
        Assert.IsNotNull(HtmlMarkdown.ToMarkdown(broken));
        Assert.AreEqual(Markdown.MaxLength, HtmlMarkdown.ToMarkdown(huge).Length);
    }

    [TestMethod]
    public void TextoPlano_ParaResumenesCortadoEnUnaPalabra()
    {
        var text = HtmlMarkdown.ToText("<p>Un texto <b>bastante</b> largo para cortar en una palabra entera sin partirla</p>", 30);

        Assert.AreEqual("Un texto bastante largo para…", text);
        Assert.AreEqual("Hola", HtmlMarkdown.ToText("<p>Hola</p>"));
    }
}
