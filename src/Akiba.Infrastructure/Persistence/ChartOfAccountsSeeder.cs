using Akiba.Domain.Ledger;
using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Akiba.Infrastructure.Persistence;

/// <summary>
/// Creates the society-wide accounts if they are not there yet.
/// </summary>
/// <remarks>
/// <para>
/// Idempotent: it adds only what is missing, so it is safe to run on every startup. It seeds
/// the chart of accounts and nothing else. <b>No member, loan or balance is ever seeded</b> -
/// opening balances arrive through the migration tooling, which is a reviewed and signed-off
/// process, not a side effect of starting the application.
/// </para>
/// <para>
/// Per-member and per-loan accounts are not seeded either. They are created when a member
/// joins and when a loan is disbursed.
/// </para>
/// </remarks>
public sealed class ChartOfAccountsSeeder
{
    private readonly AkibaDbContext _context;
    private readonly ILogger<ChartOfAccountsSeeder> _logger;

    public ChartOfAccountsSeeder(AkibaDbContext context, ILogger<ChartOfAccountsSeeder> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>Adds any society account that does not already exist.</summary>
    /// <returns>How many accounts were created.</returns>
    public async Task<int> SeedAsync(CancellationToken cancellationToken = default)
    {
        var existingCodes = await _context.Accounts
            .Select(account => account.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var missing = ChartOfAccounts.SocietyAccounts
            .Where(seed => !existingCodes.Contains(seed.Code))
            .ToList();

        if (missing.Count == 0)
        {
            return 0;
        }

        foreach (var seed in missing)
        {
            _context.Accounts.Add(LedgerMapper.ToRow(seed.ToAccount()));
            _logger.LogInformation("Seeded ledger account {Code} {Name}", seed.Code, seed.Name);
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return missing.Count;
    }
}

/// <summary>Applies migrations and seeds the chart of accounts at startup.</summary>
public static class DatabaseStartup
{
    /// <summary>
    /// Brings the database up to date.
    /// </summary>
    /// <remarks>
    /// Migrating on startup suits Akiba specifically: one database, on one machine, upgraded
    /// by one person who is not a DBA. It would be the wrong choice for a system with several
    /// instances racing to migrate the same database, and Akiba is deliberately not that.
    /// </remarks>
    public static async Task MigrateAndSeedAsync(
        AkibaDbContext context,
        ChartOfAccountsSeeder seeder,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(seeder);
        ArgumentNullException.ThrowIfNull(logger);

        var pending = await context.Database
            .GetPendingMigrationsAsync(cancellationToken)
            .ConfigureAwait(false);

        var pendingList = pending.ToList();

        if (pendingList.Count > 0)
        {
            logger.LogInformation(
                "Applying {Count} pending migration(s): {Migrations}",
                pendingList.Count,
                string.Join(", ", pendingList));

            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        var seeded = await seeder.SeedAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Database ready. {Seeded} ledger account(s) seeded.", seeded);
    }
}
