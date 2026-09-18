using Akiba.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Akiba.Infrastructure.Tests.Persistence;

/// <summary>
/// A real PostgreSQL database, shared by every test in the collection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never SQLite and never the in-memory provider.</b> Both differ from PostgreSQL in
/// exactly the places that matter to a ledger - numeric precision, transaction isolation,
/// constraint enforcement - so a green test against either would prove nothing about what
/// happens in Nairobi.
/// </para>
/// <para>
/// It connects to a PostgreSQL server you already have, rather than starting a container.
/// Akiba does not use Docker: it runs as a service against an installed PostgreSQL, so the
/// tests run the same way. CI provides the server as a workflow service.
/// </para>
/// <para>
/// The schema is created by running the real migrations rather than by <c>EnsureCreated</c>,
/// so the tests exercise the same SQL that will run against the live database and a migration
/// that would fail on deployment fails here first.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Overrides the connection string the tests use.</summary>
    public const string ConnectionStringVariable = "AKIBA_TEST_POSTGRES";

    /// <summary>
    /// Where the tests look when <see cref="ConnectionStringVariable"/> is not set: a local
    /// PostgreSQL with the default superuser. Development machines usually have one.
    /// </summary>
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=akiba_tests;Username=postgres;Password=postgres";

    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } configured
            ? configured
            : DefaultConnectionString;

    public async Task InitializeAsync()
    {
        await using var context = CreateContext();

        try
        {
            await context.Database.MigrateAsync();
        }
        catch (NpgsqlException exception)
        {
            throw new InvalidOperationException(
                $"""
                Could not reach PostgreSQL for the integration tests.

                Akiba's integration tests run against a real PostgreSQL server - never SQLite
                and never the in-memory provider, because both differ from PostgreSQL exactly
                where a ledger is sensitive.

                Point them at a server you have:

                    {ConnectionStringVariable}="Host=localhost;Port=5432;Database=akiba_tests;Username=postgres;Password=..."

                and create the database first:

                    CREATE DATABASE akiba_tests;

                Tried: {Redact(ConnectionString)}
                """,
                exception);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public AkibaDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AkibaDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new AkibaDbContext(options);
    }

    /// <summary>
    /// Clears every table between tests.
    /// </summary>
    /// <remarks>
    /// This is the only place in the entire system that deletes a financial record, and it
    /// exists solely so that tests do not see each other's rows. Nothing in
    /// <c>Akiba.Application</c> or <c>Akiba.Infrastructure</c> can delete a journal entry -
    /// <c>IJournalRepository</c> has no Delete, by design.
    ///
    /// Every table, not just the ledger ones. A table left out here does not fail loudly - it
    /// leaks rows into the next test, which then fails somewhere unrelated on a duplicate key.
    /// </remarks>
    public async Task ResetAsync()
    {
        await using var context = CreateContext();

        await context.Database.ExecuteSqlRawAsync(
            $"""
            TRUNCATE TABLE
                "{AkibaDbContext.Schema}"."bank_statement_lines",
                "{AkibaDbContext.Schema}"."bank_reconciliations",
                "{AkibaDbContext.Schema}"."journal_lines",
                "{AkibaDbContext.Schema}"."journal_entries",
                "{AkibaDbContext.Schema}"."accounting_periods",
                "{AkibaDbContext.Schema}"."receipt_allocations",
                "{AkibaDbContext.Schema}"."receipts",
                "{AkibaDbContext.Schema}"."guarantees",
                "{AkibaDbContext.Schema}"."approval_decisions",
                "{AkibaDbContext.Schema}"."loan_security",
                "{AkibaDbContext.Schema}"."attached_documents",
                "{AkibaDbContext.Schema}"."loans",
                "{AkibaDbContext.Schema}"."loan_applications",
                "{AkibaDbContext.Schema}"."zone_representatives",
                "{AkibaDbContext.Schema}"."zones",
                "{AkibaDbContext.Schema}"."borrowers",
                "{AkibaDbContext.Schema}"."accounts"
            RESTART IDENTITY CASCADE
            """);

        // Npgsql pools connections across contexts, and a pooled connection can hold a stale
        // view of a type that a migration has since changed.
        NpgsqlConnection.ClearAllPools();
    }

    /// <summary>Strips the password so a failure message can be pasted into a chat safely.</summary>
    private static string Redact(string connectionString) =>
        string.Join(
            ';',
            connectionString
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.TrimStart().StartsWith("Password", StringComparison.OrdinalIgnoreCase)
                    ? "Password=***"
                    : part));
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
