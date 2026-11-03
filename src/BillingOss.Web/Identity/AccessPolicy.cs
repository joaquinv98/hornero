using BillingOss.App.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace BillingOss.Web
{
    /// <summary>Marca de los textos de fraude y seguridad de acceso: Resources/SecurityTexts.resx (es-AR) y SecurityTexts.en.resx.</summary>
    public sealed class SecurityTexts;

    /// <summary>Un texto que puede estar en cualquiera de los dos catálogos de la interfaz.</summary>
    public static class UiText
    {
        public static LocalizedString Get(IStringLocalizer<Texts> texts, IStringLocalizer<SecurityTexts> security, string key, params object[] args)
        {
            ArgumentNullException.ThrowIfNull(texts);
            ArgumentNullException.ThrowIfNull(security);
            return security[key, args] is { ResourceNotFound: false } found ? found : texts[key, args];
        }

        /// <summary>Fecha y hora de un instante en la zona del proveedor, con la cultura del pedido.</summary>
        public static string When(DateTimeOffset at, string timeZone) =>
            TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).ToString("g", System.Globalization.CultureInfo.CurrentCulture);

        /// <summary>El tono del estado de un intento de ingreso (sección 4.4 del sistema visual).</summary>
        public static string Tone(App.Identity.LoginResult result) => result switch
        {
            App.Identity.LoginResult.Success => "ok",
            App.Identity.LoginResult.Locked or App.Identity.LoginResult.BlockedIp or App.Identity.LoginResult.Disabled => "danger",
            _ => "warn",
        };
    }
}

namespace BillingOss.Web.Identity
{
    /// <summary>
    /// Política de contraseñas con los ajustes del panel (diseno/fraude-y-acceso.md, sección 7). Vale al crear el usuario,
    /// al restablecer y al cambiar la contraseña. Los códigos de error empiezan con "Password", como los de Identity.
    /// </summary>
    public sealed class PasswordPolicy<TUser>(SecuritySettingsStore settings, IConfiguration config) : IPasswordValidator<TUser>
        where TUser : LoginUser
    {
        public async Task<IdentityResult> ValidateAsync(UserManager<TUser> manager, TUser user, string? password)
        {
            ArgumentNullException.ThrowIfNull(user);
            var problems = PasswordRules.Problems(await settings.Get().ConfigureAwait(false), password, user.Email, config["Ui:Brand"], user is StaffUser);
            return problems.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. problems.Select(p => new IdentityError { Code = p, Description = p })]);
        }
    }

    public sealed class EmailConfirmationTokenOptions : DataProtectionTokenProviderOptions
    {
        public const string ProviderName = "email-confirmation";

        public EmailConfirmationTokenOptions()
        {
            Name = ProviderName;
            TokenLifespan = TimeSpan.FromHours(24);
        }
    }

    /// <summary>
    /// Enlace para verificar el correo: Data Protection (anillo de llaves en la base) con 24 horas de vida, atado al
    /// sello del usuario. Es aparte del de recuperar la contraseña, que vence en una hora.
    /// </summary>
    public sealed class EmailConfirmationTokenProvider<TUser>(IDataProtectionProvider protection, IOptions<EmailConfirmationTokenOptions> options, ILogger<DataProtectorTokenProvider<TUser>> logger)
        : DataProtectorTokenProvider<TUser>(protection, Microsoft.Extensions.Options.Options.Create<DataProtectionTokenProviderOptions>(options.Value), logger)
        where TUser : class;

    /// <summary>
    /// Segundo factor obligatorio por rol (sección 8). Es un requisito de la política del portal, así vale para toda
    /// página o ruta que use su sesión, también las que no son páginas como el PDF de una factura. Mientras no lo active,
    /// quien lo tiene obligatorio solo entra a la página de seguridad y a salir. No aplica a "ver como cliente".
    /// </summary>
    public sealed class TwoFactorEnrolled : IAuthorizationRequirement
    {
        public const string SecurityPath = "/cuenta/seguridad";

        public const string LogoutPath = "/salir";
    }

    public sealed class TwoFactorEnrolledHandler(AccessGuard guard) : AuthorizationHandler<TwoFactorEnrolled>
    {
        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, TwoFactorEnrolled requirement)
        {
            ArgumentNullException.ThrowIfNull(context);
            var http = context.Resource as HttpContext;
            var exempt = http is not null && (http.Request.Path.StartsWithSegments(TwoFactorEnrolled.SecurityPath) || http.Request.Path.StartsWithSegments(TwoFactorEnrolled.LogoutPath));
            if (exempt || context.User.ActingAccount() is not null || context.User.UserId() is not { } id
                || !await guard.TwoFactorPending(id, http?.RequestAborted ?? CancellationToken.None).ConfigureAwait(false))
            {
                context.Succeed(requirement);
            }
        }
    }

    /// <summary>Quien tiene el segundo factor pendiente va a la página de seguridad, no a la de error.</summary>
    public sealed class TwoFactorRedirect : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler fallback = new();

        public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(authorizeResult);
            if (authorizeResult.Forbidden && authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<TwoFactorEnrolled>().Any() == true)
            {
                context.Response.Redirect(TwoFactorEnrolled.SecurityPath);
                return Task.CompletedTask;
            }

            return fallback.HandleAsync(next, context, policy, authorizeResult);
        }
    }
}
