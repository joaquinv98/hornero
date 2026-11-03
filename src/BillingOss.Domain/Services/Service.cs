using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Services;

/// <summary>Lo contratado (sección 5 del modelo de dominio).</summary>
public sealed class Service
{
    private Service()
    {
    }

    public Service(long accountId, long productId, BillingCycle cycle, Currency currency, decimal unitPrice, int quantity, DateTimeOffset now, PriceMode priceMode = PriceMode.TaxIncluded)
    {
        if (quantity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "La cantidad tiene que ser 1 o más.");
        }

        AccountId = accountId;
        ProductId = productId;
        CycleUnit = cycle.Unit;
        CycleCount = cycle.Count;
        Currency = currency;
        UnitPrice = unitPrice;
        PriceMode = priceMode;
        Quantity = quantity;
        CreatedAt = now;
    }

    public long Id { get; private set; }

    /// <summary>
    /// Identidad estable del servicio fuera de esta instalación. Los módulos la dejan como marca en el sistema
    /// externo: a diferencia del ID, no choca con la de otra instalación que comparte el panel.
    /// </summary>
    public Guid Uuid { get; private set; } = Guid.CreateVersion7();

    public long AccountId { get; private set; }

    public long ProductId { get; private set; }

    public long? ParentServiceId { get; set; }

    public long? OrderLineId { get; set; }

    /// <summary>Hostname, dominio o nombre que lo identifica para el cliente.</summary>
    public string? Label { get; set; }

    public CycleUnit CycleUnit { get; private set; }

    public int CycleCount { get; private set; }

    public Currency Currency { get; private set; }

    public decimal UnitPrice { get; private set; }

    /// <summary>
    /// Moneda del precio pactado cuando no es la de la factura, por ejemplo dólares en una cuenta en pesos. Cada
    /// renovación convierte <see cref="IndexedUnitPrice"/> con la cotización del día y lo deja en <see cref="UnitPrice"/>,
    /// así la mora, el saldo a favor y los cobros siguen en la moneda de la factura (sección 6.4 del modelo de dominio).
    /// </summary>
    public Currency? IndexCurrency { get; private set; }

    /// <summary>El precio unitario pactado en <see cref="IndexCurrency"/>.</summary>
    public decimal? IndexedUnitPrice { get; private set; }

    /// <summary>Congelado junto con el precio: cambiar un ajuste nunca cambia lo que paga un cliente que ya existe.</summary>
    public PriceMode PriceMode { get; private set; }

    public int Quantity { get; private set; }

    public decimal? DiscountPercent { get; set; }

    /// <summary>Descuento fijo por unidad y por período, de un cupón de monto fijo.</summary>
    public decimal? DiscountAmount { get; set; }

    /// <summary>
    /// Ciclos de descuento que quedan. Nulo con descuento es para siempre. Cero es un descuento que se terminó: ya no va
    /// en lo que se factura, pero el período que lo consumió lo tuvo hasta <see cref="DiscountEndsOn"/>.
    /// </summary>
    public int? DiscountCyclesLeft { get; set; }

    /// <summary>Fin del último período con el descuento que se terminó. El prorrateo de un cambio lo aplica hasta acá.</summary>
    public DateOnly? DiscountEndsOn { get; set; }

    /// <summary>Cupón con el que se contrató, como referencia. El descuento vigente es el de arriba.</summary>
    public long? CouponId { get; set; }

    /// <summary>Fin de la prueba gratis con la que empezó: el primer período facturado arranca ese día.</summary>
    public DateOnly? TrialEndsOn { get; set; }

    public int AnchorDay { get; private set; }

    /// <summary>Inicio del próximo período a facturar. Nulo si no renueva.</summary>
    public DateOnly? NextDueDate { get; private set; }

    /// <summary>
    /// Hasta dónde está pago: el inicio del primer período facturado y no pagado, o el próximo a facturar si está
    /// todo pago. La mora se mide desde acá (sección 5.2 del modelo de dominio).
    /// </summary>
    public DateOnly? PaidThrough { get; private set; }

    public ServiceStatus Status { get; private set; }

    /// <summary>Baja pedida a fin de período: no renueva desde esta fecha y ese día se da de baja.</summary>
    public DateOnly? CancelAt { get; set; }

    /// <summary>Motivo de una suspensión que no es por mora. Nulo si es por mora: solo esa la levanta el pago.</summary>
    public string? SuspendReason { get; set; }

    /// <summary>Exento de la suspensión automática por mora.</summary>
    public bool NoAutoSuspend { get; set; }

    /// <summary>Lo que el módulo confirmó la última vez (active, suspended, terminated, not_found o unknown).</summary>
    public string? ObservedStatus { get; set; }

    public long? ModuleInstanceId { get; set; }

    /// <summary>Parámetros del módulo congelados al contratar. JSON.</summary>
    public string ModuleParams { get; set; } = "{}";

    /// <summary>IDs que devolvió el módulo y se le mandan en cada llamada. JSON.</summary>
    public string ExternalIds { get; set; } = "{}";

    /// <summary>Operación larga en curso en el módulo, que se consulta hasta que termina (sección 7 del protocolo).</summary>
    public string? PendingOperation { get; set; }

    /// <summary>Medio de pago elegido al contratar. Decide si el comprobante en moneda extranjera se cancela en esa moneda.</summary>
    public string? PaymentMethod { get; set; }

    /// <summary>Generación de la intención, para las claves de idempotencia.</summary>
    public int Generation { get; private set; } = 1;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    public DateTimeOffset? TerminatedAt { get; private set; }

    public BillingCycle Cycle => new(CycleUnit, CycleCount);

    public decimal RecurringAmount => Rounding.Commercial(DiscountedUnitPrice * Quantity);

    /// <summary>Precio unitario del próximo período, con el descuento vigente. Se lee antes de tomar el período, que puede consumirlo.</summary>
    public decimal DiscountedUnitPrice => Discounted(UnitPrice);

    /// <summary>Un precio unitario con el descuento vigente del servicio, sin bajar de cero. El cambio de plan lo conserva.</summary>
    public decimal Discounted(decimal unitPrice) => DiscountCyclesLeft == 0 ? unitPrice : WithDiscount(unitPrice);

    /// <summary>
    /// Un precio unitario con el descuento que tuvo un período ya facturado: el vigente, o el que se terminó si el período
    /// fue uno de los suyos. Es lo que prorratea un cambio de plan.
    /// </summary>
    public decimal DiscountedIn(Period period, decimal unitPrice) =>
        DiscountCyclesLeft != 0 || period.Start < DiscountEndsOn ? WithDiscount(unitPrice) : unitPrice;

    /// <summary>Tiene un descuento que vale para lo que se facture o para el período en curso el día <paramref name="on"/>.</summary>
    public bool Discounting(DateOnly on) => (DiscountPercent is > 0m || DiscountAmount is > 0m) && (DiscountCyclesLeft != 0 || on < DiscountEndsOn);

    /// <summary>
    /// Un cambio a un producto o un ciclo donde el descuento no vale lo termina. Lo que quedaba del período ya se cobró a
    /// precio lleno con el cambio, así que ningún período lo conserva para un cambio que venga después.
    /// </summary>
    public void EndDiscount()
    {
        DiscountCyclesLeft = 0;
        DiscountEndsOn = null;
    }

    /// <summary>Pacta el precio en otra moneda: la factura sigue en <see cref="Currency"/>, convertida en cada renovación.</summary>
    public void IndexTo(Currency currency, decimal unitPrice)
    {
        if (currency == Currency)
        {
            throw new ArgumentException("Un precio en la moneda de la factura no se convierte.", nameof(currency));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(unitPrice);
        IndexCurrency = currency;
        IndexedUnitPrice = unitPrice;
    }

    /// <summary>El precio en la moneda de la factura que dio el pactado con la cotización del día.</summary>
    public void Reprice(decimal unitPrice) =>
        UnitPrice = IndexCurrency is not null && unitPrice >= 0m ? unitPrice : throw new InvalidOperationException($"El servicio {Id} no tiene precio pactado en otra moneda.");

    /// <summary>Fija el primer período: el día de anclaje queda guardado para siempre.</summary>
    public void StartBilling(DateOnly firstPeriodStart)
    {
        AnchorDay = firstPeriodStart.Day;
        NextDueDate = Cycle.IsRecurring ? firstPeriodStart : null;
        PaidThrough = NextDueDate;
    }

    /// <summary>
    /// Se pagó una renovación facturada: lo pagado avanza un ciclo, sin pasar lo facturado. Vale igual para las
    /// facturas importadas, que no traen sus períodos.
    /// </summary>
    public void RenewalPaid()
    {
        if (PaidThrough is { } paid && NextDueDate is { } next && paid < next)
        {
            PaidThrough = BillingSchedule.PeriodStartingAt(paid, Cycle, AnchorDay).End;
        }
    }

    /// <summary>Se revirtió el pago de un período facturado: lo pagado vuelve, como mucho, a su inicio.</summary>
    public void RenewalReversed(DateOnly periodStart)
    {
        if (PaidThrough is { } paid && periodStart < paid)
        {
            PaidThrough = periodStart;
        }
    }

    /// <summary>Período que sigue en facturarse y avance de la fecha. Baja un ciclo de descuento.</summary>
    public Period TakeNextPeriod()
    {
        var start = NextDueDate ?? throw new InvalidOperationException($"El servicio {Id} no renueva.");
        var period = BillingSchedule.PeriodStartingAt(start, Cycle, AnchorDay);
        NextDueDate = period.End;
        if (DiscountCyclesLeft is > 0)
        {
            // El último período con descuento lo lleva hasta su fin: una baja de plan en ese período acredita lo que se
            // cobró con descuento, no el precio lleno.
            DiscountCyclesLeft--;
            if (DiscountCyclesLeft == 0)
            {
                DiscountEndsOn = period.End;
            }
        }

        return period;
    }

    public void MoveTo(ServiceStatus status, DateTimeOffset now)
    {
        Status = ServiceStates.Machine.Move(Status, status);
        if (status == ServiceStatus.Active)
        {
            ActivatedAt ??= now;
        }
        else if (status == ServiceStatus.Terminated)
        {
            TerminatedAt = now;
            NextDueDate = null;
        }
    }

    /// <summary>
    /// Cambio de plan aplicado: producto, ciclo y precio nuevos. El período en curso y lo pagado no se tocan: el ciclo
    /// nuevo vale desde la próxima renovación.
    /// </summary>
    public void ChangePlan(long productId, BillingCycle cycle, decimal unitPrice)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        if (cycle.IsRecurring != Cycle.IsRecurring)
        {
            throw new InvalidOperationException("Un servicio recurrente no pasa a un ciclo sin renovación, ni al revés.");
        }

        ProductId = productId;
        CycleUnit = cycle.Unit;
        CycleCount = cycle.Count;
        UnitPrice = unitPrice;
    }

    /// <summary>Una intención nueva: la próxima operación del módulo usa otra clave de idempotencia.</summary>
    public void NextGeneration() => Generation++;

    public void SetImportedId(long id) => Id = id;

    /// <summary>Estado traído del sistema anterior, sin pasar por la máquina de estados (D-17).</summary>
    public void ImportState(ServiceStatus status, int anchorDay, DateOnly? nextDueDate, DateOnly? paidThrough, DateTimeOffset? activatedAt, DateTimeOffset? terminatedAt)
    {
        Status = status;
        AnchorDay = Math.Clamp(anchorDay, 1, 31);
        NextDueDate = Cycle.IsRecurring && status is ServiceStatus.Active or ServiceStatus.Suspended ? nextDueDate : null;
        PaidThrough = NextDueDate is null ? null : paidThrough ?? NextDueDate;
        ActivatedAt = activatedAt;
        TerminatedAt = terminatedAt;
    }

    private decimal WithDiscount(decimal unitPrice) => Math.Max(0m, (unitPrice * (1m - ((DiscountPercent ?? 0m) / 100m))) - (DiscountAmount ?? 0m));
}
