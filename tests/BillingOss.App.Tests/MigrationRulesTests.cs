using System.Text.RegularExpressions;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace BillingOss.App.Tests;

/// <summary>
/// Disciplina de migraciones (A8 de D-20, sección 7.4): la versión anterior funciona con el esquema nuevo. Lo que quita o
/// endurece algo va en una minor y declara la versión más vieja del núcleo que sigue soportada, los índices que no
/// bloquean se pueden repetir, y la CI corre las pruebas de la versión anterior sobre el esquema nuevo.
/// </summary>
[TestClass]
public sealed partial class MigrationRulesTests
{
    /// <summary>La primera migración escrita con estas reglas: las anteriores son de antes de que existiera un proveedor.</summary>
    private const string Since = "20261101151103_Actualizaciones";

    [TestMethod]
    public void LasMigracionesQueQuitanAlgo_DeclaranLaVersionMinimaDelNucleo()
    {
        using var db = new BillingDbContext(DesignTimeFactory.Configure(new DbContextOptionsBuilder<BillingDbContext>(), "Host=localhost;Database=billing_oss").Options);
        var assembly = db.GetService<IMigrationsAssembly>();
        var migrations = assembly.Migrations
            .Where(m => string.CompareOrdinal(m.Key, Since) >= 0)
            .Select(m => (m.Key, (IReadOnlyList<MigrationOperation>)assembly.CreateMigration(m.Value, db.Database.ProviderName!).UpOperations))
            .ToList();

        Assert.IsNotEmpty(migrations);
        var violations = Violations(migrations);
        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void BorrarUnaColumnaSinDeclararLaVersionMinima_HaceFallarLaCi()
    {
        foreach (var destructive in (Action<MigrationBuilder>[])[
            b => b.DropColumn("vieja", "invoices", "billing"),
            b => b.DropTable("viejas", "billing"),
            b => b.RenameColumn("vieja", "invoices", "nueva", "billing"),
            b => b.AlterColumn<string>("nota", "invoices", "text", nullable: false, schema: "billing", oldClrType: typeof(string), oldNullable: true),
            b => b.Sql("ALTER TABLE billing.invoices DROP COLUMN vieja"),

            // Endurecer una tabla que la versión anterior ya usa también la rompe: lo que escribe deja de entrar.
            b => b.AddColumn<string>("nueva", "invoices", "text", nullable: false, schema: "billing"),
            b => b.AddCheckConstraint("ck_invoices_total", "invoices", "total >= 0", "billing"),
            b => b.AddUniqueConstraint("ux_invoices_number", "invoices", "number", "billing"),
            b => b.AddPrimaryKey("pk_viejas", "viejas", "id", "billing"),
            b => b.AddForeignKey("fk_invoices_account", "invoices", "account_id", "accounts", "billing", principalSchema: "billing", principalColumn: "id"),

            // Sobre una columna nueva con valor por defecto también: lo que inserta la anterior lleva ese valor y se controla.
            b =>
            {
                b.AddColumn<long>("cuenta", "invoices", nullable: false, defaultValue: 0L, schema: "billing");
                b.AddForeignKey("fk_invoices_cuenta", "invoices", "cuenta", "accounts", "billing", principalSchema: "billing", principalColumn: "id");
            },
            b => b.CreateIndex("ux_invoices_number", "invoices", "number", "billing", unique: true),
            b => b.Sql("ALTER TABLE billing.invoices ADD CONSTRAINT ck_invoices_total CHECK (total >= 0)"),
            b => b.Sql("CREATE UNIQUE INDEX ux_invoices_number ON billing.invoices (number)"),
            b => b.Sql("ALTER TABLE billing.invoices ADD COLUMN nueva text NOT NULL"),
            b => b.Sql("ALTER TABLE billing.invoices ADD COLUMN total numeric(18,2) NOT NULL"),
            b => b.Sql("ALTER TABLE billing.invoices ADD COLUMN nota text, ADD COLUMN nueva text NOT NULL"),
            b => b.Sql("ALTER TABLE billing.invoices ADD UNIQUE (number)"),
        ])
        {
            var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
            destructive(builder);
            Assert.HasCount(1, Violations([("20270201000000_Minor", builder.Operations)]), builder.Operations[0].GetType().Name);

            builder.RequireCoreVersion("0.5.0");
            Assert.IsEmpty(Violations([("20270201000000_Minor", builder.Operations)]));
        }

        // Agregar no pide nada: es lo que va en un parche. Una tabla nueva se endurece como se quiera, la anterior no la usa.
        var additive = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        additive.AddColumn<string>("nueva", "invoices", "text", nullable: true, schema: "billing");
        additive.AddColumn<string>("con_valor", "invoices", "text", nullable: false, defaultValue: "x", schema: "billing");
        additive.AddColumn<DateTimeOffset>("desde", "invoices", nullable: false, defaultValueSql: "now()", schema: "billing");
        additive.Sql("ALTER TABLE billing.invoices ADD COLUMN con_valor_sql text NOT NULL DEFAULT 'x'");
        additive.Sql("ALTER TABLE billing.invoices ADD COLUMN monto numeric(18,2) NOT NULL DEFAULT 0, ADD COLUMN aclaracion text");
        additive.CreateIndexConcurrently("ix_invoices_nueva", "billing.invoices", "(nueva)");
        additive.CreateTable("nuevas", t => new { id = t.Column<long>(nullable: false), invoice_id = t.Column<long>(nullable: false) }, "billing", t => t.PrimaryKey("pk_nuevas", x => x.id));
        additive.CreateIndex("ux_nuevas_invoice", "nuevas", "invoice_id", "billing", unique: true);
        additive.AddForeignKey("fk_nuevas_invoice", "nuevas", "invoice_id", "invoices", "billing", principalSchema: "billing", principalColumn: "id");
        additive.AddColumn<long>("nueva_id", "invoices", nullable: true, schema: "billing");
        additive.AddForeignKey("fk_invoices_nueva", "invoices", "nueva_id", "nuevas", "billing", principalSchema: "billing", principalColumn: "id");
        additive.Sql("CREATE TABLE billing.otras (id int NOT NULL)");
        additive.Sql("CREATE UNIQUE INDEX ux_otras_id ON billing.otras (id)");
        additive.Sql("ALTER TABLE billing.otras ADD CONSTRAINT ck_otras_id CHECK (id > 0)");
        Assert.IsEmpty(Violations([("20270201000000_Parche", additive.Operations)]));
    }

    [TestMethod]
    public async Task UnIndiceConcurrentlyQueFalloAMitad_SeRehaceEnteroAlReintentar()
    {
        await using var core = await CoreHarness.Start();
        await UpdatesTests.Owner(core, "CREATE TABLE public.prueba_indice (n int)");
        await UpdatesTests.Owner(core, "INSERT INTO public.prueba_indice VALUES (1), (1)");

        // Un intento anterior que falló a mitad deja un índice inválido: un IF NOT EXISTS lo daría por bueno.
        await Assert.ThrowsExactlyAsync<PostgresException>(() => UpdatesTests.Owner(core, "CREATE UNIQUE INDEX CONCURRENTLY ix_prueba_indice_n ON public.prueba_indice (n)"));
        Assert.IsFalse(await Valid(core, "ix_prueba_indice_n"));

        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        builder.CreateIndexConcurrently("ix_prueba_indice_n", "public.prueba_indice", "(n)");
        var operations = builder.Operations.Cast<SqlOperation>().ToList();
        Assert.IsTrue(operations.All(o => o.SuppressTransaction), "Va fuera de la transacción de la migración.");
        foreach (var attempt in Enumerable.Range(0, 2))
        {
            foreach (var operation in operations)
            {
                await UpdatesTests.Owner(core, operation.Sql);
            }

            Assert.IsTrue(await Valid(core, "ix_prueba_indice_n"), $"intento {attempt}");
        }
    }

    [TestMethod]
    public async Task ConLaPlantillaDeLaCi_LasBasesNacenConElEsquemaNuevo_YMigrarNoHaceNada()
    {
        // Sin pool: PostgreSQL no copia una plantilla con sesiones abiertas, y el pool las dejaría.
        var (template, dropTemplate) = await TestDatabase.Create(pooling: false);
        try
        {
            await using (var db = Context(template))
            {
                await db.Database.MigrateAsync();
            }

            // Algo que solo trae la versión nueva.
            await using (var connection = new NpgsqlConnection(template))
            {
                await connection.OpenAsync();
                await using var cmd = new NpgsqlCommand("CREATE TABLE public.solo_en_la_version_nueva (id int)", connection);
                await cmd.ExecuteNonQueryAsync();
            }

            // Nadie quedó conectado a la plantilla.
            var (copy, dropCopy) = await TestDatabase.Create(template: new NpgsqlConnectionStringBuilder(template).Database);
            try
            {
                await using var db = Context(copy);
                Assert.IsEmpty(await db.Database.GetPendingMigrationsAsync());
                Assert.AreEqual(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_tables WHERE tablename = 'solo_en_la_version_nueva'").SingleAsync());
            }
            finally
            {
                await dropCopy();
            }

            await Assert.ThrowsExactlyAsync<ArgumentException>(() => TestDatabase.Create(template: "x; DROP DATABASE postgres"));
        }
        finally
        {
            await dropTemplate();
        }
    }

    /// <summary>
    /// Lo que quita o endurece algo del esquema que la versión anterior ya usa: borrar o renombrar tablas, columnas o
    /// esquemas, pasar una columna a NOT NULL o cambiarle el tipo, agregar una columna NOT NULL sin valor por defecto, y
    /// sumar una restricción, una clave o un índice único a una tabla que ya existía. Una migración así es de una minor y
    /// anota con RequireCoreVersion desde qué versión del núcleo vale, así un nodo con un binario anterior no arranca
    /// (SchemaGate). Lo que endurece una tabla que crea la misma migración no cuenta: la anterior no la usa. Tampoco una
    /// clave foránea sobre columnas que la misma migración agrega nulas y sin valor por defecto: la anterior no las escribe.
    /// </summary>
    internal static List<string> Violations(IEnumerable<(string Id, IReadOnlyList<MigrationOperation> Operations)> migrations) =>
        [.. migrations
            .Where(m => !m.Operations.OfType<SqlOperation>().Any(o => o.Sql.Contains("min_core_version", StringComparison.Ordinal)))
            .SelectMany(m => m.Operations.Where(o => Destructive(o, Created(m.Operations), Added(m.Operations))).Select(o => $"{m.Id}: {o.GetType().Name} sin RequireCoreVersion."))];

    private static bool Destructive(MigrationOperation operation, HashSet<string> created, HashSet<string> added) => operation switch
    {
        // Una clave foránea sobre columnas que la misma migración agrega nulas y sin valor por defecto no endurece nada:
        // la versión anterior no las conoce y las deja en NULL, que la clave no controla.
        AddForeignKeyOperation f when f.Columns.All(c => added.Contains($"{Table(f.Schema, f.Table)}.{c}")) => false,
        DropColumnOperation or DropTableOperation or DropSchemaOperation or RenameColumnOperation or RenameTableOperation => true,
        AlterColumnOperation a => (!a.IsNullable && a.OldColumn.IsNullable) || a.ClrType != a.OldColumn.ClrType || (a.ColumnType is not null && a.OldColumn.ColumnType is not null && a.ColumnType != a.OldColumn.ColumnType),
        AddColumnOperation c => !c.IsNullable && c.DefaultValue is null && c.DefaultValueSql is null && !created.Contains(Table(c.Schema, c.Table)),
        AddCheckConstraintOperation k => !created.Contains(Table(k.Schema, k.Table)),
        AddUniqueConstraintOperation u => !created.Contains(Table(u.Schema, u.Table)),
        AddPrimaryKeyOperation p => !created.Contains(Table(p.Schema, p.Table)),
        AddForeignKeyOperation f => !created.Contains(Table(f.Schema, f.Table)),
        CreateIndexOperation i => i.IsUnique && !created.Contains(Table(i.Schema, i.Table)),
        SqlOperation s => DestructiveSql().IsMatch(s.Sql) || HardeningSql(s.Sql, created),
        _ => false,
    };

    /// <summary>Las tablas que crea la misma migración, como esquema.tabla.</summary>
    private static HashSet<string> Created(IReadOnlyList<MigrationOperation> operations) =>
        operations.OfType<CreateTableOperation>().Select(t => Table(t.Schema, t.Name))
            .Concat(operations.OfType<SqlOperation>().SelectMany(s => CreateTableSql().Matches(s.Sql).Select(m => Table(null, m.Groups[1].Value))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Las columnas que agrega la misma migración, nulas y sin valor por defecto, como esquema.tabla.columna.</summary>
    private static HashSet<string> Added(IReadOnlyList<MigrationOperation> operations) =>
        operations.OfType<AddColumnOperation>().Where(c => c.IsNullable && c.DefaultValue is null && c.DefaultValueSql is null)
            .Select(c => $"{Table(c.Schema, c.Table)}.{c.Name}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool HardeningSql(string sql, HashSet<string> created) =>
        UniqueIndexSql().Matches(sql).Any(m => !created.Contains(Table(null, m.Groups[1].Value)))
        || AlterTableSql().Matches(sql).Where(m => !created.Contains(Table(null, m.Groups[1].Value)))
            .SelectMany(m => AddSql().Matches(m.Groups[2].Value))
            .Select(a => a.Groups[1].Value)
            .Any(a => TableConstraintSql().IsMatch(a) || (NotNullSql().IsMatch(a) && !DefaultSql().IsMatch(a)));

    private static string Table(string? schema, string name)
    {
        var plain = name.Replace("\"", string.Empty, StringComparison.Ordinal);
        return plain.Contains('.') ? plain : $"{schema ?? "public"}.{plain}";
    }

    private static BillingDbContext Context(string connectionString) =>
        new(DesignTimeFactory.Configure(new DbContextOptionsBuilder<BillingDbContext>(), connectionString).Options);

    private static async Task<bool> Valid(CoreHarness core, string index) =>
        await core.Query(db => db.Database.SqlQuery<bool>($"SELECT indisvalid AS \"Value\" FROM pg_index WHERE indexrelid = to_regclass({"public." + index})").SingleAsync());

    [GeneratedRegex(@"\bDROP\s+(TABLE|COLUMN|SCHEMA)\b|\bRENAME\b|\bSET\s+NOT\s+NULL\b|\bALTER\s+COLUMN\s+\w+\s+(SET\s+DATA\s+)?TYPE\b", RegexOptions.IgnoreCase)]
    private static partial Regex DestructiveSql();

    [GeneratedRegex(@"\bCREATE\s+(?:UNLOGGED\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?([\w.""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CreateTableSql();

    [GeneratedRegex(@"\bCREATE\s+UNIQUE\s+INDEX\s+(?:CONCURRENTLY\s+)?(?:IF\s+NOT\s+EXISTS\s+)?[\w.""]+\s+ON\s+(?:ONLY\s+)?([\w.""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UniqueIndexSql();

    /// <summary>Una sentencia ALTER TABLE: la tabla y el resto, hasta el punto y coma.</summary>
    [GeneratedRegex(@"\bALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:ONLY\s+)?([\w.""]+)([^;]*)", RegexOptions.IgnoreCase)]
    private static partial Regex AlterTableSql();

    /// <summary>Cada ADD de la sentencia, hasta la coma que lo separa del siguiente: la de un numeric(18,2) no corta.</summary>
    [GeneratedRegex(@"\bADD\s+((?:[^,()]|\([^()]*\))*)", RegexOptions.IgnoreCase)]
    private static partial Regex AddSql();

    /// <summary>Una restricción de la tabla, con nombre o sin él.</summary>
    [GeneratedRegex(@"^(?:CONSTRAINT|PRIMARY\s+KEY|UNIQUE|CHECK|FOREIGN\s+KEY|EXCLUDE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TableConstraintSql();

    [GeneratedRegex(@"\bNOT\s+NULL\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotNullSql();

    [GeneratedRegex(@"\bDEFAULT\b", RegexOptions.IgnoreCase)]
    private static partial Regex DefaultSql();
}
