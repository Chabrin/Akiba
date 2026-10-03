using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akiba.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharesOnHold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Q14: shares may be placed on hold when a member exits and their funds cannot yet
            // be released — outstanding loans, or guarantees that exceed the borrower's shares.
            // The clerk releases the hold manually once the obligations are resolved.
            migrationBuilder.AddColumn<bool>(
                name: "SharesOnHold",
                schema: "akiba",
                table: "borrowers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SharesOnHold",
                schema: "akiba",
                table: "borrowers");
        }
    }
}
