using BillingOss.App.Content;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Content;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Base de conocimiento: categorías, artículos, audiencia, búsqueda, sugerencias, votos y lecturas (sección 3 de diseno/contenidos.md).</summary>
[TestClass]
[DoNotParallelize] // Comparten una base: crear una cuesta casi un minuto con la PC cargada.
public sealed class ContentKnowledgeTests
{
    private static CoreHarness h = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _) => h = await ContentHarness.Start();

    [ClassCleanup]
    public static async Task Stop() => await ContentHarness.Stop(h);

    [TestInitialize]
    public Task Clean() => h.Reset();

    private static ContentText Text(string title, string? summary = null, string? body = null) => new(title, summary, body);

    private static Task<long> NewCategory(string name = "Servidores de juegos", long? parent = null, bool hidden = false, string? slug = null) =>
        h.Use<CategoryService, long>(c => c.Create(CategoryKind.KnowledgeBase, new CategoryInput(parent, slug, 0, hidden, new Dictionary<string, ContentText> { ["es-AR"] = Text(name), ["en"] = Text(name + " (en)") })));

    private static ArticleInput Input(long category, string title = "Cómo entrar al panel", string body = "Entrá al **panel** con tu usuario.", ContentAudience audience = ContentAudience.Public, long[]? products = null, long[]? groups = null, bool featured = false, string? slug = null, string? english = null, int? revision = null)
    {
        var texts = new Dictionary<string, ArticleTextInput> { ["es-AR"] = new(title, null, body) };
        if (english is not null)
        {
            texts["en"] = new ArticleTextInput(english, null, "English **body**");
        }

        return new ArticleInput(category, slug, featured, 0, audience, products ?? [], groups ?? [], texts, revision);
    }

    private static async Task<long> Published(ArticleInput input)
    {
        var id = await h.Use<KnowledgeService, long>(k => k.Create(input));
        await h.Use<KnowledgeService, bool>(async k =>
        {
            await k.Publish(id);
            return true;
        });
        return id;
    }

    private static Task<ArticleView?> Read(ContentViewer viewer, string slug, string? language = "es-AR") => h.Use<KnowledgeService, ArticleView?>(k => k.Read(viewer, slug, language));

    private static Task<long> NewProduct() => h.Query(async db =>
    {
        var product = new Product { Name = "Game server " + Guid.NewGuid().ToString("N")[..6], Kind = ProductKind.Manual };
        db.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    });

    private static Task<long> NewGroup() => h.Query(async db =>
    {
        var group = new AccountGroup { Name = "Grupo " + Guid.NewGuid().ToString("N")[..6], CreatedAt = h.Clock.Now };
        db.Add(group);
        await db.SaveChangesAsync();
        return group.Id;
    });

    [TestMethod]
    public async Task Categorias_ArbolDeTresNivelesSinCiclos_YConSlugsUnicos()
    {
        var root = await NewCategory("Ayuda", slug: "ayuda");
        var child = await NewCategory("Juegos", root);
        var leaf = await NewCategory("Minecraft", child);

        var tooDeep = await Assert.ThrowsAsync<ValidationException>(() => NewCategory("Cuarto nivel", leaf));
        Assert.Contains("3 niveles", tooDeep.Message);

        // Mover la raíz abajo de su hoja haría un ciclo.
        var cycle = await Assert.ThrowsAsync<ValidationException>(() => h.Use<CategoryService, bool>(async c =>
        {
            await c.Update(CategoryKind.KnowledgeBase, root, new CategoryInput(leaf, "ayuda", 0, false, new Dictionary<string, ContentText> { ["es-AR"] = Text("Ayuda") }));
            return true;
        }));
        Assert.Contains("sí misma", cycle.Message);

        // Mover una rama con hijos que haría pasar los tres niveles tampoco vale.
        var other = await NewCategory("Otra raíz");
        var otherChild = await NewCategory("Hija", other);
        var deep = await Assert.ThrowsAsync<ValidationException>(() => h.Use<CategoryService, bool>(async c =>
        {
            await c.Update(CategoryKind.KnowledgeBase, child, new CategoryInput(otherChild, null, 0, false, new Dictionary<string, ContentText> { ["es-AR"] = Text("Juegos") }));
            return true;
        }));
        Assert.Contains("3 niveles", deep.Message);

        // El slug se arma con el nombre y no choca: la segunda "Ayuda" queda como "ayuda-2".
        var second = await NewCategory("Ayuda");
        var rows = await h.Use<CategoryService, IReadOnlyList<CategoryRow>>(c => c.List(CategoryKind.KnowledgeBase));
        Assert.AreEqual("ayuda-2", rows.Single(r => r.Category.Id == second).Category.Slug);
        await Assert.ThrowsAsync<InvalidOperationException>(() => NewCategory("Repetida", slug: "ayuda"));
        await Assert.ThrowsAsync<ValidationException>(() => NewCategory("Sin madre", parent: 9999));
        await Assert.ThrowsAsync<ValidationException>(() => NewCategory("Slug malo", slug: "Con Mayúsculas"));
    }

    [TestMethod]
    public async Task Categorias_NoSeBorraUnaConHijasOArticulos()
    {
        var root = await NewCategory("Ayuda");
        var child = await NewCategory("Juegos", root);
        await Published(Input(child));

        var withChild = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Use<CategoryService, bool>(async c => { await c.Delete(CategoryKind.KnowledgeBase, root); return true; }));
        var withArticle = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Use<CategoryService, bool>(async c => { await c.Delete(CategoryKind.KnowledgeBase, child); return true; }));
        Assert.Contains("subcategorías o contenido", withChild.Message);
        Assert.Contains("subcategorías o contenido", withArticle.Message);

        var empty = await NewCategory("Vacía");
        await h.Use<CategoryService, bool>(async c => { await c.Delete(CategoryKind.KnowledgeBase, empty); return true; });
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Use<CategoryService, bool>(async c => { await c.Delete(CategoryKind.KnowledgeBase, empty); return true; }));
    }

    [TestMethod]
    public async Task Articulo_UnBorradorNoSeVe_PublicadoSiSeVeConElMarkdownConvertido()
    {
        var category = await NewCategory();
        var id = await h.Use<KnowledgeService, long>(k => k.Create(Input(category, slug: "como-entrar", english: "How to sign in")));

        Assert.IsNull(await Read(ContentViewer.Anonymous, "como-entrar"), "Un borrador no se ve.");

        await h.Use<KnowledgeService, bool>(async k => { await k.Publish(id); return true; });
        var view = (await Read(ContentViewer.Anonymous, "como-entrar"))!;

        Assert.AreEqual("Cómo entrar al panel", view.Card.Title);
        Assert.AreEqual("<p>Entrá al <strong>panel</strong> con tu usuario.</p>", view.Html);
        Assert.IsFalse(view.Card.Fallback);
        Assert.AreEqual("Entrá al panel con tu usuario.", view.Card.Summary, "Sin resumen, sale el comienzo del cuerpo.");
        Assert.AreEqual("Servidores de juegos", view.Crumbs.Single().Name);

        // En inglés sale el texto en inglés. En un idioma que no existe sale el de base.
        var english = (await Read(ContentViewer.Anonymous, "como-entrar", "en"))!;
        Assert.AreEqual("How to sign in", english.Card.Title);
        Assert.AreEqual("<p>English <strong>body</strong></p>", english.Html);
        Assert.AreEqual("Cómo entrar al panel", (await Read(ContentViewer.Anonymous, "como-entrar", "pt"))!.Card.Title);

        await h.Use<KnowledgeService, bool>(async k => { await k.Archive(id); return true; });
        Assert.IsNull(await Read(ContentViewer.Anonymous, "como-entrar"), "Un archivado tampoco.");
    }

    [TestMethod]
    public async Task Articulo_SinTextoEnElIdiomaDelLector_AvisaQueEsElDeBase()
    {
        var category = await NewCategory();
        await Published(Input(category, slug: "solo-espanol"));

        var english = (await Read(ContentViewer.Anonymous, "solo-espanol", "en"))!;

        Assert.IsTrue(english.Card.Fallback);
        Assert.AreEqual("es-AR", english.Card.Language);
    }

    [TestMethod]
    public async Task Articulo_LasLecturasSuman_Y_LoQueNoSeVeNoSuma()
    {
        var category = await NewCategory();
        var id = await Published(Input(category, slug: "lecturas"));
        var hidden = await h.Use<KnowledgeService, long>(k => k.Create(Input(category, slug: "borrador")));

        for (var i = 0; i < 3; i++)
        {
            await Read(ContentViewer.Anonymous, "lecturas");
        }

        await Read(ContentViewer.Anonymous, "borrador");

        Assert.AreEqual(3L, await h.Query(db => db.Set<Article>().Where(a => a.Id == id).Select(a => a.Views).SingleAsync()));
        Assert.AreEqual(0L, await h.Query(db => db.Set<Article>().Where(a => a.Id == hidden).Select(a => a.Views).SingleAsync()));
    }

    [TestMethod]
    public async Task Audiencia_PublicaClientesYDirigida_ConLaMismaRegla()
    {
        var category = await NewCategory();
        var product = await NewProduct();
        var group = await NewGroup();
        await Published(Input(category, "Pública", slug: "publica"));
        await Published(Input(category, "Para clientes", slug: "clientes", audience: ContentAudience.Customers));
        await Published(Input(category, "Del producto", slug: "del-producto", audience: ContentAudience.Targeted, products: [product]));
        await Published(Input(category, "Del grupo", slug: "del-grupo", audience: ContentAudience.Targeted, groups: [group]));
        var nothing = new ContentViewer(true, [], []);
        var withProduct = new ContentViewer(true, [product], []);
        var inGroup = new ContentViewer(true, [], [group]);

        async Task<string[]> Slugs(ContentViewer viewer) =>
            [.. (await h.Use<KnowledgeService, IReadOnlyList<ArticleCard>>(k => k.Latest(viewer, "es-AR", 20))).Select(c => c.Slug).Order()];

        CollectionAssert.AreEqual(new[] { "publica" }, await Slugs(ContentViewer.Anonymous));
        CollectionAssert.AreEqual(new[] { "clientes", "publica" }, await Slugs(nothing));
        CollectionAssert.AreEqual(new[] { "clientes", "del-producto", "publica" }, await Slugs(withProduct));
        CollectionAssert.AreEqual(new[] { "clientes", "del-grupo", "publica" }, await Slugs(inGroup));

        // Lo ajeno da lo mismo que lo que no existe, y la lista y la lectura coinciden.
        Assert.IsNotNull(await Read(withProduct, "del-producto"));
        Assert.IsNull(await Read(ContentViewer.Anonymous, "del-producto"));
        Assert.IsNull(await Read(nothing, "del-producto"));
        Assert.IsNull(await Read(withProduct, "del-grupo"));
        Assert.IsNotNull(await Read(inGroup, "del-grupo"));
    }

    [TestMethod]
    public async Task Visitantes_ResuelvenLosProductosDeLosServiciosVivosYLosGruposDeLasCuentas()
    {
        var product = await NewProduct();
        var group = await NewGroup();
        await h.Db(async db =>
        {
            db.Services.Add(new Service(h.AccountId, product, BillingCycle.Monthly, Currency.Ars, 100m, 1, h.Clock.Now));
            await db.SaveChangesAsync();
            await db.Accounts.Where(a => a.Id == h.AccountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.GroupId, group));
        });

        var alive = await h.Use<ContentViewers, ContentViewer>(v => v.ForAccounts([h.AccountId]));
        CollectionAssert.Contains(alive.ProductIds, product);
        CollectionAssert.AreEqual(new[] { group }, alive.GroupIds);

        // Un servicio terminado o cancelado ya no cuenta, y sacar la cuenta del grupo también la saca de ahí.
        await h.Db(db => db.Services.Where(s => s.ProductId == product).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ServiceStatus.Terminated)));
        await h.Db(db => db.Accounts.Where(a => a.Id == h.AccountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.GroupId, (long?)null)));
        var gone = await h.Use<ContentViewers, ContentViewer>(v => v.ForAccounts([h.AccountId]));
        CollectionAssert.DoesNotContain(gone.ProductIds, product);
        Assert.IsEmpty(gone.GroupIds);

        // Sin cuentas sigue siendo un cliente, pero sin productos ni grupos.
        var empty = await h.Use<ContentViewers, ContentViewer>(v => v.ForAccounts([]));
        Assert.IsTrue(empty.IsCustomer);
        Assert.IsEmpty(empty.ProductIds);
    }

    [TestMethod]
    public async Task Audiencia_UnaDirigidaSinListasOConProductoInexistenteNoSeGuarda()
    {
        var category = await NewCategory();

        var empty = await Assert.ThrowsAsync<ValidationException>(() => h.Use<KnowledgeService, long>(k => k.Create(Input(category, audience: ContentAudience.Targeted))));
        var missingProduct = await Assert.ThrowsAsync<ValidationException>(() => h.Use<KnowledgeService, long>(k => k.Create(Input(category, audience: ContentAudience.Targeted, products: [987654]))));
        var missingGroup = await Assert.ThrowsAsync<ValidationException>(() => h.Use<KnowledgeService, long>(k => k.Create(Input(category, audience: ContentAudience.Targeted, groups: [987654]))));

        Assert.Contains("al menos un producto", empty.Message);
        Assert.Contains("No existe el producto 987654", missingProduct.Message);
        Assert.Contains("No existe el grupo de clientes 987654", missingGroup.Message);
    }

    [TestMethod]
    public async Task Categorias_ElArbolCuentaSoloLoVisibleYOcultaLasVacias()
    {
        var root = await NewCategory("Ayuda", slug: "ayuda");
        var games = await NewCategory("Juegos", root, slug: "juegos");
        var empty = await NewCategory("Vacía", root, slug: "vacia");
        var hiddenCategory = await NewCategory("Oculta", hidden: true, slug: "oculta");
        await Published(Input(games, "Uno", slug: "uno"));
        await Published(Input(games, "Dos", slug: "dos", audience: ContentAudience.Customers));
        await Published(Input(hiddenCategory, "Tres", slug: "tres"));
        await h.Use<KnowledgeService, long>(k => k.Create(Input(empty, "Borrador", slug: "borrador")));

        var anonymous = await h.Use<KnowledgeService, IReadOnlyList<CategoryNode>>(k => k.Tree(ContentViewer.Anonymous, "es-AR"));
        var customer = await h.Use<KnowledgeService, IReadOnlyList<CategoryNode>>(k => k.Tree(new ContentViewer(true, [], []), "es-AR"));

        Assert.AreEqual("ayuda", anonymous.Single().Slug);
        Assert.AreEqual(1, anonymous.Single().Count);
        Assert.AreEqual("juegos", anonymous.Single().Children.Single().Slug, "La vacía y la oculta no están.");
        Assert.AreEqual(2, customer.Single().Count);

        var page = (await h.Use<KnowledgeService, CategoryPage?>(k => k.Category(ContentViewer.Anonymous, "juegos", "es-AR")))!;
        CollectionAssert.AreEqual(new[] { "ayuda", "juegos" }, page.Crumbs.Select(c => c.Slug).ToArray());
        CollectionAssert.AreEqual(new[] { "uno" }, page.Articles.Select(a => a.Slug).ToArray());
        Assert.IsNull(await h.Use<KnowledgeService, CategoryPage?>(k => k.Category(ContentViewer.Anonymous, "vacia", "es-AR")));
        Assert.IsNull(await h.Use<KnowledgeService, CategoryPage?>(k => k.Category(ContentViewer.Anonymous, "oculta", "es-AR")));
    }

    [TestMethod]
    public async Task Busqueda_PliegaTildes_UsaLaRaizDelIdioma_YRespetaLaAudiencia()
    {
        var category = await NewCategory();
        await Published(Input(category, "Configuración del panel", "Los servidores de juegos se administran desde el panel.", slug: "configuracion", english: "Panel setup"));
        await Published(Input(category, "Facturas y pagos", "Cómo pagar una factura con Mercado Pago.", slug: "facturas"));
        await Published(Input(category, "Solo para clientes", "El servidor de Minecraft usa la versión 1.20.", slug: "minecraft", audience: ContentAudience.Customers));

        async Task<string[]> Find(ContentViewer viewer, string query, string language = "es-AR") =>
            [.. (await h.Use<KnowledgeService, IReadOnlyList<ArticleCard>>(k => k.Search(viewer, query, language))).Select(c => c.Slug)];

        CollectionAssert.AreEqual(new[] { "configuracion" }, await Find(ContentViewer.Anonymous, "configuracion"));
        CollectionAssert.AreEqual(new[] { "configuracion" }, await Find(ContentViewer.Anonymous, "configuración"));
        CollectionAssert.AreEqual(new[] { "configuracion" }, await Find(ContentViewer.Anonymous, "servidor"), "Singular y plural comparten la raíz.");
        CollectionAssert.AreEqual(new[] { "facturas" }, await Find(ContentViewer.Anonymous, "\"mercado pago\""));
        CollectionAssert.AreEqual(Array.Empty<string>(), await Find(ContentViewer.Anonymous, "minecraft"), "No se encuentra lo que no se puede ver.");
        CollectionAssert.AreEqual(new[] { "minecraft" }, await Find(new ContentViewer(true, [], []), "minecraft"));
        CollectionAssert.AreEqual(new[] { "configuracion" }, await Find(ContentViewer.Anonymous, "panel -factura"));
        CollectionAssert.AreEqual(Array.Empty<string>(), await Find(ContentViewer.Anonymous, "   "));
        CollectionAssert.AreEqual(Array.Empty<string>(), await Find(ContentViewer.Anonymous, "zzzzzz"));

        // En inglés se busca en los textos en inglés y se muestra ese título.
        var english = await h.Use<KnowledgeService, IReadOnlyList<ArticleCard>>(k => k.Search(ContentViewer.Anonymous, "setup", "en"));
        Assert.AreEqual("Panel setup", english.Single().Title);
    }

    [TestMethod]
    public async Task Busqueda_LoMasRelevantePrimero_ConElTituloPesandoMas()
    {
        var category = await NewCategory();
        await Published(Input(category, "Reiniciar el servidor", "Pasos para reiniciar.", slug: "titulo"));
        await Published(Input(category, "Otra guía", "Tené paciencia: reiniciar el servidor tarda un minuto, y el servidor vuelve solo.", slug: "cuerpo"));

        var found = await h.Use<KnowledgeService, IReadOnlyList<ArticleCard>>(k => k.Search(ContentViewer.Anonymous, "servidor", "es-AR"));

        CollectionAssert.AreEqual(new[] { "titulo", "cuerpo" }, found.Select(c => c.Slug).ToArray());
    }

    [TestMethod]
    public async Task Sugerencias_ParaUnTicket_BuscanConONYDevuelvenSoloLoVisible()
    {
        var category = await NewCategory();
        await Published(Input(category, "Mi servidor de Minecraft no arranca", "Revisá el archivo eula.txt y aceptá el EULA.", slug: "minecraft"));
        await Published(Input(category, "Cómo pagar una factura", "Con Mercado Pago o transferencia.", slug: "pagar"));
        await Published(Input(category, "Solo clientes: acceso por SFTP", "Conectate por SFTP al servidor.", slug: "sftp", audience: ContentAudience.Customers));

        async Task<string[]> Suggest(ContentViewer viewer, string text) =>
            [.. (await h.Use<KnowledgeService, IReadOnlyList<ArticleCard>>(k => k.Suggest(viewer, text, "es-AR"))).Select(c => c.Slug)];

        // El texto de un ticket real: no aparecen todas las palabras en un solo artículo.
        var ticket = "Hola, ayer reinicié y ahora mi servidor de Minecraft no arranca. Dice algo del EULA y no sé qué hacer, gracias";
        CollectionAssert.AreEqual(new[] { "minecraft" }, await Suggest(ContentViewer.Anonymous, ticket));
        CollectionAssert.AreEqual(new[] { "pagar" }, await Suggest(ContentViewer.Anonymous, "No puedo pagar la factura"));
        CollectionAssert.AreEqual(new[] { "sftp" }, await Suggest(new ContentViewer(true, [], []), "Necesito conectarme por SFTP"));
        CollectionAssert.AreEqual(Array.Empty<string>(), await Suggest(ContentViewer.Anonymous, "Necesito conectarme por SFTP"));
        CollectionAssert.AreEqual(Array.Empty<string>(), await Suggest(ContentViewer.Anonymous, "de la que no con por"), "Solo palabras vacías.");
        CollectionAssert.AreEqual(Array.Empty<string>(), await Suggest(ContentViewer.Anonymous, string.Empty));
        CollectionAssert.AreEqual(Array.Empty<string>(), await Suggest(ContentViewer.Anonymous, "'; DROP TABLE content.articles; -- | & ! ( ) :*"), "Los símbolos no rompen la consulta.");
    }

    [TestMethod]
    public void Sugerencias_LasPalabrasUtilesSalenSinTildesYLasMasLargasPrimero()
    {
        var terms = KnowledgeService.Terms("Cómo configuración, ñandú 404 en el panel; PANEL otra vez");

        CollectionAssert.AreEqual(new[] { "configuracion", "panel", "ñandu", "como", "otra", "404", "vez" }, terms.ToArray());
        Assert.IsLessThanOrEqualTo(12, KnowledgeService.Terms(string.Join(' ', Enumerable.Range(0, 100).Select(n => "palabra" + n))).Count);
        Assert.IsEmpty(KnowledgeService.Terms(null));
    }

    [TestMethod]
    public async Task Votos_UnoPorVotante_CambiarElVotoMueveElConteo()
    {
        var category = await NewCategory();
        var id = await Published(Input(category, slug: "votos"));

        Task<VoteResult?> Vote(string voter, bool helpful) => h.Use<KnowledgeService, VoteResult?>(k => k.Vote(ContentViewer.Anonymous, id, voter, helpful));

        Assert.AreEqual(new VoteResult(1, 0, true), await Vote("user:1", true));
        Assert.AreEqual(new VoteResult(1, 0, false), await Vote("user:1", true), "Repetirlo no suma.");
        Assert.AreEqual(new VoteResult(1, 1, true), await Vote("visit:abc", false));
        Assert.AreEqual(new VoteResult(0, 2, true), await Vote("user:1", false), "Cambiar el voto lo mueve de un lado al otro.");
        Assert.AreEqual(2, await h.Query(db => db.Set<ArticleVote>().CountAsync()));
        Assert.AreEqual(new VoteResult(1, 1, true), await Vote("user:1", true));
    }

    [TestMethod]
    public async Task Votos_DosALaVez_NoPierdenNingunoNiVotanLoQueNoSeVe()
    {
        var category = await NewCategory();
        var id = await Published(Input(category, slug: "carrera"));
        var customersOnly = await Published(Input(category, "Solo clientes", slug: "reservado", audience: ContentAudience.Customers));

        await Task.WhenAll(Enumerable.Range(0, 12).Select(n => h.Use<KnowledgeService, VoteResult?>(k => k.Vote(ContentViewer.Anonymous, id, $"visit:{n}", n % 3 != 0))));

        var article = await h.Query(db => db.Set<Article>().SingleAsync(a => a.Id == id));
        Assert.AreEqual(8L, article.Helpful);
        Assert.AreEqual(4L, article.Unhelpful);
        Assert.IsNull(await h.Use<KnowledgeService, VoteResult?>(k => k.Vote(ContentViewer.Anonymous, customersOnly, "visit:x", true)));
        Assert.AreEqual(0L, await h.Query(db => db.Set<Article>().Where(a => a.Id == customersOnly).Select(a => a.Helpful).SingleAsync()));
    }

    [TestMethod]
    public async Task Edicion_UnaRevisionAtrasadaFalla_YLaVigenteSube()
    {
        var category = await NewCategory();
        var id = await h.Use<KnowledgeService, long>(k => k.Create(Input(category, slug: "revision")));
        var first = (await h.Use<KnowledgeService, ArticleDetail?>(k => k.Get(id)))!.Article.Revision;

        await h.Use<KnowledgeService, bool>(async k => { await k.Update(id, Input(category, "Primer cambio", revision: first)); return true; });
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Use<KnowledgeService, bool>(async k => { await k.Update(id, Input(category, "Segundo cambio", revision: first)); return true; }));

        Assert.Contains("Otra persona modificó", conflict.Message);
        var detail = (await h.Use<KnowledgeService, ArticleDetail?>(k => k.Get(id)))!;
        Assert.AreEqual(first + 1, detail.Article.Revision);
        Assert.AreEqual("Primer cambio", detail.Texts.Single().Title);
    }

    [TestMethod]
    public async Task Edicion_DosCambiosALaVezNoSePisanEnSilencio()
    {
        var category = await NewCategory();
        var id = await h.Use<KnowledgeService, long>(k => k.Create(Input(category, slug: "carrera-edicion")));

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(n => h.Use<KnowledgeService, string>(async k =>
        {
            try
            {
                await k.Update(id, Input(category, "Cambio " + n, revision: 0));
                return "ok";
            }
            catch (InvalidOperationException)
            {
                return "conflicto";
            }
        })));

        CollectionAssert.AreEquivalent(new[] { "ok", "conflicto" }, results);
    }

    [TestMethod]
    public async Task Textos_ElArticuloPublicadoNecesitaUnTextoConCuerpo_YNuncaQuedaSinNinguno()
    {
        var category = await NewCategory();
        var empty = await h.Use<KnowledgeService, long>(k => k.Create(Input(category, "Sin cuerpo", body: string.Empty, slug: "sin-cuerpo")));

        var publish = await Assert.ThrowsAsync<ValidationException>(() => h.Use<KnowledgeService, bool>(async k => { await k.Publish(empty); return true; }));
        Assert.Contains("título y cuerpo", publish.Message);
        Assert.AreEqual(PublishState.Draft, (await h.Use<KnowledgeService, ArticleDetail?>(k => k.Get(empty)))!.Article.Status, "Si falla, no cambia.");

        var id = await Published(Input(category, slug: "textos", english: "English title"));
        await h.Use<KnowledgeService, bool>(async k => { await k.RemoveText(id, "en"); return true; });
        var last = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Use<KnowledgeService, bool>(async k => { await k.RemoveText(id, "es-AR"); return true; }));
        Assert.Contains("al menos un idioma", last.Message);

        await h.Use<KnowledgeService, bool>(async k => { await k.SetText(id, "en", new ArticleTextInput("Back again", "Short", "Body", "tag1, tag2, TAG1")); return true; });
        var texts = (await h.Use<KnowledgeService, ArticleDetail?>(k => k.Get(id)))!.Texts;
        Assert.AreEqual(2, texts.Count);
        Assert.AreEqual("tag1, tag2", texts.Single(t => t.Language == "en").Tags, "Las etiquetas repetidas se juntan.");
        await Assert.ThrowsAsync<ValidationException>(() => h.Use<KnowledgeService, bool>(async k => { await k.SetText(id, "fr", new ArticleTextInput("Salut", null, "Corps")); return true; }));
    }

    [TestMethod]
    public async Task Listas_FiltranPorCategoriaEstadoYTexto()
    {
        var games = await NewCategory("Juegos");
        var billing = await NewCategory("Facturación");
        var minecraft = await Published(Input(games, "Servidor de Minecraft", slug: "mc"));
        await h.Use<KnowledgeService, long>(k => k.Create(Input(billing, "Pagar con transferencia", slug: "transferencia")));

        Task<IReadOnlyList<ArticleRow>> List(ArticleFilter filter) => h.Use<KnowledgeService, IReadOnlyList<ArticleRow>>(k => k.List(filter));

        Assert.AreEqual(2, (await List(new ArticleFilter())).Count);
        Assert.AreEqual(minecraft, (await List(new ArticleFilter(CategoryId: games))).Single().Article.Id);
        Assert.AreEqual("Pagar con transferencia", (await List(new ArticleFilter(Status: PublishState.Draft))).Single().Title);
        Assert.AreEqual(minecraft, (await List(new ArticleFilter(Query: "MINECRAFT"))).Single().Article.Id);
        Assert.AreEqual(minecraft, (await List(new ArticleFilter(Query: minecraft.ToString(System.Globalization.CultureInfo.InvariantCulture)))).Single().Article.Id);
        Assert.IsEmpty(await List(new ArticleFilter(Query: "100%_")), "Los comodines del buscador se toman como texto.");
    }

    [TestMethod]
    public async Task Borrar_SeLlevaTextosYVotos_YDejaLaAuditoria()
    {
        var before = await h.Query(db => db.Set<AuditEvent>().MaxAsync(a => (long?)a.Id)) ?? 0;
        var category = await NewCategory();
        var id = await Published(Input(category, slug: "borrar", english: "Delete me"));
        await h.Use<KnowledgeService, VoteResult?>(k => k.Vote(ContentViewer.Anonymous, id, "user:1", true));

        await h.Use<KnowledgeService, bool>(async k => { await k.Delete(id); return true; });

        Assert.AreEqual(0, await h.Query(db => db.Set<ArticleText>().CountAsync()));
        Assert.AreEqual(0, await h.Query(db => db.Set<ArticleVote>().CountAsync()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Use<KnowledgeService, bool>(async k => { await k.Delete(id); return true; }));
        var actions = await h.Query(db => db.Set<AuditEvent>().Where(a => a.Id > before && a.Entity == $"article:{id}").OrderBy(a => a.Id).Select(a => a.Action).ToListAsync());
        CollectionAssert.AreEqual(new[] { "content.article.create", "content.article.publish", "content.article.delete" }, actions);
    }

    [TestMethod]
    public async Task Articulo_ElSlugPedidoDebeSerValidoYLibre_YUnaCategoriaDeDescargasNoVale()
    {
        var category = await NewCategory();
        var downloads = await h.Use<CategoryService, long>(c => c.Create(CategoryKind.Downloads, new CategoryInput(null, null, 0, false, new Dictionary<string, ContentText> { ["es-AR"] = Text("Instaladores") })));
        await h.Use<KnowledgeService, long>(k => k.Create(Input(category, slug: "tomado")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Use<KnowledgeService, long>(k => k.Create(Input(category, slug: "tomado"))));
        await Assert.ThrowsAsync<ValidationException>(() => h.Use<KnowledgeService, long>(k => k.Create(Input(category, slug: "12345"))));
        var wrongKind = await Assert.ThrowsAsync<ValidationException>(() => h.Use<KnowledgeService, long>(k => k.Create(Input(downloads))));
        Assert.Contains("base de conocimiento", wrongKind.Message);
        await Assert.ThrowsAsync<ValidationException>(() => h.Use<KnowledgeService, long>(k => k.Create(new ArticleInput(category, null, false, 0, ContentAudience.Public, [], []))));
    }

    [TestMethod]
    public async Task Portada_DestacadosYUltimos()
    {
        var category = await NewCategory();
        await Published(Input(category, "Destacado", slug: "destacado", featured: true));
        await Published(Input(category, "Común", slug: "comun"));

        var featured = await h.Use<KnowledgeService, IReadOnlyList<ArticleCard>>(k => k.Featured(ContentViewer.Anonymous, "es-AR"));
        var latest = await h.Use<KnowledgeService, IReadOnlyList<ArticleCard>>(k => k.Latest(ContentViewer.Anonymous, "es-AR"));

        CollectionAssert.AreEqual(new[] { "destacado" }, featured.Select(a => a.Slug).ToArray());
        Assert.AreEqual(2, latest.Count);
        Assert.AreEqual("comun", latest[0].Slug, "Los últimos publicados primero.");
    }
}
