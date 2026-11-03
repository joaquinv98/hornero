using BillingOss.App.Notifications;
using BillingOss.App.UseCases;
using BillingOss.Domain.Affiliates;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Programa de afiliados de punta a punta contra PostgreSQL (diseno/afiliados.md).</summary>
[TestClass]
public sealed class AffiliateFlowTests
{
    /// <summary>Programa prendido con 10 % recurrente, 30 días de espera y un afiliado activo en su propia cuenta.</summary>
    private static async Task<(CoreHarness H, long AffiliateId, long AffiliateAccount)> Start(int holdDays = 30, bool recurring = true, bool approval = false)
    {
        var h = await CoreHarness.Start();
        await h.Use<AffiliateService, bool>(async s =>
        {
            await s.UpdateProgram(enabled: true, approval, cookieDays: 60, holdDays, new Dictionary<string, decimal> { ["ARS"] = 5000m });
            await s.SetRule(null, null, CommissionKind.Percent, 10m, null, recurring);
            return true;
        });
        var account = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Afiliada", "afiliada@example.com", Currency.Ars)));
        var affiliate = await h.Use<AffiliateService, Affiliate>(s => s.Join(account));
        if (approval)
        {
            await h.Use<AffiliateService, bool>(async s =>
            {
                await s.Decide(affiliate.Id, AffiliateStatus.Active);
                return true;
            });
        }

        return (h, affiliate.Id, account);
    }

    private static Task<bool> Attribute(CoreHarness h, long affiliateId, DateTimeOffset? at = null) =>
        h.Use<AffiliateService, bool>(s => s.Attribute(h.AccountId, affiliateId, at ?? h.Clock.Now, AttributionSource.Link));

