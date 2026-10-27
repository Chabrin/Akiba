using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akiba.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LoanOriginalTermMonths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OriginalTermMonths",
                schema: "akiba",
                table: "loans",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginalTermMonths",
                schema: "akiba",
                table: "loans");
        }
    }
}
