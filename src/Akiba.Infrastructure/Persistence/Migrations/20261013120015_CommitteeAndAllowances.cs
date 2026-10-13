using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akiba.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CommitteeAndAllowances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "OtherExpenses",
                schema: "akiba",
                table: "dividend_runs",
                type: "numeric(19,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "committee_appointments",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PositionName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    HolderName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AppointedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Term = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SuspendedReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EndedOn = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_committee_appointments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "committee_positions",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AllowanceAmount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    AllowanceCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Frequency = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_committee_positions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "election_records",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HeldOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Term = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_election_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "election_outcomes",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ElectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PositionName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ElectedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_election_outcomes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_election_outcomes_election_records_ElectionId",
                        column: x => x.ElectionId,
                        principalSchema: "akiba",
                        principalTable: "election_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_committee_appointments_PositionId_Status",
                schema: "akiba",
                table: "committee_appointments",
                columns: new[] { "PositionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_committee_positions_Name",
                schema: "akiba",
                table: "committee_positions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_election_outcomes_ElectionId_PositionId",
                schema: "akiba",
                table: "election_outcomes",
                columns: new[] { "ElectionId", "PositionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "committee_appointments",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "committee_positions",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "election_outcomes",
                schema: "akiba");

            migrationBuilder.DropTable(
                name: "election_records",
                schema: "akiba");

            migrationBuilder.DropColumn(
                name: "OtherExpenses",
                schema: "akiba",
                table: "dividend_runs");
        }
    }
}
