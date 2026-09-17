using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;

namespace Akiba.Application.Abstractions;

/// <summary>Reads and writes ledger accounts.</summary>
public interface IAccountRepository
{
    Task<Account?> FindByIdAsync(AccountId id, CancellationToken cancellationToken = default);

    Task<Account?> FindByCodeAsync(AccountCode code, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Account>> AllAsync(CancellationToken cancellationToken = default);

    /// <summary>Every account belonging to a member or a loan.</summary>
    Task<IReadOnlyList<Account>> ForOwnerAsync(
        AccountOwner owner, CancellationToken cancellationToken = default);

    void Add(Account account);
}

/// <summary>Reads and writes accounting periods.</summary>
public interface IAccountingPeriodRepository
{
    Task<IReadOnlyList<AccountingPeriod>> AllAsync(CancellationToken cancellationToken = default);

    /// <summary>The periods covering a date - typically its month and its year.</summary>
    Task<IReadOnlyList<AccountingPeriod>> CoveringAsync(
        DateOnly date, CancellationToken cancellationToken = default);

    Task<AccountingPeriod?> FindMonthAsync(
        int year, int month, CancellationToken cancellationToken = default);

    void Add(AccountingPeriod period);

    void Update(AccountingPeriod period);
}

/// <summary>
/// Reads and writes journal entries.
/// </summary>
/// <remarks>
/// <para>
/// There is no Update and no Delete, and that is not an oversight. Entries are append-only.
/// A mistake is corrected by posting a reversal through
/// <see cref="JournalEntry.Reverse"/>, which leaves the original visible with its reversal
/// beside it.
/// </para>
/// <para>
/// <see cref="AddAsync"/> is the single boundary every entry passes through on its way to
/// storage, which is where the closed-period rule is enforced. Putting that check at each
/// call site would mean the one call site that forgot is the one that corrupts a closed month.
/// </para>
/// </remarks>
public interface IJournalRepository
{
    /// <summary>
    /// Records an entry.
    /// </summary>
    /// <exception cref="ClosedPeriodException">
    /// The entry is dated in a closed month or year.
    /// </exception>
    Task AddAsync(JournalEntry entry, CancellationToken cancellationToken = default);

    Task<JournalEntry?> FindByIdAsync(
        JournalEntryId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every entry touching an account, up to and including a date.
    /// </summary>
    /// <remarks>
    /// This is what a derived balance is built from. It returns entries rather than a total,
    /// because deciding what they add up to is the domain's job, not the repository's.
    /// </remarks>
    Task<IReadOnlyList<JournalEntry>> ForAccountAsOfAsync(
        AccountId accountId, DateOnly asAt, CancellationToken cancellationToken = default);

    /// <summary>Every entry up to and including a date, for a trial balance.</summary>
    Task<IReadOnlyList<JournalEntry>> AsOfAsync(
        DateOnly asAt, CancellationToken cancellationToken = default);

    /// <summary>Every entry dated within a period, for an income and expenditure account.</summary>
    Task<IReadOnlyList<JournalEntry>> BetweenAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

/// <summary>
/// Derives the balances an official actually asks for.
/// </summary>
/// <remarks>
/// A thin convenience over <see cref="IJournalRepository"/> and
/// <see cref="Domain.Ledger.LedgerBalances"/>. It exists so a handler asking "what is this
/// member's shareholding?" does not have to fetch entries and sum them itself - not because
/// a balance is stored anywhere.
/// </remarks>
public interface IBalanceQueries
{
    Task<Money> SignedBalanceAsAtAsync(
        AccountId accountId, DateOnly asAt, CancellationToken cancellationToken = default);

    Task<Money> NaturalBalanceAsAtAsync(
        AccountId accountId, DateOnly asAt, CancellationToken cancellationToken = default);

    /// <summary>Should always be zero. If it is not, something wrote to the database directly.</summary>
    Task<Money> TrialBalanceDifferenceAsAtAsync(
        DateOnly asAt, CancellationToken cancellationToken = default);
}
