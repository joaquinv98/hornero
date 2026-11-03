using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Integracion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_jobs_entity_key_id",
                schema: "platform",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "ix_jobs_status_run_at",
                schema: "platform",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "ix_accounts_email",
                schema: "accounts",
                table: "accounts");

            // Un correo por cuenta, sin distinguir mayúsculas. Las cuentas sin correo (de reemplazo o importadas así) quedan afuera.
            migrationBuilder.Sql("CREATE UNIQUE INDEX ix_accounts_email ON accounts.accounts (lower(email)) WHERE email <> ''");

            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.EnsureSchema(
                name: "domains");

            migrationBuilder.EnsureSchema(
                name: "notifications");

            migrationBuilder.AddColumn<DateOnly>(
                name: "cancel_at",
                schema: "services",
                table: "services",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "no_auto_suspend",
                schema: "services",
                table: "services",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "suspend_reason",
                schema: "services",
                table: "services",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "fiscal",
                table: "series",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<decimal>(
                name: "refunded",
                schema: "payments",
                table: "payments",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "actor",
                schema: "platform",
                table: "jobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<IPAddress>(
                name: "actor_ip",
                schema: "platform",
                table: "jobs",
                type: "inet",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "actor_kind",
                schema: "platform",
                table: "jobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "trace_parent",
                schema: "platform",
                table: "jobs",
                type: "character varying(55)",
                maxLength: 55,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "activity_start",
                schema: "fiscal",
                table: "issuers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "address",
                schema: "fiscal",
                table: "issuers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gross_income_number",
                schema: "fiscal",
                table: "issuers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "vat_condition",
                schema: "fiscal",
                table: "issuers",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "fiscal",
                table: "emissions",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<decimal>(
                name: "refunded",
                schema: "billing",
                table: "documents",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "source_status",
                schema: "billing",
                table: "documents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "domain_id",
                schema: "billing",
                table: "document_lines",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "requests_per_minute",
                schema: "platform",
                table: "api_keys",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    action = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    entity = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true),
                    trace_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cancellation_requests",
                schema: "services",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    account_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    code_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    receipt = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cancellation_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    xml = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_pdfs",
                schema: "billing",
                columns: table => new
                {
                    document_id = table.Column<long>(type: "bigint", nullable: false),
                    fiscal_document_id = table.Column<long>(type: "bigint", nullable: false),
                    file_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_pdfs", x => x.document_id);
                    table.ForeignKey(
                        name: "fk_document_pdfs_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "billing",
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_document_pdfs_fiscal_documents_fiscal_document_id",
                        column: x => x.fiscal_document_id,
                        principalSchema: "fiscal",
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "domains",
                schema: "domains",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    registrar_instance_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    registered_on = table.Column<DateOnly>(type: "date", nullable: true),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    years = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    recurring_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    auto_renew = table.Column<bool>(type: "boolean", nullable: false),
                    dns_management = table.Column<bool>(type: "boolean", nullable: false),
                    email_forwarding = table.Column<bool>(type: "boolean", nullable: false),
                    id_protection = table.Column<bool>(type: "boolean", nullable: false),
                    nameservers = table.Column<string>(type: "jsonb", nullable: false),
                    external_ids = table.Column<string>(type: "jsonb", nullable: false),
                    payment_method = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    next_due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    paid_through = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_domains", x => x.id);
                    table.ForeignKey(
                        name: "fk_domains_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "accounts",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_domains_module_instances_registrar_instance_id",
                        column: x => x.registrar_instance_id,
                        principalSchema: "platform",
                        principalTable: "module_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outbound_emails",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    template = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    culture = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: true),
                    recipient = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    html = table.Column<string>(type: "text", nullable: false),
                    message_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    dedup_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbound_emails", x => x.id);
                    table.ForeignKey(
                        name: "fk_outbound_emails_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "accounts",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "portal_users",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    two_factor_required = table.Column<bool>(type: "boolean", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    security_stamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    authenticator_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    recovery_codes = table.Column<byte[]>(type: "bytea", nullable: true),
                    language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_portal_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "refunds",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    payment_id = table.Column<long>(type: "bigint", nullable: false),
                    origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    credit_note_id = table.Column<long>(type: "bigint", nullable: true),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    uncovered = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => x.id);
                    table.CheckConstraint("ck_refunds_amounts", "amount > 0 AND uncovered >= 0 AND uncovered <= amount");
                    table.ForeignKey(
                        name: "fk_refunds_documents_credit_note_id",
                        column: x => x.credit_note_id,
                        principalSchema: "billing",
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refunds_payments_payment_id",
                        column: x => x.payment_id,
                        principalSchema: "payments",
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "staff_tasks",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    module_instance_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    subject = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: false),
                    input = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_tasks", x => x.id);
                    table.ForeignKey(
                        name: "fk_staff_tasks_module_instances_module_instance_id",
                        column: x => x.module_instance_id,
                        principalSchema: "platform",
                        principalTable: "module_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "staff_users",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    permissions = table.Column<string[]>(type: "text[]", nullable: false),
                    oidc_subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    security_stamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    authenticator_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    recovery_codes = table.Column<byte[]>(type: "bytea", nullable: true),
                    language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "templates",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    culture = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tld_prices",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tld = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    registrar_instance_id = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    years = table.Column<int>(type: "integer", nullable: false),
                    register = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    renew = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    transfer = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tld_prices", x => x.id);
                    table.CheckConstraint("ck_tld_prices_years", "years BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "fk_tld_prices_module_instances_registrar_instance_id",
                        column: x => x.registrar_instance_id,
                        principalSchema: "platform",
                        principalTable: "module_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "operations",
                schema: "domains",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    domain_id = table.Column<long>(type: "bigint", nullable: false),
                    operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    input = table.Column<string>(type: "jsonb", nullable: false),
                    dedup_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    pending_operation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operations", x => x.id);
                    table.ForeignKey(
                        name: "fk_operations_domains_domain_id",
                        column: x => x.domain_id,
                        principalSchema: "domains",
                        principalTable: "domains",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_users",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    is_owner = table.Column<bool>(type: "boolean", nullable: false),
                    capabilities = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_users", x => x.id);
                    table.ForeignKey(
                        name: "fk_account_users_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "accounts",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_account_users_portal_user_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "portal_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_refunded",
                schema: "payments",
                table: "payments",
                sql: "refunded >= 0 AND refunded <= amount");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_live_entity_key",
                schema: "platform",
                table: "jobs",
                columns: new[] { "entity_key", "id" },
                filter: "status IN ('Pending', 'Running') AND entity_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_live_run_at",
                schema: "platform",
                table: "jobs",
                columns: new[] { "run_at", "id" },
                filter: "status IN ('Pending', 'Running')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_documents_refunded",
                schema: "billing",
                table: "documents",
                sql: "refunded >= 0 AND refunded <= credited");

            migrationBuilder.CreateIndex(
                name: "ix_document_lines_domain_id",
                schema: "billing",
                table: "document_lines",
                column: "domain_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_api_keys_requests_per_minute",
                schema: "platform",
                table: "api_keys",
                sql: "requests_per_minute >= 1");

            migrationBuilder.CreateIndex(
                name: "ix_account_users_account_id",
                schema: "identity",
                table: "account_users",
                column: "account_id",
                unique: true,
                filter: "is_owner");

            migrationBuilder.CreateIndex(
                name: "ix_account_users_account_id_user_id",
                schema: "identity",
                table: "account_users",
                columns: new[] { "account_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_account_users_user_id",
                schema: "identity",
                table: "account_users",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_action_created_at",
                schema: "platform",
                table: "audit_events",
                columns: new[] { "action", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_cancellation_requests_account_ids",
                schema: "services",
                table: "cancellation_requests",
                column: "account_ids")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_cancellation_requests_receipt",
                schema: "services",
                table: "cancellation_requests",
                column: "receipt",
                unique: true,
                filter: "receipt IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_cancellation_requests_reference",
                schema: "services",
                table: "cancellation_requests",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_pdfs_fiscal_document_id",
                schema: "billing",
                table: "document_pdfs",
                column: "fiscal_document_id");

            migrationBuilder.CreateIndex(
                name: "ix_domains_account_id",
                schema: "domains",
                table: "domains",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_domains_name",
                schema: "domains",
                table: "domains",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_domains_next_due_date",
                schema: "domains",
                table: "domains",
                column: "next_due_date");

            migrationBuilder.CreateIndex(
                name: "ix_domains_registrar_instance_id",
                schema: "domains",
                table: "domains",
                column: "registrar_instance_id");

            migrationBuilder.CreateIndex(
                name: "ix_operations_domain_id_dedup_key",
                schema: "domains",
                table: "operations",
                columns: new[] { "domain_id", "dedup_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbound_emails_account_id",
                schema: "notifications",
                table: "outbound_emails",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_emails_dedup_key",
                schema: "notifications",
                table: "outbound_emails",
                column: "dedup_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_portal_users_normalized_email",
                schema: "identity",
                table: "portal_users",
                column: "normalized_email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refunds_credit_note_id",
                schema: "payments",
                table: "refunds",
                column: "credit_note_id");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_payment_id_status",
                schema: "payments",
                table: "refunds",
                columns: new[] { "payment_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_staff_tasks_module_instance_id_idempotency_key",
                schema: "platform",
                table: "staff_tasks",
                columns: new[] { "module_instance_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_staff_tasks_status",
                schema: "platform",
                table: "staff_tasks",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_staff_tasks_subject",
                schema: "platform",
                table: "staff_tasks",
                column: "subject",
                unique: true,
                filter: "status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "ix_staff_users_normalized_email",
                schema: "identity",
                table: "staff_users",
                column: "normalized_email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_staff_users_oidc_subject",
                schema: "identity",
                table: "staff_users",
                column: "oidc_subject",
                unique: true,
                filter: "oidc_subject IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_templates_key_culture",
                schema: "notifications",
                table: "templates",
                columns: new[] { "key", "culture" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tld_prices_registrar_instance_id",
                schema: "catalog",
                table: "tld_prices",
                column: "registrar_instance_id");

            migrationBuilder.CreateIndex(
                name: "ix_tld_prices_tld_registrar_instance_id_currency_years_valid_f",
                schema: "catalog",
                table: "tld_prices",
                columns: new[] { "tld", "registrar_instance_id", "currency", "years", "valid_from" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_document_lines_domains_domain_id",
                schema: "billing",
                table: "document_lines",
                column: "domain_id",
                principalSchema: "domains",
                principalTable: "domains",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Auditoría que solo se agrega, función de métricas de negocio e índice de avisos sin procesar (D-15).
            migrationBuilder.Sql(ObservabilitySql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ObservabilitySql.Down);

            migrationBuilder.DropForeignKey(
                name: "fk_document_lines_domains_domain_id",
                schema: "billing",
                table: "document_lines");

            migrationBuilder.DropTable(
                name: "account_users",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "cancellation_requests",
                schema: "services");

            migrationBuilder.DropTable(
                name: "data_protection_keys",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "document_pdfs",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "operations",
                schema: "domains");

            migrationBuilder.DropTable(
                name: "outbound_emails",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "refunds",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "staff_tasks",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "staff_users",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "templates",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "tld_prices",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "portal_users",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "domains",
                schema: "domains");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_refunded",
                schema: "payments",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "ix_jobs_live_entity_key",
                schema: "platform",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "ix_jobs_live_run_at",
                schema: "platform",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_documents_refunded",
                schema: "billing",
                table: "documents");

            migrationBuilder.DropIndex(
                name: "ix_document_lines_domain_id",
                schema: "billing",
                table: "document_lines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_api_keys_requests_per_minute",
                schema: "platform",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "cancel_at",
                schema: "services",
                table: "services");

            migrationBuilder.DropColumn(
                name: "no_auto_suspend",
                schema: "services",
                table: "services");

            migrationBuilder.DropColumn(
                name: "suspend_reason",
                schema: "services",
                table: "services");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "fiscal",
                table: "series");

            migrationBuilder.DropColumn(
                name: "refunded",
                schema: "payments",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "actor",
                schema: "platform",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "actor_ip",
                schema: "platform",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "actor_kind",
                schema: "platform",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "trace_parent",
                schema: "platform",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "activity_start",
                schema: "fiscal",
                table: "issuers");

            migrationBuilder.DropColumn(
                name: "address",
                schema: "fiscal",
                table: "issuers");

            migrationBuilder.DropColumn(
                name: "gross_income_number",
                schema: "fiscal",
                table: "issuers");

            migrationBuilder.DropColumn(
                name: "vat_condition",
                schema: "fiscal",
                table: "issuers");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "fiscal",
                table: "emissions");

            migrationBuilder.DropColumn(
                name: "refunded",
                schema: "billing",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "source_status",
                schema: "billing",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "domain_id",
                schema: "billing",
                table: "document_lines");

            migrationBuilder.DropColumn(
                name: "requests_per_minute",
                schema: "platform",
                table: "api_keys");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_entity_key_id",
                schema: "platform",
                table: "jobs",
                columns: new[] { "entity_key", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_status_run_at",
                schema: "platform",
                table: "jobs",
                columns: new[] { "status", "run_at" });

            migrationBuilder.Sql("DROP INDEX accounts.ix_accounts_email");
            migrationBuilder.CreateIndex(
                name: "ix_accounts_email",
                schema: "accounts",
                table: "accounts",
                column: "email");
        }
    }
}
