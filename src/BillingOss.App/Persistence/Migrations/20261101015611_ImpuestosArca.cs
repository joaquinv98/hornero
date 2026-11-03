using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImpuestosArca : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "account_id",
                schema: "catalog",
                table: "tax_rules",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "business_with_validated_number",
                schema: "catalog",
                table: "tax_rules",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "category",
                schema: "catalog",
                table: "tax_rules",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "certificate_ref",
                schema: "catalog",
                table: "tax_rules",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "created_by",
                schema: "catalog",
                table: "tax_rules",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "legend",
                schema: "catalog",
                table: "tax_rules",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "note",
                schema: "catalog",
                table: "tax_rules",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason",
                schema: "catalog",
                table: "tax_rules",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tax_code",
                schema: "catalog",
                table: "tax_rules",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "source",
                schema: "accounts",
                table: "tax_identities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vat_number",
                schema: "accounts",
                table: "tax_identities",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "vat_number_validated_at",
                schema: "accounts",
                table: "tax_identities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vat_validation_ref",
                schema: "accounts",
                table: "tax_identities",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "verified_at",
                schema: "accounts",
                table: "tax_identities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "price_mode",
                schema: "services",
                table: "services",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "tax_category",
                schema: "catalog",
                table: "products",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(FiscalSql.ProductsToCategories);

            migrationBuilder.DropColumn(
                name: "tax_treatment",
                schema: "catalog",
                table: "products");

            migrationBuilder.AddColumn<string>(
                name: "price_mode",
                schema: "catalog",
                table: "plan_prices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "active_since",
                schema: "fiscal",
                table: "issuers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "class_a_with_legend",
                schema: "fiscal",
                table: "issuers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "exports",
                schema: "fiscal",
                table: "issuers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "known_points_of_sale",
                schema: "fiscal",
                table: "issuers",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "last_test",
                schema: "fiscal",
                table: "issuers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_test_at",
                schema: "fiscal",
                table: "issuers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "setup_step",
                schema: "fiscal",
                table: "issuers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "uses_registry",
                schema: "fiscal",
                table: "issuers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "price_mode",
                schema: "domains",
                table: "domains",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "legends",
                schema: "billing",
                table: "documents",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "other_taxes",
                schema: "billing",
                table: "documents",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "taxes",
                schema: "billing",
                table: "documents",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "category",
                schema: "billing",
                table: "document_lines",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "legend",
                schema: "billing",
                table: "document_lines",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason",
                schema: "billing",
                table: "document_lines",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "large_company",
                schema: "accounts",
                table: "accounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "credentials",
                schema: "fiscal",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    issuer_id = table.Column<long>(type: "bigint", nullable: false),
                    environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    alias = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    subject_tax_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    organization = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    private_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    csr = table.Column<string>(type: "text", nullable: false),
                    certificate = table.Column<string>(type: "text", nullable: true),
                    certificate_tax_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    certificate_alias = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    certificate_issuer = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    certificate_serial = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    thumbprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    not_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notices_sent = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credentials", x => x.id);
                    table.ForeignKey(
                        name: "fk_credentials_issuer_profiles_issuer_id",
                        column: x => x.issuer_id,
                        principalSchema: "fiscal",
                        principalTable: "issuers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tax_categories",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    explanation = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    treatment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    external_codes = table.Column<string>(type: "jsonb", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tax_settings",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    issuer_country = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    price_mode_by_currency = table.Column<string>(type: "jsonb", nullable: false),
                    catalog_display = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    business_may_view_net = table.Column<bool>(type: "boolean", nullable: false),
                    consumer_id_threshold = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    consumer_id_threshold_from = table.Column<DateOnly>(type: "date", nullable: false),
                    fce_minimum = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    fce_minimum_from = table.Column<DateOnly>(type: "date", nullable: false),
                    gross_income_regimes = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "taxes",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    level = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    @base = table.Column<string>(name: "base", type: "character varying(32)", maxLength: 32, nullable: false),
                    fiscal_code = table.Column<int>(type: "integer", nullable: true),
                    adds_to_total = table.Column<bool>(type: "boolean", nullable: false),
                    group = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_taxes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tax_rules_account_id",
                schema: "catalog",
                table: "tax_rules",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_tax_rules_tax_code_account_id",
                schema: "catalog",
                table: "tax_rules",
                columns: new[] { "tax_code", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_credentials_issuer_id_environment",
                schema: "fiscal",
                table: "credentials",
                columns: new[] { "issuer_id", "environment" },
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_tax_categories_code",
                schema: "catalog",
                table: "tax_categories",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_taxes_code",
                schema: "catalog",
                table: "taxes",
                column: "code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_tax_rules_accounts_account_id",
                schema: "catalog",
                table: "tax_rules",
                column: "account_id",
                principalSchema: "accounts",
                principalTable: "accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(FiscalSql.Seed);

            migrationBuilder.Sql(FiscalSql.Backfill);

            migrationBuilder.Sql(FiscalSql.MetricsUp);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(FiscalSql.MetricsDown);

            migrationBuilder.DropForeignKey(
                name: "fk_tax_rules_accounts_account_id",
                schema: "catalog",
                table: "tax_rules");

            migrationBuilder.DropTable(
                name: "credentials",
                schema: "fiscal");

            migrationBuilder.DropTable(
                name: "tax_categories",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "tax_settings",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "taxes",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "ix_tax_rules_account_id",
                schema: "catalog",
                table: "tax_rules");

            migrationBuilder.DropIndex(
                name: "ix_tax_rules_tax_code_account_id",
                schema: "catalog",
                table: "tax_rules");

            migrationBuilder.DropColumn(
                name: "account_id",
                schema: "catalog",
                table: "tax_rules");

            migrationBuilder.DropColumn(
                name: "business_with_validated_number",
                schema: "catalog",
                table: "tax_rules");

            migrationBuilder.DropColumn(
                name: "category",
                schema: "catalog",
                table: "tax_rules");

            migrationBuilder.DropColumn(
                name: "certificate_ref",
                schema: "catalog",
                table: "tax_rules");

            migrationBuilder.DropColumn(
                name: "created_by",
                schema: "catalog",
                table: "tax_rules");

            migrationBuilder.DropColumn(
                name: "legend",
                schema: "catalog",
                table: "tax_rules");

            migrationBuilder.DropColumn(
                name: "note",
                schema: "catalog",
                table: "tax_rules");

            migrationBuilder.DropColumn(
                name: "reason",
                schema: "catalog",
                table: "tax_rules");

            migrationBuilder.DropColumn(
                name: "tax_code",
                schema: "catalog",
                table: "tax_rules");

            migrationBuilder.DropColumn(
                name: "source",
                schema: "accounts",
                table: "tax_identities");

            migrationBuilder.DropColumn(
                name: "vat_number",
                schema: "accounts",
                table: "tax_identities");

            migrationBuilder.DropColumn(
                name: "vat_number_validated_at",
                schema: "accounts",
                table: "tax_identities");

            migrationBuilder.DropColumn(
                name: "vat_validation_ref",
                schema: "accounts",
                table: "tax_identities");

            migrationBuilder.DropColumn(
                name: "verified_at",
                schema: "accounts",
                table: "tax_identities");

            migrationBuilder.DropColumn(
                name: "price_mode",
                schema: "services",
                table: "services");

            migrationBuilder.AddColumn<string>(
                name: "tax_treatment",
                schema: "catalog",
                table: "products",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(FiscalSql.CategoriesToProducts);

            migrationBuilder.DropColumn(
                name: "tax_category",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "price_mode",
                schema: "catalog",
                table: "plan_prices");

            migrationBuilder.DropColumn(
                name: "active_since",
                schema: "fiscal",
                table: "issuers");

            migrationBuilder.DropColumn(
                name: "class_a_with_legend",
                schema: "fiscal",
                table: "issuers");

            migrationBuilder.DropColumn(
                name: "exports",
                schema: "fiscal",
                table: "issuers");

            migrationBuilder.DropColumn(
                name: "known_points_of_sale",
                schema: "fiscal",
                table: "issuers");

            migrationBuilder.DropColumn(
                name: "last_test",
                schema: "fiscal",
                table: "issuers");

            migrationBuilder.DropColumn(
                name: "last_test_at",
                schema: "fiscal",
                table: "issuers");

            migrationBuilder.DropColumn(
                name: "setup_step",
                schema: "fiscal",
                table: "issuers");

            migrationBuilder.DropColumn(
                name: "uses_registry",
                schema: "fiscal",
                table: "issuers");

            migrationBuilder.DropColumn(
                name: "price_mode",
                schema: "domains",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "legends",
                schema: "billing",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "other_taxes",
                schema: "billing",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "taxes",
                schema: "billing",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "category",
                schema: "billing",
                table: "document_lines");

            migrationBuilder.DropColumn(
                name: "legend",
                schema: "billing",
                table: "document_lines");

            migrationBuilder.DropColumn(
                name: "reason",
                schema: "billing",
                table: "document_lines");

            migrationBuilder.DropColumn(
                name: "large_company",
                schema: "accounts",
                table: "accounts");
        }
    }
}
