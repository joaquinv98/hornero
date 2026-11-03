using System.Net;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Las pantallas del catálogo en el panel del staff: ver, crear y editar, con los mismos casos de uso que la API.</summary>
[TestClass]
[DoNotParallelize]
public sealed class CatalogUiTests
{
    private static string Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString ?? string.Empty;

    [TestMethod]
    public async Task Producto_SeCreaConSusPrecios_YUnPrecioNuevoAbreOtraVigencia()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("catalogo@example.com", StaffPermissions.CatalogRead, StaffPermissions.CatalogWrite);
        using var c = await UiTests.StaffSession(h, "catalogo@example.com");

        var catalog = await c.Html("/admin/catalogo");
        StringAssert.Contains(catalog, "VPS 2 GB");
        StringAssert.Contains(catalog, "12.100,00 ARS por mes");

        // Una fila con importe de punto decimal, otra en blanco que no cuenta.
        long productId;
        using (var created = await c.Submit("/admin/catalogo/productos/nuevo", "/admin/catalogo/productos/nuevo", new()
        {
            ["Input.Name"] = "Hosting Web",
            ["Input.Kind"] = "Manual",
            ["Input.TaxCategory"] = "digital",
            ["Input.Visible"] = "true",
            ["Input.Stock"] = "3",
            ["Input.Description"] = "Hosting con correo",
            ["Input.Activation"] = "OnOrder",
            ["Input.Prices[0].Cycle"] = "P1M",
            ["Input.Prices[0].Currency"] = "ARS",
            ["Input.Prices[0].Amount"] = "18900.50",
            ["Input.Prices[0].SetupFee"] = "1000",
            ["Input.Prices[1].Cycle"] = string.Empty,
        }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, created.StatusCode);
            StringAssert.StartsWith(Location(created), "/admin/catalogo/productos/");
            productId = long.Parse(Location(created).Split('/')[^1].Split('?')[0], System.Globalization.CultureInfo.InvariantCulture);
        }

        var page = await c.Html($"/admin/catalogo/productos/{productId}?guardado=true");
        StringAssert.Contains(page, "quedó guardado");
        StringAssert.Contains(page, "value=\"18900.5\"");
        var product = await h.Query(db => db.Products.SingleAsync(p => p.Id == productId));
        Assert.AreEqual(("Hosting Web", 3, "Hosting con correo", ProductActivation.OnOrder), (product.Name, product.Stock!.Value, product.Description!, product.Activation));
        StringAssert.Contains(page, "<option value=\"OnOrder\" selected=\"selected\">Al pedir</option>");
        StringAssert.Contains(await c.Html("/admin/catalogo"), "<td>Al pedir</td>");
        var price = await h.Query(db => db.PlanPrices.SingleAsync(p => db.Plans.Any(x => x.Id == p.PlanId && x.ProductId == productId)));
        Assert.AreEqual((18900.5m, 1000m), (price.Amount, price.SetupFee));

