using Akiba.Domain.Financial;

namespace Akiba.Domain.Lending;

/// <summary>How overdue an amount is.</summary>
public enum ArrearsBucket
{
    /// <summary>Nothing overdue.</summary>
    Current = 0,

    Days1To30 = 1,
    Days31To60 = 2,
    Days61To90 = 3,

    /// <summary>More than ninety days overdue.</summary>
    Over90Days = 4,
}

/// <summary>
/// One loan's arrears position.
/// </summary>
/// <param name="LoanNumber">The loan.</param>
/// <param name="ExpectedPaidBy">What the schedule says should have been paid by now.</param>
/// <param name="ActuallyPaid">What the ledger shows was paid.</param>
/// <param name="AmountOverdue">The difference, where the schedule is ahead of the ledger.</param>
/// <param name="OldestUnpaidDueDate">The due date of the earliest instalment still short.</param>
/// <param name="DaysOverdue">How long that instalment has been outstanding.</param>
/// <param name="Bucket">Which ageing band it falls in.</param>
public sealed record LoanArrears(
    string LoanNumber,
    Money ExpectedPaidBy,
    Money ActuallyPaid,
    Money AmountOverdue,
    DateOnly? OldestUnpaidDueDate,
    int DaysOverdue,
    ArrearsBucket Bucket)
{
    public bool IsInArrears => AmountOverdue.IsPositive;
}

/// <summary>
/// Works out what is overdue on a loan by comparing its schedule with the ledger.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no penalty calculation here, because Akiba has no penalty rule.</b> The
/// questionnaire asked what counts as a missed payment and whether there is a penalty, and
/// the question came back unanswered. Inventing a penalty would be inventing a charge against
/// a member. See docs/open-questions.md, item 12.
/// </para>
/// <para>
/// Arrears also mean different things for different borrowers. A serving CAL employee repays
/// by deduction at source and cannot really miss a payment, so arrears there usually mean the
/// two-thirds rule squeezed the deduction, or HR deducted less than the schedule asked for -
/// which is what the payroll reconciliation is for. A client borrower, an exited member or a
/// direct depositor genuinely can fall behind.
/// </para>
/// <para>
/// What was actually paid comes from the ledger, never from a stored figure: it is the
/// movement on the loan's receivable account.
/// </para>
/// </remarks>
public static class Arrears
{
    /// <summary>
    /// Assesses one loan as at a date.
    /// </summary>
    /// <param name="loanNumber">The loan's number, for the report.</param>
    /// <param name="schedule">Its instalment schedule.</param>
    /// <param name="amountRepaid">
    /// What has actually been repaid, derived from the loan's receivable account.
    /// </param>
    /// <param name="asAt">The date to assess at.</param>
    public static LoanArrears Assess(
        string loanNumber,
        RepaymentSchedule schedule,
        Money amountRepaid,
        DateOnly asAt)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentException.ThrowIfNullOrWhiteSpace(loanNumber);

        var expected = schedule.ExpectedPaidBy(asAt);
        var shortfall = expected - amountRepaid;
        var overdue = shortfall.IsPositive ? shortfall : Money.Zero(expected.Currency);

        if (!overdue.IsPositive)
        {
            return new LoanArrears(
                loanNumber, expected, amountRepaid, overdue, null, 0, ArrearsBucket.Current);
        }

        // Walk the schedule from the start, consuming what was paid. The first instalment the
        // payments do not cover is the one the ageing is measured from - which is how an
        // official reads a ledger page, and gives the oldest debt rather than the newest.
        var remaining = amountRepaid;
        DateOnly? oldestUnpaid = null;

        foreach (var instalment in schedule.Instalments.Where(i => i.DueDate <= asAt))
        {
            if (remaining >= instalment.Amount)
            {
                remaining -= instalment.Amount;
                continue;
            }

            oldestUnpaid = instalment.DueDate;
            break;
        }

        var daysOverdue = oldestUnpaid is { } due ? asAt.DayNumber - due.DayNumber : 0;

        return new LoanArrears(
            loanNumber, expected, amountRepaid, overdue, oldestUnpaid, daysOverdue, BucketFor(daysOverdue));
    }

    /// <summary>The ageing band a number of days overdue falls into.</summary>
    public static ArrearsBucket BucketFor(int daysOverdue) => daysOverdue switch
    {
        <= 0 => ArrearsBucket.Current,
        <= 30 => ArrearsBucket.Days1To30,
        <= 60 => ArrearsBucket.Days31To60,
        <= 90 => ArrearsBucket.Days61To90,
        _ => ArrearsBucket.Over90Days,
    };

    /// <summary>
    /// Totals a set of loans into the ageing bands, for the arrears report.
    /// </summary>
    public static IReadOnlyDictionary<ArrearsBucket, Money> Age(IEnumerable<LoanArrears> loans)
    {
        ArgumentNullException.ThrowIfNull(loans);

        var totals = Enum.GetValues<ArrearsBucket>()
            .ToDictionary(bucket => bucket, _ => Money.ZeroKes);

        foreach (var loan in loans.Where(loan => loan.IsInArrears))
        {
            totals[loan.Bucket] += loan.AmountOverdue;
        }

        return totals;
    }
}
