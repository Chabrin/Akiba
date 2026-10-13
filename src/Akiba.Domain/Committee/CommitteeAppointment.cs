using Akiba.Domain.Common;

namespace Akiba.Domain.Committee;

public readonly record struct CommitteeAppointmentId(Guid Value)
{
    public static CommitteeAppointmentId New() => new(Guid.NewGuid());
    public bool IsSpecified => Value != Guid.Empty;
    public override string ToString() => Value.ToString();
}

public enum AppointmentStatus
{
    Active = 1,
    Suspended = 2,
    Ended = 3,
}

/// <summary>
/// One person holding one committee position for a given term.
/// </summary>
/// <remarks>
/// <para>
/// Appointments are created by the clerk recording a committee decision, or when an election
/// is ratified. When the committee changes, old appointments are ended and new ones are
/// created — the history of who held each seat is never overwritten.
/// </para>
/// <para>
/// The holder's name is stored as a snapshot rather than linked to a member record. Not every
/// person on the committee is necessarily a member (the accounts clerk, for instance, may be
/// a permanent employee rather than a CAL member), and the history must be readable even if
/// a member record is later corrected.
/// </para>
/// </remarks>
public sealed class CommitteeAppointment : AggregateRoot<CommitteeAppointmentId>
{
    private CommitteeAppointment(
        CommitteeAppointmentId id,
        CommitteePositionId positionId,
        string positionName,
        string holderName,
        DateOnly appointedOn,
        string term)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(positionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(holderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(term);

        PositionId = positionId;
        PositionName = positionName.Trim();
        HolderName = holderName.Trim();
        AppointedOn = appointedOn;
        Term = term.Trim();
    }

    public CommitteePositionId PositionId { get; }

    /// <summary>Snapshot of the position name at the time of appointment.</summary>
    public string PositionName { get; }

    public string HolderName { get; }

    public DateOnly AppointedOn { get; }

    /// <summary>The committee term, e.g. "2024–2026".</summary>
    public string Term { get; }

    public AppointmentStatus Status { get; private set; } = AppointmentStatus.Active;

    public string? SuspendedReason { get; private set; }

    public DateOnly? EndedOn { get; private set; }

    public bool IsActive => Status == AppointmentStatus.Active;

    public static CommitteeAppointment Appoint(
        CommitteePositionId positionId,
        string positionName,
        string holderName,
        DateOnly appointedOn,
        string term) =>
        new(CommitteeAppointmentId.New(), positionId, positionName, holderName, appointedOn, term);

    /// <summary>Rebuilds an appointment from storage. For the persistence layer only.</summary>
    public static CommitteeAppointment Rehydrate(
        CommitteeAppointmentId id,
        CommitteePositionId positionId,
        string positionName,
        string holderName,
        DateOnly appointedOn,
        string term,
        AppointmentStatus status,
        string? suspendedReason,
        DateOnly? endedOn) =>
        new(id, positionId, positionName, holderName, appointedOn, term)
        {
            Status = status,
            SuspendedReason = suspendedReason,
            EndedOn = endedOn,
        };

    /// <summary>Temporarily removes this person from the seat, with a reason.</summary>
    public void Suspend(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status != AppointmentStatus.Active)
        {
            throw new InvalidOperationException(
                $"Cannot suspend a {Status} appointment.");
        }

        Status = AppointmentStatus.Suspended;
        SuspendedReason = reason.Trim();
    }

    /// <summary>Returns a suspended person to their seat.</summary>
    public void Reinstate()
    {
        if (Status != AppointmentStatus.Suspended)
        {
            throw new InvalidOperationException(
                "Only a suspended appointment can be reinstated.");
        }

        Status = AppointmentStatus.Active;
        SuspendedReason = null;
    }

    /// <summary>
    /// Ends this appointment, typically because an election has produced a successor.
    /// </summary>
    public void End(DateOnly endedOn)
    {
        if (Status == AppointmentStatus.Ended)
        {
            throw new InvalidOperationException("This appointment has already ended.");
        }

        Status = AppointmentStatus.Ended;
        EndedOn = endedOn;
    }

    public override string ToString() => $"{HolderName} as {PositionName} ({Term})";
}
