using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace BillingOss.Domain.Content;

/// <param name="ImageHosts">Hosts desde los que se aceptan imágenes por https, con sus subdominios. Las rutas del propio sitio siempre valen.</param>
/// <param name="HeadingOffset">Niveles que se suman a los títulos: con 1, "#" sale como h2, porque el h1 es el de la página.</param>
public sealed record MarkdownOptions(IReadOnlyCollection<string>? ImageHosts = null, int HeadingOffset = 1);

/// <summary>
/// El Markdown acotado de los contenidos (sección 2.4 de diseno/contenidos.md). Nunca deja pasar HTML: todo lo que no
/// entiende sale como texto codificado, y los enlaces e imágenes solo aceptan las direcciones permitidas. La conversión
/// es lineal: las búsquedas de cierre que fallan no se repiten y la anidación tiene tope.
/// </summary>
public static partial class Markdown
{
    public const int MaxLength = 200_000;

    public static string ToHtml(string? source, MarkdownOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var text = (source.Length > MaxLength ? source[..MaxLength] : source).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\t", "    ", StringComparison.Ordinal);
        var output = new StringBuilder();
        new Renderer(options ?? new MarkdownOptions()).Blocks(text.Split('\n'), 0, output, tightFirst: false);
        return output.ToString().TrimEnd();
    }

