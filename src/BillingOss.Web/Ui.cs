using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.Web.Content;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;

namespace BillingOss.Web;

/// <summary>Marca de los textos de la interfaz: Resources/Texts.resx (es-AR) y Resources/Texts.en.resx.</summary>
public sealed class Texts;

/// <summary>
/// Portal del cliente y admin (D-10 y D-13): Razor Pages con htmx, sin SPA, con ASP.NET Core Identity en dos
/// esquemas separados. Lo arma solo el rol web.
/// </summary>
public static class Ui
{
    public const string FormLimiter = "ui-forms";

    public const string PortalTotp = "portal-totp";

    public const string StaffTotp = "staff-totp";

    public static IReadOnlyList<CultureInfo> Cultures { get; } = [new("es-AR"), new("en")];

    public static void AddBillingUi(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        var config = builder.Configuration;

        services.AddLocalization(o => o.ResourcesPath = "Resources");

        // Acentos y eñes tal cual en el HTML: Razor sigue codificando lo peligroso (<, >, &, comillas).
        services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(o => o.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));
        services.AddRazorPages(o =>
        {
            o.Conventions.AuthorizeFolder("/Portal", Schemes.Portal);
            o.Conventions.AuthorizeFolder("/Admin", Schemes.Staff);
            o.Conventions.AllowAnonymousToFolder("/Admin/Access");

            // La solapa Tickets de la ficha del cliente (D-19, 10.2) es la lista de soporte filtrada por la cuenta.
            o.Conventions.AddPageRoute("/Admin/Tickets/Index", "admin/cuentas/{cuenta:long}/tickets");

            // La solapa Presupuestos de la ficha del cliente (docs/interfaz/menu.md): la lista filtrada por la cuenta.
            o.Conventions.AddPageRoute("/Admin/Billing/Quotes", "admin/cuentas/{cuenta:long}/presupuestos");

            // Tope por IP en los formularios que se pueden usar para adivinar contraseñas, códigos o correos.
            foreach (var page in (string[])["/Access/Login", "/Access/LoginCode", "/Access/Forgot", "/Access/Reset", "/Access/Verify", "/Access/Register", "/Access/External", "/Access/ExternalConfirm", "/Consumer/Request", "/Admin/Access/Login", "/Admin/Access/Code", "/Portal/Security", "/Admin/Security"])
            {
                o.Conventions.AddPageApplicationModelConvention(page, m => m.EndpointMetadata.Add(new EnableRateLimitingAttribute(FormLimiter)));
            }
        }).ConfigureApplicationPartManager(m =>
        {
            // En las pruebas el ensamblado de entrada no es este: las páginas se agregan si todavía no están.
            var assembly = typeof(Ui).Assembly;
            if (!m.ApplicationParts.OfType<AssemblyPart>().Any(p => p.Assembly == assembly))
            {
                foreach (var part in ApplicationPartFactory.GetApplicationPartFactory(assembly).GetApplicationParts(assembly))
                {
                    m.ApplicationParts.Add(part);
                }
            }
        });
        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "bo_af";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        });
        services.AddScoped<PortalAccess>();
        services.AddContentUi();

        // Dos Identity, uno por tabla de usuarios. Las opciones son comunes: bloqueo de 15 minutos tras 5 intentos.
        services.AddScoped<IPasswordHasher<PortalUser>, ImportedPasswordHasher<PortalUser>>();
        services.AddScoped<IPasswordHasher<StaffUser>, ImportedPasswordHasher<StaffUser>>();
        services.AddIdentityCore<PortalUser>(IdentityOptions)
            .AddUserStore<UserStore<PortalUser>>()
            .AddTokenProvider<DataProtectorTokenProvider<PortalUser>>(TokenOptions.DefaultProvider)
            .AddTokenProvider<AuthenticatorTokenProvider<PortalUser>>(PortalTotp)
            .AddTokenProvider<EmailConfirmationTokenProvider<PortalUser>>(EmailConfirmationTokenOptions.ProviderName)
            .AddPasswordValidator<PasswordPolicy<PortalUser>>();
        services.AddIdentityCore<StaffUser>(IdentityOptions)
            .AddUserStore<UserStore<StaffUser>>()
            .AddTokenProvider<AuthenticatorTokenProvider<StaffUser>>(StaffTotp)
            .AddPasswordValidator<PasswordPolicy<StaffUser>>();
        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(1));

        var auth = services.AddAuthentication();
        auth.AddCookie(Schemes.Portal, o =>
        {
            Cookie(o, "bo_portal", "/", SameSiteMode.Lax, TimeSpan.FromHours(8));
            o.LoginPath = "/ingresar";
            o.AccessDeniedPath = "/estado/404";
            o.Events.OnValidatePrincipal = Sessions.ValidatePortal;
        });
        auth.AddCookie(Schemes.PortalPending, o => Cookie(o, "bo_portal_2fa", "/", SameSiteMode.Strict, Sessions.PendingLifetime));

        // Ingreso de clientes con proveedores externos (diseno/ingreso-clientes.md, sección 4). Los proveedores se cargan en
        // el panel y se leen de la base en cada ida y vuelta: no hay un esquema por proveedor que haya que reiniciar. Las dos
        // cookies son Lax porque la vuelta llega desde el sitio del proveedor, y viven solo bajo /ingresar/externo.
        auth.AddCookie(Schemes.PortalChallenge, o => Cookie(o, "bo_portal_oidc", "/ingresar/externo", SameSiteMode.Lax, ExternalSignIn.TripLifetime));
        auth.AddCookie(Schemes.PortalExternal, o => Cookie(o, "bo_portal_ext", "/ingresar/externo", SameSiteMode.Lax, ExternalSignIn.TripLifetime));
        services.AddSingleton<OidcMetadata>();
        services.AddScoped<ExternalSignIn>();
        services.AddScoped<PortalRegistration>();
        services.AddScoped<ChallengeGuard>();
        services.AddHttpClient(ChallengeGuard.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        // El canal con los proveedores: sin redirecciones, con tope de tiempo y de tamaño. Las pruebas cambian el manejador
        // por un proveedor falso con PostConfigure.
        services.AddHttpClient(ExternalSignIn.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.MaxResponseContentBufferSize = 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        auth.AddCookie(Schemes.Staff, o =>
        {
            Cookie(o, "bo_staff", "/admin", SameSiteMode.Strict, TimeSpan.FromHours(8));
            o.LoginPath = "/admin/ingresar";
            o.AccessDeniedPath = "/estado/403";
            o.Events.OnValidatePrincipal = Sessions.ValidateStaff;
        });
        auth.AddCookie(Schemes.StaffPending, o => Cookie(o, "bo_staff_2fa", "/admin", SameSiteMode.Strict, Sessions.PendingLifetime));

        // Login del staff por OIDC (por ejemplo Authentik), solo si está configurado. El secreto del cliente sale del
        // vault por variable de entorno, nunca de un archivo del repo.
        var oidc = config.GetSection("Staff:Oidc");
        if (oidc["Authority"] is { Length: > 0 } authority)
        {
            // Lax: la vuelta del proveedor es una navegación desde otro sitio.
            auth.AddCookie(Schemes.StaffExternal, o => Cookie(o, "bo_staff_ext", "/admin", SameSiteMode.Lax, Sessions.PendingLifetime));
            auth.AddOpenIdConnect(Schemes.StaffOidc, o =>
            {
                o.SignInScheme = Schemes.StaffExternal;
                o.Authority = authority;
                o.ClientId = oidc["ClientId"];
                o.ClientSecret = oidc["ClientSecret"];
                o.ResponseType = "code";
                o.UsePkce = true;
                o.CallbackPath = "/admin/oidc/callback";
                o.MapInboundClaims = false;
                o.SaveTokens = false;
                o.Scope.Clear();
                o.Scope.Add("openid");
                o.Scope.Add("email");
                o.Scope.Add("profile");
            });
        }

        services.AddAuthorization(o =>
        {
            // El segundo factor obligatorio por rol va en la política: vale para toda página o ruta con la sesión del portal.
            o.AddPolicy(Schemes.Portal, p => p.AddAuthenticationSchemes(Schemes.Portal).RequireAuthenticatedUser().AddRequirements(new TwoFactorEnrolled()));
            o.AddPolicy(Schemes.Staff, p => p.AddAuthenticationSchemes(Schemes.Staff).RequireAuthenticatedUser());
            foreach (var permission in StaffPermissions.All)
            {
                o.AddPolicy(permission, p => p.AddAuthenticationSchemes(Schemes.Staff).RequireClaim(Claims.Permission, StaffPermissions.Granting(permission)));
            }

            o.AddPolicy(StaffPermissions.AnyReport, p => p.AddAuthenticationSchemes(Schemes.Staff).RequireClaim(Claims.Permission, [StaffPermissions.ReportsRead, .. ReportPermissions.All]));
        });

        services.AddScoped<IAuthorizationHandler, TwoFactorEnrolledHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, TwoFactorRedirect>();

        // Además del bloqueo por cuenta, un tope por IP a los formularios de ingreso y de trámites. Es por nodo: con
        // varios nodos el tope total es la suma, y el global va en el balanceador.
        services.AddRateLimiter(o => o.AddPolicy(FormLimiter, http => HttpMethods.IsPost(http.Request.Method)
            ? RateLimitPartition.GetFixedWindowLimiter(
                "ip:" + http.Connection.RemoteIpAddress,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(5) })
            : RateLimitPartition.GetNoLimiter("get")));
    }

    public static void UseBillingUi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        // En el pipeline principal y no en una rama: solo ahí la página de error vuelve a pasar por el ruteo.
        app.UseStatusCodePagesWithReExecute("/estado/{0}");
        app.Use((http, next) =>
        {
            if (!IsUi(http.Request.Path) && http.Features.Get<IStatusCodePagesFeature>() is { } statusPages)
            {
                statusPages.Enabled = false;
            }

            if (IsUi(http.Request.Path))
            {
                var headers = http.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "same-origin";
                headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
            }

            return next(http);
        });
        app.UseStaticFiles();
        app.UseRequestLocalization(o =>
        {
            o.DefaultRequestCulture = new RequestCulture(Cultures[0]);
            o.SupportedCultures = [.. Cultures];
            o.SupportedUICultures = [.. Cultures];
            o.RequestCultureProviders = [new CookieRequestCultureProvider(), new AcceptLanguageHeaderRequestCultureProvider()];
        });

        // La interfaz solo anda por HTTPS: el antiforgery y las cookies son Secure (docs/interfaz). Un pedido por HTTP
        // que no viene de un proxy declarado (solo a ese se le cree X-Forwarded-Proto) recibe el motivo en vez del 500
        // del antiforgery, y el log lo dice una vez por proceso.
        var warned = 0;
        app.Use(async (http, next) =>
        {
            if (IsUi(http.Request.Path) && !http.Request.IsHttps)
            {
                if (Interlocked.Exchange(ref warned, 1) == 0)
                {
                    app.Logger.LogWarning("Pedido por HTTP a {Path}: la interfaz solo se sirve por HTTPS. Si un proxy termina TLS, declaralo en ForwardedHeaders:KnownProxies o KnownNetworks.", http.Request.Path.Value);
                }

                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                http.Response.ContentType = "text/plain; charset=utf-8";
                await http.Response.WriteAsync(http.RequestServices.GetRequiredService<IStringLocalizer<Texts>>()["Status.NoTls"], http.RequestAborted);
                return;
            }

            await next(http);
        });
        app.UseAuthentication();
        app.UseAuthorization();

        // Quién actúa (D-15): lo que se audita o se encola desde la interfaz lleva la sesión que lo pidió. La API fija
        // el suyo al validar la llave.
        app.Use(async (http, next) =>
        {
            if (IsUi(http.Request.Path))
            {
                // Una IPv4 que llega como IPv6 mapeada queda como IPv4, igual que en el historial de ingresos: así la
                // auditoría y el tope de altas por IP cuentan la misma dirección.
                var ip = http.Connection.RemoteIpAddress;
                Actor.Current = UiActor(http.User, ip is null ? null : Networks.Normalize(ip));
            }

            await next(http);
        });
    }

    public static void MapBillingUi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapRazorPages();
        app.MapContentUi();
        app.MapGet("/idioma/{culture}", (string culture, string? volver, HttpResponse response) =>
        {
            if (!Cultures.Any(c => c.Name == culture))
            {
                return Results.NotFound();
            }

            response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(365), IsEssential = true });
            return Results.LocalRedirect(SafeReturn(volver));
        });
    }

    /// <summary>Importe con la cultura del pedido y el código de moneda: "12.100,00 ARS".</summary>
    public static string Money(decimal amount, BillingOss.Domain.Shared.Currency currency) =>
        string.Create(CultureInfo.CurrentCulture, $"{amount:N2} {currency.Code}");

    public static string Date(DateOnly? date) => date?.ToString("d", CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>Clave de texto de un valor en snake_case: ("DomainOperation", "set_nameservers") da "DomainOperation.SetNameservers".</summary>
    public static string Key(string prefix, string value) =>
        prefix + "." + string.Concat((value ?? string.Empty).Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    /// <summary>La fecha para el atributo datetime de &lt;time&gt;.</summary>
    public static string Iso(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>El importe para el atributo value de &lt;data&gt;.</summary>
    public static string Value(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Tono y forma del estado de un dominio (D-19, 4.4): lleno es vigente, hueco es en curso y con anillo pide acción.
    /// </summary>
    public static (string Tone, string Shape) Tone(BillingOss.Domain.Domains.DomainStatus status) => status switch
    {
        BillingOss.Domain.Domains.DomainStatus.Pending => ("info", "hollow"),
        BillingOss.Domain.Domains.DomainStatus.Active => ("ok", "solid"),
        BillingOss.Domain.Domains.DomainStatus.Grace => ("warn", "ring"),
        BillingOss.Domain.Domains.DomainStatus.Expired => ("danger", "ring"),
        _ => ("neutral", "solid"),
    };

    /// <summary>Una alícuota como porcentaje: 0,21 da "21 %" y 0,105 da "10,5 %".</summary>
    public static string Percent(decimal rate) => (rate * 100m).ToString("0.##", CultureInfo.CurrentCulture) + " %";

    /// <summary>
    /// Un número escrito en un formulario, con punto o con coma. El enlace de modelos usa la cultura del pedido y en
    /// es-AR "0.21" se leería como 21: acá se acepta cualquiera de los dos separadores.
    /// </summary>
    public static decimal? ParseDecimal(string? text) =>
        decimal.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Una dirección de vuelta propia del sitio, o el inicio. Evita redirecciones a otros sitios.</summary>
    public static string SafeReturn(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal) ? url : "/";

    /// <summary>
    /// El staff con su sesión o mirando como cliente, el cliente con la suya, y "public" para lo que llega sin sesión
    /// (los botones de baja y de arrepentimiento, el ingreso).
    /// </summary>
    internal static Actor UiActor(ClaimsPrincipal user, IPAddress? ip) => user.Identity?.AuthenticationType switch
    {
        Schemes.Staff when user.UserId() is { } id => new(ActorKind.Staff, string.Create(CultureInfo.InvariantCulture, $"staff:{id}"), ip),
        Schemes.Portal when user.FindFirstValue(Claims.ActStaff) is { } staff => new(ActorKind.Staff, "staff:" + staff, ip),
        Schemes.Portal when user.UserId() is { } id => new(ActorKind.Customer, string.Create(CultureInfo.InvariantCulture, $"user:{id}"), ip),
        _ => new(ActorKind.Customer, "public", ip),
    };

    /// <summary>Lo que no es interfaz conserva sus respuestas: la API, los avisos de pasarelas y la salud.</summary>
    private static bool IsUi(PathString path) =>
        !(path.StartsWithSegments("/api") || path.StartsWithSegments("/webhooks") || path.StartsWithSegments("/includes")
          || path.StartsWithSegments("/health") || path.StartsWithSegments("/openapi"));

    private static void IdentityOptions(IdentityOptions o)
    {
        o.User.RequireUniqueEmail = true;
        o.User.AllowedUserNameCharacters = string.Empty;
        // El piso de NIST. El largo de verdad y el resto de la política salen de los ajustes del panel (PasswordPolicy).
        o.Password.RequiredLength = PasswordRules.Floor;
        o.Tokens.EmailConfirmationTokenProvider = EmailConfirmationTokenOptions.ProviderName;
        o.Password.RequireDigit = false;
        o.Password.RequireLowercase = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Lockout.AllowedForNewUsers = true;
    }

    private static void Cookie(CookieAuthenticationOptions o, string name, string path, SameSiteMode sameSite, TimeSpan lifetime)
    {
        o.Cookie.Name = name;
        o.Cookie.Path = path;
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = sameSite;
        o.ExpireTimeSpan = lifetime;
        o.SlidingExpiration = lifetime > Sessions.PendingLifetime;
    }
}
