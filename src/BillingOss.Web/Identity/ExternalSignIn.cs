using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BillingOss.App;
using BillingOss.App.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace BillingOss.Web.Identity;

/// <summary>Quién volvió del proveedor, ya validado: su identificador ("sub") y su correo, con si el proveedor lo verificó.</summary>
public sealed record ExternalIdentity(string Provider, long ProviderId, string ProviderName, string Subject, string? Email, bool EmailVerified, string? Name);

/// <summary>Para qué se fue al proveedor y adónde sigue. <see cref="UserId"/> es la sesión que pidió vincular.</summary>
public sealed record ExternalTrip(string Purpose, string ReturnUrl, long? UserId);

public static class ExternalPurpose
{
    /// <summary>Ingresar, o registrarse si no hay usuario con ese correo.</summary>
    public const string Login = "login";

    /// <summary>Vincular el proveedor al usuario de la sesión, desde su página de seguridad.</summary>
    public const string Link = "link";
}

/// <summary>
/// Ingreso de clientes con un proveedor OpenID Connect (diseno/ingreso-clientes.md, sección 4): flujo de código con PKCE,
/// state y nonce, contra el proveedor que el staff carga en el panel. Lo que la vuelta necesita viaja en una cookie corta
/// y protegida, y la configuración del proveedor sale de la base: la vuelta puede caer en cualquier nodo y un cambio en
/// el panel vale enseguida, sin reiniciar.
/// </summary>
public sealed class ExternalSignIn(ExternalLoginStore store, OidcMetadata metadata, IHttpClientFactory http, IOptions<BillingOptions> options, ILogger<ExternalSignIn> logger)
{
    public const string HttpClientName = "oidc";

    /// <summary>Cuánto puede tardar la persona en el proveedor.</summary>
    public static TimeSpan TripLifetime { get; } = TimeSpan.FromMinutes(10);

    private static readonly JsonWebTokenHandler Tokens = new() { MapInboundClaims = false };

