using BillingOss.App.Content;
using BillingOss.App.Identity;
using BillingOss.App.Notifications;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.Support;
using BillingOss.App.UseCases;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BillingOss.App;

public static class CoreServices
{
    /// <summary>
    /// Registra el núcleo. Con <paramref name="secrets"/> se habilitan los módulos por el protocolo v0, y sin ellos no
    /// se corren emisiones fiscales ni cobros (esos trabajos esperan a un nodo con llaves). Los trabajadores en segundo
    /// plano se suman aparte, según el rol del proceso.
    /// </summary>
    public static IServiceCollection AddBillingCore(this IServiceCollection services, string connectionString, Action<BillingOptions>? configure = null, SecretBox? secrets = null)
    {
        if (secrets is not null)
        {
            services.AddSingleton(secrets);
            services.AddSingleton<ModuleClient>();
            services.AddScoped<ModuleRegistry>();
            services.AddScoped<ProtocolServiceModules>();
            services.AddScoped<IServiceModules>(sp => sp.GetRequiredService<ProtocolServiceModules>());
            services.AddScoped<IJobHandler, ServiceReconcileHandler>();
            services.AddScoped<IJobHandler, FiscalEmissionHandler>();
            services.AddScoped<IJobHandler, FiscalSweepHandler>();
            services.AddScoped<FiscalSetupService>();
            services.AddScoped<IJobHandler, FiscalCertificateHandler>();
            services.AddScoped<IJobHandler, FiscalCheckHandler>();
            services.AddScoped<IJobHandler, FiscalRateHandler>();
            services.AddScoped<FiscalStaffService>();
            services.AddScoped<GatewayPayments>();
            services.AddScoped<IJobHandler, WebhookVerifyHandler>();
            services.AddScoped<IJobHandler, PaymentSyncHandler>();
            services.AddScoped<IJobHandler, PaymentReconcileHandler>();
            services.AddScoped<IJobHandler, CheckoutSweepHandler>();
            services.AddScoped<IJobHandler, PaymentRefundHandler>();
            services.AddScoped<IJobHandler, DomainOperationHandler>();
            services.AddScoped<RegistrarQueries>();
            services.AddScoped<IJobHandler, DomainSyncHandler>();
            services.AddScoped<ReconcileService>();
            services.AddScoped<IJobHandler, ReconcileHandler>();

            // Cobros incluidos (sección 4 del diseño de cobros): medios guardados, débito automático, suscripciones,
            // asistente del panel y alta de los módulos de la receta.
            services.AddScoped<SavedMethodService>();
            services.AddScoped<AutoChargeService>();
            services.AddScoped<UseCases.SubscriptionService>();
            services.AddScoped<GatewaySetupService>();
            services.AddScoped<IncludedModules>();
            services.AddScoped<IJobHandler, MethodSyncHandler>();
            services.AddScoped<IJobHandler, MethodDeleteHandler>();
            services.AddScoped<IJobHandler, AutoChargeScanHandler>();
            services.AddScoped<IJobHandler, AutoChargePrepareHandler>();
            services.AddScoped<IJobHandler, AutoChargeHandler>();
            services.AddScoped<IJobHandler, SubscriptionSyncHandler>();
            services.AddScoped<IJobHandler, SubscriptionCancelHandler>();
            services.AddScoped<IJobHandler, SubscriptionAmountsHandler>();
            services.AddScoped<IJobHandler, SubscriptionUpdateHandler>();

            // Webhooks salientes: el secreto de cada destino va cifrado, así que mandan solo los nodos con llaves.
            services.AddSingleton<WebhookSender>();
            services.AddScoped<WebhookService>();
            services.AddScoped<IJobHandler, WebhookDeliveryHandler>();
            services.AddScoped<ExtensionService>();
            services.AddScoped<IJobHandler, ExtensionEventHandler>();

            // Evaluación de fraude de los pedidos por su módulo (diseno/fraude-y-acceso.md).
            services.AddScoped<IJobHandler, FraudScreenHandler>();

            // Lectura diaria del uso medido: llama al módulo del servicio.
            services.AddScoped<IJobHandler, UsageReadHandler>();
        }

        services.AddDbContext<BillingDbContext>(o => DesignTimeFactory.Configure((DbContextOptionsBuilder<BillingDbContext>)o, connectionString));
        services.AddOptions<BillingOptions>().Configure(o => configure?.Invoke(o));
        services.AddOptions<PaymentsOptions>();
        services.AddScoped<TransferService>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<JobQueue>();
        services.AddScoped<AuditLog>();
        services.AddScoped<BillingCore>();
        services.AddScoped<AccountService>();
        services.AddScoped<TaxSimulator>();
        services.AddScoped<TaxSetupService>();

        // Validación de números de IVA de la UE contra VIES. Las pruebas registran uno falso antes.
        services.TryAddSingleton<IVatNumberValidator>(sp => new ViesClient(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<ApiKeyService>();
        services.AddScoped<OrderService>();
        services.AddScoped<OrderValidation>();
        services.AddScoped<PaymentService>();
        services.AddScoped<WebhookInbox>();
        services.AddScoped<CreditService>();
        services.AddScoped<InvoiceService>();
        services.AddScoped<BillableItemService>();
        services.AddScoped<QuoteService>();
        services.AddScoped<ExchangeRateService>();
        services.TryAddSingleton(_ => new BnaRates(new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) }) { Timeout = TimeSpan.FromSeconds(30) }));
        services.AddScoped<BillingRunService>();
        services.AddScoped<DunningService>();
        services.AddScoped<ServiceChangeService>();
        services.AddScoped<CatalogService>();
        services.AddScoped<ServiceAdmin>();
        services.AddScoped<ManualModule>();
        services.AddScoped<StaffTaskService>();

