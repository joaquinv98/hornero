namespace BillingOss.App.Identity;

public enum ConsumerRequestKind
{
    /// <summary>Botón de baja de servicio (Disposición 954/2025, art. 4).</summary>
    Cancellation,

    /// <summary>Botón de arrepentimiento (Disposición 954/2025, art. 1, y Ley 24.240).</summary>
    Withdrawal,
}

/// <summary>
/// Correos de acceso y de trámites del consumidor. La interfaz la arma el portal y la implementa el módulo de
/// notificaciones, que los pone en la cola de salida con reintentos (sección 9 del modelo de dominio). Los enlaces y
/// los códigos son secretos de vida corta: la implementación no los loguea.
/// </summary>
public interface IAccountMail
{
    /// <summary>Enlace para elegir una contraseña nueva. Vence en una hora.</summary>
    Task SendPasswordReset(string email, string language, Uri link, CancellationToken ct);

    /// <summary>Código de 6 dígitos que verifica que quien pide la baja o el arrepentimiento recibe el correo de la cuenta.</summary>
    Task SendConsumerCode(string email, string language, ConsumerRequestKind kind, string code, CancellationToken ct);

    /// <summary>Constancia con el código de registración: tiene que salir dentro de las 24 horas (Disposición 954/2025, art. 5).</summary>
    Task SendConsumerReceipt(string email, string language, ConsumerRequestKind kind, string receipt, DateTimeOffset at, CancellationToken ct);

    /// <summary>Enlace para verificar el correo del cliente. Vence en 24 horas.</summary>
    Task SendEmailVerification(string email, string language, Uri link, CancellationToken ct);

    /// <summary>Aviso de un ingreso desde una red nueva, con el enlace a la página de seguridad si hay URL pública.</summary>
    Task SendNewLogin(string email, string language, DateTimeOffset at, string ip, string device, Uri? link, CancellationToken ct);

    /// <summary>
    /// Enlace para confirmar el vínculo de un proveedor externo, como Google, con el usuario (diseno/ingreso-clientes.md,
    /// sección 5). Vence en una hora.
    /// </summary>
    Task SendExternalLink(string email, string language, string provider, Uri link, CancellationToken ct);
}
