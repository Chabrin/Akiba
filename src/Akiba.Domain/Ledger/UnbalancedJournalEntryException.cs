using Akiba.Domain.Financial;

namespace Akiba.Domain.Ledger;

/// <summary>
/// Thrown by the <see cref="JournalEntry"/> constructor when the lines do not sum to zero.
/// </summary>
/// <remarks>
/// <para>
/// This is the invariant the whole system rests on, and it is enforced in a constructor
/// rather than in a validator, a handler or a database constraint. An unbalanced entry is
/// not a thing that can exist in memory, so it is not a thing that can reach the database.
/// The trial balance is therefore correct by construction rather than by vigilance.
/// </para>
/// <para>
/// If you are reading this message, the code that built the entry has a bug - the fix is in
/// that code, never here and never by relaxing the check.
/// </para>
/// </remarks>
public sealed class UnbalancedJournalEntryException : InvalidOperationException
{
    public UnbalancedJournalEntryException(Money difference, IReadOnlyList<JournalLine> lines)
        : base(BuildMessage(difference, lines))
    {
        Difference = difference;
    }

    public UnbalancedJournalEntryException()
        : base("A journal entry's lines must sum to zero.")
    {
    }

    public UnbalancedJournalEntryException(string message)
        : base(message)
    {
    }

    public UnbalancedJournalEntryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>How far out the entry was. Debits exceed credits when this is positive.</summary>
    public Money Difference { get; }

    private static string BuildMessage(Money difference, IReadOnlyList<JournalLine> lines)
    {
        var detail = lines is { Count: > 0 }
            ? Environment.NewLine + string.Join(Environment.NewLine, lines.Select(line => "  " + line))
            : string.Empty;

        var direction = difference.IsPositive ? "Debits exceed credits" : "Credits exceed debits";

        return $"A journal entry's lines must sum to zero. {direction} by {difference.Abs()}.{detail}";
    }
}
