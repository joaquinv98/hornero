using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Afiliados : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "affiliates");

            migrationBuilder.CreateTable(
                name: "affiliates",
                schema: "affiliates",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_affiliates", x => x.id);
                    table.ForeignKey(
                        name: "fk_affiliates_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "accounts",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "program",
                schema: "affiliates",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    requires_approval = table.Column<bool>(type: "boolean", nullable: false),
                    cookie_days = table.Column<int>(type: "integer", nullable: false),
                    hold_days = table.Column<int>(type: "integer", nullable: false),
                    minimum_payouts = table.Column<string>(type: "jsonb", nullable: false),
                    signup_bonuses = table.Column<string>(type: "jsonb", nullable: false),
                    payout_notify_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    links = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    monthly_report = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_program", x => x.id);
                    table.CheckConstraint("ck_program_days", "cookie_days BETWEEN 1 AND 365 AND hold_days BETWEEN 0 AND 365");
                    table.CheckConstraint("ck_program_single", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "attributions",
                schema: "affiliates",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    affiliate_id = table.Column<long>(type: "bigint", nullable: false),
                    clicked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attributions", x => x.account_id);
                    table.ForeignKey(
                        name: "fk_attributions_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "accounts",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attributions_affiliate_affiliate_id",
                        column: x => x.affiliate_id,
                        principalSchema: "affiliates",
                        principalTable: "affiliates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "clicks",
                schema: "affiliates",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    affiliate_id = table.Column<long>(type: "bigint", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    referrer_host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clicks", x => x.id);
                    table.ForeignKey(
                        name: "fk_clicks_affiliate_affiliate_id",
                        column: x => x.affiliate_id,
                        principalSchema: "affiliates",
                        principalTable: "affiliates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commission_rules",
                schema: "affiliates",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    product_id = table.Column<long>(type: "bigint", nullable: true),
                    affiliate_id = table.Column<long>(type: "bigint", nullable: true),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    value = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    recurring = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commission_rules", x => x.id);
                    table.CheckConstraint("ck_commission_rules_currency", "(kind = 'Fixed') = (currency IS NOT NULL)");
                    table.CheckConstraint("ck_commission_rules_value", "kind <> 'Percent' OR (value > 0 AND value <= 100)");
                    table.ForeignKey(
                        name: "fk_commission_rules_affiliates_affiliate_id",
                        column: x => x.affiliate_id,
                        principalSchema: "affiliates",
                        principalTable: "affiliates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_commission_rules_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payouts",
                schema: "affiliates",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    affiliate_id = table.Column<long>(type: "bigint", nullable: false),
                    method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    details = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    credit_entry_id = table.Column<long>(type: "bigint", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payouts", x => x.id);
                    table.CheckConstraint("ck_payouts_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_payouts_affiliates_affiliate_id",
                        column: x => x.affiliate_id,
                        principalSchema: "affiliates",
                        principalTable: "affiliates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payouts_credit_entries_credit_entry_id",
                        column: x => x.credit_entry_id,
                        principalSchema: "billing",
                        principalTable: "credit_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "referrals",
                schema: "affiliates",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    affiliate_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    service_id = table.Column<long>(type: "bigint", nullable: false),
                    order_id = table.Column<long>(type: "bigint", nullable: true),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    paid_before = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_referrals", x => x.id);
                    table.ForeignKey(
                        name: "fk_referrals_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "accounts",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_referrals_affiliates_affiliate_id",
                        column: x => x.affiliate_id,
                        principalSchema: "affiliates",
                        principalTable: "affiliates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_referrals_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_referrals_services_service_id",
                        column: x => x.service_id,
                        principalSchema: "services",
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commissions",
                schema: "affiliates",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    affiliate_id = table.Column<long>(type: "bigint", nullable: false),
                    referral_id = table.Column<long>(type: "bigint", nullable: false),
                    service_id = table.Column<long>(type: "bigint", nullable: false),
                    document_id = table.Column<long>(type: "bigint", nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    @base = table.Column<decimal>(name: "base", type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    rate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    recurring = table.Column<bool>(type: "boolean", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    reversed = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    release_on = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reversed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    imported = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commissions", x => x.id);
                    table.CheckConstraint("ck_commissions_amounts", "amount > 0 AND reversed >= 0 AND reversed <= amount");
                    table.ForeignKey(
                        name: "fk_commissions_affiliate_affiliate_id",
                        column: x => x.affiliate_id,
                        principalSchema: "affiliates",
                        principalTable: "affiliates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_commissions_affiliate_referral_referral_id",
                        column: x => x.referral_id,
                        principalSchema: "affiliates",
                        principalTable: "referrals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_commissions_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "billing",
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_commissions_services_service_id",
                        column: x => x.service_id,
                        principalSchema: "services",
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                schema: "affiliates",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    affiliate_id = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    commission_id = table.Column<long>(type: "bigint", nullable: true),
                    payout_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_entries", x => x.id);
                    table.CheckConstraint("ck_ledger_entries_amount", "amount <> 0");
                    table.ForeignKey(
                        name: "fk_ledger_entries_affiliate_payout_payout_id",
                        column: x => x.payout_id,
                        principalSchema: "affiliates",
                        principalTable: "payouts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ledger_entries_affiliates_affiliate_id",
                        column: x => x.affiliate_id,
                        principalSchema: "affiliates",
                        principalTable: "affiliates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ledger_entries_commissions_commission_id",
                        column: x => x.commission_id,
                        principalSchema: "affiliates",
                        principalTable: "commissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_affiliates_account_id",
                schema: "affiliates",
                table: "affiliates",
                column: "account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_affiliates_code",
                schema: "affiliates",
                table: "affiliates",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_affiliates_status",
                schema: "affiliates",
                table: "affiliates",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_attributions_affiliate_id",
                schema: "affiliates",
                table: "attributions",
                column: "affiliate_id");

            migrationBuilder.CreateIndex(
                name: "ix_clicks_affiliate_id_at",
                schema: "affiliates",
                table: "clicks",
                columns: new[] { "affiliate_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_commission_rules_affiliate_id",
                schema: "affiliates",
                table: "commission_rules",
                column: "affiliate_id");

            migrationBuilder.CreateIndex(
                name: "ix_commission_rules_product_id_affiliate_id_currency",
                schema: "affiliates",
                table: "commission_rules",
                columns: new[] { "product_id", "affiliate_id", "currency" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_commissions_affiliate_id",
                schema: "affiliates",
                table: "commissions",
                column: "affiliate_id");

            migrationBuilder.CreateIndex(
                name: "ix_commissions_document_id_service_id",
                schema: "affiliates",
                table: "commissions",
                columns: new[] { "document_id", "service_id" },
                unique: true,
                filter: "status <> 'Reversed' AND document_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_commissions_referral_id",
                schema: "affiliates",
                table: "commissions",
                column: "referral_id");

            migrationBuilder.CreateIndex(
                name: "ix_commissions_service_id",
                schema: "affiliates",
                table: "commissions",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_commissions_status_release_on",
                schema: "affiliates",
                table: "commissions",
                columns: new[] { "status", "release_on" });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_affiliate_id_currency",
                schema: "affiliates",
                table: "ledger_entries",
                columns: new[] { "affiliate_id", "currency" });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_bonus",
                schema: "affiliates",
                table: "ledger_entries",
                column: "affiliate_id",
                unique: true,
                filter: "kind = 'Bonus'");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_commission_id",
                schema: "affiliates",
                table: "ledger_entries",
                column: "commission_id");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_payout_id",
                schema: "affiliates",
                table: "ledger_entries",
                column: "payout_id");

            migrationBuilder.CreateIndex(
                name: "ix_payouts_affiliate_id",
                schema: "affiliates",
                table: "payouts",
                column: "affiliate_id");

            migrationBuilder.CreateIndex(
                name: "ix_payouts_credit_entry_id",
                schema: "affiliates",
                table: "payouts",
                column: "credit_entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_payouts_status",
                schema: "affiliates",
                table: "payouts",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_referrals_account_id",
                schema: "affiliates",
                table: "referrals",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_referrals_affiliate_id",
                schema: "affiliates",
                table: "referrals",
                column: "affiliate_id");

            migrationBuilder.CreateIndex(
                name: "ix_referrals_order_id",
                schema: "affiliates",
                table: "referrals",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_referrals_service_id",
                schema: "affiliates",
                table: "referrals",
                column: "service_id",
                unique: true);

            // El libro y los clics solo se agregan.
            migrationBuilder.Sql(AffiliatesSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AffiliatesSql.Down);

            migrationBuilder.DropTable(
                name: "attributions",
                schema: "affiliates");

            migrationBuilder.DropTable(
                name: "clicks",
                schema: "affiliates");

            migrationBuilder.DropTable(
                name: "commission_rules",
                schema: "affiliates");

            migrationBuilder.DropTable(
                name: "ledger_entries",
                schema: "affiliates");

            migrationBuilder.DropTable(
                name: "program",
                schema: "affiliates");

            migrationBuilder.DropTable(
                name: "payouts",
                schema: "affiliates");

            migrationBuilder.DropTable(
                name: "commissions",
                schema: "affiliates");

            migrationBuilder.DropTable(
                name: "referrals",
                schema: "affiliates");

            migrationBuilder.DropTable(
                name: "affiliates",
                schema: "affiliates");
        }
    }
}
