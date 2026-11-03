using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Soporte : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "support");

            migrationBuilder.AddColumn<string>(
                name: "headers",
                schema: "notifications",
                table: "outbound_emails",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "departments",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    hidden = table.Column<bool>(type: "boolean", nullable: false),
                    clients_only = table.Column<bool>(type: "boolean", nullable: false),
                    auto_reply = table.Column<bool>(type: "boolean", nullable: false),
                    staff_alert_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    request_feedback = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    mail_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    imap_host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    imap_port = table.Column<int>(type: "integer", nullable: false),
                    imap_security = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    imap_user = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    imap_password = table.Column<byte[]>(type: "bytea", nullable: true),
                    imap_uid_validity = table.Column<long>(type: "bigint", nullable: false),
                    imap_last_uid = table.Column<long>(type: "bigint", nullable: false),
                    mail_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    mail_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_departments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mail_filters",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mail_filters", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "saved_replies",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    body = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_replies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.id);
                    table.CheckConstraint("ck_settings_single", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "escalation_rules",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    department_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    statuses = table.Column<string[]>(type: "text[]", nullable: false),
                    priorities = table.Column<string[]>(type: "text[]", nullable: false),
                    after_minutes = table.Column<int>(type: "integer", nullable: false),
                    new_department_id = table.Column<long>(type: "bigint", nullable: true),
                    new_priority = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    new_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    assign_staff_id = table.Column<long>(type: "bigint", nullable: true),
                    notify = table.Column<bool>(type: "boolean", nullable: false),
                    notify_staff_ids = table.Column<long[]>(type: "bigint[]", nullable: false),
                    note = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: true),
                    reply = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_escalation_rules", x => x.id);
                    table.CheckConstraint("ck_escalation_rules_after_minutes", "after_minutes > 0");
                    table.ForeignKey(
                        name: "fk_escalation_rules_departments_new_department_id",
                        column: x => x.new_department_id,
                        principalSchema: "support",
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_escalation_rules_staff_users_assign_staff_id",
                        column: x => x.assign_staff_id,
                        principalSchema: "identity",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tickets",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: true),
                    requester_user_id = table.Column<long>(type: "bigint", nullable: true),
                    requester_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    requester_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    cc = table.Column<string[]>(type: "text[]", nullable: false),
                    department_id = table.Column<long>(type: "bigint", nullable: false),
                    service_id = table.Column<long>(type: "bigint", nullable: true),
                    subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    priority = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    assigned_staff_id = table.Column<long>(type: "bigint", nullable: true),
                    reply_token = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    client_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    merged_into_id = table.Column<long>(type: "bigint", nullable: true),
                    feedback_token = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    imported = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tickets", x => x.id);
                    table.ForeignKey(
                        name: "fk_tickets_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "accounts",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_departments_department_id",
                        column: x => x.department_id,
                        principalSchema: "support",
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_portal_users_requester_user_id",
                        column: x => x.requester_user_id,
                        principalSchema: "identity",
                        principalTable: "portal_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_services_service_id",
                        column: x => x.service_id,
                        principalSchema: "services",
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_staff_users_assigned_staff_id",
                        column: x => x.assigned_staff_id,
                        principalSchema: "identity",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_tickets_merged_into_id",
                        column: x => x.merged_into_id,
                        principalSchema: "support",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "escalations",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    rule_id = table.Column<long>(type: "bigint", nullable: false),
                    ticket_id = table.Column<long>(type: "bigint", nullable: false),
                    since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_escalations", x => x.id);
                    table.ForeignKey(
                        name: "fk_escalations_escalation_rules_rule_id",
                        column: x => x.rule_id,
                        principalSchema: "support",
                        principalTable: "escalation_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_escalations_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "support",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "feedback",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ticket_id = table.Column<long>(type: "bigint", nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: false),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    staff_id = table.Column<long>(type: "bigint", nullable: true),
                    imported = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feedback", x => x.id);
                    table.CheckConstraint("ck_feedback_rating", "rating BETWEEN 0 AND 10");
                    table.ForeignKey(
                        name: "fk_feedback_staff_users_staff_id",
                        column: x => x.staff_id,
                        principalSchema: "identity",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_feedback_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "support",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inbound_emails",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    department_id = table.Column<long>(type: "bigint", nullable: false),
                    message_key = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: false),
                    sender = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ticket_id = table.Column<long>(type: "bigint", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbound_emails", x => x.id);
                    table.ForeignKey(
                        name: "fk_inbound_emails_departments_department_id",
                        column: x => x.department_id,
                        principalSchema: "support",
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_inbound_emails_ticket_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "support",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ticket_id = table.Column<long>(type: "bigint", nullable: false),
                    author = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: true),
                    staff_id = table.Column<long>(type: "bigint", nullable: true),
                    author_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    author_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    body = table.Column<string>(type: "text", nullable: false),
                    @internal = table.Column<bool>(name: "internal", type: "boolean", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    email_message_id = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_messages_portal_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "portal_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_messages_staff_users_staff_id",
                        column: x => x.staff_id,
                        principalSchema: "identity",
                        principalTable: "staff_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_messages_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalSchema: "support",
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attachments",
                schema: "support",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    message_id = table.Column<long>(type: "bigint", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    missing = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attachments", x => x.id);
                    table.ForeignKey(
                        name: "fk_attachments_ticket_message_message_id",
                        column: x => x.message_id,
                        principalSchema: "support",
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attachment_contents",
                schema: "support",
                columns: table => new
                {
                    attachment_id = table.Column<long>(type: "bigint", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attachment_contents", x => x.attachment_id);
                    table.ForeignKey(
                        name: "fk_attachment_contents_ticket_attachment_attachment_id",
                        column: x => x.attachment_id,
                        principalSchema: "support",
                        principalTable: "attachments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_message_id",
                schema: "support",
                table: "attachments",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_escalation_rules_assign_staff_id",
                schema: "support",
                table: "escalation_rules",
                column: "assign_staff_id");

            migrationBuilder.CreateIndex(
                name: "ix_escalation_rules_new_department_id",
                schema: "support",
                table: "escalation_rules",
                column: "new_department_id");

            migrationBuilder.CreateIndex(
                name: "ix_escalations_rule_id_ticket_id_since",
                schema: "support",
                table: "escalations",
                columns: new[] { "rule_id", "ticket_id", "since" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_escalations_ticket_id",
                schema: "support",
                table: "escalations",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_feedback_created_at",
                schema: "support",
                table: "feedback",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_feedback_staff_id",
                schema: "support",
                table: "feedback",
                column: "staff_id");

            migrationBuilder.CreateIndex(
                name: "ix_feedback_ticket_id",
                schema: "support",
                table: "feedback",
                column: "ticket_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inbound_emails_department_id",
                schema: "support",
                table: "inbound_emails",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_inbound_emails_message_key",
                schema: "support",
                table: "inbound_emails",
                column: "message_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inbound_emails_sender_received_at",
                schema: "support",
                table: "inbound_emails",
                columns: new[] { "sender", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_inbound_emails_ticket_id",
                schema: "support",
                table: "inbound_emails",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_messages_email_message_id",
                schema: "support",
                table: "messages",
                column: "email_message_id",
                filter: "email_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_messages_staff_id",
                schema: "support",
                table: "messages",
                column: "staff_id");

            migrationBuilder.CreateIndex(
                name: "ix_messages_ticket_id_id",
                schema: "support",
                table: "messages",
                columns: new[] { "ticket_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_user_id",
                schema: "support",
                table: "messages",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_account_id",
                schema: "support",
                table: "tickets",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_assigned_staff_id",
                schema: "support",
                table: "tickets",
                column: "assigned_staff_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_client_reference",
                schema: "support",
                table: "tickets",
                column: "client_reference",
                unique: true,
                filter: "client_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_department_id",
                schema: "support",
                table: "tickets",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_feedback_token",
                schema: "support",
                table: "tickets",
                column: "feedback_token",
                unique: true,
                filter: "feedback_token IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_merged_into_id",
                schema: "support",
                table: "tickets",
                column: "merged_into_id",
                filter: "merged_into_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_number",
                schema: "support",
                table: "tickets",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tickets_reply_token",
                schema: "support",
                table: "tickets",
                column: "reply_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tickets_requester_user_id",
                schema: "support",
                table: "tickets",
                column: "requester_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_service_id",
                schema: "support",
                table: "tickets",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_status_last_message_at",
                schema: "support",
                table: "tickets",
                columns: new[] { "status", "last_message_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attachment_contents",
                schema: "support");

            migrationBuilder.DropTable(
                name: "escalations",
                schema: "support");

            migrationBuilder.DropTable(
                name: "feedback",
                schema: "support");

            migrationBuilder.DropTable(
                name: "inbound_emails",
                schema: "support");

            migrationBuilder.DropTable(
                name: "mail_filters",
                schema: "support");

            migrationBuilder.DropTable(
                name: "saved_replies",
                schema: "support");

            migrationBuilder.DropTable(
                name: "settings",
                schema: "support");

            migrationBuilder.DropTable(
                name: "attachments",
                schema: "support");

            migrationBuilder.DropTable(
                name: "escalation_rules",
                schema: "support");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "support");

            migrationBuilder.DropTable(
                name: "tickets",
                schema: "support");

            migrationBuilder.DropTable(
                name: "departments",
                schema: "support");

            migrationBuilder.DropColumn(
                name: "headers",
                schema: "notifications",
                table: "outbound_emails");
        }
    }
}
