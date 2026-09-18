using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akiba.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_reconciliations",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BankAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    To = table.Column<DateOnly>(type: "date", nullable: false),
                    OpeningBalance = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    ClosingBalance = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SignedOffByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SignedOffByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SignedOffAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_reconciliations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bank_reconciliations_accounts_BankAccountId",
                        column: x => x.BankAccountId,
                        principalSchema: "akiba",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bank_statement_lines",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BankReconciliationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    ValueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    BankReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    MatchedToId = table.Column<Guid>(type: "uuid", nullable: true),
                    MatchedToDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    NotOursReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_statement_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bank_statement_lines_bank_reconciliations_BankReconciliatio~",
                        column: x => x.BankReconciliationId,
                        principalSchema: "akiba",
                        principalTable: "bank_reconciliations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bank_reconciliations_BankAccountId_From_To",
                schema: "akiba",
                table: "bank_reconciliations",
                columns: new[] { "BankAccountId", "From", "To" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_reconciliations_To",
                schema: "akiba",
                table: "bank_reconciliations",
                column: "To");

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_lines_BankReconciliationId_LineNumber",
                schema: "akiba",
                table: "bank_statement_lines",
                columns: new[] { "BankReconciliationId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_lines_MatchedToId",
                schema: "akiba",
                table: "bank_statement_lines",
                column: "MatchedToId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_statement_lines",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "bank_reconciliations",
                schema: "akiba");
        }
    }
}