    /// <summary>El texto sin formato, en una línea y cortado en una palabra, para resúmenes y listas.</summary>
    public static string ToPlainText(string? source, int maxLength = 200)
    {
        var html = ToHtml(source, new MarkdownOptions(HeadingOffset: 0));
        var text = Whitespace().Replace(WebUtility.HtmlDecode(Tags().Replace(BlockTags().Replace(html, " "), string.Empty)), " ").Trim();
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', maxLength - 1);
        if (cut < maxLength / 2)
        {
            cut = maxLength - 1;
        }

        return text[..cut].TrimEnd(' ', ',', '.', ';', ':') + "…";
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"</?(p|h[1-6]|li|ul|ol|tr|td|th|table|thead|tbody|blockquote|pre|hr|br)\b[^>]*>")]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("^[A-Za-z0-9_+#.-]{1,24}$")]
    private static partial Regex Language();

    [GeneratedRegex(@"^[^\s@<>]+@[^\s@<>]+\.[^\s@<>]+$")]
    private static partial Regex Email();

    [GeneratedRegex("^:?-+:?$")]
    private static partial Regex DelimiterCell();

    private sealed class InlineState
    {
        public Dictionary<(char, int), int> NoCloser { get; } = [];

        public Dictionary<int, int> NoBacktick { get; } = [];
    }

    private readonly record struct Item(int Indent, bool Ordered, char Marker, int Number, int ContentIndent, string Text);

    private sealed class Renderer(MarkdownOptions options)
    {
        private const int MaxDepth = 8;
        private const int MaxRows = 200;
        private const int MaxColumns = 20;

        private readonly HashSet<string> imageHosts = new(options.ImageHosts ?? [], StringComparer.OrdinalIgnoreCase);

        // ---- Bloques ----

        public void Blocks(IReadOnlyList<string> lines, int depth, StringBuilder o, bool tightFirst)
        {
            if (depth > MaxDepth)
            {
                o.Append("<p>");
                Enc(string.Join('\n', lines), o);
                o.Append("</p>\n");
                return;
            }

            var i = 0;
            var first = true;
            while (i < lines.Count)
            {
                var line = lines[i];
                if (IsBlank(line))
                {
                    i++;
                    continue;
                }

                var unwrap = tightFirst && first;
                first = false;
                if (TryFence(line, out var fence, out var fenceLength, out var info))
                {
                    i = Fence(lines, i, fence, fenceLength, info, o);
                }
                else if (TryHeading(line, out var level, out var heading))
                {
                    var tag = Math.Clamp(level + options.HeadingOffset, 1, 6);
                    o.Append("<h").Append(tag).Append('>');
                    Inline(heading, o, 0);
                    o.Append("</h").Append(tag).Append(">\n");
                    i++;
                }
                else if (IsRule(line))
                {
                    o.Append("<hr>\n");
                    i++;
                }
                else if (IsQuote(line))
                {
                    i = Quote(lines, i, depth, o);
                }
                else if (TryItem(line, out _))
                {
                    i = List(lines, i, depth, o);
                }
                else if (i + 1 < lines.Count && IsTableStart(line, lines[i + 1]))
                {
                    i = Table(lines, i, o);
                }
                else
                {
                    i = Paragraph(lines, i, o, unwrap);
                }
            }
        }

        private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

        private static int Indent(string line)
        {
            var n = 0;
            while (n < line.Length && line[n] == ' ')
            {
                n++;
            }

            return n;
        }

        private static int RunLength(string s, int from, char c)
        {
            var n = 0;
            while (from + n < s.Length && s[from + n] == c)
            {
                n++;
            }

            return n;
        }

        private static bool TryFence(string line, out char fence, out int length, out string info)
        {
            fence = ' ';
            length = 0;
            info = string.Empty;
            var indent = Indent(line);
            if (indent > 3 || indent + 3 > line.Length || (line[indent] != '`' && line[indent] != '~'))
            {
                return false;
            }

            fence = line[indent];
            length = RunLength(line, indent, fence);
            info = line[(indent + length)..].Trim();
            return length >= 3 && !(fence == '`' && info.Contains('`', StringComparison.Ordinal));
        }

        private static int Fence(IReadOnlyList<string> lines, int i, char fence, int length, string info, StringBuilder o)
        {
            var fenceIndent = Indent(lines[i]);
            var language = info.Split(' ', 2)[0];
            o.Append("<pre><code");
            if (Language().IsMatch(language))
            {
                o.Append(" class=\"language-").Append(language).Append('"');
            }

            o.Append('>');
            i++;
            while (i < lines.Count)
            {
                var l = lines[i];
                i++;
                var indent = Indent(l);
                if (indent <= 3 && indent < l.Length && l[indent] == fence && RunLength(l, indent, fence) >= length && IsBlank(l[(indent + RunLength(l, indent, fence))..]))
                {
                    break;
                }

                Enc(l[Math.Min(fenceIndent, indent)..], o);
                o.Append('\n');
            }

            o.Append("</code></pre>\n");
            return i;
        }

        private static bool TryHeading(string line, out int level, out string text)
        {
            level = 0;
            text = string.Empty;
            var indent = Indent(line);
            if (indent > 3)
            {
                return false;
            }

            var t = line[indent..].TrimEnd();
            var n = RunLength(t, 0, '#');
            if (n is < 1 or > 6 || (n < t.Length && t[n] != ' '))
            {
                return false;
            }

            level = n;
            text = t[n..].Trim();
            var end = text.Length;
            while (end > 0 && text[end - 1] == '#')
            {
                end--;
            }

            if (end < text.Length && (end == 0 || text[end - 1] == ' '))
            {
                text = text[..end].TrimEnd();
            }

            return true;
        }

        private static bool IsRule(string line)
        {
            var t = line.Trim();
            if (t.Length < 3 || t[0] is not ('-' or '*' or '_'))
            {
                return false;
            }

            var count = 0;
            foreach (var c in t)
            {
                if (c == t[0])
                {
                    count++;
                }
                else if (c != ' ')
                {
                    return false;
                }
            }

            return count >= 3;
        }

        private static bool IsQuote(string line) => Indent(line) <= 3 && line.TrimStart().StartsWith('>');

        private static bool StartsBlock(string line) => TryFence(line, out _, out _, out _) || TryHeading(line, out _, out _) || IsRule(line) || IsQuote(line);

        private int Quote(IReadOnlyList<string> lines, int i, int depth, StringBuilder o)
        {
            var inner = new List<string>();
            while (i < lines.Count && IsQuote(lines[i]))
            {
                var t = lines[i].TrimStart()[1..];
                inner.Add(t.StartsWith(' ') ? t[1..] : t);
                i++;
            }

            o.Append("<blockquote>\n");
            Blocks(inner, depth + 1, o, tightFirst: false);
            o.Append("</blockquote>\n");
            return i;
        }

        private static bool TryItem(string line, out Item item)
        {
            item = default;
            var indent = Indent(line);
            if (indent >= line.Length)
            {
                return false;
            }

            var c = line[indent];
            var ordered = false;
            var number = 0;
            var marker = c;
            int width;
            if (c is '-' or '*' or '+')
            {
                width = 1;
            }
            else if (char.IsAsciiDigit(c))
            {
                var d = indent;
                while (d < line.Length && char.IsAsciiDigit(line[d]))
                {
                    d++;
                }

                if (d - indent > 9 || d >= line.Length || line[d] is not ('.' or ')'))
                {
                    return false;
                }

                ordered = true;
                marker = line[d];
                number = int.Parse(line.AsSpan(indent, d - indent), CultureInfo.InvariantCulture);
                width = d - indent + 1;
            }
            else
            {
                return false;
            }

            var after = indent + width;
            if (after < line.Length && line[after] != ' ')
            {
                return false;
            }

            var spaces = 0;
            while (after + spaces < line.Length && line[after + spaces] == ' ')
            {
                spaces++;
            }

            if (after + spaces >= line.Length || spaces > 4)
            {
                spaces = 1;
            }

            var text = after + spaces >= line.Length ? string.Empty : line[(after + spaces)..].TrimStart();
            item = new Item(indent, ordered, marker, number, after + spaces, text);
            return true;
        }

        private int List(IReadOnlyList<string> lines, int i, int depth, StringBuilder o)
        {
            TryItem(lines[i], out var head);
            var tag = head.Ordered ? "ol" : "ul";
            o.Append('<').Append(tag);
            if (head.Ordered && head.Number != 1)
            {
                o.Append(" start=\"").Append(head.Number.ToString(CultureInfo.InvariantCulture)).Append('"');
            }

            o.Append(">\n");
            var previousContent = head.ContentIndent;
            while (i < lines.Count && TryItem(lines[i], out var item) && item.Ordered == head.Ordered && item.Marker == head.Marker
                   && item.Indent >= head.Indent && item.Indent < previousContent)
            {
                var body = new List<string> { item.Text };
                i++;
                while (i < lines.Count)
                {
                    var l = lines[i];
                    if (IsBlank(l))
                    {
                        var j = i + 1;
                        while (j < lines.Count && IsBlank(lines[j]))
                        {
                            j++;
                        }

                        if (j < lines.Count && Indent(lines[j]) >= item.ContentIndent)
                        {
                            for (var k = i; k < j; k++)
                            {
                                body.Add(string.Empty);
                            }

                            i = j;
                            continue;
                        }

                        i = j;
                        break;
                    }

                    if (Indent(l) >= item.ContentIndent)
                    {
                        body.Add(l[Math.Min(item.ContentIndent, l.Length)..]);
                        i++;
                    }
                    else if (TryItem(l, out _) || StartsBlock(l))
                    {
                        break;
                    }
                    else
                    {
                        body.Add(l.TrimStart());
                        i++;
                    }
                }

                var inner = new StringBuilder();
                Blocks(body, depth + 1, inner, tightFirst: true);
                o.Append("<li>").Append(inner.ToString().TrimEnd()).Append("</li>\n");
                previousContent = item.ContentIndent;
            }

            o.Append("</").Append(tag).Append(">\n");
            return i;
        }

        private int Paragraph(IReadOnlyList<string> lines, int i, StringBuilder o, bool unwrap)
        {
            var parts = new List<string>();
            while (i < lines.Count && !IsBlank(lines[i]))
            {
                if (parts.Count > 0 && (StartsBlock(lines[i]) || (TryItem(lines[i], out var item) && (!item.Ordered || item.Number == 1))))
                {
                    break;
                }

                parts.Add(lines[i].TrimStart());
                i++;
            }

            if (!unwrap)
            {
                o.Append("<p>");
            }

            Inline(string.Join('\n', parts).TrimEnd(), o, 0);
            o.Append(unwrap ? "\n" : "</p>\n");
            return i;
        }

        // ---- Tablas ----

        private static List<string> Cells(string row)
        {
            var t = row.Trim();
            if (t.StartsWith('|'))
            {
                t = t[1..];
            }

            if (t.EndsWith('|') && !t.EndsWith("\\|", StringComparison.Ordinal))
            {
                t = t[..^1];
            }

            var cells = new List<string>();
            var cell = new StringBuilder();
            for (var i = 0; i < t.Length; i++)
            {
                if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] == '|')
                {
                    cell.Append('|');
                    i++;
                }
                else if (t[i] == '|')
                {
                    cells.Add(cell.ToString().Trim());
                    cell.Clear();
                }
                else
                {
                    cell.Append(t[i]);
                }
            }

            cells.Add(cell.ToString().Trim());
            return cells;
        }

        private static bool IsTableStart(string header, string delimiter)
        {
            if (!header.Contains('|', StringComparison.Ordinal) || !delimiter.Contains('|', StringComparison.Ordinal) || !delimiter.Contains('-', StringComparison.Ordinal))
            {
                return false;
            }

            var head = Cells(header);
            var cells = Cells(delimiter);
            return head.Count == cells.Count && cells.All(c => DelimiterCell().IsMatch(c));
        }

        private int Table(IReadOnlyList<string> lines, int i, StringBuilder o)
        {
            var head = Cells(lines[i]);
            var align = Cells(lines[i + 1]).Select(c => c.StartsWith(':') && c.EndsWith(':') ? "align-center" : c.EndsWith(':') ? "align-right" : null).ToList();
            var columns = Math.Min(head.Count, MaxColumns);
            o.Append("<table>\n<thead><tr>");
            for (var c = 0; c < columns; c++)
            {
                o.Append("<th scope=\"col\"").Append(Class(align[c])).Append('>');
                Inline(head[c], o, 1);
                o.Append("</th>");
            }

            o.Append("</tr></thead>\n<tbody>\n");
            i += 2;
            var rows = 0;
            while (i < lines.Count && !IsBlank(lines[i]) && lines[i].Contains('|', StringComparison.Ordinal) && rows < MaxRows)
            {
                var cells = Cells(lines[i]);
                o.Append("<tr>");
                for (var c = 0; c < columns; c++)
                {
                    o.Append("<td").Append(Class(align[c])).Append('>');
                    Inline(c < cells.Count ? cells[c] : string.Empty, o, 1);
                    o.Append("</td>");
                }

                o.Append("</tr>\n");
                i++;
                rows++;
            }

            o.Append("</tbody>\n</table>\n");
            return i;
        }

        private static string Class(string? name) => name is null ? string.Empty : $" class=\"{name}\"";

        // ---- En línea ----

        public void Inline(string s, StringBuilder o, int depth)
        {
            if (depth > MaxDepth)
            {
                Enc(s, o);
                return;
            }

            var state = new InlineState();
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '\\')
                {
                    if (i + 1 < s.Length && s[i + 1] == '\n')
                    {
                        o.Append("<br>\n");
                        i += 2;
                    }
                    else if (i + 1 < s.Length && char.IsAscii(s[i + 1]) && (char.IsPunctuation(s[i + 1]) || char.IsSymbol(s[i + 1])))
                    {
                        Enc(s[i + 1], o);
                        i += 2;
                    }
                    else
                    {
                        o.Append('\\');
                        i++;
                    }
                }
                else if (c == '`')
                {
                    i = CodeSpan(s, i, o, state);
                }
                else if (c == '!' && i + 1 < s.Length && s[i + 1] == '[' && TryLink(s, i + 1, true, o, depth, state, out var afterImage))
                {
                    i = afterImage;
                }
                else if (c == '[' && TryLink(s, i, false, o, depth, state, out var afterLink))
                {
                    i = afterLink;
                }
                else if (c == '<' && TryAutoLink(s, i, o, out var afterAuto))
                {
                    i = afterAuto;
                }
                else if (c is '*' or '_')
                {
                    i = Emphasis(s, i, o, depth, state);
                }
                else if (c == '\n')
                {
                    var spaces = 0;
                    while (spaces < o.Length && o[o.Length - 1 - spaces] == ' ')
                    {
                        spaces++;
                    }

                    o.Length -= spaces;
                    o.Append(spaces >= 2 ? "<br>\n" : "\n");
                    i++;
                }
                else
                {
                    Enc(c, o);
                    i++;
                }
            }
        }

        private static int FindBacktickRun(string s, int from, int length, InlineState state)
        {
            if (state.NoBacktick.TryGetValue(length, out var failed) && from >= failed)
            {
                return -1;
            }

            var j = from;
            while (j < s.Length)
            {
                if (s[j] != '`')
                {
                    j++;
                    continue;
                }

                var run = RunLength(s, j, '`');
                if (run == length)
                {
                    return j;
                }

                j += run;
            }

            state.NoBacktick[length] = from;
            return -1;
        }

        private static int CodeSpan(string s, int i, StringBuilder o, InlineState state)
        {
            var run = RunLength(s, i, '`');
            var close = FindBacktickRun(s, i + run, run, state);
            if (close < 0)
            {
                o.Append('`', run);
                return i + run;
            }

            var content = s[(i + run)..close].Replace('\n', ' ');
            if (content.Length >= 2 && content[0] == ' ' && content[^1] == ' ' && content.Trim().Length > 0)
            {
                content = content[1..^1];
            }

            o.Append("<code>");
            Enc(content, o);
            o.Append("</code>");
            return close + run;
        }

        private static bool IsWord(char c) => char.IsLetterOrDigit(c);

        private static int FindCloser(string s, int from, char c, int use, InlineState state)
        {
            if (state.NoCloser.TryGetValue((c, use), out var failed) && from >= failed)
            {
                return -1;
            }

            var j = from;
            while (j < s.Length)
            {
                var d = s[j];
                if (d == '\\')
                {
                    j += 2;
                }
                else if (d == '`')
                {
                    var run = RunLength(s, j, '`');
                    var close = FindBacktickRun(s, j + run, run, state);
                    j = close < 0 ? j + run : close + run;
                }
                else if (d == c)
                {
                    var run = RunLength(s, j, c);
                    var before = s[j - 1];
                    var after = j + run < s.Length ? s[j + run] : ' ';
                    if (j > from && run >= use && !char.IsWhiteSpace(before) && (c == '*' || !IsWord(after)) && (use > 1 || run == 1))
                    {
                        return j;
                    }

                    j += run;
                }
                else
                {
                    j++;
                }
            }

            state.NoCloser[(c, use)] = from;
            return -1;
        }

        private int Emphasis(string s, int i, StringBuilder o, int depth, InlineState state)
        {
            var c = s[i];
            var run = RunLength(s, i, c);
            var previous = i > 0 ? s[i - 1] : ' ';
            var next = i + run < s.Length ? s[i + run] : ' ';
            var use = Math.Min(run, 3);
            var close = !char.IsWhiteSpace(next) && (c == '*' || !IsWord(previous)) ? FindCloser(s, i + run, c, use, state) : -1;
            if (close < 0)
            {
                o.Append(c, run);
                return i + run;
            }

            o.Append(use switch { 1 => "<em>", 2 => "<strong>", _ => "<strong><em>" });
            Inline(s[(i + use)..close], o, depth + 1);
            o.Append(use switch { 1 => "</em>", 2 => "</strong>", _ => "</em></strong>" });
            return close + use;
        }

        private int MatchBracket(string s, int open, InlineState state)
        {
            var nesting = 0;
            var limit = Math.Min(s.Length, open + 1001);
            for (var j = open; j < limit; j++)
            {
                var c = s[j];
                if (c == '\\')
                {
                    j++;
                }
                else if (c == '`')
                {
                    var run = RunLength(s, j, '`');
                    var close = FindBacktickRun(s, j + run, run, state);
                    j = close < 0 ? j + run - 1 : close + run - 1;
                }
                else if (c == '[')
                {
                    nesting++;
                }
                else if (c == ']' && --nesting == 0)
                {
                    return j;
                }
            }

            return -1;
        }

        private static bool TryDestination(string s, int from, out string url, out string? title, out int end)
        {
            url = string.Empty;
            title = null;
            end = from;
            var j = from;
            while (j < s.Length && s[j] == ' ')
            {
                j++;
            }

            if (j >= s.Length)
            {
                return false;
            }

            if (s[j] == '<')
            {
                var k = s.IndexOf('>', j + 1, Math.Min(2048, s.Length - j - 1));
                if (k < 0)
                {
                    return false;
                }

                url = s[(j + 1)..k];
                j = k + 1;
            }
            else
            {
                var start = j;
                var parentheses = 0;
                while (j < s.Length && j - start < 2048 && s[j] is not (' ' or '\n' or '\t'))
                {
                    if (s[j] == '(')
                    {
                        parentheses++;
                    }
                    else if (s[j] == ')' && parentheses-- == 0)
                    {
                        break;
                    }

                    j++;
                }

                url = s[start..j];
            }

            while (j < s.Length && s[j] == ' ')
            {
                j++;
            }

            if (j < s.Length && s[j] is '"' or '\'')
            {
                var close = s.IndexOf(s[j], j + 1, Math.Min(300, s.Length - j - 1));
                if (close < 0)
                {
                    return false;
                }

                title = s[(j + 1)..close];
                j = close + 1;
                while (j < s.Length && s[j] == ' ')
                {
                    j++;
                }
            }

            if (j >= s.Length || s[j] != ')')
            {
                return false;
            }

            end = j + 1;
            return true;
        }

        private bool TryLink(string s, int open, bool image, StringBuilder o, int depth, InlineState state, out int next)
        {
            next = open;
            var close = MatchBracket(s, open, state);
            if (close < 0 || close + 1 >= s.Length || s[close + 1] != '(' || !TryDestination(s, close + 2, out var url, out var title, out var end))
            {
                return false;
            }

            var label = s[(open + 1)..close];
            next = end;
            if (image)
            {
                var alt = label.Replace("*", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
                if (SafeImage(url) is not { } src)
                {
                    Enc(alt, o);
                    return true;
                }

                o.Append("<img src=\"");
                Enc(src, o);
                o.Append("\" alt=\"");
                Enc(alt, o);
                o.Append("\" loading=\"lazy\" decoding=\"async\">");
                return true;
            }

            if (SafeLink(url) is not { } href)
            {
                Inline(label, o, depth + 1);
                return true;
            }

            OpenAnchor(href, title, o);
            Inline(label, o, depth + 1);
            o.Append("</a>");
            return true;
        }

        private static bool TryAutoLink(string s, int i, StringBuilder o, out int next)
        {
            next = i;
            var close = s.IndexOf('>', i + 1, Math.Min(300, s.Length - i - 1));
            if (close < 0)
            {
                return false;
            }

            var inner = s[(i + 1)..close];
            if (inner.Length == 0 || inner.Any(char.IsWhiteSpace) || inner.Contains('<', StringComparison.Ordinal))
            {
                return false;
            }

            var href = inner.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || inner.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? SafeLink(inner)
                : Email().IsMatch(inner) ? "mailto:" + inner
                : null;
            if (href is null)
            {
                return false;
            }

            OpenAnchor(href, null, o);
            Enc(inner, o);
            o.Append("</a>");
            next = close + 1;
            return true;
        }

        private static void OpenAnchor(string href, string? title, StringBuilder o)
        {
            o.Append("<a href=\"");
            Enc(href, o);
            o.Append('"');
            if (title is { Length: > 0 })
            {
                o.Append(" title=\"");
                Enc(title, o);
                o.Append('"');
            }

            if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                o.Append(" rel=\"noopener noreferrer\"");
            }

            o.Append('>');
        }

        // ---- Direcciones ----

        private static bool Clean(string url) =>
            url.Length is > 0 and <= 2048 && !url.Any(c => c < ' ' || c == '\u007f' || char.IsWhiteSpace(c) || c == '\\');

        private static string? SafeLink(string url)
        {
            if (!Clean(url))
            {
                return null;
            }

            if (url[0] == '#')
            {
                return url;
            }

            if (url[0] == '/')
            {
                return url.Length > 1 && url[1] == '/' ? null : url;
            }

            var colon = url.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                return null;
            }

            return url[..colon].ToLowerInvariant() switch
            {
                "http" or "https" => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? url : null,
                "mailto" or "tel" => url.Length <= 200 ? url : null,
                _ => null,
            };
        }

        private string? SafeImage(string url)
        {
            if (!Clean(url))
            {
                return null;
            }

            if (url[0] == '/')
            {
                return url.Length > 1 && url[1] == '/' ? null : url;
            }

            return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0
                && imageHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))
                ? url
                : null;
        }

        // ---- Codificación ----

        private static void Enc(string s, StringBuilder o)
        {
            foreach (var c in s)
            {
                Enc(c, o);
            }
        }

        private static void Enc(char c, StringBuilder o)
        {
            switch (c)
            {
                case '&':
                    o.Append("&amp;");
                    break;
                case '<':
                    o.Append("&lt;");
                    break;
                case '>':
                    o.Append("&gt;");
                    break;
                case '"':
                    o.Append("&quot;");
                    break;
                case '\'':
                    o.Append("&#39;");
                    break;
                default:
                    o.Append(c);
                    break;
            }
        }
    }
}
