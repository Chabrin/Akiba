using Akiba.Domain.Common;
using Akiba.Domain.Financial;

namespace Akiba.Domain.Ledger;

/// <summary>Raised when a balanced entry has been posted.</summary>
public sealed record JournalEntryPosted(
    JournalEntryId EntryId,
    DateOnly EntryDate,
    Money Total,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Raised when an entry has been reversed. Carries the reason, because a correction without
/// a stated reason is indistinguishable from a mistake.
/// </summary>
public sealed record JournalEntryReversed(
    JournalEntryId OriginalEntryId,
    JournalEntryId ReversalEntryId,
    string Reason,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>Raised when the treasurer closes a period.</summary>
public sealed record AccountingPeriodClosed(
    AccountingPeriodId PeriodId,
    DateOnly Start,
    DateOnly End,
    Actor ClosedBy,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Raised when the chairman reopens a closed period. Carries the reason, which is the whole
/// point of restricting who may do it.
/// </summary>
public sealed record AccountingPeriodReopened(
    AccountingPeriodId PeriodId,
    DateOnly Start,
    DateOnly End,
    Actor ReopenedBy,
    string Reason,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
