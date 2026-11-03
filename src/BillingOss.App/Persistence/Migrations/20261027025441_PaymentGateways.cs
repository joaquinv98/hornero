using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentGateways : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attempts",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    module_instance_id = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    document_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    redirect_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    payment_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_attempts_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "accounts",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attempts_module_instances_module_instance_id",
                        column: x => x.module_instance_id,
                        principalSchema: "platform",
                        principalTable: "module_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attempts_payments_payment_id",
                        column: x => x.payment_id,
                        principalSchema: "payments",
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_receipts",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    module_instance_id = table.Column<long>(type: "bigint", nullable: false),
                    headers = table.Column<string>(type: "jsonb", nullable: false),
                    query = table.Column<string>(type: "jsonb", nullable: false),
                    body = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_receipts", x => x.id);
                    table.ForeignKey(
                        name: "fk_webhook_receipts_module_instances_module_instance_id",
                        column: x => x.module_instance_id,
                        principalSchema: "platform",
                        principalTable: "module_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gateway_events",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    module_instance_id = table.Column<long>(type: "bigint", nullable: false),
                    event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payment_external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    receipt_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gateway_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_gateway_events_module_instances_module_instance_id",
                        column: x => x.module_instance_id,
                        principalSchema: "platform",
                        principalTable: "module_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_gateway_events_webhook_receipts_receipt_id",
                        column: x => x.receipt_id,
                        principalSchema: "payments",
                        principalTable: "webhook_receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attempts_account_id",
                schema: "payments",
                table: "attempts",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_attempts_module_instance_id",
                schema: "payments",
                table: "attempts",
                column: "module_instance_id");

            migrationBuilder.CreateIndex(
                name: "ix_attempts_payment_id",
                schema: "payments",
                table: "attempts",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_attempts_reference",
                schema: "payments",
                table: "attempts",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gateway_events_module_instance_id_event_id",
                schema: "payments",
                table: "gateway_events",
                columns: new[] { "module_instance_id", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gateway_events_receipt_id",
                schema: "payments",
                table: "gateway_events",
                column: "receipt_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_receipts_module_instance_id",
                schema: "payments",
                table: "webhook_receipts",
                column: "module_instance_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attempts",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "gateway_events",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "webhook_receipts",
                schema: "payments");
        }
    }
}