    /// <summary>Solo firmas asimétricas: una firmada con el secreto del cliente no prueba nada que el núcleo no sepa.</summary>
    private static readonly string[] Algorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
    ];

    public static string CallbackPath(string key) => $"/ingresar/externo/{key}/vuelta";

    /// <summary>La dirección de vuelta que se registra en el proveedor. Sale de Billing:PublicUrl, nunca del Host del pedido.</summary>
    public string? RedirectUri(string key) => options.Value.PublicUrl is { Length: > 0 } url ? url.TrimEnd('/') + CallbackPath(key) : null;

    /// <summary>
    /// La ida: guarda state, nonce y el verificador de PKCE en la cookie del viaje y devuelve la dirección del proveedor.
    /// Null si el proveedor no existe, está apagado, le falta el secreto o no se pudo leer su configuración.
    /// </summary>
    public async Task<string?> Challenge(HttpContext context, string key, string purpose, string? returnUrl, long? userId)
    {
        ArgumentNullException.ThrowIfNull(context);
        var provider = await store.Find(key, context.RequestAborted).ConfigureAwait(false);
        if (provider is not { Enabled: true, ClientSecret: not null } || provider.ClientId.Length == 0 || RedirectUri(provider.Key) is not { } redirect)
        {
            return null;
        }

        OpenIdConnectConfiguration config;
        try
        {
            config = await metadata.Get(provider.Authority, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo leer la configuración del proveedor {Provider}.", provider.Key);
            return null;
        }

        var state = RandomText();
        var nonce = RandomText();
        var verifier = RandomText();
        var properties = new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow + TripLifetime };
        properties.Items["provider"] = provider.Key;
        properties.Items["state"] = state;
        properties.Items["nonce"] = nonce;
        properties.Items["verifier"] = verifier;
        properties.Items["purpose"] = purpose;
        properties.Items["return"] = Ui.SafeReturn(returnUrl);
        if (userId is { } id)
        {
            properties.Items["user"] = id.ToString(CultureInfo.InvariantCulture);
        }

        await context.SignInAsync(Schemes.PortalChallenge, new ClaimsPrincipal(new ClaimsIdentity(Schemes.PortalChallenge)), properties).ConfigureAwait(false);
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = provider.ClientId,
            ["redirect_uri"] = redirect,
            ["scope"] = "openid email profile",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
        };
        if (provider.Kind == ExternalProviderKind.Google)
        {
            // Quien tiene varias cuentas de Google elige con cuál entra.
            query["prompt"] = "select_account";
        }

        return QueryHelpers.AddQueryString(config.AuthorizationEndpoint, query);
    }

    /// <summary>
    /// La vuelta: compara el state con el de la cookie del viaje (que se borra), canjea el código con el verificador de
    /// PKCE y valida el ID token (firma, emisor, destinatario, vencimiento y nonce). Devuelve la identidad o la clave del
    /// error para la pantalla. El viaje sale aunque falle, para saber adónde volver.
    /// </summary>
    public async Task<(ExternalIdentity? Identity, ExternalTrip? Trip, string? Error)> Callback(HttpContext context, string key, string? code, string? state, string? error)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.RequestAborted;
        var started = await context.AuthenticateAsync(Schemes.PortalChallenge).ConfigureAwait(false);
        await context.SignOutAsync(Schemes.PortalChallenge).ConfigureAwait(false);
        var items = started.Succeeded ? started.Properties : null;
        if (items is null || items.GetString("provider") != key || items.GetString("state") is not { } expected || !Same(expected, state)
            || items.GetString("nonce") is not { } nonce || items.GetString("verifier") is not { } verifier)
        {
            return (null, null, "External.Expired");
        }

        var trip = new ExternalTrip(items.GetString("purpose") ?? ExternalPurpose.Login, Ui.SafeReturn(items.GetString("return")),
            long.TryParse(items.GetString("user"), CultureInfo.InvariantCulture, out var user) ? user : null);
        if (error is not null || string.IsNullOrEmpty(code))
        {
            return (null, trip, error == "access_denied" ? "External.Cancelled" : "External.Failed");
        }

        var provider = await store.Find(key, ct).ConfigureAwait(false);
        if (provider is not { Enabled: true } || store.Secret(provider) is not { } secret || RedirectUri(key) is not { } redirect)
        {
            return (null, trip, "External.Unavailable");
        }

        try
        {
            var config = await metadata.Get(provider.Authority, ct).ConfigureAwait(false);
            var token = await Exchange(provider, config, secret, code, redirect, verifier, ct).ConfigureAwait(false);
            if (token is null)
            {
                return (null, trip, "External.Failed");
            }

            var result = await Validate(provider, config, token).ConfigureAwait(false);
            if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
            {
                // El proveedor rotó sus llaves: se vuelve a leer su configuración una vez.
                metadata.Refresh(provider.Authority);
                result = await Validate(provider, await metadata.Get(provider.Authority, ct).ConfigureAwait(false), token).ConfigureAwait(false);
            }

            if (!result.IsValid)
            {
                logger.LogWarning(result.Exception, "El ID token del proveedor {Provider} no es válido.", provider.Key);
                return (null, trip, "External.Failed");
            }

            var claims = result.Claims;
            var subject = Text(claims, "sub");
            if (string.IsNullOrEmpty(subject) || subject.Length > 255 || !Same(nonce, Text(claims, "nonce"))
                || (Text(claims, "azp") is { } azp && azp != provider.ClientId))
            {
                logger.LogWarning("El ID token del proveedor {Provider} no trae el nonce, el sub o el azp esperados.", provider.Key);
                return (null, trip, "External.Failed");
            }

            var email = Text(claims, "email")?.Trim();
            var verified = email is { Length: > 0 and <= 320 } && claims.TryGetValue("email_verified", out var v)
                && (v is true || (v?.ToString() is { } s && s.Equals("true", StringComparison.OrdinalIgnoreCase)));
            var name = Text(claims, "name")?.Trim();
            return (new ExternalIdentity(provider.Key, provider.Id, provider.Name, subject, email is { Length: > 0 and <= 320 } ? email : null, verified, name is { Length: > 200 } ? name[..200] : name), trip, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Falló la vuelta del proveedor {Provider}.", provider.Key);
            return (null, trip, "External.Failed");
        }
    }

    /// <summary>
    /// Prueba la conexión sin ninguna persona del otro lado: lee la configuración y canjea un código inventado. Si el
    /// proveedor responde que el código no vale, reconoció al cliente y su secreto. Devuelve la clave del resultado.
    /// </summary>
    public async Task<string> Test(ExternalProvider provider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (store.Secret(provider) is not { } secret || provider.ClientId.Length == 0)
        {
            return "External.TestIncomplete";
        }

        OpenIdConnectConfiguration config;
        try
        {
            config = await metadata.Get(provider.Authority, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No se pudo leer la configuración del proveedor {Provider}.", provider.Key);
            return "External.TestNoConfig";
        }

        try
        {
            using var client = http.CreateClient(HttpClientName);
            using var form = Form(provider, secret, "billing-oss-connection-test", RedirectUri(provider.Key) ?? provider.Authority, RandomText());
            using var response = await client.PostAsync(new Uri(config.TokenEndpoint), form, ct).ConfigureAwait(false);
            return ErrorCode(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) switch
            {
                "invalid_grant" => "External.TestOk",
                "invalid_client" or "unauthorized_client" => "External.TestBadClient",
                _ => "External.TestUnknown",
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "No se pudo probar el canje con el proveedor {Provider}.", provider.Key);
            return "External.TestNoConfig";
        }
    }

    private async Task<string?> Exchange(ExternalProvider provider, OpenIdConnectConfiguration config, string secret, string code, string redirect, string verifier, CancellationToken ct)
    {
        using var client = http.CreateClient(HttpClientName);
        using var form = Form(provider, secret, code, redirect, verifier);
        using var response = await client.PostAsync(new Uri(config.TokenEndpoint), form, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Solo el código de error: la respuesta podría traer datos de la persona.
            logger.LogWarning("El proveedor {Provider} rechazó el canje del código: {Status} {Error}.", provider.Key, (int)response.StatusCode, ErrorCode(body));
            return null;
        }

        using var json = JsonDocument.Parse(body);
        return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("id_token", out var token) && token.ValueKind == JsonValueKind.String
            ? token.GetString()
            : null;
    }

    private static Task<TokenValidationResult> Validate(ExternalProvider provider, OpenIdConnectConfiguration config, string token) =>
        Tokens.ValidateTokenAsync(token, new TokenValidationParameters
        {
            // Google firma con https://accounts.google.com, y en algunos flujos sin el esquema.
            ValidIssuers = provider.Kind == ExternalProviderKind.Google ? [config.Issuer, "accounts.google.com"] : [config.Issuer],
            ValidAudience = provider.ClientId,
            IssuerSigningKeys = config.SigningKeys,
            ValidAlgorithms = Algorithms,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        });

    private static FormUrlEncodedContent Form(ExternalProvider provider, string secret, string code, string redirect, string verifier) => new(new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["redirect_uri"] = redirect,
        ["client_id"] = provider.ClientId,
        ["client_secret"] = secret,
        ["code_verifier"] = verifier,
    });

    private static string? ErrorCode(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(IDictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value) ? value as string ?? value?.ToString() : null;

    private static bool Same(string expected, string? actual) =>
        actual is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    /// <summary>32 bytes al azar en base64url: 43 caracteres, lo que pide PKCE para el verificador.</summary>
    private static string RandomText() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
}

