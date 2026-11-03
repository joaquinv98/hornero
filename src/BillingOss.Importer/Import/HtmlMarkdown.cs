using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BillingOss.Domain.Content;

namespace BillingOss.Importer.Import;

/// <summary>
/// Pasa el HTML de los textos del sistema anterior al Markdown acotado de los contenidos (sección 2.4 de diseno/contenidos.md).
/// Entiende lo que ese Markdown acepta: párrafos, títulos, negrita, cursiva, código, listas, citas, tablas, enlaces e
/// imágenes. Todo lo demás (estilos, scripts, atributos, etiquetas que no conoce) se descarta y queda el texto. Nunca copia HTML.
/// </summary>
public static partial class HtmlMarkdown
{
    private const int MaxNesting = 8;

    /// <summary>Cuántas imágenes y enlaces vio la última conversión de este hilo, para el reporte.</summary>
    public sealed record Result(string Markdown, int Images, int Links);

    public static string ToMarkdown(string? html) => Convert(html).Markdown;

    /// <summary>El texto sin formato y en una línea, cortado en una palabra: para resúmenes y descripciones.</summary>
    public static string ToText(string? html, int maxLength = 500) => Markdown.ToPlainText(Convert(html).Markdown, maxLength);

    public static Result Convert(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new Result(string.Empty, 0, 0);
        }

        var state = new State();
        var position = 0;
        foreach (Match tag in Token().Matches(html))
        {
            if (tag.Index > position)
            {
                state.Text(html[position..tag.Index]);
            }

            position = tag.Index + tag.Length;
            if (tag.Groups["comment"].Success)
            {
                continue;
            }

            state.Tag(tag.Groups["name"].Value.ToLowerInvariant(), tag.Groups["close"].Length > 0, tag.Groups["attrs"].Value);
        }

        if (position < html.Length)
        {
            state.Text(html[position..]);
        }

