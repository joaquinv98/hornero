using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FraudeAcceso : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blocked_entries",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    value = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    network = table.Column<IPNetwork>(type: "cidr", nullable: true),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blocked_entries", x => x.id);
                    table.CheckConstraint("ck_blocked_entries_network", "(kind = 'Ip') = (network IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "fraud_checks",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    order_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    reasons = table.Column<string>(type: "jsonb", nullable: false),
                    module_instance_id = table.Column<long>(type: "bigint", nullable: true),
                    external_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    user_id = table.Column<long>(type: "bigint", nullable: true),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fraud_checks", x => x.id);
                    table.CheckConstraint("ck_fraud_checks_score", "score IS NULL OR (score >= 0 AND score <= 100)");
                    table.ForeignKey(
                        name: "fk_fraud_checks_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "accounts",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fraud_checks_module_instances_module_instance_id",
                        column: x => x.module_instance_id,
                        principalSchema: "platform",
                        principalTable: "module_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fraud_checks_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "login_events",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    audience = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: true),
                    ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    network = table.Column<IPNetwork>(type: "cidr", nullable: true),
                    user_agent = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    new_location = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_login_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "security_settings",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_settings", x => x.id);
                    table.CheckConstraint("ck_security_settings_single", "id = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_blocked_entries_kind_value",
                schema: "identity",
                table: "blocked_entries",
                columns: new[] { "kind", "value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fraud_checks_account_id",
                schema: "orders",
                table: "fraud_checks",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_fraud_checks_module_instance_id",
                schema: "orders",
                table: "fraud_checks",
                column: "module_instance_id");

            migrationBuilder.CreateIndex(
                name: "ix_fraud_checks_order_id",
                schema: "orders",
                table: "fraud_checks",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fraud_checks_status",
                schema: "orders",
                table: "fraud_checks",
                column: "status",
                filter: "status IN ('Pending', 'Review')");

            migrationBuilder.CreateIndex(
                name: "ix_login_events_audience_user_id_created_at",
                schema: "identity",
                table: "login_events",
                columns: new[] { "audience", "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_login_events_created_at",
                schema: "identity",
                table: "login_events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_login_events_ip_created_at",
                schema: "identity",
                table: "login_events",
                columns: new[] { "ip", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blocked_entries",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "fraud_checks",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "login_events",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "security_settings",
                schema: "identity");
        }
    }
}
