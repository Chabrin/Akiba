namespace Akiba.Domain.Ledger;

/// <summary>
/// Decides whether a date may be posted into, given the periods that exist.
/// </summary>
/// <remarks>
/// <para>
/// The rule is stated once, here, and called from the repository - the single boundary every
/// entry passes through on its way to storage. Putting it at each call site would mean the
/// one call site that forgot is the one that corrupts a closed month.
/// </para>
/// <para>
/// A date with no period covering it is postable. Periods are created as the ledger reaches
/// them, so the absence of one simply means nobody has closed that month yet.
/// </para>
/// </remarks>
public sealed class PeriodCalendar
{
    private readonly IReadOnlyList<AccountingPeriod> _periods;

    public PeriodCalendar(IReadOnlyList<AccountingPeriod> periods)
    {
        ArgumentNullException.ThrowIfNull(periods);
        _periods = periods;
    }

    /// <summary>Every closed period containing the date - a closed month, or a closed year.</summary>
    public IReadOnlyList<AccountingPeriod> ClosedPeriodsCovering(DateOnly date) =>
        [.. _periods.Where(period => period.IsClosed && period.Contains(date))];

    public bool IsOpenForPosting(DateOnly date) => ClosedPeriodsCovering(date).Count == 0;

    /// <summary>
    /// Throws if the date falls in a closed period.
    /// </summary>
    /// <exception cref="ClosedPeriodException">The date is in a closed month or year.</exception>
    public void EnsureOpenForPosting(DateOnly date)
    {
        var closed = ClosedPeriodsCovering(date);

        if (closed.Count > 0)
        {
            // Report the narrowest closed period containing the date - a closed month is more
            // useful to an official than "the year is closed".
            var narrowest = closed.OrderBy(period => period.End.DayNumber - period.Start.DayNumber).First();

            throw new ClosedPeriodException(date, narrowest);
        }
    }
}
