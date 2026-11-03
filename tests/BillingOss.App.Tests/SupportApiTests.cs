using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Notifications;
using BillingOss.App.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>Tickets en la API propia: buscar y abrir sin duplicar, lo que usan los flujos de IVR y SMS (sección 5 de api-compatible.md).</summary>
[TestClass]
public sealed class SupportApiTests
{
    private static Task<long> Department(WebHarness w) => w.Query(async db =>
    {
        var department = new Department { Name = "Soporte técnico", Email = "soporte@hosting.example", CreatedAt = DateTimeOffset.UtcNow };
        db.Add(department);
        await db.SaveChangesAsync();
        return department.Id;
    });

    private static async Task<(HttpStatusCode Status, JsonNode? Body)> Send(WebHarness w, HttpMethod method, string path, string token, object? body = null, string? key = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await w.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    [TestMethod]
    public async Task Abrir_ConLaMismaClave_NoDuplica_YAvisaComoUnoQueAbreElCliente()
    {
        await using var w = await WebHarness.Start();
        var department = await Department(w);
        var writer = await w.Key("tickets:write");
        var reader = await w.Key("tickets:read");
        var call = new { department_id = department, subject = "Llamada al 0800", message = "Pide que lo llamen por el VPS.", account_id = w.AccountId, priority = "high" };

        var first = await Send(w, HttpMethod.Post, "/api/v1/tickets", writer, call, "llamada-123");
        var again = await Send(w, HttpMethod.Post, "/api/v1/tickets", writer, call, "llamada-123");
        var forbidden = await Send(w, HttpMethod.Post, "/api/v1/tickets", reader, call, "llamada-124");

        Assert.AreEqual(HttpStatusCode.Created, first.Status, first.Body?.ToJsonString());
        Assert.AreEqual(HttpStatusCode.Created, again.Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.Status);
        Assert.AreEqual(first.Body!["id"]!.GetValue<long>(), again.Body!["id"]!.GetValue<long>());
        var ticket = await w.Query(db => db.Set<Ticket>().AsNoTracking().SingleAsync());
        Assert.AreEqual((TicketStatus.Open, TicketPriority.High, w.AccountId), (ticket.Status, ticket.Priority, ticket.AccountId));
        Assert.AreEqual(first.Body!["number"]!.GetValue<string>(), ticket.Number);
        var message = await w.Query(db => db.Set<TicketMessage>().AsNoTracking().SingleAsync());
        Assert.AreEqual((MessageAuthor.Customer, MessageSource.Api), (message.Author, message.Source));

        // La misma clave para quien no es de una cuenta es otra consulta: la clave vale por cuenta o por correo.
        var other = await Send(w, HttpMethod.Post, "/api/v1/tickets", writer,
            new { department_id = department, subject = "SMS", message = "Quiere contratar.", requester_email = "nuevo@example.com" }, "llamada-123");
        Assert.AreEqual(HttpStatusCode.Created, other.Status, other.Body?.ToJsonString());
        Assert.AreNotEqual(first.Body!["id"]!.GetValue<long>(), other.Body!["id"]!.GetValue<long>());
        Assert.AreEqual("nuevo@example.com", await w.Query(db => db.Set<Ticket>().Where(t => t.AccountId == null).Select(t => t.RequesterEmail).SingleAsync()));

        await w.RunJobs();
        Assert.AreEqual(2, await w.Query(db => db.Set<OutboundEmail>().CountAsync(e => e.Template == TemplateKeys.TicketOpened)));
    }

    [TestMethod]
    public async Task Abrir_DatosQueNoSirven_DanErrorSinAbrir()
    {
        await using var w = await WebHarness.Start();
        var department = await Department(w);
        var writer = await w.Key("tickets:write");

        var noEmail = await Send(w, HttpMethod.Post, "/api/v1/tickets", writer, new { department_id = department, subject = "Hola", message = "Sin correo." });
        var badEmail = await Send(w, HttpMethod.Post, "/api/v1/tickets", writer, new { department_id = department, subject = "Hola", message = "x", requester_email = "no-es-correo" });
        var noDepartment = await Send(w, HttpMethod.Post, "/api/v1/tickets", writer, new { department_id = department + 99, subject = "Hola", message = "x", account_id = w.AccountId });
        var noAccount = await Send(w, HttpMethod.Post, "/api/v1/tickets", writer, new { department_id = department, subject = "Hola", message = "x", account_id = w.AccountId + 99 });
        var longKey = await Send(w, HttpMethod.Post, "/api/v1/tickets", writer, new { department_id = department, subject = "Hola", message = "x", account_id = w.AccountId }, new string('k', 151));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, noEmail.Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, badEmail.Status);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, noDepartment.Status);
        Assert.AreEqual(HttpStatusCode.NotFound, noAccount.Status);
        Assert.AreEqual(HttpStatusCode.BadRequest, longKey.Status);
        Assert.AreEqual(0, await w.Query(db => db.Set<Ticket>().CountAsync()));
    }

    [TestMethod]
    public async Task Buscar_PorCuentaCorreoYNumero_ElUnidoSoloPorSuNumero_YElDetalleConLasNotas()
    {
        await using var w = await WebHarness.Start();
        var department = await Department(w);
        var reader = await w.Key("tickets:read");
        OpenedTicket open, closed, merged;
        await using (var scope = w.Services.CreateAsyncScope())
        {
            var tickets = scope.ServiceProvider.GetRequiredService<TicketService>();
            var customer = new Author(MessageAuthor.Customer, "Cliente", "cliente@example.com", MessageSource.Portal);
            open = await tickets.Open(new TicketDraft(department, "No anda el correo", "Desde ayer.", w.AccountId), customer);
            closed = await tickets.Open(new TicketDraft(department, "Factura", "Ya está.", w.AccountId), customer);
            merged = await tickets.Open(new TicketDraft(department, "No anda el correo (2)", "Sigue.", w.AccountId), customer);
            await tickets.Close(closed.Id);
            await tickets.Merge(merged.Id, open.Id);
            await tickets.Reply(open.Id, new ReplyDraft("Revisar el MX.", Internal: true), new Author(MessageAuthor.Staff, "Ana", null, MessageSource.Panel));
        }

        var byAccount = await Send(w, HttpMethod.Get, $"/api/v1/tickets?account={w.AccountId}", reader);
        var all = await Send(w, HttpMethod.Get, "/api/v1/tickets?email=CLIENTE@example.com&status=all", reader);
        var byNumber = await Send(w, HttpMethod.Get, $"/api/v1/tickets?number=%23{merged.Number}", reader);
        var unknown = await Send(w, HttpMethod.Get, "/api/v1/tickets?status=pendiente", reader);
        var detail = await Send(w, HttpMethod.Get, $"/api/v1/tickets/{open.Id}", reader);
        var missing = await Send(w, HttpMethod.Get, $"/api/v1/tickets/{open.Id + 99}", reader);

        CollectionAssert.AreEqual(new[] { open.Id }, byAccount.Body!.AsArray().Select(t => t!["id"]!.GetValue<long>()).ToArray());
        CollectionAssert.AreEquivalent(new[] { open.Id, closed.Id }, all.Body!.AsArray().Select(t => t!["id"]!.GetValue<long>()).ToArray());
        Assert.AreEqual(open.Id, byNumber.Body!.AsArray().Single()!["merged_into_id"]!.GetValue<long>());
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.Status);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.Status);
        Assert.AreEqual("open", detail.Body!["status"]!.GetValue<string>());
        var messages = detail.Body!["messages"]!.AsArray();
        Assert.IsTrue(messages.Any(m => m!["internal"]!.GetValue<bool>() && m["body"]!.GetValue<string>() == "Revisar el MX."));
        Assert.IsTrue(messages.Any(m => m!["body"]!.GetValue<string>() == "Sigue."), "Los mensajes del unido están en el que lo recibió.");
    }
}
