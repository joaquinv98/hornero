using System.Globalization;
using BillingOss.Domain.Content;

namespace BillingOss.Importer.Import;

/// <summary>
/// La parte de contenidos para clientes de la importación (sección 11 de diseno/contenidos.md): base de conocimiento, novedades,
/// descargas y estado de la red. Lee la estructura y las filas de la copia. Los textos del proveedor son suyos y se importan, pero
/// el reporte solo cuenta: no reproduce ningún texto. Los IDs de artículos, categorías, novedades, descargas y eventos se conservan.
/// </summary>
public sealed partial class SourceImport
{
    /// <summary>
    /// Las tablas de contenidos de la copia cuya cantidad de filas se compara con la línea base. Cada fila se cuenta una vez, ya
    /// sea importada, unida como traducción de otra o descartada con su incidencia: ninguna se pierde en silencio.
    /// </summary>
    public static IReadOnlySet<string> ContentTables { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "tblknowledgebasecats", "tblknowledgebase", "tblknowledgebaselinks", "tblknowledgebasetags", "tblannouncements",
        "tbldownloadcats", "tbldownloads", "tblproduct_downloads", "tblnetworkissues",
    };

    /// <summary>Las imágenes de los artículos: el volcado trae solo los nombres, no los archivos.</summary>
    public const string ImagesTable = "tblknowledgebase_images";

    /// <summary>Sección de las cifras de filas leídas por tabla, y sección de las cifras de lo importado.</summary>
    public const string ContentRows = "content_rows";

    public const string ContentImported = "content_imported";

    private const string ContentAuthor = "importación";

    private static readonly string[] ContentSourceTables = [.. ContentTables, ImagesTable];

    /// <summary>
    /// El mayor ID de categoría del origen, de los dos árboles. Las categorías que arma el importador (donde caen los artículos y
    /// las descargas sin una válida) toman IDs por encima: la secuencia todavía no avanzó y chocarían con los importados.
    /// </summary>
    private long contentCategoryMax;

    private async Task Contents(CancellationToken ct)
    {
        contentCategoryMax = Rows("tblknowledgebasecats").Concat(Rows("tbldownloadcats")).Select(r => Long(r, "id")).DefaultIfEmpty(0).Max();
        var knowledgeCategories = await KnowledgeCategories(ct).ConfigureAwait(false);
        await KnowledgeArticles(knowledgeCategories, ct).ConfigureAwait(false);
        await Announcements(ct).ConfigureAwait(false);
        var downloadCategories = await DownloadCategories(knowledgeCategories, ct).ConfigureAwait(false);
        await Downloads(downloadCategories, ct).ConfigureAwait(false);
        await NetworkIssues(ct).ConfigureAwait(false);

        var images = Rows(ImagesTable).Count();
        if (images > 0)
        {
            Incidents.Add($"{images} imágenes de la base de conocimiento sin archivo (el volcado trae solo los nombres): cargalas en un host de Content:ImageHosts y corregí los enlaces.");
        }

        var summary = ContentSourceTables.Select(t => $"{t}={Rows(t).Count()}");
        await log.WriteLineAsync($"Contenidos: {string.Join(", ", summary)}").ConfigureAwait(false);
    }

