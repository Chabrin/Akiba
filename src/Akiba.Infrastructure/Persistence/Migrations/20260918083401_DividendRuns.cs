using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akiba.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DividendRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dividend_runs",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    InterestEarned = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    BankCharges = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    BasisName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BasisExplanation = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ComputedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ComputedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ComputedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ApprovedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PostedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    PostedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WithdrawnReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dividend_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dividend_lines",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DividendRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SharesAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    BasisAmount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dividend_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dividend_lines_dividend_runs_DividendRunId",
                        column: x => x.DividendRunId,
                        principalSchema: "akiba",
                        principalTable: "dividend_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dividend_lines_DividendRunId_Sequence",
                schema: "akiba",
                table: "dividend_lines",
                columns: new[] { "DividendRunId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dividend_lines_MemberId",
                schema: "akiba",
                table: "dividend_lines",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_dividend_runs_Year",
                schema: "akiba",
                table: "dividend_runs",
                column: "Year");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dividend_lines",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "dividend_runs",
                schema: "akiba");
        }
    }
}
