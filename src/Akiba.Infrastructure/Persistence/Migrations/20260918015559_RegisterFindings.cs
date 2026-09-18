using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akiba.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RegisterFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_borrowers_PayrollNumber",
                schema: "akiba",
                table: "borrowers");

            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                schema: "akiba",
                table: "borrowers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<string>(
                name: "NationalId",
                schema: "akiba",
                table: "borrowers",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(12)",
                oldMaxLength: 12);

            migrationBuilder.CreateIndex(
                name: "IX_borrowers_PayrollNumber",
                schema: "akiba",
                table: "borrowers",
                column: "PayrollNumber",
                unique: true,
                filter: "\"PayrollNumber\" IS NOT NULL AND \"PayrollNumber\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_borrowers_PayrollNumber",
                schema: "akiba",
                table: "borrowers");

            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                schema: "akiba",
                table: "borrowers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NationalId",
                schema: "akiba",
                table: "borrowers",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(12)",
                oldMaxLength: 12,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_borrowers_PayrollNumber",
                schema: "akiba",
                table: "borrowers",
                column: "PayrollNumber",
                unique: true,
                filter: "\"PayrollNumber\" IS NOT NULL");
        }
    }
}
