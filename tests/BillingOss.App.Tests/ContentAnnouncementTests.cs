using System.Xml.Linq;
using BillingOss.App.Content;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Content;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Novedades: borrador, publicación programada, fijadas, vencimiento, audiencia y RSS (sección 4 de diseno/contenidos.md).</summary>
[TestClass]
[DoNotParallelize] // Comparten una base: crear una cuesta casi un minuto con la PC cargada.
public sealed class ContentAnnouncementTests
{
    private static CoreHarness h = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _) => h = await ContentHarness.Start();

    [ClassCleanup]
    public static async Task Stop() => await ContentHarness.Stop(h);

    [TestInitialize]
    public Task Clean() => h.Reset();

    private static AnnouncementInput Input(string title = "Mantenimiento del sábado", string body = "Habrá un **corte** corto.", bool pinned = false, ContentAudience audience = ContentAudience.Public, long[]? products = null, DateTimeOffset? expires = null, string? english = null, int? revision = null)
    {
        var texts = new Dictionary<string, ContentText> { ["es-AR"] = new(title, null, body) };
        if (english is not null)
        {
            texts["en"] = new ContentText(english, null, "A short **outage**.");
        }

        return new AnnouncementInput(audience, products ?? [], [], pinned, expires, texts, revision);
    }

    private static Task<long> Create(AnnouncementInput input) => h.Use<AnnouncementService, long>(a => a.Create(input));

    private static async Task Publish(long id, DateTimeOffset? at = null) =>
        await h.Use<AnnouncementService, bool>(async a => { await a.Publish(id, at); return true; });

    private static async Task<string[]> Titles(ContentViewer viewer, string language = "es-AR") =>
        [.. (await h.Use<AnnouncementService, IReadOnlyList<AnnouncementView>>(a => a.Visible(viewer, language))).Select(v => v.Title)];

    [TestMethod]
    public async Task Borrador_NoSeVe_Publicada_SiSeVeConElCuerpoConvertido_YDespublicarOArchivarLaRetira()
    {
        var id = await Create(Input());
        CollectionAssert.AreEqual(Array.Empty<string>(), await Titles(ContentViewer.Anonymous));
        Assert.IsNull(await h.Use<AnnouncementService, AnnouncementView?>(a => a.Read(ContentViewer.Anonymous, id, "es-AR")));

        await Publish(id);
        var view = (await h.Use<AnnouncementService, AnnouncementView?>(a => a.Read(ContentViewer.Anonymous, id, "es-AR")))!;
        Assert.AreEqual("Mantenimiento del sábado", view.Title);
        Assert.AreEqual("<p>Habrá un <strong>corte</strong> corto.</p>", view.Html);
        Assert.AreEqual("Habrá un corte corto.", view.Summary);
        Assert.AreEqual(h.Clock.Now, view.PublishedAt, "Publicar sin fecha usa el momento actual.");

        await h.Use<AnnouncementService, bool>(async a => { await a.Unpublish(id); return true; });
        CollectionAssert.AreEqual(Array.Empty<string>(), await Titles(ContentViewer.Anonymous));
        await Publish(id);
        await h.Use<AnnouncementService, bool>(async a => { await a.Archive(id); return true; });
        CollectionAssert.AreEqual(Array.Empty<string>(), await Titles(ContentViewer.Anonymous));
        Assert.AreEqual(AnnouncementPhase.Archived, (await h.Use<AnnouncementService, AnnouncementRow?>(a => a.Get(id)))!.Phase);
    }

    [TestMethod]
    public async Task Fechas_ConDesfaseDeOtraZonaSeGuardanEnUtc_SinFallar()
    {
        // La API y el panel mandan la hora con el desfase de quien la escribió. La base solo guarda UTC.
        var buenosAires = TimeSpan.FromHours(-3);
        var id = await Create(Input("Con desfase", expires: new DateTimeOffset(2031, 6, 1, 9, 0, 0, buenosAires)));
        await Publish(id, new DateTimeOffset(2031, 5, 20, 10, 30, 0, buenosAires));

        var row = (await h.Use<AnnouncementService, AnnouncementRow?>(a => a.Get(id)))!;
        Assert.AreEqual(new DateTimeOffset(2031, 5, 20, 13, 30, 0, TimeSpan.Zero), row.Announcement.PublishAt);
        Assert.AreEqual(new DateTimeOffset(2031, 6, 1, 12, 0, 0, TimeSpan.Zero), row.Announcement.ExpiresAt);
        Assert.AreEqual(TimeSpan.Zero, row.Announcement.PublishAt!.Value.Offset);
    }

    [TestMethod]
    public async Task Programada_EmpiezaAVerseSolaAlLlegarLaHora_SinNingunTrabajo()
    {
        var id = await Create(Input("Promoción de verano"));
        await Publish(id, h.Clock.Now.AddDays(2));

        Assert.AreEqual(AnnouncementPhase.Scheduled, (await h.Use<AnnouncementService, AnnouncementRow?>(a => a.Get(id)))!.Phase);
        CollectionAssert.AreEqual(Array.Empty<string>(), await Titles(ContentViewer.Anonymous));
        Assert.AreEqual(0, await h.Query(db => db.Jobs.CountAsync()), "No hace falta un trabajo que cambie filas: la fase se deduce con la hora.");

        h.Clock.Now = h.Clock.Now.AddDays(2).AddMinutes(-1);
        CollectionAssert.AreEqual(Array.Empty<string>(), await Titles(ContentViewer.Anonymous));

        h.Clock.Now = h.Clock.Now.AddMinutes(1);
        CollectionAssert.AreEqual(new[] { "Promoción de verano" }, await Titles(ContentViewer.Anonymous));
        Assert.AreEqual(AnnouncementPhase.Live, (await h.Use<AnnouncementService, AnnouncementRow?>(a => a.Get(id)))!.Phase);
    }

    [TestMethod]
    public async Task Vencimiento_LaNovedadDejaDeVerseSolaYElVencimientoTieneQueSerPosterior()
    {
        var id = await Create(Input("Oferta por una semana", expires: h.Clock.Now.AddDays(7)));
        await Publish(id);
        CollectionAssert.AreEqual(new[] { "Oferta por una semana" }, await Titles(ContentViewer.Anonymous));

        h.Clock.Now = h.Clock.Now.AddDays(7);

        CollectionAssert.AreEqual(Array.Empty<string>(), await Titles(ContentViewer.Anonymous));
        Assert.AreEqual(AnnouncementPhase.Expired, (await h.Use<AnnouncementService, AnnouncementRow?>(a => a.Get(id)))!.Phase);

        // Programar con un vencimiento anterior a la publicación no se puede.
        var other = await Create(Input("Mal armada", expires: h.Clock.Now.AddDays(1)));
        var error = await Assert.ThrowsAsync<ValidationException>(() => h.Use<AnnouncementService, bool>(async a => { await a.Publish(other, h.Clock.Now.AddDays(2)); return true; }));
        Assert.Contains("posterior a la publicación", error.Message);
    }

    [TestMethod]
    public async Task Fijadas_PrimeroYDespuesLasMasNuevas()
    {
        var old = await Create(Input("Vieja"));
        await Publish(old);
        h.Clock.Now = h.Clock.Now.AddDays(1);
        var pinned = await Create(Input("Fijada", pinned: true));
        await Publish(pinned);
        h.Clock.Now = h.Clock.Now.AddDays(1);
        var recent = await Create(Input("Nueva"));
        await Publish(recent);

        CollectionAssert.AreEqual(new[] { "Fijada", "Nueva", "Vieja" }, await Titles(ContentViewer.Anonymous));
        var views = await h.Use<AnnouncementService, IReadOnlyList<AnnouncementView>>(a => a.Visible(ContentViewer.Anonymous, "es-AR"));
        Assert.IsTrue(views[0].Pinned);
    }

    [TestMethod]
    public async Task Audiencia_PublicaClientesYDirigida()
    {
        var product = await h.Query(async db =>
        {
            var p = new Product { Name = "VPS " + Guid.NewGuid().ToString("N")[..6], Kind = ProductKind.Manual };
            db.Add(p);
            await db.SaveChangesAsync();
            return p.Id;
        });
        foreach (var input in new[] { Input("Pública"), Input("Clientes", audience: ContentAudience.Customers), Input("Del producto", audience: ContentAudience.Targeted, products: [product]) })
        {
            await Publish(await Create(input));
        }

        CollectionAssert.AreEqual(new[] { "Pública" }, await Titles(ContentViewer.Anonymous));
        CollectionAssert.AreEqual(new[] { "Clientes", "Pública" }, (await Titles(new ContentViewer(true, [], []))).Order().ToArray());
        CollectionAssert.AreEqual(new[] { "Clientes", "Del producto", "Pública" }, (await Titles(new ContentViewer(true, [product], []))).Order().ToArray());
        var error = await Assert.ThrowsAsync<ValidationException>(() => Create(Input("Sin lista", audience: ContentAudience.Targeted)));
        Assert.Contains("al menos un producto", error.Message);
    }

    [TestMethod]
    public async Task Idioma_ElLectorVeElSuyoOElDeBase()
    {
        var id = await Create(Input("Corte programado", english: "Scheduled outage"));
        await Publish(id);
        var onlySpanish = await Create(Input("Solo en español"));
        await Publish(onlySpanish);

        CollectionAssert.AreEquivalent(new[] { "Scheduled outage", "Solo en español" }, await Titles(ContentViewer.Anonymous, "en"));
        var english = (await h.Use<AnnouncementService, AnnouncementView?>(a => a.Read(ContentViewer.Anonymous, id, "en")))!;
        Assert.AreEqual("<p>A short <strong>outage</strong>.</p>", english.Html);
        Assert.IsFalse(english.Fallback);
        Assert.IsTrue((await h.Use<AnnouncementService, AnnouncementView?>(a => a.Read(ContentViewer.Anonymous, onlySpanish, "en")))!.Fallback);
    }

    [TestMethod]
    public async Task Validaciones_TituloCuerpoAlPublicarYRevision()
    {
        var noTitle = await Assert.ThrowsAsync<ValidationException>(() => Create(Input("   ")));
        Assert.Contains("título", noTitle.Message);

        var id = await Create(Input("Sin cuerpo", body: string.Empty));
        var noBody = await Assert.ThrowsAsync<ValidationException>(() => h.Use<AnnouncementService, bool>(async a => { await a.Publish(id); return true; }));
        Assert.Contains("Falta el cuerpo", noBody.Message);

        var first = (await h.Use<AnnouncementService, AnnouncementRow?>(a => a.Get(id)))!.Announcement.Revision;
        await h.Use<AnnouncementService, bool>(async a => { await a.Update(id, Input("Primero", revision: first)); return true; });
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Use<AnnouncementService, bool>(async a => { await a.Update(id, Input("Segundo", revision: first)); return true; }));
        Assert.Contains("Otra persona modificó", stale.Message);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Use<AnnouncementService, bool>(async a => { await a.Publish(999); return true; }));
    }

    [TestMethod]
    public async Task Rss_SoloLasPublicasVigentes_ConEnlacesYFechasYElHtmlEscapado()
    {
        var first = await Create(Input("Novedad <uno> & más", "Texto con **negrita** y un [enlace](https://example.com/x?a=1&b=2)."));
        await Publish(first);
        h.Clock.Now = h.Clock.Now.AddHours(3);
        await Publish(await Create(Input("Segunda", english: "Second")));
        await Publish(await Create(Input("Solo clientes", audience: ContentAudience.Customers)));
        await Create(Input("Borrador"));
        await Publish(await Create(Input("Programada")), h.Clock.Now.AddDays(1));

        var xml = await h.Use<AnnouncementService, string>(a => a.Rss("es", "Ejemplo", new Uri("https://billing.example.com")));
        var rss = XDocument.Parse(xml).Root!;
        var channel = rss.Element("channel")!;
        var items = channel.Elements("item").ToList();

        Assert.AreEqual("2.0", rss.Attribute("version")!.Value);
        Assert.AreEqual("Ejemplo: anuncios", channel.Element("title")!.Value);
        Assert.AreEqual("https://billing.example.com/anuncios", channel.Element("link")!.Value);
        Assert.AreEqual("es-AR", channel.Element("language")!.Value);
        CollectionAssert.AreEqual(new[] { "Segunda", "Novedad <uno> & más" }, items.Select(i => i.Element("title")!.Value).ToArray());
        Assert.AreEqual($"https://billing.example.com/anuncios/{first}", items[1].Element("link")!.Value);
        Assert.AreEqual("true", items[1].Element("guid")!.Attribute("isPermaLink")!.Value);
        Assert.AreEqual("Mon, 02 Nov 2026 15:00:00 GMT", items[1].Element("pubDate")!.Value);
        Assert.AreEqual("Mon, 02 Nov 2026 18:00:00 GMT", channel.Element("lastBuildDate")!.Value);
        StringAssert.Contains(items[1].Element("description")!.Value, "<strong>negrita</strong>");
        StringAssert.Contains(items[1].Element("description")!.Value, "href=\"https://example.com/x?a=1&amp;b=2\"");
        StringAssert.StartsWith(xml, "<?xml version=\"1.0\" encoding=\"utf-8\"?>");

        var english = XDocument.Parse(await h.Use<AnnouncementService, string>(a => a.Rss("en", "Ejemplo", new Uri("https://billing.example.com")))).Root!.Element("channel")!;
        Assert.AreEqual("Ejemplo: announcements", english.Element("title")!.Value);
        Assert.AreEqual("Second", english.Elements("item").First().Element("title")!.Value);
    }

    [TestMethod]
    public async Task Rss_SinNovedades_SigueSiendoUnCanalValido()
    {
        var xml = await h.Use<AnnouncementService, string>(a => a.Rss(null, "Ejemplo", new Uri("https://billing.example.com")));

        var channel = XDocument.Parse(xml).Root!.Element("channel")!;
        Assert.IsEmpty(channel.Elements("item"));
        Assert.AreEqual("Mon, 02 Nov 2026 15:00:00 GMT", channel.Element("lastBuildDate")!.Value);
    }

    [TestMethod]
    public async Task Borrar_QuedaEnLaAuditoria()
    {
        var before = await h.Query(db => db.Set<BillingOss.App.Platform.AuditEvent>().MaxAsync(a => (long?)a.Id)) ?? 0;
        var id = await Create(Input());
        await Publish(id);
        await h.Use<AnnouncementService, bool>(async a => { await a.Delete(id); return true; });

        var actions = await h.Query(db => db.Set<BillingOss.App.Platform.AuditEvent>().Where(a => a.Id > before && a.Entity == $"announcement:{id}").OrderBy(a => a.Id).Select(a => a.Action).ToListAsync());

        CollectionAssert.AreEqual(new[] { "content.announcement.create", "content.announcement.publish", "content.announcement.delete" }, actions);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Use<AnnouncementService, bool>(async a => { await a.Delete(id); return true; }));
    }
}
