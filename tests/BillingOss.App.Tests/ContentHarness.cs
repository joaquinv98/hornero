using BillingOss.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BillingOss.App.Tests;

/// <summary>
/// Una base de prueba compartida por todas las pruebas de una clase de contenidos. Crear una base cuesta casi un minuto
/// con la PC cargada: cada clase crea la suya una vez, y cada prueba arranca vaciando las tablas del módulo. Las clases
/// que la usan corren sus pruebas de a una.
/// </summary>
internal static class ContentHarness
{
    /// <summary>Crea la base de la clase. Con el servidor saturado el CREATE DATABASE puede vencer: se reintenta dos veces.</summary>
    public static async Task<CoreHarness> Start(Action<Notifications.NotificationOptions>? notifications = null, Action<BillingOss.App.Content.ContentOptions>? content = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Con pool: con la PC cargada, abrir una conexión nueva por consulta vence el tiempo de conexión.
                return await CoreHarness.Start(
                    pooling: true,
                    notifications: notifications,
                    services: content is null ? null : s => s.Configure(content));
            }
            catch (Exception ex) when (attempt < 3 && ex is NpgsqlException or TimeoutException)
            {
                await Task.Delay(TimeSpan.FromSeconds(10 * attempt));
            }
        }
    }

    /// <summary>Cierra la base de la clase, si llegó a crearse.</summary>
    public static async Task Stop(CoreHarness? h)
    {
        if (h is null)
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await h.DisposeAsync();
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                // Con el servidor saturado el DROP puede vencer. No es una falla de la prueba: si no se logra, la base huérfana
                // la barre la primera prueba de la próxima corrida (TestDatabase).
                if (attempt == 3)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
            }
        }
    }

    /// <summary>Vacía lo que escriben los contenidos y los avisos, y deja las cuentas y el catálogo sembrados.</summary>
    public static Task Reset(this CoreHarness h)
    {
        ArgumentNullException.ThrowIfNull(h);
        h.Clock.Now = new DateTimeOffset(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);
        return h.Db(db => db.Database.ExecuteSqlRawAsync("""
            TRUNCATE content.articles, content.categories, content.announcements, content.downloads, content.components,
                     content.status_events, content.subscribers, platform.jobs, notifications.outbound_emails RESTART IDENTITY CASCADE
            """));
    }
}
