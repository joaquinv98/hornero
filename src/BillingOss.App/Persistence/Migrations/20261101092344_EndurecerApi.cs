using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EndurecerApi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_orders_account_id_client_reference",
                schema: "orders",
                table: "orders");

            migrationBuilder.AddColumn<long>(
                name: "api_key_id",
                schema: "orders",
                table: "orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "request_fingerprint",
                schema: "orders",
                table: "orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_account_id",
                schema: "orders",
                table: "orders",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_api_key_id_account_id_client_reference",
                schema: "orders",
                table: "orders",
                columns: new[] { "api_key_id", "account_id", "client_reference" },
                unique: true,
                filter: "client_reference IS NOT NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddForeignKey(
                name: "fk_orders_api_keys_api_key_id",
                schema: "orders",
                table: "orders",
                column: "api_key_id",
                principalSchema: "platform",
                principalTable: "api_keys",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_orders_api_keys_api_key_id",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ix_orders_account_id",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ix_orders_api_key_id_account_id_client_reference",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "api_key_id",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "request_fingerprint",
                schema: "orders",
                table: "orders");

            migrationBuilder.CreateIndex(
                name: "ix_orders_account_id_client_reference",
                schema: "orders",
                table: "orders",
                columns: new[] { "account_id", "client_reference" },
                unique: true,
                filter: "client_reference IS NOT NULL");
        }
    }
}
