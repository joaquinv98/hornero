using System.Diagnostics;
using BillingOss.Domain.Content;

namespace BillingOss.Domain.Tests.Content;

/// <summary>El Markdown acotado de los contenidos: estructura, direcciones permitidas y entradas hostiles.</summary>
[TestClass]
public sealed class MarkdownTests
{
    private static readonly MarkdownOptions Cdn = new(["cdn.example.com"]);

    [TestMethod]
    public void Parrafos_SaltosYLineaVacia()
    {
        Assert.AreEqual("<p>Hola mundo</p>", Markdown.ToHtml("Hola mundo"));
        Assert.AreEqual("<p>uno\ndos</p>", Markdown.ToHtml("uno\ndos"));
        Assert.AreEqual("<p>uno<br>\ndos</p>", Markdown.ToHtml("uno  \ndos"));
        Assert.AreEqual("<p>uno<br>\ndos</p>", Markdown.ToHtml("uno\\\ndos"));
        Assert.AreEqual("<p>a</p>\n<p>b</p>", Markdown.ToHtml("a\r\n\r\n\r\nb"));
        Assert.AreEqual(string.Empty, Markdown.ToHtml("   \n  "));
        Assert.AreEqual(string.Empty, Markdown.ToHtml(null));
    }

    [TestMethod]
    public void Titulos_ElNumeralSaleUnNivelMasAbajo()
    {
        Assert.AreEqual("<h2>Título</h2>\n<h3>Sub</h3>\n<h6>Hondo</h6>", Markdown.ToHtml("# Título\n\n## Sub\n\n###### Hondo"));
        Assert.AreEqual("<h1>Raíz</h1>", Markdown.ToHtml("# Raíz", new MarkdownOptions(HeadingOffset: 0)));
        Assert.AreEqual("<h2>Con cierre</h2>", Markdown.ToHtml("# Con cierre ##"));
        Assert.AreEqual("<p>#sinespacio</p>", Markdown.ToHtml("#sinespacio"));
        Assert.AreEqual("<p>####### siete</p>", Markdown.ToHtml("####### siete"));
    }

    [TestMethod]
    public void EnLinea_NegritaCursivaYCodigo()
    {
        Assert.AreEqual("<p><strong>negrita</strong> y <em>cursiva</em> y <code>código</code></p>", Markdown.ToHtml("**negrita** y *cursiva* y `código`"));
        Assert.AreEqual("<p><strong>a</strong> <em>b</em></p>", Markdown.ToHtml("__a__ _b_"));
        Assert.AreEqual("<p><strong><em>todo</em></strong></p>", Markdown.ToHtml("***todo***"));
        Assert.AreEqual("<p><em>una <strong>mezcla</strong> buena</em></p>", Markdown.ToHtml("*una **mezcla** buena*"));
        Assert.AreEqual("<p><code>a &lt;b&gt; `c`</code></p>", Markdown.ToHtml("`` a <b> `c` ``"));
    }

    [TestMethod]
    public void EnLinea_LoQueNoEsFormatoQuedaComoTexto()
    {
        Assert.AreEqual("<p>snake_case_name y 2 * 3 * 4</p>", Markdown.ToHtml("snake_case_name y 2 * 3 * 4"));
        Assert.AreEqual("<p>*sin cierre y `sin cierre</p>", Markdown.ToHtml("*sin cierre y `sin cierre"));
        Assert.AreEqual("<p>*no* _ni esto_</p>", Markdown.ToHtml("\\*no\\* \\_ni esto\\_"));
        Assert.AreEqual("<p>5 &gt; 3 &amp; 2 &lt; 4</p>", Markdown.ToHtml("5 > 3 & 2 < 4"));
    }

    [TestMethod]
    public void Listas_ConAnidadoYNumeracion()
    {
        Assert.AreEqual("<ul>\n<li>uno</li>\n<li>dos</li>\n</ul>", Markdown.ToHtml("- uno\n- dos"));
        Assert.AreEqual("<ul>\n<li>a\n<ul>\n<li>b</li>\n</ul></li>\n<li>c</li>\n</ul>", Markdown.ToHtml("- a\n  - b\n- c"));
        Assert.AreEqual("<ol start=\"3\">\n<li>tres</li>\n<li>cuatro</li>\n</ol>", Markdown.ToHtml("3. tres\n4. cuatro"));
        Assert.AreEqual("<ol>\n<li>uno</li>\n</ol>\n<ul>\n<li>otro tipo</li>\n</ul>", Markdown.ToHtml("1. uno\n- otro tipo"));
        Assert.AreEqual("<p>texto</p>\n<ul>\n<li>una lista que corta el párrafo</li>\n</ul>", Markdown.ToHtml("texto\n- una lista que corta el párrafo"));
    }

