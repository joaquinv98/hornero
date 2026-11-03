using System.Globalization;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using Microsoft.AspNetCore.Identity;

namespace BillingOss.Web.Identity;

/// <summary>
/// Alta del staff por línea de comandos: el staff no se importa y alguien tiene que crear el primero.
/// <code>
/// staff add correo "Nombre" permiso1,permiso2
/// staff oidc correo sub
/// staff disable correo
/// staff reset-2fa correo
/// </code>
/// La contraseña inicial se muestra una sola vez. El segundo factor se enrola en el primer ingreso.
/// </summary>
public static class StaffCommand
{
    public static async Task<int> Run(WebApplication app, string[] args)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(args);
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        switch (args)
        {
            case ["add", var email, var name, .. var rest]:
                var permissions = rest.Length == 0 ? [] : rest[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (permissions.Except(StaffPermissions.All).FirstOrDefault() is { } unknown)
                {
                    return Fail($"Permiso desconocido: {unknown}. Los válidos son {string.Join(", ", StaffPermissions.All)}.");
                }

                // Cumple la política de los ajustes: largo del staff y composición si está prendida.
                var password = PasswordRules.Generate(await scope.ServiceProvider.GetRequiredService<SecuritySettingsStore>().Get().ConfigureAwait(false), staff: true);
                var user = new StaffUser { Email = email, Name = name, Permissions = permissions, CreatedAt = DateTimeOffset.UtcNow };
                var created = await users.CreateAsync(user, password).ConfigureAwait(false);
                if (!created.Succeeded)
                {
                    return Fail(string.Join(" ", created.Errors.Select(e => e.Description)));
                }

                await Console.Out.WriteLineAsync($"Usuario {email} creado. Contraseña inicial, se muestra una sola vez: {password}").ConfigureAwait(false);
                return 0;

            case ["oidc", var email, var subject]:
                return await Change(users, email, u => u.OidcSubject = subject).ConfigureAwait(false);

            case ["disable", var email]:
                return await Change(users, email, u => u.DisabledAt = DateTimeOffset.UtcNow).ConfigureAwait(false);

            // Perdió el teléfono y los códigos: en el próximo ingreso enrola un TOTP nuevo, como el primer día. Queda en la
            // auditoría con el comando como actor.
            case ["reset-2fa", var email]:
                var reset = await Change(users, email, u =>
                {
                    u.AuthenticatorKey = null;
                    u.RecoveryCodes = null;
                    u.TwoFactorEnabled = false;
                }).ConfigureAwait(false);
                if (reset == 0 && await users.FindByEmailAsync(email).ConfigureAwait(false) is { } target)
                {
                    await scope.ServiceProvider.GetRequiredService<AuditLog>().Record("staff.2fa_reset", string.Create(CultureInfo.InvariantCulture, $"staff:{target.Id}")).ConfigureAwait(false);
                }

                return reset;

            default:
                return Fail("Uso: staff add <correo> <nombre> [permisos], staff oidc <correo> <sub>, staff disable <correo> o staff reset-2fa <correo>.");
        }
    }

    private static async Task<int> Change(UserManager<StaffUser> users, string email, Action<StaffUser> change)
    {
        var user = await users.FindByEmailAsync(email).ConfigureAwait(false);
        if (user is null)
        {
            return Fail($"No existe el usuario {email}.");
        }

        change(user);
        await users.UpdateSecurityStampAsync(user).ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Usuario {email} actualizado.").ConfigureAwait(false);
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
