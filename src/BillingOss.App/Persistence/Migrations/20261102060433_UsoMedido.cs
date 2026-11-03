using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UsoMedido : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "usage_metrics",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    product_id = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    aggregation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    included = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    step = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    cycle = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    alert_percents = table.Column<int[]>(type: "integer[]", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    active_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_metrics", x => x.id);
                    table.CheckConstraint("ck_usage_metrics_amounts", "included >= 0 AND step > 0");
                    table.ForeignKey(
                        name: "fk_usage_metrics_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "usage_charges",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    service_id = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    metric_id = table.Column<long>(type: "bigint", nullable: false),
                    start = table.Column<DateOnly>(type: "date", nullable: false),
                    end = table.Column<DateOnly>(type: "date", nullable: false),
                    measured = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    included = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    excess = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    document_line_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_charges", x => x.id);
                    table.CheckConstraint("ck_usage_charges_window", "\"end\" > start AND measured >= 0 AND included >= 0 AND excess >= 0");
                    table.ForeignKey(
                        name: "fk_usage_charges_document_lines_document_line_id",
                        column: x => x.document_line_id,
                        principalSchema: "billing",
                        principalTable: "document_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_usage_charges_services_service_id",
                        column: x => x.service_id,
                        principalSchema: "services",
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_usage_charges_usage_metric_metric_id",
                        column: x => x.metric_id,
                        principalSchema: "catalog",
                        principalTable: "usage_metrics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "usage_metric_prices",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    metric_id = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_metric_prices", x => x.id);
                    table.CheckConstraint("ck_usage_metric_prices_positive", "unit_price >= 0");
                    table.ForeignKey(
                        name: "fk_usage_metric_prices_usage_metrics_metric_id",
                        column: x => x.metric_id,
                        principalSchema: "catalog",
                        principalTable: "usage_metrics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "usage_samples",
                schema: "services",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    service_id = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    reading = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    value = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    charge_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_samples", x => x.id);
                    table.CheckConstraint("ck_usage_samples_positive", "reading >= 0 AND value >= 0");
                    table.ForeignKey(
                        name: "fk_usage_samples_services_service_id",
                        column: x => x.service_id,
                        principalSchema: "services",
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_usage_samples_usage_charges_charge_id",
                        column: x => x.charge_id,
                        principalSchema: "billing",
                        principalTable: "usage_charges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_usage_charges_document_line_id",
                schema: "billing",
                table: "usage_charges",
                column: "document_line_id");

            migrationBuilder.CreateIndex(
                name: "ix_usage_charges_metric_id",
                schema: "billing",
                table: "usage_charges",
                column: "metric_id");

            migrationBuilder.CreateIndex(
                name: "ix_usage_charges_service_id_code_end",
                schema: "billing",
                table: "usage_charges",
                columns: new[] { "service_id", "code", "end" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usage_metric_prices_metric_id_currency_valid_from",
                schema: "catalog",
                table: "usage_metric_prices",
                columns: new[] { "metric_id", "currency", "valid_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usage_metrics_product_id_code",
                schema: "catalog",
                table: "usage_metrics",
                columns: new[] { "product_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usage_samples_charge_id",
                schema: "services",
                table: "usage_samples",
                column: "charge_id");

            migrationBuilder.CreateIndex(
                name: "ix_usage_samples_service_id_code_date",
                schema: "services",
                table: "usage_samples",
                columns: new[] { "service_id", "code", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usage_samples_unbilled",
                schema: "services",
                table: "usage_samples",
                columns: new[] { "service_id", "code" },
                filter: "charge_id IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usage_metric_prices",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "usage_samples",
                schema: "services");

            migrationBuilder.DropTable(
                name: "usage_charges",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "usage_metrics",
                schema: "catalog");
        }
    }
}
