using System;
using BillingOss.App.Platform;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModulosPaneles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "uuid",
                schema: "services",
                table: "services",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.CreateIndex(
                name: "ix_services_uuid",
                schema: "services",
                table: "services",
                column: "uuid",
                unique: true);

            // El índice único sobre una tabla que ya existía cuenta como endurecer (sección 7.4 de D-20), aunque la versión
            // anterior no escribe la columna y el valor por defecto es un uuid al azar.
            migrationBuilder.RequireCoreVersion("0.1.0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_services_uuid",
                schema: "services",
                table: "services");

            migrationBuilder.DropColumn(
                name: "uuid",
                schema: "services",
                table: "services");
        }
    }
}