    private static string Cut(string? text, int max, ref int cut)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length <= max)
        {
            return value;
        }

        cut++;
        return value[..max].TrimEnd();
    }

    /// <summary>El idioma de un texto del origen: vacío es el de base, inglés y español se reconocen, cualquier otro no se importa.</summary>
    private static string? ContentLanguage(string? source) =>
        string.IsNullOrWhiteSpace(source) ? ContentTexts.DefaultLanguage
        : source.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en"
        : source.StartsWith("es", StringComparison.OrdinalIgnoreCase) || source.StartsWith("sp", StringComparison.OrdinalIgnoreCase) ? ContentTexts.DefaultLanguage
        : null;

    private static string FreeSlug(HashSet<string> used, string? title)
    {
        var slug = Slugs.From(title);
        for (var n = 1; ; n++)
        {
            var candidate = Slugs.Numbered(slug, n);
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string TextsOf(IReadOnlyDictionary<string, ContentText> texts) => ContentTexts.Serialize(texts);

    /// <summary>
    /// Las categorías de la base de conocimiento. Las filas con <c>catid</c> son traducciones de otra categoría. Se guardan
    /// por nivel, las madres primero, porque la clave foránea de la madre es inmediata.
    /// </summary>
    private async Task<HashSet<long>> KnowledgeCategories(CancellationToken ct)
    {
        var all = Rows("tblknowledgebasecats").ToList();
        var bases = all.Where(r => Long(r, "catid") == 0).ToDictionary(r => Long(r, "id"));
        var texts = bases.ToDictionary(b => b.Key, b => new Dictionary<string, ContentText>());
        var cut = 0;
        foreach (var r in all)
        {
            Count(ContentRows, "tblknowledgebasecats");
            var id = Long(r, "id");
            var owner = Long(r, "catid") == 0 ? id : Long(r, "catid");
            if (!texts.TryGetValue(owner, out var own))
            {
                Incidents.Add($"Categoría de la base de conocimiento {id}: es traducción de la {owner}, que no existe.");
                continue;
            }

            if (ContentLanguage(Str(r, "language")) is not { } language)
            {
                Incidents.Add($"Categoría de la base de conocimiento {id}: idioma sin soporte, no se importa ese texto.");
                continue;
            }

            if (own.ContainsKey(language) || Blank(Str(r, "name")) is not { } name)
            {
                Incidents.Add($"Categoría de la base de conocimiento {id}: texto repetido o sin nombre, no se importa.");
                continue;
            }

            own[language] = new ContentText(Cut(name, 100, ref cut), Blank(HtmlMarkdown.ToText(Str(r, "description"), 500)));
        }

        var kept = new HashSet<long>();
        var parents = bases.ToDictionary(b => b.Key, b => Long(b.Value, "parentid"));
        int Depth(long id, int guard = 0) => guard > 10 || !parents.TryGetValue(id, out var parent) || parent == 0 || !parents.ContainsKey(parent) ? 1 : 1 + Depth(parent, guard + 1);
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var deep = 0;
        foreach (var level in bases.Keys.GroupBy(id => Depth(id)).OrderBy(g => g.Key))
        {
            foreach (var id in level.Order())
            {
                if (texts[id].Count == 0)
                {
                    Incidents.Add($"Categoría de la base de conocimiento {id}: sin ningún texto, no se importa.");
                    continue;
                }

                var parent = parents[id];
                var category = new ContentCategory(CategoryKind.KnowledgeBase, FreeSlug(slugs, ContentTexts.Pick(texts[id], ContentTexts.DefaultLanguage)?.Title), now)
                {
                    ParentId = parent != 0 && kept.Contains(parent) ? parent : null,
                    Hidden = Str(bases[id], "hidden") is "on" or "1",
                    Texts = TextsOf(texts[id]),
                };
                if (parent != 0 && category.ParentId is null)
                {
                    Incidents.Add($"Categoría de la base de conocimiento {id}: su madre {parent} no existe o no se importó, queda de primer nivel.");
                }

                if (level.Key > ContentCategory.MaxDepth)
                {
                    deep++;
                }

                category.SetImportedId(id);
                db.Add(category);
                kept.Add(id);
            }

            await Flush(ct).ConfigureAwait(false);
        }

        Figures[(ContentImported, "categorías de la base de conocimiento")] = (kept.Count, 0m);
        if (cut > 0)
        {
            Incidents.Add($"{cut} nombres de categorías pasaban los 100 caracteres: se cortaron.");
        }

        if (deep > 0)
        {
            Incidents.Add($"{deep} categorías de la base de conocimiento están a más de {ContentCategory.MaxDepth} niveles: se importan igual, pero el panel no deja crear más abajo.");
        }

        return kept;
    }

    /// <summary>
    /// Los artículos con una versión por idioma. Las filas con <c>parentid</c> son traducciones. Cada artículo tiene una sola
    /// categoría: si el origen trae más, queda en la primera. Las etiquetas alimentan la búsqueda. El cuerpo HTML pasa al Markdown acotado.
    /// </summary>
    private async Task KnowledgeArticles(HashSet<long> categories, CancellationToken ct)
    {
        var all = Rows("tblknowledgebase").ToList();
        var bases = all.Where(r => Long(r, "parentid") == 0).ToDictionary(r => Long(r, "id"));
        var links = new Dictionary<long, List<long>>();
        foreach (var link in Rows("tblknowledgebaselinks").OrderBy(r => Long(r, "id")))
        {
            Count(ContentRows, "tblknowledgebaselinks");
            var articleId = Long(link, "articleid");
            if (!bases.ContainsKey(articleId) || !categories.Contains(Long(link, "categoryid")))
            {
                Incidents.Add($"Enlace {Long(link, "id")} de la base de conocimiento: apunta a un artículo o una categoría que no se importó, se descarta.");
                continue;
            }

            (links.TryGetValue(articleId, out var list) ? list : links[articleId] = []).Add(Long(link, "categoryid"));
        }

        var tags = new Dictionary<long, List<string>>();
        var longTags = 0;
        foreach (var tag in Rows("tblknowledgebasetags"))
        {
            Count(ContentRows, "tblknowledgebasetags");
            var articleId = Long(tag, "articleid");
            var text = Blank(Str(tag, "tag"));
            if (text is null || !bases.ContainsKey(articleId))
            {
                continue;
            }

            if (text.Length > 40)
            {
                longTags++;
                continue;
            }

            var list = tags.TryGetValue(articleId, out var existing) ? existing : tags[articleId] = [];
            if (list.Count < 20 && !list.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(text);
            }
        }

        var maxId = bases.Keys.DefaultIfEmpty(0).Max();
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var texts = new List<ArticleText>();
        var withTexts = new Dictionary<long, HashSet<string>>();
        long? fallback = null;
        var cut = 0;
        var empty = 0;
        var extraLinks = 0;
        var images = 0;
        foreach (var (id, r) in bases.OrderBy(b => b.Key))
        {
            Count(ContentRows, "tblknowledgebase");
            if (links.GetValueOrDefault(id) is not { Count: > 0 } own)
            {
                fallback ??= await FallbackCategory(CategoryKind.KnowledgeBase, ct).ConfigureAwait(false);
                own = [fallback.Value];
                Incidents.Add($"Artículo {id}: sin categoría en el origen, queda en la categoría «Importados».");
            }
            else if (own.Count > 1)
            {
                extraLinks++;
            }

            if (ContentLanguage(Str(r, "language")) is not { } language)
            {
                Incidents.Add($"Artículo {id}: idioma sin soporte, no se importa.");
                continue;
            }

            var body = HtmlMarkdown.Convert(Str(r, "article"));
            images += body.Images;
            var hasBody = body.Markdown.Length > 0;
            var title = Blank(Str(r, "title")) ?? $"Artículo {id}";
            var votes = Math.Max(0, Long(r, "votes"));
            var helpful = Math.Clamp(Long(r, "useful"), 0, votes);
            var article = new Article
            {
                CategoryId = own[0],
                Slug = FreeSlug(slugs, title),
                Status = hasBody ? PublishState.Published : PublishState.Draft,
                SortOrder = (int)Math.Clamp(Long(r, "order"), 0, 100_000),
                Audience = Str(r, "private") is "on" or "1" ? ContentAudience.Customers : ContentAudience.Public,
                Views = Math.Max(0, Long(r, "views")),
                Helpful = helpful,
                Unhelpful = votes - helpful,
                CreatedAt = now,
                UpdatedAt = now,
                PublishedAt = hasBody ? now.AddSeconds(id - maxId) : null,
            };
            if (!hasBody)
            {
                empty++;
            }

            article.SetImportedId(id);
            db.Add(article);
            withTexts[id] = [language];
            texts.Add(new ArticleText { ArticleId = id, Language = language, Title = Cut(title, 200, ref cut), Body = body.Markdown, Tags = string.Join(", ", tags.GetValueOrDefault(id) ?? []), UpdatedAt = now });
        }

        await Flush(ct).ConfigureAwait(false);

        // Las traducciones se suman al artículo de origen.
        var translated = 0;
        foreach (var r in all.Where(r => Long(r, "parentid") != 0))
        {
            Count(ContentRows, "tblknowledgebase");
            var parent = Long(r, "parentid");
            var id = Long(r, "id");
            if (!withTexts.TryGetValue(parent, out var languages))
            {
                Incidents.Add($"Artículo {id}: es traducción del {parent}, que no existe o no se importó.");
                continue;
            }

            if (ContentLanguage(Str(r, "language")) is not { } language || !languages.Add(language))
            {
                Incidents.Add($"Artículo {id}: idioma sin soporte o repetido para el artículo {parent}, no se importa ese texto.");
                continue;
            }

            var body = HtmlMarkdown.Convert(Str(r, "article"));
            images += body.Images;
            texts.Add(new ArticleText { ArticleId = parent, Language = language, Title = Cut(Blank(Str(r, "title")) ?? $"Artículo {parent}", 200, ref cut), Body = body.Markdown, Tags = string.Join(", ", tags.GetValueOrDefault(parent) ?? []), UpdatedAt = now });
            translated++;
        }

        db.AddRange(texts);
        await Flush(ct).ConfigureAwait(false);

        Figures[(ContentImported, "artículos de la base de conocimiento")] = (bases.Count, 0m);
        Figures[(ContentImported, "traducciones de artículos")] = (translated, 0m);
        if (extraLinks > 0)
        {
            Incidents.Add($"{extraLinks} artículos tenían más de una categoría en el origen: quedan en la primera, el modelo propio admite una sola.");
        }

        if (empty > 0)
        {
            Incidents.Add($"{empty} artículos no tienen cuerpo: quedan en borrador.");
        }

        if (longTags > 0)
        {
            Incidents.Add($"{longTags} etiquetas pasaban los 40 caracteres: no se importan.");
        }

        if (cut > 0)
        {
            Incidents.Add($"{cut} títulos pasaban el máximo: se cortaron.");
        }

        if (images > 0)
        {
            Incidents.Add($"Los artículos enlazan {images} imágenes: se muestran con su texto alternativo hasta que se carguen en un host de Content:ImageHosts.");
        }
    }

    /// <summary>La categoría donde caen los artículos o las descargas del origen que no tienen una válida.</summary>
    private async Task<long> FallbackCategory(CategoryKind kind, CancellationToken ct)
    {
        var category = new ContentCategory(kind, "importados", now)
        {
            Texts = TextsOf(new Dictionary<string, ContentText> { [ContentTexts.DefaultLanguage] = new("Importados"), ["en"] = new("Imported") }),
        };
        category.SetImportedId(++contentCategoryMax);
        db.Add(category);
        await Flush(ct).ConfigureAwait(false);
        return category.Id;
    }

    private async Task Announcements(CancellationToken ct)
    {
        var all = Rows("tblannouncements").ToList();
        var bases = all.Where(r => Long(r, "parentid") == 0).ToDictionary(r => Long(r, "id"));
        var texts = new Dictionary<long, Dictionary<string, ContentText>>();
        var cut = 0;
        var translated = 0;
        foreach (var r in all.OrderBy(r => Long(r, "parentid") == 0 ? 0 : 1).ThenBy(r => Long(r, "id")))
        {
            Count(ContentRows, "tblannouncements");
            var id = Long(r, "id");
            var owner = Long(r, "parentid") == 0 ? id : Long(r, "parentid");
            if (!bases.ContainsKey(owner))
            {
                Incidents.Add($"Novedad {id}: es traducción de la {owner}, que no existe.");
                continue;
            }

            var own = texts.TryGetValue(owner, out var existing) ? existing : texts[owner] = [];
            if (ContentLanguage(Str(r, "language")) is not { } language || own.ContainsKey(language) || Blank(Str(r, "title")) is not { } title)
            {
                Incidents.Add($"Novedad {id}: idioma sin soporte, repetido o sin título, no se importa ese texto.");
                continue;
            }

            own[language] = new ContentText(Cut(title, 200, ref cut), null, HtmlMarkdown.ToMarkdown(Str(r, "announcement")));
            if (owner != id)
            {
                translated++;
            }
        }

        var drafts = 0;
        foreach (var (id, r) in bases.OrderBy(b => b.Key))
        {
            if (!texts.TryGetValue(id, out var own) || own.Count == 0)
            {
                Incidents.Add($"Novedad {id}: sin ningún texto, no se importa.");
                continue;
            }

            var published = Long(r, "published") != 0 && own.Values.Any(t => !string.IsNullOrWhiteSpace(t.Body));
            if (!published)
            {
                drafts++;
            }

            var date = Instant(DateTimeValue(r, "date"));
            var announcement = new Announcement
            {
                Status = published ? PublishState.Published : PublishState.Draft,
                PublishAt = published ? date ?? now : null,
                Audience = ContentAudience.Public,
                Texts = TextsOf(own),
                CreatedAt = date ?? now,
                UpdatedAt = now,
            };
            announcement.SetImportedId(id);
            db.Add(announcement);
        }

        await Flush(ct).ConfigureAwait(false);
        Figures[(ContentImported, "novedades")] = (bases.Count, 0m);
        Figures[(ContentImported, "traducciones de novedades")] = (translated, 0m);
        if (drafts > 0)
        {
            Incidents.Add($"{drafts} novedades quedan en borrador: no estaban publicadas en el origen o no tienen cuerpo.");
        }

        if (cut > 0)
        {
            Incidents.Add($"{cut} títulos de novedades pasaban los 200 caracteres: se cortaron.");
        }
    }

    /// <summary>
    /// Las categorías de las descargas. Comparten tabla y numeración con las de la base de conocimiento, y los dos orígenes numeran
    /// por su cuenta: una categoría de descargas conserva su ID si está libre y si no toma uno nuevo, por encima de todos los del
    /// origen. Devuelve el ID del origen y el que quedó.
    /// </summary>
    private async Task<Dictionary<long, long>> DownloadCategories(HashSet<long> taken, CancellationToken ct)
    {
        var all = Rows("tbldownloadcats").ToList();
        var ids = all.Select(r => Long(r, "id")).ToHashSet();
        var parents = all.ToDictionary(r => Long(r, "id"), r => Long(r, "parentid"));
        int Depth(long id, int guard = 0) => guard > 10 || !parents.TryGetValue(id, out var parent) || parent == 0 || !ids.Contains(parent) ? 1 : 1 + Depth(parent, guard + 1);
        var kept = new Dictionary<long, long>();
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var cut = 0;
        var renumbered = 0;
        foreach (var level in all.GroupBy(r => Depth(Long(r, "id"))).OrderBy(g => g.Key))
        {
            foreach (var r in level.OrderBy(r => Long(r, "id")))
            {
                Count(ContentRows, "tbldownloadcats");
                var id = Long(r, "id");
                if (Blank(Str(r, "name")) is not { } name)
                {
                    Incidents.Add($"Categoría de descargas {id}: sin nombre, no se importa.");
                    continue;
                }

                var parent = Long(r, "parentid");
                var category = new ContentCategory(CategoryKind.Downloads, FreeSlug(slugs, name), now)
                {
                    ParentId = parent != 0 && kept.TryGetValue(parent, out var mapped) ? mapped : null,
                    Hidden = Long(r, "hidden") != 0,
                    Texts = TextsOf(new Dictionary<string, ContentText> { [ContentTexts.DefaultLanguage] = new(Cut(name, 100, ref cut), Blank(HtmlMarkdown.ToText(Str(r, "description"), 500))) }),
                };
                if (parent != 0 && category.ParentId is null)
                {
                    Incidents.Add($"Categoría de descargas {id}: su madre {parent} no existe o no se importó, queda de primer nivel.");
                }

                var target = taken.Contains(id) ? ++contentCategoryMax : id;
                if (target != id)
                {
                    renumbered++;
                }

                category.SetImportedId(target);
                db.Add(category);
                kept[id] = target;
            }

            await Flush(ct).ConfigureAwait(false);
        }

        Figures[(ContentImported, "categorías de descargas")] = (kept.Count, 0m);
        if (renumbered > 0)
        {
            Incidents.Add($"{renumbered} categorías de descargas tomaron un ID nuevo porque el del origen ya era de una categoría de la base de conocimiento.");
        }

        return kept;
    }

    /// <summary>
    /// Las descargas quedan en borrador y sin archivo: el volcado no trae los bytes. Una descarga con productos asociados es dirigida
    /// a esos productos, una solo para clientes va a los clientes y el resto es pública. El contador se conserva.
    /// </summary>
    private async Task Downloads(Dictionary<long, long> categories, CancellationToken ct)
    {
        var byDownload = new Dictionary<long, List<long>>();
        var dropped = 0;
        foreach (var link in Rows("tblproduct_downloads"))
        {
            Count(ContentRows, "tblproduct_downloads");
            var downloadId = Long(link, "download_id");
            var productId = Long(link, "product_id");
            if (!products.Contains(productId))
            {
                dropped++;
                continue;
            }

            (byDownload.TryGetValue(downloadId, out var list) ? list : byDownload[downloadId] = []).Add(productId);
        }

        long? fallback = null;
        var slugless = 0;
        var cut = 0;
        foreach (var r in Rows("tbldownloads").OrderBy(r => Long(r, "id")))
        {
            Count(ContentRows, "tbldownloads");
            var id = Long(r, "id");
            if (!categories.TryGetValue(Long(r, "category"), out var categoryId))
            {
                fallback ??= await FallbackCategory(CategoryKind.Downloads, ct).ConfigureAwait(false);
                categoryId = fallback.Value;
                slugless++;
            }

            var linked = byDownload.GetValueOrDefault(id)?.Distinct().Order().ToArray() ?? [];
            var download = new Download
            {
                CategoryId = categoryId,
                Status = PublishState.Draft,
                Audience = linked.Length > 0 ? ContentAudience.Targeted : Long(r, "clientsonly") != 0 ? ContentAudience.Customers : ContentAudience.Public,
                ProductIds = linked,
                DownloadCount = Math.Max(0, Long(r, "downloads")),
                Texts = TextsOf(new Dictionary<string, ContentText> { [ContentTexts.DefaultLanguage] = new(Cut(Str(r, "title") is { Length: > 0 } title ? title : $"Descarga {id}", 200, ref cut), Blank(HtmlMarkdown.ToText(Str(r, "description"), 2000))) }),
                CreatedAt = now,
                UpdatedAt = now,
            };
            download.SetImportedId(id);
            db.Add(download);
        }

        await Flush(ct).ConfigureAwait(false);
        var total = Rows("tbldownloads").Count();
        Figures[(ContentImported, "descargas")] = (total, 0m);
        if (total > 0)
        {
            Incidents.Add($"{total} descargas quedan en borrador y sin archivo (el volcado no trae los bytes): subí el archivo de cada una desde el panel y publicala.");
        }

        if (slugless > 0)
        {
            Incidents.Add($"{slugless} descargas tenían una categoría que no existe: quedan en la categoría «Importados».");
        }

        if (dropped > 0)
        {
            Incidents.Add($"{dropped} vínculos de descargas con productos que no se importaron: se descartan.");
        }

        if (cut > 0)
        {
            Incidents.Add($"{cut} títulos de descargas pasaban los 200 caracteres: se cortaron.");
        }
    }

    /// <summary>
    /// Los incidentes y mantenimientos. Un componente por servidor o, si no hay servidor, por el texto de lo afectado: el staff los
    /// renombra después. <c>Scheduled</c> es un mantenimiento y <c>Resolved</c> un incidente resuelto. Lo demás queda abierto.
    /// </summary>
    private async Task NetworkIssues(CancellationToken ct)
    {
        var issues = Rows("tblnetworkissues").OrderBy(r => Long(r, "id")).ToList();
        if (issues.Count == 0)
        {
            return;
        }

        var components = new Dictionary<string, StatusComponent>(StringComparer.Ordinal);
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        string KeyOf(DumpRow r) => Long(r, "server") > 0 ? "s" + Long(r, "server").ToString(CultureInfo.InvariantCulture) : Blank(Str(r, "affecting")) is { } text ? "t" + text.ToLowerInvariant() : "x";
        var cut = 0;
        foreach (var r in issues)
        {
            var key = KeyOf(r);
            if (components.ContainsKey(key))
            {
                continue;
            }

            var name = key[0] switch
            {
                's' => $"Servidor {Long(r, "server")}",
                't' => Cut(Str(r, "affecting"), 100, ref cut),
                _ => "Sin especificar",
            };
            components[key] = new StatusComponent
            {
                Kind = Str(r, "type") == "Server" ? ComponentKind.Server : ComponentKind.Service,
                Slug = FreeSlug(slugs, name),
                SortOrder = components.Count,
                Texts = TextsOf(new Dictionary<string, ContentText> { [ContentTexts.DefaultLanguage] = new(name) }),
                CreatedAt = now,
                UpdatedAt = now,
            };
        }

        db.AddRange(components.Values);
        await Flush(ct).ConfigureAwait(false);

        var open = 0;
        var fixedWindow = 0;
        foreach (var r in issues)
        {
            Count(ContentRows, "tblnetworkissues");
            var id = Long(r, "id");
            var maintenance = Str(r, "status") == "Scheduled";
            var starts = Instant(DateTimeValue(r, "startdate")) ?? now;
            var ends = Instant(DateTimeValue(r, "enddate"));
            var updated = Instant(DateTimeValue(r, "lastupdate")) ?? starts;
            var e = new StatusEvent
            {
                Kind = maintenance ? StatusEventKind.Maintenance : StatusEventKind.Incident,
                StartsAt = starts,
                Texts = TextsOf(new Dictionary<string, ContentText> { [ContentTexts.DefaultLanguage] = new(Cut(Blank(Str(r, "title")) ?? $"Evento {id}", 200, ref cut)) }),
                CreatedAt = now,
                UpdatedAt = now,
            };
            if (maintenance)
            {
                if (ends is not { } end || end <= starts)
                {
                    ends = starts.AddHours(1);
                    fixedWindow++;
                }

                e.EndsAt = ends;
                e.State = ends <= now ? StatusEventState.Completed : StatusEventState.Scheduled;
            }
            else if (Str(r, "status") == "Resolved")
            {
                e.State = StatusEventState.Resolved;
                e.EndsAt = new[] { ends ?? updated, starts }.Max();
            }
            else
            {
                e.State = Str(r, "status") == "In Progress" ? StatusEventState.Identified : StatusEventState.Investigating;
                open++;
            }

            var component = components[KeyOf(r)];
            var impact = maintenance ? ComponentStatus.Maintenance
                : Str(r, "status") == "Outage" || Str(r, "priority") == "Critical" ? ComponentStatus.MajorOutage
                : Str(r, "priority") == "High" ? ComponentStatus.PartialOutage
                : ComponentStatus.Degraded;
            e.SetImportedId(id);
            db.Add(e);
            await Flush(ct).ConfigureAwait(false);

            db.Add(new StatusEventComponent { EventId = id, ComponentId = component.Id, Impact = impact });
            if (HtmlMarkdown.ToMarkdown(Str(r, "description")) is { Length: > 0 } message)
            {
                db.Add(new StatusUpdate
                {
                    EventId = id,
                    State = e.State,
                    PostedAt = new[] { updated, starts }.Max(),
                    Texts = TextsOf(new Dictionary<string, ContentText> { [ContentTexts.DefaultLanguage] = new(string.Empty, null, message) }),
                    Author = ContentAuthor,
                });
            }

            await Flush(ct).ConfigureAwait(false);
        }

        Figures[(ContentImported, "componentes de la red")] = (components.Count, 0m);
        Figures[(ContentImported, "incidentes y mantenimientos")] = (issues.Count, 0m);
        Incidents.Add($"{components.Count} componentes de la red creados a partir del servidor o del texto de lo afectado: renombralos desde el panel.");
        if (open > 0)
        {
            Incidents.Add($"{open} incidentes del origen siguen abiertos y se importan abiertos: la página de estado los muestra hasta que se resuelvan. Revisalos antes del corte.");
        }

        if (fixedWindow > 0)
        {
            Incidents.Add($"{fixedWindow} mantenimientos sin horario de fin válido: quedan de una hora.");
        }

        if (cut > 0)
        {
            Incidents.Add($"{cut} títulos o nombres de la red pasaban el máximo: se cortaron.");
        }
    }
}
