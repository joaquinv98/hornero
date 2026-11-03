using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BillingOss.App.Persistence;
using BillingOss.App.Support;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.Web.Api;

public sealed record TicketRequest(long DepartmentId, string Subject, string Message, long? AccountId = null, string? RequesterName = null, string? RequesterEmail = null,
    TicketPriority Priority = TicketPriority.Normal, long? ServiceId = null, IReadOnlyList<string>? Cc = null);

/// <summary>
/// Tickets en la API propia: buscar y abrir sin duplicar, lo que usan los flujos de IVR y SMS (sección 5 de
/// api-compatible.md). Abrir con la misma Idempotency-Key devuelve el mismo ticket, también desde dos nodos a la vez.
/// </summary>
internal static class TicketsApi
{
    /// <summary>Largo máximo de la Idempotency-Key: con la cuenta adelante entra en la referencia del ticket.</summary>
    public const int MaxKey = 150;

    public static void MapTickets(this RouteGroupBuilder api)
    {
        // Sin estado, los que no están cerrados. Los unidos a otro aparecen solo buscados por su número, con el que los
        // recibió en merged_into_id, y el número busca en todos los estados.
        api.MapGet("/tickets", async (long? account, string? email, string? number, string? status, BillingDbContext db, CancellationToken ct) =>
        {
            var byNumber = number?.Trim().TrimStart('#') is { Length: > 0 } n ? n : null;
            var tickets = byNumber is null ? db.Set<Ticket>().AsNoTracking().Where(t => t.MergedIntoId == null) : db.Set<Ticket>().AsNoTracking().Where(t => t.Number == byNumber);
            if (account is { } accountId)
            {
                tickets = tickets.Where(t => t.AccountId == accountId);
            }

            if (email?.Trim().ToLowerInvariant() is { Length: > 0 } address)
            {
                tickets = tickets.Where(t => t.RequesterEmail.ToLower() == address);
            }

            tickets = (string.IsNullOrEmpty(status) ? byNumber is null ? "open" : "all" : status) switch
            {
                "all" => tickets,
                "closed" => tickets.Where(t => t.Status == TicketStatus.Closed),
                "open" => tickets.Where(t => t.Status != TicketStatus.Closed),
                _ => throw new ArgumentException($"Estado desconocido: {status}. Se acepta open, closed o all."),
            };
            return Results.Ok(await tickets.OrderByDescending(t => t.LastMessageAt).ThenByDescending(t => t.Id).Take(100)
                .Select(t => new { t.Id, t.Number, t.Subject, t.Status, t.Priority, t.DepartmentId, t.AccountId, t.RequesterEmail, t.ServiceId, t.AssignedStaffId, t.MergedIntoId, t.CreatedAt, t.LastMessageAt })
                .ToListAsync(ct));
        }).WithMetadata(new RequireScope("tickets:read"));

        // Para el staff y sus integraciones: trae también las notas internas, marcadas.
        api.MapGet("/tickets/{id:long}", async (long id, BillingDbContext db, CancellationToken ct) =>
        {
            var t = await db.Set<Ticket>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (t is null)
            {
                return Results.Problem(statusCode: 404, title: $"No existe el ticket {id}.");
            }

            var messages = await db.Set<TicketMessage>().AsNoTracking().Where(m => m.TicketId == id).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
                .Select(m => new { m.Id, m.Author, m.AuthorName, m.Internal, m.Source, m.Body, m.CreatedAt }).ToListAsync(ct);
            return Results.Ok(new
            {
                t.Id,
                t.Number,
                t.Subject,
                t.Status,
                t.Priority,
                t.DepartmentId,
                t.AccountId,
                t.RequesterName,
                t.RequesterEmail,
                t.Cc,
                t.ServiceId,
                t.AssignedStaffId,
                t.MergedIntoId,
                t.CreatedAt,
                t.LastMessageAt,
                messages,
            });
        }).WithMetadata(new RequireScope("tickets:read"));

        // Abre a nombre del cliente o de quien escribió: avisa como un ticket que abre el cliente. La Idempotency-Key vale
        // por cuenta, o por correo si no hay cuenta, como la de los pedidos: dos integraciones con la misma no se pisan.
        api.MapPost("/tickets", async (TicketRequest r, HttpContext http, BillingDbContext db, TicketService tickets, CancellationToken ct) =>
        {
            var key = http.Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
            if (key?.Length > MaxKey)
            {
                throw new ArgumentException($"La Idempotency-Key supera los {MaxKey} caracteres.");
            }

            var account = r.AccountId is { } accountId
                ? await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => new { a.Name, a.Email }).SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException($"No existe la cuenta {accountId}.")
                : null;
            var email = string.IsNullOrWhiteSpace(r.RequesterEmail) ? account?.Email : r.RequesterEmail.Trim();
            var name = string.IsNullOrWhiteSpace(r.RequesterName) ? account?.Name ?? email : r.RequesterName.Trim();
            var reference = string.IsNullOrEmpty(key) ? null : $"{Scope(r.AccountId, email)}:{key}";
            var opened = await tickets.Open(
                new TicketDraft(r.DepartmentId, r.Subject, r.Message, r.AccountId, r.Priority, r.ServiceId, RequesterName: name, RequesterEmail: email, Cc: r.Cc, ClientReference: reference),
                new Author(MessageAuthor.Customer, name ?? string.Empty, email, MessageSource.Api), ct);
            return Results.Created($"/api/v1/tickets/{opened.Id}", new { opened.Id, opened.Number });
        }).WithMetadata(new RequireScope("tickets:write"));
    }

    /// <summary>"a42" para la cuenta 42, o el correo resumido: la referencia tiene 200 caracteres como mucho.</summary>
    private static string Scope(long? accountId, string? email) => accountId is { } id
        ? string.Create(CultureInfo.InvariantCulture, $"a{id}")
        : "e" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((email ?? string.Empty).ToLowerInvariant())))[..16];
}
