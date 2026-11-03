using System.Globalization;
using System.Text.Json.Nodes;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Tests;

/// <summary>Opciones configurables, cupones, ciclos y prorrateo de cambios de plan (diseno/catalogo.md).</summary>
[TestClass]
public sealed class CatalogTests
{
    private static readonly BillingCycle Monthly = BillingCycle.Monthly;

    private static DateOnly D(string iso) => DateOnly.Parse(iso, CultureInfo.InvariantCulture);

    /// <summary>RAM en lista (1 y 2 GB, más uno oculto), IP extra por cantidad (0 a 4) y backups de sí o no.</summary>
    private static OptionCatalog Vps()
    {
        var ram = Option(1, "RAM", OptionKind.Select, "memory");
        var ips = Option(2, "IP extra", OptionKind.Quantity, "network.extra_ips", max: 4);
        var backups = Option(3, "Backups", OptionKind.YesNo, "backups");
        OptionValue[] values = [Value(11, 1, "1 GB", "1024", 0), Value(12, 1, "2 GB", "2048", 1), Value(13, 1, "64 GB", "65536", 2, hidden: true), Value(21, 2, "IP"), Value(31, 3, "Diario")];
        OptionPrice[] prices =
        [
            new(11, Currency.Ars, Monthly, 0m, 0m), new(12, Currency.Ars, Monthly, 5000m, 1000m), new(13, Currency.Ars, Monthly, 90000m, 0m),
            new(21, Currency.Ars, Monthly, 2500m, 500m), new(31, Currency.Ars, Monthly, 1500m, 0m),
            new(11, Currency.Ars, BillingCycle.Annually, 0m, 0m),
        ];
        return new OptionCatalog([ram, ips, backups], values, prices);
    }

    [TestMethod]
    public void SinElegir_TomaLosDefectos_YNoCobraNada()
    {
        var chosen = Vps().Resolve(null, Currency.Ars, Monthly);

        Assert.AreEqual(3, chosen.Count);
        Assert.AreEqual(11L, chosen[0].Value.Id);
        Assert.AreEqual(0, chosen[1].Quantity);
        Assert.AreEqual(0, chosen[2].Quantity);
        Assert.AreEqual(0m, chosen.Sum(c => c.Amount));
    }

    [TestMethod]
    public void Elegido_CobraElValor_LaCantidadPorUnidad_YElSiOno()
    {
        var chosen = Vps().Resolve([new(1, 12), new(2, Quantity: 3), new(3, Quantity: 1)], Currency.Ars, Monthly);

        Assert.AreEqual(5000m + (3 * 2500m) + 1500m, chosen.Sum(c => c.Amount));
        Assert.AreEqual(1000m + 500m, chosen.Sum(c => c.SetupFee));
        CollectionAssert.AreEqual(new[] { new OptionChoice(1, 12), new OptionChoice(2, 21, 3), new OptionChoice(3, 31, 1) }, chosen.Select(c => c.Choice).ToArray());
    }

    [TestMethod]
    public void ValorOculto_NoEsElDefecto_PeroSePuedePedir()
    {
        var chosen = Vps().Resolve([new(1, 13)], Currency.Ars, Monthly);

        Assert.AreEqual(90000m, chosen[0].Amount);
    }

