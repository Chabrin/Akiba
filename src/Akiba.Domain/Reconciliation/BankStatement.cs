using Akiba.Domain.Common;
using Akiba.Domain.Financial;

namespace Akiba.Domain.Reconciliation;

/// <summary>Identifies an imported bank statement.</summary>
public readonly record struct BankStatementId(Guid Value)
{
    public static BankStatementId New() => new(Guid.NewGuid());

    public bool IsSpecified => Value != Guid.Empty;

    public override string ToString() => Value.ToString();
}

/// <summary>Whether a statement line brought money in or took it out.</summary>
public enum StatementDirection
{
    /// <summary>Money into the account.</summary>
    Credit = 1,

    /// <summary>Money out of the account.</summary>
    Debit = 2,
}

/// <summary>Why a statement line is or is not matched.</summary>
public enum MatchState
{
    /// <summary>Nothing in Akiba has been matched to it yet.</summary>
    Unmatched = 0,

    /// <summary>Akiba proposed a match on amount and date; a clerk has not confirmed it.</summary>
    Suggested = 1,

    /// <summary>A clerk has confirmed it.</summary>
    Matched = 2,

    /// <summary>
    /// A clerk has looked at it and decided it is not Akiba's - a bank error, or somebody
    /// else's money.
    /// </summary>
    NotOurs = 3,
}

/// <summary>
/// One line of a bank statement, as the bank wrote it.
/// </summary>
/// <remarks>
/// <para>
/// Kept exactly as imported. The description is whatever the bank printed - "TRF FRM CHABRIN
/// AGENCIES", "CHQ 000431" - and matching works from that rather than tidying it, because
/// when a match is wrong the clerk needs to see what the bank actually said.
/// </para>
/// <para>
/// Statements arrive quarterly, so a line can be three months older than the day it is read.
/// </para>
/// </remarks>
public sealed class BankStatementLine
{
    private BankStatementLine(
        Guid id,
        int lineNumber,
        DateOnly valueDate,
        string description,
        Money amount,
        StatementDirection direction,
        string? bankReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        if (!amount.IsPositive)
        {
            throw new ArgumentException(
                "A statement line is for a positive amount; the direction says which way it went.",
                nameof(amount));
        }

        Id = id;
        LineNumber = lineNumber;
        ValueDate = valueDate;
        Description = description.Trim();
        Amount = amount.Round();
        Direction = direction;
        BankReference = string.IsNullOrWhiteSpace(bankReference) ? null : bankReference.Trim();
        State = MatchState.Unmatched;
    }

    public Guid Id { get; }

    /// <summary>Its position in the file, so a problem can be pointed at.</summary>
    public int LineNumber { get; }

    public DateOnly ValueDate { get; }

    /// <summary>What the bank printed, untouched.</summary>
    public string Description { get; }

    public Money Amount { get; }

    public StatementDirection Direction { get; }

    public string? BankReference { get; }

    public MatchState State { get; private set; }

    /// <summary>The Akiba receipt or journal entry this line was matched to.</summary>
    public Guid? MatchedToId { get; private set; }

    /// <summary>What the match is against, so the clerk knows what they are looking at.</summary>
    public string? MatchedToDescription { get; private set; }

    /// <summary>Why a clerk decided this line is not Akiba's.</summary>
    public string? NotOursReason { get; private set; }

    public Actor? DecidedBy { get; private set; }

    /// <summary>The amount signed the way the ledger signs it: money in is a debit to Bank.</summary>
    public Money SignedAmount => Direction == StatementDirection.Credit ? Amount : -Amount;

    public bool IsResolved => State is MatchState.Matched or MatchState.NotOurs;

    public static BankStatementLine Import(
        int lineNumber,
        DateOnly valueDate,
        string description,
        Money amount,
        StatementDirection direction,
        string? bankReference = null) =>
        new(Guid.NewGuid(), lineNumber, valueDate, description, amount, direction, bankReference);

    /// <summary>Rebuilds a line from storage. For the persistence layer only.</summary>
    public static BankStatementLine Rehydrate(
        Guid id,
        int lineNumber,
        DateOnly valueDate,
        string description,
        Money amount,
        StatementDirection direction,
        string? bankReference,
        MatchState state,
        Guid? matchedToId,
        string? matchedToDescription,
        string? notOursReason,
        Actor? decidedBy) =>
        new(id, lineNumber, valueDate, description, amount, direction, bankReference)
        {
            State = state,
            MatchedToId = matchedToId,
            MatchedToDescription = matchedToDescription,
            NotOursReason = notOursReason,
            DecidedBy = decidedBy,
        };

    /// <summary>
    /// Akiba thinks this line is a particular receipt, on amount and date.
    /// </summary>
    /// <remarks>
    /// A suggestion, never a decision. It is not matched until a clerk says so, for the same
    /// reason allocation is never inferred: a wrong guess that looks like somebody's judgement
    /// is worse than an open question.
    /// </remarks>
    public void Suggest(Guid candidateId, string candidateDescription)
    {
        if (State != MatchState.Unmatched)
        {
            return;
        }

        State = MatchState.Suggested;
        MatchedToId = candidateId;
        MatchedToDescription = candidateDescription;
    }

    /// <summary>A clerk confirms what this line is.</summary>
    public void Match(Guid matchedToId, string description, Actor clerk)
    {
        if (!clerk.IsSpecified)
        {
            throw new ArgumentException("A match records who made it.", nameof(clerk));
        }

        if (State == MatchState.NotOurs)
        {
            throw new InvalidOperationException(
                $"Line {LineNumber} was marked as not Akiba's. Clear that first.");
        }

        State = MatchState.Matched;
        MatchedToId = matchedToId;
        MatchedToDescription = description;
        DecidedBy = clerk;
    }

    /// <summary>A clerk decides this line is not Akiba's money.</summary>
    public void MarkNotOurs(string reason, Actor clerk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!clerk.IsSpecified)
        {
            throw new ArgumentException("This decision records who made it.", nameof(clerk));
        }

        State = MatchState.NotOurs;
        NotOursReason = reason.Trim();
        DecidedBy = clerk;
        MatchedToId = null;
        MatchedToDescription = null;
    }

    /// <summary>Undoes a match or a not-ours decision.</summary>
    public void Clear()
    {
        State = MatchState.Unmatched;
        MatchedToId = null;
        MatchedToDescription = null;
        NotOursReason = null;
        DecidedBy = null;
    }

    public override string ToString() =>
        $"{ValueDate:yyyy-MM-dd} {Description} {(Direction == StatementDirection.Credit ? "+" : "-")}{Amount}";
}
