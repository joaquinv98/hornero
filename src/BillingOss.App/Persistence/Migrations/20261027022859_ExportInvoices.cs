using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExportInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_emissions_issuer_id",
                schema: "fiscal",
                table: "emissions");

            migrationBuilder.AddColumn<string>(
                name: "payment_method",
                schema: "services",
                table: "services",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "settings",
                schema: "fiscal",
                table: "issuers",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<long>(
                name: "request_id",
                schema: "fiscal",
                table: "emissions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_emissions_issuer_id_request_id",
                schema: "fiscal",
                table: "emissions",
                columns: new[] { "issuer_id", "request_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_emissions_issuer_id_request_id",
                schema: "fiscal",
                table: "emissions");

            migrationBuilder.DropColumn(
                name: "payment_method",
                schema: "services",
                table: "services");

            migrationBuilder.DropColumn(
                name: "settings",
                schema: "fiscal",
                table: "issuers");

            migrationBuilder.DropColumn(
                name: "request_id",
                schema: "fiscal",
                table: "emissions");

            migrationBuilder.CreateIndex(
                name: "ix_emissions_issuer_id",
                schema: "fiscal",
                table: "emissions",
                column: "issuer_id");
        }
    }
}
