using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Modules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pending_operation",
                schema: "services",
                table: "services",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "module_installations",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    base_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    signing_secret = table.Column<byte[]>(type: "bytea", nullable: false),
                    tls_fingerprint = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    manifest = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_module_installations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "service_secrets",
                schema: "services",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    service_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_secrets", x => x.id);
                    table.ForeignKey(
                        name: "fk_service_secrets_services_service_id",
                        column: x => x.service_id,
                        principalSchema: "services",
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "module_instances",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    installation_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    secrets = table.Column<byte[]>(type: "bytea", nullable: true),
                    state = table.Column<byte[]>(type: "bytea", nullable: true),
                    state_version = table.Column<int>(type: "integer", nullable: false),
                    browser_hosts = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_module_instances", x => x.id);
                    table.ForeignKey(
                        name: "fk_module_instances_module_installations_installation_id",
                        column: x => x.installation_id,
                        principalSchema: "platform",
                        principalTable: "module_installations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_module_installations_name",
                schema: "platform",
                table: "module_installations",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_module_instances_installation_id",
                schema: "platform",
                table: "module_instances",
                column: "installation_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_secrets_service_id_name",
                schema: "services",
                table: "service_secrets",
                columns: new[] { "service_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "module_instances",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "service_secrets",
                schema: "services");

            migrationBuilder.DropTable(
                name: "module_installations",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "pending_operation",
                schema: "services",
                table: "services");
        }
    }
}
