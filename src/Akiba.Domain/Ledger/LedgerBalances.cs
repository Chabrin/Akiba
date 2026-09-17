using Akiba.Domain.Financial;

namespace Akiba.Domain.Ledger;

/// <summary>
/// Derives balances by summing journal lines. This is the only way a balance is ever
/// produced in Akiba.
/// </summary>
/// <remarks>
/// <para>
/// There is no <c>Balance</c> column on <see cref="Account"/>, on a member, or on a loan.
/// A member's shareholding, a loan's outstanding balance and the bank position are all
/// computed here, from entries, as at a date.
/// </para>
/// <para>
/// That is what makes any historical figure reproducible. A member asking in December what
/// their shareholding was in June gets an answer by summing to 30 June - and gets the same
/// answer every time they ask, because nothing that made up the June figure was ever
/// overwritten.
/// </para>
/// <para>
/// These are pure functions over a sequence of entries. Fetching the right entries is the
/// repository's job; deciding what they add up to is the domain's.
/// </para>
/// </remarks>
public static class LedgerBalances
{
    /// <summary>
    /// The signed balance of an account as at a date, with debits positive.
    /// </summary>
    /// <remarks>
    /// Signed rather than natural, so that balances can be added together across account
    /// types without each caller having to know which side each account sits on. Use
    /// <see cref="NaturalBalanceAsAt"/> to present a figure to a person.
    /// </remarks>
    /// <param name="entries">The entries to consider. Entries dated after <paramref name="asAt"/> are ignored.</param>
    /// <param name="accountId">The account to balance.</param>
    /// <param name="asAt">The date to balance as at, inclusive.</param>
    /// <param name="currency">The currency of the result, used when there are no entries.</param>
    public static Money SignedBalanceAsAt(
        IEnumerable<JournalEntry> entries,
        AccountId accountId,
        DateOnly asAt,
        Currency currency)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return entries
            .Where(entry => entry.EntryDate <= asAt)
            .SelectMany(entry => entry.Lines)
            .Where(line => line.AccountId == accountId)
            .Sum(line => line.SignedAmount, currency);
    }

    /// <summary>
    /// The balance of an account as at a date, expressed the way an official reads it:
    /// positive when the account carries what it normally carries.
    /// </summary>
    /// <remarks>
    /// Member Shares is a liability, so a member who has contributed 120,000 has a signed
    /// balance of -120,000 and a natural balance of 120,000. The second figure is the one
    /// that goes on a statement.
    /// </remarks>
    public static Money NaturalBalanceAsAt(
        IEnumerable<JournalEntry> entries,
        Account account,
        DateOnly asAt)
    {
        ArgumentNullException.ThrowIfNull(account);

        var currency = Currency.Kes;
        var signed = SignedBalanceAsAt(entries, account.Id, asAt, currency);

        return account.NormalBalance == BalanceSide.Debit ? signed : -signed;
    }

    /// <summary>
    /// The movement on an account between two dates, both inclusive. Used for statements and
    /// for the income and expenditure account, which report a period rather than a position.
    /// </summary>
    public static Money MovementBetween(
        IEnumerable<JournalEntry> entries,
        AccountId accountId,
        DateOnly from,
        DateOnly to,
        Currency currency)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (to < from)
        {
            throw new ArgumentException(
                $"The period end {to:yyyy-MM-dd} is before its start {from:yyyy-MM-dd}.", nameof(to));
        }

        return entries
            .Where(entry => entry.EntryDate >= from && entry.EntryDate <= to)
            .SelectMany(entry => entry.Lines)
            .Where(line => line.AccountId == accountId)
            .Sum(line => line.SignedAmount, currency);
    }

    /// <summary>
    /// Every account's signed balance as at a date, for building a trial balance.
    /// </summary>
    public static IReadOnlyDictionary<AccountId, Money> AllSignedBalancesAsAt(
        IEnumerable<JournalEntry> entries,
        DateOnly asAt,
        Currency currency)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var balances = new Dictionary<AccountId, Money>();

        foreach (var line in entries.Where(entry => entry.EntryDate <= asAt).SelectMany(entry => entry.Lines))
        {
            var running = balances.TryGetValue(line.AccountId, out var existing)
                ? existing
                : Money.Zero(currency);

            balances[line.AccountId] = running + line.SignedAmount;
        }

        return balances;
    }

    /// <summary>
    /// The sum of every signed balance in the ledger as at a date, which must be zero.
    /// </summary>
    /// <remarks>
    /// This is the trial balance check. It cannot fail unless an entry reached storage
    /// without going through <see cref="JournalEntry"/>'s constructor - so if it ever does,
    /// the problem is that something wrote to the database directly.
    /// </remarks>
    public static Money TrialBalanceDifferenceAsAt(
        IEnumerable<JournalEntry> entries,
        DateOnly asAt,
        Currency currency) =>
        AllSignedBalancesAsAt(entries, asAt, currency).Values.Sum(currency);
}
