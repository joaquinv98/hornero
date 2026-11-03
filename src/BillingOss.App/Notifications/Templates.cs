using System.Text.Encodings.Web;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using Fluid;
using Fluid.Values;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BillingOss.App.Notifications;

public sealed record RenderedEmail(string Subject, string Html);

/// <summary>
/// Plantillas de los avisos, con el motor tipo Liquid común (<see cref="Liquid"/>, D-13). Reciben modelos planos
/// (diccionarios, listas y textos ya formateados, nunca objetos del dominio). Las de fábrica están en Templates/, una
/// por aviso e idioma, con el asunto en la primera línea. El proveedor las reemplaza desde la API.
/// </summary>
public sealed class TemplateRenderer(BillingDbContext db, ILogger<TemplateRenderer> logger)
{
    /// <summary>Cabecera y pie comunes. Recibe el aviso ya armado en "content".</summary>
    public const string Layout = "layout";

    public static IReadOnlyList<string> Cultures { get; } = ["es-AR", "en"];

    /// <summary>El catálogo de avisos. Cada uno tiene su plantilla en cada idioma.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        Layout,
        TemplateKeys.InvoiceIssued,
        TemplateKeys.InvoiceReminder,
        TemplateKeys.InvoiceOverdue,
        TemplateKeys.PaymentReceived,
        TemplateKeys.ServiceActivated,
        TemplateKeys.ServiceReactivated,
        TemplateKeys.ServiceSuspended,
        TemplateKeys.ServiceTerminated,
        TemplateKeys.FiscalDocument,
        TemplateKeys.PasswordReset,
        TemplateKeys.ConsumerCode,
        TemplateKeys.CancellationReceipt,
        TemplateKeys.WithdrawalReceipt,
        TemplateKeys.EmailVerification,
        TemplateKeys.NewLogin,
        TemplateKeys.TicketOpened,
        TemplateKeys.TicketReply,
        TemplateKeys.TicketStaffAlert,
        TemplateKeys.TicketEscalated,
        TemplateKeys.TicketFeedback,
        TemplateKeys.UsageThreshold,
        TemplateKeys.AffiliatePayoutRequested,
        TemplateKeys.AffiliateMonthly,
        TemplateKeys.StatusEvent,
        TemplateKeys.StatusConfirm,
        TemplateKeys.Quote,
        TemplateKeys.AutoChargeNotice,
        TemplateKeys.CardExpiring,
        TemplateKeys.SubscriptionCancelled,

        // Los avisos de dominios (DomainNotices.Template): vencimiento, gracia y lo que terminó en el registrar.
        .. Enum.GetValues<UseCases.DomainNotice>().Select(UseCases.DomainNotices.Template),
    ];

    private static readonly TemplateOptions Options = Liquid.Options();

    public static string CultureFor(string? language) =>
        language is not null && language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "es-AR";

    public static bool Exists(string key, string culture) => Keys.Contains(key) && Cultures.Contains(culture);

    /// <summary>La plantilla de fábrica.</summary>
    public static (string Subject, string Body) Default(string key, string culture)
    {
        using var stream = typeof(TemplateRenderer).Assembly.GetManifestResourceStream($"Templates.{key}.{culture}.liquid")
            ?? throw new KeyNotFoundException($"No existe la plantilla {key} en {culture}.");
        using var reader = new StreamReader(stream);
        var subject = key == Layout ? string.Empty : reader.ReadLine()?.Trim() ?? string.Empty;
        return (subject, reader.ReadToEnd());
    }

    /// <summary>Devuelve el error de sintaxis, o null si la plantilla se puede usar.</summary>
    public static string? Validate(string source) => Liquid.Validate(source);

    /// <summary>
    /// Arma el aviso con las plantillas del proveedor. Si una editada falla (sintaxis, topes), se usa la de fábrica:
    /// un error de edición no deja a nadie sin su aviso.
    /// </summary>
    public async Task<RenderedEmail> Render(string key, string culture, IReadOnlyDictionary<string, object?> model, CancellationToken ct = default)
    {
        var custom = await db.Set<Template>().AsNoTracking()
            .Where(t => t.Culture == culture && (t.Key == key || t.Key == Layout))
            .ToListAsync(ct).ConfigureAwait(false);
        var body = custom.Find(t => t.Key == key);
        var layout = custom.Find(t => t.Key == Layout);
        if (body is not null || layout is not null)
        {
            try
            {
                return await Render(
                    body is null ? Default(key, culture) : (body.Subject, body.Body),
                    layout is null ? Default(Layout, culture).Body : layout.Body,
                    model).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("La plantilla editada {Key} ({Culture}) falló y se usa la de fábrica: {Error}", key, culture, ex.Message);
            }
        }

        return await Render(Default(key, culture), Default(Layout, culture).Body, model).ConfigureAwait(false);
    }

    private static async Task<RenderedEmail> Render((string Subject, string Body) template, string layout, IReadOnlyDictionary<string, object?> model)
    {
        var context = new TemplateContext(Options);
        foreach (var (name, value) in model)
        {
            context.SetValue(name, FluidValue.Create(value, Options));
        }

        // El asunto es texto de un encabezado: sin codificar y en una sola línea.
        var subject = await Write(template.Subject, context, NullEncoder.Default).ConfigureAwait(false);
        subject = string.Join(' ', subject.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var content = await Write(template.Body, context, Liquid.Html).ConfigureAwait(false);
        context.SetValue("content", new StringValue(content, encode: false));
        var html = await Write(layout, context, Liquid.Html).ConfigureAwait(false);
        return new RenderedEmail(subject.Length > 300 ? subject[..300] : subject, html);
    }

    private static Task<string> Write(string source, TemplateContext context, TextEncoder encoder) =>
        Liquid.Render(Liquid.Parse(source), context, encoder);
}

