using System.Text;
using BillingOss.App.Persistence;
using BillingOss.Domain.Content;
using BillingOss.Importer.Import;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BillingOss.Importer.Tests;

/// <summary>
/// Importación de la base de conocimiento, las novedades, las descargas y el estado de la red desde un origen sintético
/// (sección 11 de diseno/contenidos.md). Los textos son inventados: nada de clientes ni del proveedor.
/// </summary>
[TestClass]
public sealed class ContentImportTests
{
    private const string Dump = """
        CREATE TABLE `tblcurrencies` (`id` int(11), `code` text, `default` int(11));
        CREATE TABLE `tblproductgroups` (`id` int(11), `name` text);
        CREATE TABLE `tblproducts` (`id` int(11), `gid` int(11), `name` text, `hidden` tinyint(1), `retired` tinyint(1), `paytype` text, `servertype` text, `tax` tinyint(1), `configoption1` text, `autoterminatedays` int(11));
        INSERT INTO `tblcurrencies` VALUES (3,'ARS',1);
        INSERT INTO `tblproductgroups` VALUES (7,'Game servers');
        INSERT INTO `tblproducts` VALUES (40,7,'Minecraft 4 GB',0,0,'recurring','',1,'',0);

        CREATE TABLE `tblknowledgebasecats` (`id` int(11), `parentid` int(11), `name` text, `description` text, `hidden` text, `catid` int(11), `language` text);
        INSERT INTO `tblknowledgebasecats` VALUES
          (1,0,'Hosting','<p>Todo sobre <b>hosting</b></p>','',0,''),
          (2,1,'Correo','','',0,''),
          (3,0,'Interno','','on',0,''),
          (4,0,'Hosting guides','Hosting guides','',1,'english'),
          (5,0,'Huérfana','','',99,'english'),
          (6,5,'Con madre ausente','','',0,'');
        CREATE TABLE `tblknowledgebase` (`id` int(11), `title` text, `article` text, `views` int(11), `useful` int(11), `votes` int(11), `private` text, `order` int(11), `parentid` int(11), `language` text);
        INSERT INTO `tblknowledgebase` VALUES
          (10,'Configurar el correo','<h2>Pasos</h2><p>Entrá a <a href="https://example.com/x">Webmail</a>.</p><ul><li>Uno</li><li>Dos <b>negrita</b></li></ul><p><img src="/a.png" alt="logo"></p>',120,8,10,'',2,0,''),
          (11,'Facturas privadas','<p>Solo clientes</p>',5,0,0,'on',1,0,''),
          (12,'Mail setup','<p>Open Webmail</p>',0,0,0,'',0,10,'english'),
          (13,'Sin cuerpo','',0,0,0,'',0,0,''),
          (14,'Traducción huérfana','<p>x</p>',0,0,0,'',0,999,'english'),
          (15,'Sin categoría','<p>algo</p>',0,0,0,'',0,0,''),
          (16,'Configurar el correo','<p>Otro con el mismo título</p>',0,3,1,'',0,0,'');
        CREATE TABLE `tblknowledgebaselinks` (`id` int(11), `categoryid` int(11), `articleid` int(11));
        INSERT INTO `tblknowledgebaselinks` VALUES (1,2,10),(2,1,10),(3,1,11),(4,1,13),(5,99,11),(6,2,16);
        CREATE TABLE `tblknowledgebasetags` (`id` int(11), `articleid` int(11), `tag` varchar(64));
        INSERT INTO `tblknowledgebasetags` VALUES (1,10,'correo'),(2,10,'imap'),(3,10,'Correo'),(4,11,'facturas'),(5,10,'una etiqueta demasiado larga para entrar en cuarenta caracteres'),(6,999,'huérfana');
        CREATE TABLE `tblknowledgebase_images` (`id` int(10), `filename` varchar(128), `original_name` varchar(255));
        INSERT INTO `tblknowledgebase_images` VALUES (1,'a.png','a.png'),(2,'b.png','b.png');

        CREATE TABLE `tblannouncements` (`id` int(11), `date` datetime, `title` text, `announcement` text, `published` tinyint(1), `parentid` int(11), `language` text);
        INSERT INTO `tblannouncements` VALUES
          (20,'2026-10-01 09:00:00','Mantenimiento','<p>Habrá un <b>corte</b></p>',1,0,''),
          (21,'2026-10-02 09:00:00','Maintenance','<p>An outage</p>',1,20,'english'),
          (22,'2026-10-03 09:00:00','Borrador','<p>x</p>',0,0,''),
          (23,'2026-10-04 09:00:00','Sin cuerpo','',1,0,'');

        CREATE TABLE `tbldownloadcats` (`id` int(11), `parentid` int(11), `name` text, `description` text, `hidden` tinyint(1));
        INSERT INTO `tbldownloadcats` VALUES (1,0,'Agentes','<p>Instaladores</p>',0),(2,1,'Windows','',1);
        CREATE TABLE `tbldownloads` (`id` int(11), `category` int(11), `type` text, `title` text, `description` text, `downloads` int(11), `location` text, `clientsonly` tinyint(1), `hidden` tinyint(1), `productdownload` tinyint(1));
        INSERT INTO `tbldownloads` VALUES
          (30,1,'zip','Agente','<p>Instalador del <b>agente</b></p>',42,'agente.zip',1,0,0),
          (31,2,'zip','Solo producto','',7,'p.zip',0,0,1),
          (32,77,'zip','Sin categoría','',0,'x.zip',0,0,0);
        CREATE TABLE `tblproduct_downloads` (`id` int(10), `product_id` int(11), `download_id` int(11));
        INSERT INTO `tblproduct_downloads` VALUES (1,40,31),(2,999,30);

        CREATE TABLE `tblnetworkissues` (`id` int(10), `title` varchar(150), `description` text, `type` text, `affecting` varchar(100), `server` int(10), `priority` text, `startdate` datetime, `enddate` datetime, `status` text, `lastupdate` datetime);
        INSERT INTO `tblnetworkissues` VALUES
          (40,'Caída del nodo','<p>Estamos <b>revisando</b></p>','Server','',3,'Critical','2026-10-10 10:00:00','0000-00-00 00:00:00','Investigating','2026-10-10 10:30:00'),
          (41,'Corte resuelto','<p>Listo</p>','System','Panel de clientes',0,'Low','2026-09-01 10:00:00','2026-09-01 12:00:00','Resolved','2026-09-01 12:00:00'),
          (42,'Cambio de switch','','Other','Red',0,'Medium','2090-01-10 02:00:00','2090-01-10 04:00:00','Scheduled','2026-10-11 10:00:00'),
          (43,'Mantenimiento pasado','','Other','Red',0,'Medium','2026-01-10 02:00:00','0000-00-00 00:00:00','Scheduled','2026-01-01 00:00:00');
        """;