    [TestMethod]
    [DataRow(9L, null, null, "no es de este producto")]
    [DataRow(1L, 21L, null, "no es de la opción RAM")]
    [DataRow(2L, null, 5, "va de 0 a 4")]
    [DataRow(3L, null, 2, "va con 0 o 1")]
    public void LoQueNoEsDelProducto_OFueraDeRango_SeRechaza(long option, long? value, int? quantity, string message)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => Vps().Resolve([new(option, value, quantity)], Currency.Ars, Monthly));

        StringAssert.Contains(ex.Message, message);
    }

    [TestMethod]
    public void SinPrecioEnElCiclo_NoSeOfrece_SalvoQueNoCobre()
    {
        // Anual solo tiene precio el valor de 1 GB: lo demás en cero no hace falta, el de 2 GB no se ofrece.
        Assert.AreEqual(0m, Vps().Resolve(null, Currency.Ars, BillingCycle.Annually).Sum(c => c.Amount));
        var ex = Assert.ThrowsExactly<ArgumentException>(() => Vps().Resolve([new(1, 12)], Currency.Ars, BillingCycle.Annually));
        StringAssert.Contains(ex.Message, "no se ofrece en ARS con el ciclo P1Y");

        // En el ciclo gratis las opciones no cobran ni necesitan precio.
        Assert.AreEqual(0m, Vps().Resolve([new(1, 12), new(2, Quantity: 2)], Currency.Usd, BillingCycle.Free).Sum(c => c.Amount));
    }

    [TestMethod]
    public void Desde_ElValorMasBaratoQueVeElCliente_YLoOcultoConSuDefecto()
    {
        // La RAM de 1 GB es la más barata que se ve (la de 64 GB está oculta), sin IP extra ni backups.
        Assert.AreEqual((0m, 0m), Vps().Cheapest(Currency.Ars, Monthly));

        // Con el mínimo de una IP y un tamaño oculto por defecto, cuentan los dos.
        var ram = Option(1, "RAM", OptionKind.Select, "memory");
        var ips = Option(2, "IP extra", OptionKind.Quantity, "network.extra_ips", max: 4);
        ips.MinQuantity = 1;
        var disk = Option(4, "Disco", OptionKind.Select, "disk");
        disk.Hidden = true;
        OptionValue[] values = [Value(12, 1, "2 GB", order: 1), Value(11, 1, "1 GB", order: 2), Value(21, 2, "IP"), Value(41, 4, "40 GB", order: 1), Value(42, 4, "20 GB", order: 2)];
        OptionPrice[] prices =
        [
            new(11, Currency.Ars, Monthly, 3000m, 100m), new(12, Currency.Ars, Monthly, 5000m, 0m), new(21, Currency.Ars, Monthly, 2500m, 500m),
            new(41, Currency.Ars, Monthly, 800m, 0m), new(42, Currency.Ars, Monthly, 400m, 0m),
        ];
        var catalog = new OptionCatalog([ram, ips, disk], values, prices);
        Assert.AreEqual((3000m + 2500m + 800m, 100m + 500m), catalog.Cheapest(Currency.Ars, Monthly));

        // Sin precio en esa moneda o ese ciclo no se puede pedir, salvo en el ciclo gratis, donde no cobran.
        Assert.IsNull(catalog.Cheapest(Currency.Usd, Monthly));
        Assert.IsNull(catalog.Cheapest(Currency.Ars, BillingCycle.Annually));
        Assert.AreEqual((0m, 0m), catalog.Cheapest(Currency.Usd, BillingCycle.Free));
    }

    [TestMethod]
    public void ValorSinNingunPrecio_NoCobraEnNingunCicloNiMoneda()
    {
        // Una versión que se elige sin costo: sin precios cargados se ofrece en todo y no cobra.
        var version = Option(4, "Versión", OptionKind.Select, "variables.VERSION");
        var catalog = new OptionCatalog([version], [Value(41, 4, "1.20"), Value(42, 4, "1.21")], []);
        var chosen = catalog.Resolve([new(4, 42)], Currency.Usd, BillingCycle.Annually).Single();
        Assert.AreEqual((42L, 0m, 0m), (chosen.Value.Id, chosen.Amount, chosen.SetupFee));
    }

    [TestMethod]
    public void ParametrosDelModulo_LasOpcionesPisanYAnidan()
    {
        var chosen = Vps().Resolve([new(1, 12), new(2, Quantity: 2), new(3, Quantity: 1)], Currency.Ars, Monthly);

        var merged = JsonNode.Parse(OptionCatalog.ModuleParams("""{"memory":512,"template":"debian","network":{"bridge":"vmbr0"}}""", chosen));

        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse("""{"memory":2048,"template":"debian","network":{"bridge":"vmbr0","extra_ips":2},"backups":true}"""), merged), merged!.ToJsonString());
    }

    [TestMethod]
    [DataRow("1.20", "\"1.20\"")]
    [DataRow("0135", "\"0135\"")]
    [DataRow("135", "135")]
    [DataRow("Forge", "\"Forge\"")]
    public void ParametrosDelModulo_SoloLosEnterosVanComoNumero(string moduleValue, string json)
    {
        var option = Option(1, "Versión", OptionKind.Select, "version");
        var value = Value(1, 1, "Versión", moduleValue);

        var merged = OptionCatalog.ModuleParams("{}", [new ChosenOption(option, value, 1, 0m, 0m)]);

        Assert.AreEqual($$"""{"version":{{json}}}""", merged);
    }

    [TestMethod]
    public void OpcionSinClave_SoloCobra()
    {
        var option = Option(1, "Prueba de 48 horas", OptionKind.YesNo, moduleKey: null);

        Assert.AreEqual("{}", OptionCatalog.ModuleParams("{}", [new ChosenOption(option, Value(1, 1, "Sí"), 1, 0m, 0m)]));
    }

    [TestMethod]
    public void Cupon_VigenciaUsosYCuentas()
    {
        var coupon = new Coupon { Code = "VERANO", Kind = DiscountKind.Percent, Value = 25m, StartsOn = D("2026-12-01"), ExpiresOn = D("2027-02-28"), MaxUses = 2, Uses = 1, NewAccountsOnly = true, OncePerAccount = true };

        StringAssert.Contains(coupon.Rejection(D("2026-11-30"), false, false), "no está vigente");
        StringAssert.Contains(coupon.Rejection(D("2027-03-01"), false, false), "no está vigente");
        Assert.IsNull(coupon.Rejection(D("2027-02-28"), false, false));
        StringAssert.Contains(coupon.Rejection(D("2027-01-10"), true, false), "cuentas nuevas");
        StringAssert.Contains(coupon.Rejection(D("2027-01-10"), false, true), "ya se usó en esta cuenta");
        coupon.Uses = 2;
        StringAssert.Contains(coupon.Rejection(D("2027-01-10"), false, false), "todas las veces");
    }

    [TestMethod]
    public void Cupon_ProductosCiclosYMoneda()
    {
        var coupon = new Coupon { Code = "VPS", Kind = DiscountKind.Fixed, Value = 10m, Currency = Currency.Usd, ProductIds = [7], Cycles = ["P1M"], Recurring = true, RecurringCycles = 3 };

        Assert.IsTrue(coupon.AppliesTo(7, Monthly, Currency.Usd));
        Assert.IsFalse(coupon.AppliesTo(8, Monthly, Currency.Usd));
        Assert.IsFalse(coupon.AppliesTo(7, BillingCycle.Annually, Currency.Usd));
        Assert.IsFalse(coupon.AppliesTo(7, Monthly, Currency.Ars));
        Assert.IsFalse(new Coupon { Code = "GRATIS", Value = 50m }.AppliesTo(7, BillingCycle.Free, Currency.Ars));
        Assert.AreEqual(3, coupon.CyclesFor(Monthly));
        Assert.AreEqual(1, coupon.CyclesFor(BillingCycle.OneTime));
        Assert.AreEqual(1, new Coupon { Code = "UNA", Value = 10m }.CyclesFor(Monthly));
    }

    [TestMethod]
    public void Ciclo_CodigoIsoYVuelta()
    {
        foreach (var cycle in new[] { Monthly, new BillingCycle(CycleUnit.Month, 3), BillingCycle.Annually, new BillingCycle(CycleUnit.Year, 2), BillingCycle.OneTime, BillingCycle.Free })
        {
            Assert.AreEqual(cycle, BillingCycle.Parse(cycle.Code));
        }

        Assert.AreEqual("P6M", new BillingCycle(CycleUnit.Month, 6).Code);
        Assert.AreEqual(BillingCycle.Annually, BillingCycle.Parse(" p1y "));
        Assert.ThrowsExactly<ArgumentException>(() => BillingCycle.Parse("P0M"));
        Assert.ThrowsExactly<ArgumentException>(() => BillingCycle.Parse("monthly"));
    }

    [TestMethod]
    public void PeriodoQueTermina_UsaElMismoAnclaje()
    {
        Assert.AreEqual(D("2027-02-28"), BillingSchedule.PeriodEndingAt(D("2027-03-31"), Monthly, 31).Start);
        Assert.AreEqual(D("2027-01-31"), BillingSchedule.PeriodEndingAt(D("2027-02-28"), Monthly, 31).Start);
        Assert.AreEqual(D("2026-11-15"), BillingSchedule.PeriodEndingAt(D("2027-11-15"), BillingCycle.Annually, 15).Start);
    }

    [TestMethod]
    public void Prorrateo_DelDiaHastaLoFacturado()
    {
        // Mitad de noviembre: quedan 15 de 30 días.
        Assert.AreEqual(500m, BillingSchedule.ProrateUntil(1000m, D("2026-11-16"), D("2026-12-01"), Monthly, 1));

        // Facturado por adelantado hasta enero: la mitad de noviembre y diciembre entero.
        Assert.AreEqual(1500m, BillingSchedule.ProrateUntil(1000m, D("2026-11-16"), D("2027-01-01"), Monthly, 1));

        // Negativo para una baja de plan, con redondeo comercial.
        Assert.AreEqual(-333.33m, BillingSchedule.ProrateUntil(-1000m, D("2026-11-21"), D("2026-12-01"), Monthly, 1));

        // El día del vencimiento no queda nada.
        Assert.AreEqual(0m, BillingSchedule.ProrateUntil(1000m, D("2026-12-01"), D("2026-12-01"), Monthly, 1));
    }

    [TestMethod]
    public void DescuentoFijo_PorUnidad_SinBajarDeCero_YTerminaConSusCiclos()
    {
        var service = new Service(1, 1, Monthly, Currency.Ars, 1000m, 2, DateTimeOffset.UnixEpoch) { DiscountAmount = 300m, DiscountCyclesLeft = 1 };
        service.StartBilling(D("2026-11-01"));

        Assert.AreEqual(700m, service.DiscountedUnitPrice);
        Assert.AreEqual(1400m, service.RecurringAmount);
        var november = service.TakeNextPeriod();
        Assert.AreEqual(0, service.DiscountCyclesLeft);
        Assert.AreEqual(1000m, service.DiscountedUnitPrice);
        Assert.AreEqual(2000m, service.RecurringAmount);

        // El período que lo consumió lo tuvo hasta su fin: un cambio de plan en noviembre prorratea con descuento.
        Assert.AreEqual(D("2026-12-01"), service.DiscountEndsOn);
        Assert.AreEqual(700m, service.DiscountedIn(november, 1000m));
        Assert.AreEqual(1000m, service.DiscountedIn(service.TakeNextPeriod(), 1000m));

        service.DiscountCyclesLeft = null;
        service.DiscountAmount = 5000m;
        Assert.AreEqual(0m, service.DiscountedUnitPrice);
    }

    [TestMethod]
    public void CambioDePlan_NoPasaDeRecurrenteAPagoUnico()
    {
        var service = new Service(1, 1, Monthly, Currency.Ars, 1000m, 1, DateTimeOffset.UnixEpoch);

        service.ChangePlan(2, BillingCycle.Annually, 10000m);

        Assert.AreEqual(BillingCycle.Annually, service.Cycle);
        Assert.AreEqual(2L, service.ProductId);
        Assert.ThrowsExactly<InvalidOperationException>(() => service.ChangePlan(2, BillingCycle.OneTime, 1m));
    }

    [TestMethod]
    public void Paquete_SusPlanesConSusUnidades_NiMasNiMenos()
    {
        var bundle = new Bundle { Name = "VPS con backup", PlanIds = [1, 2, 2], DiscountCycles = 3 };

        Assert.IsTrue(bundle.Matches([(2, 1), (1, 1), (2, 1)]));
        Assert.IsTrue(bundle.Matches([(1, 1), (2, 2)]));
        Assert.IsFalse(bundle.Matches([(1, 1), (2, 1)]));
        Assert.IsFalse(bundle.Matches([(1, 1), (2, 2), (3, 1)]));
        Assert.IsFalse(bundle.Matches([(1, 2), (2, 2)]));
        Assert.IsFalse(bundle.Matches([]));
        Assert.AreEqual(3, bundle.CyclesFor(Monthly));
        Assert.AreEqual(1, bundle.CyclesFor(BillingCycle.OneTime));
    }

    private static Option Option(long id, string name, OptionKind kind, string? moduleKey = null, int max = 0)
    {
        var option = new Option { GroupId = 1, Name = name, Kind = kind, ModuleKey = moduleKey, MaxQuantity = max };
        option.SetImportedId(id);
        return option;
    }

    private static OptionValue Value(long id, long optionId, string name, string? moduleValue = null, int order = 0, bool hidden = false)
    {
        var value = new OptionValue { OptionId = optionId, Name = name, ModuleValue = moduleValue, SortOrder = order, Hidden = hidden };
        value.SetImportedId(id);
        return value;
    }
}
