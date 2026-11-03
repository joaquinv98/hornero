using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Pasarelas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pending_manifest",
                schema: "platform",
                table: "module_installations",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "account_id",
                schema: "payments",
                table: "attempts",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<string>(
                name: "consent",
                schema: "payments",
                table: "attempts",
                type: "jsonb",
                nullable: true);

            // Escrito a mano: los intentos que ya existían son todos del cliente. Con el "" que genera EF, leerlos fallaba
            // porque no es ningún valor de AttemptInitiator.
            migrationBuilder.AddColumn<string>(
                name: "initiator",
                schema: "payments",
                table: "attempts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Customer");

            migrationBuilder.AddColumn<bool>(
                name: "is_test",
                schema: "payments",
                table: "attempts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "outcome",
                schema: "payments",
                table: "attempts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "retry",
                schema: "payments",
                table: "attempts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "save_method",
                schema: "payments",
                table: "attempts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "saved_method_id",
                schema: "payments",
                table: "attempts",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "gateways",
                schema: "payments",
                columns: table => new
                {
                    module_instance_id = table.Column<long>(type: "bigint", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    currencies = table.Column<string[]>(type: "text[]", nullable: false),
                    environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    same_currency_cancel = table.Column<bool>(type: "boolean", nullable: false),
                    auto_charge = table.Column<bool>(type: "boolean", nullable: false),
                    last_test_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_test_ok = table.Column<bool>(type: "boolean", nullable: true),
                    last_test_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gateways", x => x.module_instance_id);
                    table.ForeignKey(
                        name: "fk_gateways_module_instances_module_instance_id",
                        column: x => x.module_instance_id,
                        principalSchema: "platform",
                        principalTable: "module_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "saved_methods",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    module_instance_id = table.Column<long>(type: "bigint", nullable: false),
                    customer_external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    method_external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    brand = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    exp_month = table.Column<int>(type: "integer", nullable: true),
                    exp_year = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    auto_charge = table.Column<bool>(type: "boolean", nullable: false),
                    consent = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_methods", x => x.id);
                    table.ForeignKey(
                        name: "fk_saved_methods_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "accounts",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_saved_methods_module_instances_module_instance_id",
                        column: x => x.module_instance_id,
                        principalSchema: "platform",
                        principalTable: "module_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    module_instance_id = table.Column<long>(type: "bigint", nullable: false),
                    reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    redirect_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    consent = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                    table.CheckConstraint("ck_subscriptions_amount", "amount >= 0");
                    table.ForeignKey(
                        name: "fk_subscriptions_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "accounts",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_subscriptions_module_instances_module_instance_id",
                        column: x => x.module_instance_id,
                        principalSchema: "platform",
                        principalTable: "module_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transfer_accounts",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    bank = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    holder = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    cuit = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    cbu = table.Column<string>(type: "character varying(22)", maxLength: 22, nullable: false),
                    alias = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    csv_map = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transfer_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bank_movements",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    transfer_account_id = table.Column<long>(type: "bigint", nullable: false),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    payer_cuit = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: true),
                    payer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    concept = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: true),
                    suggested_document_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    payment_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_movements", x => x.id);
                    table.CheckConstraint("ck_bank_movements_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_bank_movements_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "accounts",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_bank_movements_payments_payment_id",
                        column: x => x.payment_id,
                        principalSchema: "payments",
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_bank_movements_transfer_accounts_transfer_account_id",
                        column: x => x.transfer_account_id,
                        principalSchema: "payments",
                        principalTable: "transfer_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transfer_notices",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    document_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    transferred_on = table.Column<DateOnly>(type: "date", nullable: false),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    receipt = table.Column<byte[]>(type: "bytea", nullable: true),
                    receipt_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    receipt_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    movement_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transfer_notices", x => x.id);
                    table.CheckConstraint("ck_transfer_notices_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_transfer_notices_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "accounts",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transfer_notices_bank_movements_movement_id",
                        column: x => x.movement_id,
                        principalSchema: "payments",
                        principalTable: "bank_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attempts_saved_method_id",
                schema: "payments",
                table: "attempts",
                column: "saved_method_id");

            migrationBuilder.CreateIndex(
                name: "ix_bank_movements_account_id",
                schema: "payments",
                table: "bank_movements",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_bank_movements_payment_id",
                schema: "payments",
                table: "bank_movements",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_bank_movements_status",
                schema: "payments",
                table: "bank_movements",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_bank_movements_transfer_account_id_reference",
                schema: "payments",
                table: "bank_movements",
                columns: new[] { "transfer_account_id", "reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_saved_methods_auto_charge",
                schema: "payments",
                table: "saved_methods",
                column: "account_id",
                unique: true,
                filter: "auto_charge");

            migrationBuilder.CreateIndex(
                name: "ix_saved_methods_module_instance_id_method_external_id",
                schema: "payments",
                table: "saved_methods",
                columns: new[] { "module_instance_id", "method_external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_account_id_status",
                schema: "payments",
                table: "subscriptions",
                columns: new[] { "account_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_live",
                schema: "payments",
                table: "subscriptions",
                column: "account_id",
                unique: true,
                filter: "status <> 'Cancelled'");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_module_instance_id_external_id",
                schema: "payments",
                table: "subscriptions",
                columns: new[] { "module_instance_id", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_reference",
                schema: "payments",
                table: "subscriptions",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transfer_notices_account_id_status",
                schema: "payments",
                table: "transfer_notices",
                columns: new[] { "account_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_transfer_notices_movement_id",
                schema: "payments",
                table: "transfer_notices",
                column: "movement_id");

            migrationBuilder.AddForeignKey(
                name: "fk_attempts_saved_payment_methods_saved_method_id",
                schema: "payments",
                table: "attempts",
                column: "saved_method_id",
                principalSchema: "payments",
                principalTable: "saved_methods",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Escrito a mano: la clave foránea y el account_id que acepta nulos cambian payments.attempts, que ya existía
            // (sección 7.4 de D-20). Desde acá el esquema pide un núcleo con los cobros incluidos.
            Platform.UpdatesSql.RequireCoreVersion(migrationBuilder, "0.1.0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_attempts_saved_payment_methods_saved_method_id",
                schema: "payments",
                table: "attempts");

            migrationBuilder.DropTable(
                name: "gateways",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "saved_methods",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "subscriptions",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "transfer_notices",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "bank_movements",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "transfer_accounts",
                schema: "payments");

            migrationBuilder.DropIndex(
                name: "ix_attempts_saved_method_id",
                schema: "payments",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "pending_manifest",
                schema: "platform",
                table: "module_installations");

            migrationBuilder.DropColumn(
                name: "consent",
                schema: "payments",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "initiator",
                schema: "payments",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "is_test",
                schema: "payments",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "outcome",
                schema: "payments",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "retry",
                schema: "payments",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "save_method",
                schema: "payments",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "saved_method_id",
                schema: "payments",
                table: "attempts");

            migrationBuilder.AlterColumn<long>(
                name: "account_id",
                schema: "payments",
                table: "attempts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);
        }
    }
}
