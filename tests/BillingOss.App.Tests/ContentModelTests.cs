using BillingOss.App.Persistence;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Content;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BillingOss.App.Tests;

/// <summary>Lo que la base garantiza por sí sola en las tablas de contenidos (diseno/contenidos.md, secciones 3 a 6).</summary>
[TestClass]
[DoNotParallelize] // Comparten una base: crear una cuesta casi un minuto con la PC cargada.
public sealed class ContentModelTests
{
    private static CoreHarness h = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _) => h = await ContentHarness.Start();

    [ClassCleanup]
    public static async Task Stop() => await ContentHarness.Stop(h);

    [TestInitialize]
    public Task Clean() => h.Reset();

    private static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    private static async Task<long> Category(BillingDbContext db, CategoryKind kind = CategoryKind.KnowledgeBase, string slug = "ayuda", long? parent = null)
    {
        var category = new ContentCategory(kind, slug, Now) { ParentId = parent };
        db.Add(category);
        await db.SaveChangesAsync();
        return category.Id;
    }

    private static async Task<long> Article(BillingDbContext db, long category, string slug = "como-entrar")
    {
        var article = new Article { CategoryId = category, Slug = slug, CreatedAt = Now, UpdatedAt = Now };
        db.Add(article);
        await db.SaveChangesAsync();
        return article.Id;
    }

    private static async Task<PostgresException> Rejected(BillingDbContext db)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        return (PostgresException)ex.InnerException!;
    }

    [TestMethod]
    public async Task LaMigracionCreaLasTablasDelModulo()
    {

        var tables = await h.Query(db => db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'content' ORDER BY 1").ToListAsync());

        CollectionAssert.AreEqual(
            new[]
            {
                "announcements", "article_texts", "article_votes", "articles", "categories", "components", "download_files", "downloads",
                "status_event_components", "status_event_updates", "status_events", "subscribers",
            },
            tables);
        Assert.AreEqual(1, await h.Query(db => db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'content' AND table_name = 'article_texts' AND column_name = 'search' AND is_generated = 'ALWAYS'").SingleAsync()));
    }

    [TestMethod]
    public async Task Categorias_LaMadreEsDelMismoTipoYNoEsLaPropia()
    {
        var knowledge = await h.Query(db => Category(db));

        // Una categoría de descargas no puede colgar de una de la base de conocimiento.
        var error = await h.Query(async db =>
        {
            db.Add(new ContentCategory(CategoryKind.Downloads, "instaladores", Now) { ParentId = knowledge });
            return await Rejected(db);
        });
        Assert.AreEqual("fk_categories_parent", error.ConstraintName);

        error = await h.Query(async db =>
        {
            db.Add(new ContentCategory(CategoryKind.KnowledgeBase, "ayuda", Now));
            return await Rejected(db);
        });
        Assert.AreEqual("ix_categories_kind_slug", error.ConstraintName);

        // El mismo slug en el otro tipo sí vale.
        await h.Db(db => Category(db, CategoryKind.Downloads));
        var self = await Assert.ThrowsAsync<PostgresException>(() => h.Db(db => db.Database.ExecuteSqlAsync($"UPDATE content.categories SET parent_id = id WHERE id = {knowledge}")));
        Assert.AreEqual("ck_categories_parent", self.ConstraintName);
    }

    [TestMethod]
    public async Task Articulos_UnaAudienciaDirigidaNecesitaProductosOGrupos()
    {
        var category = await h.Query(db => Category(db));

        var error = await h.Query(async db =>
        {
            db.Add(new Article { CategoryId = category, Slug = "a", Audience = ContentAudience.Targeted, CreatedAt = Now, UpdatedAt = Now });
            return await Rejected(db);
        });
        Assert.AreEqual("ck_articles_audience", error.ConstraintName);

        await h.Db(async db =>
        {
            db.Add(new Article { CategoryId = category, Slug = "b", Audience = ContentAudience.Targeted, ProductIds = [4], CreatedAt = Now, UpdatedAt = Now });
            await db.SaveChangesAsync();
        });
    }

    [TestMethod]
    public async Task Articulos_BorrarElArticuloBorraSusTextosYVotos_YElSlugEsUnico()
    {
        var category = await h.Query(db => Category(db));
        var id = await h.Query(db => Article(db, category));
        await h.Db(async db =>
        {
            db.Add(new ArticleText { ArticleId = id, Language = "es-AR", Title = "Cómo entrar", Body = "Cuerpo", UpdatedAt = Now });
            db.Add(new ArticleVote { ArticleId = id, Voter = "user:1", Helpful = true, VotedAt = Now });
            await db.SaveChangesAsync();
        });

        var duplicated = await h.Query(async db =>
        {
            db.Add(new Article { CategoryId = category, Slug = "como-entrar", CreatedAt = Now, UpdatedAt = Now });
            return await Rejected(db);
        });
        Assert.AreEqual("ix_articles_slug", duplicated.ConstraintName);

        await h.Db(async db => await db.Set<Article>().Where(a => a.Id == id).ExecuteDeleteAsync());

        Assert.AreEqual(0, await h.Query(db => db.Set<ArticleText>().CountAsync()));
        Assert.AreEqual(0, await h.Query(db => db.Set<ArticleVote>().CountAsync()));
    }

    [TestMethod]
    public async Task Articulos_UnaCategoriaConArticulosNoSeBorra()
    {
        var category = await h.Query(db => Category(db));
        await h.Db(db => Article(db, category));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => h.Db(db => db.Set<ContentCategory>().Where(c => c.Id == category).ExecuteDeleteAsync()));

        Assert.AreEqual("fk_articles_category", ex.ConstraintName);
    }

    [TestMethod]
    public async Task Busqueda_PliegaTildesYUsaLaRaizDelIdioma()
    {
        var category = await h.Query(db => Category(db));
        var id = await h.Query(db => Article(db, category));
        await h.Db(async db =>
        {
            db.Add(new ArticleText { ArticleId = id, Language = "es-AR", Title = "Configuración del panel", Body = "Los servidores se administran desde el panel.", Tags = "minecraft, eula", UpdatedAt = Now });
            db.Add(new ArticleText { ArticleId = id, Language = "en", Title = "Panel setup", Body = "Servers are managed from the panel.", UpdatedAt = Now });
            await db.SaveChangesAsync();
        });

        Task<int> Hits(string config, string query) => h.Query(db => db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM content.article_texts WHERE search @@ websearch_to_tsquery(CAST(@cfg AS regconfig), translate(@q, 'áéíóúüÁÉÍÓÚÜ', 'aeiouuAEIOUU'))", new NpgsqlParameter("cfg", config), new NpgsqlParameter("q", query)).SingleAsync());

        Assert.AreEqual(1, await Hits("spanish", "configuracion"));
        Assert.AreEqual(1, await Hits("spanish", "configuración"));
        Assert.AreEqual(1, await Hits("spanish", "servidor"), "La raíz une singular y plural.");
        Assert.AreEqual(1, await Hits("spanish", "eula"), "Las etiquetas también se buscan.");
        Assert.AreEqual(0, await Hits("spanish", "factura"));
        Assert.AreEqual(1, await Hits("english", "managing server"));
    }

    [TestMethod]
    public async Task Novedades_ElVencimientoEsPosteriorALaPublicacion()
    {

        var error = await h.Query(async db =>
        {
            db.Add(new Announcement { PublishAt = Now, ExpiresAt = Now.AddDays(-1), CreatedAt = Now, UpdatedAt = Now });
            return await Rejected(db);
        });

        Assert.AreEqual("ck_announcements_dates", error.ConstraintName);
    }

    [TestMethod]
    public async Task Descargas_NoSePublicaSinArchivoYLosBytesSiguenALaDescarga()
    {
        var category = await h.Query(db => Category(db, CategoryKind.Downloads, "instaladores"));

        var error = await h.Query(async db =>
        {
            db.Add(new Download { CategoryId = category, Status = PublishState.Published, CreatedAt = Now, UpdatedAt = Now });
            return await Rejected(db);
        });
        Assert.AreEqual("ck_downloads_published", error.ConstraintName);

        var id = await h.Query(async db =>
        {
            var download = new Download { CategoryId = category, Status = PublishState.Published, FileName = "agente.zip", Size = 3, CreatedAt = Now, UpdatedAt = Now };
            db.Add(download);
            await db.SaveChangesAsync();
            db.Add(new DownloadFile { DownloadId = download.Id, Bytes = [1, 2, 3] });
            await db.SaveChangesAsync();
            return download.Id;
        });
        await h.Db(async db => await db.Set<Download>().Where(d => d.Id == id).ExecuteDeleteAsync());

        Assert.AreEqual(0, await h.Query(db => db.Set<DownloadFile>().CountAsync()));
    }

    [TestMethod]
    public async Task EstadoDeLaRed_MantenimientoConVentanaYComponenteConHistorial()
    {
        var component = await h.Query(async db =>
        {
            var c = new StatusComponent { Slug = "panel", Kind = ComponentKind.Service, CreatedAt = Now, UpdatedAt = Now };
            db.Add(c);
            await db.SaveChangesAsync();
            return c.Id;
        });

        var error = await h.Query(async db =>
        {
            db.Add(new StatusEvent { Kind = StatusEventKind.Maintenance, State = StatusEventState.Scheduled, StartsAt = Now, CreatedAt = Now, UpdatedAt = Now });
            return await Rejected(db);
        });
        Assert.AreEqual("ck_status_events_maintenance", error.ConstraintName);

        var eventId = await h.Query(async db =>
        {
            var e = new StatusEvent { Kind = StatusEventKind.Incident, State = StatusEventState.Investigating, StartsAt = Now, CreatedAt = Now, UpdatedAt = Now };
            db.Add(e);
            await db.SaveChangesAsync();
            db.Add(new StatusEventComponent { EventId = e.Id, ComponentId = component, Impact = ComponentStatus.Degraded });
            await db.SaveChangesAsync();
            return e.Id;
        });

        // Un componente con historial no se borra. Borrar el evento se lleva sus vínculos.
        var blocked = await Assert.ThrowsAsync<PostgresException>(() => h.Db(db => db.Set<StatusComponent>().Where(c => c.Id == component).ExecuteDeleteAsync()));
        Assert.AreEqual("fk_status_event_components_component", blocked.ConstraintName);
        await h.Db(db => db.Set<StatusEvent>().Where(e => e.Id == eventId).ExecuteDeleteAsync());
        Assert.AreEqual(0, await h.Query(db => db.Set<StatusEventComponent>().CountAsync()));
    }

    [TestMethod]
    public async Task Suscriptores_UnaCasillaPorSuscriptorSinDistinguirMayusculas_YUnTokenUnico()
    {
        await h.Db(async db =>
        {
            db.Add(new StatusSubscriber { Email = "Ana@Example.com", Token = "t1", CreatedAt = Now });
            await db.SaveChangesAsync();
        });

        var sameEmail = await h.Query(async db =>
        {
            db.Add(new StatusSubscriber { Email = "ana@example.com", Token = "t2", CreatedAt = Now });
            return await Rejected(db);
        });
        var sameToken = await h.Query(async db =>
        {
            db.Add(new StatusSubscriber { Email = "otra@example.com", Token = "t1", CreatedAt = Now });
            return await Rejected(db);
        });

        Assert.AreEqual("ix_subscribers_email", sameEmail.ConstraintName);
        Assert.AreEqual("ix_subscribers_token", sameToken.ConstraintName);
    }

    [TestMethod]
    public async Task Cuentas_ElGrupoDeLaCuentaApuntaAUnGrupoQueExiste()
    {
        var group = await h.Query(async db =>
        {
            var g = new AccountGroup { Name = "VIP", CreatedAt = Now };
            db.Add(g);
            await db.SaveChangesAsync();
            return g.Id;
        });

        await h.Db(db => db.Accounts.Where(a => a.Id == h.AccountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.GroupId, group)));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => h.Db(db => db.Accounts.Where(a => a.Id == h.AccountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.GroupId, 9999L))));

        Assert.AreEqual("fk_accounts_group", ex.ConstraintName);
    }
}
