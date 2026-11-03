using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Identity;

/// <summary>
/// Pedido de baja o de arrepentimiento hecho con el botón público, sin iniciar sesión (sección 12.3 del modelo de
/// dominio y Disposición 954/2025). La identidad se verifica con un código al correo de la cuenta. Al confirmarlo
/// queda el código de constancia, que se muestra en pantalla y se manda por correo.
/// </summary>
public sealed class CancellationRequest
{
    public long Id { get; private set; }

    public ConsumerRequestKind Kind { get; set; }

    /// <summary>El correo que escribió quien hizo el pedido, en minúsculas.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Las cuentas con ese correo. Casi siempre una, pero el sistema anterior permitía repetirlo.</summary>
    public long[] AccountIds { get; set; } = [];

    /// <summary>Lo que escribió la persona: qué servicio y cualquier aclaración. Texto plano.</summary>
    public string? Detail { get; set; }

    /// <summary>Referencia aleatoria del paso de verificación. No dice nada de la cuenta.</summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>SHA-256 del código mandado por correo.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public DateTimeOffset CodeExpiresAt { get; set; }

    public int Attempts { get; set; }

    /// <summary>Código de registración de la constancia. Nulo hasta que se verifica el correo.</summary>
    public string? Receipt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string? Ip { get; set; }
}

public sealed record ConsumerReceipt(string Receipt, DateTimeOffset At, ConsumerRequestKind Kind);

/// <summary>Los dos pasos del botón de baja y del de arrepentimiento.</summary>
public sealed class ConsumerRequestService(BillingDbContext db, IAccountMail mail, AuditLog audit, TimeProvider time)
{
    public static TimeSpan CodeLifetime { get; } = TimeSpan.FromMinutes(30);

    public const int MaxAttempts = 5;

    /// <summary>
    /// Manda el código si el correo es de alguna cuenta. Siempre devuelve una referencia, exista o no la cuenta, para
    /// que el formulario no sirva para averiguar qué correos son clientes.
    /// </summary>
    public async Task<string> Start(ConsumerRequestKind kind, string email, string? detail, string? ip, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        var normalized = email.Trim().ToLowerInvariant();
        var reference = Token(16);
        if (detail is { Length: > 1000 })
        {
            throw new ValidationException("El detalle no puede pasar de 1000 caracteres.");
        }

        var accounts = await db.Accounts.AsNoTracking().Where(a => a.Email.ToLower() == normalized).OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.Language }).ToListAsync(ct).ConfigureAwait(false);
        if (accounts.Count == 0)
        {
            return reference;
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var now = time.GetUtcNow();
        db.Set<CancellationRequest>().Add(new CancellationRequest
        {
            Kind = kind,
            Email = normalized,
            AccountIds = [.. accounts.Select(a => a.Id)],
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
            Reference = reference,
            CodeHash = Hash(code),
            CodeExpiresAt = now + CodeLifetime,
            CreatedAt = now,
            Ip = ip,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await mail.SendConsumerCode(normalized, accounts[0].Language, kind, code, ct).ConfigureAwait(false);
        return reference;
    }

    /// <summary>
    /// Verifica el código y registra el pedido. Devuelve null si la referencia no existe, el código venció, se pasaron
    /// los intentos o no coincide: los cuatro casos se ven igual desde afuera.
    /// </summary>
    public async Task<ConsumerReceipt?> Confirm(string reference, string code, CancellationToken ct = default)
    {
        var request = await db.Set<CancellationRequest>().AsNoTracking().SingleOrDefaultAsync(r => r.Reference == reference, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (request is null || request.CodeExpiresAt <= now || request.Attempts >= MaxAttempts)
        {
            return null;
        }

        // El intento se cuenta antes de comparar y en la base: varios pedidos a la vez no pasan el tope.
        var requests = db.Set<CancellationRequest>().Where(r => r.Id == request.Id);
        if (await requests.Where(r => r.Attempts < MaxAttempts).ExecuteUpdateAsync(u => u.SetProperty(r => r.Attempts, r => r.Attempts + 1), ct).ConfigureAwait(false) == 0)
        {
            return null;
        }

        var expected = Encoding.ASCII.GetBytes(request.CodeHash);
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(Hash(code?.Trim() ?? string.Empty))))
        {
            return null;
        }

        // Una sola constancia por pedido: si otro pedido la registró antes (un doble clic), se devuelve esa.
        var receipt = (request.Kind == ConsumerRequestKind.Cancellation ? "BAJA-" : "ARRE-") + Token(8).ToUpperInvariant();
        if (await requests.Where(r => r.Receipt == null).ExecuteUpdateAsync(u => u.SetProperty(r => r.Receipt, receipt).SetProperty(r => r.ConfirmedAt, now), ct).ConfigureAwait(false) == 0)
        {
            var existing = await requests.AsNoTracking().SingleAsync(ct).ConfigureAwait(false);
            return new ConsumerReceipt(existing.Receipt!, existing.ConfirmedAt!.Value, existing.Kind);
        }

        request.Receipt = receipt;

        // Quien lo pide no inició sesión: queda como "public" con la IP del pedido.
        Actor.Current = new Actor(ActorKind.Customer, "public", IPAddress.TryParse(request.Ip, out var ip) ? ip : null);
        await audit.Record(request.Kind == ConsumerRequestKind.Cancellation ? "consumer.cancellation" : "consumer.withdrawal",
            string.Create(CultureInfo.InvariantCulture, $"request:{request.Id}"), new { accounts = request.AccountIds, receipt = request.Receipt }, ct).ConfigureAwait(false);
        var language = await db.Accounts.AsNoTracking().Where(a => a.Id == request.AccountIds[0]).Select(a => a.Language).SingleAsync(ct).ConfigureAwait(false);
        await mail.SendConsumerReceipt(request.Email, language, request.Kind, request.Receipt, now, ct).ConfigureAwait(false);
        return new ConsumerReceipt(request.Receipt, now, request.Kind);
    }

    private static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    /// <summary>Letras y números sin los que se confunden al dictarlos (0, O, 1, I, L).</summary>
    private static string Token(int length) => RandomNumberGenerator.GetString("23456789abcdefghjkmnpqrstuvwxyz", length);
}
