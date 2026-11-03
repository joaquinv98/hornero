using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>
/// La API de los contenidos para clientes (sección 8 de diseno/contenidos.md): permisos por recurso, base de conocimiento,
/// novedades, descargas y estado de la red, con los códigos de error que documenta: 404, 409, 413, 415 y 422.
/// </summary>
[TestClass]
[DoNotParallelize] // Comparten un host y su base: crear una cuesta casi un minuto con la PC cargada.
public sealed class ContentApiTests
{
    private const int MaxBytes = 2000;

    private static WebHarness w = null!;
    private static string all = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        // Content:MaxDownloadBytes llega por la configuración, como en producción.
        w = await WebHarness.Start(settings: new() { ["Content:MaxDownloadBytes"] = MaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        all = await w.Key("kb:read", "kb:write", "announcements:read", "announcements:write", "downloads:read", "downloads:write", "status:read", "status:write");
    }

    [ClassCleanup]
    public static async Task Stop() => await w.DisposeAsync();

    private static async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, object? body = null, string? token = null)
    {
        using var request = new HttpRequestMessage(method, new Uri("/api/v1" + path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? all);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await w.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonNode.Parse(text) : null);
    }

    private static Task<(HttpStatusCode Status, JsonNode? Body)> Get(string path, string? token = null) => Call(HttpMethod.Get, path, null, token);

    private static async Task<long> Created(string path, object body)
    {
        var (status, json) = await Call(HttpMethod.Post, path, body);
        Assert.AreEqual(HttpStatusCode.Created, status, json?.ToJsonString());
        return json!["id"]!.GetValue<long>();
    }

    private static async Task NoContent(HttpMethod method, string path, object? body = null)
    {
        var (status, json) = await Call(method, path, body);
        Assert.AreEqual(HttpStatusCode.NoContent, status, $"{method} {path}: {json?.ToJsonString()}");
    }

