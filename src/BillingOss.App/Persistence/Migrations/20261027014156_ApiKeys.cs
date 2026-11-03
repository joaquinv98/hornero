using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_orders_account_id",
                schema: "orders",
                table: "orders");

            migrationBuilder.AddColumn<string>(
                name: "client_reference",
                schema: "orders",
                table: "orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "api_keys",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    prefix = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    scopes = table.Column<string>(type: "jsonb", nullable: false),
                    allowed_ips = table.Column<string>(type: "jsonb", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_orders_account_id_client_reference",
                schema: "orders",
                table: "orders",
                columns: new[] { "account_id", "client_reference" },
                unique: true,
                filter: "client_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_prefix",
                schema: "platform",
                table: "api_keys",
                column: "prefix",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_keys",
                schema: "platform");

            migrationBuilder.DropIndex(
                name: "ix_orders_account_id_client_reference",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "client_reference",
                schema: "orders",
                table: "orders");

            migrationBuilder.CreateIndex(
                name: "ix_orders_account_id",
                schema: "orders",
                table: "orders",
                column: "account_id");
        }
    }
}
