using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akiba.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChequeBookControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ChequeBookId",
                schema: "akiba",
                table: "loan_applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ChequeLeafId",
                schema: "akiba",
                table: "loan_applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentVoucherReference",
                schema: "akiba",
                table: "loan_applications",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreparedLoanNumber",
                schema: "akiba",
                table: "loan_applications",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReservedChequeNumber",
                schema: "akiba",
                table: "loan_applications",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "VoucherPreparedOn",
                schema: "akiba",
                table: "loan_applications",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VoucherRevision",
                schema: "akiba",
                table: "loan_applications",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "cheque_books",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BankAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    IsClosed = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cheque_books", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cheque_books_accounts_BankAccountId",
                        column: x => x.BankAccountId,
                        principalSchema: "akiba",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cheque_leaves",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChequeBookId = table.Column<Guid>(type: "uuid", nullable: false),
                    BankAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ReservedForApplicationId = table.Column<Guid>(type: "uuid", nullable: true),
                    IssuedForLoanId = table.Column<Guid>(type: "uuid", nullable: true),
                    VoidReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cheque_leaves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cheque_leaves_accounts_BankAccountId",
                        column: x => x.BankAccountId,
                        principalSchema: "akiba",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cheque_leaves_cheque_books_ChequeBookId",
                        column: x => x.ChequeBookId,
                        principalSchema: "akiba",
                        principalTable: "cheque_books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cheque_leaves_loan_applications_ReservedForApplicationId",
                        column: x => x.ReservedForApplicationId,
                        principalSchema: "akiba",
                        principalTable: "loan_applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cheque_leaves_loans_IssuedForLoanId",
                        column: x => x.IssuedForLoanId,
                        principalSchema: "akiba",
                        principalTable: "loans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_loan_applications_ChequeBookId",
                schema: "akiba",
                table: "loan_applications",
                column: "ChequeBookId");

            migrationBuilder.CreateIndex(
                name: "IX_loan_applications_ChequeLeafId",
                schema: "akiba",
                table: "loan_applications",
                column: "ChequeLeafId");

            migrationBuilder.CreateIndex(
                name: "IX_loan_applications_PaymentVoucherReference",
                schema: "akiba",
                table: "loan_applications",
                column: "PaymentVoucherReference",
                unique: true,
                filter: "\"PaymentVoucherReference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_cheque_books_BankAccountId_BookReference",
                schema: "akiba",
                table: "cheque_books",
                columns: new[] { "BankAccountId", "BookReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cheque_leaves_BankAccountId_Number",
                schema: "akiba",
                table: "cheque_leaves",
                columns: new[] { "BankAccountId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cheque_leaves_ChequeBookId",
                schema: "akiba",
                table: "cheque_leaves",
                column: "ChequeBookId");

            migrationBuilder.CreateIndex(
                name: "IX_cheque_leaves_IssuedForLoanId",
                schema: "akiba",
                table: "cheque_leaves",
                column: "IssuedForLoanId");

            migrationBuilder.CreateIndex(
                name: "IX_cheque_leaves_ReservedForApplicationId",
                schema: "akiba",
                table: "cheque_leaves",
                column: "ReservedForApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_cheque_leaves_Status_ChequeBookId",
                schema: "akiba",
                table: "cheque_leaves",
                columns: new[] { "Status", "ChequeBookId" });

            migrationBuilder.AddForeignKey(
                name: "FK_loan_applications_cheque_books_ChequeBookId",
                schema: "akiba",
                table: "loan_applications",
                column: "ChequeBookId",
                principalSchema: "akiba",
                principalTable: "cheque_books",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_loan_applications_cheque_leaves_ChequeLeafId",
                schema: "akiba",
                table: "loan_applications",
                column: "ChequeLeafId",
                principalSchema: "akiba",
                principalTable: "cheque_leaves",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_loan_applications_cheque_books_ChequeBookId",
                schema: "akiba",
                table: "loan_applications");

            migrationBuilder.DropForeignKey(
                name: "FK_loan_applications_cheque_leaves_ChequeLeafId",
                schema: "akiba",
                table: "loan_applications");

            migrationBuilder.DropTable(
                name: "cheque_leaves",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "cheque_books",
                schema: "akiba");

            migrationBuilder.DropIndex(
                name: "IX_loan_applications_ChequeBookId",
                schema: "akiba",
                table: "loan_applications");

            migrationBuilder.DropIndex(
                name: "IX_loan_applications_ChequeLeafId",
                schema: "akiba",
                table: "loan_applications");

            migrationBuilder.DropIndex(
                name: "IX_loan_applications_PaymentVoucherReference",
                schema: "akiba",
                table: "loan_applications");

            migrationBuilder.DropColumn(
                name: "ChequeBookId",
                schema: "akiba",
                table: "loan_applications");

            migrationBuilder.DropColumn(
                name: "ChequeLeafId",
                schema: "akiba",
                table: "loan_applications");

            migrationBuilder.DropColumn(
                name: "PaymentVoucherReference",
                schema: "akiba",
                table: "loan_applications");

            migrationBuilder.DropColumn(
                name: "PreparedLoanNumber",
                schema: "akiba",
                table: "loan_applications");

            migrationBuilder.DropColumn(
                name: "ReservedChequeNumber",
                schema: "akiba",
                table: "loan_applications");

            migrationBuilder.DropColumn(
                name: "VoucherPreparedOn",
                schema: "akiba",
                table: "loan_applications");

            migrationBuilder.DropColumn(
                name: "VoucherRevision",
                schema: "akiba",
                table: "loan_applications");
        }
    }
}
