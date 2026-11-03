using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BillingOss.Web.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BillingOss.App.Tests;

/// <summary>
/// Un proveedor OpenID Connect de mentira, en proceso: configuración, llaves, autorización con PKCE y canje del código.
/// Atiende cualquier host, así hace también de Google sin salir a la red (diseno/ingreso-clientes.md, sección 9).
/// </summary>
internal sealed class FakeOidcProvider : HttpMessageHandler
{
    public const string ClientId = "cliente-de-prueba.apps.googleusercontent.com";

    public const string ClientSecret = "secreto-de-prueba";

    private readonly RSA rsa = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Grant> codes = new();

    private sealed record Grant(string ClientId, string RedirectUri, string Challenge, string Nonce, string Issuer, string Subject, string? Email, bool Verified, string? Name);

    /// <summary>Cambia el ID token antes de firmarlo, para probar que el núcleo lo rechaza.</summary>
    public Action<SecurityTokenDescriptor>? Tamper { get; set; }

    public int TokenRequests => tokenRequests;

    private int tokenRequests;

    /// <summary>Lo suma al host de prueba en lugar de la red: PostConfigure corre después de la configuración de la aplicación.</summary>
    public void Install(IServiceCollection services) =>
        services.PostConfigure<HttpClientFactoryOptions>(ExternalSignIn.HttpClientName, o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = this));

    /// <summary>
    /// La persona entra en el proveedor y acepta: devuelve la dirección de vuelta con el código y el state, como la
    /// redirección del navegador. Controla lo que el núcleo tiene que mandar en la ida.
    /// </summary>
    public Uri Authorize(Uri authorization, string subject, string? email, bool verified = true, string? name = "Ana Pérez")
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var q = QueryHelpers.ParseQuery(authorization.Query);
        Assert.AreEqual("code", q["response_type"].ToString());
        Assert.AreEqual("S256", q["code_challenge_method"].ToString());
        Assert.AreEqual(43, q["code_challenge"].ToString().Length);
        StringAssert.Contains(q["scope"].ToString(), "openid");
        Assert.IsFalse(string.IsNullOrEmpty(q["state"]) || string.IsNullOrEmpty(q["nonce"]));
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var issuer = authorization.GetLeftPart(UriPartial.Path)[..^"/o/auth".Length];
        codes[code] = new Grant(q["client_id"]!, q["redirect_uri"]!, q["code_challenge"]!, q["nonce"]!, issuer, subject, email, verified, name);
        return new Uri(QueryHelpers.AddQueryString(q["redirect_uri"]!, new Dictionary<string, string?> { ["code"] = code, ["state"] = q["state"] }));
    }

    protected override void Dispose(bool disposing)
    {
        // La fábrica de clientes descarta los manejadores viejos: este vive lo que dura la prueba.
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
        {
            var issuer = $"{uri.Scheme}://{uri.Authority}{path[..^"/.well-known/openid-configuration".Length]}";
            return Json(HttpStatusCode.OK, new Dictionary<string, object>
            {
                ["issuer"] = issuer,
                ["authorization_endpoint"] = issuer + "/o/auth",
                ["token_endpoint"] = issuer + "/token",
                ["jwks_uri"] = issuer + "/jwks",
                ["response_types_supported"] = new[] { "code" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            });
        }

        if (path.EndsWith("/jwks", StringComparison.Ordinal))
        {
            var p = rsa.ExportParameters(false);
            return Json(HttpStatusCode.OK, new { keys = new[] { new { kty = "RSA", use = "sig", kid = "k1", alg = "RS256", n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent) } } });
        }

        if (path.EndsWith("/token", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
        {
            Interlocked.Increment(ref tokenRequests);
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (form["client_id"] != ClientId || form["client_secret"] != ClientSecret)
            {
                return Json(HttpStatusCode.Unauthorized, new { error = "invalid_client" });
            }

            // Un código sirve una vez, con la misma dirección de vuelta y el verificador de PKCE de la ida.
            if (form["grant_type"] != "authorization_code" || !codes.TryRemove(form["code"].ToString(), out var grant) || grant.RedirectUri != form["redirect_uri"]
                || grant.Challenge != Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))))
            {
                return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant" });
            }

            return Json(HttpStatusCode.OK, new { access_token = "token-de-prueba", token_type = "Bearer", expires_in = 3600, id_token = IdToken(grant) });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private string IdToken(Grant grant)
    {
        var claims = new Dictionary<string, object> { ["sub"] = grant.Subject, ["nonce"] = grant.Nonce, ["email_verified"] = grant.Verified };
        if (grant.Email is not null)
        {
            claims["email"] = grant.Email;
        }

        if (grant.Name is not null)
        {
            claims["name"] = grant.Name;
        }

        var key = new RsaSecurityKey(rsa) { KeyId = "k1" };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = grant.Issuer,
            Audience = grant.ClientId,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        };
        Tamper?.Invoke(descriptor);
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
