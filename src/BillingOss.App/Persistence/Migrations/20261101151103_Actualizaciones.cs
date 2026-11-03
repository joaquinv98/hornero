using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Actualizaciones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "updates");

            migrationBuilder.CreateTable(
                name: "nodes",
                schema: "updates",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    images = table.Column<string>(type: "jsonb", nullable: true),
                    previous_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    previous_images = table.Column<string>(type: "jsonb", nullable: true),
                    updater_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    pulled = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    applied = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nodes", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "requests",
                schema: "updates",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    origin = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "web"),
                    requested_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "pending"),
                    phase = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    step = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    node_index = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    plan = table.Column<string>(type: "jsonb", nullable: true),
                    progress = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    from_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_requests", x => x.id);
                    table.CheckConstraint("ck_requests_origin", "origin IN ('web', 'console')");
                    table.CheckConstraint("ck_requests_status", "status IN ('pending', 'running', 'done', 'failed', 'rejected')");
                    table.CheckConstraint("ck_requests_version", "version ~ '^(0|[1-9][0-9]{0,4})\\.(0|[1-9][0-9]{0,4})\\.(0|[1-9][0-9]{0,5})$'");
                });

            migrationBuilder.CreateTable(
                name: "state",
                schema: "updates",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "jsonb", nullable: true),
                    channel_serial = table.Column<long>(type: "bigint", nullable: false),
                    channel_issued = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_keys = table.Column<string[]>(type: "text[]", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    check_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    check_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    check_rejected = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    images = table.Column<string>(type: "jsonb", nullable: true),
                    min_core_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    blocked_versions = table.Column<string[]>(type: "text[]", nullable: false),
                    lease_holder = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scheduler_paused_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_state", x => x.id);
                    table.CheckConstraint("ck_state_single", "id = 1");
                });

            // Esquema del actualizador (D-20): una sola fila de estado, un solo pedido activo y la aplicación solo lee y
            // deja pedidos.
            migrationBuilder.Sql(Platform.UpdatesSql.Seed);
            migrationBuilder.Sql(Platform.UpdatesSql.OneActiveRequest);
            migrationBuilder.Sql(Platform.UpdatesSql.RestrictOtherRoles);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nodes",
                schema: "updates");

            migrationBuilder.DropTable(
                name: "requests",
                schema: "updates");

            migrationBuilder.DropTable(
                name: "state",
                schema: "updates");
        }
    }
}