    private const string Baseline = """
        # section	keys...	values...
        table_rows	tblannouncements	4
        table_rows	tblknowledgebasetags	6
        table_rows	tblknowledgebase	7
        table_rows	tblknowledgebaselinks	6
        table_rows	tblknowledgebasecats	6
        table_rows	tbldownloadcats	2
        table_rows	tblproduct_downloads	2
        table_rows	tblknowledgebase_images	2
        table_rows	tblnetworkissues	4
        table_rows	tbldownloads	3
        """;

    [TestMethod]
    public async Task ImportaLaBaseDeConocimiento_ConIdsCategoriasTraduccionesYEtiquetas() => await WithImport(Dump, async (db, import, _) =>
    {
        var categories = await db.Set<ContentCategory>().Where(c => c.Kind == CategoryKind.KnowledgeBase).OrderBy(c => c.Id).ToListAsync();
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 6, 7 }, categories.Select(c => c.Id).ToArray(), "Conserva los IDs, y la categoría de reemplazo (la 7) toma uno por encima del mayor del origen.");
        var hosting = categories[0];
        Assert.AreEqual("hosting", hosting.Slug);
        var hostingTexts = ContentTexts.Parse(hosting.Texts);
        Assert.AreEqual("Hosting", hostingTexts["es-AR"].Title);
        Assert.AreEqual("Todo sobre hosting", hostingTexts["es-AR"].Summary);
        Assert.AreEqual("Hosting guides", hostingTexts["en"].Title, "La fila con catid es la traducción de la categoría.");
        Assert.AreEqual(1L, categories[1].ParentId, "Correo cuelga de Hosting.");
        Assert.IsTrue(categories[2].Hidden);
        Assert.IsNull(categories[3].ParentId, "La madre 5 no se importó: queda de primer nivel.");
        Assert.AreEqual("importados", categories[4].Slug);
        Assert.IsTrue(import.Incidents.Any(i => i.Contains("es traducción de la 99", StringComparison.Ordinal)), string.Join(" | ", import.Incidents));

