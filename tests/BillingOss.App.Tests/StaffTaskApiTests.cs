using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

[TestClass]
public sealed class StaffTaskApiTests
{
    private static async Task<HttpResponseMessage> Send(WebHarness w, HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await w.Client.SendAsync(request);
    }

    [TestMethod]
    public async Task Admin_VeLaTareaAbierta_LaCierra_YElServicioQuedaActivo()
    {
        await using var w = await WebHarness.Start();
        var token = await w.Key("orders:write", "payments:write", "tasks:read", "tasks:write", "domains:read");
        var planId = await w.Query(async db =>
        {
            var instance = await ManualModule.AddInstance(db, "servicios-manuales", new JsonObject { ["queue"] = "servicios" }, DateTimeOffset.UtcNow);
            var product = await db.Products.SingleAsync(p => p.Id == w.ProductId);
            product.ModuleInstanceId = instance;
            product.ModuleParams = """{"activation":"staff","lifecycle":"staff","staff_fields":[{"id":"tunel","label":{"es":"Túnel"},"client_visible":true}]}""";
            await db.SaveChangesAsync();
            return await db.Plans.Where(p => p.ProductId == w.ProductId).Select(p => p.Id).SingleAsync();
        });

        using (await Send(w, HttpMethod.Post, "/api/v1/orders", token, new { account_id = w.AccountId, items = new[] { new { plan_id = planId } } }))
        using (await Send(w, HttpMethod.Post, "/api/v1/payments", token, new { account_id = w.AccountId, method = "transferencia", external_id = "T-1", currency = "ARS", amount = 12100m }))
        {
            await w.RunJobs();
        }

        using var open = await Send(w, HttpMethod.Get, "/api/v1/staff-tasks", token);
        var tasks = JsonNode.Parse(await open.Content.ReadAsStringAsync())!.AsArray();
        Assert.HasCount(1, tasks);
        Assert.AreEqual("create", tasks[0]!["operation"]!.GetValue<string>());
        var id = tasks[0]!["id"]!.GetValue<long>();

        using var unknownField = await Send(w, HttpMethod.Post, $"/api/v1/staff-tasks/{id}/complete", token, new { fields = new { otro = "x" } });
        using var completed = await Send(w, HttpMethod.Post, $"/api/v1/staff-tasks/{id}/complete", token, new { staff_ref = "S2S-1", fields = new { tunel = "tun-1" } });
        using var twice = await Send(w, HttpMethod.Post, $"/api/v1/staff-tasks/{id}/reject", token, new { reason = "tarde" });
        await w.RunJobs();

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, unknownField.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, completed.StatusCode, await completed.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.AreEqual(ServiceStatus.Active, (await w.Query(db => db.Services.SingleAsync())).Status);

        using var expired = await Send(w, HttpMethod.Get, "/api/v1/domains?status=expired", token);
        using var badStatus = await Send(w, HttpMethod.Get, "/api/v1/domains?status=otro", token);
        Assert.AreEqual("[]", await expired.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.BadRequest, badStatus.StatusCode);
    }

    [TestMethod]
    public async Task Staff_PideOperacionesYAcciones_ConOSinCuerpo()
    {
        await using var w = await WebHarness.Start();
        var token = await w.Key("orders:write", "payments:write", "services:write");
        var planId = await w.Query(async db =>
        {
            var instance = await ManualModule.AddInstance(db, "servicios-manuales", new JsonObject { ["queue"] = "servicios" }, DateTimeOffset.UtcNow);
            var product = await db.Products.SingleAsync(p => p.Id == w.ProductId);
            product.ModuleInstanceId = instance;
            product.ModuleParams = """{"activation":"on_payment","lifecycle":"state_only"}""";
            await db.SaveChangesAsync();
            return await db.Plans.Where(p => p.ProductId == w.ProductId).Select(p => p.Id).SingleAsync();
        });

        using (await Send(w, HttpMethod.Post, "/api/v1/orders", token, new { account_id = w.AccountId, items = new[] { new { plan_id = planId } } }))
        using (await Send(w, HttpMethod.Post, "/api/v1/payments", token, new { account_id = w.AccountId, method = "transferencia", external_id = "T-1", currency = "ARS", amount = 12100m }))
        {
            await w.RunJobs();
        }

        var id = (await w.Query(db => db.Services.SingleAsync())).Id;
        Assert.AreEqual(ServiceStatus.Active, (await w.Query(db => db.Services.SingleAsync())).Status);

        // El módulo manual no declara acciones, una operación desconocida no existe y el alta no se reintenta sobre un servicio activo.
        using var noAction = await Send(w, HttpMethod.Post, $"/api/v1/services/{id}/actions/reboot", token, new { @params = new { } });
        using var unknown = await Send(w, HttpMethod.Post, $"/api/v1/services/{id}/nope", token);
        using var notFailed = await Send(w, HttpMethod.Post, $"/api/v1/services/{id}/create", token);
        Assert.AreEqual(HttpStatusCode.NotFound, noAction.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, notFailed.StatusCode);

        // Suspender sin cuerpo y dar de baja con input: las dos se encolan y corren.
        using var suspend = await Send(w, HttpMethod.Post, $"/api/v1/services/{id}/suspend", token);
        Assert.AreEqual(HttpStatusCode.Accepted, suspend.StatusCode, await suspend.Content.ReadAsStringAsync());
        await w.RunJobs();
        Assert.AreEqual(ServiceStatus.Suspended, (await w.Query(db => db.Services.SingleAsync())).Status);

        using var terminate = await Send(w, HttpMethod.Post, $"/api/v1/services/{id}/terminate", token, new { input = new { force = true } });
        Assert.AreEqual(HttpStatusCode.Accepted, terminate.StatusCode, await terminate.Content.ReadAsStringAsync());
        await w.RunJobs();
        Assert.AreEqual(ServiceStatus.Terminated, (await w.Query(db => db.Services.SingleAsync())).Status);
        var payload = JsonNode.Parse((await w.Query(db => db.Jobs.Where(j => j.Kind == JobKinds.ServiceOperation).OrderByDescending(j => j.Id).FirstAsync())).Payload)!;
        Assert.IsTrue(JsonNode.Parse(payload["Input"]!.GetValue<string>())!["force"]!.GetValue<bool>());
    }
}