    [TestMethod]
    public void Listas_UnItemConVariosBloques()
    {
        var html = Markdown.ToHtml("1. Primero\n\n   Sigue en otro párrafo\n\n   ```\n   código\n   ```\n2. Segundo");

        Assert.AreEqual("<ol>\n<li>Primero\n<p>Sigue en otro párrafo</p>\n<pre><code>código\n</code></pre></li>\n<li>Segundo</li>\n</ol>", html);
        Assert.AreEqual("<ul>\n<li>continúa\nen la línea de abajo</li>\n</ul>", Markdown.ToHtml("- continúa\nen la línea de abajo"));
    }

    [TestMethod]
    public void BloquesDeCodigo_CodificanTodo()
    {
        Assert.AreEqual(
            "<pre><code class=\"language-csharp\">var a = &quot;&lt;b&gt;&quot; &amp; x;\n</code></pre>",
            Markdown.ToHtml("```csharp\nvar a = \"<b>\" & x;\n```"));
        Assert.AreEqual("<pre><code>sin cerrar\n</code></pre>", Markdown.ToHtml("~~~\nsin cerrar"));
        Assert.AreEqual("<pre><code>un\n# no es título\n</code></pre>", Markdown.ToHtml("```\nun\n# no es título\n```"));

        // La clase del lenguaje solo acepta caracteres seguros.
        Assert.AreEqual("<pre><code>x\n</code></pre>", Markdown.ToHtml("```\"><script>\nx\n```"));
    }

    [TestMethod]
    public void CitasYReglas()
    {
        Assert.AreEqual("<blockquote>\n<p>cita\ndos</p>\n</blockquote>", Markdown.ToHtml("> cita\n> dos"));
        Assert.AreEqual("<blockquote>\n<blockquote>\n<p>honda</p>\n</blockquote>\n</blockquote>", Markdown.ToHtml("> > honda"));
        Assert.AreEqual("<p>a</p>\n<hr>\n<p>b</p>", Markdown.ToHtml("a\n---\nb"));
        Assert.AreEqual("<hr>", Markdown.ToHtml("* * *"));
    }

    [TestMethod]
    public void Tablas_ConAlineacionYCeldasEnLinea()
    {
        var html = Markdown.ToHtml("| A | B |\n|---|--:|\n| **1** | 2 |\n| solo uno |");

        Assert.AreEqual(
            "<table>\n<thead><tr><th scope=\"col\">A</th><th scope=\"col\" class=\"align-right\">B</th></tr></thead>\n<tbody>\n"
            + "<tr><td><strong>1</strong></td><td class=\"align-right\">2</td></tr>\n<tr><td>solo uno</td><td class=\"align-right\"></td></tr>\n</tbody>\n</table>",
            html);
        Assert.AreEqual("<p>a | b\nno es tabla</p>", Markdown.ToHtml("a | b\nno es tabla"));
        StringAssert.Contains(Markdown.ToHtml("| x |\n|:-:|\n| a \\| b |"), "<td class=\"align-center\">a | b</td>");
    }

    [TestMethod]
    public void Enlaces_SoloLasDireccionesPermitidas()
    {
        Assert.AreEqual("<p><a href=\"https://panel.example.com/x?a=1&amp;b=2\" rel=\"noopener noreferrer\">Panel</a></p>", Markdown.ToHtml("[Panel](https://panel.example.com/x?a=1&b=2)"));
        Assert.AreEqual("<p><a href=\"/ayuda/x\">Ayuda</a> y <a href=\"#abajo\">ancla</a></p>", Markdown.ToHtml("[Ayuda](/ayuda/x) y [ancla](#abajo)"));
        Assert.AreEqual("<p><a href=\"mailto:soporte@example.com\">escribinos</a></p>", Markdown.ToHtml("[escribinos](mailto:soporte@example.com)"));
        Assert.AreEqual("<p><a href=\"https://x.example.com\" title=\"Título\" rel=\"noopener noreferrer\">con título</a></p>", Markdown.ToHtml("[con título](https://x.example.com \"Título\")"));
        Assert.AreEqual("<p><a href=\"https://x.example.com/a(b)\" rel=\"noopener noreferrer\">paréntesis</a></p>", Markdown.ToHtml("[paréntesis](https://x.example.com/a(b))"));
        Assert.AreEqual("<p><a href=\"https://x.example.com\" rel=\"noopener noreferrer\"><strong>negrita</strong> adentro</a></p>", Markdown.ToHtml("[**negrita** adentro](https://x.example.com)"));
    }