        var articles = await db.Set<Article>().OrderBy(a => a.Id).ToDictionaryAsync(a => a.Id);
        CollectionAssert.AreEqual(new long[] { 10, 11, 13, 15, 16 }, articles.Keys.ToArray());
        var setup = articles[10];
        Assert.AreEqual(2L, setup.CategoryId, "Con dos categorías en el origen queda en la primera.");
        Assert.AreEqual("configurar-el-correo", setup.Slug);
        Assert.AreEqual("configurar-el-correo-2", articles[16].Slug, "El slug repetido se numera.");
        Assert.AreEqual(PublishState.Published, setup.Status);
        Assert.AreEqual(ContentAudience.Public, setup.Audience);
        Assert.AreEqual((120L, 8L, 2L), (setup.Views, setup.Helpful, setup.Unhelpful));
        Assert.AreEqual((1L, 0L), (articles[16].Helpful, articles[16].Unhelpful), "Más útiles que votos se acota a los votos.");
        Assert.AreEqual(ContentAudience.Customers, articles[11].Audience);
        Assert.AreEqual(1L, articles[11].CategoryId, "El enlace a una categoría que no existe se descarta.");
        Assert.AreEqual(PublishState.Draft, articles[13].Status, "Sin cuerpo queda en borrador.");
        Assert.AreEqual(7L, articles[15].CategoryId, "Sin categoría cae en la de reemplazo.");

        var texts = await db.Set<ArticleText>().Where(t => t.ArticleId == 10).OrderBy(t => t.Language).ToListAsync();
        CollectionAssert.AreEqual(new[] { "en", "es-AR" }, texts.Select(t => t.Language).ToArray());
        Assert.AreEqual("Mail setup", texts[0].Title);
        Assert.AreEqual("## Pasos\n\nEntrá a [Webmail](https://example.com/x).\n\n- Uno\n- Dos **negrita**\n\n![logo](/a.png)", texts[1].Body);
        Assert.AreEqual("correo, imap", texts[1].Tags, "Sin repetidas, y la que pasa los 40 caracteres no entra.");

