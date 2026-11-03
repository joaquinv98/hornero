using System;
using BillingOss.App.Platform;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Dominios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Altera domains.domains, que ya existe: no espera un lock más de cinco segundos, y el índice único de los
            // nombres vivos la endurece, así que declara la versión mínima del núcleo (sección 7.4 de D-20).
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s'");
            migrationBuilder.RequireCoreVersion("0.1.0");

            migrationBuilder.DropIndex(
                name: "ix_domains_name",
                schema: "domains",
                table: "domains");

            migrationBuilder.AddColumn<byte[]>(
                name: "auth_code",
                schema: "domains",
                table: "domains",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "locked",
                schema: "domains",
                table: "domains",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "option_prices",
                schema: "domains",
                table: "domains",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "registrant",
                schema: "domains",
                table: "domains",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "registry_status",
                schema: "domains",
                table: "domains",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "synced_at",
                schema: "domains",
                table: "domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "domain_option_prices",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    option = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_domain_option_prices", x => x.id);
                    table.CheckConstraint("ck_domain_option_prices_price", "price >= 0");
                });

            migrationBuilder.CreateTable(
                name: "tld_options",
                schema: "catalog",
                columns: table => new
                {
                    tld = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    dns = table.Column<bool>(type: "boolean", nullable: false),
                    email_forwarding = table.Column<bool>(type: "boolean", nullable: false),
                    privacy = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tld_options", x => x.tld);
                });

            migrationBuilder.CreateIndex(
                name: "ux_domains_live_name",
                schema: "domains",
                table: "domains",
                column: "name",
                unique: true,
                filter: "status IN ('Pending', 'Active', 'Grace')");

            migrationBuilder.CreateIndex(
                name: "ix_domain_option_prices_option_currency_valid_from",
                schema: "catalog",
                table: "domain_option_prices",
                columns: new[] { "option", "currency", "valid_from" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "domain_option_prices",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "tld_options",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "ux_domains_live_name",
                schema: "domains",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "auth_code",
                schema: "domains",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "locked",
                schema: "domains",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "option_prices",
                schema: "domains",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "registrant",
                schema: "domains",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "registry_status",
                schema: "domains",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "synced_at",
                schema: "domains",
                table: "domains");

            migrationBuilder.CreateIndex(
                name: "ix_domains_name",
                schema: "domains",
                table: "domains",
                column: "name");
        }
    }
}