    [TestMethod]
    [DataRow("[x](javascript:alert(1))")]
    [DataRow("[x](JaVaScRiPt:alert(1))")]
    [DataRow("[x](  javascript:alert(1))")]
    [DataRow("[x](data:text/html;base64,PHNjcmlwdD4=)")]
    [DataRow("[x](vbscript:msgbox(1))")]
    [DataRow("[x](//evil.example.com)")]
    [DataRow("[x](/\\evil.example.com)")]
    [DataRow("[x](file:///etc/passwd)")]
    [DataRow("[x](ftp://example.com)")]
    [DataRow("[x](pagina-relativa)")]
    [DataRow("[x](<javascript:alert(1)>)")]
    public void Enlaces_LosEsquemasPeligrososQuedanSoloComoTexto(string source)
    {
        var html = Markdown.ToHtml(source);

        Assert.AreEqual("<p>x</p>", html);
        Assert.IsFalse(html.Contains("href", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Enlaces_UnEsquemaPartidoPorUnTabularNoSeInterpreta()
    {
        var html = Markdown.ToHtml("[x](java\tscript:alert(1))");

        Assert.IsFalse(html.Contains("<a ", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("href", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Enlaces_UnTituloNoPuedeAgregarAtributos()
    {
        var html = Markdown.ToHtml("[a](https://x.example.com \"t\\\" onmouseover=\\\"alert(1)\")");
        var quoted = Markdown.ToHtml("[a](https://x.example.com/\"onmouseover=alert(1))");

        Assert.IsFalse(html.Contains("<a ", StringComparison.Ordinal), html);
        Assert.IsFalse(quoted.Contains("\"onmouseover", StringComparison.Ordinal), quoted);
        StringAssert.Contains(quoted, "&quot;onmouseover=alert(1)");
    }

    [TestMethod]
    public void Autoenlaces()
    {
        Assert.AreEqual("<p><a href=\"https://example.com\" rel=\"noopener noreferrer\">https://example.com</a></p>", Markdown.ToHtml("<https://example.com>"));
        Assert.AreEqual("<p><a href=\"mailto:ayuda@example.com\">ayuda@example.com</a></p>", Markdown.ToHtml("<ayuda@example.com>"));
        Assert.AreEqual("<p>&lt;javascript:alert(1)&gt;</p>", Markdown.ToHtml("<javascript:alert(1)>"));
    }

    [TestMethod]
    public void Imagenes_SoloDelPropioSitioODeUnHostPermitido()
    {
        Assert.AreEqual("<p><img src=\"https://cdn.example.com/a.png\" alt=\"logo\" loading=\"lazy\" decoding=\"async\"></p>", Markdown.ToHtml("![logo](https://cdn.example.com/a.png)", Cdn));
        Assert.AreEqual("<p><img src=\"https://img.cdn.example.com/a.png\" alt=\"sub\" loading=\"lazy\" decoding=\"async\"></p>", Markdown.ToHtml("![sub](https://img.cdn.example.com/a.png)", Cdn));
        Assert.AreEqual("<p><img src=\"/ayuda/imagenes/1\" alt=\"propia\" loading=\"lazy\" decoding=\"async\"></p>", Markdown.ToHtml("![propia](/ayuda/imagenes/1)"));

        // Otro host, http, esquemas peligrosos y un host que solo termina parecido: queda el texto alternativo.
        Assert.AreEqual("<p>otro</p>", Markdown.ToHtml("![otro](https://evil.example.org/a.png)", Cdn));
        Assert.AreEqual("<p>http</p>", Markdown.ToHtml("![http](http://cdn.example.com/a.png)", Cdn));
        Assert.AreEqual("<p>parecido</p>", Markdown.ToHtml("![parecido](https://evilcdn.example.com/a.png)", Cdn));
        Assert.AreEqual("<p>data</p>", Markdown.ToHtml("![data](data:image/png;base64,AAAA)", Cdn));
        Assert.AreEqual("<p>sin host</p>", Markdown.ToHtml("![sin host](https://cdn.example.com/a.png)"));
        Assert.AreEqual("<p>x&quot; onerror=&quot;alert(1)</p>", Markdown.ToHtml("![x\" onerror=\"alert(1)](https://evil.example.org/a.png)"));
    }

    [TestMethod]
    [DataRow("<script>alert(1)</script>")]
    [DataRow("<img src=x onerror=alert(1)>")]
    [DataRow("<a href=\"javascript:alert(1)\">x</a>")]
    [DataRow("<iframe src=\"https://evil.example.com\"></iframe>")]
    [DataRow("<svg onload=alert(1)>")]
    [DataRow("<div style=\"background:url(javascript:alert(1))\">x</div>")]
    public void HtmlCrudo_NuncaPasa(string source)
    {
        var html = Markdown.ToHtml(source);

        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase), html);
        Assert.IsFalse(html.Contains("<img", StringComparison.OrdinalIgnoreCase), html);
        Assert.IsFalse(html.Contains("<iframe", StringComparison.OrdinalIgnoreCase), html);
        Assert.IsFalse(html.Contains("<svg", StringComparison.OrdinalIgnoreCase), html);
        Assert.IsFalse(html.Contains("<div", StringComparison.OrdinalIgnoreCase), html);
        Assert.IsFalse(html.Contains("<a ", StringComparison.OrdinalIgnoreCase), html);
        StringAssert.StartsWith(html, "<p>&lt;");
    }

    [TestMethod]
    public void HtmlCrudo_EnTodasPartesSeCodifica()
    {
        var html = Markdown.ToHtml("# <b>t</b>\n\n- <i>x</i>\n\n> <u>q</u>\n\n| <s> |\n|---|\n| <em> |\n\n**<mark>**\n\n`<code>`");

        Assert.IsFalse(html.Contains("<b>", StringComparison.Ordinal), html);
        Assert.IsFalse(html.Contains("<i>", StringComparison.Ordinal), html);
        Assert.IsFalse(html.Contains("<u>", StringComparison.Ordinal), html);
        Assert.IsFalse(html.Contains("<s>", StringComparison.Ordinal), html);
        Assert.IsFalse(html.Contains("<mark>", StringComparison.Ordinal), html);
        StringAssert.Contains(html, "&lt;b&gt;t&lt;/b&gt;");
        StringAssert.Contains(html, "<code>&lt;code&gt;</code>");
    }

    [TestMethod]
    public void TextoPlano_SinFormatoYCortadoEnUnaPalabra()
    {
        Assert.AreEqual("Título Un párrafo con negrita y un enlace.", Markdown.ToPlainText("# Título\n\nUn párrafo con **negrita** y un [enlace](https://x.example.com)."));
        Assert.AreEqual("a < b & c", Markdown.ToPlainText("a < b & c"));
        var cut = Markdown.ToPlainText("uno dos tres cuatro cinco seis siete ocho nueve diez", 20);
        Assert.IsTrue(cut.EndsWith('…'), cut);
        Assert.IsTrue(cut.Length <= 21, cut);
        Assert.AreEqual("uno dos tres cuatro…", cut);
    }

    [TestMethod]
    [DataRow("*a ", 70_000)]
    [DataRow("[", 200_000)]
    [DataRow("`", 200_000)]
    [DataRow("**a", 70_000)]
    [DataRow("![a](", 40_000)]
    [DataRow("_x ", 70_000)]
    [DataRow("<", 200_000)]
    [DataRow("> ", 100_000)]
    [DataRow("- ", 100_000)]
    [DataRow("| a ", 50_000)]
    public void EntradasHostiles_NoTrabanElConversor(string piece, int times)
    {
        var source = string.Concat(Enumerable.Repeat(piece, times));
        var clock = Stopwatch.StartNew();

        var html = Markdown.ToHtml(source);

        Assert.IsLessThan(3000, clock.ElapsedMilliseconds, $"Tardó {clock.ElapsedMilliseconds} ms con {piece} por {times}.");
        Assert.IsFalse(html.Contains("<script", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EntradasHostiles_LaAnidacionTieneTope()
    {
        var lists = string.Join('\n', Enumerable.Range(0, 1000).Select(n => new string(' ', n * 2) + "- x"));
        var quotes = new string('>', 3000) + " x";
        var emphasis = string.Concat(Enumerable.Repeat("*a ", 2000)) + string.Concat(Enumerable.Repeat("a* ", 2000));

        Assert.IsTrue(Markdown.ToHtml(lists).Length > 0);
        Assert.IsTrue(Markdown.ToHtml(quotes).Length > 0);
        Assert.IsTrue(Markdown.ToHtml(emphasis).Length > 0);
    }

    [TestMethod]
    public void CuerpoMuyLargo_SeCorta()
    {
        var html = Markdown.ToHtml(new string('a', Markdown.MaxLength + 5000));

        Assert.AreEqual("<p>" + new string('a', Markdown.MaxLength) + "</p>", html);
    }
}