        // Lo importado se ve con las reglas del sitio: el Markdown no deja HTML y la búsqueda encuentra el artículo.
        StringAssert.Contains(Markdown.ToHtml(texts[1].Body), "<strong>negrita</strong>");
        Assert.AreEqual(1, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM content.article_texts WHERE search @@ websearch_to_tsquery('spanish', 'webmail') AND article_id = 10 AND language = 'es-AR'").SingleAsync());
        Assert.IsTrue(import.Incidents.Any(i => i.Contains("más de una categoría", StringComparison.Ordinal)));
        Assert.IsTrue(import.Incidents.Any(i => i.Contains("imágenes", StringComparison.Ordinal)));
    });

    [TestMethod]
    public async Task ImportaNovedadesDescargasYEstadoDeLaRed() => await WithImport(Dump, async (db, import, _) =>
    {
        // Novedades: la publicada con su fecha en UTC y su traducción, el borrador, y la publicada sin cuerpo queda en borrador.
        var news = await db.Set<Announcement>().OrderBy(a => a.Id).ToDictionaryAsync(a => a.Id);
        CollectionAssert.AreEqual(new long[] { 20, 22, 23 }, news.Keys.ToArray());
        Assert.AreEqual(PublishState.Published, news[20].Status);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), news[20].PublishAt, "09:00 en Buenos Aires son las 12:00 UTC.");
        var newsTexts = ContentTexts.Parse(news[20].Texts);
        Assert.AreEqual("Habrá un **corte**", newsTexts["es-AR"].Body);
        Assert.AreEqual("Maintenance", newsTexts["en"].Title);
        Assert.AreEqual(ContentAudience.Public, news[20].Audience);
        Assert.AreEqual(PublishState.Draft, news[22].Status);
        Assert.AreEqual(PublishState.Draft, news[23].Status);

        // Descargas: categorías con su árbol, y las descargas en borrador y sin archivo, con su contador y su audiencia. Las dos
        // categorías de descargas tenían los IDs 1 y 2, que ya eran de la base de conocimiento: toman IDs nuevos por encima de todo.
        var downloadCategories = await db.Set<ContentCategory>().Where(c => c.Kind == CategoryKind.Downloads).OrderBy(c => c.Id).ToListAsync();
        CollectionAssert.AreEqual(new long[] { 8, 9, 10 }, downloadCategories.Select(c => c.Id).ToArray());
        Assert.AreEqual(8L, downloadCategories[1].ParentId, "Windows cuelga de Agentes con su ID nuevo.");
        Assert.IsTrue(import.Incidents.Any(i => i.Contains("2 categorías de descargas tomaron un ID nuevo", StringComparison.Ordinal)), string.Join(" | ", import.Incidents));
        Assert.IsTrue(downloadCategories[1].Hidden);
        var downloads = await db.Set<Download>().OrderBy(d => d.Id).ToDictionaryAsync(d => d.Id);
        Assert.IsTrue(downloads.Values.All(d => d.Status == PublishState.Draft && !d.HasFile));
        Assert.AreEqual(42L, downloads[30].DownloadCount);
        Assert.AreEqual(ContentAudience.Customers, downloads[30].Audience, "El vínculo con un producto que no se importó se descarta.");
        Assert.AreEqual("Instalador del agente", ContentTexts.Parse(downloads[30].Texts)["es-AR"].Summary);
        Assert.AreEqual(ContentAudience.Targeted, downloads[31].Audience);
        CollectionAssert.AreEqual(new long[] { 40 }, downloads[31].ProductIds);
        Assert.AreEqual(8L, downloads[30].CategoryId);
        Assert.AreEqual(9L, downloads[31].CategoryId);
        Assert.AreEqual(10L, downloads[32].CategoryId, "Una categoría que no existe cae en la de reemplazo.");
        Assert.IsTrue(import.Incidents.Any(i => i.Contains("3 descargas quedan en borrador y sin archivo", StringComparison.Ordinal)), string.Join(" | ", import.Incidents));

        // Estado de la red: un componente por servidor o por texto, y cada evento con su estado, su impacto y su mensaje.
        var components = await db.Set<StatusComponent>().OrderBy(c => c.Id).ToListAsync();
        CollectionAssert.AreEqual(new[] { "Servidor 3", "Panel de clientes", "Red" }, components.Select(c => ContentTexts.Parse(c.Texts)["es-AR"].Title).ToArray());
        Assert.AreEqual(ComponentKind.Server, components[0].Kind);
        Assert.AreEqual(ComponentKind.Service, components[1].Kind);
        var events = await db.Set<StatusEvent>().OrderBy(e => e.Id).ToDictionaryAsync(e => e.Id);
        CollectionAssert.AreEqual(new long[] { 40, 41, 42, 43 }, events.Keys.ToArray());
        Assert.AreEqual((StatusEventKind.Incident, StatusEventState.Investigating), (events[40].Kind, events[40].State));
        Assert.IsNull(events[40].EndsAt);
        Assert.AreEqual((StatusEventKind.Incident, StatusEventState.Resolved), (events[41].Kind, events[41].State));
        Assert.AreEqual(new DateTimeOffset(2026, 9, 1, 15, 0, 0, TimeSpan.Zero), events[41].EndsAt);
        Assert.AreEqual((StatusEventKind.Maintenance, StatusEventState.Scheduled), (events[42].Kind, events[42].State));
        Assert.AreEqual((StatusEventKind.Maintenance, StatusEventState.Completed), (events[43].Kind, events[43].State), "Un mantenimiento sin fin válido queda de una hora y, ya pasado, terminado.");
        Assert.AreEqual(events[43].StartsAt.AddHours(1), events[43].EndsAt);
        Assert.IsTrue(events.Values.All(e => StatusRules.Error(e) is null), "Cada evento cumple las reglas del dominio.");

        var impacts = await db.Set<StatusEventComponent>().ToDictionaryAsync(l => l.EventId, l => (l.ComponentId, l.Impact));
        Assert.AreEqual((components[0].Id, ComponentStatus.MajorOutage), impacts[40]);
        Assert.AreEqual((components[1].Id, ComponentStatus.Degraded), impacts[41]);
        Assert.AreEqual((components[2].Id, ComponentStatus.Maintenance), impacts[42]);
        var updates = await db.Set<StatusUpdate>().ToListAsync();
        Assert.AreEqual(2, updates.Count, "Solo los eventos con descripción traen una novedad.");
        Assert.AreEqual("Estamos **revisando**", ContentTexts.Parse(updates.Single(u => u.EventId == 40).Texts)["es-AR"].Body);
        Assert.IsTrue(updates.All(u => u.Author == "importación"));
        Assert.IsTrue(import.Incidents.Any(i => i.Contains("siguen abiertos", StringComparison.Ordinal)));

        // Las secuencias arrancan arriba de lo importado: lo que se crea después no choca.
        db.Add(new ContentCategory(CategoryKind.KnowledgeBase, "nueva", DateTimeOffset.UtcNow));
        db.Add(new StatusEvent { Kind = StatusEventKind.Incident, State = StatusEventState.Investigating, StartsAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        Assert.IsGreaterThan(10L, (await db.Set<ContentCategory>().SingleAsync(c => c.Slug == "nueva")).Id);
        Assert.IsGreaterThan(43L, await db.Set<StatusEvent>().MaxAsync(e => e.Id));
    });

    [TestMethod]
    public async Task LaConciliacionCuentaCadaFilaDeContenidos_YMarcaLasImagenesComoFaltantes() => await WithImport(Dump, async (_, import, baseline) =>
    {
        var output = new StringWriter();

        var diffs = await Reconciliation.Compare(import, baseline, output);

        var text = output.ToString();
        Assert.AreEqual(0, diffs, text);
        foreach (var table in SourceImport.ContentTables)
        {
            StringAssert.Contains(text, $"OK   contenidos {table}: ");
        }

        StringAssert.Contains(text, "contenidos tblknowledgebase: 7 / 7");
        StringAssert.Contains(text, "FALTA tblknowledgebase_images 2");
        StringAssert.Contains(text, "INFO content_imported artículos de la base de conocimiento: 5");
        StringAssert.Contains(text, "Sin importar todavía: imágenes de la base de conocimiento");

        // Una fila que el origen tiene y no se contó es una diferencia.
        var short_ = Path.Combine(Path.GetDirectoryName(baseline)!, "corto.tsv");
        await File.WriteAllTextAsync(short_, "table_rows\ttblknowledgebase\t99\ntable_rows\ttblannouncements\t4\n");
        var again = new StringWriter();
        Assert.AreEqual(1, await Reconciliation.Compare(import, short_, again), again.ToString());
        StringAssert.Contains(again.ToString(), "DIF  contenidos tblknowledgebase: 7 / 99");
    });

    private static async Task WithImport(string dump, Func<BillingDbContext, SourceImport, string, Task> check)
    {
        var admin = Environment.GetEnvironmentVariable("BILLINGOSS_TEST_PG")
            ?? throw new AssertInconclusiveException("Falta BILLINGOSS_TEST_PG: se saltean las pruebas de integración.");
        var name = $"billing_oss_t_{Guid.NewGuid():N}"[..40];
        var folder = Path.Combine(Path.GetTempPath(), "billing-oss-contenidos-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        await Execute(admin, $"CREATE DATABASE {name}");
        try
        {
            var target = new NpgsqlConnectionStringBuilder(admin) { Database = name, Pooling = false }.ConnectionString;
            await using var db = new BillingDbContext(DesignTimeFactory.Configure(new DbContextOptionsBuilder<BillingDbContext>(), target).Options);
            await db.Database.MigrateAsync();
            var import = new SourceImport(db, TextWriter.Null);
            await using (var reader = new DumpReader(new MemoryStream(Encoding.UTF8.GetBytes(dump))))
            {
                await import.Load(reader, CancellationToken.None);
            }

            await import.Run(CancellationToken.None);
            var baseline = Path.Combine(folder, "linea-base.tsv");
            await File.WriteAllTextAsync(baseline, Baseline);
            await check(db, import, baseline);
        }
        finally
        {
            await Execute(admin, $"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}
