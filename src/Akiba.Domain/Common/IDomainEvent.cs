namespace Akiba.Domain.Common;

/// <summary>
/// Something that happened in the domain, stated in the past tense.
/// </summary>
/// <remarks>
/// Aggregates raise these; handlers in the application layer do the ledger posting and the
/// notification dispatch. An aggregate does not know that email exists.
/// </remarks>
public interface IDomainEvent
{
    /// <summary>When the event occurred, in UTC.</summary>
    DateTimeOffset OccurredAtUtc { get; }
}
