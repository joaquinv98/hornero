using System;
using BillingOss.App.Platform;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Catalogo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "coupon_id",
                schema: "services",
                table: "services",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "discount_amount",
                schema: "services",
                table: "services",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "discount_ends_on",
                schema: "services",
                table: "services",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "trial_ends_on",
                schema: "services",
                table: "services",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "activation",
                schema: "catalog",
                table: "products",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "OnPayment");

            migrationBuilder.AddColumn<bool>(
                name: "addon",
                schema: "catalog",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "allow_quantity",
                schema: "catalog",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "description",
                schema: "catalog",
                table: "products",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "group_id",
                schema: "catalog",
                table: "products",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "option_changes",
                schema: "catalog",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long[]>(
                name: "parent_product_ids",
                schema: "catalog",
                table: "products",
                type: "bigint[]",
                nullable: false,
                defaultValue: new long[0]);

            migrationBuilder.AddColumn<int>(
                name: "sort_order",
                schema: "catalog",
                table: "products",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "trial_days",
                schema: "catalog",
                table: "products",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long[]>(
                name: "upgrade_product_ids",
                schema: "catalog",
                table: "products",
                type: "bigint[]",
                nullable: false,
                defaultValue: new long[0]);

            migrationBuilder.AddColumn<DateOnly>(
                name: "valid_to",
                schema: "catalog",
                table: "plan_prices",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "bundle_id",
                schema: "orders",
                table: "orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "coupon_id",
                schema: "orders",
                table: "orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "bundles",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    plan_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    discount_percent = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    discount_cycles = table.Column<int>(type: "integer", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bundles", x => x.id);
                    table.CheckConstraint("ck_bundles_discount", "discount_percent >= 0 AND discount_percent <= 100 AND cardinality(plan_ids) > 0");
                });

            migrationBuilder.CreateTable(
                name: "coupons",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    value = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    recurring = table.Column<bool>(type: "boolean", nullable: false),
                    recurring_cycles = table.Column<int>(type: "integer", nullable: true),
                    product_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    cycles = table.Column<string[]>(type: "text[]", nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: true),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    max_uses = table.Column<int>(type: "integer", nullable: true),
                    uses = table.Column<int>(type: "integer", nullable: false),
                    once_per_account = table.Column<bool>(type: "boolean", nullable: false),
                    new_accounts_only = table.Column<bool>(type: "boolean", nullable: false),
                    existing_accounts_only = table.Column<bool>(type: "boolean", nullable: false),
                    once_per_order = table.Column<bool>(type: "boolean", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coupons", x => x.id);
                    table.CheckConstraint("ck_coupons_uses", "uses >= 0 AND (max_uses IS NULL OR uses <= max_uses)");
                    table.CheckConstraint("ck_coupons_value", "value >= 0 AND (kind <> 'Percent' OR value <= 100)");
                });

            migrationBuilder.CreateTable(
                name: "option_groups",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_option_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product_groups",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    headline = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    visible = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "service_changes",
                schema: "services",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    service_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    from_product_id = table.Column<long>(type: "bigint", nullable: false),
                    to_product_id = table.Column<long>(type: "bigint", nullable: false),
                    cycle_unit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    cycle_count = table.Column<int>(type: "integer", nullable: false),
                    from_unit_price = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    to_unit_price = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    options = table.Column<string>(type: "jsonb", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    billed_until = table.Column<DateOnly>(type: "date", nullable: true),
                    ends_discount = table.Column<bool>(type: "boolean", nullable: false),
                    document_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_service_changes_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "billing",
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_changes_services_service_id",
                        column: x => x.service_id,
                        principalSchema: "services",
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "options",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    module_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    min_quantity = table.Column<int>(type: "integer", nullable: false),
                    max_quantity = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    hidden = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_options", x => x.id);
                    table.CheckConstraint("ck_options_quantity", "min_quantity >= 0 AND max_quantity >= 0");
                    table.ForeignKey(
                        name: "fk_options_option_groups_group_id",
                        column: x => x.group_id,
                        principalSchema: "catalog",
                        principalTable: "option_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_option_groups",
                schema: "catalog",
                columns: table => new
                {
                    product_id = table.Column<long>(type: "bigint", nullable: false),
                    option_group_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_option_groups", x => new { x.product_id, x.option_group_id });
                    table.ForeignKey(
                        name: "fk_product_option_groups_option_groups_option_group_id",
                        column: x => x.option_group_id,
                        principalSchema: "catalog",
                        principalTable: "option_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_option_groups_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "option_values",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    option_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    module_value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    hidden = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_option_values", x => x.id);
                    table.ForeignKey(
                        name: "fk_option_values_options_option_id",
                        column: x => x.option_id,
                        principalSchema: "catalog",
                        principalTable: "options",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "option_prices",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    value_id = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    cycle_unit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    cycle_count = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    setup_fee = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_option_prices", x => x.id);
                    table.CheckConstraint("ck_option_prices_positive", "amount >= 0 AND setup_fee >= 0");
                    table.ForeignKey(
                        name: "fk_option_prices_option_values_value_id",
                        column: x => x.value_id,
                        principalSchema: "catalog",
                        principalTable: "option_values",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_options",
                schema: "services",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    service_id = table.Column<long>(type: "bigint", nullable: false),
                    option_id = table.Column<long>(type: "bigint", nullable: false),
                    value_id = table.Column<long>(type: "bigint", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_options", x => x.id);
                    table.CheckConstraint("ck_service_options_quantity", "quantity >= 0");
                    table.ForeignKey(
                        name: "fk_service_options_option_values_value_id",
                        column: x => x.value_id,
                        principalSchema: "catalog",
                        principalTable: "option_values",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_options_options_option_id",
                        column: x => x.option_id,
                        principalSchema: "catalog",
                        principalTable: "options",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_options_services_service_id",
                        column: x => x.service_id,
                        principalSchema: "services",
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_services_coupon_id",
                schema: "services",
                table: "services",
                column: "coupon_id");

            migrationBuilder.CreateIndex(
                name: "ix_services_one_trial",
                schema: "services",
                table: "services",
                columns: new[] { "account_id", "product_id" },
                unique: true,
                filter: "trial_ends_on IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_products_group_id",
                schema: "catalog",
                table: "products",
                column: "group_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_stock",
                schema: "catalog",
                table: "products",
                sql: "stock IS NULL OR stock >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_orders_bundle_id",
                schema: "orders",
                table: "orders",
                column: "bundle_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_coupon_id",
                schema: "orders",
                table: "orders",
                column: "coupon_id");

            migrationBuilder.CreateIndex(
                name: "ix_option_prices_value_id_currency_cycle_unit_cycle_count",
                schema: "catalog",
                table: "option_prices",
                columns: new[] { "value_id", "currency", "cycle_unit", "cycle_count" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_option_values_option_id",
                schema: "catalog",
                table: "option_values",
                column: "option_id");

            migrationBuilder.CreateIndex(
                name: "ix_options_group_id",
                schema: "catalog",
                table: "options",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_option_groups_option_group_id",
                schema: "catalog",
                table: "product_option_groups",
                column: "option_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_changes_document_id",
                schema: "services",
                table: "service_changes",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_changes_service_id",
                schema: "services",
                table: "service_changes",
                column: "service_id",
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_service_options_option_id",
                schema: "services",
                table: "service_options",
                column: "option_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_options_service_id_option_id",
                schema: "services",
                table: "service_options",
                columns: new[] { "service_id", "option_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_options_value_id",
                schema: "services",
                table: "service_options",
                column: "value_id");

            migrationBuilder.AddForeignKey(
                name: "fk_orders_bundles_bundle_id",
                schema: "orders",
                table: "orders",
                column: "bundle_id",
                principalSchema: "catalog",
                principalTable: "bundles",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_orders_coupons_coupon_id",
                schema: "orders",
                table: "orders",
                column: "coupon_id",
                principalSchema: "catalog",
                principalTable: "coupons",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_products_product_groups_group_id",
                schema: "catalog",
                table: "products",
                column: "group_id",
                principalSchema: "catalog",
                principalTable: "product_groups",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_services_coupons_coupon_id",
                schema: "services",
                table: "services",
                column: "coupon_id",
                principalSchema: "catalog",
                principalTable: "coupons",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Los grupos que eran un nombre en el producto pasan a ser grupos propios, antes de sacar la columna.
            migrationBuilder.Sql("""
                INSERT INTO catalog.product_groups (name, visible, sort_order)
                SELECT DISTINCT group_name, true, 0 FROM catalog.products WHERE group_name IS NOT NULL AND group_name <> '';
                UPDATE catalog.products p SET group_id = g.id FROM catalog.product_groups g WHERE g.name = p.group_name;
                """);

            migrationBuilder.DropColumn(
                name: "group_name",
                schema: "catalog",
                table: "products");

            // El código del cupón es único sin distinguir mayúsculas, como lo escribe el cliente.
            migrationBuilder.Sql("CREATE UNIQUE INDEX ix_coupons_code ON catalog.coupons (lower(code))");

            // Borra el nombre de grupo del producto y endurece tablas que ya existían (servicios, pedidos, productos): un
            // núcleo anterior al catálogo no anda con este esquema (sección 7.4 de actualizaciones y seguridad).
            migrationBuilder.RequireCoreVersion("0.1.0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_orders_bundles_bundle_id",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropForeignKey(
                name: "fk_orders_coupons_coupon_id",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropForeignKey(
                name: "fk_products_product_groups_group_id",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropForeignKey(
                name: "fk_services_coupons_coupon_id",
                schema: "services",
                table: "services");

            migrationBuilder.DropTable(
                name: "bundles",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "coupons",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "option_prices",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_groups",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_option_groups",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "service_changes",
                schema: "services");

            migrationBuilder.DropTable(
                name: "service_options",
                schema: "services");

            migrationBuilder.DropTable(
                name: "option_values",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "options",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "option_groups",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "ix_services_coupon_id",
                schema: "services",
                table: "services");

            migrationBuilder.DropIndex(
                name: "ix_services_one_trial",
                schema: "services",
                table: "services");

            migrationBuilder.DropIndex(
                name: "ix_products_group_id",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_stock",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropIndex(
                name: "ix_orders_bundle_id",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ix_orders_coupon_id",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "coupon_id",
                schema: "services",
                table: "services");

            migrationBuilder.DropColumn(
                name: "discount_amount",
                schema: "services",
                table: "services");

            migrationBuilder.DropColumn(
                name: "discount_ends_on",
                schema: "services",
                table: "services");

            migrationBuilder.DropColumn(
                name: "trial_ends_on",
                schema: "services",
                table: "services");

            migrationBuilder.DropColumn(
                name: "activation",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "addon",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "allow_quantity",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "description",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "group_id",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "option_changes",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "parent_product_ids",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "sort_order",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "trial_days",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "upgrade_product_ids",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "valid_to",
                schema: "catalog",
                table: "plan_prices");

            migrationBuilder.DropColumn(
                name: "bundle_id",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "coupon_id",
                schema: "orders",
                table: "orders");

            migrationBuilder.AddColumn<string>(
                name: "group_name",
                schema: "catalog",
                table: "products",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }
    }
}
