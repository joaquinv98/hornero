using BillingOss.App.Platform;
using Npgsql;

namespace BillingOss.Importer.Tests;

/// <summary>
/// Base de prueba propia sobre BILLINGOSS_TEST_PG, con el mismo formato de nombre que en App.Tests: la hora de creación
/// deja que el barrido de esa suite borre la base si la corrida se corta. Las conexiones van con pool, como en
/// producción: sin pool cada una es un proceso nuevo del servidor, y el importador abre cientos.
/// </summary>
internal static class TestDatabase
{
    /// <summary>La cadena de BILLINGOSS_TEST_PG, con un minuto para conectar: con muchas suites a la vez el servidor tarda en atender.</summary>
    public static string Admin => new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("BILLINGOSS_TEST_PG")
        ?? throw new AssertInconclusiveException("Falta BILLINGOSS_TEST_PG: se saltean las pruebas de integración.")) { Timeout = 60 }.ConnectionString;

    /// <summary>Crea una base vacía y devuelve su cadena de conexión.</summary>
    public static async Task<string> Create()
    {
        var name = $"billing_oss_t_{DateTime.UtcNow:yyMMddHHmm}_{Guid.NewGuid():N}"[..33];
        await Execute(Admin, $"CREATE DATABASE {name}");
        // El plazo de los comandos es el mínimo de producción (Failover.MainConnection).
        return new NpgsqlConnectionStringBuilder(Admin) { Database = name, CommandTimeout = Failover.MinCommandTimeout }.ConnectionString;
    }

    /// <summary>La cadena de otra base del mismo servidor, como la de trabajo del paralelo.</summary>
    public static string Other(string database) => new NpgsqlConnectionStringBuilder(Admin) { Database = database }.ConnectionString;

    /// <summary>
    /// Cierra las conexiones del pool de la base y la borra. DROP DATABASE fuerza un checkpoint y con el servidor cargado
    /// por otras suites tarda más que los 30 segundos por defecto: se le dan cinco minutos.
    /// </summary>
    public static async Task Drop(string connectionString)
    {
        await ClosePool(connectionString);
        await Execute(Admin, $"DROP DATABASE IF EXISTS {new NpgsqlConnectionStringBuilder(connectionString).Database} WITH (FORCE)");
    }

    /// <summary>Cierra las conexiones del pool de la base, como pide DROP DATABASE o usarla de plantilla.</summary>
    public static async Task ClosePool(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(connection);
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync();
    }
}