        // Lee del contexto de réplicas (Failover.Replica), que registra AddBillingReplica.
        services.AddScoped<Reports.ReportService>();
        services.AddScoped<DomainService>();
        services.AddScoped<DomainOrderService>();
        services.AddScoped<TldPricing>();
        services.AddScoped<DomainSearch>();
        services.TryAddSingleton<DomainLookup>();
        services.AddScoped<IJobHandler, DomainSyncDayHandler>();
        services.AddScoped<IJobHandler, DomainNoticesHandler>();
        services.TryAddScoped<IServiceModules, NoServiceModules>();
        services.AddScoped<IJobHandler, ServiceOperationHandler>();
        services.AddScoped<IJobHandler, ActivateServicesOfInvoiceHandler>();
        services.AddScoped<IJobHandler, BillingRunHandler>();
        services.AddScoped<IJobHandler, BillingAccountHandler>();
        services.AddScoped<IJobHandler, DunningHandler>();
        services.AddScoped<IJobHandler, ReissueReleasedHandler>();
        services.AddScoped<IJobHandler, ChangeGapHandler>();
        services.AddScoped<IJobHandler, RetentionHandler>();
        services.AddScoped<IJobHandler, ExchangeRatesHandler>();

        // Uso medido (diseno/uso-medido.md). La lectura llama a los módulos: va con las llaves, arriba.
        services.AddScoped<UsageCatalog>();
        services.AddScoped<UsageBilling>();
        services.AddScoped<UsageAlerts>();
        services.AddScoped<UsageQueries>();
        services.AddScoped<IJobHandler, UsageSweepHandler>();
        services.AddScoped<IJobHandler, UsageCloseHandler>();

        // Programa de afiliados (diseno/afiliados.md).
        services.AddScoped<AffiliateService>();
        services.AddScoped<AffiliateCommissions>();
        services.AddScoped<IJobHandler, AffiliateOrderHandler>();
        services.AddScoped<IJobHandler, AffiliatePaidHandler>();
        services.AddScoped<IJobHandler, AffiliateReviewHandler>();
        services.AddScoped<IJobHandler, AffiliateReleaseHandler>();
        services.AddScoped<IJobHandler, AffiliateMonthlyHandler>();

        services.AddOptions<WorkerOptions>();
        services.AddSingleton<WriteGate>();
        services.AddSingleton<JobRunner>();

        // Avisos por correo (D-13) e identidad y trámites del consumidor (D-10): los correos de acceso y de trámites
        // también salen por la cola de notificaciones.
        services.AddNotifications();
        services.AddScoped<ConsumerRequestService>();

        // Actualizaciones (D-20): el panel lee lo que deja el actualizador y le deja los pedidos.
        services.AddScoped<UpdateService>();

        // Fraude y seguridad de acceso (diseno/fraude-y-acceso.md).
        services.AddScoped<SecuritySettingsStore>();
        services.AddScoped<AccessGuard>();
        services.AddScoped<FraudScreening>();
        services.AddScoped<FraudReviewService>();

        // Soporte (sección 9 del modelo de dominio): tickets, avisos y correo entrante.
        services.AddSupport(secrets is not null);

        // Base de conocimiento, novedades, descargas y estado de la red (diseno/contenidos.md).
        services.AddContent();

        // Registro y proveedores externos de los clientes (diseno/ingreso-clientes.md).
        services.AddScoped<ExternalLoginStore>();
        return services;
    }

    public static IServiceCollection AddBillingWorkers(this IServiceCollection services)
    {
        services.AddHostedService(sp => sp.GetRequiredService<JobRunner>());
        services.AddHostedService<Scheduler>();
        return services;
    }
}
