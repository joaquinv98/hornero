using BillingOss.Domain.Affiliates;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Tests.Affiliates;

[TestClass]
public sealed class AffiliateRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    private const long AffiliateId = 7;

    private const long ProductId = 40;

    private static CommissionRule Rule(long? product, long? affiliate, CommissionKind kind, decimal value = 0m, Currency? currency = null, bool recurring = true) =>
        new(product, affiliate, kind, value, currency, recurring);

    [TestMethod]
    public void Regla_GanaElPrimerNivelUsable()
    {
        var program = Rule(null, null, CommissionKind.Percent, 5m);
        var product = Rule(ProductId, null, CommissionKind.Percent, 8m);
        var own = Rule(null, AffiliateId, CommissionKind.Percent, 10m);
        var ownForProduct = Rule(ProductId, AffiliateId, CommissionKind.Percent, 12m);

        Assert.AreSame(program, CommissionRules.Resolve([program], AffiliateId, ProductId, Currency.Ars));
        Assert.AreSame(product, CommissionRules.Resolve([program, product], AffiliateId, ProductId, Currency.Ars));
        Assert.AreSame(own, CommissionRules.Resolve([program, product, own], AffiliateId, ProductId, Currency.Ars));
        Assert.AreSame(ownForProduct, CommissionRules.Resolve([program, product, own, ownForProduct], AffiliateId, ProductId, Currency.Ars));
        Assert.AreSame(program, CommissionRules.Resolve([program, product], AffiliateId, 41, Currency.Ars), "Otro producto sin regla propia: la del programa.");
        Assert.AreSame(own, CommissionRules.Resolve([program, product, own], AffiliateId, 41, Currency.Ars));
        Assert.IsNull(CommissionRules.Resolve([product], AffiliateId, 41, Currency.Ars));
    }

    [TestMethod]
    public void ProductoSinComision_GanaALaReglaDelAfiliado_PeroNoALaDelAfiliadoParaEseProducto()
    {
        var none = Rule(ProductId, null, CommissionKind.None);
        var own = Rule(null, AffiliateId, CommissionKind.Percent, 10m);
        var ownForProduct = Rule(ProductId, AffiliateId, CommissionKind.Fixed, 500m, Currency.Ars);

        Assert.AreEqual(CommissionKind.None, CommissionRules.Resolve([none, own], AffiliateId, ProductId, Currency.Ars)!.Kind);
        Assert.AreSame(ownForProduct, CommissionRules.Resolve([none, own, ownForProduct], AffiliateId, ProductId, Currency.Ars));
    }

    [TestMethod]
    public void MontoFijoEnOtraMoneda_NoEsUsable_YSigueElNivelSiguiente()
    {
        var program = Rule(null, null, CommissionKind.Percent, 5m);
        var ars = Rule(ProductId, null, CommissionKind.Fixed, 1000m, Currency.Ars);
        var usd = Rule(ProductId, null, CommissionKind.Fixed, 2m, Currency.Usd);

        Assert.AreSame(ars, CommissionRules.Resolve([program, ars, usd], AffiliateId, ProductId, Currency.Ars));
        Assert.AreSame(usd, CommissionRules.Resolve([program, ars, usd], AffiliateId, ProductId, Currency.Usd));
        Assert.AreSame(program, CommissionRules.Resolve([program, ars], AffiliateId, ProductId, Currency.Usd));
    }

    [TestMethod]
    public void Importe_PorcentajeSobreElNetoConRedondeoComercial_YFijoSoloEnSuMoneda()
    {
        // 5 % de 10.000,10 es 500,005: con la mitad hacia arriba queda 500,01.
        Assert.AreEqual(500.01m, Rule(null, null, CommissionKind.Percent, 5m).Amount(10000.10m, Currency.Ars));
        Assert.AreEqual(1000m, Rule(null, null, CommissionKind.Fixed, 1000m, Currency.Ars).Amount(1m, Currency.Ars));
        Assert.IsNull(Rule(null, null, CommissionKind.Fixed, 1000m, Currency.Ars).Amount(1m, Currency.Usd));
        Assert.IsNull(Rule(null, null, CommissionKind.None).Amount(10000m, Currency.Ars));
    }

    [TestMethod]
    public void Base_CuentaElServicioMenosMoraYRedondeo_YUnDescuentoLaBaja()
    {
        var bases = CommissionRules.Bases(
        [
            (1, LineKind.Renewal, 10000m),
            (1, LineKind.Manual, -2500m), // la promoción importada del servicio
            (1, LineKind.Setup, 1000m),
            (1, LineKind.LateFee, 800m),
            (1, LineKind.RoundingAdjustment, 0.01m),
            (2, LineKind.LateFee, 500m),
            (3, LineKind.Renewal, 3000m),
            (3, LineKind.Manual, -3000m),
        ]);

        Assert.AreEqual(8500m, bases[1]);
        Assert.IsFalse(bases.ContainsKey(2), "Solo mora: no hay base.");
        Assert.IsFalse(bases.ContainsKey(3), "Con el descuento entero no se pagó nada por el servicio.");
    }

    [TestMethod]
    public void Regla_ValidaPorcentajeYMoneda()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Rule(null, null, CommissionKind.Percent, 0m));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Rule(null, null, CommissionKind.Percent, 101m));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Rule(null, null, CommissionKind.Percent, 5m, Currency.Ars));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Rule(null, null, CommissionKind.Fixed, 10m));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Rule(null, null, CommissionKind.Fixed, 10.001m, Currency.Ars));
        var none = Rule(ProductId, null, CommissionKind.None, 5m, Currency.Ars);
        Assert.AreEqual(0m, none.Value);
        Assert.IsNull(none.Currency);
    }

    [TestMethod]
    public void UnaReglaNueva_ReemplazaLasDeSuNivelQueChocan()
    {
        var percent = Rule(ProductId, null, CommissionKind.Percent, 5m);
        var ars = Rule(ProductId, null, CommissionKind.Fixed, 1000m, Currency.Ars);
        var usd = Rule(ProductId, null, CommissionKind.Fixed, 2m, Currency.Usd);
        var other = Rule(41, null, CommissionKind.Percent, 5m);
        CommissionRule[] rules = [percent, ars, usd, other];

        CollectionAssert.AreEquivalent(new[] { percent, ars, usd }, CommissionRules.Replaced(rules, ProductId, null, null).ToList());
        CollectionAssert.AreEquivalent(new[] { percent, ars }, CommissionRules.Replaced(rules, ProductId, null, Currency.Ars).ToList());
    }

    [TestMethod]
    public void LoQueSeDebe_CeroSiNoEstaPaga_YProporcionalConUnaNotaParcial()
    {
        Assert.AreEqual(500m, AffiliateCommission.Target(500m, total: 12100m, credited: 0m, pending: 0m));
        Assert.AreEqual(0m, AffiliateCommission.Target(500m, total: 12100m, credited: 0m, pending: 2000m));
        Assert.AreEqual(0m, AffiliateCommission.Target(500m, total: 12100m, credited: 12100m, pending: 0m));
        // Una nota de 2.000 sobre 12.100 deja 10.100: 500 por 10.100 dividido 12.100 es 417,355..., que queda 417,36.
        Assert.AreEqual(417.36m, AffiliateCommission.Target(500m, total: 12100m, credited: 2000m, pending: 0m));
    }

    [TestMethod]
    public void Comision_SeRevierteEnPartes_NuncaSube_YLiberaLoQueQueda()
    {
        var c = Commission(500m);

        Assert.AreEqual(82.64m, c.ReduceTo(417.36m, Now));
        Assert.AreEqual(0m, c.ReduceTo(500m, Now), "Lo revertido no vuelve.");
        Assert.AreEqual(CommissionStatus.Held, c.Status);
        Assert.AreEqual(417.36m, c.Release(Now));
        Assert.AreEqual(CommissionStatus.Released, c.Status);
        Assert.AreEqual(417.36m, c.ReduceTo(0m, Now));
        Assert.AreEqual(CommissionStatus.Reversed, c.Status);
        Assert.AreEqual(0m, c.ReduceTo(0m, Now));
    }

    [TestMethod]
    public void Comision_SinNadaParaLiberar_QuedaRevertida()
    {
        var c = Commission(500m);
        c.ReduceTo(0m, Now);

        Assert.AreEqual(CommissionStatus.Reversed, c.Status);
        Assert.ThrowsExactly<InvalidOperationException>(() => c.Release(Now));
    }

    [TestMethod]
    public void Atribucion_GanaElPrimerClicDentroDeLaVentana()
    {
        var a = new AffiliateAttribution(1, 7, Now, AttributionSource.Link, Now);

        Assert.IsFalse(a.TakesClick(8, Now.AddDays(10), 60), "Otro afiliado dentro de la ventana no la pisa.");
        Assert.IsTrue(a.TakesClick(8, Now.AddDays(61), 60), "Vencida, la toma el nuevo.");
        Assert.IsTrue(a.TakesClick(7, Now.AddDays(10), 60), "El mismo afiliado renueva la ventana.");
        Assert.IsTrue(a.Covers(Now.AddDays(60), 60));
        Assert.IsFalse(a.Covers(Now.AddDays(60).AddSeconds(1), 60));
        Assert.IsFalse(a.Covers(Now.AddSeconds(-1), 60), "Un pedido anterior al clic no es referido.");
    }

    [TestMethod]
    public void Afiliado_ConAprobacionQuedaPendiente_YSoloValenLasTransicionesDelDiseno()
    {
        var pending = new Affiliate(1, Affiliate.NewCode(), requiresApproval: true, Now);
        var active = new Affiliate(2, Affiliate.NewCode(), requiresApproval: false, Now);

        Assert.AreEqual(AffiliateStatus.Pending, pending.Status);
        Assert.AreEqual(AffiliateStatus.Active, active.Status);
        Assert.ThrowsExactly<InvalidOperationException>(() => pending.MoveTo(AffiliateStatus.Suspended, Now));
        pending.MoveTo(AffiliateStatus.Rejected, Now);
        pending.MoveTo(AffiliateStatus.Active, Now);
        active.MoveTo(AffiliateStatus.Suspended, Now);
        Assert.ThrowsExactly<InvalidOperationException>(() => active.MoveTo(AffiliateStatus.Pending, Now));
        StringAssert.Matches(Affiliate.NewCode(), new System.Text.RegularExpressions.Regex("^[a-hjkmnp-z2-9]{8}$"));
    }

    [TestMethod]
    public void Retiro_SePagaOSeRechazaUnaSolaVez()
    {
        var paid = new AffiliatePayout(1, PayoutMethod.Withdrawal, Currency.Ars, 5000m, "CBU 1", Now);
        var rejected = new AffiliatePayout(1, PayoutMethod.Withdrawal, Currency.Ars, 5000m, null, Now);

        Assert.AreEqual(PayoutStatus.Requested, paid.Status);
        Assert.IsTrue(paid.MarkPaid("TR-1", Now));
        Assert.IsFalse(paid.MarkPaid("TR-2", Now));
        Assert.AreEqual("TR-1", paid.Reference);
        Assert.ThrowsExactly<InvalidOperationException>(() => paid.Reject("no", Now));
        Assert.IsTrue(rejected.Reject("Datos incompletos", Now));
        Assert.IsFalse(rejected.Reject("otra vez", Now));
        Assert.AreEqual(PayoutStatus.Paid, new AffiliatePayout(1, PayoutMethod.Credit, Currency.Ars, 1m, null, Now).Status);
    }

    [TestMethod]
    public void Programa_ValidaDiasYGuardaMinimosPorMoneda()
    {
        var program = new AffiliateProgram();
        program.SetDays(90, 0);
        program.SetMinimumPayouts(new Dictionary<string, decimal> { ["USD"] = 5m, ["ARS"] = 0m });

        Assert.AreEqual(5m, program.MinimumPayout(Currency.Usd));
        Assert.AreEqual(0m, program.MinimumPayout(Currency.Ars));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => program.SetDays(0, 10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => program.SetDays(10, -1));
    }

    [TestMethod]
    public void Programa_BonoPorMoneda_CasillaDeAvisos_YMaterialConElEnlace()
    {
        var program = new AffiliateProgram();
        program.SetSignupBonuses(new Dictionary<string, decimal> { ["ARS"] = 1500m });
        Assert.AreEqual(1500m, program.SignupBonus(Currency.Ars));
        Assert.AreEqual(0m, program.SignupBonus(Currency.Usd));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => program.SetSignupBonuses(new Dictionary<string, decimal> { ["ARS"] = -1m }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => program.SetSignupBonuses(new Dictionary<string, decimal> { ["ARS"] = 1.005m }));

        program.SetPayoutNotifyEmail(" facturacion@example.com ");
        Assert.AreEqual("facturacion@example.com", program.PayoutNotifyEmail);
        program.SetPayoutNotifyEmail(" ");
        Assert.IsNull(program.PayoutNotifyEmail);
        Assert.ThrowsExactly<ArgumentException>(() => program.SetPayoutNotifyEmail("Facturación <facturacion@example.com>"));

        Assert.IsEmpty(program.Snippets("https://x.example/r/abc"));
        program.SetLinks("Contratá en {enlace}\r\n---\r\n<a href=\"{enlace}\">Hosting</a>\n---\n");
        CollectionAssert.AreEqual(
            new[] { "Contratá en https://x.example/r/abc", "<a href=\"https://x.example/r/abc\">Hosting</a>" },
            program.Snippets("https://x.example/r/abc").ToArray());
        Assert.ThrowsExactly<ArgumentException>(() => program.SetLinks(new string('x', 4001)));
    }

    private static AffiliateCommission Commission(decimal amount) =>
        new(AffiliateId, 1, 1, 1, Currency.Ars, 10000m, Rule(null, null, CommissionKind.Percent, 5m), amount, new DateOnly(2026, 12, 2), Now);
}