/// <summary>Los avisos del catálogo (D-13). El nombre es el de la plantilla.</summary>
public static class TemplateKeys
{
    public const string InvoiceIssued = "invoice-issued";
    public const string InvoiceReminder = "invoice-reminder";
    public const string InvoiceOverdue = "invoice-overdue";
    public const string PaymentReceived = "payment-received";
    public const string ServiceActivated = "service-activated";
    public const string ServiceReactivated = "service-reactivated";
    public const string ServiceSuspended = "service-suspended";
    public const string ServiceTerminated = "service-terminated";
    public const string FiscalDocument = "fiscal-document";
    public const string PasswordReset = "password-reset";
    public const string ConsumerCode = "consumer-code";
    public const string CancellationReceipt = "cancellation-receipt";
    public const string WithdrawalReceipt = "withdrawal-receipt";
    public const string EmailVerification = "email-verification";
    public const string NewLogin = "new-login";
    public const string TicketOpened = "ticket-opened";
    public const string TicketReply = "ticket-reply";
    public const string TicketStaffAlert = "ticket-staff-alert";
    public const string TicketEscalated = "ticket-escalated";
    public const string TicketFeedback = "ticket-feedback";
    public const string UsageThreshold = "usage-threshold";

    /// <summary>Al equipo: un afiliado pidió un retiro (sección 3 de diseno/afiliados.md).</summary>
    public const string AffiliatePayoutRequested = "affiliate-payout-requested";

    /// <summary>Al afiliado: el resumen del mes anterior.</summary>
    public const string AffiliateMonthly = "affiliate-monthly";

    /// <summary>Aviso de un incidente o un mantenimiento de la red a quien se suscribió (diseno/contenidos.md, sección 10).</summary>
    public const string StatusEvent = "status-event";

    /// <summary>Enlace para confirmar la suscripción a los avisos de la red.</summary>
    public const string StatusConfirm = "status-confirm";
    public const string Quote = "quote";

    /// <summary>El débito automático no pudo cobrar y el cliente tiene que hacer algo: otro medio o confirmar el pago.</summary>
    public const string AutoChargeNotice = "autocharge-notice";

    /// <summary>La tarjeta del débito automático vence en 30 días o menos.</summary>
    public const string CardExpiring = "card-expiring";

    /// <summary>La pasarela dio de baja la suscripción del débito automático, por ejemplo después de varias cuotas rechazadas.</summary>
    public const string SubscriptionCancelled = "subscription-cancelled";
    public const string ExternalLink = "external-link";
}