    private static object Es(string title, string? summary = null, string? body = null) => new Dictionary<string, object> { ["es-AR"] = new { title, summary, body } };

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    [TestMethod]
    public async Task Permisos_SinLlaveElPermisoDeOtroRecursoOLaLecturaParaEscribir_NoPasan()
    {
        using var anonymous = await w.Client.GetAsync(new Uri("/api/v1/kb/articles", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var reader = await w.Key("kb:read");
        Assert.AreEqual(HttpStatusCode.OK, (await Get("/kb/articles", reader)).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Get("/announcements", reader)).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Get("/status", reader)).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Get("/downloads", reader)).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/kb/categories", new { texts = Es("x") }, reader)).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Call(HttpMethod.Delete, "/kb/articles/1", null, reader)).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, "/downloads/1/file?name=a.zip", null, reader)).Status);

        var network = await w.Key("status:read");
        Assert.AreEqual(HttpStatusCode.OK, (await Get("/status", network)).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/status/incidents", new { }, network)).Status);
    }

    [TestMethod]
    public async Task BaseDeConocimiento_CategoriasArticulosTextosYEstados_PorLaApi()
    {
        var slug = Unique("hosting-");
        var category = await Created("/kb/categories", new { slug, sort_order = 1, texts = Es("Hosting", "Todo sobre el hosting") });
        var categories = (await Get("/kb/categories")).Body!.AsArray();
        var row = categories.Single(c => c!["id"]!.GetValue<long>() == category)!;
        Assert.AreEqual(slug, row["slug"]!.GetValue<string>());
        Assert.AreEqual(0, row["items"]!.GetValue<int>());
        Assert.AreEqual("Hosting", row["texts"]!["es-AR"]!["title"]!.GetValue<string>());

        // Crear: borrador, con los dos idiomas y las etiquetas.
        var article = await Created("/kb/articles", new
        {
            category_id = category,
            featured = true,
            audience = "public",
            texts = new Dictionary<string, object>
            {
                ["es-AR"] = new { title = "Cómo cambiar la contraseña del correo", body = "Entrá a **Webmail** y elegí *Ajustes*.", tags = "correo, clave" },
                ["en"] = new { title = "How to change your mail password", body = "Open **Webmail**." },
            },
        });
        var detail = (await Get($"/kb/articles/{article}")).Body!;
        Assert.AreEqual("draft", detail["status"]!.GetValue<string>());
        Assert.AreEqual("public", detail["audience"]!.GetValue<string>());
        Assert.AreEqual("correo, clave", detail["texts"]!["es-AR"]!["tags"]!.GetValue<string>());
        Assert.AreEqual("How to change your mail password", detail["texts"]!["en"]!["title"]!.GetValue<string>());
        var revision = detail["revision"]!.GetValue<int>();

        // Un borrador no sale en la búsqueda ni en las sugerencias, y publicado sí.
        Assert.IsEmpty((await Get("/kb/search?q=contrase%C3%B1a")).Body!.AsArray().Where(h => h!["id"]!.GetValue<long>() == article).ToList());
        await NoContent(HttpMethod.Post, $"/kb/articles/{article}/publish");
        var found = (await Get("/kb/search?q=contrase%C3%B1a&language=es")).Body!.AsArray();
        Assert.AreEqual(article, found.Single(h => h!["id"]!.GetValue<long>() == article)!["id"]!.GetValue<long>());
        var english = (await Get("/kb/search?q=password&language=en")).Body!.AsArray();
        Assert.AreEqual("How to change your mail password", english.Single(h => h!["id"]!.GetValue<long>() == article)!["title"]!.GetValue<string>());
        var suggestions = (await Get("/kb/suggestions?q=No%20puedo%20cambiar%20la%20clave%20del%20correo&language=es")).Body!.AsArray();
        Assert.IsTrue(suggestions.Any(h => h!["id"]!.GetValue<long>() == article), "Las palabras del asunto del ticket encuentran el artículo.");
        Assert.IsEmpty((await Get("/kb/suggestions?q=a%20y%20de")).Body!.AsArray(), "Sin palabras con contenido no hay sugerencias.");

        // Listar con filtros.
        var listed = (await Get($"/kb/articles?category_id={category}&status=published&q=correo")).Body!.AsArray();
        Assert.AreEqual(1, listed.Count);
        Assert.AreEqual("Cómo cambiar la contraseña del correo", listed[0]!["title"]!.GetValue<string>());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Get("/kb/articles?status=cualquiera")).Status);

