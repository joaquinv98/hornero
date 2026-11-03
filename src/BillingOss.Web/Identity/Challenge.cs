using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using Microsoft.AspNetCore.DataProtection;

namespace BillingOss.Web.Identity;

/// <summary>Lo que necesita la página para mostrar el desafío de un formulario.</summary>
public sealed record ChallengeView(string Provider, string? Token, int Bits, string? SiteKey);

/// <summary>
/// Desafío anti robots opcional por formulario (diseno/ingreso-clientes.md, sección 7). La prueba de trabajo propia es
/// invisible: el navegador busca un número que, con el desafío firmado por el servidor, dé un SHA-256 con los primeros
/// bits en cero, y cada desafío sirve una vez en cualquier nodo. Turnstile, de Cloudflare, es invisible o de un clic y
/// se valida en su servidor con la clave secreta, cifrada por campo. Los botones de baja y de arrepentimiento nunca lo
/// piden: no lo llaman.
/// </summary>
public sealed class ChallengeGuard(SecuritySettingsStore settings, BillingDbContext db, IDataProtectionProvider protection, IHttpClientFactory http, IServiceProvider services, ILogger<ChallengeGuard> logger)
{
    public const string TokenField = "Challenge";

    public const string NonceField = "ChallengeNonce";

    public const string TurnstileField = "cf-turnstile-response";

    public const string HttpClientName = "challenge";

    public const string TurnstileOrigin = "https://challenges.cloudflare.com";

    public static readonly Uri TurnstileVerify = new(TurnstileOrigin + "/turnstile/v0/siteverify");

    /// <summary>Cuánto vale un desafío desde que se muestra la página.</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromMinutes(15);

    private const string Purpose = "BillingOss.Challenge";

    private const string SecretTable = "identity.security_settings";

    private const string SecretColumn = "challenge_secret";

    /// <summary>
    /// El desafío del formulario, o null si no pide ninguno. Con Turnstile suma su origen a la política de contenido de
    /// esta respuesta, que por defecto solo deja cargar lo propio.
    /// </summary>
    public async Task<ChallengeView?> For(HttpContext context, string form)
    {
        ArgumentNullException.ThrowIfNull(context);
        var s = await settings.Get(context.RequestAborted).ConfigureAwait(false);
        switch (s.ChallengeFor(form))
        {
            case SecuritySettings.ChallengeWork:
                var token = protection.CreateProtector(Purpose).ToTimeLimitedDataProtector()
                    .Protect(JsonSerializer.Serialize(new Ticket(form, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)))), Lifetime);
                return new ChallengeView(SecuritySettings.ChallengeWork, token, s.ChallengeBits, null);
            case SecuritySettings.ChallengeTurnstile:
                context.Response.Headers.ContentSecurityPolicy =
                    $"default-src 'self'; script-src 'self' {TurnstileOrigin}; frame-src {TurnstileOrigin}; connect-src 'self' {TurnstileOrigin}; img-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
                return new ChallengeView(SecuritySettings.ChallengeTurnstile, null, 0, s.ChallengeSiteKey);
            default:
                return null;
        }
    }

    /// <summary>True si el formulario no pide desafío o si el que vino con el pedido vale.</summary>
    public async Task<bool> Verify(HttpContext context, string form)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.RequestAborted;
        var s = await settings.Get(ct).ConfigureAwait(false);
        var fields = context.Request.HasFormContentType ? await context.Request.ReadFormAsync(ct).ConfigureAwait(false) : null;
        return s.ChallengeFor(form) switch
        {
            SecuritySettings.ChallengeWork => await Work(form, fields?[TokenField], fields?[NonceField], s.ChallengeBits, ct).ConfigureAwait(false),
            SecuritySettings.ChallengeTurnstile => await Turnstile(s, fields?[TurnstileField], context.Connection.RemoteIpAddress?.ToString(), ct).ConfigureAwait(false),
            _ => true,
        };
    }

    /// <summary>La clave secreta cifrada, en base64, para guardarla en los ajustes.</summary>
    public string SealSecret(string secret) =>
        Convert.ToBase64String(Box.Encrypt(secret, SecretTable, SecretColumn, SecuritySettingsRow.SingleId, TurnstileVerify.ToString()));

    /// <summary>Cuántos bits en cero tiene al principio el SHA-256 del desafío con el número.</summary>
    public static int LeadingZeroBits(string token, string nonce)
    {
        ArgumentNullException.ThrowIfNull(token);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token + ":" + nonce));
        var bits = 0;
        foreach (var b in hash)
        {
            if (b == 0)
            {
                bits += 8;
                continue;
            }

            return bits + System.Numerics.BitOperations.LeadingZeroCount((uint)b) - 24;
        }

        return bits;
    }

    private async Task<bool> Work(string form, string? token, string? nonce, int bits, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || nonce is not { Length: > 0 and <= 20 } || !nonce.All(char.IsAsciiDigit))
        {
            return false;
        }

        Ticket? ticket;
        try
        {
            ticket = JsonSerializer.Deserialize<Ticket>(protection.CreateProtector(Purpose).ToTimeLimitedDataProtector().Unprotect(token, out var expires));
            if (ticket is null || ticket.Form != form || LeadingZeroBits(token, nonce) < bits)
            {
                return false;
            }

            return await ChallengeUses.TryUse(db, ticket.Id, expires, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return false;
        }
    }

    private async Task<bool> Turnstile(SecuritySettings s, string? response, string? ip, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(response) || response.Length > 2048 || s.ChallengeSecret is not { } sealedSecret)
        {
            return false;
        }

        try
        {
            var secret = Box.Decrypt(Convert.FromBase64String(sealedSecret), SecretTable, SecretColumn, SecuritySettingsRow.SingleId, TurnstileVerify.ToString());
            var form = new Dictionary<string, string> { ["secret"] = secret, ["response"] = response };
            if (ip is not null)
            {
                form["remoteip"] = ip;
            }

            using var client = http.CreateClient(HttpClientName);
            using var content = new FormUrlEncodedContent(form);
            using var answer = await client.PostAsync(TurnstileVerify, content, ct).ConfigureAwait(false);
            using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return answer.IsSuccessStatusCode && json.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or CryptographicException or FormatException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Sin respuesta del proveedor el formulario no pasa: el tope por IP y las listas siguen valiendo igual.
            logger.LogWarning(ex, "No se pudo validar el desafío anti robots con Turnstile.");
            return false;
        }
    }

    private SecretBox Box => services.GetService<SecretBox>()
        ?? throw new InvalidOperationException("Falta la llave de secretos (Secrets:KeyFile): sin ella no se guarda ni se lee la clave del desafío.");

    private sealed record Ticket(string Form, string Id);
}
