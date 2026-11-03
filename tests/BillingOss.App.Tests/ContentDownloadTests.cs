using System.Security.Cryptography;
using BillingOss.App.Content;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Content;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Descargas: archivos con tope y tipos permitidos, audiencia, contador y listados (sección 5 de diseno/contenidos.md).</summary>
[TestClass]
[DoNotParallelize] // Comparten una base: crear una cuesta casi un minuto con la PC cargada.
public sealed class ContentDownloadTests
{
    private const int Max = 4096;

    private static CoreHarness h = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _) =>
        h = await ContentHarness.Start(content: o =>
        {
            o.MaxDownloadBytes = Max;
            o.AllowedDownloadExtensions = ["zip", "txt", "exe", "pdf"];
        });

    [ClassCleanup]
    public static async Task Stop() => await ContentHarness.Stop(h);

    [TestInitialize]
    public Task Clean() => h.Reset();

    private static byte[] Bytes(int length, int seed = 7) => [.. Enumerable.Range(0, length).Select(i => (byte)((i * 31) + seed))];

    private static Task<long> NewCategory(string name = "Instaladores", long? parent = null, bool hidden = false) =>
        h.Use<CategoryService, long>(c => c.Create(CategoryKind.Downloads, new CategoryInput(parent, null, 0, hidden, new Dictionary<string, ContentText> { ["es-AR"] = new(name) })));

    private static DownloadInput Input(long category, string title = "Agente de monitoreo", ContentAudience audience = ContentAudience.Customers, long[]? products = null, string? english = null, int sort = 0, int? revision = null)
    {
        var texts = new Dictionary<string, ContentText> { ["es-AR"] = new(title, "Instalador para Windows") };
        if (english is not null)
        {
            texts["en"] = new ContentText(english, "Windows installer");
        }

        return new DownloadInput(category, sort, audience, products ?? [], [], texts, revision);
    }

    private static Task<long> Create(DownloadInput input) => h.Use<DownloadService, long>(d => d.Create(input));

    private static Task<UploadedFile> Upload(long id, string? name, byte[] content) =>
        h.Use<DownloadService, UploadedFile>(d => d.Upload(id, name, new MemoryStream(content)));

    private static async Task<long> WithFile(long category, string title = "Agente", ContentAudience audience = ContentAudience.Customers, long[]? products = null, byte[]? content = null, string name = "agente.zip", bool publish = true, int sort = 0)
    {
        var id = await Create(Input(category, title, audience, products, sort: sort));
        await Upload(id, name, content ?? Bytes(100));
        if (publish)
        {
            await h.Use<DownloadService, bool>(async d => { await d.Publish(id); return true; });
        }

        return id;
    }

    /// <summary>Abre el archivo y lo lee entero adentro del alcance: el flujo vive mientras viva el contexto.</summary>
    private static Task<(byte[] Bytes, string Name, string Type)?> Fetch(ContentViewer viewer, long id) =>
        h.Use<DownloadService, (byte[], string, string)?>(async d =>
        {
            var file = await d.Open(viewer, id);
            if (file is null)
            {
                return null;
            }

            await using (file.Content)
            {
                using var buffer = new MemoryStream();
                await file.Content.CopyToAsync(buffer);
                return (buffer.ToArray(), file.FileName, file.ContentType);
            }
        });

    private static async Task<long> Count(long id) => await h.Query(db => db.Set<Download>().Where(d => d.Id == id).Select(d => d.DownloadCount).SingleAsync());

    private sealed class NeverRead : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("No se tendría que haber leído el cuerpo.");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task Subir_GuardaLosBytes_CalculaElShaYElTipo_YLimpiaElNombre()
    {
        var category = await NewCategory();
        var id = await Create(Input(category));
        var content = Bytes(1500);

        var uploaded = await Upload(id, "..\\..\\Mi agente (v2).zip", content);

        Assert.AreEqual("Mi_agente_v2_.zip", uploaded.FileName);
        Assert.AreEqual("application/zip", uploaded.ContentType);
        Assert.AreEqual(1500L, uploaded.Size);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(content)), uploaded.Sha256);
        var row = (await h.Use<DownloadService, DownloadRow?>(d => d.Get(id)))!;
        Assert.AreEqual(uploaded.Sha256, row.Download.Sha256);
        Assert.IsTrue(row.Download.HasFile);
        var staff = await h.Use<DownloadService, byte[]?>(async d =>
        {
            var file = await d.OpenForStaff(id);
            await using (file!.Content)
            {
                using var buffer = new MemoryStream();
                await file.Content.CopyToAsync(buffer);
                return buffer.ToArray();
            }
        });
        CollectionAssert.AreEqual(content, staff);
    }

    [TestMethod]
    public async Task Subir_ElTopeSeRespeta_ConElBordeIncluido_YNoDejaNadaAMedias()
    {
        var category = await NewCategory();
        var id = await Create(Input(category));
        await Upload(id, "ok.zip", Bytes(Max));

        var big = await Assert.ThrowsAsync<ContentRejectedException>(() => Upload(id, "grande.zip", Bytes(Max + 1)));

        Assert.AreEqual(413, big.StatusCode);
        var row = (await h.Use<DownloadService, DownloadRow?>(d => d.Get(id)))!.Download;
        Assert.AreEqual("ok.zip", row.FileName, "El archivo anterior queda como estaba.");
        Assert.AreEqual((long)Max, row.Size);
        Assert.AreEqual((long)Max, await h.Query(db => db.Database.SqlQueryRaw<long>("SELECT length(bytes)::bigint AS \"Value\" FROM content.download_files WHERE download_id = {0}", id).SingleAsync()));
    }

    [TestMethod]
    [DataRow("pagina.html")]
    [DataRow("imagen.svg")]
    [DataRow("script.js")]
    [DataRow("web.php")]
    [DataRow("agente.zip.html")]
    [DataRow("agente.png")]
    public async Task Subir_UnaExtensionFueraDeLaLista_Da415(string name)
    {
        var category = await NewCategory();
        var id = await Create(Input(category));

        var rejected = await Assert.ThrowsAsync<ContentRejectedException>(() => Upload(id, name, Bytes(10)));

        Assert.AreEqual(415, rejected.StatusCode);
        Assert.IsFalse((await h.Use<DownloadService, DownloadRow?>(d => d.Get(id)))!.Download.HasFile);
    }

    [TestMethod]
    public async Task Subir_LaExtensionNoDistingueMayusculas_YElNombreNecesitaExtension()
    {
        var category = await NewCategory();
        var id = await Create(Input(category));

        Assert.AreEqual("AGENTE.ZIP", (await Upload(id, "AGENTE.ZIP", Bytes(10))).FileName);
        await Assert.ThrowsAsync<ValidationException>(() => Upload(id, "sin-extension", Bytes(10)));
        await Assert.ThrowsAsync<ValidationException>(() => Upload(id, "..", Bytes(10)));
        await Assert.ThrowsAsync<ValidationException>(() => Upload(id, null, Bytes(10)));
        var empty = await Assert.ThrowsAsync<ValidationException>(() => Upload(id, "vacio.zip", []));
        Assert.Contains("vacío", empty.Message);
    }

    [TestMethod]
    public async Task Subir_ADescargaInexistente_NoLeeElCuerpo()
    {
        var missing = await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Use<DownloadService, UploadedFile>(d => d.Upload(404, "a.zip", new NeverRead())));

        Assert.Contains("404", missing.Message);
    }

    [TestMethod]
    public async Task Publicar_PideElArchivo_YLaBaseTambien()
    {
        var category = await NewCategory();
        var id = await Create(Input(category));

        var error = await Assert.ThrowsAsync<ValidationException>(() => h.Use<DownloadService, bool>(async d => { await d.Publish(id); return true; }));
        Assert.Contains("no tiene archivo", error.Message);

        await Upload(id, "agente.zip", Bytes(10));
        await h.Use<DownloadService, bool>(async d => { await d.Publish(id); return true; });
        Assert.AreEqual(PublishState.Published, (await h.Use<DownloadService, DownloadRow?>(d => d.Get(id)))!.Download.Status);

        // La restricción de la base es la última palabra.
        var db = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => h.Db(c => c.Database.ExecuteSqlAsync($"UPDATE content.downloads SET size = NULL WHERE id = {id}")));
        Assert.AreEqual("ck_downloads_published", db.ConstraintName);
    }

    [TestMethod]
    public async Task Reemplazar_ElArchivoMantieneElEstadoYElContador()
    {
        var category = await NewCategory();
        var id = await WithFile(category);
        var customer = new ContentViewer(true, [], []);
        await Fetch(customer, id);
        await Fetch(customer, id);

        var second = Bytes(300, seed: 99);
        await Upload(id, "agente-v2.zip", second);

        var row = (await h.Use<DownloadService, DownloadRow?>(d => d.Get(id)))!.Download;
        Assert.AreEqual(PublishState.Published, row.Status);
        Assert.AreEqual(2L, row.DownloadCount);
        var file = (await Fetch(customer, id))!.Value;
        CollectionAssert.AreEqual(second, file.Bytes);
        Assert.AreEqual("agente-v2.zip", file.Name);
        Assert.AreEqual(1, await h.Query(db => db.Set<DownloadFile>().CountAsync()), "Reemplazar no deja el archivo viejo.");
    }

    [TestMethod]
    public async Task Audiencia_ElArchivoSoloSeEntregaALosQueCorresponden_YElContadorSoloSumaEntregas()
    {
        var category = await NewCategory();
        var product = await h.Query(async db =>
        {
            var p = new Product { Name = "Game server " + Guid.NewGuid().ToString("N")[..6], Kind = ProductKind.Manual };
            db.Add(p);
            await db.SaveChangesAsync();
            return p.Id;
        });
        var content = Bytes(500);
        var forCustomers = await WithFile(category, "Para clientes", content: content);
        var forProduct = await WithFile(category, "Del producto", ContentAudience.Targeted, [product], content, "producto.zip");
        var forAll = await WithFile(category, "Pública", ContentAudience.Public, content: content, name: "publica.zip");
        var draft = await WithFile(category, "Borrador", content: content, name: "borrador.zip", publish: false);

        var anonymous = ContentViewer.Anonymous;
        var customer = new ContentViewer(true, [], []);
        var owner = new ContentViewer(true, [product], []);

        Assert.IsNull(await Fetch(anonymous, forCustomers));
        Assert.IsNull(await Fetch(customer, forProduct));
        Assert.IsNull(await Fetch(anonymous, forProduct));
        Assert.IsNull(await Fetch(owner, draft));
        Assert.IsNull(await Fetch(owner, 987654));
        Assert.AreEqual(0L, await Count(forCustomers));
        Assert.AreEqual(0L, await Count(forProduct));

        CollectionAssert.AreEqual(content, (await Fetch(customer, forCustomers))!.Value.Bytes);
        CollectionAssert.AreEqual(content, (await Fetch(owner, forProduct))!.Value.Bytes);
        CollectionAssert.AreEqual(content, (await Fetch(anonymous, forAll))!.Value.Bytes);
        Assert.AreEqual(1L, await Count(forCustomers));
        Assert.AreEqual(1L, await Count(forProduct));
        Assert.AreEqual(1L, await Count(forAll));

        // El staff baja sin sumar.
        await h.Use<DownloadService, bool>(async d => { await (await d.OpenForStaff(forCustomers))!.Content.DisposeAsync(); return true; });
        Assert.AreEqual(1L, await Count(forCustomers));

        // Archivado, ya no se entrega.
        await h.Use<DownloadService, bool>(async d => { await d.Archive(forAll); return true; });
        Assert.IsNull(await Fetch(anonymous, forAll));
    }

    [TestMethod]
    public async Task Contador_DiezPedidosALaVezSumanDiez_YDosFlujosAbiertosNoSePisan()
    {
        var category = await NewCategory();
        var content = Bytes(3000);
        var id = await WithFile(category, content: content);
        var customer = new ContentViewer(true, [], []);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Fetch(customer, id)));

        Assert.AreEqual(10L, await Count(id));
        Assert.IsTrue(results.All(r => r!.Value.Bytes.AsSpan().SequenceEqual(content)));
    }

    [TestMethod]
    public async Task Listado_AgrupadoPorCategoriaConSuRuta_SinOcultasNiLoQueNoCorresponde()
    {
        var root = await NewCategory("Instaladores");
        var windows = await NewCategory("Windows", root);
        var docs = await NewCategory("Documentos");
        var secret = await NewCategory("Oculta", hidden: true);
        await WithFile(windows, "Agente", name: "agente.exe", sort: 2);
        await WithFile(windows, "Cliente VPN", name: "vpn.exe", sort: 1);
        await WithFile(docs, "Manual", ContentAudience.Public, name: "manual.pdf");
        await WithFile(secret, "En categoría oculta", ContentAudience.Public, name: "oculta.pdf");
        await WithFile(docs, "Borrador", name: "borrador.pdf", publish: false);

        var customer = await h.Use<DownloadService, IReadOnlyList<DownloadGroup>>(d => d.Visible(new ContentViewer(true, [], []), "es-AR"));
        var anonymous = await h.Use<DownloadService, IReadOnlyList<DownloadGroup>>(d => d.Visible(ContentViewer.Anonymous, "es-AR"));

        CollectionAssert.AreEqual(new[] { "Documentos", "Instaladores / Windows" }, customer.Select(g => g.Title).ToArray());
        CollectionAssert.AreEqual(new[] { "Cliente VPN", "Agente" }, customer[1].Items.Select(i => i.Title).ToArray(), "Por orden y después por ID.");
        Assert.AreEqual("vpn.exe", customer[1].Items[0].FileName);
        Assert.AreEqual(64, customer[1].Items[0].Sha256.Length);
        CollectionAssert.AreEqual(new[] { "Documentos" }, anonymous.Select(g => g.Title).ToArray());
        CollectionAssert.AreEqual(new[] { "Manual" }, anonymous[0].Items.Select(i => i.Title).ToArray());
    }

    [TestMethod]
    public async Task Edicion_LaRevisionYLaAudienciaDirigida()
    {
        var category = await NewCategory();
        var id = await Create(Input(category));
        var revision = (await h.Use<DownloadService, DownloadRow?>(d => d.Get(id)))!.Download.Revision;

        await h.Use<DownloadService, bool>(async d => { await d.Update(id, Input(category, "Nuevo título", revision: revision)); return true; });
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Use<DownloadService, bool>(async d => { await d.Update(id, Input(category, "Otro", revision: revision)); return true; }));
        var targeted = await Assert.ThrowsAsync<ValidationException>(() => Create(Input(category, audience: ContentAudience.Targeted)));
        var help = await h.Use<CategoryService, long>(c => c.Create(CategoryKind.KnowledgeBase, new CategoryInput(null, null, 0, false, new Dictionary<string, ContentText> { ["es-AR"] = new("Ayuda") })));
        var kind = await Assert.ThrowsAsync<ValidationException>(() => Create(Input(help)));

        Assert.Contains("Otra persona modificó", stale.Message);
        Assert.Contains("al menos un producto", targeted.Message);
        Assert.Contains("de descargas", kind.Message);
        Assert.AreEqual("Nuevo título", ContentTexts.Pick((await h.Use<DownloadService, DownloadRow?>(d => d.Get(id)))!.Texts, "es")!.Title);
    }

    [TestMethod]
    public async Task Borrar_SeLlevaLosBytes_YLaCategoriaConDescargasNoSeBorra()
    {
        var category = await NewCategory();
        var id = await WithFile(category);

        var used = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Use<CategoryService, bool>(async c => { await c.Delete(CategoryKind.Downloads, category); return true; }));
        await h.Use<DownloadService, bool>(async d => { await d.Delete(id); return true; });

        Assert.Contains("subcategorías o contenido", used.Message);
        Assert.AreEqual(0, await h.Query(db => db.Set<DownloadFile>().CountAsync()));
        await h.Use<CategoryService, bool>(async c => { await c.Delete(CategoryKind.Downloads, category); return true; });
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Use<DownloadService, bool>(async d => { await d.Delete(id); return true; }));
    }

    [TestMethod]
    public void Opciones_LaListaDeFabricaYLaConfigurada()
    {
        var factory = new ContentOptions();
        var custom = new ContentOptions { AllowedDownloadExtensions = [".ZIP", "Txt "] };

        CollectionAssert.Contains(factory.Extensions.ToList(), "zip");
        CollectionAssert.Contains(factory.Extensions.ToList(), "png");
        CollectionAssert.DoesNotContain(factory.Extensions.ToList(), "html");
        CollectionAssert.AreEquivalent(new[] { "zip", "txt" }, custom.Extensions.ToList());
        Assert.AreEqual(50L * 1024 * 1024, factory.MaxDownloadBytes);
    }
}
