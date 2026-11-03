using System.Net;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Panel de actualizaciones (D-20, sección 7.2): permiso, menú de la sección 10 de D-19, estado y pedido con el segundo factor.</summary>
public sealed partial class UiTests
{
    [TestMethod]
    public async Task Actualizaciones_CuelgaDeConfiguracion_YSoloConSuPermiso()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("lector@example.com", StaffPermissions.AccountsRead);
        await h.Staff("admin@example.com", StaffPermissions.AccountsRead, StaffPermissions.SystemUpdates);

        using (var lector = await StaffSession(h, "lector@example.com"))
        {
            Assert.IsFalse((await lector.Html("/admin")).Contains("/admin/actualizaciones", StringComparison.Ordinal));
            using var forbidden = await lector.Get("/admin/actualizaciones");
            Assert.AreEqual(HttpStatusCode.Redirect, forbidden.StatusCode);
            StringAssert.Contains(Location(forbidden), "/estado/403");
        }

        using var admin = await StaffSession(h, "admin@example.com");
        var home = await admin.Html("/admin");
        StringAssert.Contains(home, "<summary>Configuración</summary>");
        StringAssert.Contains(home, "<a href=\"/admin/actualizaciones\">Actualizaciones</a>");

        // Sin actualizador ni canal, nunca "al día".
        var page = await admin.Html("/admin/actualizaciones");
        StringAssert.Contains(page, "Sin datos");
        StringAssert.Contains(page, "Todavía no hay un chequeo del canal");
    }

    [TestMethod]
    public async Task Actualizaciones_ConUnParcheDeSeguridad_PideElCodigoYDejaElPedidoAuditado()
    {
        await using var h = await UiHarness.Start();
        var staff = await h.Staff("admin@example.com", StaffPermissions.SystemUpdates);
        var c = h.NewClient();
        (await c.Submit("/admin/ingresar", "/admin/ingresar", new() { ["Email"] = "admin@example.com", ["Password"] = UiHarness.Password })).Dispose();
        var key = Key(await c.Html("/admin/ingresar/codigo"));
        (await c.Submit("/admin/ingresar/codigo", "/admin/ingresar/codigo", new() { ["Code"] = UiHarness.Totp(key) })).Dispose();

        var channel = UpdatesTests.Channel(UpdatesTests.Release("0.1.1", security: true)) with { Issued = DateTimeOffset.UtcNow.AddHours(-1), Expires = DateTimeOffset.UtcNow.AddDays(6) };
        // Lo que deja el actualizador con el rol dueño: el canal verificado y la señal de un nodo.
        await h.Query(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE updates.state SET channel = {0}::jsonb, checked_at = now(), check_attempt_at = now()", channel.ToJson());
            return await db.Database.ExecuteSqlRawAsync("INSERT INTO updates.nodes (name, version, seen_at) VALUES ('nodo1', '0.1.0', now())");
        });

        var page = await c.Html("/admin/actualizaciones");
        StringAssert.Contains(page, "Actualizá ya");
        StringAssert.Contains(page, "Falta un parche de seguridad.");
        StringAssert.Contains(page, "CVE-2026-0001");
        StringAssert.Contains(page, "href=\"/admin/actualizaciones?version=0.1.1#confirmar\"");

        var confirm = "/admin/actualizaciones?version=0.1.1";
        StringAssert.Contains(await c.Html(confirm), "Confirmá la actualización a 0.1.1");
        using (var wrong = await c.Submit(confirm, confirm, new() { ["Version"] = "0.1.1", ["Code"] = "000000" }))
        {
            var html = await wrong.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "El código no es correcto.");
            StringAssert.Contains(html, "aria-describedby=\"code-help code-error\"", "El motivo va junto al campo, no solo en el aviso de arriba.");
            StringAssert.Contains(html, "<span id=\"code-error\"");
        }

        Assert.AreEqual(0, await h.Query(db => db.Set<UpdateRequest>().CountAsync()));

        long id;
        using (var ok = await c.Submit(confirm, confirm, new() { ["Version"] = "0.1.1", ["Code"] = UiHarness.Totp(key) }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, ok.StatusCode);
            id = await h.Query(db => db.Set<UpdateRequest>().Select(r => r.Id).SingleAsync());
            Assert.AreEqual($"/admin/actualizaciones/{id}", Location(ok));
        }

        var request = await h.Query(db => db.Set<UpdateRequest>().SingleAsync());
        Assert.AreEqual(("0.1.1", "web", "pending", $"staff:{staff.Id}"), (request.Version, request.Origin, request.Status, request.RequestedBy));
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == UpdateService.Audited && a.Actor == $"staff:{staff.Id}")));

        var progress = await c.Html($"/admin/actualizaciones/{id}");
        StringAssert.Contains(progress, $"Pedido N.º {id}");
        StringAssert.Contains(progress, "El actualizador todavía no tomó el pedido.");
        StringAssert.Contains(progress, "hx-trigger=\"every 5s\"");

        // Un pedido a la vez: el segundo se rechaza con el motivo, sin romper la página.
        using var again = await c.Submit(confirm, confirm, new() { ["Version"] = "0.1.1", ["Code"] = UiHarness.Totp(key) });
        StringAssert.Contains(await again.Content.ReadAsStringAsync(), "Ya hay una actualización pedida o en curso.");
        c.Dispose();
    }
}
