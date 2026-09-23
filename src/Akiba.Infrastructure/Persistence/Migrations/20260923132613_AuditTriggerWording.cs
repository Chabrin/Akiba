using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akiba.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditTriggerWording : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The message an official sees when something tries to edit history. It read
            // "Entries cannot be update once written" - lower(TG_OP) gives "update" and
            // "delete", neither of which fits that sentence. Wording that is visibly wrong
            // makes a reader doubt the thing it is protecting.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION akiba.audit_entries_are_immutable()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION
                        'The audit trail is append-only. Entries cannot be changed or removed '
                        'once written.';
                END;
                $$ LANGUAGE plpgsql;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
        }
    }
}
