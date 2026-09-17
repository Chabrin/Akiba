using Akiba.Domain.Common;
using Akiba.Domain.Financial;

namespace Akiba.Domain.Ledger;

/// <summary>
/// A balanced, append-only record of one movement of money.
/// </summary>
/// <remarks>
/// <para>
/// <b>The constructor refuses to build an entry whose lines do not sum to zero.</b> Not a
/// validation attribute, not a check in a handler, not a database constraint - the
/// constructor. An unbalanced entry cannot exist in memory, so it cannot reach the database.
/// </para>
/// <para>
/// Entries are never updated and never deleted. A mistake is corrected by posting a
/// reversing entry through <see cref="Reverse"/>, which carries a reason and an author, and
/// then posting the correct entry. The wrong entry stays visible forever with its reversal
/// beside it - which is exactly what the paper ledger did, and what makes a wrong figure
/// traceable to the posting that caused it.
/// </para>
/// </remarks>
public sealed class JournalEntry : AggregateRoot<JournalEntryId>
{
    private readonly List<JournalLine> _lines;

    private JournalEntry(
        JournalEntryId id,
        DateOnly entryDate,
        DateOnly valueDate,
        string narration,
        SourceDocument sourceDocument,
        Actor postedBy,
        DateTimeOffset postedAtUtc,
        IReadOnlyList<JournalLine> lines,
        JournalEntryId? reverses)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(narration);

        if (!postedBy.IsSpecified)
        {
            throw new ArgumentException(
                "Every entry records who posted it. An entry with no author is not an audit trail.",
                nameof(postedBy));
        }

        if (lines.Count < 2)
        {
            throw new ArgumentException(
                $"A journal entry needs at least two lines; this one has {lines.Count}. " +
                "Money moves from somewhere to somewhere.",
                nameof(lines));
        }

        var currency = lines[0].Currency;

        if (lines.Any(line => line.Currency != currency))
        {
            throw new CurrencyMismatchException(
                "Every line in a journal entry must be in the same currency.");
        }

        // The invariant. Lines are already rounded to posting precision when they are
        // constructed, so this sum is exact and the comparison is not approximate.
        var difference = lines.Select(line => line.SignedAmount).Sum(currency);

        if (!difference.IsZero)
        {
            throw new UnbalancedJournalEntryException(difference, lines);
        }

        EntryDate = entryDate;
        ValueDate = valueDate;
        Narration = narration.Trim();
        SourceDocument = sourceDocument;
        PostedBy = postedBy;
        PostedAtUtc = postedAtUtc;
        Reverses = reverses;
        _lines = [.. lines];
    }

    /// <summary>
    /// The date the entry belongs to for accounting purposes. This is what period close
    /// tests and what every "as at" balance compares against.
    /// </summary>
    public DateOnly EntryDate { get; }

    /// <summary>
    /// The date the money actually moved, which can differ from the entry date - a cheque
    /// deposited in June and confirmed on a statement in September has a June value date.
    /// </summary>
    public DateOnly ValueDate { get; }

    /// <summary>What this entry is, in the words an official would use.</summary>
    public string Narration { get; }

    /// <summary>The paper this entry came from.</summary>
    public SourceDocument SourceDocument { get; }

    /// <summary>Who posted it. In practice always the accounts clerk.</summary>
    public Actor PostedBy { get; }

    public DateTimeOffset PostedAtUtc { get; }

    /// <summary>The entry this one reverses, when it is a correction.</summary>
    public JournalEntryId? Reverses { get; }

    public bool IsReversal => Reverses.HasValue;

    public IReadOnlyList<JournalLine> Lines => _lines;

    public Currency Currency => _lines[0].Currency;

    /// <summary>The total of the debit side, which by the invariant equals the credit side.</summary>
    public Money Total => _lines
        .Where(line => line.Side == BalanceSide.Debit)
        .Sum(line => line.SignedAmount, Currency);

    /// <summary>
    /// Posts a new entry.
    /// </summary>
    /// <exception cref="UnbalancedJournalEntryException">The lines do not sum to zero.</exception>
    /// <exception cref="ArgumentException">
    /// There are fewer than two lines, the narration is blank, or the author is unspecified.
    /// </exception>
    /// <exception cref="CurrencyMismatchException">The lines are not all in one currency.</exception>
    public static JournalEntry Post(
        DateOnly entryDate,
        string narration,
        SourceDocument sourceDocument,
        Actor postedBy,
        DateTimeOffset postedAtUtc,
        IReadOnlyList<JournalLine> lines,
        DateOnly? valueDate = null)
    {
        var entry = new JournalEntry(
            JournalEntryId.New(),
            entryDate,
            valueDate ?? entryDate,
            narration,
            sourceDocument,
            postedBy,
            postedAtUtc,
            lines,
            reverses: null);

        entry.Raise(new JournalEntryPosted(entry.Id, entry.EntryDate, entry.Total, postedAtUtc));

        return entry;
    }

    /// <summary>
    /// Rebuilds an entry from storage. For the persistence layer only.
    /// </summary>
    /// <remarks>
    /// This still runs the balance check. A stored entry that no longer balances means the
    /// data has been tampered with or a migration went wrong, and that must surface as a
    /// loud failure on read rather than as a quietly wrong trial balance.
    /// </remarks>
    public static JournalEntry Rehydrate(
        JournalEntryId id,
        DateOnly entryDate,
        DateOnly valueDate,
        string narration,
        SourceDocument sourceDocument,
        Actor postedBy,
        DateTimeOffset postedAtUtc,
        IReadOnlyList<JournalLine> lines,
        JournalEntryId? reverses) =>
        new(id, entryDate, valueDate, narration, sourceDocument, postedBy, postedAtUtc, lines, reverses);

    /// <summary>
    /// Creates the entry that undoes this one: the same lines with their signs flipped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only way to correct a mistake. The original stays in the ledger untouched.
    /// </para>
    /// <para>
    /// The reversal is dated in the <b>currently open period</b>, which is usually not the
    /// period the original was posted in. A correction found in October for a September
    /// mistake posts in October, because September is closed and a closed period does not
    /// change. That is a feature: the trial balance an official signed off in September still
    /// reads the way it did when they signed it.
    /// </para>
    /// </remarks>
    /// <param name="entryDate">The date to post the reversal on, in the open period.</param>
    /// <param name="reason">Why the original was wrong. Recorded in the narration.</param>
    /// <param name="reversedBy">Who decided to reverse it.</param>
    /// <param name="reversedAtUtc">When the reversal was made, in UTC.</param>
    public JournalEntry Reverse(
        DateOnly entryDate,
        string reason,
        Actor reversedBy,
        DateTimeOffset reversedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var reversal = new JournalEntry(
            JournalEntryId.New(),
            entryDate,
            ValueDate,
            $"Reversal of '{Narration}': {reason.Trim()}",
            SourceDocument.Of(SourceDocumentKind.Correction, Id.ToString()),
            reversedBy,
            reversedAtUtc,
            [.. _lines.Select(line => line.Negated())],
            reverses: Id);

        reversal.Raise(new JournalEntryReversed(Id, reversal.Id, reason.Trim(), reversedAtUtc));

        return reversal;
    }

    public override string ToString() =>
        $"{EntryDate:yyyy-MM-dd} {Narration} ({Total})";
}
