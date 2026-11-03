using BillingOss.App;
using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Content;
using BillingOss.Web.Api;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace BillingOss.Web.Content;

/// <summary>Un producto, un grupo o una categoría para elegir en un formulario del panel.</summary>
public sealed record Choice(long Id, string Name);

/// <summary>El texto de un contenido en un idioma, tal como lo escribe el staff en el panel.</summary>
public sealed class TextForm
{
    public string Language { get; set; } = ContentTexts.DefaultLanguage;

    public string? Title { get; set; }

    public string? Summary { get; set; }

    public string? Body { get; set; }

    public string? Tags { get; set; }

    /// <summary>Un idioma sin título, resumen ni cuerpo no existe: no se guarda.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Summary) && string.IsNullOrWhiteSpace(Body);

    /// <summary>Un formulario con un texto vacío por cada idioma soportado.</summary>
    public static List<TextForm> Blank() => [.. ContentTexts.Languages.Select(l => new TextForm { Language = l })];

    /// <summary>Un texto por idioma soportado, con lo que haya guardado.</summary>
    public static List<TextForm> From(IReadOnlyDictionary<string, ContentText> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return [.. ContentTexts.Languages.Select(l => texts.TryGetValue(l, out var t) ? new TextForm { Language = l, Title = t.Title, Summary = t.Summary, Body = t.Body } : new TextForm { Language = l })];
    }

    public static List<TextForm> From(IEnumerable<ArticleText> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var byLanguage = texts.ToDictionary(t => t.Language);
        return [.. ContentTexts.Languages.Select(l => byLanguage.TryGetValue(l, out var t) ? new TextForm { Language = l, Title = t.Title, Summary = t.Summary, Body = t.Body, Tags = t.Tags } : new TextForm { Language = l })];
    }

    /// <summary>Los textos cargados como los espera un contenido: por idioma y sin los vacíos. Si un idioma viene dos veces, vale el último.</summary>
    public static Dictionary<string, ContentText> ToTexts(IEnumerable<TextForm> forms)
    {
        ArgumentNullException.ThrowIfNull(forms);
        return Filled(forms).ToDictionary(g => g.Key, g => new ContentText(g.Last().Title ?? string.Empty, g.Last().Summary, g.Last().Body));
    }

    /// <summary>Los textos de un artículo: lo mismo que <see cref="ToTexts"/>, con las etiquetas de la búsqueda.</summary>
    public static Dictionary<string, ArticleTextInput> ToArticleTexts(IEnumerable<TextForm> forms)
    {
        ArgumentNullException.ThrowIfNull(forms);
        return Filled(forms).ToDictionary(g => g.Key, g => new ArticleTextInput(g.Last().Title ?? string.Empty, g.Last().Summary, g.Last().Body ?? string.Empty, g.Last().Tags));
    }

    /// <summary>Solo los títulos, por idioma, de los textos que tienen título. Para los eventos de la red.</summary>
    public static Dictionary<string, ContentText> ToTitles(IEnumerable<TextForm> forms)
    {
        ArgumentNullException.ThrowIfNull(forms);
        return forms.Where(f => !string.IsNullOrWhiteSpace(f.Title))
            .GroupBy(f => ContentTexts.Canonical(f.Language) ?? f.Language ?? string.Empty)
            .ToDictionary(g => g.Key, g => new ContentText(g.Last().Title!));
    }

    /// <summary>Solo los mensajes, por idioma, de los textos que tienen cuerpo: el título queda vacío. Para las novedades de un evento.</summary>
    public static Dictionary<string, ContentText> ToMessages(IEnumerable<TextForm> forms)
    {
        ArgumentNullException.ThrowIfNull(forms);
        return forms.Where(f => !string.IsNullOrWhiteSpace(f.Body))
            .GroupBy(f => ContentTexts.Canonical(f.Language) ?? f.Language ?? string.Empty)
            .ToDictionary(g => g.Key, g => new ContentText(string.Empty, null, g.Last().Body));
    }

    private static IEnumerable<IGrouping<string, TextForm>> Filled(IEnumerable<TextForm> forms) =>
        forms.Where(f => !f.IsEmpty).GroupBy(f => ContentTexts.Canonical(f.Language) ?? f.Language ?? string.Empty);
}

/// <summary>Un servidor o servicio en el formulario de un evento de la red: si se eligió y con qué impacto (el nombre del estado).</summary>
public sealed class ComponentPick
{
    public long ComponentId { get; set; }

    public bool Selected { get; set; }

    public string? Impact { get; set; }

    /// <summary>El impacto como estado, o el que se pasa si no vino o no se entiende.</summary>
    public ComponentStatus ImpactOr(ComponentStatus fallback) => Enum.TryParse<ComponentStatus>(Impact, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;
}

/// <summary>Lo que muestra el parcial de impactos: los componentes que se pueden elegir y lo ya elegido. <paramref name="WithImpact"/> pide el impacto de cada uno.</summary>
public sealed record ImpactFields(string Prefix, IReadOnlyList<ComponentPick> Picks, IReadOnlyList<Choice> Components, bool WithImpact);

/// <summary>Lo que muestra el parcial de textos: un bloque por idioma con título y, según el contenido, resumen, cuerpo y etiquetas.</summary>
public sealed record TextsFields(string Prefix, IReadOnlyList<TextForm> Texts, bool Summary, bool Body, bool Tags = false, string TitleKey = "Cms.Title", string BodyKey = "Cms.Body", string SummaryKey = "Cms.Summary", bool Preview = false, string? PreviewUrl = null);

/// <summary>Lo que muestra el parcial de audiencia: a quién se le muestra un contenido.</summary>
public sealed record AudienceFields(string Prefix, ContentAudience Audience, long[] ProductIds, long[] GroupIds, IReadOnlyList<Choice> Products, IReadOnlyList<Choice> Groups);

/// <summary>
/// Base de las pantallas de contenidos del panel: los permisos de escritura, la zona horaria y la traducción de los errores de
/// negocio a un mensaje en la página. El permiso para entrar lo pide cada página (<c>content.read</c>, <c>content.write</c> o <c>status.write</c>).
/// </summary>
public abstract class AdminContentPage : PageModel
{
    public bool CanWrite => User.HasClaim(Claims.Permission, StaffPermissions.ContentWrite);

