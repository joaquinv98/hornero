using BillingOss.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BillingOss.Importer.Import;

/// <summary>Una importación: la del comando import y la de cada día del paralelo.</summary>
public sealed record ImportOptions(string Dump)
{
    public string? Baseline { get; init; }

    public IReadOnlyDictionary<string, ModuleMapping>? Modules { get; init; }

    /// <summary>Todo en una transacción que se descarta.</summary>
    public bool Simulate { get; init; }

    /// <summary>Base que se borra y se crea de nuevo antes de importar (solo nombres billing_oss_*).</summary>
    public string? RecreateDb { get; init; }

    public IReadOnlyList<string> OwnZones { get; init; } = [];

    public bool ShadowInstances { get; init; }

    public DateOnly? AsOf { get; init; }

    /// <summary>La carpeta de adjuntos del soporte del sistema anterior, copiada de su servidor. Sin ella, entran sin el archivo.</summary>
    public string? SupportFiles { get; init; }
}

public static class ImportRun
{
    /// <summary>Sale con 0 si concilia, con 3 si hay diferencias, con 2 si las opciones no sirven y con 1 si la base no está vacía.</summary>
    public static async Task<int> Run(string connection, ImportOptions options, TextWriter output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        if (options.RecreateDb is { } target)
        {
            // Para repetir la importación hasta el corte: base nueva cada vez. Solo nombres propios del importador.
            if (!System.Text.RegularExpressions.Regex.IsMatch(target, "^billing_oss_[a-z0-9_]{1,40}$"))
            {
                await output.WriteLineAsync("--recreate-db solo acepta nombres que empiezan con billing_oss_.").ConfigureAwait(false);
                return 2;
            }

            await using (var admin = new NpgsqlConnection(connection))
            {
                await admin.OpenAsync(ct).ConfigureAwait(false);
                foreach (var sql in new[] { $"DROP DATABASE IF EXISTS {target} WITH (FORCE)", $"CREATE DATABASE {target}" })
                {
                    await using var cmd = new NpgsqlCommand(sql, admin) { CommandTimeout = 300 };
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            connection = new NpgsqlConnectionStringBuilder(connection) { Database = target }.ConnectionString;
            await output.WriteLineAsync($"Base destino recreada: {target}").ConfigureAwait(false);
        }

        try
        {
            var builder = DesignTimeFactory.Configure(new DbContextOptionsBuilder<BillingDbContext>(), connection);
            await using var db = new BillingDbContext(builder.Options);
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
            if (await db.Accounts.AnyAsync(ct).ConfigureAwait(false))
            {
                await output.WriteLineAsync("La base destino no está vacía: el importador corre sobre una base nueva.").ConfigureAwait(false);
                return 1;
            }

            var started = DateTime.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var import = new SourceImport(db, output, options.Modules)
            {
                OwnZones = [.. options.OwnZones.Select(z => z.ToLowerInvariant())],
                ShadowInstances = options.ShadowInstances,
                AsOf = options.AsOf ?? SourceImport.Today(),
                SupportFiles = options.SupportFiles,
            };
            await using (var dump = DumpReader.Open(options.Dump))
            {
                await import.Load(dump, ct).ConfigureAwait(false);
            }

            await import.Run(ct).ConfigureAwait(false);
            await output.WriteLineAsync($"Incidencias: {import.Incidents.Count}").ConfigureAwait(false);
            foreach (var incident in import.Incidents.Take(40))
            {
                await output.WriteLineAsync($"  {incident}").ConfigureAwait(false);
            }

            var diffs = options.Baseline is null ? 0 : await Reconciliation.Compare(import, options.Baseline, output).ConfigureAwait(false);
            if (options.Simulate)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                await output.WriteLineAsync($"Simulación en {(DateTime.UtcNow - started).TotalSeconds:0} s: no se guardó nada.").ConfigureAwait(false);
            }
            else
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
                await output.WriteLineAsync($"Importación guardada en {(DateTime.UtcNow - started).TotalSeconds:0} s.").ConfigureAwait(false);
            }

            return diffs == 0 ? 0 : 3;
        }
        finally
        {
            // Sin sesiones abiertas: la base importada es la plantilla de la de trabajo del paralelo.
            await using var pooled = new NpgsqlConnection(connection);
            NpgsqlConnection.ClearPool(pooled);
        }
    }
}
