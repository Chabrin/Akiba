using Akiba.Application.Abstractions;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Akiba.Infrastructure.Persistence.Repositories;

/// <summary>
/// Stores and retrieves journal entries, and is where the closed-period rule is enforced.
/// </summary>
/// <remarks>
/// <para>
/// Note what this class does not have: an <c>Update</c> and a <c>Delete</c>. The ledger is
/// append-only, so those operations have no meaning here. A mistake is corrected by posting a
/// reversal.
/// </para>
/// <para>
/// <see cref="AddAsync"/> checks the period before anything is written. This is the single
/// boundary every entry passes through, which is why the check belongs here rather than in a
/// handler or a Blazor component - the one call site that forgot would be the one that
/// corrupts a month the treasurer had already signed off.
/// </para>
/// </remarks>
internal sealed class JournalRepository : IJournalRepository
{
    private readonly AkibaDbContext _context;

    public JournalRepository(AkibaDbContext context) => _context = context;

    public async Task AddAsync(JournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await EnsurePeriodIsOpenAsync(entry.EntryDate, cancellationToken).ConfigureAwait(false);

        await _context.JournalEntries
            .AddAsync(LedgerMapper.ToRow(entry), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<JournalEntry?> FindByIdAsync(
        JournalEntryId id,
        CancellationToken cancellationToken = default)
    {
        var row = await _context.JournalEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : LedgerMapper.ToDomain(row);
    }

    public async Task<IReadOnlyList<JournalEntry>> ForAccountAsOfAsync(
        AccountId accountId,
        DateOnly asAt,
        CancellationToken cancellationToken = default)
    {
        // Entries are fetched whole, with every line, rather than only the lines touching this
        // account. A caller deriving a balance sums the right lines itself, and a caller
        // showing a statement needs to see the other side of each entry - "Bank" against
        // "Member Shares" is what makes a line legible.
        var rows = await _context.JournalEntries
            .AsNoTracking()
            .Where(entry => entry.EntryDate <= asAt
                && entry.Lines.Any(line => line.AccountId == accountId.Value))
            .OrderBy(entry => entry.EntryDate)
            .ThenBy(entry => entry.PostedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(LedgerMapper.ToDomain)];
    }

    public async Task<IReadOnlyList<JournalEntry>> AsOfAsync(
        DateOnly asAt,
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.JournalEntries
            .AsNoTracking()
            .Where(entry => entry.EntryDate <= asAt)
            .OrderBy(entry => entry.EntryDate)
            .ThenBy(entry => entry.PostedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(LedgerMapper.ToDomain)];
    }

    public async Task<IReadOnlyList<JournalEntry>> BetweenAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            throw new ArgumentException(
                $"The period end {to:yyyy-MM-dd} is before its start {from:yyyy-MM-dd}.", nameof(to));
        }

        var rows = await _context.JournalEntries
            .AsNoTracking()
            .Where(entry => entry.EntryDate >= from && entry.EntryDate <= to)
            .OrderBy(entry => entry.EntryDate)
            .ThenBy(entry => entry.PostedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(LedgerMapper.ToDomain)];
    }

    private async Task EnsurePeriodIsOpenAsync(DateOnly entryDate, CancellationToken cancellationToken)
    {
        var closed = await _context.AccountingPeriods
            .AsNoTracking()
            .Where(period => period.Status == (int)AccountingPeriodStatus.Closed
                && period.Start <= entryDate
                && period.End >= entryDate)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (closed.Count == 0)
        {
            return;
        }

        var calendar = new PeriodCalendar([.. closed.Select(LedgerMapper.ToDomain)]);

        calendar.EnsureOpenForPosting(entryDate);
    }
}

/// <summary>
/// Derives the balances officials ask for, from the entries.
/// </summary>
/// <remarks>
/// Every method here fetches entries and hands them to
/// <see cref="Domain.Ledger.LedgerBalances"/>. Nothing is read from a stored total, because
/// there is no stored total.
/// </remarks>
internal sealed class BalanceQueries : IBalanceQueries
{
    private readonly AkibaDbContext _context;
    private readonly IJournalRepository _journal;

