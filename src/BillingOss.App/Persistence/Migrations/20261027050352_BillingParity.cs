using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BillingParity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "paid_through",
                schema: "services",
                table: "services",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "termination_days",
                schema: "catalog",
                table: "products",
                type: "integer",
                nullable: true);

            // Los servicios que ya existían se toman como pagos hasta lo facturado.
            migrationBuilder.Sql("UPDATE services.services SET paid_through = next_due_date WHERE next_due_date IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "paid_through",
                schema: "services",
                table: "services");

            migrationBuilder.DropColumn(
                name: "termination_days",
                schema: "catalog",
                table: "products");
        }
    }
}