        // Un ciclo que no existe no guarda nada y dice qué pasó, con lo cargado a la vista.
        using (var bad = await c.Submit($"/admin/catalogo/productos/{productId}", $"/admin/catalogo/productos/{productId}", new()
        {
            ["Input.Name"] = "Hosting Web",
            ["Input.Prices[0].Cycle"] = "P1Q",
            ["Input.Prices[0].Currency"] = "ARS",
            ["Input.Prices[0].Amount"] = "1",
        }))
        {
            Assert.AreEqual(HttpStatusCode.OK, bad.StatusCode);
            var html = await bad.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "role=\"alert\"");
            StringAssert.Contains(html, "Ciclo desconocido");
        }

        Assert.AreEqual(1, await h.Query(db => db.Plans.CountAsync(p => p.ProductId == productId)));
        Assert.IsTrue(await h.Query(db => db.Set<AuditEvent>().AnyAsync(a => a.Action == "catalog.product.saved" && a.Entity == $"product:{productId}")));

        // Duplicar abre la copia, oculta y con el nombre en el idioma del staff.
        using (var copied = await c.Submit($"/admin/catalogo/productos/{productId}", $"/admin/catalogo/productos/{productId}?handler=Copy", []))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, copied.StatusCode);
            var copyId = long.Parse(Location(copied).Split('/')[^1].Split('?')[0], System.Globalization.CultureInfo.InvariantCulture);
            var copy = await h.Query(db => db.Products.SingleAsync(p => p.Id == copyId));
            Assert.AreEqual(("Hosting Web (copia)", false), (copy.Name, copy.Visible));
        }
    }

    [TestMethod]
    public async Task Opciones_SeCarganConValoresYPrecios_YQuedanEnElProducto()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("catalogo@example.com", StaffPermissions.CatalogRead, StaffPermissions.CatalogWrite);
        using var c = await UiTests.StaffSession(h, "catalogo@example.com");
        var vps = await h.Query(db => db.Products.Where(p => p.Name == "VPS 2 GB").Select(p => p.Id).SingleAsync());

        using (var created = await c.Submit("/admin/catalogo/opciones/nuevo", "/admin/catalogo/opciones/nuevo", new()
        {
            ["Input.Name"] = "VPS",
            ["Input.ProductIds"] = vps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Input.Options[0].Name"] = "RAM",
            ["Input.Options[0].Kind"] = "Select",
            ["Input.Options[0].ModuleKey"] = "memory_mb",
            ["Input.Options[0].Values[0].Name"] = "2 GB",
            ["Input.Options[0].Values[0].ModuleValue"] = "2048",
            ["Input.Options[0].Values[0].Prices[0].Cycle"] = "P1M",
            ["Input.Options[0].Values[0].Prices[0].Currency"] = "ARS",
            ["Input.Options[0].Values[0].Prices[0].Amount"] = "0",
            ["Input.Options[0].Values[1].Name"] = "4 GB",
            ["Input.Options[0].Values[1].ModuleValue"] = "4096",
            ["Input.Options[0].Values[1].SortOrder"] = "1",
            ["Input.Options[0].Values[1].Prices[0].Cycle"] = "P1M",
            ["Input.Options[0].Values[1].Prices[0].Currency"] = "ARS",
            ["Input.Options[0].Values[1].Prices[0].Amount"] = "6050",
            ["Input.Options[0].Values[1].Prices[0].SetupFee"] = "500.25",
            ["Input.Options[1].Name"] = string.Empty,
        }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, created.StatusCode, await created.Content.ReadAsStringAsync());
        }

        var group = await h.Query(db => db.OptionGroups.SingleAsync());
        var ram = await h.Query(db => db.Options.SingleAsync());
        Assert.AreEqual(("RAM", OptionKind.Select, "memory_mb"), (ram.Name, ram.Kind, ram.ModuleKey));
        var big = await h.Query(db => db.OptionValues.SingleAsync(v => v.Name == "4 GB"));
        var bigPrice = await h.Query(db => db.OptionPrices.SingleAsync(p => p.ValueId == big.Id));
        Assert.AreEqual((6050m, 500.25m, Currency.Ars), (bigPrice.Amount, bigPrice.SetupFee, bigPrice.Currency));
        Assert.IsTrue(await h.Query(db => db.ProductOptionGroups.AnyAsync(l => l.ProductId == vps && l.OptionGroupId == group.Id)));

        // La pantalla del grupo muestra lo guardado, y el catálogo dice que el producto tiene opciones.
        var page = await c.Html($"/admin/catalogo/opciones/{group.Id}");
        StringAssert.Contains(page, "value=\"memory_mb\"");
        StringAssert.Contains(page, "value=\"500.25\"");
        StringAssert.Contains(await c.Html("/admin/catalogo"), "con opciones (1)");
    }

    [TestMethod]
    public async Task Cupon_SeCrea_YSoloLectura_NoGuardaNiMuestraElBoton()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("catalogo@example.com", StaffPermissions.CatalogRead, StaffPermissions.CatalogWrite);
        await h.Staff("lector@example.com", StaffPermissions.CatalogRead);
        await h.Staff("cuentas@example.com", StaffPermissions.AccountsRead);

        using (var c = await UiTests.StaffSession(h, "catalogo@example.com"))
        {
            using var created = await c.Submit("/admin/catalogo/cupones/nuevo", "/admin/catalogo/cupones/nuevo", new()
            {
                ["Input.Code"] = "VERANO25",
                ["Input.Kind"] = "Percent",
                ["Input.Value"] = "25",
                ["Input.Recurring"] = "true",
                ["Input.RecurringCycles"] = "3",
                ["Input.Cycles"] = "P1M",
                ["Input.ExpiresOn"] = "2027-03-31",
                ["Input.MaxUses"] = "100",
                ["Input.OncePerAccount"] = "true",
                ["Input.Active"] = "true",
            });
            Assert.AreEqual(HttpStatusCode.Redirect, created.StatusCode, await created.Content.ReadAsStringAsync());
        }

        var coupon = await h.Query(db => db.Coupons.SingleAsync());
        Assert.AreEqual(("VERANO25", DiscountKind.Percent, 25m, 3, new DateOnly(2027, 3, 31), 100, true), (coupon.Code, coupon.Kind, coupon.Value, coupon.RecurringCycles!.Value, coupon.ExpiresOn!.Value, coupon.MaxUses!.Value, coupon.OncePerAccount));
        CollectionAssert.AreEqual(new[] { "P1M" }, coupon.Cycles);

        using (var reader = await UiTests.StaffSession(h, "lector@example.com"))
        {
            var page = await reader.Html($"/admin/catalogo/cupones/{coupon.Id}");
            StringAssert.Contains(page, "VERANO25");
            Assert.IsFalse(page.Contains("Guardar el cupón", StringComparison.Ordinal));
            using var attempt = await reader.Submit($"/admin/catalogo/cupones/{coupon.Id}", $"/admin/catalogo/cupones/{coupon.Id}", new() { ["Input.Code"] = "OTRO", ["Input.Value"] = "1" });
            Assert.AreNotEqual(HttpStatusCode.OK, attempt.StatusCode);
            Assert.AreEqual("VERANO25", await h.Query(db => db.Coupons.Select(x => x.Code).SingleAsync()));
        }

        using var other = await UiTests.StaffSession(h, "cuentas@example.com");
        using var denied = await other.Get("/admin/catalogo");
        Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
        StringAssert.Contains(Location(denied), "/estado/403");
        Assert.IsFalse((await other.Html("/admin")).Contains("/admin/catalogo", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Paquete_SeArmaConSusPlanes_YCuelgaDeConfiguracionEnElMenu()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("catalogo@example.com", StaffPermissions.CatalogRead, StaffPermissions.CatalogWrite);
        using var c = await UiTests.StaffSession(h, "catalogo@example.com");
        var plan = await h.Query(db => db.Plans.Where(p => db.Products.Any(x => x.Id == p.ProductId && x.Name == "VPS 2 GB")).Select(p => p.Id).SingleAsync());

        // El menú clásico: Configuración con Productos y servicios y Promociones y cupones.
        var home = await c.Html("/admin/catalogo");
        StringAssert.Contains(home, "<summary>Configuración</summary>");
        StringAssert.Contains(home, "href=\"/admin/catalogo/cupones\"");
        StringAssert.Contains(await c.Html("/admin/catalogo/cupones"), "Nuevo cupón");

        var form = await c.Html("/admin/catalogo/paquetes/nuevo");
        StringAssert.Contains(form, "VPS 2 GB · Mensual");
        long bundleId;
        using (var created = await c.Submit("/admin/catalogo/paquetes/nuevo", "/admin/catalogo/paquetes/nuevo", new()
        {
            ["Input.Name"] = "VPS doble",
            ["Input.DiscountPercent"] = "12.5",
            ["Input.DiscountCycles"] = "6",
            ["Input.Active"] = "true",
            ["Input.Items[0].PlanId"] = plan.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Input.Items[0].Quantity"] = "2",
            ["Input.Items[1].PlanId"] = string.Empty,
        }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, created.StatusCode, await created.Content.ReadAsStringAsync());
            bundleId = long.Parse(Location(created).Split('/')[^1].Split('?')[0], System.Globalization.CultureInfo.InvariantCulture);
        }

        var bundle = await h.Query(db => db.Bundles.SingleAsync());
        Assert.AreEqual(("VPS doble", 12.5m, 6), (bundle.Name, bundle.DiscountPercent, bundle.DiscountCycles!.Value));
        CollectionAssert.AreEqual(new[] { plan, plan }, bundle.PlanIds);
        var page = await c.Html($"/admin/catalogo/paquetes/{bundleId}?guardado=true");
        StringAssert.Contains(page, "value=\"12.5\"");
        StringAssert.Contains(page, "21.175,00 ARS");
        var catalog = await c.Html("/admin/catalogo");
        StringAssert.Contains(catalog, "VPS doble");
        StringAssert.Contains(catalog, "VPS 2 GB · Mensual x2");

        // Sin planes no se guarda, y dice por qué.
        using var bad = await c.Submit($"/admin/catalogo/paquetes/{bundleId}", $"/admin/catalogo/paquetes/{bundleId}", new() { ["Input.Name"] = "VPS doble", ["Input.Items[0].PlanId"] = string.Empty });
        StringAssert.Contains(await bad.Content.ReadAsStringAsync(), "al menos un plan");
        Assert.AreEqual(2, (await h.Query(db => db.Bundles.SingleAsync())).PlanIds.Length);
    }

    [TestMethod]
    public async Task CambioDeOpciones_DesdeElPanel_CalculaConfirmaYCancela()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("ventas@example.com", StaffPermissions.AccountsRead, StaffPermissions.ServicesChange);
        await h.Staff("soporte@example.com", StaffPermissions.AccountsRead);
        var serviceId = await h.Query(async db =>
        {
            var product = await db.Products.SingleAsync(p => p.Name == "VPS 2 GB");
            await CatalogSeed.VpsOptions(db, product.Id);
            var service = await db.Services.SingleAsync(s => s.AccountId == h.AccountA);
            service.MoveTo(ServiceStatus.Provisioning, DateTimeOffset.UtcNow);
            service.MoveTo(ServiceStatus.Active, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            return service.Id;
        });
        var productId = await h.Query(db => db.Services.Where(s => s.Id == serviceId).Select(s => s.ProductId).SingleAsync());
        var page = $"/admin/servicios/{serviceId}/cambio";
        Dictionary<string, string> Form() => new()
        {
            ["Input.ProductId"] = productId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Input.Cycle"] = "P1M",
            ["Input.Prorate"] = "true",
            ["Input.Choices[0].OptionId"] = CatalogSeed.Ram.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Input.Choices[0].ValueId"] = CatalogSeed.Ram2.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Input.Choices[1].OptionId"] = CatalogSeed.Ips.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Input.Choices[1].Quantity"] = "0",
            ["Input.Choices[2].OptionId"] = CatalogSeed.Backups.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Input.Choices[2].Quantity"] = "0",
        };

        using var c = await UiTests.StaffSession(h, "ventas@example.com");
        StringAssert.Contains(await c.Html($"/admin/cuentas/{h.AccountA}"), page);
        StringAssert.Contains(await c.Html(page), "RAM");

        // Calcular no cambia nada: dice que se factura la diferencia del período.
        using (var preview = await c.Submit(page, page + "?handler=Preview", Form()))
        {
            var html = await preview.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode, html);
            StringAssert.Contains(html, "Se emite una factura por 5.000,00 ARS");
            StringAssert.Contains(html, "handler=Confirm");
        }

        Assert.AreEqual(0, await h.Query(db => db.ServiceChanges.CountAsync()));
        using (var confirm = await c.Submit(page, page + "?handler=Confirm", Form()))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, confirm.StatusCode, await confirm.Content.ReadAsStringAsync());
            StringAssert.Contains(Location(confirm), "hecho=");
        }

        var change = await h.Query(db => db.ServiceChanges.SingleAsync());
        Assert.AreEqual((ServiceChangeStatus.Pending, 5000m), (change.Status, change.Amount));
        Assert.IsNotNull(change.DocumentId);
        var pending = await c.Html(page);
        StringAssert.Contains(pending, "Cambio esperando el pago");

        // Cancelar anula la factura del cambio y el servicio queda como estaba.
        using (var cancel = await c.Submit(page, $"{page}?handler=Cancel&change={change.Id}", []))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, cancel.StatusCode);
        }

        Assert.AreEqual(ServiceChangeStatus.Cancelled, await h.Query(db => db.ServiceChanges.Select(x => x.Status).SingleAsync()));
        Assert.AreEqual(BillingOss.Domain.Billing.InvoiceStatus.Cancelled, await h.Query(db => db.Documents.Where(d => d.Id == change.DocumentId).Select(d => d.Status).SingleAsync()));
        Assert.AreEqual(12100m, await h.Query(db => db.Services.Where(s => s.Id == serviceId).Select(s => s.UnitPrice).SingleAsync()));

        // Sin el permiso no se ve el enlace ni se abre la página.
        using var other = await UiTests.StaffSession(h, "soporte@example.com");
        Assert.IsFalse((await other.Html($"/admin/cuentas/{h.AccountA}")).Contains(page, StringComparison.Ordinal));
        using var denied = await other.Get(page);
        StringAssert.Contains(Location(denied), "/estado/403");
    }

    [TestMethod]
    public async Task CambioDesdeElPortal_LasOpcionesVisibles_YLaFacturaParaPagar()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        await h.PortalUser("tecnico@example.com", h.AccountA, owner: false, capabilities: [AccountCapabilities.Services]);
        var serviceId = await h.Query(async db =>
        {
            var product = await db.Products.SingleAsync(p => p.Name == "VPS 2 GB");
            await CatalogSeed.VpsOptions(db, product.Id);
            product.OptionChanges = true;
            (await db.Options.SingleAsync(o => o.Id == CatalogSeed.Backups)).Hidden = true;
            var service = await db.Services.SingleAsync(s => s.AccountId == h.AccountA);
            service.MoveTo(ServiceStatus.Provisioning, DateTimeOffset.UtcNow);
            service.MoveTo(ServiceStatus.Active, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            return service.Id;
        });
        var page = $"/servicios/{serviceId}/cambio";
        var ram2 = CatalogSeed.Ram2.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Sin ver las facturas de la cuenta no hay cambio: ni el enlace ni la página.
        using (var tecnico = h.NewClient())
        {
            (await tecnico.Login("tecnico@example.com")).Dispose();
            Assert.IsFalse((await tecnico.Html("/servicios")).Contains(page, StringComparison.Ordinal));
            using var hidden = await tecnico.Get(page);
            Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode);
        }

        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();
        StringAssert.Contains(await c.Html("/servicios"), page);
        var form = await c.Html(page);
        StringAssert.Contains(form, "RAM");
        StringAssert.Contains(form, "5.000,00 ARS)</option>");
        Assert.IsFalse(form.Contains("Backups", StringComparison.Ordinal), "una opción oculta no se le ofrece al cliente");

        using (var preview = await c.Submit(page, page + "?handler=Preview", new() { ["Input.ProductId"] = "0", ["Input.Choices[0].OptionId"] = CatalogSeed.Ram.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Input.Choices[0].ValueId"] = ram2 }))
        {
            var html = await preview.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode, html);
            StringAssert.Contains(html, "Se emite una factura por 5.000,00 ARS");
        }

        using (var confirm = await c.Submit(page, page + "?handler=Confirm", new() { ["Input.Choices[0].OptionId"] = CatalogSeed.Ram.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Input.Choices[0].ValueId"] = ram2 }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, confirm.StatusCode, await confirm.Content.ReadAsStringAsync());
        }

        var change = await h.Query(db => db.ServiceChanges.SingleAsync());
        Assert.AreEqual((ServiceChangeStatus.Pending, 5000m), (change.Status, change.Amount));
        StringAssert.Contains(await c.Html($"{page}?hecho={change.Id}"), $"href=\"/facturas/{change.DocumentId}\"");
        Assert.IsTrue(await h.Query(db => db.Set<AuditEvent>().AnyAsync(a => a.Action == "service.change.requested" && a.Entity == $"service:{serviceId}")));
    }

    [TestMethod]
    public async Task Contratar_ConOpcionesComplementoYCupon_CalculaConfirmaUnaSolaVez_YDejaLaFactura()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        await h.PortalUser("tecnico@example.com", h.AccountA, owner: false, capabilities: [AccountCapabilities.Services]);
        var (productId, planId, hiddenId, backupPlan, gameId) = await h.Query(async db =>
        {
            var product = await db.Products.SingleAsync(p => p.Name == "VPS 2 GB");
            await CatalogSeed.VpsOptions(db, product.Id);
            (await db.Options.SingleAsync(o => o.Id == CatalogSeed.Backups)).Hidden = true;
            var hidden = new Product { Name = "Producto oculto", Kind = ProductKind.Manual, Visible = false };
            db.Products.Add(hidden);
            db.Coupons.Add(new Coupon { Code = "DIEZ", Kind = DiscountKind.Percent, Value = 10m });
            await db.SaveChangesAsync();
            var (_, backup) = await CatalogSeed.Product(db, "Backup diario", 2000m, p => { p.Addon = true; p.ParentProductIds = [product.Id]; });

            // Como los game servers importados: el plan en cero y el precio en las opciones, con un mínimo de jugadores.
            var (game, _) = await CatalogSeed.Product(db, "Servidor de juegos", 0m);
            var players = new OptionGroup { Name = "Juegos" };
            players.SetImportedId(901);
            var option = new Option { GroupId = 901, Name = "Jugadores", Kind = OptionKind.Quantity, MinQuantity = 10, MaxQuantity = 100 };
            option.SetImportedId(902);
            var value = new OptionValue { OptionId = 902, Name = "Jugador" };
            value.SetImportedId(903);
            db.AddRange(players, option, value, new ProductOptionGroup(game, 901), new OptionPrice(903, Currency.Ars, BillingCycle.Monthly, 300m, 0m));
            await db.SaveChangesAsync();
            return (product.Id, await db.Plans.Where(p => p.ProductId == product.Id).Select(p => p.Id).SingleAsync(), hidden.Id, backup, game);
        });
        string Text(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var page = $"/contratar/{productId}";

        // Sin ver las facturas de la cuenta no se contrata: la tienda no ofrece nada y el pedido no existe.
        using (var tecnico = h.NewClient())
        {
            (await tecnico.Login("tecnico@example.com")).Dispose();
            StringAssert.Contains(await tecnico.Html("/contratar"), "No tenés una cuenta activa");
            using var denied = await tecnico.Get(page);
            Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode);
        }

        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();
        StringAssert.Contains(await c.Html("/servicios"), "href=\"/contratar\"");
        var shop = await c.Html("/contratar");
        StringAssert.Contains(shop, "12.100,00 ARS por mes");
        StringAssert.Contains(shop, "3.000,00 ARS por mes", "un producto que cobra por sus opciones sale con lo menos que cuesta, no en cero");
        StringAssert.Contains(await c.Html($"/contratar/{gameId}"), "Mensual · desde 3.000,00 ARS");
        StringAssert.Contains(shop, $"href=\"{page}\"");
        Assert.IsFalse(shop.Contains("Producto oculto", StringComparison.Ordinal));
        using (var hidden = await c.Get($"/contratar/{hiddenId}"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode);
        }

        var form = await c.Html(page);
        StringAssert.Contains(form, "5.000,00 ARS)</option>");
        StringAssert.Contains(form, $"name=\"Input.AddonPlanIds\" value=\"{Text(backupPlan)}\"");
        Assert.IsFalse(form.Contains("Backups", StringComparison.Ordinal), "una opción oculta no se le ofrece al cliente");

        var fields = new Dictionary<string, string>
        {
            ["Input.AccountId"] = Text(h.AccountA),
            ["Input.PlanId"] = Text(planId),
            ["Input.Label"] = "vps.example.com",
            ["Input.Coupon"] = "diez",
            ["Input.Reference"] = Guid.NewGuid().ToString("N"),
            ["Input.Choices[0].OptionId"] = Text(CatalogSeed.Ram),
            ["Input.Choices[0].ValueId"] = Text(CatalogSeed.Ram2),
            ["Input.Choices[1].OptionId"] = Text(CatalogSeed.Ips),
            ["Input.Choices[1].Quantity"] = "1",
            ["Input.AddonPlanIds"] = Text(backupPlan),
        };

        // 12.100 del plan, 5.000 de la RAM, 2.500 de una IP y 2.000 del backup con el 10 % del cupón, más las altas de la RAM
        // y de la IP.
        string total;
        using (var quote = await c.Submit(page, page + "?handler=Quote", fields))
        {
            var html = await quote.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, quote.StatusCode, html);
            StringAssert.Contains(html, "Se emite una factura por 20.940,00 ARS");
            StringAssert.Contains(html, "Backup diario");
            total = System.Text.RegularExpressions.Regex.Match(html, "name=\"Input.Total\" value=\"([^\"]+)\"").Groups[1].Value;
            Assert.AreEqual(20940m, decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.AreEqual(2, await h.Query(db => db.Orders.CountAsync()), "calcular no pide nada");

        // Confirmar con un total distinto del calculado no pide: muestra el nuevo.
        fields["Input.Total"] = "1";
        using (var changed = await c.Submit(page, page + "?handler=Confirm", fields))
        {
            var html = await changed.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, html);
            StringAssert.Contains(html, "El pedido cambió");
        }

        Assert.AreEqual(2, await h.Query(db => db.Orders.CountAsync()));

        // El mismo formulario enviado dos veces es un solo pedido.
        fields["Input.Total"] = total;
        string invoicePage;
        using (var confirm = await c.Submit(page, page + "?handler=Confirm", fields))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, confirm.StatusCode, await confirm.Content.ReadAsStringAsync());
            invoicePage = Location(confirm);
            StringAssert.StartsWith(invoicePage, "/facturas/");
        }

        using (var again = await c.Submit(page, page + "?handler=Confirm", fields))
        {
            Assert.AreEqual(invoicePage, Location(again));
        }

        var order = await h.Query(db => db.Orders.OrderByDescending(o => o.Id).FirstAsync());
        var serviceIds = await h.Query(db => db.Orders.Where(o => o.Id == order.Id).SelectMany(o => o.Lines).Select(l => l.ServiceId!.Value).ToListAsync());
        var services = await h.Query(db => db.Services.Where(s => serviceIds.Contains(s.Id)).ToListAsync());
        var service = services.Single(s => s.ParentServiceId is null);
        Assert.AreEqual(3, await h.Query(db => db.Orders.CountAsync()));
        Assert.AreEqual(("vps.example.com", 19600m), (service.Label, service.UnitPrice));
        Assert.AreEqual(service.Id, services.Single(s => s.ParentServiceId is not null).ParentServiceId, "el backup cuelga del VPS del mismo pedido");
        Assert.IsNotNull(order.CouponId);
        Assert.AreEqual(20940m, await h.Query(db => db.Documents.Where(d => d.Id == order.InvoiceId).Select(d => d.Total).SingleAsync()));
        StringAssert.Contains(await c.Html(invoicePage), "20.940,00");
    }

    [TestMethod]
    public async Task AgregarPedido_DesdeElPanel_ProductoYOpcionOcultos_ConCupon_AceptadoSinElPago_YUnaSolaVez()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("ventas@example.com", StaffPermissions.AccountsRead, StaffPermissions.OrdersWrite);
        await h.Staff("soporte@example.com", StaffPermissions.AccountsRead);
        var (productId, planId) = await h.Query(async db =>
        {
            // Un abono que la tienda no muestra, con las opciones del VPS y los backups ocultos para el cliente.
            var (product, plan) = await CatalogSeed.Product(db, "Soporte mensual", 50000m, p => p.Visible = false);
            await CatalogSeed.VpsOptions(db, product);
            (await db.Options.SingleAsync(o => o.Id == CatalogSeed.Backups)).Hidden = true;
            db.Coupons.Add(new Coupon { Code = "DIEZ", Kind = DiscountKind.Percent, Value = 10m });
            await db.SaveChangesAsync();
            return (product, plan);
        });
        string Text(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var page = $"/admin/pedidos/nuevo?cuenta={Text(h.AccountA)}&producto={Text(productId)}";

        // Sin el permiso no se ve el enlace ni se abre la página.
        using (var other = await UiTests.StaffSession(h, "soporte@example.com"))
        {
            Assert.IsFalse((await other.Html($"/admin/cuentas/{h.AccountA}")).Contains("/admin/pedidos/nuevo", StringComparison.Ordinal));
            using var denied = await other.Get(page);
            StringAssert.Contains(Location(denied), "/estado/403");
        }

        using var c = await UiTests.StaffSession(h, "ventas@example.com");
        StringAssert.Contains(await c.Html($"/admin/cuentas/{h.AccountA}"), $"/admin/pedidos/nuevo?cuenta={Text(h.AccountA)}");
        StringAssert.Contains(await c.Html("/admin/pedidos/nuevo"), "Número de cuenta");
        StringAssert.Contains(await c.Html("/admin/pedidos/nuevo?cuenta=999999"), "No existe la cuenta 999999.");
        var form = await c.Html(page);
        StringAssert.Contains(form, "Soporte mensual (oculto)");
        StringAssert.Contains(form, "Backups", "el staff ve también las opciones ocultas");

        var fields = new Dictionary<string, string>
        {
            ["Input.AccountId"] = Text(h.AccountA),
            ["Input.ProductId"] = Text(productId),
            ["Input.Reference"] = Guid.NewGuid().ToString("N"),
            ["Input.PlanId"] = Text(planId),
            ["Input.Label"] = "cliente-a",
            ["Input.Coupon"] = "diez",
            ["Input.Accept"] = "true",
            ["Input.Choices[0].OptionId"] = Text(CatalogSeed.Ram),
            ["Input.Choices[0].ValueId"] = Text(CatalogSeed.Ram2),
            ["Input.Choices[1].OptionId"] = Text(CatalogSeed.Ips),
            ["Input.Choices[1].Quantity"] = "0",
            ["Input.Choices[2].OptionId"] = Text(CatalogSeed.Backups),
            ["Input.Choices[2].Quantity"] = "1",
        };

        // 50.000 del abono, 5.000 de la RAM y 1.500 de los backups con el 10 % del cupón, más el alta de la RAM.
        using (var quote = await c.Submit(page, "/admin/pedidos/nuevo?handler=Quote", fields))
        {
            var html = await quote.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, quote.StatusCode, html);
            StringAssert.Contains(html, "Se emite una factura por 51.850,00 ARS");
            fields["Input.Total"] = System.Text.RegularExpressions.Regex.Match(html, "name=\"Input.Total\" value=\"([^\"]+)\"").Groups[1].Value;
        }

        var before = await h.Query(db => db.Orders.CountAsync());
        string done;
        using (var confirm = await c.Submit(page, "/admin/pedidos/nuevo?handler=Confirm", fields))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, confirm.StatusCode, await confirm.Content.ReadAsStringAsync());
            done = Location(confirm);
            StringAssert.Contains(done, "hecho=");
        }

        // El mismo formulario otra vez, desde cualquier nodo, va al mismo pedido.
        using (var again = await c.Submit(page, "/admin/pedidos/nuevo?handler=Confirm", fields))
        {
            Assert.AreEqual(done, Location(again));
        }

        Assert.AreEqual(before + 1, await h.Query(db => db.Orders.CountAsync()));
        var order = await h.Query(db => db.Orders.Include(o => o.Lines).OrderByDescending(o => o.Id).FirstAsync());
        var serviceId = order.Lines.Single().ServiceId!.Value;
        var service = await h.Query(db => db.Services.SingleAsync(s => s.Id == serviceId));
        Assert.AreEqual((BillingOss.Domain.Orders.OrderStatus.Accepted, "cliente-a"), (order.Status, service.Label));
        Assert.AreNotEqual(ServiceStatus.Pending, service.Status, "aceptado, se aprovisiona sin esperar el pago");
        Assert.IsNotNull(order.CouponId);
        Assert.AreEqual(51850m, await h.Query(db => db.Documents.Where(d => d.Id == order.InvoiceId).Select(d => d.Total).SingleAsync()));
        Assert.AreEqual(1, await h.Query(db => db.ServiceOptions.Where(o => o.ServiceId == service.Id && o.OptionId == CatalogSeed.Backups).Select(o => o.Quantity).SingleAsync()));
        var notice = await c.Html(done);
        StringAssert.Contains(notice, $"El pedido {Text(order.Id)} quedó hecho.");
        StringAssert.Contains(notice, "Está aceptado");
        Assert.IsTrue(await h.Query(db => db.Set<AuditEvent>().AnyAsync(a => a.Action == "order.placed_by_staff" && a.Entity == $"order:{order.Id}" && EF.Functions.Like(a.Actor, "staff:%"))));
    }

    [TestMethod]
    public async Task ContratarUnPaquete_CadaLineaConSusOpciones_YElComplementoColgadoDeSuLinea()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        var bundleId = await h.Query(async db =>
        {
            var product = await db.Products.SingleAsync(p => p.Name == "VPS 2 GB");
            await CatalogSeed.VpsOptions(db, product.Id);
            var vps = await db.Plans.Where(p => p.ProductId == product.Id).Select(p => p.Id).SingleAsync();
            var (_, backup) = await CatalogSeed.Product(db, "Backup", 2000m, p => { p.Addon = true; p.ParentProductIds = [product.Id]; });
            var bundle = new Bundle { Name = "VPS con backup", PlanIds = [backup, vps], DiscountPercent = 20m, DiscountCycles = 1 };
            db.Bundles.Add(bundle);
            await db.SaveChangesAsync();
            return bundle.Id;
        });
        string Text(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var page = $"/contratar/paquete/{bundleId}";

        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();
        var shop = await c.Html("/contratar");
        StringAssert.Contains(shop, "VPS con backup");
        StringAssert.Contains(shop, $"href=\"{page}\"");
        var form = await c.Html(page);
        StringAssert.Contains(form, "Se agrega a VPS 2 GB");
        StringAssert.Contains(form, "name=\"Input.Lines[1].Choices[0].ValueId\"");
        StringAssert.Contains(form, "name=\"Input.Lines[0].Label\" value=\"\"");

        // El complemento va primero en el paquete y cuelga igual de la línea del VPS. La RAM de 2 GB, sin IP ni backups.
        var fields = new Dictionary<string, string>
        {
            ["Input.AccountId"] = Text(h.AccountA),
            ["Input.Reference"] = Guid.NewGuid().ToString("N"),
            ["Input.Lines[0].Label"] = string.Empty,
            ["Input.Lines[1].Label"] = "vps.example.com",
            ["Input.Lines[1].Choices[0].OptionId"] = Text(CatalogSeed.Ram),
            ["Input.Lines[1].Choices[0].ValueId"] = Text(CatalogSeed.Ram2),
            ["Input.Lines[1].Choices[1].OptionId"] = Text(CatalogSeed.Ips),
            ["Input.Lines[1].Choices[1].Quantity"] = "0",
            ["Input.Lines[1].Choices[2].OptionId"] = Text(CatalogSeed.Backups),
            ["Input.Lines[1].Choices[2].Quantity"] = "0",
        };
        string total;
        using (var quote = await c.Submit(page, page + "?handler=Quote", fields))
        {
            var html = await quote.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, quote.StatusCode, html);

            // Backup 2.000 y VPS 17.100 con el 20 %, más el alta de la RAM.
            StringAssert.Contains(html, "Se emite una factura por 16.280,00 ARS");
            total = System.Text.RegularExpressions.Regex.Match(html, "name=\"Input.Total\" value=\"([^\"]+)\"").Groups[1].Value;
        }

        fields["Input.Total"] = total;
        using (var confirm = await c.Submit(page, page + "?handler=Confirm", fields))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, confirm.StatusCode, await confirm.Content.ReadAsStringAsync());
            StringAssert.StartsWith(Location(confirm), "/facturas/");
        }

        var order = await h.Query(db => db.Orders.OrderByDescending(o => o.Id).FirstAsync());
        Assert.AreEqual(bundleId, order.BundleId);
        var serviceIds = await h.Query(db => db.Orders.Where(o => o.Id == order.Id).SelectMany(o => o.Lines).Select(l => l.ServiceId!.Value).ToListAsync());
        var services = await h.Query(db => db.Services.Where(s => serviceIds.Contains(s.Id)).ToListAsync());
        var vpsService = services.Single(s => s.Label == "vps.example.com");
        Assert.AreEqual(vpsService.Id, services.Single(s => s.Id != vpsService.Id).ParentServiceId);
        Assert.AreEqual(17100m, vpsService.UnitPrice);
    }
}
