namespace Akiba.Domain.Lending;

/// <summary>
/// The monthly lock period: applications received after the cutoff are considered in the
/// following month.
/// </summary>
/// <remarks>
/// <para>
/// Both loan application forms carry this in capitals: "THIS LOAN APPLICATION FORM SHOULD BE
/// SUBMITTED AND RECEIVED AT THE SOCIETY'S OFFICE ON OR BEFORE THE 15th DAY OF THE MONTH.
/// LATE APPLICATIONS WILL BE CONSIDERED IN THE SUCCEEDING MONTH."
/// </para>
/// <para>
/// A late application is <b>accepted, not rejected</b>. It is recorded with its true
/// submission date and queued for the next cycle, which is why the office tracks loans
/// approved but not yet disbursed because they were applied for after the lock period.
/// </para>
/// <para>
/// The cutoff day is configuration rather than a constant, so the committee can move it
/// without a code change.
/// </para>
/// </remarks>
public sealed class ApplicationCutoff
{
    private ApplicationCutoff(int dayOfMonth, int version, DateOnly effectiveFrom)
    {
        if (dayOfMonth is < 1 or > 28)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dayOfMonth),
                dayOfMonth,
                "The cutoff must fall on a day every month has.");
        }

        DayOfMonth = dayOfMonth;
        Version = version;
        EffectiveFrom = effectiveFrom;
    }

    /// <summary>The last day of the month on which an application still makes that cycle.</summary>
    public int DayOfMonth { get; }

    public int Version { get; }

    public DateOnly EffectiveFrom { get; }

    /// <summary>The rule as printed on both application forms: the 15th.</summary>
    public static ApplicationCutoff Version1 { get; } =
        new(dayOfMonth: 15, version: 1, effectiveFrom: new DateOnly(2026, 1, 1));

    /// <summary>A different cutoff, for when the committee moves it.</summary>
    public static ApplicationCutoff NewVersion(int dayOfMonth, int version, DateOnly effectiveFrom) =>
        new(dayOfMonth, version, effectiveFrom);

    /// <summary>Whether an application received on this date makes the current cycle.</summary>
    public bool MakesCurrentCycle(DateOnly receivedOn) => receivedOn.Day <= DayOfMonth;

    /// <summary>
    /// The month an application received on this date will be considered in, as the first of
    /// that month.
    /// </summary>
    public DateOnly ConsiderationMonth(DateOnly receivedOn)
    {
        var thisMonth = new DateOnly(receivedOn.Year, receivedOn.Month, 1);

        return MakesCurrentCycle(receivedOn) ? thisMonth : thisMonth.AddMonths(1);
    }

    public override string ToString() =>
        $"Applications received on or before the {DayOfMonth}th are considered that month";
}