/// <summary>
/// La configuración de cada proveedor (su documento .well-known y sus llaves), leída por HTTPS y guardada en memoria del
/// nodo con el refresco del ConfigurationManager. Es un caché de datos públicos: cualquier nodo la vuelve a leer.
/// </summary>
public sealed class OidcMetadata(IHttpClientFactory http)
{
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> managers = new(StringComparer.Ordinal);

    public async Task<OpenIdConnectConfiguration> Get(string authority, CancellationToken ct)
    {
        var config = await Manager(authority).GetConfigurationAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(config.Issuer) || !Https(config.AuthorizationEndpoint) || !Https(config.TokenEndpoint))
        {
            throw new InvalidOperationException("La configuración del proveedor no trae el emisor o sus direcciones no son HTTPS.");
        }

        return config;
    }

    public void Refresh(string authority) => Manager(authority).RequestRefresh();

    private static bool Https(string? address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private ConfigurationManager<OpenIdConnectConfiguration> Manager(string authority) =>
        managers.GetOrAdd(authority, a => new(a.TrimEnd('/') + "/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(), new Retriever(http)));

    /// <summary>Solo HTTPS, con el cliente "oidc", que no sigue redirecciones.</summary>
    private sealed class Retriever(IHttpClientFactory http) : IDocumentRetriever
    {
        public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
        {
            if (!Https(address))
            {
                throw new InvalidOperationException("El proveedor solo se lee por HTTPS.");
            }

            using var client = http.CreateClient(ExternalSignIn.HttpClientName);
            return await client.GetStringAsync(new Uri(address), cancel).ConfigureAwait(false);
        }
    }
}
