using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akiba.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RestructuredLoansHaveNoCheque : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "VoucherReference",
                schema: "akiba",
                table: "loans",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40);

            migrationBuilder.AlterColumn<string>(
                name: "ChequeSignatories",
                schema: "akiba",
                table: "loans",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(400)",
                oldMaxLength: 400);

            migrationBuilder.AlterColumn<string>(
                name: "ChequeNumber",
                schema: "akiba",
                table: "loans",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "ChequeDrawnOn",
                schema: "akiba",
                table: "loans",
                type: "date",
                nullable: true,
                oldClrType: typeof(DateOnly),
                oldType: "date");

            migrationBuilder.AlterColumn<decimal>(
                name: "ChequeAmount",
                schema: "akiba",
                table: "loans",
                type: "numeric(19,4)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,4)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "VoucherReference",
                schema: "akiba",
                table: "loans",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ChequeSignatories",
                schema: "akiba",
                table: "loans",
                type: "character varying(400)",
                maxLength: 400,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(400)",
                oldMaxLength: 400,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ChequeNumber",
                schema: "akiba",
                table: "loans",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "ChequeDrawnOn",
                schema: "akiba",
                table: "loans",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1),
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "ChequeAmount",
                schema: "akiba",
                table: "loans",
                type: "numeric(19,4)",
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,4)",
                oldNullable: true);
        }
    }
}
