using Akiba.Domain.Common;
using Akiba.Domain.Financial;

namespace Akiba.Domain.Committee;

public readonly record struct CommitteePositionId(Guid Value)
{
    public static CommitteePositionId New() => new(Guid.NewGuid());
    public bool IsSpecified => Value != Guid.Empty;
    public override string ToString() => Value.ToString();
}

/// <summary>Whether the allowance is paid per calendar month or per committee sitting.</summary>
public enum AllowanceFrequency
{
    Monthly = 1,
    PerSitting = 2,
}

/// <summary>
/// A named seat at the committee — Chairperson, Secretary, Zone Representative, etc. —
/// with a configurable allowance attached to it.
/// </summary>
/// <remarks>
/// Positions are not tied to any system user. The people who hold them are tracked separately
/// through <see cref="CommitteeAppointment"/>. A position can be deactivated (no longer held
/// after a restructure) without losing the history of who held it.
/// </remarks>
public sealed class CommitteePosition : AggregateRoot<CommitteePositionId>
{
    private CommitteePosition(
        CommitteePositionId id,
        string name,
        string? description,
        Money allowance,
        AllowanceFrequency frequency)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        Description = description?.Trim();
        Allowance = allowance;
        Frequency = frequency;
    }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    /// <summary>The allowance paid, per month or per sitting.</summary>
    public Money Allowance { get; private set; }

    public AllowanceFrequency Frequency { get; private set; }

    public bool IsActive { get; private set; } = true;

    public static CommitteePosition Create(
        string name,
        string? description,
        Money allowance,
        AllowanceFrequency frequency) =>
        new(CommitteePositionId.New(), name, description, allowance, frequency);

    /// <summary>Rebuilds a position from storage. For the persistence layer only.</summary>
    public static CommitteePosition Rehydrate(
        CommitteePositionId id,
        string name,
        string? description,
        Money allowance,
        AllowanceFrequency frequency,
        bool isActive) =>
        new(id, name, description, allowance, frequency) { IsActive = isActive };

    public void UpdateAllowance(Money allowance, AllowanceFrequency frequency)
    {
        Allowance = allowance;
        Frequency = frequency;
    }

    public void Rename(string name, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        Description = description?.Trim();
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    public override string ToString() => Name;
}
