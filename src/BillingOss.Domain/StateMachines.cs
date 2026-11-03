using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Services
{
    public enum ServiceStatus
    {
        Pending,
        Provisioning,
        Active,
        Failed,
        Suspended,
        Terminated,
        Cancelled,
    }

    /// <summary>Sección 5.1 del modelo de dominio.</summary>
    public static class ServiceStates
    {
        public static StateMachine<ServiceStatus> Machine { get; } = new(
            (ServiceStatus.Pending, ServiceStatus.Provisioning),
            (ServiceStatus.Pending, ServiceStatus.Cancelled),
            (ServiceStatus.Provisioning, ServiceStatus.Active),
            (ServiceStatus.Provisioning, ServiceStatus.Failed),
            (ServiceStatus.Failed, ServiceStatus.Provisioning),
            (ServiceStatus.Active, ServiceStatus.Suspended),
            (ServiceStatus.Suspended, ServiceStatus.Active),
            (ServiceStatus.Active, ServiceStatus.Terminated),
            (ServiceStatus.Suspended, ServiceStatus.Terminated));
    }
}

namespace BillingOss.Domain.Payments
{
    public enum PaymentStatus
    {
        Pending,
        Approved,
        Rejected,
        Disputed,
        ChargedBack,
        PartiallyRefunded,
        Refunded,
    }

    /// <summary>Sección 8.1 del modelo de dominio. Lo que no está en el diagrama se registra y se ignora.</summary>
    public static class PaymentStates
    {
        public static StateMachine<PaymentStatus> Machine { get; } = new(
            (PaymentStatus.Pending, PaymentStatus.Approved),
            (PaymentStatus.Pending, PaymentStatus.Rejected),
            (PaymentStatus.Approved, PaymentStatus.Disputed),
            (PaymentStatus.Disputed, PaymentStatus.Approved),
            (PaymentStatus.Disputed, PaymentStatus.ChargedBack),

            // Un contracargo puede llegar sin haber visto la disputa: Mercado Pago no avisa los contracargos.
            (PaymentStatus.Approved, PaymentStatus.ChargedBack),
            (PaymentStatus.Approved, PaymentStatus.PartiallyRefunded),
            (PaymentStatus.Approved, PaymentStatus.Refunded),
            (PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded));
    }
}

namespace BillingOss.Domain.Fiscal
{
    public enum EmissionStatus
    {
        Pending,
        Sending,
        Authorized,
        Rejected,
        Unknown,
        Conflict,
    }

    public enum SeriesCheck
    {
        /// <summary>El fisco y el núcleo coinciden: se puede emitir el siguiente.</summary>
        InSync,

        /// <summary>El fisco va adelante: hay que conciliar con find antes de emitir.</summary>
        MustReconcile,

        /// <summary>El núcleo va adelante: no debería pasar nunca y frena la serie.</summary>
        LocalAhead,
    }

    /// <summary>Secciones 7 y 7.1 del modelo de dominio y 9.4 del protocolo.</summary>
    public static class EmissionRules
    {
        public static StateMachine<EmissionStatus> Machine { get; } = new(
            (EmissionStatus.Pending, EmissionStatus.Sending),
            (EmissionStatus.Sending, EmissionStatus.Authorized),
            (EmissionStatus.Sending, EmissionStatus.Rejected),
            (EmissionStatus.Sending, EmissionStatus.Unknown),
            (EmissionStatus.Unknown, EmissionStatus.Authorized),
            (EmissionStatus.Unknown, EmissionStatus.Pending),
            (EmissionStatus.Unknown, EmissionStatus.Conflict),
            (EmissionStatus.Rejected, EmissionStatus.Pending),

            // El núcleo rechaza sin llamar al fisco: una nota cuyo original no tiene comprobante.
            (EmissionStatus.Pending, EmissionStatus.Rejected),

            // Intervención del staff: adopta lo que el fisco ya tiene con ese número, o reintenta con otro.
            (EmissionStatus.Pending, EmissionStatus.Authorized),
            (EmissionStatus.Conflict, EmissionStatus.Authorized),
            (EmissionStatus.Conflict, EmissionStatus.Pending));

        public static SeriesCheck CheckSeries(long lastAtFisco, long lastRecorded) =>
            lastAtFisco == lastRecorded ? SeriesCheck.InSync
            : lastAtFisco > lastRecorded ? SeriesCheck.MustReconcile
            : SeriesCheck.LocalAhead;

        /// <summary>Cuánto dura una comprobación de la serie contra el fisco: el control diario la renueva.</summary>
        public static TimeSpan SyncWindow { get; } = TimeSpan.FromHours(24);

        /// <summary>
        /// La serie se da por sincronizada si se comprobó contra el fisco hace menos de un día: ahí se propone el
        /// siguiente número sin pedir el último, y un número ya usado vuelve como incierto y se resuelve consultando.
        /// </summary>
        public static bool IsSynced(DateTimeOffset? syncedAt, DateTimeOffset now) =>
            syncedAt is { } at && now - at < SyncWindow && at <= now;

        /// <summary>
        /// El fisco admite una cotización entre el 2 % y el 400 % de la orientativa que publica para la fecha (manual
        /// de WSFEv1 4.7, validación 10119). Se comprueba antes de llamar, para que una cotización copiada de un
        /// comprobante viejo o importado no viaje al fisco.
        /// </summary>
        public static bool RateInRange(decimal rate, decimal reference) =>
            reference > 0m && rate >= reference * 0.02m && rate <= reference * 4m;

        /// <summary>
        /// La fecha del comprobante es la del intento y nunca anterior a la del último autorizado de la serie. Si ese
        /// último es de mañana (un reloj adelantado), se sigue con su fecha: el fisco acepta hasta diez días después del
        /// envío para servicios y rechaza retroceder (manual de WSFEv1 4.7, validaciones de CbteFch).
        /// </summary>
        public static DateOnly DocumentDate(DateOnly today, DateOnly? lastAuthorizedDate) =>
            lastAuthorizedDate is { } last && last > today ? last : today;

        /// <summary>El vencimiento de pago informado nunca es anterior a la fecha del comprobante.</summary>
        public static DateOnly PaymentDue(DateOnly documentDate, DateOnly invoiceDue) =>
            invoiceDue >= documentDate ? invoiceDue : documentDate;
    }
}
