using Akiba.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Akiba.Infrastructure.Tests.Persistence;

/// <summary>
/// A real PostgreSQL instance, shared by every test in the collection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never SQLite and never the in-memory provider.</b> Both differ from PostgreSQL in
/// exactly the places that matter to a ledger - numeric precision, transaction isolation,
/// constraint enforcement - so a green test against either would prove nothing about what
/// happens in Nairobi.
/// </para>
/// <para>
/// By default the instance is a container started by Testcontainers, which is what CI uses
/// and what everybody should use. Where <c>AKIBA_TEST_POSTGRES</c> is set, that connection
/// string is used instead, so a developer whose Docker daemon is not running can still run
/// these tests against a PostgreSQL they already have. That is an escape hatch for a machine,
/// not a relaxation of the rule: it is still a real PostgreSQL server, running real
/// migrations, and the tests are identical either way.
/// </para>
/// <para>
/// The schema is created by running the real migrations rather than by <c>EnsureCreated</c>,
/// so the tests exercise the same SQL that will run against the live database and a migration
/// that would fail on deployment fails here first.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>
    /// Points the tests at an existing PostgreSQL instead of starting a container.
    /// </summary>
    public const string ConnectionStringVariable = "AKIBA_TEST_POSTGRES";

    private readonly PostgreSqlContainer? _container;
    private readonly string? _externalConnectionString;

    public PostgresFixture()
    {
        _externalConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (string.IsNullOrWhiteSpace(_externalConnectionString))
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine")
                .WithDatabase("akiba_tests")
                .WithUsername("akiba")
                .WithPassword("akiba-tests-only")
                .Build();
        }
    }

    public string ConnectionString =>
        _container?.GetConnectionString() ?? _externalConnectionString!;

    /// <summary>True when running against a container rather than a developer's own server.</summary>
    public bool UsesContainer => _container is not null;

    public async Task InitializeAsync()
    {
        if (_container is not null)
        {
            await _container.StartAsync();
        }

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public AkibaDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AkibaDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new AkibaDbContext(options);
    }

    /// <summary>
    /// Clears the ledger between tests.
    /// </summary>
    /// <remarks>
    /// This is the only place in the entire system that deletes a financial record, and it
    /// exists solely so that tests do not see each other's entries. Nothing in
    /// <c>Akiba.Application</c> or <c>Akiba.Infrastructure</c> can delete a journal entry -
    /// <c>IJournalRepository</c> has no Delete, by design.
    /// </remarks>
    public async Task ResetAsync()
    {
        await using var context = CreateContext();

        // Every table, not just the ledger ones. A table left out here does not fail loudly -
        // it leaks rows into the next test, which then fails somewhere unrelated on a
        // duplicate key.
        await context.Database.ExecuteSqlRawAsync(
            $"""
            TRUNCATE TABLE
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
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