    public bool CanStatus => User.HasClaim(Claims.Permission, StaffPermissions.StatusWrite);

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(HttpContext.RequestServices.GetRequiredService<IOptions<BillingOptions>>().Value.TimeZone);

    public string? Error { get; protected set; }

    /// <summary>Se guardó: la página a la que se volvió después de guardar trae <c>?guardado=1</c>.</summary>
    public bool Saved => Request.Query.ContainsKey("guardado");

    protected CancellationToken Ct => HttpContext.RequestAborted;

    /// <summary>Los textos de los contenidos en el idioma del pedido.</summary>
    protected IStringLocalizer<ContentLabels> Labels => HttpContext.RequestServices.GetRequiredService<IStringLocalizer<ContentLabels>>();

    /// <summary>El nombre de una audiencia para mostrar en una lista.</summary>
    public string AudienceName(ContentAudience audience) => Labels["Cms.Audience" + audience].Value;

    private ContentOptions Settings => HttpContext.RequestServices.GetRequiredService<IOptions<ContentOptions>>().Value;

    /// <summary>El Markdown de un cuerpo convertido a HTML con las reglas del sitio, para la vista previa.</summary>
    protected string Render(string? markdown) => Markdown.ToHtml(markdown, Settings.Rendering);

    /// <summary>Las imágenes de los hosts configurados (<c>Content:ImageHosts</c>) también se ven en la vista previa del editor.</summary>
    protected void AllowImages() => Response.Headers.ContentSecurityPolicy = ContentUi.ImagePolicy(Settings.ImageHosts);

    /// <summary>La misma página con la marca de guardado: <c>?guardado</c>.</summary>
    protected IActionResult SavedRedirect(object? values = null) => RedirectToPage(null, null, new RouteValueDictionary(values) { ["guardado"] = 1 });

    /// <summary>
    /// Corre una acción del staff. Un error de negocio propio (datos que no valen, un cambio que choca, algo que no existe) queda
    /// como mensaje en la página; los demás siguen al manejador de errores. Devuelve si salió bien.
    /// </summary>
    protected async Task<bool> Try(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            await work();
            return true;
        }
        catch (Exception ex) when (ex is ValidationException or InvalidOperationException or KeyNotFoundException or ContentRejectedException && PublicErrors.IsOwn(ex))
        {
            Error = ex.Message;
            return false;
        }
    }

    /// <summary>Los productos del catálogo, para elegir a quiénes se dirige un contenido.</summary>
    protected async Task<IReadOnlyList<Choice>> ProductChoices()
    {
        var db = HttpContext.RequestServices.GetRequiredService<BillingDbContext>();
        var rows = await db.Products.AsNoTracking().OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync(Ct);
        return [.. rows.Select(p => new Choice(p.Id, p.Name))];
    }

    /// <summary>Los grupos de clientes, para elegir a quiénes se dirige un contenido.</summary>
    protected async Task<IReadOnlyList<Choice>> GroupChoices()
    {
        var db = HttpContext.RequestServices.GetRequiredService<BillingDbContext>();
        var rows = await db.Set<AccountGroup>().AsNoTracking().OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync(Ct);
        return [.. rows.Select(g => new Choice(g.Id, g.Name))];
    }

    protected AudienceFields Audience(ContentAudience audience, long[] products, long[] groups, IReadOnlyList<Choice> productChoices, IReadOnlyList<Choice> groupChoices, string prefix = "Input") =>
        new(prefix, audience, products, groups, productChoices, groupChoices);

    /// <summary>Los árboles de categorías como una lista plana con su ruta ("Instaladores / Windows"), para un selector.</summary>
    protected static List<Choice> CategoryPaths(IEnumerable<CategoryRow> rows, string language = ContentTexts.DefaultLanguage)
    {
        var byId = rows.ToDictionary(r => r.Category.Id);
        string Name(CategoryRow r) => ContentTexts.Pick(r.Texts, language)?.Title ?? r.Category.Slug;
        string Path(CategoryRow r)
        {
            var parts = new List<string> { Name(r) };
            for (var up = r.Category.ParentId; up is { } id && byId.TryGetValue(id, out var parent) && parts.Count < 6; up = parent.Category.ParentId)
            {
                parts.Insert(0, Name(parent));
            }

            return string.Join(" / ", parts);
        }

        return [.. byId.Values.Select(r => new Choice(r.Category.Id, Path(r))).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];
    }
}

/// <summary>
/// Sube el tope del cuerpo del pedido (1 MiB en todo el sitio) hasta el máximo de las descargas, solo en las páginas que reciben
/// un archivo. Corre antes que el control antiforgery, que ya lee el formulario.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ContentUploadLimitAttribute : Attribute, IFilterFactory, IOrderedFilter
{
    public bool IsReusable => false;

    public int Order => -10_000;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return new Filter(serviceProvider.GetRequiredService<IOptions<ContentOptions>>());
    }

    private sealed class Filter(IOptions<ContentOptions> options) : IAuthorizationFilter, IOrderedFilter
    {
        public int Order => -10_000;

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            {
                // El archivo más los campos del formulario.
                feature.MaxRequestBodySize = options.Value.MaxDownloadBytes + (1024 * 1024);
            }
        }
    }
}