        return new Result(state.Finish(), state.Images, state.Links);
    }

    [GeneratedRegex(@"<!--.*?-->|<(?<comment>!)[^>]*>|<(?<close>/?)(?<name>[A-Za-z][A-Za-z0-9]*)(?<attrs>(?:""[^""]*""|'[^']*'|[^>""'])*)>", RegexOptions.Singleline)]
    private static partial Regex Token();

    [GeneratedRegex(@"(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s""'>]+))")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"[ \t\r\n\f]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex Blanks();

    private static string? Attr(string attrs, string name)
    {
        foreach (Match m in Attribute().Matches(attrs))
        {
            if (string.Equals(m.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase))
            {
                return WebUtility.HtmlDecode(m.Groups["v"].Value);
            }
        }

        return null;
    }

    private sealed class State
    {
        private readonly StringBuilder o = new();
        private readonly Stack<(bool Ordered, int Number)> lists = new();
        private readonly Stack<int> quotes = new();
        private readonly Stack<(int Start, string Href)> links = new();
        private readonly Stack<(string Marker, StringBuilder Builder, int Start)> emphasis = new();
        private List<List<string>>? table;
        private List<string>? row;
        private StringBuilder? cell;
        private int skip;
        private int pre;
        private int code;
        private bool afterMarker;

        public int Images { get; private set; }

        public int Links { get; private set; }

        public void Text(string raw)
        {
            if (skip > 0)
            {
                return;
            }

            var text = WebWhitespace(WebUtility.HtmlDecode(raw));
            if (pre > 0)
            {
                Raw(text.Replace("```", "'''", StringComparison.Ordinal));
                return;
            }

            if (code > 0)
            {
                Out(Spaces().Replace(text.Replace('`', '\'').Replace("|", cell is null ? "|" : "\\|", StringComparison.Ordinal), " "));
                return;
            }

            text = Spaces().Replace(text, " ");
            if (text.Length == 0 || (text == " " && AtLineStart()))
            {
                return;
            }

            if (AtLineStart())
            {
                text = text.TrimStart();
            }

            Out(Escape(text));
        }

        public void Tag(string name, bool closing, string attrs)
        {
            if (name is "script" or "style" or "head" or "title" or "iframe" or "object" or "svg" or "noscript")
            {
                skip = Math.Max(0, skip + (closing ? -1 : 1));
                return;
            }

            if (skip > 0)
            {
                return;
            }

            switch (name)
            {
                case "p" or "div" or "section" or "article" or "header" or "footer" or "center" or "form":
                    Block();
                    break;
                case "br":
                    Out(pre > 0 ? "\n" : "\\\n");
                    break;
                case "hr" when !closing:
                    Block();
                    Mark("---");
                    Block();
                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Block();
                    if (!closing)
                    {
                        Mark(new string('#', Math.Min(name[1] - '0', 4)) + " ");
                    }

                    break;
                case "strong" or "b":
                    Emphasis("**", closing);
                    break;
                case "em" or "i":
                    Emphasis("*", closing);
                    break;
                case "code" when pre == 0:
                    code = Math.Max(0, code + (closing ? -1 : 1));
                    Out("`");
                    break;
                case "pre" when !closing:
                    Block();
                    pre++;
                    Mark("```\n");
                    break;
                case "pre":
                    if (pre > 0)
                    {
                        pre--;
                        if (o.Length > 0 && o[^1] != '\n')
                        {
                            o.Append('\n');
                        }

                        o.Append("```");
                        Block();
                    }

                    break;
                case "ul" or "ol":
                    if (lists.Count > 0)
                    {
                        // Una lista adentro de un ítem sigue en la línea de abajo, sin línea en blanco.
                        LineBreak();
                    }
                    else
                    {
                        Block();
                    }

                    if (!closing && lists.Count < MaxNesting)
                    {
                        lists.Push((name == "ol", 0));
                    }
                    else if (closing && lists.Count > 0)
                    {
                        lists.Pop();
                        if (lists.Count == 0)
                        {
                            Block();
                        }
                    }

                    break;
                case "li" when !closing:
                    Item();
                    break;
                case "blockquote":
                    Quote(closing);
                    break;
                case "a":
                    Anchor(closing, attrs);
                    break;
                case "img" when !closing:
                    Image(attrs);
                    break;
                case "table" when !closing:
                    Block();
                    table = [];
                    break;
                case "table":
                    FlushTable();
                    break;
                case "tr" when !closing:
                    row = [];
                    break;
                case "tr":
                    EndRow();
                    break;
                case "td" or "th" when !closing:
                    EndCell();
                    cell = new StringBuilder();
                    break;
                case "td" or "th":
                    EndCell();
                    break;
            }
        }

        public string Finish()
        {
            while (links.Count > 0)
            {
                links.Pop();
            }

            EndRow();
            FlushTable();
            var text = Blanks().Replace(o.ToString().Replace("\r", string.Empty, StringComparison.Ordinal), "\n\n").Trim();
            return text.Length > Markdown.MaxLength ? text[..Markdown.MaxLength] : text;
        }

        private static string WebWhitespace(string text) => text.Replace(' ', ' ');

        private StringBuilder Target => cell ?? o;

        private bool AtLineStart() => Target.Length == 0 || Target[^1] == '\n';

        private void Out(string text)
        {
            Target.Append(text);
            afterMarker = false;
        }

        private void Raw(string text) => Out(text);

        /// <summary>Texto que va directo a la salida, sin escapar: marcas de títulos, reglas y bloques de código.</summary>
        private void Mark(string text)
        {
            o.Append(text);
            afterMarker = false;
        }

        /// <summary>Pasa a la línea de abajo si la salida no está ya al principio de una.</summary>
        private void LineBreak()
        {
            while (o.Length > 0 && o[^1] == ' ')
            {
                o.Length--;
            }

            if (o.Length > 0 && o[^1] != '\n')
            {
                o.Append('\n');
            }
        }

        /// <summary>Un salto de bloque: deja una línea en blanco. Dentro de una celda de tabla no hace nada.</summary>
        private void Block()
        {
            if (afterMarker)
            {
                return;
            }

            if (cell is not null)
            {
                if (cell.Length > 0 && cell[^1] != ' ')
                {
                    cell.Append(' ');
                }

                return;
            }

            while (o.Length > 0 && o[^1] == ' ')
            {
                o.Length--;
            }

            if (o.Length == 0)
            {
                return;
            }

            var newlines = 0;
            for (var i = o.Length - 1; i >= 0 && o[i] == '\n'; i--)
            {
                newlines++;
            }

            o.Append('\n', Math.Max(0, 2 - newlines));
        }

        /// <summary>
        /// Negrita y cursiva. Las marcas van pegadas al texto: los espacios que quedaron adentro se pasan afuera, y un énfasis
        /// vacío no deja marcas sueltas.
        /// </summary>
        private void Emphasis(string marker, bool closing)
        {
            if (!closing)
            {
                emphasis.Push((marker, Target, Target.Length));
                Out(marker);
                return;
            }

            if (emphasis.Count == 0 || emphasis.Peek().Marker != marker)
            {
                return;
            }

            var (_, builder, start) = emphasis.Pop();
            var inner = builder.ToString(start + marker.Length, builder.Length - start - marker.Length);
            builder.Length = start;
            if (inner.Trim().Length == 0)
            {
                builder.Append(inner.Length > 0 ? " " : string.Empty);
                return;
            }

            var lead = inner[..(inner.Length - inner.TrimStart().Length)];
            var trail = inner[inner.TrimEnd().Length..];
            builder.Append(lead).Append(marker).Append(inner.Trim()).Append(marker).Append(trail);
        }

        private void Item()
        {
            if (lists.Count == 0)
            {
                lists.Push((false, 0));
            }

            LineBreak();
            var (ordered, number) = lists.Pop();
            number++;
            lists.Push((ordered, number));
            Mark(new string(' ', 4 * (lists.Count - 1)) + (ordered ? $"{number}. " : "- "));
            afterMarker = true;
        }

        private void Quote(bool closing)
        {
            Block();
            if (!closing && quotes.Count < MaxNesting)
            {
                quotes.Push(o.Length);
            }
            else if (closing && quotes.Count > 0)
            {
                var start = quotes.Pop();
                var inner = o.ToString(start, o.Length - start).TrimEnd('\n');
                o.Length = start;
                o.Append(string.Join('\n', inner.Split('\n').Select(line => line.Length == 0 ? ">" : "> " + line)));
                Block();
            }
        }

        private void Anchor(bool closing, string attrs)
        {
            if (!closing)
            {
                links.Push((Target.Length, Attr(attrs, "href") ?? string.Empty));
                return;
            }

            if (links.Count == 0)
            {
                return;
            }

            var (start, href) = links.Pop();
            var target = Target;
            var label = target.ToString(start, target.Length - start).Trim();
            if (href.Length is 0 or > 2048 || href.Any(c => c < ' ' || char.IsWhiteSpace(c) || c is '(' or ')' or '<' or '>' or '\\') || label.Length == 0 || label.Contains('\n'))
            {
                return;
            }

            target.Length = start;
            target.Append('[').Append(label.Replace("]", "\\]", StringComparison.Ordinal)).Append("](").Append(href).Append(')');
            Links++;
        }

        private void Image(string attrs)
        {
            var src = Attr(attrs, "src");
            if (string.IsNullOrWhiteSpace(src) || src.Length > 2048 || src.Any(c => c < ' ' || char.IsWhiteSpace(c) || c is '(' or ')' or '<' or '>' or '\\'))
            {
                return;
            }

            var alt = (Attr(attrs, "alt") ?? string.Empty).Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal).Trim();
            Out($"![{alt}]({src})");
            Images++;
        }

        private void EndCell()
        {
            if (cell is null)
            {
                return;
            }

            row?.Add(Spaces().Replace(cell.ToString(), " ").Trim());
            cell = null;
        }

        private void EndRow()
        {
            EndCell();
            if (row is { Count: > 0 })
            {
                table?.Add(row);
            }

            row = null;
        }

        private void FlushTable()
        {
            EndRow();
            if (table is not { Count: > 0 })
            {
                table = null;
                return;
            }

            var width = Math.Min(table.Max(r => r.Count), 20);
            string Line(List<string> cells) => "| " + string.Join(" | ", Enumerable.Range(0, width).Select(i => i < cells.Count ? cells[i] : string.Empty)) + " |";
            o.Append(Line(table[0])).Append('\n').Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", width))).Append(" |\n");
            foreach (var r in table.Skip(1).Take(200))
            {
                o.Append(Line(r)).Append('\n');
            }

            table = null;
            Block();
        }

        /// <summary>Lo que el Markdown tomaría como marca en un texto suelto va con barra.</summary>
        private string Escape(string text)
        {
            var b = new StringBuilder(text.Length + 4);
            var start = AtLineStart();
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>'
                    || (c == '|' && cell is not null)
                    || (c == '!' && i + 1 < text.Length && text[i + 1] == '[')
                    || (start && i == 0 && (c is '#' or '~' || (c is '+' or '-' && i + 1 < text.Length && text[i + 1] == ' '))))
                {
                    b.Append('\\');
                }

                b.Append(c);
            }

            var escaped = b.ToString();
            return start ? NumberedStart().Replace(escaped, @"$1\$2") : escaped;
        }
    }

    [GeneratedRegex(@"^(\d{1,9})([.)])")]
    private static partial Regex NumberedStart();
}
