using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.Domain.Content;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>
/// Las pantallas del panel de contenidos de punta a punta (sección 9 de diseno/contenidos.md): quién entra a cada una, los
/// formularios con sus dos idiomas, la revisión contra ediciones a la vez, la vista previa, la subida de archivos y los eventos
/// de la red. Cada prueba mira después lo que ve el cliente en el sitio.
/// </summary>
[TestClass]
[DoNotParallelize] // Comparten un host y su base: crear una cuesta casi un minuto con la PC cargada.
public sealed partial class ContentAdminTests
{
    private static ContentSite site = null!;
    private static Client writer = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        site = await ContentSite.Start();
        writer = await site.Staff("escritor@example.com", StaffPermissions.ContentRead, StaffPermissions.ContentWrite, StaffPermissions.StatusWrite);
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        writer.Dispose();
        await site.DisposeAsync();
    }

    public TestContext TestContext { get; set; } = null!;

    /// <summary>Si una prueba falla, los errores que registró la aplicación ayudan a entender un 500.</summary>
    [TestCleanup]
    public void ShowErrors()
    {
        if (TestContext.CurrentTestOutcome != UnitTestOutcome.Passed)
        {
            TestContext.WriteLine(string.Join(Environment.NewLine, site.Errors));
        }
    }

    /// <summary>El último número de una dirección de redirección: <c>.../articulos/12?guardado=1</c> es 12.</summary>
    private static long IdOf(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode, "Se esperaba que guardar redirigiera.");
        var match = LastNumber().Match(response.Location());
        return match.Success ? long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : throw new AssertFailedException($"La redirección no trae un ID: {response.Location()}");
    }

    /// <summary>La revisión que trae un formulario abierto: el campo oculto <c>*.Revision</c>.</summary>
    private static string RevisionIn(string html) =>
        Revision().Match(html) is { Success: true } m ? m.Groups[1].Value : throw new AssertFailedException("El formulario no trae la revisión.");

    private static (string, string)[] Texts(string prefix, string spanishTitle, string? spanishBody = null, string? englishTitle = null, string? englishBody = null, string? summary = null)
    {
        var fields = new List<(string, string)>
        {
            ($"{prefix}[0].Language", "es-AR"),
            ($"{prefix}[0].Title", spanishTitle),
            ($"{prefix}[0].Body", spanishBody ?? string.Empty),
            ($"{prefix}[0].Summary", summary ?? string.Empty),
        };
        if (englishTitle is not null)
        {
            fields.Add(($"{prefix}[1].Language", "en"));
            fields.Add(($"{prefix}[1].Title", englishTitle));
            fields.Add(($"{prefix}[1].Body", englishBody ?? string.Empty));
        }

        return [.. fields];
    }

    private static (string, string)[] Join(params (string, string)[][] parts) => [.. parts.SelectMany(p => p)];

    [TestMethod]
    public async Task Permisos_CadaAreaPideElSuyo_YSinSesionOSinPermisoNoSeEntra()
    {
        using var nothing = await site.Staff("nada@example.com");
        using var reader = await site.Staff("lector@example.com", StaffPermissions.ContentRead);
        using var network = await site.Staff("red@example.com", StaffPermissions.StatusWrite);
        const string editor = "/admin/conocimiento/articulos";
        const string newsEditor = "/admin/anuncios/nueva";
        const string downloadEditor = "/admin/descargas/nueva";
        const string category = "/admin/conocimiento/categorias";

        // Sin ningún permiso de contenidos no se ve ni la portada.
        foreach (var path in new[] { "/admin/contenidos", "/admin/conocimiento", "/admin/anuncios", "/admin/descargas", "/admin/estado-red", editor })
        {
            using var response = await nothing.Get(path);
            Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode, path);
            StringAssert.Contains(response.Location(), "/estado/403");
        }

        // Quien solo lee ve las listas y la portada, pero no los editores ni la red.
        foreach (var path in new[] { "/admin/contenidos", "/admin/conocimiento", "/admin/anuncios", "/admin/descargas" })
        {
            Assert.AreEqual(HttpStatusCode.OK, (await reader.Read(path)).Status, path);
        }

        foreach (var path in new[] { editor, newsEditor, downloadEditor, category, "/admin/estado-red", "/admin/estado-red/nueva", "/admin/estado-red/componentes" })
        {
            using var response = await reader.Get(path);
            Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode, path);
            StringAssert.Contains(response.Location(), "/estado/403");
        }

        // Los botones de crear no aparecen para quien solo lee.
        Assert.DoesNotContain(editor, (await reader.Read("/admin/conocimiento")).Html);

        // Quien administra la red entra a la red, y su menú lo lleva ahí. La portada de contenidos es de quien lee.
        var home = await network.Read("/admin/estado-red");
        Assert.AreEqual(HttpStatusCode.OK, home.Status);
        StringAssert.Contains(home.Html, "href=\"/admin/estado-red\"");
        using var denied = await network.Get("/admin/contenidos");
        StringAssert.Contains(denied.Location(), "/estado/403");

        // Sin sesión del staff, al ingreso. La cookie de un cliente tampoco abre el panel.
        using var anonymous = site.Visitor();
        using var login = await anonymous.Get("/admin/contenidos");
        Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
        StringAssert.Contains(login.Location(), "/admin/ingresar");

        // Un POST sin permiso tampoco hace nada.
        using var post = await reader.Post("/admin/conocimiento", editor + "?handler=Save", ("Input.CategoryId", "1"));
        Assert.AreEqual(HttpStatusCode.Redirect, post.StatusCode);
        StringAssert.Contains(post.Location(), "/estado/403");
    }

    [TestMethod]
    public async Task BaseDeConocimiento_CategoriaYArticuloPorElPanel_DeBorradorAPublicadoYBorrado()
    {
        // Una categoría con sus dos idiomas.
        using var newCategory = await writer.Post("/admin/conocimiento/categorias", "/admin/conocimiento/categorias?handler=Save",
            Join([("Input.Slug", "panel-de-control"), ("Input.SortOrder", "1")], Texts("Input.Texts", "Panel de control", null, "Control panel", null, "Todo sobre el panel")));
        var categoryId = IdOf(newCategory);
        var categoryPage = await writer.Html($"/admin/conocimiento/categorias/{categoryId}?guardado=1");
        StringAssert.Contains(categoryPage, "Listo, guardamos los cambios.");
        StringAssert.Contains(categoryPage, "value=\"panel-de-control\"");
        StringAssert.Contains(categoryPage, "Control panel");

        // Un artículo en borrador, con el cuerpo en Markdown y HTML que no debe pasar.
        const string body = "Entrá a **Mi cuenta**.\n\n<script>alert(1)</script>";
        using var created = await writer.Post("/admin/conocimiento/articulos", "/admin/conocimiento/articulos?handler=Save",
            Join(
                [("Input.CategoryId", categoryId.ToString()), ("Input.Slug", string.Empty), ("Input.SortOrder", "0"), ("Input.Featured", "true"), ("Input.Audience", "Public"), ("Input.Revision", string.Empty)],
                Texts("Input.Texts", "Cambiar la contraseña", body, "Change your password", "Go to **My account**.")));
        var id = IdOf(created);
        var editorUrl = $"/admin/conocimiento/articulos/{id}";
        var editor = await writer.Html(editorUrl);
        StringAssert.Contains(editor, "Cambiar la contraseña");
        StringAssert.Contains(editor, "Change your password");
        StringAssert.Contains(editor, "cambiar-la-contrasena");
        StringAssert.Contains(editor, "Borrador");

        var list = await writer.Html("/admin/conocimiento?q=contrase%C3%B1a");
        StringAssert.Contains(list, "Cambiar la contraseña");
        StringAssert.Contains(list, "es-AR, en");
        Assert.DoesNotContain("Cambiar la contraseña", await writer.Html("/admin/conocimiento?state=Published"));

        // Un borrador no se ve en el sitio. Publicado sí, con el HTML escapado.
        using var visitor = site.Visitor();
        Assert.AreEqual(HttpStatusCode.NotFound, (await visitor.Read("/conocimiento/cambiar-la-contrasena")).Status);
        using (var publish = await writer.Post(editorUrl, editorUrl + "?handler=Publish", ("revision", RevisionIn(editor))))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, publish.StatusCode);
        }

        var published = await visitor.Html("/conocimiento/cambiar-la-contrasena");
        StringAssert.Contains(published, "<strong>Mi cuenta</strong>");
        Assert.DoesNotContain("<script>alert(1)", published);
        StringAssert.Contains(await writer.Html(editorUrl), "Publicado");

        // Guardar con la revisión con que se abrió el formulario, ya vieja, falla y no pisa lo guardado.
        var stale = RevisionIn(editor);
        using var conflict = await writer.Post(editorUrl, editorUrl + "?handler=Save",
            Join(
                [("Input.CategoryId", categoryId.ToString()), ("Input.Slug", "cambiar-la-contrasena"), ("Input.SortOrder", "0"), ("Input.Audience", "Public"), ("Input.Revision", stale)],
                Texts("Input.Texts", "Título pisado", body)));
        var conflictHtml = await conflict.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, conflict.StatusCode);
        StringAssert.Contains(conflictHtml, "Otra persona modificó el artículo");
        StringAssert.Contains(conflictHtml, "Título pisado", "El formulario conserva lo que se escribió.");
        StringAssert.Contains(await visitor.Html("/conocimiento/cambiar-la-contrasena"), "Cambiar la contraseña");

        // Con la revisión vigente se guarda, y sacar el inglés lo saca del artículo.
        var current = RevisionIn(await writer.Html(editorUrl));
        using var saved = await writer.Post(editorUrl, editorUrl + "?handler=Save",
            Join(
                [("Input.CategoryId", categoryId.ToString()), ("Input.Slug", "cambiar-la-contrasena"), ("Input.SortOrder", "0"), ("Input.Audience", "Public"), ("Input.Revision", current)],
                Texts("Input.Texts", "Cambiar tu contraseña", body)));
        Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode);
        var after = await writer.Html(editorUrl);
        StringAssert.Contains(after, "Cambiar tu contraseña");
        Assert.DoesNotContain("Change your password", after);

        // La vista previa convierte el Markdown con las reglas del sitio.
        using var preview = await writer.Post(editorUrl, editorUrl + "?handler=Preview", Join([("which", "0")], Texts("Input.Texts", "x", "Hola **mundo** <script>x</script> [a](javascript:alert(1))")));
        var previewHtml = await preview.Content.ReadAsStringAsync();
        Assert.AreEqual("text/html", preview.Content.Headers.ContentType?.MediaType);
        StringAssert.Contains(previewHtml, "<strong>mundo</strong>");
        Assert.DoesNotContain("<script>", previewHtml);
        Assert.DoesNotContain("javascript:", previewHtml);

        // Borrar pide marcar la casilla.
        using var noConfirm = await writer.Post(editorUrl, editorUrl + "?handler=Delete");
        StringAssert.Contains(await noConfirm.Content.ReadAsStringAsync(), "Marcá la casilla");
        Assert.AreEqual(HttpStatusCode.OK, (await visitor.Read("/conocimiento/cambiar-la-contrasena")).Status);
        using var deleted = await writer.Post(editorUrl, editorUrl + "?handler=Delete", ("confirm", "true"));
        Assert.AreEqual(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await visitor.Read("/conocimiento/cambiar-la-contrasena")).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await writer.Read(editorUrl)).Status);

        // Una categoría vacía se borra, y una dirección repetida da un mensaje y no un error.
        using var duplicate = await writer.Post("/admin/conocimiento/categorias", "/admin/conocimiento/categorias?handler=Save",
            Join([("Input.Slug", "panel-de-control")], Texts("Input.Texts", "Otra")));
        StringAssert.Contains(await duplicate.Content.ReadAsStringAsync(), "ya está en uso");
        var categoryUrl = $"/admin/conocimiento/categorias/{categoryId}";
        using var categoryDeleted = await writer.Post(categoryUrl, categoryUrl + "?handler=Delete", ("confirm", "true"));
        Assert.AreEqual(HttpStatusCode.Redirect, categoryDeleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await writer.Read(categoryUrl)).Status);
    }

    [TestMethod]
    public async Task Novedades_ProgramarPublicarRetirarYBorrar_ConLaHoraDelProveedor()
    {
        using var created = await writer.Post("/admin/anuncios/nueva", "/admin/anuncios/nueva?handler=Save",
            Join([("Input.Audience", "Public"), ("Input.Pinned", "true"), ("Input.ExpiresText", string.Empty), ("Input.Revision", string.Empty)],
                Texts("Input.Texts", "Corte del sábado", "Habrá un **corte** programado.", "Saturday outage", "There will be a **scheduled** outage.")));
        var id = IdOf(created);
        var url = $"/admin/anuncios/{id}";
        var editor = await writer.Html(url);
        StringAssert.Contains(editor, "Corte del sábado");
        StringAssert.Contains(await writer.Html("/admin/anuncios"), "Borrador");

        // Programada para el futuro: el staff la ve programada y el cliente todavía no.
        using var visitor = site.Visitor();
        using (var bad = await writer.Post(url, url + "?handler=Publish", ("publishAt", "mañana"), ("revision", RevisionIn(editor))))
        {
            StringAssert.Contains(await bad.Content.ReadAsStringAsync(), "No entendimos la fecha");
        }

        using (var scheduled = await writer.Post(url, url + "?handler=Publish", ("publishAt", "2031-05-20T10:30"), ("revision", RevisionIn(editor))))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, scheduled.StatusCode);
        }

        var plan = await site.Use<AnnouncementService, AnnouncementRow?>(a => a.Get(id));
        Assert.AreEqual(AnnouncementPhase.Scheduled, plan!.Phase);
        Assert.AreEqual(new DateTimeOffset(2031, 5, 20, 13, 30, 0, TimeSpan.Zero), plan.Announcement.PublishAt, "10:30 en Buenos Aires son las 13:30 UTC.");
        var programmed = await writer.Html(url);
        StringAssert.Contains(programmed, "Programada");
        StringAssert.Contains(programmed, "Reprogramar");
        StringAssert.Contains(programmed, "2031-05-20T10:30");
        Assert.DoesNotContain("Corte del sábado", await visitor.Html("/anuncios"));

        // Reprogramarla sin fecha la publica ya.
        using (var now = await writer.Post(url, url + "?handler=Publish", ("publishAt", string.Empty), ("revision", RevisionIn(programmed))))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, now.StatusCode);
        }

        StringAssert.Contains(await visitor.Html("/anuncios"), "Corte del sábado");
        StringAssert.Contains(await writer.Html("/admin/anuncios?state=Published"), "Corte del sábado");

        // Un vencimiento anterior a la publicación se rechaza con un mensaje.
        using var past = await writer.Post(url, url + "?handler=Save",
            Join([("Input.Audience", "Public"), ("Input.ExpiresText", "2001-01-01T00:00"), ("Input.Revision", RevisionIn(await writer.Html(url)))], Texts("Input.Texts", "Corte del sábado", "Cuerpo.")));
        StringAssert.Contains(await past.Content.ReadAsStringAsync(), "posterior a la publicación");

        // Retirarla la vuelve a borrador, archivarla la saca, y borrarla pide la casilla.
        using (var unpublish = await writer.Post(url, url + "?handler=Unpublish", ("revision", RevisionIn(await writer.Html(url)))))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, unpublish.StatusCode);
        }

        Assert.DoesNotContain("Corte del sábado", await visitor.Html("/anuncios"));
        using (var archive = await writer.Post(url, url + "?handler=Archive", ("revision", RevisionIn(await writer.Html(url)))))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, archive.StatusCode);
        }

        StringAssert.Contains(await writer.Html(url), "Archivado");
        using (var noConfirm = await writer.Post(url, url + "?handler=Delete"))
        {
            StringAssert.Contains(await noConfirm.Content.ReadAsStringAsync(), "Marcá la casilla");
        }

        using (var deleted = await writer.Post(url, url + "?handler=Delete", ("confirm", "true")))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, deleted.StatusCode);
        }

        Assert.AreEqual(HttpStatusCode.NotFound, (await writer.Read(url)).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await writer.Read("/admin/anuncios/no-es-un-numero")).Status);
    }

    [TestMethod]
    public async Task Descargas_CategoriaDescargaYArchivoPorElPanel_ConTopeTipoYContador()
    {
        using var category = await writer.Post("/admin/descargas/categorias", "/admin/descargas/categorias?handler=Save",
            Join([("Input.Slug", "instaladores")], Texts("Input.Texts", "Instaladores", null, "Installers")));
        var categoryId = IdOf(category);

        using var created = await writer.Post("/admin/descargas/nueva", "/admin/descargas/nueva?handler=Save",
            Join([("Input.CategoryId", categoryId.ToString()), ("Input.SortOrder", "0"), ("Input.Audience", "Public"), ("Input.Revision", string.Empty)],
                Texts("Input.Texts", "Agente de monitoreo", null, "Monitoring agent", null, "Instala el agente.")));
        var id = IdOf(created);
        var url = $"/admin/descargas/{id}";
        StringAssert.Contains(await writer.Html("/admin/descargas"), "Sin archivo");

        // Sin archivo no se publica.
        using (var noFile = await writer.Post(url, url + "?handler=Publish", ("revision", RevisionIn(await writer.Html(url)))))
        {
            StringAssert.Contains(await noFile.Content.ReadAsStringAsync(), "no tiene archivo");
        }

        // Una extensión que un navegador ejecuta no se acepta, y no deja nada a medias.
        using (var html = await writer.Upload(url, url + "?handler=Upload", "file", "pagina.html", "<h1>x</h1>"u8.ToArray()))
        {
            StringAssert.Contains(await html.Content.ReadAsStringAsync(), "No se aceptan archivos .html");
        }

        Assert.IsNull(await site.Ui.Query(db => db.Set<Download>().Where(d => d.Id == id).Select(d => d.Size).SingleAsync()));
        using (var empty = await writer.Post(url, url + "?handler=Upload"))
        {
            StringAssert.Contains(await empty.Content.ReadAsStringAsync(), "Elegí un archivo");
        }

        // Sube el archivo: queda el tamaño, el SHA-256 y el nombre limpio.
        var bytes = Encoding.UTF8.GetBytes("PK-contenido-del-agente");
        using (var upload = await writer.Upload(url, url + "?handler=Upload", "file", "Agente v1 (final).zip", bytes))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, upload.StatusCode);
        }

        var withFile = await writer.Html(url);
        StringAssert.Contains(withFile, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        StringAssert.Contains(withFile, "Agente_v1_final_.zip");
        StringAssert.Contains(withFile, "Reemplazar el archivo");

        // El staff baja el archivo sin sumar al contador, como adjunto.
        using (var staffFile = await writer.Get(url + "?handler=File"))
        {
            Assert.AreEqual(HttpStatusCode.OK, staffFile.StatusCode);
            CollectionAssert.AreEqual(bytes, await staffFile.Content.ReadAsByteArrayAsync());
            Assert.AreEqual("attachment", staffFile.Content.Headers.ContentDisposition?.DispositionType);
            Assert.AreEqual("nosniff", string.Join(' ', staffFile.Headers.GetValues("X-Content-Type-Options")));
        }

        Assert.AreEqual(0, await site.Ui.Query(db => db.Set<Download>().Where(d => d.Id == id).Select(d => d.DownloadCount).SingleAsync()));

        // Publicada, la baja el cliente y el contador sube. Reemplazar el archivo no lo reinicia ni la despublica.
        using (var publish = await writer.Post(url, url + "?handler=Publish", ("revision", RevisionIn(withFile))))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, publish.StatusCode);
        }

        using var visitor = site.Visitor();
        StringAssert.Contains(await visitor.Html("/descargas"), "Agente de monitoreo");
        using (var file = await visitor.Get($"/descargas/{id}/archivo"))
        {
            CollectionAssert.AreEqual(bytes, await file.Content.ReadAsByteArrayAsync());
        }

        var replacement = Encoding.UTF8.GetBytes("PK-version-2");
        using (var replace = await writer.Upload(url, url + "?handler=Upload", "file", "agente.zip", replacement))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, replace.StatusCode);
        }

        var row = await site.Ui.Query(db => db.Set<Download>().AsNoTracking().SingleAsync(d => d.Id == id));
        Assert.AreEqual(PublishState.Published, row.Status);
        Assert.AreEqual(1, row.DownloadCount);
        Assert.AreEqual(replacement.Length, row.Size);

        // Retirarla y borrarla: el archivo se va con ella.
        using (var archive = await writer.Post(url, url + "?handler=Archive", ("revision", RevisionIn(await writer.Html(url)))))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, archive.StatusCode);
        }

        Assert.AreEqual(HttpStatusCode.NotFound, (await visitor.Read($"/descargas/{id}/archivo")).Status);
        using (var deleted = await writer.Post(url, url + "?handler=Delete", ("confirm", "true")))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, deleted.StatusCode);
        }

        Assert.AreEqual(0, await site.Ui.Query(db => db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM content.download_files WHERE download_id = {id}").SingleAsync()));
    }

    [TestMethod]
    public async Task EstadoDeLaRed_ComponentesIncidenteYMantenimientoPorElPanel_ConAvisosYSuscriptores()
    {
        // Un grupo con un servidor.
        using var group = await writer.Post("/admin/estado-red/componentes", "/admin/estado-red/componentes?handler=Save",
            Join([("Input.Kind", "Group"), ("Input.Slug", "centro-de-datos"), ("Input.SortOrder", "0")], Texts("Input.Texts", "Centro de datos", null, "Data center")));
        var groupId = IdOf(group);
        using var server = await writer.Post("/admin/estado-red/componentes", "/admin/estado-red/componentes?handler=Save",
            Join([("Input.Kind", "Server"), ("Input.ParentId", groupId.ToString()), ("Input.SortOrder", "0")], Texts("Input.Texts", "Nodo de cómputo 1", null, "Compute node 1", null, "Aloja los VPS")));
        var serverId = IdOf(server);
        var network = await writer.Html("/admin/estado-red");
        StringAssert.Contains(network, "Centro de datos");
        StringAssert.Contains(network, "Nodo de cómputo 1");
        StringAssert.Contains(network, "Operativo");
        StringAssert.Contains(network, "/admin/estado-red/nueva");
        StringAssert.Contains(await writer.Html("/admin/estado-red/nueva"), "Abrir un incidente");

        // Un grupo no cuelga de otro grupo, y el tipo no se cambia.
        using (var nested = await writer.Post("/admin/estado-red/componentes", "/admin/estado-red/componentes?handler=Save",
                   Join([("Input.Kind", "Group"), ("Input.ParentId", groupId.ToString())], Texts("Input.Texts", "Subgrupo"))))
        {
            StringAssert.Contains(await nested.Content.ReadAsStringAsync(), "un grupo no cuelga de otro");
        }

        // Abre un incidente con un componente y su impacto: redirige al evento y el sitio lo muestra.
        using var opened = await writer.Post("/admin/estado-red/nueva", "/admin/estado-red/nueva?handler=OpenIncident",
            Join(
                [("Incident.State", "Investigating"), ("Incident.Components[0].ComponentId", serverId.ToString()), ("Incident.Components[0].Selected", "true"), ("Incident.Components[0].Impact", "PartialOutage")],
                Texts("Incident.Texts", "Lentitud en el nodo", "Estamos **investigando**.", "Slowness on the node", "We are **investigating**.")));
        var incidentId = IdOf(opened);
        var eventUrl = $"/admin/estado-red/eventos/{incidentId}";
        var eventPage = await writer.Html(eventUrl + "?guardado=1");
        StringAssert.Contains(eventPage, "Lentitud en el nodo");
        StringAssert.Contains(eventPage, "Interrupción parcial");
        StringAssert.Contains(eventPage, "Investigando");
        using var visitor = site.Visitor();
        var publicPage = await visitor.Html("/estado-red");
        StringAssert.Contains(publicPage, "Lentitud en el nodo");
        StringAssert.Contains(publicPage, "Interrupción parcial");

        // Las vistas de la lista: abierto está en las abiertas, y ni en las programadas ni en las resueltas.
        StringAssert.Contains(await writer.Html("/admin/estado-red?vista=abiertas"), "Lentitud en el nodo");
        Assert.DoesNotContain("Lentitud en el nodo", await writer.Html("/admin/estado-red?vista=programadas"));
        Assert.DoesNotContain("Lentitud en el nodo", await writer.Html("/admin/estado-red?vista=resueltas"));

        // Sin componentes o sin mensaje no se abre.
        using (var empty = await writer.Post("/admin/estado-red/nueva", "/admin/estado-red/nueva?handler=OpenIncident",
                   Join([("Incident.State", "Investigating")], Texts("Incident.Texts", "Sin nada", "Mensaje"))))
        {
            Assert.AreEqual(HttpStatusCode.OK, empty.StatusCode);
            StringAssert.Contains(await empty.Content.ReadAsStringAsync(), "Abrir un incidente");
        }

        // Publica una novedad que lo pasa a monitoreando, y otra que lo resuelve. Una revisión vieja falla.
        var revision = RevisionIn(eventPage);
        using (var monitoring = await writer.Post(eventUrl, eventUrl + "?handler=Update",
                   Join([("Update.State", "Monitoring"), ("Update.Revision", revision)], Texts("Update.Texts", "x", "Ya lo estamos **monitoreando**."))))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, monitoring.StatusCode);
        }

        using (var stale = await writer.Post(eventUrl, eventUrl + "?handler=Update",
                   Join([("Update.State", "Resolved"), ("Update.Revision", revision)], Texts("Update.Texts", "x", "Resuelto."))))
        {
            StringAssert.Contains(await stale.Content.ReadAsStringAsync(), "Otra persona modificó el evento");
        }

        var monitored = await writer.Html(eventUrl);
        StringAssert.Contains(monitored, "Monitoreando");
        StringAssert.Contains(monitored, "<strong>monitoreando</strong>");
        using (var resolved = await writer.Post(eventUrl, eventUrl + "?handler=Update",
                   Join([("Update.State", "Resolved"), ("Update.Revision", RevisionIn(monitored))], Texts("Update.Texts", "x", "Resuelto."))))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, resolved.StatusCode);
        }

        var closed = await writer.Html(eventUrl);
        StringAssert.Contains(closed, "Resuelto");
        Assert.DoesNotContain("Publicar la novedad y avisar", closed, "Un incidente resuelto no se reabre.");
        Assert.DoesNotContain("Lentitud en el nodo", (await visitor.Html("/estado-red")).Split("Últimos 14 días")[0].Split("Servidores y servicios")[0], "Ya no figura como vigente.");

        // Resuelto pasa a las resueltas del mes en que empezó, y un mes sin eventos lo dice.
        Assert.DoesNotContain("Lentitud en el nodo", await writer.Html("/admin/estado-red?vista=abiertas"));
        StringAssert.Contains(await writer.Html("/admin/estado-red?vista=resueltas"), "Lentitud en el nodo");
        var oldMonth = await writer.Html("/admin/estado-red?vista=resueltas&mes=2020-01");
        Assert.DoesNotContain("Lentitud en el nodo", oldMonth);
        StringAssert.Contains(oldMonth, "No hay eventos en esta vista.");

        // Corrige el título sin avisar a nadie.
        using (var retitle = await writer.Post(eventUrl, eventUrl + "?handler=Retitle",
                   Join([("Retitle.Revision", RevisionIn(closed))], Texts("Retitle.Texts", "Lentitud en el nodo 1", null, "Slowness on node 1"))))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, retitle.StatusCode);
        }

        StringAssert.Contains(await writer.Html(eventUrl), "Lentitud en el nodo 1");

        // Programa un mantenimiento: fecha ilegible, horario al revés y uno bueno que después se reprograma y se cancela.
        // Tiene que caer dentro de los 30 días que la página pública muestra por adelantado.
        var day = DateTime.UtcNow.AddDays(5);
        var nextDay = day.AddDays(1);
        string StartsAt(DateTime d) => $"{d:yyyy-MM-dd}T02:00";
        string EndsAt(DateTime d, int hour) => $"{d:yyyy-MM-dd}T{hour:00}:00";
        string[] common = ["Maintenance.Components[0].ComponentId", serverId.ToString()];
        using (var badDate = await writer.Post("/admin/estado-red/nueva", "/admin/estado-red/nueva?handler=Schedule",
                   Join([("Maintenance.StartsText", "pronto"), ("Maintenance.EndsText", "luego"), (common[0], common[1]), ("Maintenance.Components[0].Selected", "true")], Texts("Maintenance.Texts", "Cambio de switch"))))
        {
            StringAssert.Contains(await badDate.Content.ReadAsStringAsync(), "No entendimos la fecha");
        }

        using (var reversed = await writer.Post("/admin/estado-red/nueva", "/admin/estado-red/nueva?handler=Schedule",
                   Join([("Maintenance.StartsText", EndsAt(day, 4)), ("Maintenance.EndsText", StartsAt(day)), (common[0], common[1]), ("Maintenance.Components[0].Selected", "true")], Texts("Maintenance.Texts", "Cambio de switch"))))
        {
            StringAssert.Contains(await reversed.Content.ReadAsStringAsync(), "terminar después de empezar");
        }

        using var scheduled = await writer.Post("/admin/estado-red/nueva", "/admin/estado-red/nueva?handler=Schedule",
            Join([("Maintenance.StartsText", StartsAt(day)), ("Maintenance.EndsText", EndsAt(day, 4)), (common[0], common[1]), ("Maintenance.Components[0].Selected", "true")], Texts("Maintenance.Texts", "Cambio de switch", null, "Switch change")));
        var maintenanceId = IdOf(scheduled);
        var maintenanceUrl = $"/admin/estado-red/eventos/{maintenanceId}";
        var maintenance = await writer.Html(maintenanceUrl);
        StringAssert.Contains(maintenance, "Próximo");
        StringAssert.Contains(maintenance, $"value=\"{StartsAt(day)}\"");
        StringAssert.Contains(await visitor.Html("/estado-red"), "Cambio de switch");
        StringAssert.Contains(await writer.Html("/admin/estado-red?vista=programadas"), "Cambio de switch");
        Assert.DoesNotContain("Cambio de switch", await writer.Html("/admin/estado-red?vista=abiertas"));

        using (var moved = await writer.Post(maintenanceUrl, maintenanceUrl + "?handler=Reschedule",
                   ("Reschedule.StartsText", StartsAt(nextDay)), ("Reschedule.EndsText", EndsAt(nextDay, 5)), ("Reschedule.Revision", RevisionIn(maintenance))))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, moved.StatusCode);
        }

        var rescheduled = await writer.Html(maintenanceUrl);
        StringAssert.Contains(rescheduled, $"value=\"{EndsAt(nextDay, 5)}\"");
        using (var cancel = await writer.Post(maintenanceUrl, maintenanceUrl + "?handler=Cancel", ("revision", RevisionIn(rescheduled))))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, cancel.StatusCode);
        }

        StringAssert.Contains(await writer.Html(maintenanceUrl), "Cancelado");
        Assert.DoesNotContain("Cambio de switch", await writer.Html("/admin/estado-red?vista=programadas"));
        StringAssert.Contains(await writer.Html($"/admin/estado-red?vista=resueltas&mes={nextDay:yyyy-MM}"), "Cambio de switch");
        Assert.DoesNotContain("Cambio de switch", (await visitor.Html("/estado-red")).Split("Servidores y servicios")[0], "Un mantenimiento cancelado no figura entre los próximos.");

        // Los suscriptores se listan sin su enlace de baja, y el staff puede borrarlos.
        await site.Use<SubscriptionService>(s => s.Subscribe("lista@example.com", "es", []));
        var subscribers = await writer.Html("/admin/estado-red");
        StringAssert.Contains(subscribers, "lista@example.com");
        Assert.DoesNotContain("/estado-red/suscripcion/baja/", subscribers);
        var subscriberId = (await site.Use<SubscriptionService, IReadOnlyList<SubscriberRow>>(s => s.List())).Single(r => r.Email == "lista@example.com").Id;
        using (var removed = await writer.Post("/admin/estado-red", $"/admin/estado-red?handler=RemoveSubscriber&id={subscriberId}"))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, removed.StatusCode);
        }

        Assert.DoesNotContain("lista@example.com", await writer.Html("/admin/estado-red"));

        // Un componente con historial no se borra: se oculta. Uno sin historial, sí.
        var serverUrl = $"/admin/estado-red/componentes/{serverId}";
        using (var refuse = await writer.Post(serverUrl, serverUrl + "?handler=Delete", ("confirm", "true")))
        {
            StringAssert.Contains(await refuse.Content.ReadAsStringAsync(), "ocultalo en lugar de borrarlo");
        }

        using var loose = await writer.Post("/admin/estado-red/componentes", "/admin/estado-red/componentes?handler=Save",
            Join([("Input.Kind", "Service")], Texts("Input.Texts", "Servicio suelto")));
        var looseUrl = $"/admin/estado-red/componentes/{IdOf(loose)}";
        using (var removedLoose = await writer.Post(looseUrl, looseUrl + "?handler=Delete", ("confirm", "true")))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, removedLoose.StatusCode);
        }

        Assert.AreEqual(HttpStatusCode.NotFound, (await writer.Read(looseUrl)).Status);
    }

    [GeneratedRegex(@"/(\d+)(?:\?|$)")]
    private static partial Regex LastNumber();

    [GeneratedRegex(@"name=""[A-Za-z]*\.?Revision"" value=""(\d*)""")]
    private static partial Regex Revision();
}