        // Cargar y sacar el texto de un idioma. El último no se saca.
        await NoContent(HttpMethod.Put, $"/kb/articles/{article}/texts/en", new { title = "Change your mail password", body = "Open **Webmail** and pick *Settings*." });
        Assert.AreEqual("Change your mail password", (await Get($"/kb/articles/{article}")).Body!["texts"]!["en"]!["title"]!.GetValue<string>());
        await NoContent(HttpMethod.Delete, $"/kb/articles/{article}/texts/en");
        Assert.IsNull((await Get($"/kb/articles/{article}")).Body!["texts"]!["en"]);
        Assert.AreEqual(HttpStatusCode.Conflict, (await Call(HttpMethod.Delete, $"/kb/articles/{article}/texts/es-AR")).Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Put, $"/kb/articles/{article}/texts/fr", new { title = "Mot de passe", body = "x" })).Status);

        // Una revisión vieja da 409, la vigente guarda, y los datos malos dan 422.
        var edit = new
        {
            category_id = category,
            featured = false,
            audience = "public",
            slug = (string?)null,
            texts = new Dictionary<string, object> { ["es-AR"] = new { title = "Cambiar la clave del correo", body = "Nuevo cuerpo." } },
            revision = 1,
        };
        var current = (await Get($"/kb/articles/{article}")).Body!["revision"]!.GetValue<int>();
        Assert.IsGreaterThan(revision, current);
        Assert.AreEqual(HttpStatusCode.Conflict, (await Call(HttpMethod.Put, $"/kb/articles/{article}", edit)).Status);
        await NoContent(HttpMethod.Put, $"/kb/articles/{article}", new { edit.category_id, edit.featured, edit.audience, edit.slug, edit.texts, revision = current });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/kb/articles", new { category_id = category, audience = "targeted", texts = Es("Dirigido", null, "x") })).Status, "Dirigido sin productos ni grupos.");
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/kb/articles", new { category_id = 987654, audience = "public", texts = Es("Sin categoría", null, "x") })).Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/kb/articles", new { category_id = category, audience = "public", texts = new Dictionary<string, object>() })).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Get("/kb/articles/987654")).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, "/kb/articles/987654/publish")).Status);

        // La categoría con un artículo no se borra. Sin él, sí.
        Assert.AreEqual(HttpStatusCode.Conflict, (await Call(HttpMethod.Delete, $"/kb/categories/{category}")).Status);
        await NoContent(HttpMethod.Post, $"/kb/articles/{article}/archive");
        Assert.IsEmpty((await Get("/kb/search?q=clave")).Body!.AsArray().Where(h => h!["id"]!.GetValue<long>() == article).ToList(), "Archivado no se busca.");
        await NoContent(HttpMethod.Delete, $"/kb/articles/{article}");
        await NoContent(HttpMethod.Delete, $"/kb/categories/{category}");
        Assert.AreEqual(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/kb/categories/{category}")).Status);
    }

    [TestMethod]
    public async Task BaseDeConocimiento_LaBusquedaConCuentaVeLoDeLosClientes_YSinCuentaSoloLoPublico()
    {
        var category = await Created("/kb/categories", new { texts = Es("Facturación " + Unique("")) });
        var customers = await Created("/kb/articles", new { category_id = category, audience = "customers", texts = Es("Cómo leer tu factura mensual", null, "El detalle de tu factura.") });
        await NoContent(HttpMethod.Post, $"/kb/articles/{customers}/publish");

        var anonymous = (await Get("/kb/search?q=factura")).Body!.AsArray();
        Assert.IsEmpty(anonymous.Where(h => h!["id"]!.GetValue<long>() == customers).ToList());
        var asCustomer = (await Get($"/kb/search?q=factura&account_id={w.AccountId}")).Body!.AsArray();
        Assert.IsTrue(asCustomer.Any(h => h!["id"]!.GetValue<long>() == customers));
        var suggested = (await Get($"/kb/suggestions?q=Tengo%20una%20duda%20con%20mi%20factura&account_id={w.AccountId}")).Body!.AsArray();
        Assert.IsTrue(suggested.Any(h => h!["id"]!.GetValue<long>() == customers));
    }

    [TestMethod]
    public async Task Novedades_CrearProgramarPublicarRetirarYBorrar_ConHorasDeCualquierZona()
    {
        var id = await Created("/announcements", new { audience = "public", pinned = true, texts = Es("Corte programado del sábado", null, "Un **corte** corto.") });
        var draft = (await Get($"/announcements/{id}")).Body!;
        Assert.AreEqual("draft", draft["status"]!.GetValue<string>());
        Assert.AreEqual("draft", draft["phase"]!.GetValue<string>());
        Assert.IsTrue(draft["pinned"]!.GetValue<bool>());

        // Programarla con una hora con desfase: se guarda en UTC y queda programada.
        await NoContent(HttpMethod.Post, $"/announcements/{id}/publish", new { publish_at = "2031-05-20T10:30:00-03:00" });
        var scheduled = (await Get($"/announcements/{id}")).Body!;
        Assert.AreEqual("scheduled", scheduled["phase"]!.GetValue<string>());
        Assert.AreEqual(new DateTimeOffset(2031, 5, 20, 13, 30, 0, TimeSpan.Zero), scheduled["publish_at"]!.GetValue<DateTimeOffset>());

        // Sin cuerpo se publica ya.
        await NoContent(HttpMethod.Post, $"/announcements/{id}/publish");
        Assert.AreEqual("live", (await Get($"/announcements/{id}")).Body!["phase"]!.GetValue<string>());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, $"/announcements/{id}/publish", "esto no es un objeto json")).Status);

        // Listas con filtro y vencimiento anterior a la publicación.
        var published = (await Get("/announcements?status=published")).Body!.AsArray();
        Assert.IsTrue(published.Any(a => a!["id"]!.GetValue<long>() == id));
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Get("/announcements?status=nada")).Status);
        var current = (await Get($"/announcements/{id}")).Body!["revision"]!.GetValue<int>();
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Put, $"/announcements/{id}", new { audience = "public", expires_at = "2001-01-01T00:00:00Z", texts = Es("Corte", null, "x"), revision = current })).Status);
        Assert.AreEqual(HttpStatusCode.Conflict, (await Call(HttpMethod.Put, $"/announcements/{id}", new { audience = "public", texts = Es("Corte", null, "x"), revision = current - 1 })).Status);

        await NoContent(HttpMethod.Post, $"/announcements/{id}/unpublish");
        Assert.AreEqual("draft", (await Get($"/announcements/{id}")).Body!["status"]!.GetValue<string>());
        await NoContent(HttpMethod.Post, $"/announcements/{id}/archive");
        await NoContent(HttpMethod.Delete, $"/announcements/{id}");
        Assert.AreEqual(HttpStatusCode.NotFound, (await Get($"/announcements/{id}")).Status);

        // Un borrador puede no tener cuerpo, pero publicarlo lo pide.
        var bodyless = await Created("/announcements", new { audience = "public", texts = Es("Sin cuerpo todavía") });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/announcements/{bodyless}/publish")).Status);
    }

    [TestMethod]
    public async Task Descargas_SubirElArchivoConTopeTipoYSha_YBajarloSinContar()
    {
        var category = await Created("/download-categories", new { slug = Unique("agentes-"), texts = Es("Agentes") });
        var id = await Created("/downloads", new { category_id = category, audience = "customers", texts = Es("Agente de monitoreo", null, "Instalador del agente.") });
        var empty = (await Get($"/downloads/{id}")).Body!;
        Assert.IsNull(empty["file"]);
        Assert.AreEqual(0, empty["download_count"]!.GetValue<int>());

        // Sin archivo no se publica.
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/downloads/{id}/publish")).Status);

        // El archivo viaja en el cuerpo. Un tipo que un navegador ejecuta da 415 y uno que pasa el tope, 413.
        async Task<(HttpStatusCode Status, JsonNode? Body)> Put(string name, byte[] bytes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/downloads/{id}/file?name={Uri.EscapeDataString(name)}", UriKind.Relative)) { Content = new ByteArrayContent(bytes) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", all);
            using var response = await w.Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, text.StartsWith('{') ? JsonNode.Parse(text) : null);
        }

        Assert.AreEqual((HttpStatusCode)415, (await Put("pagina.html", "<h1>x</h1>"u8.ToArray())).Status);
        Assert.AreEqual((HttpStatusCode)413, (await Put("grande.zip", new byte[MaxBytes + 1])).Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Put("sin-extension", [1, 2, 3])).Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Put("vacio.zip", [])).Status);
        Assert.IsNull((await Get($"/downloads/{id}")).Body!["file"], "Un rechazo no deja nada a medias.");

        var bytes = new byte[MaxBytes];
        new Random(7).NextBytes(bytes);
        var (status, uploaded) = await Put("Agente v2.zip", bytes);
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("Agente_v2.zip", uploaded!["file_name"]!.GetValue<string>());
        Assert.AreEqual("application/zip", uploaded["content_type"]!.GetValue<string>());
        Assert.AreEqual(MaxBytes, uploaded["size"]!.GetValue<int>());
        Assert.AreEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), uploaded["sha256"]!.GetValue<string>());

        // Publicarla y bajarla desde la API no suma al contador.
        await NoContent(HttpMethod.Post, $"/downloads/{id}/publish");
        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/downloads/{id}/file", UriKind.Relative)))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", all);
            using var response = await w.Client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            CollectionAssert.AreEqual(bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.AreEqual("nosniff", string.Join(' ', response.Headers.GetValues("X-Content-Type-Options")));
            Assert.AreEqual("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        }

        var listed = (await Get($"/downloads?category_id={category}&status=published")).Body!.AsArray();
        Assert.AreEqual(1, listed.Count);
        Assert.AreEqual(0, listed[0]!["download_count"]!.GetValue<int>());
        Assert.AreEqual("Agente_v2.zip", listed[0]!["file"]!["name"]!.GetValue<string>());

        // Una categoría con descargas no se borra. La descarga se borra con su archivo.
        Assert.AreEqual(HttpStatusCode.Conflict, (await Call(HttpMethod.Delete, $"/download-categories/{category}")).Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/downloads", new { category_id = 987654, audience = "public", texts = Es("Sin categoría") })).Status);
        await NoContent(HttpMethod.Delete, $"/downloads/{id}");
        Assert.AreEqual(HttpStatusCode.NotFound, (await Get($"/downloads/{id}/file")).Status);
        await NoContent(HttpMethod.Delete, $"/download-categories/{category}");
    }

    [TestMethod]
    public async Task EstadoDeLaRed_ComponentesIncidentesYMantenimientos_PorLaApi()
    {
        var group = await Created("/status/components", new { kind = "group", slug = Unique("grupo-"), texts = Es("Cómputo") });
        var server = await Created("/status/components", new { kind = "server", parent_id = group, product_ids = new[] { w.ProductId }, texts = Es("Nodo 1") });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/status/components", new { kind = "group", parent_id = group, texts = Es("Subgrupo") })).Status);
        var components = (await Get("/status/components")).Body!.AsArray();
        Assert.AreEqual("operational", components.Single(c => c!["id"]!.GetValue<long>() == server)!["status"]!.GetValue<string>());

        // Abrir un incidente: pide componentes, con el impacto de un incidente.
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/status/incidents", new { title = Es("Sin componentes"), message = Es("x", null, "Mensaje"), components = Array.Empty<object>() })).Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/status/incidents", new { title = Es("Impacto malo"), message = Es("x", null, "Mensaje"), components = new[] { new { component_id = server, impact = "maintenance" } } })).Status);
        var incident = await Created("/status/incidents", new { title = Es("Lentitud en el nodo 1"), message = Es("x", null, "Estamos **investigando**."), components = new[] { new { component_id = server, impact = "partial_outage" } } });
        var detail = (await Get($"/status/incidents/{incident}")).Body!;
        Assert.AreEqual("investigating", detail["state"]!.GetValue<string>());
        Assert.AreEqual("partial_outage", detail["components"]![0]!["impact"]!.GetValue<string>());
        var overview = (await Get("/status")).Body!;
        Assert.AreEqual("partial_outage", overview["overall"]!.GetValue<string>());
        Assert.AreEqual(incident, overview["active"]![0]!["id"]!.GetValue<long>());

        // El producto del componente: la tarjeta del servicio y el ticket ven el incidente.
        var issues = (await Get($"/status/issues?product_id={w.ProductId}")).Body!.AsArray();
        Assert.AreEqual(incident, issues.Single()!["id"]!.GetValue<long>());
        Assert.IsEmpty((await Get("/status/issues?product_id=987654")).Body!.AsArray());
        Assert.IsEmpty((await Get("/status/issues")).Body!.AsArray());

        // Novedades: cambiar el estado, y al resolver deja de afectar y no se reabre.
        await NoContent(HttpMethod.Post, $"/status/incidents/{incident}/updates", new { message = Es("x", null, "Ya lo **monitoreamos**."), state = "monitoring" });
        Assert.AreEqual("monitoring", (await Get($"/status/incidents/{incident}")).Body!["state"]!.GetValue<string>());
        Assert.AreEqual(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/status/incidents/{incident}/updates", new { message = Es("x", null, "No"), state = "scheduled" })).Status);
        await NoContent(HttpMethod.Post, $"/status/incidents/{incident}/updates", new { message = Es("x", null, "Resuelto."), state = "resolved" });
        Assert.AreEqual("operational", (await Get("/status")).Body!["overall"]!.GetValue<string>());
        Assert.AreEqual(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/status/incidents/{incident}/updates", new { message = Es("x", null, "Otra vez"), state = "investigating" })).Status);
        Assert.AreEqual(2 + 1, (await Get($"/status/incidents/{incident}")).Body!["updates"]!.AsArray().Count);
        Assert.IsEmpty((await Get("/status/incidents?open=true")).Body!.AsArray().Where(i => i!["id"]!.GetValue<long>() == incident).ToList());
        Assert.AreEqual(HttpStatusCode.NotFound, (await Get($"/status/incidents/{incident + 987654}")).Status);

        // Mantenimientos: programarlo, reprogramarlo, darlo por terminado antes de hora no vale si no empezó, y cancelarlo.
        var starts = new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero);
        var maintenance = await Created("/status/maintenances", new { title = Es("Cambio de disco"), component_ids = new[] { server }, starts_at = starts, ends_at = starts.AddHours(2) });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/status/maintenances", new { title = Es("Al revés"), component_ids = new[] { server }, starts_at = starts.AddHours(2), ends_at = starts })).Status);
        var plan = (await Get($"/status/maintenances/{maintenance}")).Body!;
        Assert.AreEqual("scheduled", plan["state"]!.GetValue<string>());
        Assert.AreEqual("upcoming", plan["phase"]!.GetValue<string>());
        await NoContent(HttpMethod.Put, $"/status/maintenances/{maintenance}", new { starts_at = starts.AddDays(1), ends_at = starts.AddDays(1).AddHours(3) });
        Assert.AreEqual(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/status/maintenances/{maintenance}/complete")).Status);
        await NoContent(HttpMethod.Post, $"/status/maintenances/{maintenance}/updates", new { message = Es("x", null, "Se movió un día.") });
        await NoContent(HttpMethod.Post, $"/status/maintenances/{maintenance}/cancel");
        Assert.AreEqual("cancelled", (await Get($"/status/maintenances/{maintenance}")).Body!["state"]!.GetValue<string>());
        Assert.AreEqual(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/status/maintenances/{maintenance}/cancel")).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Get($"/status/maintenances/{incident}")).Status, "Un incidente no es un mantenimiento.");

        // Los suscriptores salen sin su token.
        await using (var scope = w.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<BillingOss.App.Content.SubscriptionService>().Subscribe("lista@example.com", "es", [server]);
        }

        var subscribers = (await Get("/status/subscribers")).Body!.AsArray();
        var subscriber = subscribers.Single(s => s!["email"]!.GetValue<string>() == "lista@example.com")!;
        Assert.IsNull(subscriber["token"]);
        Assert.IsFalse(subscriber.ToJsonString().Contains("baja", StringComparison.OrdinalIgnoreCase), "El enlace de baja no sale de la API.");
        await NoContent(HttpMethod.Delete, $"/status/subscribers/{subscriber["id"]!.GetValue<long>()}");
        Assert.AreEqual(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/status/subscribers/{subscriber["id"]!.GetValue<long>()}")).Status);

        // Un componente con historial no se borra. Uno sin historial, sí.
        Assert.AreEqual(HttpStatusCode.Conflict, (await Call(HttpMethod.Delete, $"/status/components/{server}")).Status);
        var loose = await Created("/status/components", new { kind = "service", texts = Es("Suelto") });
        await NoContent(HttpMethod.Delete, $"/status/components/{loose}");
    }
}
