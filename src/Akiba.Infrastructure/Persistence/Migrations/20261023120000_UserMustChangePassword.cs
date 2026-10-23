using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akiba.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserMustChangePassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing officials keep false so they are not forced through the flow on
            // upgrade day. New accounts created after this migration default to true in
            // AkibaUser.MustChangePassword, so the chairman's next created account will
            // be prompted on first sign-in.
            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                schema: "akiba",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                schema: "akiba",
                table: "AspNetUsers");
        }
    }
}
