using System.Globalization;
using System.Text.RegularExpressions;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BillingOss.App.Tests;

/// <summary>
/// Base de prueba propia por prueba, creada y borrada sobre el PostgreSQL de integración. La cadena de conexión
/// viene de BILLINGOSS_TEST_PG. Sin esa variable, las pruebas de integración se saltean.
/// El nombre lleva la hora de creación en UTC: la primera prueba de cada corrida borra las bases de más de dos horas
/// que dejó una corrida cortada, salvo las que tienen sesiones abiertas.
/// Las conexiones van con pool, como en producción: sin pool cada una es un proceso nuevo del servidor, y con
/// muchas pruebas a la vez, crear tantos procesos puede tumbar a un PostgreSQL local.
/// </summary>
internal static partial class TestDatabase
{
    private static readonly Lazy<Task> Sweep = new(SweepOrphans);

    /// <summary>
    /// Plazo de CREATE y DROP DATABASE. Con el servidor cargado por otras suites pasan los 30 segundos por defecto: DROP
    /// fuerza un checkpoint, y un plazo vencido cancela el comando y deja la base huérfana.
    /// </summary>
    private const int Slow = 300;

    /// <summary>La cadena de BILLINGOSS_TEST_PG, con un minuto para conectar: con muchas suites a la vez el servidor tarda en atender.</summary>
    public static string? AdminConnectionString =>
        Environment.GetEnvironmentVariable("BILLINGOSS_TEST_PG") is { } admin ? new NpgsqlConnectionStringBuilder(admin) { Timeout = 60 }.ConnectionString : null;

    public static string RequireAdmin() =>
        AdminConnectionString ?? throw new AssertInconclusiveException("Falta BILLINGOSS_TEST_PG: se saltean las pruebas de integración.");

    /// <summary>billing_oss_t_aaMMddHHmm_xxxxxxxx: la hora de creación en UTC y ocho hexadecimales al azar.</summary>
    public static string NewName() => $"billing_oss_t_{DateTime.UtcNow:yyMMddHHmm}_{Guid.NewGuid():N}"[..33];

    /// <summary>
    /// Plantilla de la CI (A8 de D-20): la versión anterior corre sus pruebas sobre bases que nacen con el esquema que
    /// dejaron las migraciones de la versión nueva. Los arneses que migran la usan. Sin la variable, base vacía.
    /// </summary>
    public static string? Template => Environment.GetEnvironmentVariable("BILLINGOSS_TEST_TEMPLATE");

    /// <summary>Crea una base, vacía o copia de <paramref name="template"/>, y devuelve su cadena de conexión y una acción para borrarla.</summary>
    /// <param name="pooling">Sin pool solo las pruebas que necesitan sesiones nuevas a propósito: las que pasan la base a
    /// solo lectura con ALTER DATABASE, que vale recién para las sesiones que se abren después.</param>
    /// <param name="template">Una base sin sesiones abiertas: PostgreSQL no copia una plantilla en uso.</param>
    public static async Task<(string ConnectionString, Func<Task> Drop)> Create(bool pooling = true, string? template = null)
    {
        if (template is not null && !TemplateName().IsMatch(template))
        {
            throw new ArgumentException($"Plantilla inválida: «{template}».", nameof(template));
        }

        var admin = new NpgsqlConnectionStringBuilder(RequireAdmin());
        await Sweep.Value;
        var name = NewName();
        await using (var db = new NpgsqlConnection(admin.ConnectionString))
        {
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}{(template is null ? string.Empty : " TEMPLATE " + template)}", db) { CommandTimeout = Slow };
            await cmd.ExecuteNonQueryAsync();
        }

