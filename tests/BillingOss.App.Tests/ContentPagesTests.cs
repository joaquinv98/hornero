using System.Globalization;
using System.Net;
using System.Text;
using BillingOss.App.Content;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Content;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>
/// Las páginas de contenidos para clientes de punta a punta (secciones 3 a 6 de diseno/contenidos.md): lo que ve una visita, un
/// cliente de un grupo y un cliente de otro, el Markdown seguro, los votos, el RSS, las descargas y el estado de la red.
/// </summary>
[TestClass]
[DoNotParallelize] // Comparten un host y su base: crear una cuesta casi un minuto con la PC cargada.
public sealed class ContentPagesTests
{
    private static ContentSite site = null!;
    private static long articleId;
    private static long publicDownload;
    private static long customersDownload;
    private static long groupDownload;
    private static long incidentId;
    private static long hiddenIncidentId;
    private static long maintenanceId;
    private static long panelId;
    private static readonly byte[] PublicBytes = [.. "PK\u0003\u0004publico"u8];

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        site = await ContentSite.Start(s => s.Configure<ContentOptions>(o => o.ImageHosts.Add("img.example.com")));
        await Seed();
    }

    [ClassCleanup]
    public static async Task Stop() => await site.DisposeAsync();

    private static Dictionary<string, ContentText> Both(string es, string? en = null, string? summary = null, string? body = null)
    {
        var texts = new Dictionary<string, ContentText> { ["es-AR"] = new(es, summary, body) };
        if (en is not null)
        {
            texts["en"] = new ContentText(en, summary, body);
        }

        return texts;
    }

    private static async Task Seed()
    {
        var group = await site.Ui.Query(async db =>
        {
            var g = new AccountGroup { Name = "Revendedores", CreatedAt = DateTimeOffset.UtcNow };
            db.Add(g);
            await db.SaveChangesAsync();
            (await db.Accounts.SingleAsync(a => a.Id == site.Ui.AccountA)).GroupId = g.Id;
            await db.SaveChangesAsync();
            return g.Id;
        });

        await site.AddCustomer(EmailA, site.Ui.AccountA);
        await site.AddCustomer(EmailB, site.Ui.AccountB);

        // Base de conocimiento.
        var root = await site.Use<CategoryService, long>(c => c.Create(CategoryKind.KnowledgeBase, new CategoryInput(null, "hosting-web", 0, false, Both("Hosting web", "Web hosting"))));
        var mail = await site.Use<CategoryService, long>(c => c.Create(CategoryKind.KnowledgeBase, new CategoryInput(root, "correo", 0, false, Both("Correo", "Mail"))));
        async Task<long> Article(long category, string title, string body, ContentAudience audience = ContentAudience.Public, long[]? groups = null, bool featured = false, string? english = null, bool publish = true)
        {
            var texts = new Dictionary<string, ArticleTextInput> { ["es-AR"] = new(title, null, body, "email, imap") };
            if (english is not null)
            {
                texts["en"] = new ArticleTextInput(english, null, "English **body**");
            }

            var id = await site.Use<KnowledgeService, long>(k => k.Create(new ArticleInput(category, null, featured, 0, audience, [], groups ?? [], texts)));
            if (publish)
            {
                await site.Use<KnowledgeService>(k => k.Publish(id));
            }

            return id;
        }

        articleId = await Article(mail, "Configurar el correo", "Usá **IMAP** para leer.\n\n[x](javascript:alert(1)) <script>alert(2)</script>\n\n![logo](https://img.example.com/a.png)", featured: true, english: "Set up your mail");
        await Article(root, "Facturas pagas", "Cómo ver tus facturas.", ContentAudience.Customers);
        await Article(root, "Cuenta de revendedor", "Beneficios para revendedores.", ContentAudience.Targeted, [group]);
        await Article(root, "Borrador secreto", "Todavía no sale.", publish: false);

        // Novedades.
        async Task<long> News(string title, bool pinned = false, ContentAudience audience = ContentAudience.Public, DateTimeOffset? at = null, DateTimeOffset? expires = null, bool publish = true)
        {
            var id = await site.Use<AnnouncementService, long>(a => a.Create(new AnnouncementInput(audience, [], [], pinned, null, Both(title, null, null, $"Cuerpo de **{title}**."))));
            if (publish)
            {
                await site.Use<AnnouncementService>(a => a.Publish(id, at));
            }

            if (expires is not null)
            {
                await site.Use<AnnouncementService>(a => a.Update(id, new AnnouncementInput(audience, [], [], pinned, expires, Both(title, null, null, "Cuerpo."))));
            }

            return id;
        }

        await News("Aviso común");
        await News("Aviso fijado", pinned: true);
        await News("Aviso para clientes", audience: ContentAudience.Customers);
        await News("Aviso futuro", at: DateTimeOffset.UtcNow.AddDays(2));
        await News("Aviso vencido", at: DateTimeOffset.UtcNow.AddDays(-3), expires: DateTimeOffset.UtcNow.AddDays(-1));
        await News("Aviso borrador", publish: false);

        // Descargas.
        var agents = await site.Use<CategoryService, long>(c => c.Create(CategoryKind.Downloads, new CategoryInput(null, "agentes", 0, false, Both("Agentes", "Agents"))));
        async Task<long> Download(string title, ContentAudience audience, long[]? groups = null, bool publish = true)
        {
            var id = await site.Use<DownloadService, long>(d => d.Create(new DownloadInput(agents, 0, audience, [], groups ?? [], Both(title, null, "Instalador."))));
            await site.Use<DownloadService, UploadedFile>(async d => await d.Upload(id, "agente.zip", new MemoryStream(PublicBytes)));
            if (publish)
            {
                await site.Use<DownloadService>(d => d.Publish(id));
            }

            return id;
        }

        publicDownload = await Download("Agente público", ContentAudience.Public);
        customersDownload = await Download("Agente de clientes", ContentAudience.Customers);
        groupDownload = await Download("Agente de revendedores", ContentAudience.Targeted, [group]);
        await Download("Agente borrador", ContentAudience.Public, publish: false);

        // Estado de la red.
        async Task<long> Component(string name, ComponentKind kind, long? parent = null, bool hidden = false) =>
            await site.Use<StatusService, long>(s => s.CreateComponent(new ComponentInput(parent, kind, null, 0, hidden, [], Both(name))));
        var servers = await Component("Servidores", ComponentKind.Group);
        var vps = await Component("VPS Buenos Aires", ComponentKind.Server, servers);
        panelId = await Component("Panel de clientes", ComponentKind.Service, servers);
        var hiddenNode = await Component("Nodo interno", ComponentKind.Server, servers, hidden: true);
        incidentId = await site.Use<StatusService, long>(s => s.OpenIncident(new IncidentInput(Both("Latencia en los VPS"), Both("x", body: "Estamos **investigando** la causa."), [new ComponentImpact(vps, ComponentStatus.PartialOutage)])));
        hiddenIncidentId = await site.Use<StatusService, long>(s => s.OpenIncident(new IncidentInput(Both("Falla del nodo interno"), Both("x", body: "Solo para el equipo."), [new ComponentImpact(hiddenNode, ComponentStatus.MajorOutage)])));
        maintenanceId = await site.Use<StatusService, long>(s => s.ScheduleMaintenance(new MaintenanceInput(Both("Mantenimiento del panel"), [panelId], DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow.AddDays(2).AddHours(2))));
    }

    private const string EmailA = "revendedor@example.com";

    private const string EmailB = "cliente@example.com";

    /// <summary>El titular de la cuenta del grupo de revendedores.</summary>
    private static Task<Client> CustomerA() => site.Login(EmailA);

    /// <summary>El titular de la otra cuenta, que no está en ningún grupo.</summary>
    private static Task<Client> CustomerB() => site.Login(EmailB);

    [TestMethod]
    public async Task Ayuda_UnaVisitaVeLoPublico_UnClienteVeLoSuyo_YLoDemasDa404()
    {
        using var visitor = site.Visitor();
        var home = await visitor.Html("/conocimiento");
        StringAssert.Contains(home, "Hosting web");
        StringAssert.Contains(home, "Configurar el correo");
        Assert.DoesNotContain("Facturas pagas", home);
        Assert.DoesNotContain("Borrador secreto", home);
        StringAssert.Contains(await visitor.Html("/conocimiento/c/correo"), "Configurar el correo");
        StringAssert.Contains(await visitor.Html("/conocimiento/c/hosting-web"), "Correo");

        foreach (var hidden in new[] { "/conocimiento/facturas-pagas", "/conocimiento/cuenta-de-revendedor", "/conocimiento/borrador-secreto", "/conocimiento/no-existe", "/conocimiento/c/no-existe" })
        {
            Assert.AreEqual(HttpStatusCode.NotFound, (await visitor.Read(hidden)).Status, hidden);
        }

        // Un cliente de un grupo ve lo suyo, y un cliente de otro ve lo de los clientes pero no lo dirigido al grupo.
        using var reseller = await CustomerA();
        StringAssert.Contains(await reseller.Html("/conocimiento"), "Facturas pagas");
        StringAssert.Contains(await reseller.Html("/conocimiento/cuenta-de-revendedor"), "Beneficios para revendedores");
        StringAssert.Contains(await reseller.Html("/conocimiento/facturas-pagas"), "Cómo ver tus facturas");

        using var other = await CustomerB();
        Assert.DoesNotContain("Cuenta de revendedor", await other.Html("/conocimiento"));
        StringAssert.Contains(await other.Html("/conocimiento/facturas-pagas"), "Cómo ver tus facturas");
        Assert.AreEqual(HttpStatusCode.NotFound, (await other.Read("/conocimiento/cuenta-de-revendedor")).Status);
    }

    [TestMethod]
    public async Task Ayuda_BuscaPorTextoYEtiqueta_YRespetaLaAudiencia()
    {
        using var visitor = site.Visitor();
        StringAssert.Contains(await visitor.Html("/conocimiento?q=correo"), "Configurar el correo");
        StringAssert.Contains(await visitor.Html("/conocimiento?q=imap"), "Configurar el correo");
        var none = await visitor.Html("/conocimiento?q=facturas");
        Assert.DoesNotContain("href=\"/conocimiento/facturas-pagas\"", none);
        StringAssert.Contains(none, "No encontramos artículos");

        using var customer = await CustomerB();
        StringAssert.Contains(await customer.Html("/conocimiento?q=facturas"), "href=\"/conocimiento/facturas-pagas\"");
        StringAssert.Contains(await customer.Html("/conocimiento?q=%22"), "No encontramos"); // Una comilla suelta no rompe la búsqueda.
    }

    [TestMethod]
    public async Task Articulo_ConvierteElMarkdownSinHtmlPeligroso_YPermiteLasImagenesConfiguradas()
    {
        using var visitor = site.Visitor();
        using var response = await visitor.Get("/conocimiento/configurar-el-correo");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "<strong>IMAP</strong>");
        Assert.DoesNotContain("<script>alert(2)", html);
        StringAssert.Contains(html, "&lt;script&gt;");
        Assert.DoesNotContain("javascript:", html);
        StringAssert.Contains(html, "src=\"https://img.example.com/a.png\"");
        var policy = string.Join(' ', response.Headers.GetValues("Content-Security-Policy"));
        StringAssert.Contains(policy, "https://img.example.com");
        StringAssert.Contains(policy, "object-src 'none'");
        Assert.DoesNotContain("public", string.Join(' ', response.Headers.GetValues("Cache-Control")), "Lo que depende de la sesión no va a cachés compartidas.");

        // El ID de origen redirige para siempre a la dirección corta, y un número que no existe da 404.
        using var byId = await visitor.Get($"/conocimiento/{articleId}");
        Assert.AreEqual(HttpStatusCode.MovedPermanently, byId.StatusCode);
        Assert.AreEqual("/conocimiento/configurar-el-correo", byId.Location());
        Assert.AreEqual(HttpStatusCode.NotFound, (await visitor.Read("/conocimiento/9999999")).Status);
    }

    [TestMethod]
    public async Task DireccionesDelSistemaAnterior_RedirigenPorSiempreALaDireccionNueva()
    {
        using var visitor = site.Visitor();
        var mail = await site.Use<CategoryService, long>(async c => (await c.List(CategoryKind.KnowledgeBase)).Single(r => r.Category.Slug == "correo").Category.Id);
        foreach (var (old, expected) in new[]
        {
            ($"/index.php?rp=/knowledgebase/{articleId}/", $"/conocimiento/{articleId}"),
            ($"/index.php?rp=/knowledgebase/{mail}/Correo", $"/conocimiento/c/{mail}"),
            ($"/knowledgebase.php?action=displayarticle&catid={mail}&id={articleId}", $"/conocimiento/{articleId}"),
            ("/index.php?rp=/knowledgebase/tag/imap", "/conocimiento?q=imap"),
            ("/index.php?rp=/announcements/3/Aviso", "/anuncios/3"),
            ("/index.php?rp=/download", "/descargas"),
            ("/serverstatus.php", "/estado-red"),
            ("/submitticket.php?step=2&deptid=4&subject=Hola", "/soporte/nuevo"),
        })
        {
            using var response = await visitor.Get(old);
            Assert.AreEqual(HttpStatusCode.MovedPermanently, response.StatusCode, old);
            Assert.AreEqual(expected, response.Location(), old);
        }

        // La categoría por su número termina en su dirección corta, y lo que no es de contenidos da 404.
        using var category = await visitor.Get($"/conocimiento/c/{mail}");
        Assert.AreEqual(HttpStatusCode.MovedPermanently, category.StatusCode);
        Assert.AreEqual("/conocimiento/c/correo", category.Location());
        Assert.AreEqual(HttpStatusCode.NotFound, (await visitor.Read("/index.php?rp=/store/vps")).Status);
    }

    [TestMethod]
    public async Task Articulo_ElLectorVeSuIdioma_YSinTraduccionSeAvisa()
    {
        using var english = site.Visitor();
        english.Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        var translated = await english.Html("/conocimiento/configurar-el-correo");
        StringAssert.Contains(translated, "Set up your mail");
        Assert.DoesNotContain("not translated", translated);

        using var customer = await CustomerB();
        customer.Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        var fallback = await customer.Html("/conocimiento/facturas-pagas");
        StringAssert.Contains(fallback, "Facturas pagas");
        StringAssert.Contains(fallback, "not translated");
    }

    [TestMethod]
    public async Task Votos_UnoPorVisitaConSuCookie_CambiarloMueveElConteo_YLoQueNoSeVeNoSeVota()
    {
        using var first = site.Visitor();
        const string page = "/conocimiento/configurar-el-correo";
        using (var yes = await first.Post(page, page + "?handler=Vote", ("helpful", "true")))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, yes.StatusCode);
            StringAssert.StartsWith(yes.Location(), page + "?voto=si");
            StringAssert.Contains(string.Join(';', yes.Headers.GetValues("Set-Cookie")), "bo_visit=");
        }

        var after = await first.Html(page + "?voto=si");
        StringAssert.Contains(after, "Gracias por avisarnos");
        StringAssert.Contains(after, "A 1 de 1 personas");

        // Votar lo mismo otra vez no suma. Cambiar el voto mueve el conteo.
        (await first.Post(page, page + "?handler=Vote", ("helpful", "true"))).Dispose();
        StringAssert.Contains(await first.Html(page), "A 1 de 1 personas");
        (await first.Post(page, page + "?handler=Vote", ("helpful", "false"))).Dispose();
        StringAssert.Contains(await first.Html(page), "A 0 de 1 personas");

        using var second = site.Visitor();
        (await second.Post(page, page + "?handler=Vote", ("helpful", "true"))).Dispose();
        StringAssert.Contains(await second.Html(page), "A 1 de 2 personas");

        // Un artículo que no se ve no se vota.
        using var blocked = await second.Post(page, "/conocimiento/facturas-pagas?handler=Vote", ("helpful", "true"));
        Assert.AreEqual(HttpStatusCode.NotFound, blocked.StatusCode);
    }

    [TestMethod]
    public async Task Novedades_SoloLasVigentesParaQuienMira_LasFijadasPrimero_YElRssEsAnonimo()
    {
        using var visitor = site.Visitor();
        var list = await visitor.Html("/anuncios");
        Assert.IsLessThan(list.IndexOf("Aviso común", StringComparison.Ordinal), list.IndexOf("Aviso fijado", StringComparison.Ordinal), "Las fijadas van arriba.");
        foreach (var hidden in new[] { "Aviso para clientes", "Aviso futuro", "Aviso vencido", "Aviso borrador" })
        {
            Assert.DoesNotContain(hidden, list);
        }

        StringAssert.Contains(list, "/anuncios.rss");
        var common = await site.Use<AnnouncementService, IReadOnlyList<AnnouncementView>>(a => a.Visible(ContentViewer.Anonymous, "es-AR"));
        var commonId = common.Single(v => v.Title == "Aviso común").Id;
        StringAssert.Contains(await visitor.Html($"/anuncios/{commonId}"), "<strong>Aviso común</strong>");

        var rows = await site.Use<AnnouncementService, IReadOnlyList<AnnouncementRow>>(a => a.List());
        foreach (var row in rows.Where(r => r.Texts["es-AR"].Title is "Aviso para clientes" or "Aviso futuro" or "Aviso borrador" or "Aviso vencido"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, (await visitor.Read($"/anuncios/{row.Announcement.Id}")).Status, row.Texts["es-AR"].Title);
        }

        using var customer = await CustomerB();
        StringAssert.Contains(await customer.Html("/anuncios"), "Aviso para clientes");

        using var rss = await visitor.Get("/anuncios.rss");
        var xml = await rss.Content.ReadAsStringAsync();
        Assert.AreEqual("application/rss+xml", rss.Content.Headers.ContentType?.MediaType);
        StringAssert.Contains(xml, "<title>Aviso fijado</title>");
        StringAssert.Contains(xml, $"https://billing.example.com/anuncios/{commonId}");
        Assert.DoesNotContain("Aviso para clientes", xml);
        Assert.DoesNotContain("Aviso futuro", xml);
    }

    [TestMethod]
    public async Task Descargas_ElListadoYElArchivoSoloParaQuienCorresponde_SiempreComoAdjunto()
    {
        using var visitor = site.Visitor();
        var list = await visitor.Html("/descargas");
        StringAssert.Contains(list, "Agente público");
        StringAssert.Contains(list, "agente.zip");
        StringAssert.Contains(list, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(PublicBytes)));
        Assert.DoesNotContain("Agente de clientes", list);
        Assert.DoesNotContain("Agente borrador", list);

        using var before = await visitor.Get($"/descargas/{publicDownload}/archivo");
        Assert.AreEqual(HttpStatusCode.OK, before.StatusCode);
        CollectionAssert.AreEqual(PublicBytes, await before.Content.ReadAsByteArrayAsync());
        Assert.AreEqual("attachment", before.Content.Headers.ContentDisposition?.DispositionType);
        Assert.AreEqual("agente.zip", before.Content.Headers.ContentDisposition?.FileName);
        Assert.AreEqual("application/zip", before.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("nosniff", string.Join(' ', before.Headers.GetValues("X-Content-Type-Options")));
        StringAssert.Contains(string.Join(' ', before.Headers.GetValues("Cache-Control")), "no-store");

        // Un HEAD cuenta nada. Un GET completo suma una.
        var count = () => site.Ui.Query(db => db.Set<Download>().Where(d => d.Id == publicDownload).Select(d => d.DownloadCount).SingleAsync());
        var counted = await count();
        using (var head = new HttpRequestMessage(HttpMethod.Head, $"/descargas/{publicDownload}/archivo"))
        using (var response = await visitor.Http.SendAsync(head))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(PublicBytes.Length, response.Content.Headers.ContentLength);
        }

        Assert.AreEqual(counted, await count());
        (await visitor.Get($"/descargas/{publicDownload}/archivo")).Dispose();
        Assert.AreEqual(counted + 1, await count());

        foreach (var hidden in new[] { customersDownload, groupDownload })
        {
            Assert.AreEqual(HttpStatusCode.NotFound, (await visitor.Read($"/descargas/{hidden}/archivo")).Status);
        }

        Assert.AreEqual(HttpStatusCode.NotFound, (await visitor.Read("/descargas/999999/archivo")).Status);

        using var other = await CustomerB();
        Assert.AreEqual(HttpStatusCode.OK, (await other.Read($"/descargas/{customersDownload}/archivo")).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await other.Read($"/descargas/{groupDownload}/archivo")).Status);
        Assert.DoesNotContain("Agente de revendedores", await other.Html("/descargas"));

        using var reseller = await CustomerA();
        StringAssert.Contains(await reseller.Html("/descargas"), "Agente de revendedores");
        Assert.AreEqual(HttpStatusCode.OK, (await reseller.Read($"/descargas/{groupDownload}/archivo")).Status);
    }

    [TestMethod]
    public async Task EstadoDeLaRed_MuestraElEstadoPorComponente_ConPalabraYForma_SinLoOculto()
    {
        using var visitor = site.Visitor();
        var home = await visitor.Html("/estado-red");

        StringAssert.Contains(home, "Servidores");
        StringAssert.Contains(home, "VPS Buenos Aires");
        StringAssert.Contains(home, "Interrupción parcial");
        StringAssert.Contains(home, "data-shape=\"ring\"");
        StringAssert.Contains(home, "Operativo");
        StringAssert.Contains(home, "Latencia en los VPS");
        StringAssert.Contains(home, "Estamos investigando la causa.");
        StringAssert.Contains(home, "Mantenimiento del panel");
        Assert.DoesNotContain("Nodo interno", home);
        Assert.DoesNotContain("Falla del nodo interno", home);

        // El estado se calcula con los eventos vigentes: en inglés, las mismas palabras traducidas.
        using var english = site.Visitor();
        english.Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        StringAssert.Contains(await english.Html("/estado-red"), "Partial outage");
    }

    [TestMethod]
    public async Task EstadoDeLaRed_ElDetalleMuestraElHistorial_YUnEventoQueSoloTocaLoOcultoDa404()
    {
        using var visitor = site.Visitor();
        var incident = await visitor.Html($"/estado-red/{incidentId}");
        StringAssert.Contains(incident, "Latencia en los VPS");
        StringAssert.Contains(incident, "Estamos <strong>investigando</strong> la causa.");
        StringAssert.Contains(incident, "VPS Buenos Aires");
        StringAssert.Contains(incident, "Investigando");

        var maintenance = await visitor.Html($"/estado-red/{maintenanceId}");
        StringAssert.Contains(maintenance, "Mantenimiento del panel");
        StringAssert.Contains(maintenance, "Próximo");
        StringAssert.Contains(maintenance, "Panel de clientes");

        Assert.AreEqual(HttpStatusCode.NotFound, (await visitor.Read($"/estado-red/{hiddenIncidentId}")).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await visitor.Read("/estado-red/999999")).Status);

        // Un incidente resuelto sale del estado y queda en el historial del mes.
        var resolved = await site.Use<StatusService, long>(s => s.OpenIncident(new IncidentInput(Both("Corte breve del panel"), Both("x", body: "Sin servicio."), [new ComponentImpact(panelId, ComponentStatus.MajorOutage)])));
        await site.Use<StatusService>(s => s.PostUpdate(resolved, new StatusUpdateInput(Both("x", body: "Volvió."), StatusEventState.Resolved)));
        var history = await visitor.Html("/estado-red/historial");
        StringAssert.Contains(history, "Corte breve del panel");
        Assert.DoesNotContain("Latencia en los VPS", history);
        var old = await visitor.Html("/estado-red/historial?mes=2001-01");
        StringAssert.Contains(old, "No hubo eventos");
        StringAssert.Contains(await visitor.Html("/estado-red/historial?mes=cualquier-cosa"), "Corte breve del panel");
    }

    [TestMethod]
    public async Task Suscripcion_DobleConfirmacion_LaBajaPideUnBoton_YLaRespuestaEsLaMismaExistaONoLaCasilla()
    {
        using var visitor = site.Visitor();
        var form = await visitor.Html("/estado-red/suscribirse");
        StringAssert.Contains(form, "VPS Buenos Aires");
        Assert.DoesNotContain("Nodo interno", form);

        using var bad = await visitor.Post("/estado-red/suscribirse", "/estado-red/suscribirse", ("Email", "sin-arroba"), ("Lang", "es-AR"));
        StringAssert.Contains(await bad.Content.ReadAsStringAsync(), "Escribí un correo válido");

        var email = $"ana{Guid.NewGuid():N}@example.com"[..24] + "@example.com";
        async Task<string> Subscribe()
        {
            using var response = await visitor.Post("/estado-red/suscribirse", "/estado-red/suscribirse", ("Email", email), ("Lang", "en"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }

        var first = await Subscribe();
        StringAssert.Contains(first, "Listo");
        var token = await site.Ui.Query(db => db.Set<StatusSubscriber>().Where(s => s.Email == email).Select(s => s.Token).SingleAsync());
        var sent = await site.Ui.Query(db => db.Set<BillingOss.App.Notifications.OutboundEmail>().Where(e => e.Recipient == email).CountAsync());
        Assert.AreEqual(1, sent);
        Assert.AreEqual(first, await Subscribe(), "La respuesta no cambia si la casilla ya estaba.");
        Assert.AreEqual(1, await site.Ui.Query(db => db.Set<BillingOss.App.Notifications.OutboundEmail>().Where(e => e.Recipient == email).CountAsync()), "El enlace sale a lo sumo una vez por hora.");

        // El enlace del correo confirma. Un enlace inventado da 404.
        Assert.AreEqual(HttpStatusCode.NotFound, (await visitor.Read("/estado-red/suscripcion/confirmar/token-inventado")).Status);
        Assert.IsNull(await site.Ui.Query(db => db.Set<StatusSubscriber>().Where(s => s.Token == token).Select(s => s.ConfirmedAt).SingleAsync()));
        StringAssert.Contains(await visitor.Html($"/estado-red/suscripcion/confirmar/{token}"), "Suscripción confirmada");
        Assert.IsNotNull(await site.Ui.Query(db => db.Set<StatusSubscriber>().Where(s => s.Token == token).Select(s => s.ConfirmedAt).SingleAsync()));

        // Abrir el enlace de baja no borra nada: la página pide un botón.
        var ask = await visitor.Html($"/estado-red/suscripcion/baja/{token}");
        StringAssert.Contains(ask, "Sí, darme de baja");
        Assert.AreEqual(1, await site.Ui.Query(db => db.Set<StatusSubscriber>().CountAsync(s => s.Token == token)));
        Assert.AreEqual(HttpStatusCode.NotFound, (await visitor.Read("/estado-red/suscripcion/baja/token-inventado")).Status);
        using var done = await visitor.Post($"/estado-red/suscripcion/baja/{token}", $"/estado-red/suscripcion/baja/{token}?handler=Confirm");
        StringAssert.Contains(await done.Content.ReadAsStringAsync(), "Ya no vas a recibir");
        Assert.AreEqual(0, await site.Ui.Query(db => db.Set<StatusSubscriber>().CountAsync(s => s.Token == token)));
    }

    [TestMethod]
    public async Task Portal_ElClienteVeLosIncidentesConocidosDeSusProductos_YSoloDeEsos()
    {
        var mine = await site.Ui.Query(db => db.Products.Where(p => p.Name == "VPS 2 GB").Select(p => p.Id).SingleAsync());
        var other = await site.Ui.Query(async db =>
        {
            var product = new Product { Name = "Telefonía", Kind = ProductKind.Manual };
            db.Add(product);
            await db.SaveChangesAsync();
            return product.Id;
        });
        var network = await site.Use<StatusService, long>(s => s.CreateComponent(new ComponentInput(null, ComponentKind.Service, null, 0, false, [mine], Both("Red de los VPS"))));
        var phones = await site.Use<StatusService, long>(s => s.CreateComponent(new ComponentInput(null, ComponentKind.Service, null, 0, false, [other], Both("Central telefónica"))));
        var incident = await site.Use<StatusService, long>(s => s.OpenIncident(new IncidentInput(Both("Corte en la red de los VPS"), Both("x", body: "Estamos trabajando."), [new ComponentImpact(network, ComponentStatus.PartialOutage)])));
        await site.Use<StatusService, long>(s => s.OpenIncident(new IncidentInput(Both("Falla de la central"), Both("x", body: "Sin tono."), [new ComponentImpact(phones, ComponentStatus.MajorOutage)])));

        // El cliente ve lo que toca sus productos, en la lista de servicios y en el inicio, con la palabra y la forma del estado.
        using var customer = await CustomerB();
        foreach (var page in new[] { "/servicios", "/" })
        {
            var html = await customer.Html(page);
            StringAssert.Contains(html, "Incidentes y mantenimientos conocidos");
            StringAssert.Contains(html, "Corte en la red de los VPS");
            StringAssert.Contains(html, $"href=\"/estado-red/{incident}\"");
            StringAssert.Contains(html, "Investigando");
            Assert.DoesNotContain("Falla de la central", html, "Lo que toca un producto que no tiene no se le muestra.");
        }

        // Resuelto, deja de mostrarse.
        await site.Use<StatusService>(s => s.PostUpdate(incident, new StatusUpdateInput(Both("x", body: "Resuelto."), StatusEventState.Resolved)));
        Assert.DoesNotContain("Incidentes y mantenimientos conocidos", await customer.Html("/servicios"));
    }
}
