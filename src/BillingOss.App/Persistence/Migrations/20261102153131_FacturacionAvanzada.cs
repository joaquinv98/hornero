using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FacturacionAvanzada : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "index_currency",
                schema: "services",
                table: "services",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "indexed_unit_price",
                schema: "services",
                table: "services",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "actor",
                schema: "billing",
                table: "credit_entries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "exchange_rate_id",
                schema: "payments",
                table: "attempts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "rate",
                schema: "payments",
                table: "attempts",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "payment_amount",
                schema: "payments",
                table: "allocations",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "no_reminders",
                schema: "accounts",
                table: "accounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "billable_items",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    service_id = table.Column<long>(type: "bigint", nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    treatment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    invoice_on = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    document_id = table.Column<long>(type: "bigint", nullable: true),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billable_items", x => x.id);
                    table.CheckConstraint("ck_billable_items_invoiced", "(status = 'Invoiced') = (document_id IS NOT NULL) AND quantity > 0");
                    table.ForeignKey(
                        name: "fk_billable_items_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "accounts",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_billable_items_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "billing",
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_billable_items_services_service_id",
                        column: x => x.service_id,
                        principalSchema: "services",
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "exchange_rates",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    quote = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    rate = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exchange_rates", x => x.id);
                    table.CheckConstraint("ck_exchange_rates_rate", "rate > 0 AND currency <> quote");
                });

            migrationBuilder.CreateTable(
                name: "quotes",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: false),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    document_id = table.Column<long>(type: "bigint", nullable: true),
                    order_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quotes", x => x.id);
                    table.CheckConstraint("ck_quotes_converted", "(status = 'Ordered') = (order_id IS NOT NULL) AND (status <> 'Invoiced' OR document_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_quotes_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "accounts",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_quotes_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "billing",
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_quotes_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attempts_exchange_rate_id",
                schema: "payments",
                table: "attempts",
                column: "exchange_rate_id");

            migrationBuilder.CreateIndex(
                name: "ix_billable_items_account_id",
                schema: "billing",
                table: "billable_items",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_billable_items_document_id",
                schema: "billing",
                table: "billable_items",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_billable_items_invoice_on_account_id",
                schema: "billing",
                table: "billable_items",
                columns: new[] { "invoice_on", "account_id" },
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_billable_items_service_id",
                schema: "billing",
                table: "billable_items",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_exchange_rates_currency_quote_date",
                schema: "billing",
                table: "exchange_rates",
                columns: new[] { "currency", "quote", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_quotes_account_id",
                schema: "billing",
                table: "quotes",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_quotes_document_id",
                schema: "billing",
                table: "quotes",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_quotes_order_id",
                schema: "billing",
                table: "quotes",
                column: "order_id");

            migrationBuilder.AddForeignKey(
                name: "fk_attempts_exchange_rates_exchange_rate_id",
                schema: "payments",
                table: "attempts",
                column: "exchange_rate_id",
                principalSchema: "billing",
                principalTable: "exchange_rates",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_attempts_exchange_rates_exchange_rate_id",
                schema: "payments",
                table: "attempts");

            migrationBuilder.DropTable(
                name: "billable_items",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "exchange_rates",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "quotes",
                schema: "billing");

            migrationBuilder.DropIndex(
                name: "ix_attempts_exchange_rate_id",
                schema: "payments",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "index_currency",
                schema: "services",
                table: "services");

            migrationBuilder.DropColumn(
                name: "indexed_unit_price",
                schema: "services",
                table: "services");

            migrationBuilder.DropColumn(
                name: "actor",
                schema: "billing",
                table: "credit_entries");

            migrationBuilder.DropColumn(
                name: "exchange_rate_id",
                schema: "payments",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "rate",
                schema: "payments",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "payment_amount",
                schema: "payments",
                table: "allocations");

            migrationBuilder.DropColumn(
                name: "no_reminders",
                schema: "accounts",
                table: "accounts");
        }
    }
}