        // El plazo de los comandos es el mínimo de producción (Failover.MainConnection), que el núcleo no recibe acá.
        var test = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = name, Pooling = pooling, CommandTimeout = Failover.MinCommandTimeout };
        return (test.ConnectionString, () => Drop(admin.ConnectionString, test.ConnectionString));
    }

    /// <summary>Cierra las conexiones del pool de la base y la borra.</summary>
    public static async Task Drop(string admin, string connectionString)
    {
        await using (var test = new NpgsqlConnection(connectionString))
        {
            NpgsqlConnection.ClearPool(test);
        }

        // FORCE corta las sesiones y espera unos segundos a que terminen: con la PC cargada a veces no llegan y la base
        // sigue "en uso". Se reintenta, y si no, queda para el barrido de una corrida posterior.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var db = new NpgsqlConnection(admin);
                await db.OpenAsync();
                await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {new NpgsqlConnectionStringBuilder(connectionString).Database} WITH (FORCE)", db) { CommandTimeout = Slow };
                await cmd.ExecuteNonQueryAsync();
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ObjectInUse && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ObjectInUse)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Corre hacia atrás el vencimiento de un documento emitido, salteando la traba que lo congela: arma una factura
    /// vencida en las pruebas que corren con el reloj real.
    /// </summary>
    public static Task<int> MakeOverdue(BillingDbContext db, long documentId, int days) =>
        db.Database.ExecuteSqlRawAsync(string.Create(CultureInfo.InvariantCulture,
            $"SET session_replication_role = replica; UPDATE billing.documents SET due_date = due_date - {days} WHERE id = {documentId}; SET session_replication_role = origin;"));

    /// <summary>Lo comparten todas las pruebas de la corrida: si falla, ninguna lo ve y lo intenta la próxima corrida.</summary>
    private static async Task SweepOrphans()
    {
        try
        {
            await DropOrphans();
        }
        catch (NpgsqlException)
        {
            // La base no respondió a tiempo: cada prueba lo va a ver al crear la suya.
        }
    }

    private static async Task DropOrphans()
    {
        var limit = DateTime.UtcNow.AddHours(-2);
        await using var db = new NpgsqlConnection(RequireAdmin());
        await db.OpenAsync();
        var orphans = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT datname FROM pg_database WHERE datname ~ '^billing_oss_t_[0-9]{10}_[0-9a-f]{8}$'", db))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                var created = DateTime.ParseExact(name[14..24], "yyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                if (created < limit)
                {
                    orphans.Add(name);
                }
            }
        }

        // El barrido es una limpieza de cortesía y las pruebas lo esperan: con la PC cargada cada borrado tarda, así que
        // corre un minuto como mucho y lo que quede sigue en la próxima corrida. Nunca hace fallar una prueba.
        var until = DateTime.UtcNow.AddMinutes(1);
        foreach (var name in orphans.TakeWhile(_ => DateTime.UtcNow < until))
        {
            try
            {
                // Sin FORCE: una base con sesiones abiertas es de una corrida viva y queda.
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name}", db) { CommandTimeout = 60 };
                await drop.ExecuteNonQueryAsync();
            }
            catch (PostgresException)
            {
                // En uso o borrada por otra corrida a la vez.
            }
            catch (NpgsqlException)
            {
                // Servidor lento o conexión cortada: el resto queda para el barrido siguiente, sin trabar la corrida.
                return;
            }
        }
    }

    [GeneratedRegex(@"^[a-z_][a-z0-9_]{0,62}\z")]
    private static partial Regex TemplateName();
}

[TestClass]
public sealed class DatabaseConnectionTests
{
    [TestMethod]
    public async Task LaBaseDeIntegracionEsPostgreSql17OPosterior()
    {
        await using var db = new NpgsqlConnection(TestDatabase.RequireAdmin());
        await db.OpenAsync();

        Assert.IsGreaterThanOrEqualTo(17, db.PostgreSqlVersion.Major);
    }

    [TestMethod]
    public void ElNombreDeLaBaseLlevaLaHoraDeCreacion()
    {
        var name = TestDatabase.NewName();

        StringAssert.Matches(name, new System.Text.RegularExpressions.Regex("^billing_oss_t_[0-9]{10}_[0-9a-f]{8}$"));
        var created = DateTime.ParseExact(name[14..24], "yyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        Assert.IsLessThan(TimeSpan.FromMinutes(2), (DateTime.UtcNow - created).Duration());
    }
}
