using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akiba.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditTrail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_entries",
                schema: "akiba",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TableName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PrimaryKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Changes = table.Column<string>(type: "jsonb", nullable: true),
                    EntityValues = table.Column<string>(type: "jsonb", nullable: true),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_entries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_ActorUserId",
                schema: "akiba",
                table: "audit_entries",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_OccurredAtUtc",
                schema: "akiba",
                table: "audit_entries",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_TableName_PrimaryKey",
                schema: "akiba",
                table: "audit_entries",
                columns: new[] { "TableName", "PrimaryKey" });

            // The trail is immutable, and this is what makes that a fact rather than a claim.
            //
            // Nothing in Akiba maps an update or a delete on this table - the row type has no
            // repository and the DbSet is never written through. But "our code does not do it"
            // is a weaker promise than an auditor deserves, because the database has other
            // users: whoever holds the postgres password has a psql prompt.
            //
            // So the refusal lives in the database. Anybody trying to edit or remove history
            // gets an error naming the reason, including the person who wrote this.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION akiba.audit_entries_are_immutable()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION
                        'The audit trail is append-only. Entries cannot be % once written.',
                        lower(TG_OP);
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER audit_entries_no_update_or_delete
                BEFORE UPDATE OR DELETE ON akiba.audit_entries
                FOR EACH ROW EXECUTE FUNCTION akiba.audit_entries_are_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS audit_entries_no_update_or_delete ON akiba.audit_entries;");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS akiba.audit_entries_are_immutable();");

            migrationBuilder.DropTable(
                name: "audit_entries",
                schema: "akiba");
        }
    }
}