    private static Task<PlacedOrder> Order(CoreHarness h) =>
        h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId)]));

    private static Task<long> Pay(CoreHarness h, decimal amount, string externalId, PaymentStatus status = PaymentStatus.Approved) =>
        h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", externalId, Currency.Ars, amount, status)));

    private static Task<List<AffiliateCommission>> Commissions(CoreHarness h) =>
        h.Query(db => db.Set<AffiliateCommission>().OrderBy(c => c.Id).ToListAsync());

    private static Task<decimal> Available(CoreHarness h, long affiliateId) =>
        h.Query(db => db.Set<AffiliateLedgerEntry>().Where(e => e.AffiliateId == affiliateId).SumAsync(e => e.Amount));

    private static Task<int> Release(CoreHarness h) => h.Use<AffiliateCommissions, int>(c => c.ReleaseDue());

    [TestMethod]
    public async Task PedidoDentroDeLaVentana_PagaComisionEnEspera_YSeLiberaAlVencer()
    {
        var (h, affiliateId, _) = await Start();
        await using var _ = h;
        Assert.IsTrue(await Attribute(h, affiliateId));
        h.Clock.Now = h.Clock.Now.AddDays(5);

        var placed = await Order(h);
        await h.RunJobs();
        var referral = await h.Query(db => db.Set<AffiliateReferral>().SingleAsync());
        Assert.AreEqual(placed.ServiceIds[0], referral.ServiceId);
        Assert.AreEqual(placed.OrderId, referral.OrderId);
        Assert.AreEqual(ReferralSource.Order, referral.Source);

        await Pay(h, 12100m, "T-1");
        await h.RunJobs();

        // 10 % del neto sin IVA (10.000), en espera 30 días desde el pago.
        var commission = (await Commissions(h)).Single();
        Assert.AreEqual(1000m, commission.Amount);
        Assert.AreEqual(10000m, commission.Base);
        Assert.AreEqual(placed.InvoiceId, commission.DocumentId);
        Assert.AreEqual(CommissionStatus.Held, commission.Status);
        Assert.AreEqual(new DateOnly(2026, 12, 7), commission.ReleaseOn);
        Assert.AreEqual(0m, await Available(h, affiliateId));

        h.Clock.Now = h.Clock.Now.AddDays(29);
        await Release(h);
        Assert.AreEqual(CommissionStatus.Held, (await Commissions(h)).Single().Status, "Un día antes sigue en espera.");

        h.Clock.Now = h.Clock.Now.AddDays(1);
        await Release(h);
        Assert.AreEqual(CommissionStatus.Released, (await Commissions(h)).Single().Status);
        Assert.AreEqual(1000m, await Available(h, affiliateId));
        var balance = (await h.Use<AffiliateService, IReadOnlyList<AffiliateBalance>>(s => s.Balances(affiliateId))).Single();
        Assert.AreEqual(new AffiliateBalance(Currency.Ars, 1000m, 0m), balance);
    }

    [TestMethod]
    public async Task PedidoFueraDeLaVentana_OAnteriorAlClic_NoEsReferido()
    {
        var (h, affiliateId, _) = await Start();
        await using var _ = h;
        await Attribute(h, affiliateId, h.Clock.Now.AddMinutes(10));
        await Order(h);
        h.Clock.Now = h.Clock.Now.AddDays(61);
        await Order(h);
        await h.RunJobs();

        Assert.AreEqual(0, await h.Query(db => db.Set<AffiliateReferral>().CountAsync()));
    }

    [TestMethod]
    public async Task LaCuentaDelPropioAfiliado_NoSeAtribuye_YGanaElPrimerClic()
    {
        var (h, affiliateId, affiliateAccount) = await Start();
        await using var _ = h;
        var other = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Otro", "otro@example.com", Currency.Ars)));
        var second = await h.Use<AffiliateService, Affiliate>(s => s.Join(other));

        Assert.IsFalse(await h.Use<AffiliateService, bool>(s => s.Attribute(affiliateAccount, affiliateId, h.Clock.Now, AttributionSource.Link)));
        Assert.IsTrue(await Attribute(h, affiliateId));
        Assert.IsFalse(await Attribute(h, second.Id, h.Clock.Now.AddDays(10)), "Dentro de la ventana de otro afiliado gana el primero.");
        Assert.IsTrue(await Attribute(h, second.Id, h.Clock.Now.AddDays(61)), "Vencida, la toma el nuevo.");
        Assert.AreEqual(second.Id, (await h.Query(db => db.Set<AffiliateAttribution>().SingleAsync(a => a.AccountId == h.AccountId))).AffiliateId);
    }

    [TestMethod]
    public async Task Recurrente_PagaCadaRenovacion_YDeUnaSolaVezSoloLaPrimera()
    {
        foreach (var recurring in new[] { true, false })
        {
            var (h, affiliateId, _) = await Start(recurring: recurring);
            await using var _ = h;
            await Attribute(h, affiliateId);
            await Order(h);
            await Pay(h, 12100m, "T-1");
            await h.RunJobs();

            h.Clock.Now = h.Clock.Now.AddDays(25);
            await h.Use<BillingRunService, int>(s => s.Run());
            await Pay(h, 12100m, "T-2");
            await h.RunJobs();

            Assert.AreEqual(recurring ? 2 : 1, (await Commissions(h)).Count, recurring ? "Recurrente" : "Una vez");
        }
    }

    [TestMethod]
    public async Task ProductoSinComision_GanaALaReglaDelAfiliado_YElMontoFijoVaEnSuMoneda()
    {
        var (h, affiliateId, _) = await Start();
        await using var _ = h;
        var productId = await h.Query(db => db.Plans.Where(p => p.Id == h.PlanId).Select(p => p.ProductId).SingleAsync());
        await h.Use<AffiliateService, long>(s => s.SetRule(null, affiliateId, CommissionKind.Percent, 20m, null, true));
        await h.Use<AffiliateService, long>(s => s.SetRule(productId, null, CommissionKind.None, 0m, null, false));
        await Attribute(h, affiliateId);
        await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();
        Assert.AreEqual(0, (await Commissions(h)).Count, "El producto sin comisión no paga.");

        // El monto fijo en pesos reemplaza al "sin comisión" del producto, y la regla del afiliado sigue ganando.
        await h.Use<AffiliateService, long>(s => s.SetRule(productId, null, CommissionKind.Fixed, 1500m, Currency.Ars, true));
        Assert.AreEqual(1, await h.Query(db => db.Set<CommissionRule>().CountAsync(r => r.ProductId == productId)));
        await Order(h);
        await Pay(h, 12100m, "T-2");
        await h.RunJobs();
        Assert.AreEqual(2000m, (await Commissions(h)).Single().Amount, "20 % del afiliado sobre 10.000.");

        await h.Use<AffiliateService, long>(s => s.SetRule(productId, affiliateId, CommissionKind.Fixed, 1500m, Currency.Ars, true));
        await Order(h);
        await Pay(h, 12100m, "T-3");
        await h.RunJobs();
        Assert.AreEqual(1500m, (await Commissions(h)).Last().Amount, "La del afiliado para el producto gana a todas.");
    }

    [TestMethod]
    public async Task NotaDeCreditoTotal_RevierteLaComisionEnEspera_YNoSeLibera()
    {
        var (h, affiliateId, _) = await Start();
        await using var _ = h;
        await Attribute(h, affiliateId);
        var placed = await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();

        await h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value, [new CreditLine("Arrepentimiento", 1, 12100m, TaxTreatment.Taxed, 0.21m)], "Arrepentimiento"));
        await h.RunJobs();

        var commission = (await Commissions(h)).Single();
        Assert.AreEqual(CommissionStatus.Reversed, commission.Status);
        Assert.AreEqual(1000m, commission.Reversed);
        h.Clock.Now = h.Clock.Now.AddDays(31);
        await Release(h);
        Assert.AreEqual(0m, await Available(h, affiliateId));
    }

    [TestMethod]
    public async Task NotaParcialDespuesDeLiberar_DescuentaLaProporcionDelSaldo()
    {
        var (h, affiliateId, _) = await Start(holdDays: 0);
        await using var _ = h;
        await Attribute(h, affiliateId);
        var placed = await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();
        await Release(h);
        Assert.AreEqual(1000m, await Available(h, affiliateId));

        // 2.000 de 12.100 acreditados: se debe 1.000 por 10.100 dividido 12.100, que es 834,71.
        await h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value, [new CreditLine("Ajuste", 1, 2000m, TaxTreatment.Taxed, 0.21m)], "Ajuste"));
        await h.RunJobs();

        var commission = (await Commissions(h)).Single();
        Assert.AreEqual(CommissionStatus.Released, commission.Status);
        Assert.AreEqual(165.29m, commission.Reversed);
        Assert.AreEqual(834.71m, await Available(h, affiliateId));
    }

    [TestMethod]
    public async Task NotaParcialAntesDelPago_OLaQueLoCompleta_LaComisionNaceEnProporcion()
    {
        var (h, affiliateId, _) = await Start(holdDays: 0);
        await using var _ = h;
        await Attribute(h, affiliateId);
        var note = new CreditLine("Ajuste", 1, 2000m, TaxTreatment.Taxed, 0.21m);

        // La nota llega antes del pago, que cubre los 10.100 que quedan.
        var first = await Order(h);
        await h.Use<CreditService, long>(s => s.IssueCreditNote(first.InvoiceId!.Value, [note], "Ajuste"));
        await Pay(h, 10100m, "T-1");
        await h.RunJobs();

        // Un pago de 10.100 y después la nota, que completa lo que faltaba y deja la factura paga.
        var second = await Order(h);
        await Pay(h, 10100m, "T-2");
        await h.Use<CreditService, long>(s => s.IssueCreditNote(second.InvoiceId!.Value, [note], "Ajuste"));
        await h.RunJobs();
        await Release(h);

        // En las dos se debe 1.000 por 10.100 dividido 12.100, sea cual sea el orden de los trabajos.
        var commissions = await Commissions(h);
        Assert.HasCount(2, commissions);
        Assert.IsTrue(commissions.All(c => c is { Amount: 1000m, Reversed: 165.29m, Status: CommissionStatus.Released }));
        Assert.AreEqual(1669.42m, await Available(h, affiliateId));
    }

    [TestMethod]
    public async Task ContracargoDespuesDeLiberar_DescuentaTodo_YElPagoNuevoGeneraOtraComision()
    {
        var (h, affiliateId, _) = await Start(holdDays: 0);
        await using var _ = h;
        await Attribute(h, affiliateId);
        await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();
        await Release(h);

        await Pay(h, 12100m, "T-1", PaymentStatus.ChargedBack);
        await h.RunJobs();
        Assert.AreEqual(CommissionStatus.Reversed, (await Commissions(h)).Single().Status);
        Assert.AreEqual(0m, await Available(h, affiliateId));
        Assert.AreEqual(1, await h.Query(db => db.Set<AffiliateLedgerEntry>().CountAsync(e => e.Kind == LedgerKind.Reversal && e.Amount == -1000m)));

        await Pay(h, 12100m, "T-2");
        await h.RunJobs();
        await Release(h);
        var commissions = await Commissions(h);
        Assert.HasCount(2, commissions);
        Assert.AreEqual(CommissionStatus.Released, commissions[1].Status);
        Assert.AreEqual(1000m, await Available(h, affiliateId));
    }

    [TestMethod]
    public async Task ReembolsoDelStaff_YReembolsoInformadoPorLaPasarela_RevierteLaComision()
    {
        var (h, affiliateId, _) = await Start();
        await using var _ = h;
        await Attribute(h, affiliateId);
        var first = await Order(h);
        var paymentId = await Pay(h, 12100m, "T-1");
        await Order(h);
        await Pay(h, 12100m, "T-2");
        await h.RunJobs();
        Assert.HasCount(2, await Commissions(h));

        // El staff devuelve la primera factura entera: la nota de crédito revierte su comisión.
        await h.Use<PaymentService, long>(s => s.RequestRefund(paymentId, first.InvoiceId!.Value, [new CreditLine("Devolución", 1, 12100m, TaxTreatment.Taxed, 0.21m)], "Devolución"));
        await h.RunJobs();

        // La pasarela informa que devolvió el segundo pago: la factura vuelve a deber y su comisión se revierte.
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-2", Currency.Ars, 12100m, PaymentStatus.Refunded, Refunded: 12100m)));
        await h.RunJobs();

        Assert.IsTrue((await Commissions(h)).All(c => c.Status == CommissionStatus.Reversed));
        h.Clock.Now = h.Clock.Now.AddDays(31);
        await Release(h);
        Assert.AreEqual(0m, await Available(h, affiliateId));
    }

    [TestMethod]
    public async Task AvisoDePagoRepetido_YDosLiberacionesALaVez_NoDuplican()
    {
        var (h, affiliateId, _) = await Start(holdDays: 0);
        await using var _ = h;
        await Attribute(h, affiliateId);
        var placed = await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();

        await Task.WhenAll(
            h.Use<AffiliateCommissions, int>(c => c.InvoicePaid(placed.InvoiceId!.Value)),
            h.Use<AffiliateCommissions, int>(c => c.InvoicePaid(placed.InvoiceId!.Value)));
        Assert.HasCount(1, await Commissions(h));

        await Task.WhenAll(Release(h), Release(h));
        Assert.AreEqual(1000m, await Available(h, affiliateId));
        Assert.AreEqual(1, await h.Query(db => db.Set<AffiliateLedgerEntry>().CountAsync()));
    }

    [TestMethod]
    public async Task PagoParcial_NoGeneraComision_HastaQueLaFacturaQuedaPaga()
    {
        var (h, affiliateId, _) = await Start();
        await using var _ = h;
        await Attribute(h, affiliateId);
        await Order(h);
        await Pay(h, 6000m, "T-1");
        await h.RunJobs();
        Assert.IsEmpty(await Commissions(h));

        await Pay(h, 6100m, "T-2");
        await h.RunJobs();
        Assert.HasCount(1, await Commissions(h));
    }

    [TestMethod]
    public async Task AfiliadoSuspendido_NoSumaComisiones_PeroLoEnEsperaSeLibera()
    {
        var (h, affiliateId, _) = await Start(holdDays: 0);
        await using var _ = h;
        await Attribute(h, affiliateId);
        await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();
        await h.Use<AffiliateService, bool>(async s =>
        {
            await s.Decide(affiliateId, AffiliateStatus.Suspended);
            return true;
        });

        await Order(h);
        await Pay(h, 12100m, "T-2");
        await h.RunJobs();
        await Release(h);

        Assert.HasCount(1, await Commissions(h));
        Assert.AreEqual(1000m, await Available(h, affiliateId));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<AffiliateService, long>(s => s.ConvertToCredit(affiliateId, Currency.Ars, 10m)));
    }

    [TestMethod]
    public async Task ConAprobacion_QuedaPendiente_YRecienAprobadoSumaClics()
    {
        var h = await CoreHarness.Start();
        await using var _ = h;
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<AffiliateService, Affiliate>(s => s.Join(h.AccountId)), "Con el programa apagado nadie se suma.");
        await h.Use<AffiliateService, bool>(async s =>
        {
            await s.UpdateProgram(true, true, 60, 30, new Dictionary<string, decimal>());
            return true;
        });

        var affiliate = await h.Use<AffiliateService, Affiliate>(s => s.Join(h.AccountId));
        Assert.AreEqual(AffiliateStatus.Pending, affiliate.Status);
        Assert.AreEqual(affiliate.Id, (await h.Use<AffiliateService, Affiliate>(s => s.Join(h.AccountId))).Id, "Sumarse dos veces devuelve el mismo afiliado.");
        Assert.IsNull(await h.Use<AffiliateService, (long, int)?>(s => s.Click(affiliate.Code, null, null)));

        await h.Use<AffiliateService, bool>(async s =>
        {
            await s.Decide(affiliate.Id, AffiliateStatus.Active);
            return true;
        });
        Assert.AreEqual((affiliate.Id, 60), await h.Use<AffiliateService, (long, int)?>(s => s.Click(affiliate.Code.ToUpperInvariant(), "example.org", null)));
        Assert.IsNotNull(await h.Use<AffiliateService, (long, int)?>(s => s.Click(affiliate.Code, null, affiliate.Id)));
        Assert.AreEqual(1, await h.Query(db => db.Set<AffiliateClick>().CountAsync(c => c.ReferrerHost == "example.org")), "El que ya traía la cookie no suma otro clic.");
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<AffiliateService, bool>(async s =>
        {
            await s.Decide(affiliate.Id, AffiliateStatus.Pending);
            return true;
        }));
        Assert.AreEqual(2, await h.Query(db => db.Set<BillingOss.App.Platform.AuditEvent>().CountAsync(e => e.Action == "affiliates.join" || e.Action == "affiliates.status")));
    }

    [TestMethod]
    public async Task Retiro_RespetaElMinimoYElSaldo_ElRechazoDevuelve_YDosALaVezNoPasanElSaldo()
    {
        var (h, affiliateId, _) = await Start(holdDays: 0);
        await using var _ = h;
        await Attribute(h, affiliateId);
        for (var i = 1; i <= 6; i++)
        {
            await Order(h);
            await Pay(h, 12100m, $"T-{i}");
        }

        await h.RunJobs();
        await Release(h);
        Assert.AreEqual(6000m, await Available(h, affiliateId));

        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<AffiliateService, long>(s => s.RequestWithdrawal(affiliateId, Currency.Ars, 4999.99m, "CBU 0000003100000000000001")));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<AffiliateService, long>(s => s.RequestWithdrawal(affiliateId, Currency.Ars, 6000.01m, "CBU 0000003100000000000001")));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<AffiliateService, long>(s => s.RequestWithdrawal(affiliateId, Currency.Ars, 5000m, " ")));

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Try(() => h.Use<AffiliateService, long>(s => s.RequestWithdrawal(affiliateId, Currency.Ars, 5000m, "Alias afiliada.mp")))));
        Assert.AreEqual(1, results.Count(r => r), "Con 6.000 de saldo, de dos retiros de 5.000 a la vez pasa uno.");
        Assert.AreEqual(1000m, await Available(h, affiliateId));

        var payout = await h.Query(db => db.Set<AffiliatePayout>().SingleAsync());
        Assert.AreEqual(PayoutStatus.Requested, payout.Status);
        await h.Use<AffiliateService, bool>(async s =>
        {
            await s.RejectPayout(payout.Id, "El alias no existe");
            await s.RejectPayout(payout.Id, "Otra vez");
            return true;
        });
        Assert.AreEqual(6000m, await Available(h, affiliateId), "El rechazo devuelve el importe una sola vez.");
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<AffiliateService, bool>(async s =>
        {
            await s.MarkPayoutPaid(payout.Id, "TR-1");
            return true;
        }));

        var second = await h.Use<AffiliateService, long>(s => s.RequestWithdrawal(affiliateId, Currency.Ars, 6000m, "Alias afiliada.ok"));
        await h.Use<AffiliateService, bool>(async s =>
        {
            await s.MarkPayoutPaid(second, "TR-2");
            await s.MarkPayoutPaid(second, "TR-3");
            return true;
        });
        var paid = await h.Query(db => db.Set<AffiliatePayout>().SingleAsync(p => p.Id == second));
        Assert.AreEqual(PayoutStatus.Paid, paid.Status);
        Assert.AreEqual("TR-2", paid.Reference);
        Assert.AreEqual(0m, await Available(h, affiliateId));
        Assert.AreEqual(
            new AffiliateBalance(Currency.Ars, 0m, 0m, 6000m),
            (await h.Use<AffiliateService, IReadOnlyList<AffiliateBalance>>(s => s.Balances(affiliateId))).Single(),
            "Lo cobrado suma el retiro pagado y no el rechazado.");
    }

    [TestMethod]
    public async Task BonoDeAlta_VaEnLaPrimeraActivacion_EnLaMonedaDeLaCuenta_YUnaSolaVez()
    {
        var h = await CoreHarness.Start();
        await using var _ = h;
        await ProgramWith(h, approval: false, bonuses: new() { ["ARS"] = 1000m, ["USD"] = 2m });

        // Sin aprobación previa, el bono va al sumarse.
        var direct = await h.Use<AffiliateService, Affiliate>(s => s.Join(h.AccountId));
        Assert.AreEqual(1000m, await Available(h, direct.Id));

        // Con aprobación, al aprobar, aunque antes lo hayan rechazado. Suspender y reactivar no da otro.
        await ProgramWith(h, approval: true, bonuses: new() { ["USD"] = 2m });
        var account = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Otra", "otra@example.com", Currency.Usd)));
        var pending = await h.Use<AffiliateService, Affiliate>(s => s.Join(account));
        Assert.AreEqual(0m, await Available(h, pending.Id));
        foreach (var to in new[] { AffiliateStatus.Rejected, AffiliateStatus.Active, AffiliateStatus.Suspended, AffiliateStatus.Active })
        {
            await h.Use<AffiliateService, bool>(async s =>
            {
                await s.Decide(pending.Id, to);
                return true;
            });
        }

        var bonus = await h.Query(db => db.Set<AffiliateLedgerEntry>().SingleAsync(e => e.AffiliateId == pending.Id));
        Assert.AreEqual((LedgerKind.Bonus, 2m, Currency.Usd), (bonus.Kind, bonus.Amount, bonus.Currency));

        // La base tampoco deja un segundo bono.
        var duplicate = await Assert.ThrowsExactlyAsync<DbUpdateException>(() => h.Db(async db =>
        {
            db.Add(new AffiliateLedgerEntry(pending.Id, Currency.Usd, 2m, LedgerKind.Bonus, h.Clock.Now));
            await db.SaveChangesAsync();
        }));
        StringAssert.Contains(duplicate.InnerException!.Message, "ix_ledger_entries_bonus");
    }

    [TestMethod]
    public async Task RetiroPedido_AvisaALaCasillaDelPrograma_SinLosDatosDeLaTransferencia()
    {
        var (h, affiliateId, _) = await Start(holdDays: 0);
        await using var _ = h;
        await ProgramWith(h, approval: false, notify: "facturacion@example.com");
        await h.Db(async db =>
        {
            db.Add(new AffiliateLedgerEntry(affiliateId, Currency.Ars, 6000m, LedgerKind.Import, h.Clock.Now));
            await db.SaveChangesAsync();
        });

        var payoutId = await h.Use<AffiliateService, long>(s => s.RequestWithdrawal(affiliateId, Currency.Ars, 5000m, "CBU 0000003100000000000001"));
        await h.Use<AffiliateService, long>(s => s.ConvertToCredit(affiliateId, Currency.Ars, 100m));

        var email = await h.Query(db => db.Set<OutboundEmail>().SingleAsync(e => e.Template == TemplateKeys.AffiliatePayoutRequested));
        Assert.AreEqual(("facturacion@example.com", $"Pedido de retiro de afiliado N.º {payoutId}"), (email.Recipient, email.Subject));
        StringAssert.Contains(email.Html, "ARS 5.000,00");
        Assert.DoesNotContain("0000003100000000000001", email.Html);
        Assert.AreEqual(EmailStatus.Suppressed, email.Status, "Con el envío apagado queda registrado sin salir.");
    }

    [TestMethod]
    public async Task ResumenMensual_ElDiaUno_UnoPorAfiliadoActivo_ConLoDelMesAnterior()
    {
        var (h, affiliateId, _) = await Start(holdDays: 0);
        await using var _ = h;
        Assert.AreEqual(0, await h.Use<AffiliateService, int>(s => s.SendMonthlyReports()), "Apagado no manda nada.");
        await ProgramWith(h, approval: false, monthly: true);
        var code = await h.Query(db => db.Set<Affiliate>().Where(a => a.Id == affiliateId).Select(a => a.Code).SingleAsync());
        await h.Use<AffiliateService, (long, int)?>(s => s.Click(code, null, null));
        await Attribute(h, affiliateId);
        await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();
        await Release(h);

        // Un afiliado pendiente no recibe el resumen.
        var other = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Pendiente", "pendiente@example.com", Currency.Ars)));
        await ProgramWith(h, approval: true, monthly: true);
        await h.Use<AffiliateService, Affiliate>(s => s.Join(other));

        h.Clock.Now = new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero);
        await h.Tick();
        await h.RunJobs();
        Assert.AreEqual(0, await h.Use<AffiliateService, int>(s => s.SendMonthlyReports()), "Repetirlo el mismo mes no manda otro.");

        var email = await h.Query(db => db.Set<OutboundEmail>().SingleAsync(e => e.Template == TemplateKeys.AffiliateMonthly));
        Assert.AreEqual(("afiliada@example.com", "Tu resumen de afiliado de noviembre de 2026"), (email.Recipient, email.Subject));
        StringAssert.Contains(email.Html, "Visitas: <strong>1</strong>");
        StringAssert.Contains(email.Html, "Servicios nuevos referidos: <strong>1</strong>");
        StringAssert.Contains(email.Html, "Comisiones del mes: <strong>ARS 1.000,00</strong>");
        StringAssert.Contains(email.Html, "ARS 1.000,00 disponible");
    }

    private static Task<bool> ProgramWith(CoreHarness h, bool approval, Dictionary<string, decimal>? bonuses = null, string? notify = null, bool monthly = false) =>
        h.Use<AffiliateService, bool>(async s =>
        {
            await s.UpdateProgram(true, approval, 60, 0, new Dictionary<string, decimal>(), bonuses, notify, monthlyReport: monthly);
            return true;
        });

    [TestMethod]
    public async Task SaldoAFavor_PasaAlLibroDeLaCuenta_YPagaSuProximaFactura()
    {
        var (h, affiliateId, affiliateAccount) = await Start(holdDays: 0);
        await using var _ = h;
        await Attribute(h, affiliateId);
        await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();
        await Release(h);

        var payoutId = await h.Use<AffiliateService, long>(s => s.ConvertToCredit(affiliateId, Currency.Ars, 600m));

        var payout = await h.Query(db => db.Set<AffiliatePayout>().SingleAsync(p => p.Id == payoutId));
        Assert.AreEqual(PayoutStatus.Paid, payout.Status);
        Assert.AreEqual(PayoutMethod.Credit, payout.Method);
        var entry = await h.Query(db => db.CreditEntries.SingleAsync(e => e.Id == payout.CreditEntryId));
        Assert.AreEqual(affiliateAccount, entry.AccountId);
        Assert.AreEqual(600m, entry.Amount);
        Assert.AreEqual(600m, await h.Query(db => db.CreditBalances.Where(b => b.AccountId == affiliateAccount).SumAsync(b => b.Balance)));
        Assert.AreEqual(400m, await Available(h, affiliateId));

        var invoice = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(affiliateAccount, [new OrderItem(h.PlanId)]));
        Assert.AreEqual(600m, await h.Query(db => db.Documents.Where(d => d.Id == invoice.InvoiceId).Select(d => d.Allocated).SingleAsync()));
    }

    [TestMethod]
    public async Task ServicioAsignadoPorElStaff_GeneraComision_YNoSePuedeAsignarDosVeces()
    {
        var (h, affiliateId, affiliateAccount) = await Start();
        await using var _ = h;
        var placed = await Order(h);
        await h.RunJobs();
        Assert.AreEqual(0, await h.Query(db => db.Set<AffiliateReferral>().CountAsync()), "Sin atribución no hay referido.");

        await h.Use<AffiliateService, long>(s => s.AssignReferral(affiliateId, placed.ServiceIds[0]));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<AffiliateService, long>(s => s.AssignReferral(affiliateId, placed.ServiceIds[0])));
        var own = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(affiliateAccount, [new OrderItem(h.PlanId)]));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<AffiliateService, long>(s => s.AssignReferral(affiliateId, own.ServiceIds[0])));

        await Pay(h, 12100m, "T-1");
        await h.RunJobs();
        Assert.AreEqual(1000m, (await Commissions(h)).Single().Amount);
    }

    private static async Task<bool> Try(Func<Task> work)
    {
        try
        {
            await work();
            return true;
        }
        catch (ValidationException)
        {
            return false;
        }
    }
}
