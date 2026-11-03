using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Identity;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Shared;
using BillingOss.Domain.Usage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>API del uso medido (sección 9 de diseno/uso-medido.md).</summary>
[TestClass]
public sealed class UsageApiTests
{
    [TestMethod]
    public async Task Api_ConfiguraMetricasYPreciosConPermisos_YDaElUsoDelServicio()
    {
        await using var w = await WebHarness.Start();
        long serviceId;
        await using (var scope = w.Services.CreateAsyncScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<ModuleRegistry>();
            var installation = await registry.Install("medidor", "https://medidor.example.net/", "firma", FakeMeter.Manifest());
            var instance = await registry.AddInstance(installation.Id, "cluster");
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            (await db.Products.SingleAsync(p => p.Id == w.ProductId)).ModuleInstanceId = instance.Id;
            await db.SaveChangesAsync();
            var planId = await db.Plans.Where(p => p.ProductId == w.ProductId).Select(p => p.Id).SingleAsync();
            serviceId = (await scope.ServiceProvider.GetRequiredService<OrderService>().PlaceOrder(w.AccountId, [new OrderItem(planId)])).ServiceIds[0];
        }

        var reader = await w.Key("catalog:read", "services:read");
        var writer = await w.Key("catalog:write");
        var metric = new { included = 100m, step = 1m, cycle = "calendar_month", alert_percents = new[] { 90 } };
        var path = $"/api/v1/products/{w.ProductId}/usage-metrics";

        using (var forbidden = await w.Client.SendAsync(Request(HttpMethod.Put, $"{path}/transfer_gb", reader, metric)))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var saved = await w.Client.SendAsync(Request(HttpMethod.Put, $"{path}/transfer_gb", writer, metric)))
        {
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
            var body = JsonNode.Parse(await saved.Content.ReadAsStringAsync())!;
            Assert.AreEqual(("Transferencia", "counter", "sum", "calendar_month"), (body["name"]!.GetValue<string>(), body["kind"]!.GetValue<string>(), body["aggregation"]!.GetValue<string>(), body["cycle"]!.GetValue<string>()));
        }

        using (var unknown = await w.Client.SendAsync(Request(HttpMethod.Put, $"{path}/cpu_hours", writer, metric)))
        {
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, unknown.StatusCode, await unknown.Content.ReadAsStringAsync());
        }

        var price = new { currency = "ARS", unit_price = 500m, valid_from = "2026-01-01" };
        using (var created = await w.Client.SendAsync(Request(HttpMethod.Post, $"{path}/transfer_gb/prices", writer, price)))
        {
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        }

        using (var repeated = await w.Client.SendAsync(Request(HttpMethod.Post, $"{path}/transfer_gb/prices", writer, price)))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, repeated.StatusCode, await repeated.Content.ReadAsStringAsync());
        }

        using (var list = await w.Client.SendAsync(Request(HttpMethod.Get, path, reader)))
        {
            var metrics = JsonNode.Parse(await list.Content.ReadAsStringAsync())!.AsArray();
            Assert.AreEqual(500m, metrics.Single()!["prices"]![0]!["unit_price"]!.GetValue<decimal>());
            Assert.AreEqual(90, metrics.Single()!["alert_percents"]![0]!.GetValue<int>());
        }

        await w.Query(async db =>
        {
            db.Add(new UsageSample(serviceId, "transfer_gb", new DateOnly(2026, 11, 2), 60m, 60m, DateTimeOffset.UtcNow));
            db.Add(new UsageSample(serviceId, "transfer_gb", new DateOnly(2026, 11, 3), 70m, 70m, DateTimeOffset.UtcNow));
            return await db.SaveChangesAsync();
        });
        using (var usage = await w.Client.SendAsync(Request(HttpMethod.Get, $"/api/v1/services/{serviceId}/usage", reader)))
        {
            Assert.AreEqual(HttpStatusCode.OK, usage.StatusCode, await usage.Content.ReadAsStringAsync());
            var open = JsonNode.Parse(await usage.Content.ReadAsStringAsync())!["metrics"]![0]!;
            Assert.AreEqual((130m, 30m, 15000m, 130), (open["used"]!.GetValue<decimal>(), open["excess"]!.GetValue<decimal>(), open["estimated"]!.GetValue<decimal>(), open["percent"]!.GetValue<int>()));
            Assert.AreEqual("2026-11-02", open["since"]!.GetValue<string>());
        }

        using var missing = await w.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/services/999999/usage", reader));
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }
}

/// <summary>El uso medido en el portal y en el admin.</summary>
public sealed partial class UiTests
{
    [TestMethod]
    public async Task Uso_ElClienteVeElDeSusServicios_LoAjenoDa404_YElStaffVeLasLecturas()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        await h.PortalUser("pagos@example.com", h.AccountA, owner: false, capabilities: [AccountCapabilities.Invoices]);
        var (serviceA, serviceB, productId) = await h.Query(async db =>
        {
            var a = await db.Services.SingleAsync(s => s.AccountId == h.AccountA);
            var b = await db.Services.SingleAsync(s => s.AccountId == h.AccountB);
            var now = DateTimeOffset.UtcNow;
            var metric = new UsageMetric(a.ProductId, "transfer_gb", UsageKind.Counter, now);
            metric.Configure("Transferencia", "GB", UsageAggregation.Sum, 100m, 1m, UsageCycle.Renewal, [80, 100], true, now);
            db.Add(metric);
            await db.SaveChangesAsync();
            db.Add(new UsageMetricPrice(metric.Id, Currency.Ars, 500m, new DateOnly(2026, 1, 1)));
            db.Add(new UsageSample(a.Id, "transfer_gb", new DateOnly(2026, 11, 2), 60m, 60m, now));
            db.Add(new UsageSample(a.Id, "transfer_gb", new DateOnly(2026, 11, 3), 70m, 70m, now));
            await db.SaveChangesAsync();
            return (a.Id, b.Id, a.ProductId);
        });

        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();
        StringAssert.Contains(await c.Html("/servicios"), $"/servicios/{serviceA}/uso");
        var page = await c.Html($"/servicios/{serviceA}/uso");
        StringAssert.Contains(page, "Transferencia");
        StringAssert.Contains(page, "130 GB");
        StringAssert.Contains(page, "de 100 GB incluidos");
        StringAssert.Contains(page, "<meter");
        StringAssert.Contains(page, "130 % de lo incluido");
        StringAssert.Contains(page, "Estimado: 15.000,00 ARS");
        Assert.AreEqual(HttpStatusCode.NotFound, (await c.Get($"/servicios/{serviceB}/uso")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await c.Get("/servicios/999999/uso")).StatusCode);

