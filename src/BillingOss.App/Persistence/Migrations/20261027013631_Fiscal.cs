using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BillingOss.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Fiscal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "fiscal");

            migrationBuilder.CreateTable(
                name: "issuers",
                schema: "fiscal",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tax_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    legal_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    module_instance_id = table.Column<long>(type: "bigint", nullable: false),
                    point_of_sale = table.Column<int>(type: "integer", nullable: false),
                    export_point_of_sale = table.Column<int>(type: "integer", nullable: true),
                    type_map = table.Column<string>(type: "jsonb", nullable: false),
                    class_a_conditions = table.Column<string>(type: "jsonb", nullable: false),
                    moment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_issuers", x => x.id);
                    table.ForeignKey(
                        name: "fk_issuers_module_instances_module_instance_id",
                        column: x => x.module_instance_id,
                        principalSchema: "platform",
                        principalTable: "module_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                schema: "fiscal",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    document_id = table.Column<long>(type: "bigint", nullable: false),
                    emission_id = table.Column<long>(type: "bigint", nullable: true),
                    issuer_id = table.Column<long>(type: "bigint", nullable: false),
                    point_of_sale = table.Column<int>(type: "integer", nullable: false),
                    type_code = table.Column<int>(type: "integer", nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    authorization_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    authorization_due = table.Column<DateOnly>(type: "date", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    imported = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents1", x => x.id);
                    table.ForeignKey(
                        name: "fk_documents_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "billing",
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_documents_issuer_profiles_issuer_id",
                        column: x => x.issuer_id,
                        principalSchema: "fiscal",
                        principalTable: "issuers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "emissions",
                schema: "fiscal",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    document_id = table.Column<long>(type: "bigint", nullable: false),
                    issuer_id = table.Column<long>(type: "bigint", nullable: false),
                    point_of_sale = table.Column<int>(type: "integer", nullable: false),
                    type_code = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    proposed_number = table.Column<long>(type: "bigint", nullable: true),
                    date = table.Column<DateOnly>(type: "date", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    codes = table.Column<string>(type: "jsonb", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_emissions", x => x.id);
                    table.ForeignKey(
                        name: "fk_emissions_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "billing",
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_emissions_issuer_profiles_issuer_id",
                        column: x => x.issuer_id,
                        principalSchema: "fiscal",
                        principalTable: "issuers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "series",
                schema: "fiscal",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    issuer_id = table.Column<long>(type: "bigint", nullable: false),
                    point_of_sale = table.Column<int>(type: "integer", nullable: false),
                    type_code = table.Column<int>(type: "integer", nullable: false),
                    last_number = table.Column<long>(type: "bigint", nullable: false),
                    last_date = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_series", x => x.id);
                    table.ForeignKey(
                        name: "fk_series_issuer_profiles_issuer_id",
                        column: x => x.issuer_id,
                        principalSchema: "fiscal",
                        principalTable: "issuers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_documents_document_id",
                schema: "fiscal",
                table: "documents",
                column: "document_id",
                unique: true,
                filter: "NOT imported");

            migrationBuilder.CreateIndex(
                name: "ix_documents_issuer_id_point_of_sale_type_code_number",
                schema: "fiscal",
                table: "documents",
                columns: new[] { "issuer_id", "point_of_sale", "type_code", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_emissions_document_id",
                schema: "fiscal",
                table: "emissions",
                column: "document_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_emissions_issuer_id",
                schema: "fiscal",
                table: "emissions",
                column: "issuer_id");

            migrationBuilder.CreateIndex(
                name: "ix_issuers_module_instance_id",
                schema: "fiscal",
                table: "issuers",
                column: "module_instance_id");

            migrationBuilder.CreateIndex(
                name: "ix_series_issuer_id_point_of_sale_type_code",
                schema: "fiscal",
                table: "series",
                columns: new[] { "issuer_id", "point_of_sale", "type_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "documents",
                schema: "fiscal");

            migrationBuilder.DropTable(
                name: "emissions",
                schema: "fiscal");

            migrationBuilder.DropTable(
                name: "series",
                schema: "fiscal");

            migrationBuilder.DropTable(
                name: "issuers",
                schema: "fiscal");
        }
    }
}
