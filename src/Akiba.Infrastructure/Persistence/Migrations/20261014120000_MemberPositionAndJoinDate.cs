using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akiba.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MemberPositionAndJoinDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "JoinedOn",
                schema: "akiba",
                table: "borrowers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PositionTitle",
                schema: "akiba",
                table: "borrowers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JoinedOn",
                schema: "akiba",
                table: "borrowers");

            migrationBuilder.DropColumn(
                name: "PositionTitle",
                schema: "akiba",
                table: "borrowers");
        }
    }
}
