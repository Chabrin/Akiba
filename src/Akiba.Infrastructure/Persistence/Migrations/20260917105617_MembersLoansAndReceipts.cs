using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akiba.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MembersLoansAndReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "borrowers",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    GivenName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FamilyName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OtherNames = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    NationalId = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    MembershipNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PayrollNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: true),
                    SharesAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    EmploymentStatus = table.Column<int>(type: "integer", nullable: true),
                    ExitedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    IsLandlord = table.Column<bool>(type: "boolean", nullable: false),
                    IntroducedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_borrowers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "loan_applications",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BorrowerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: false),
                    Product = table.Column<int>(type: "integer", nullable: false),
                    RequestedPrincipal = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    RequestedTermMonths = table.Column<int>(type: "integer", nullable: true),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ConsiderationMonth = table.Column<DateOnly>(type: "date", nullable: false),
                    CutoffVersion = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DeclaredGrossSalary = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    ApprovedPrincipal = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    ApprovedInterest = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    ApprovedTermMonths = table.Column<int>(type: "integer", nullable: true),
                    ApprovedTermScaleVersion = table.Column<int>(type: "integer", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PropertyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PropertyLocation = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    MonthlyRentalIncome = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    NumberOfRentalUnits = table.Column<int>(type: "integer", nullable: true),
                    RentStatementsAttached = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loan_applications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "loans",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LoanNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BorrowerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivableAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Product = table.Column<int>(type: "integer", nullable: false),
                    Principal = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    Interest = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    TermMonths = table.Column<int>(type: "integer", nullable: false),
                    TermScaleVersion = table.Column<int>(type: "integer", nullable: false),
                    DisbursedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RestructuresLoanId = table.Column<Guid>(type: "uuid", nullable: true),
                    HasBeenRestructured = table.Column<bool>(type: "boolean", nullable: false),
                    SettledOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ChequeNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    VoucherReference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ChequeAmount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    ChequeDrawnOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ChequeSignatories = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_loans_loans_RestructuresLoanId",
                        column: x => x.RestructuresLoanId,
                        principalSchema: "akiba",
                        principalTable: "loans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receipts",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PayerNameOnSlip = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IdentifiedBorrowerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpectedClearanceOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ClearedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ReconciledOn = table.Column<DateOnly>(type: "date", nullable: true),
                    BankStatementReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "zones",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsOffice = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_zones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "approval_decisions",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LoanApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApproverUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApproverName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_decisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_approval_decisions_loan_applications_LoanApplicationId",
                        column: x => x.LoanApplicationId,
                        principalSchema: "akiba",
                        principalTable: "loan_applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attached_documents",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BorrowerId = table.Column<Guid>(type: "uuid", nullable: true),
                    LoanApplicationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attached_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_attached_documents_borrowers_BorrowerId",
                        column: x => x.BorrowerId,
                        principalSchema: "akiba",
                        principalTable: "borrowers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attached_documents_loan_applications_LoanApplicationId",
                        column: x => x.LoanApplicationId,
                        principalSchema: "akiba",
                        principalTable: "loan_applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loan_security",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LoanApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Details = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loan_security", x => x.Id);
                    table.ForeignKey(
                        name: "FK_loan_security_loan_applications_LoanApplicationId",
                        column: x => x.LoanApplicationId,
                        principalSchema: "akiba",
                        principalTable: "loan_applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guarantees",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LoanApplicationId = table.Column<Guid>(type: "uuid", nullable: true),
                    LoanId = table.Column<Guid>(type: "uuid", nullable: true),
                    GuarantorId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuarantorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    GuarantorPayrollNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GuaranteedAmount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    ShareValueAtSigning = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    SignedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    IsReleased = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guarantees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_guarantees_loan_applications_LoanApplicationId",
                        column: x => x.LoanApplicationId,
                        principalSchema: "akiba",
                        principalTable: "loan_applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guarantees_loans_LoanId",
                        column: x => x.LoanId,
                        principalSchema: "akiba",
                        principalTable: "loans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receipt_allocations",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Target = table.Column<int>(type: "integer", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    AllocatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AllocatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AllocatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReversedReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipt_allocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_receipt_allocations_receipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalSchema: "akiba",
                        principalTable: "receipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "zone_representatives",
                schema: "akiba",
                columns: table => new
                {
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_zone_representatives", x => new { x.ZoneId, x.MemberId });
                    table.ForeignKey(
                        name: "FK_zone_representatives_zones_ZoneId",
                        column: x => x.ZoneId,
                        principalSchema: "akiba",
                        principalTable: "zones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_approval_decisions_LoanApplicationId_ApproverUserId",
                schema: "akiba",
                table: "approval_decisions",
                columns: new[] { "LoanApplicationId", "ApproverUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attached_documents_BorrowerId",
                schema: "akiba",
                table: "attached_documents",
                column: "BorrowerId");

            migrationBuilder.CreateIndex(
                name: "IX_attached_documents_LoanApplicationId",
                schema: "akiba",
                table: "attached_documents",
                column: "LoanApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_borrowers_MembershipNumber",
                schema: "akiba",
                table: "borrowers",
                column: "MembershipNumber",
                unique: true,
                filter: "\"MembershipNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_borrowers_NationalId",
                schema: "akiba",
                table: "borrowers",
                column: "NationalId");

            migrationBuilder.CreateIndex(
                name: "IX_borrowers_PayrollNumber",
                schema: "akiba",
                table: "borrowers",
                column: "PayrollNumber",
                unique: true,
                filter: "\"PayrollNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_guarantees_GuarantorId",
                schema: "akiba",
                table: "guarantees",
                column: "GuarantorId");

            migrationBuilder.CreateIndex(
                name: "IX_guarantees_LoanApplicationId",
                schema: "akiba",
                table: "guarantees",
                column: "LoanApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_guarantees_LoanId",
                schema: "akiba",
                table: "guarantees",
                column: "LoanId");

            migrationBuilder.CreateIndex(
                name: "IX_loan_applications_BorrowerId",
                schema: "akiba",
                table: "loan_applications",
                column: "BorrowerId");

            migrationBuilder.CreateIndex(
                name: "IX_loan_applications_Status_ConsiderationMonth",
                schema: "akiba",
                table: "loan_applications",
                columns: new[] { "Status", "ConsiderationMonth" });

            migrationBuilder.CreateIndex(
                name: "IX_loan_security_LoanApplicationId",
                schema: "akiba",
                table: "loan_security",
                column: "LoanApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_loans_BorrowerId_Status",
                schema: "akiba",
                table: "loans",
                columns: new[] { "BorrowerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_loans_LoanNumber",
                schema: "akiba",
                table: "loans",
                column: "LoanNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_loans_RestructuresLoanId",
                schema: "akiba",
                table: "loans",
                column: "RestructuresLoanId");

            migrationBuilder.CreateIndex(
                name: "IX_receipt_allocations_ReceiptId_Sequence",
                schema: "akiba",
                table: "receipt_allocations",
                columns: new[] { "ReceiptId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_receipts_IdentifiedBorrowerId",
                schema: "akiba",
                table: "receipts",
                column: "IdentifiedBorrowerId");

            migrationBuilder.CreateIndex(
                name: "IX_receipts_Status_ReceivedOn",
                schema: "akiba",
                table: "receipts",
                columns: new[] { "Status", "ReceivedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_zones_Code",
                schema: "akiba",
                table: "zones",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approval_decisions",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "attached_documents",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "guarantees",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "loan_security",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "receipt_allocations",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "zone_representatives",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "borrowers",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "loans",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "loan_applications",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "receipts",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "zones",
                schema: "akiba");
        }
    }
}
