using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EndurecerFiscal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "synced_at",
                schema: "fiscal",
                table: "series",
                type: "timestamp with time zone",
                nullable: true);

            // El número comercial se asigna justo antes del commit, después de congelar el documento.
            migrationBuilder.Sql(NumberingSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(NumberingSql.Down);

            migrationBuilder.DropColumn(
                name: "synced_at",
                schema: "fiscal",
                table: "series");
        }
    }
}
