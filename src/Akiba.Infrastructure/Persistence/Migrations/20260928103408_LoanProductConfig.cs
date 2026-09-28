using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Akiba.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LoanProductConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "loan_product_configs",
                schema: "akiba",
                columns: table => new
                {
                    Product = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InterestRate = table.Column<decimal>(type: "numeric(6,4)", nullable: false),
                    RateIsMonthly = table.Column<bool>(type: "boolean", nullable: false),
                    MaxPrincipalKes = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    FixedTermMonths = table.Column<int>(type: "integer", nullable: true),
                    MaximumTermMonths = table.Column<int>(type: "integer", nullable: true),
                    UsesGraduatedScale = table.Column<bool>(type: "boolean", nullable: false),
                    IsConfigured = table.Column<bool>(type: "boolean", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loan_product_configs", x => x.Product);
                });

            migrationBuilder.CreateTable(
                name: "term_scale_bands",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScaleVersion = table.Column<int>(type: "integer", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    MinPrincipalKes = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    MaxPrincipalKes = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    MaxTermMonths = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_term_scale_bands", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_term_scale_bands_ScaleVersion_EffectiveFrom",
                schema: "akiba",
                table: "term_scale_bands",
                columns: new[] { "ScaleVersion", "EffectiveFrom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "loan_product_configs",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "term_scale_bands",
                schema: "akiba");
        }
    }
}
