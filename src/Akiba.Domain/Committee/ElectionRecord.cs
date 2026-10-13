using Akiba.Domain.Common;

namespace Akiba.Domain.Committee;

public readonly record struct ElectionId(Guid Value)
{
    public static ElectionId New() => new(Guid.NewGuid());
    public bool IsSpecified => Value != Guid.Empty;
    public override string ToString() => Value.ToString();
}

public enum ElectionStatus
{
    /// <summary>Outcomes are still being entered.</summary>
    Draft = 1,

    /// <summary>
    /// The committee has confirmed the results. Ratifying an election replaces all active
    /// appointments with new ones matching the outcomes recorded here.
    /// </summary>
    Ratified = 2,
}

/// <summary>One person elected to one position.</summary>
/// <param name="Id">Unique identifier for this outcome row.</param>
/// <param name="PositionId">Which seat.</param>
/// <param name="PositionName">Snapshot of the position name.</param>
/// <param name="ElectedName">Full name of the elected person.</param>
public sealed record ElectionOutcome(
    Guid Id,
    CommitteePositionId PositionId,
    string PositionName,
    string ElectedName);

/// <summary>
/// The formal record of a committee election.
/// </summary>
/// <remarks>
/// <para>
/// An election is entered as a draft, with outcomes added one per position. When the committee
/// confirms the results, the election is ratified. Ratification ends all current active
/// appointments and creates new ones from the outcomes — one step, as it was one decision.
/// </para>
/// <para>
/// A draft can be amended freely before ratification. Once ratified it is immutable; the
/// appointments it created are the record of what changed.
/// </para>
/// </remarks>
public sealed class ElectionRecord : AggregateRoot<ElectionId>
{
    private readonly List<ElectionOutcome> _outcomes = [];

    private ElectionRecord(ElectionId id, DateOnly heldOn, string term, string? notes)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        HeldOn = heldOn;
        Term = term.Trim();
        Notes = notes?.Trim();
    }

    public DateOnly HeldOn { get; }

    /// <summary>The term this election inaugurates, e.g. "2024–2026".</summary>
    public string Term { get; }

    public string? Notes { get; private set; }

    public ElectionStatus Status { get; private set; } = ElectionStatus.Draft;

    public IReadOnlyList<ElectionOutcome> Outcomes => _outcomes;

    public static ElectionRecord Create(DateOnly heldOn, string term, string? notes = null) =>
        new(ElectionId.New(), heldOn, term, notes);

    /// <summary>Rebuilds an election from storage. For the persistence layer only.</summary>
    public static ElectionRecord Rehydrate(
        ElectionId id,
        DateOnly heldOn,
        string term,
        string? notes,
        ElectionStatus status,
        IEnumerable<ElectionOutcome> outcomes)
    {
        var record = new ElectionRecord(id, heldOn, term, notes) { Status = status };
        record._outcomes.AddRange(outcomes);
        return record;
    }

    /// <summary>Adds or replaces the outcome for a position.</summary>
    public void RecordOutcome(CommitteePositionId positionId, string positionName, string electedName)
    {
        if (Status != ElectionStatus.Draft)
        {
            throw new InvalidOperationException(
                "Outcomes can only be added to a draft election.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(positionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(electedName);

        // Replace an existing outcome for the same position if present.
        _outcomes.RemoveAll(o => o.PositionId == positionId);
        _outcomes.Add(new ElectionOutcome(
            Guid.NewGuid(), positionId, positionName.Trim(), electedName.Trim()));
    }

    public void RemoveOutcome(Guid outcomeId)
    {
        if (Status != ElectionStatus.Draft)
        {
            throw new InvalidOperationException(
                "Outcomes can only be removed from a draft election.");
        }

        _outcomes.RemoveAll(o => o.Id == outcomeId);
    }

    /// <summary>
    /// Marks the results confirmed. Once ratified, new appointments must be created by the
    /// application layer.
    /// </summary>
    public void Ratify()
    {
        if (Status != ElectionStatus.Draft)
        {
            throw new InvalidOperationException("Only a draft election can be ratified.");
        }

        if (_outcomes.Count == 0)
        {
            throw new InvalidOperationException(
                "Record the elected members before ratifying the election.");
        }

        Status = ElectionStatus.Ratified;
    }

    public override string ToString() => $"Election {HeldOn:d MMMM yyyy} — {Term}";
}