        // Sin la capacidad de servicios da lo mismo que lo ajeno.
        using var billing = h.NewClient();
        (await billing.Login("pagos@example.com")).Dispose();
        Assert.AreEqual(HttpStatusCode.NotFound, (await billing.Get($"/servicios/{serviceA}/uso")).StatusCode);

        // El staff con accounts.read ve las lecturas diarias, y configurar pide catalog.write.
        await h.Staff("lector@example.com", StaffPermissions.AccountsRead);
        using var staff = await StaffSession(h, "lector@example.com");
        StringAssert.Contains(await staff.Html($"/admin/cuentas/{h.AccountA}"), $"/admin/servicios/{serviceA}/uso");
        var admin = await staff.Html($"/admin/servicios/{serviceA}/uso");
        StringAssert.Contains(admin, "Lecturas diarias");
        StringAssert.Contains(admin, "En curso");
        Assert.IsFalse(admin.Contains($"/admin/catalogo/productos/{productId}/uso", StringComparison.Ordinal));
        using var config = await staff.Get($"/admin/catalogo/productos/{productId}/uso");
        StringAssert.Contains(Location(config), "/estado/403");
    }

    [TestMethod]
    public async Task Uso_ElStaffConfiguraLasMetricasDelProducto_ConComaOPuntoDecimal()
    {
        await using var h = await UiHarness.Start();
        var productId = await h.Query(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var installation = new ModuleInstallation { Name = "medidor", BaseUrl = "https://medidor.example.net/", Manifest = FakeMeter.Manifest().ToJsonString(), CreatedAt = now };
            db.ModuleInstallations.Add(installation);
            await db.SaveChangesAsync();
            var instance = new ModuleInstance { InstallationId = installation.Id, Name = "cluster", CreatedAt = now };
            db.ModuleInstances.Add(instance);
            await db.SaveChangesAsync();
            var product = await db.Products.SingleAsync(p => p.Name == "VPS 2 GB");
            product.ModuleInstanceId = instance.Id;
            await db.SaveChangesAsync();
            return product.Id;
        });
        await h.Staff("catalogo@example.com", StaffPermissions.CatalogWrite);
        using var c = await StaffSession(h, "catalogo@example.com");
        var url = $"/admin/catalogo/productos/{productId}/uso";

        var page = await c.Html(url);
        StringAssert.Contains(page, "Este producto todavía no mide uso.");
        StringAssert.Contains(page, "value=\"transfer_gb\"");

        using (var saved = await c.Submit(url, $"{url}?handler=Metric", new()
        {
            ["code"] = "disk_gb", ["included"] = "10,5", ["step"] = "0.5", ["cycle"] = "CalendarMonth", ["aggregation"] = "Average", ["alerts"] = "90", ["active"] = "true",
        }))
        {
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
            StringAssert.Contains(await saved.Content.ReadAsStringAsync(), "Listo, quedó guardado.");
        }

        var metric = await h.Query(db => db.Set<UsageMetric>().SingleAsync());
        Assert.AreEqual((10.5m, 0.5m, UsageAggregation.Average, UsageCycle.CalendarMonth, "Disco"), (metric.Included, metric.Step, metric.Aggregation, metric.Cycle, metric.Name));
        CollectionAssert.AreEqual(new[] { 90 }, metric.AlertPercents);

        using (var price = await c.Submit(url, $"{url}?handler=Price", new() { ["code"] = "disk_gb", ["currency"] = "ars", ["unitPrice"] = "1234,5", ["validFrom"] = "2026-01-01" }))
        {
            StringAssert.Contains(await price.Content.ReadAsStringAsync(), "Listo, quedó guardado.");
        }

        Assert.AreEqual((1234.5m, Currency.Ars), await h.Query(db => db.Set<UsageMetricPrice>().Select(p => new ValueTuple<decimal, Currency>(p.UnitPrice, p.Currency)).SingleAsync()));

        // Lo que no cumple una regla se explica y no se guarda.
        using (var bad = await c.Submit(url, $"{url}?handler=Metric", new() { ["code"] = "transfer_gb", ["aggregation"] = "Max", ["included"] = "1", ["cycle"] = "Renewal" }))
        {
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
            StringAssert.Contains(await bad.Content.ReadAsStringAsync(), "No se guardó: Un contador se resume con la suma.");
        }

        Assert.AreEqual(1, await h.Query(db => db.Set<UsageMetric>().CountAsync()));
        Assert.AreEqual(2, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action.StartsWith("usage."))));
    }
}
