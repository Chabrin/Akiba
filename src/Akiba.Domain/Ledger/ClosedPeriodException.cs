namespace Akiba.Domain.Ledger;

/// <summary>
/// Thrown when an entry would post into a closed period.
/// </summary>
/// <remarks>
/// The fix is never to reopen the period on the caller's behalf. It is to post the entry
/// into the currently open period, referencing the original - which is what
/// <see cref="JournalEntry.Reverse"/> is for. Reopening a period is the chairman's decision
/// and is recorded as such.
/// </remarks>
public sealed class ClosedPeriodException : InvalidOperationException
{
    public ClosedPeriodException(DateOnly entryDate, AccountingPeriod period)
        : base(
            $"Cannot post an entry dated {entryDate:yyyy-MM-dd}: the period {period} was closed " +
            $"by {period.ClosedBy} on {period.ClosedAtUtc:yyyy-MM-dd}. Post the correction into " +
            "the open period instead, referencing the original entry.")
    {
        EntryDate = entryDate;
        PeriodStart = period?.Start ?? default;
        PeriodEnd = period?.End ?? default;
    }

    public ClosedPeriodException()
        : base("Cannot post an entry into a closed period.")
    {
    }

    public ClosedPeriodException(string message)
        : base(message)
    {
    }

    public ClosedPeriodException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DateOnly EntryDate { get; }

    public DateOnly PeriodStart { get; }

    public DateOnly PeriodEnd { get; }
}
