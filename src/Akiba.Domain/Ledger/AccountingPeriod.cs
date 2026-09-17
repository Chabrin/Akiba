using Akiba.Domain.Common;

namespace Akiba.Domain.Ledger;

public enum AccountingPeriodKind
{
    Month = 1,
    Year = 2,
}

public enum AccountingPeriodStatus
{
    Open = 1,
    Closed = 2,
}

/// <summary>
/// A month or a year of the ledger, and whether it is still open to postings.
/// </summary>
/// <remarks>
/// <para>
/// Once a period is closed, no entry may post into it. A correction found afterwards posts
/// into the currently open period, referencing the original - see
/// <see cref="JournalEntry.Reverse"/>. This is what lets an official trust a trial balance
/// they signed off: it still reads the way it did when they signed it.
/// </para>
/// <para>
/// <b>This aggregate states the rule; the repository enforces it.</b> A closed period that
/// can be written to by any code path that forgets to check is not closed, so the check lives
/// at the boundary where entries are saved, not in the UI and not at each call site.
/// </para>
/// <para>
/// The treasurer closes a period. The chairman - and only the chairman - may reopen one, and
/// the reason is recorded permanently.
/// </para>
/// </remarks>
public sealed class AccountingPeriod : AggregateRoot<AccountingPeriodId>
{
    private AccountingPeriod(
        AccountingPeriodId id,
        AccountingPeriodKind kind,
        DateOnly start,
        DateOnly end)
        : base(id)
    {
        if (end < start)
        {
            throw new ArgumentException(
                $"A period's end {end:yyyy-MM-dd} cannot precede its start {start:yyyy-MM-dd}.",
                nameof(end));
        }

        Kind = kind;
        Start = start;
        End = end;
        Status = AccountingPeriodStatus.Open;
    }

    public AccountingPeriodKind Kind { get; }

    /// <summary>First date in the period, inclusive.</summary>
    public DateOnly Start { get; }

    /// <summary>Last date in the period, inclusive.</summary>
    public DateOnly End { get; }

    public AccountingPeriodStatus Status { get; private set; }

    public bool IsClosed => Status == AccountingPeriodStatus.Closed;

    public bool IsOpen => Status == AccountingPeriodStatus.Open;

    public Actor? ClosedBy { get; private set; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    /// <summary>Why the period was last reopened, where it has been.</summary>
    public string? ReopenedReason { get; private set; }

    public Actor? ReopenedBy { get; private set; }

    public DateTimeOffset? ReopenedAtUtc { get; private set; }

    /// <summary>The calendar month containing <paramref name="anyDateInMonth"/>.</summary>
    public static AccountingPeriod ForMonth(DateOnly anyDateInMonth)
    {
        var start = new DateOnly(anyDateInMonth.Year, anyDateInMonth.Month, 1);
        var end = start.AddMonths(1).AddDays(-1);

        return new AccountingPeriod(AccountingPeriodId.New(), AccountingPeriodKind.Month, start, end);
    }

    /// <summary>
    /// The calendar year containing <paramref name="anyDateInYear"/>.
    /// </summary>
    /// <remarks>
    /// Financial periods follow the calendar year. If Akiba ever adopts a different financial
    /// year end, this is the single place that changes.
    /// </remarks>
    public static AccountingPeriod ForYear(DateOnly anyDateInYear)
    {
        var start = new DateOnly(anyDateInYear.Year, 1, 1);
        var end = new DateOnly(anyDateInYear.Year, 12, 31);

        return new AccountingPeriod(AccountingPeriodId.New(), AccountingPeriodKind.Year, start, end);
    }

    /// <summary>Rebuilds a period from storage. For the persistence layer only.</summary>
    public static AccountingPeriod Rehydrate(
        AccountingPeriodId id,
        AccountingPeriodKind kind,
        DateOnly start,
        DateOnly end,
        AccountingPeriodStatus status,
        Actor? closedBy,
        DateTimeOffset? closedAtUtc,
        Actor? reopenedBy,
        string? reopenedReason,
        DateTimeOffset? reopenedAtUtc) =>
        new(id, kind, start, end)
        {
            Status = status,
            ClosedBy = closedBy,
            ClosedAtUtc = closedAtUtc,
            ReopenedBy = reopenedBy,
            ReopenedReason = reopenedReason,
            ReopenedAtUtc = reopenedAtUtc,
        };

    public bool Contains(DateOnly date) => date >= Start && date <= End;

    /// <summary>
    /// Closes the period. The treasurer does this, once the month's reconciliations balance.
    /// </summary>
    public void Close(Actor treasurer, DateTimeOffset closedAtUtc)
    {
        if (!treasurer.IsSpecified)
        {
            throw new ArgumentException("Closing a period records who closed it.", nameof(treasurer));
        }

        if (IsClosed)
        {
            throw new InvalidOperationException(
                $"The period {this} was already closed by {ClosedBy} on {ClosedAtUtc:yyyy-MM-dd}.");
        }

        Status = AccountingPeriodStatus.Closed;
        ClosedBy = treasurer;
        ClosedAtUtc = closedAtUtc;

        Raise(new AccountingPeriodClosed(Id, Start, End, treasurer, closedAtUtc));
    }

    /// <summary>
    /// Reopens a closed period. The chairman's decision, and the reason is permanent.
    /// </summary>
    /// <remarks>
    /// Reopening should be rare. It exists because sometimes a period genuinely must be
    /// corrected in place - a duplicated payroll schedule, say - and the alternative would be
    /// to make the books wrong on purpose. Restricting it to the chairman and recording the
    /// reason is what keeps it rare.
    /// </remarks>
    public void Reopen(Actor chairman, string reason, DateTimeOffset reopenedAtUtc)
    {
        if (!chairman.IsSpecified)
        {
            throw new ArgumentException("Reopening a period records who reopened it.", nameof(chairman));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (IsOpen)
        {
            throw new InvalidOperationException($"The period {this} is already open.");
        }

        Status = AccountingPeriodStatus.Open;
        ReopenedBy = chairman;
        ReopenedReason = reason.Trim();
        ReopenedAtUtc = reopenedAtUtc;

        Raise(new AccountingPeriodReopened(Id, Start, End, chairman, reason.Trim(), reopenedAtUtc));
    }

    public override string ToString() => Kind == AccountingPeriodKind.Year
        ? $"{Start.Year}"
        : $"{Start:yyyy-MM}";
}
