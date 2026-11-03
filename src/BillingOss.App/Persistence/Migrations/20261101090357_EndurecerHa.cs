using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EndurecerHa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reconcile_findings",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconcile_findings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "timeline",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    timeline_id = table.Column<long>(type: "bigint", nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_timeline", x => x.id);
                    table.CheckConstraint("ck_timeline_single", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "unmatched",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    module_instance_id = table.Column<long>(type: "bigint", nullable: false),
                    payment_external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    external_reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unmatched", x => x.id);
                    table.ForeignKey(
                        name: "fk_unmatched_module_instances_module_instance_id",
                        column: x => x.module_instance_id,
                        principalSchema: "platform",
                        principalTable: "module_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_receipts_with_body",
                schema: "payments",
                table: "webhook_receipts",
                column: "received_at",
                filter: "octet_length(body) > 0");

            migrationBuilder.CreateIndex(
                name: "ix_payments_open",
                schema: "payments",
                table: "payments",
                column: "status",
                filter: "status IN ('Pending', 'Disputed')");

            migrationBuilder.CreateIndex(
                name: "ix_gateway_events_created_at",
                schema: "payments",
                table: "gateway_events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_emissions_open",
                schema: "fiscal",
                table: "emissions",
                column: "status",
                filter: "status <> 'Authorized'");

            migrationBuilder.CreateIndex(
                name: "ix_reconcile_findings_open",
                schema: "platform",
                table: "reconcile_findings",
                columns: new[] { "kind", "subject" },
                unique: true,
                filter: "resolved_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_unmatched_module_instance_id_payment_external_id",
                schema: "payments",
                table: "unmatched",
                columns: new[] { "module_instance_id", "payment_external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_unmatched_open",
                schema: "payments",
                table: "unmatched",
                column: "id",
                filter: "resolved_at IS NULL");

            // Métricas de negocio con la conciliación y los pagos sin casar, y los conteos acotados a lo que alerta (D-15).
            migrationBuilder.Sql(HardeningSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(HardeningSql.Down);

            migrationBuilder.DropTable(
                name: "reconcile_findings",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "timeline",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "unmatched",
                schema: "payments");

            migrationBuilder.DropIndex(
                name: "ix_webhook_receipts_with_body",
                schema: "payments",
                table: "webhook_receipts");

            migrationBuilder.DropIndex(
                name: "ix_payments_open",
                schema: "payments",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "ix_gateway_events_created_at",
                schema: "payments",
                table: "gateway_events");

            migrationBuilder.DropIndex(
                name: "ix_emissions_open",
                schema: "fiscal",
                table: "emissions");
        }
    }
}