    public BalanceQueries(AkibaDbContext context, IJournalRepository journal)
    {
        _context = context;
        _journal = journal;
    }

    public async Task<Money> SignedBalanceAsAtAsync(
        AccountId accountId,
        DateOnly asAt,
        CancellationToken cancellationToken = default)
    {
        var entries = await _journal
            .ForAccountAsOfAsync(accountId, asAt, cancellationToken)
            .ConfigureAwait(false);

        return LedgerBalances.SignedBalanceAsAt(entries, accountId, asAt, Currency.Kes);
    }

    public async Task<Money> NaturalBalanceAsAtAsync(
        AccountId accountId,
        DateOnly asAt,
        CancellationToken cancellationToken = default)
    {
        var accountRow = await _context.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(account => account.Id == accountId.Value, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No account with id {accountId}.");

        var account = LedgerMapper.ToDomain(accountRow);

        var entries = await _journal
            .ForAccountAsOfAsync(accountId, asAt, cancellationToken)
            .ConfigureAwait(false);

        return LedgerBalances.NaturalBalanceAsAt(entries, account, asAt);
    }

    public async Task<Money> TrialBalanceDifferenceAsAtAsync(
        DateOnly asAt,
        CancellationToken cancellationToken = default)
    {
        var entries = await _journal.AsOfAsync(asAt, cancellationToken).ConfigureAwait(false);

        return LedgerBalances.TrialBalanceDifferenceAsAt(entries, asAt, Currency.Kes);
    }
}

internal sealed class AccountRepository : IAccountRepository
{
    private readonly AkibaDbContext _context;

    public AccountRepository(AkibaDbContext context) => _context = context;

    public async Task<Account?> FindByIdAsync(AccountId id, CancellationToken cancellationToken = default)
    {
        var row = await _context.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(account => account.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : LedgerMapper.ToDomain(row);
    }

    public async Task<Account?> FindByCodeAsync(
        AccountCode code,
        CancellationToken cancellationToken = default)
    {
        var row = await _context.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(account => account.Code == code.Value, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : LedgerMapper.ToDomain(row);
    }

    public async Task<IReadOnlyList<Account>> AllAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _context.Accounts
            .AsNoTracking()
            .OrderBy(account => account.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(LedgerMapper.ToDomain)];
    }

    public async Task<IReadOnlyList<Account>> ForOwnerAsync(
        AccountOwner owner,
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.Accounts
            .AsNoTracking()
            .Where(account => account.OwnerKind == (int)owner.Kind && account.OwnerId == owner.OwnerId)
            .OrderBy(account => account.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(LedgerMapper.ToDomain)];
    }

    public void Add(Account account) => _context.Accounts.Add(LedgerMapper.ToRow(account));
}

internal sealed class AccountingPeriodRepository : IAccountingPeriodRepository
{
    private readonly AkibaDbContext _context;

    public AccountingPeriodRepository(AkibaDbContext context) => _context = context;

    public async Task<IReadOnlyList<AccountingPeriod>> AllAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.AccountingPeriods
            .AsNoTracking()
            .OrderBy(period => period.Start)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(LedgerMapper.ToDomain)];
    }

    public async Task<IReadOnlyList<AccountingPeriod>> CoveringAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.AccountingPeriods
            .AsNoTracking()
            .Where(period => period.Start <= date && period.End >= date)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(LedgerMapper.ToDomain)];
    }

    public async Task<AccountingPeriod?> FindMonthAsync(
        int year,
        int month,
        CancellationToken cancellationToken = default)
    {
        var start = new DateOnly(year, month, 1);

        var row = await _context.AccountingPeriods
            .AsNoTracking()
            .FirstOrDefaultAsync(
                period => period.Kind == (int)AccountingPeriodKind.Month && period.Start == start,
                cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : LedgerMapper.ToDomain(row);
    }

    public void Add(AccountingPeriod period) =>
        _context.AccountingPeriods.Add(LedgerMapper.ToRow(period));

    public void Update(AccountingPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);

        var row = _context.AccountingPeriods.Find(period.Id.Value)
            ?? throw new InvalidOperationException($"No accounting period with id {period.Id}.");

        LedgerMapper.CopyInto(period, row);
    }
}
