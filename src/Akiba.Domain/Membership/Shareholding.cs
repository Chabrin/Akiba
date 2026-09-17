using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;

namespace Akiba.Domain.Membership;

/// <summary>
/// Everything that is derived from a member's share account: their shareholding, when they
/// joined, and what they may borrow.
/// </summary>
/// <remarks>
/// <para>
/// None of this is stored. A member's shareholding is the balance of their share account as
/// at a date, and their borrowing limit and membership start date follow from it.
/// </para>
/// <para>
/// Keeping these as pure functions over entries, rather than as properties on
/// <see cref="Member"/>, is what makes them answerable "as at" any past date. A property
/// could only ever tell you about today.
/// </para>
/// </remarks>
public static class Shareholding
{
    /// <summary>
    /// A member's shareholding as at a date.
    /// </summary>
    /// <remarks>
    /// Share contributions credit the member's account, which is a liability - Akiba owes the
    /// money back. The natural balance is the positive figure that goes on a statement.
    /// </remarks>
    public static Money AsAt(IEnumerable<JournalEntry> entries, Account sharesAccount, DateOnly asAt) =>
        LedgerBalances.NaturalBalanceAsAt(entries, sharesAccount, asAt);

    /// <summary>
    /// The date membership began: the date of the first share contribution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The questionnaire is explicit that a member becomes part of Akiba when they make their
    /// first contribution in shares, not when they write to the chairman. The letter is filed
    /// as a document; this is the date that counts.
    /// </para>
    /// <para>
    /// Returns null for a member who has been enrolled but has not yet contributed - which is
    /// a real state between the letter and the first payroll run, not an error.
    /// </para>
    /// </remarks>
    public static DateOnly? MembershipStartDate(IEnumerable<JournalEntry> entries, AccountId sharesAccountId)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var contributions = entries
            .Where(entry => entry.Lines.Any(line =>
                line.AccountId == sharesAccountId && line.Side == BalanceSide.Credit))
            .Select(entry => entry.EntryDate)
            .ToList();

        return contributions.Count == 0 ? null : contributions.Min();
    }

    /// <summary>
    /// How long someone has been a member, as at a date, in whole years.
    /// </summary>
    /// <remarks>
    /// Needed only by the tenure-based repayment terms for loans of 400,001 and above, which
    /// are proposed in the minutes but never recorded as adopted - and are already printed on
    /// the live application form. That rule sits behind a feature flag, default off.
    /// See docs/open-questions.md, item 8.
    /// </remarks>
    public static int? MembershipYearsAsAt(
        IEnumerable<JournalEntry> entries,
        AccountId sharesAccountId,
        DateOnly asAt)
    {
        var start = MembershipStartDate(entries, sharesAccountId);

        if (start is null || asAt < start)
        {
            return null;
        }

        var years = asAt.Year - start.Value.Year;

        // Not yet reached the anniversary this year.
        if (asAt < start.Value.AddYears(years))
        {
            years--;
        }

        return years;
    }

    /// <summary>
    /// The most a member may borrow: twice their shareholding.
    /// </summary>
    /// <remarks>
    /// This is the gross limit on a single loan. It is not the only constraint - a member may
    /// hold at most two loans at once, and the two-thirds affordability rule can bite well
    /// before this does.
    /// </remarks>
    public static Money BorrowingLimit(Money shareholding) => shareholding * 2m;

    /// <summary>
    /// Whether a proposed loan is within the member's borrowing limit.
    /// </summary>
    public static bool IsWithinBorrowingLimit(Money shareholding, Money requestedPrincipal) =>
        requestedPrincipal <= BorrowingLimit(shareholding);

    /// <summary>
    /// How much of a loan is <b>not</b> covered by the member's own shares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This drives guarantor requirements. For an emergency loan the rule is settled: it is
    /// unguaranteed unless the member's normal loan balance exceeds their total shares.
    /// </para>
    /// <para>
    /// <b>For a normal loan the rule is not settled</b>, and this method deliberately only
    /// reports the figure rather than deciding anything with it. The application form's
    /// official-use section asks "Do guarantors sufficiently cover the loan?", so the
    /// uncovered amount is shown to the approver as an indicator. Whether a fully covered
    /// normal loan needs guarantors at all is open.
    /// See docs/open-questions.md, item 1.
    /// </para>
    /// </remarks>
    public static Money AmountNotCoveredByShares(Money shareholding, Money loanBalance)
    {
        var uncovered = loanBalance - shareholding;

        return uncovered.IsNegative ? Money.Zero(loanBalance.Currency) : uncovered;
    }
}
