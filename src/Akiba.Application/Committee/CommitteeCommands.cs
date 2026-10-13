using Akiba.Application.Abstractions;
using Akiba.Domain.Committee;
using Akiba.Domain.Financial;
using FluentValidation;
using MediatR;
using static Akiba.Domain.Financial.Money;

namespace Akiba.Application.Committee;

/// <summary>Reads and writes committee positions and appointments.</summary>
public interface ICommitteeRepository
{
    // Positions
    Task<IReadOnlyList<CommitteePosition>> AllPositionsAsync(
        CancellationToken cancellationToken = default);

    Task<CommitteePosition?> FindPositionAsync(
        CommitteePositionId id, CancellationToken cancellationToken = default);

    void AddPosition(CommitteePosition position);

    void UpdatePosition(CommitteePosition position);

    // Appointments
    Task<IReadOnlyList<CommitteeAppointment>> AllAppointmentsAsync(
        bool activeOnly = false, CancellationToken cancellationToken = default);

    Task<CommitteeAppointment?> FindAppointmentAsync(
        CommitteeAppointmentId id, CancellationToken cancellationToken = default);

    void AddAppointment(CommitteeAppointment appointment);

    void UpdateAppointment(CommitteeAppointment appointment);

    // Elections
    Task<IReadOnlyList<ElectionRecord>> AllElectionsAsync(
        CancellationToken cancellationToken = default);

    Task<ElectionRecord?> FindElectionAsync(
        ElectionId id, CancellationToken cancellationToken = default);

    void AddElection(ElectionRecord election);

    void UpdateElection(ElectionRecord election);
}

// ─── Position commands ────────────────────────────────────────────────────────

/// <summary>Creates a new committee position with an allowance.</summary>
public sealed record CreateCommitteePositionCommand(
    string Name,
    string? Description,
    decimal AllowanceAmount,
    AllowanceFrequency Frequency) : IRequest<CommitteePositionId>;

public sealed class CreateCommitteePositionValidator
    : AbstractValidator<CreateCommitteePositionCommand>
{
    public CreateCommitteePositionValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(100);
        RuleFor(c => c.AllowanceAmount).GreaterThanOrEqualTo(0);
    }
}

internal sealed class CreateCommitteePositionHandler
    : IRequestHandler<CreateCommitteePositionCommand, CommitteePositionId>
{
    private readonly ICommitteeRepository _committee;
    private readonly IUnitOfWork _unitOfWork;

    public CreateCommitteePositionHandler(
        ICommitteeRepository committee, IUnitOfWork unitOfWork)
    {
        _committee = committee;
        _unitOfWork = unitOfWork;
    }

    public async Task<CommitteePositionId> Handle(
        CreateCommitteePositionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var position = CommitteePosition.Create(
            command.Name,
            command.Description,
            Kes(command.AllowanceAmount),
            command.Frequency);

        _committee.AddPosition(position);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return position.Id;
    }
}

/// <summary>Updates the allowance on an existing position.</summary>
public sealed record UpdatePositionAllowanceCommand(
    CommitteePositionId PositionId,
    string Name,
    string? Description,
    decimal AllowanceAmount,
    AllowanceFrequency Frequency) : IRequest;

internal sealed class UpdatePositionAllowanceHandler
    : IRequestHandler<UpdatePositionAllowanceCommand>
{
    private readonly ICommitteeRepository _committee;
    private readonly IUnitOfWork _unitOfWork;

    public UpdatePositionAllowanceHandler(
        ICommitteeRepository committee, IUnitOfWork unitOfWork)
    {
        _committee = committee;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(
        UpdatePositionAllowanceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var position = await LoadPositionAsync(_committee, command.PositionId, cancellationToken)
            .ConfigureAwait(false);

        position.Rename(command.Name, command.Description);
        position.UpdateAllowance(Kes(command.AllowanceAmount), command.Frequency);

        _committee.UpdatePosition(position);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<CommitteePosition> LoadPositionAsync(
        ICommitteeRepository repo, CommitteePositionId id, CancellationToken cancellationToken) =>
        await repo.FindPositionAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"No committee position with id {id}.");
}

/// <summary>Deactivates a position that is no longer filled.</summary>
public sealed record DeactivatePositionCommand(CommitteePositionId PositionId) : IRequest;

internal sealed class DeactivatePositionHandler : IRequestHandler<DeactivatePositionCommand>
{
    private readonly ICommitteeRepository _committee;
    private readonly IUnitOfWork _unitOfWork;

    public DeactivatePositionHandler(
        ICommitteeRepository committee, IUnitOfWork unitOfWork)
    {
        _committee = committee;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(
        DeactivatePositionCommand command, CancellationToken cancellationToken)
    {
        var position = await UpdatePositionAllowanceHandler
            .LoadPositionAsync(_committee, command.PositionId, cancellationToken)
            .ConfigureAwait(false);

        position.Deactivate();
        _committee.UpdatePosition(position);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Reactivates a previously deactivated position.</summary>
public sealed record ReactivatePositionCommand(CommitteePositionId PositionId) : IRequest;

internal sealed class ReactivatePositionHandler : IRequestHandler<ReactivatePositionCommand>
{
    private readonly ICommitteeRepository _committee;
    private readonly IUnitOfWork _unitOfWork;

    public ReactivatePositionHandler(
        ICommitteeRepository committee, IUnitOfWork unitOfWork)
    {
        _committee = committee;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(
        ReactivatePositionCommand command, CancellationToken cancellationToken)
    {
        var position = await UpdatePositionAllowanceHandler
            .LoadPositionAsync(_committee, command.PositionId, cancellationToken)
            .ConfigureAwait(false);

        position.Reactivate();
        _committee.UpdatePosition(position);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

// ─── Appointment commands ─────────────────────────────────────────────────────

/// <summary>Records a person taking up a committee seat.</summary>
public sealed record AddAppointmentCommand(
    CommitteePositionId PositionId,
    string HolderName,
    DateOnly AppointedOn,
    string Term) : IRequest<CommitteeAppointmentId>;

public sealed class AddAppointmentValidator : AbstractValidator<AddAppointmentCommand>
{
    public AddAppointmentValidator()
    {
        RuleFor(c => c.HolderName).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Term).NotEmpty().MaximumLength(50);
    }
}

internal sealed class AddAppointmentHandler
    : IRequestHandler<AddAppointmentCommand, CommitteeAppointmentId>
{
    private readonly ICommitteeRepository _committee;
    private readonly IUnitOfWork _unitOfWork;

    public AddAppointmentHandler(
        ICommitteeRepository committee, IUnitOfWork unitOfWork)
    {
        _committee = committee;
        _unitOfWork = unitOfWork;
    }

    public async Task<CommitteeAppointmentId> Handle(
        AddAppointmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var position = await UpdatePositionAllowanceHandler
            .LoadPositionAsync(_committee, command.PositionId, cancellationToken)
            .ConfigureAwait(false);

        var appointment = CommitteeAppointment.Appoint(
            position.Id,
            position.Name,
            command.HolderName,
            command.AppointedOn,
            command.Term);

        _committee.AddAppointment(appointment);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return appointment.Id;
    }
}

/// <summary>Temporarily removes a person from their seat.</summary>
public sealed record SuspendAppointmentCommand(
    CommitteeAppointmentId AppointmentId,
    string Reason) : IRequest;

public sealed class SuspendAppointmentValidator : AbstractValidator<SuspendAppointmentCommand>
{
    public SuspendAppointmentValidator() =>
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(500);
}

internal sealed class SuspendAppointmentHandler : IRequestHandler<SuspendAppointmentCommand>
{
    private readonly ICommitteeRepository _committee;
    private readonly IUnitOfWork _unitOfWork;

    public SuspendAppointmentHandler(
        ICommitteeRepository committee, IUnitOfWork unitOfWork)
    {
        _committee = committee;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(
        SuspendAppointmentCommand command, CancellationToken cancellationToken)
    {
        var appointment = await LoadAppointmentAsync(
            _committee, command.AppointmentId, cancellationToken).ConfigureAwait(false);

        appointment.Suspend(command.Reason);
        _committee.UpdateAppointment(appointment);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<CommitteeAppointment> LoadAppointmentAsync(
        ICommitteeRepository repo,
        CommitteeAppointmentId id,
        CancellationToken cancellationToken) =>
        await repo.FindAppointmentAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"No committee appointment with id {id}.");
}

/// <summary>Returns a suspended person to their seat.</summary>
public sealed record ReinstateAppointmentCommand(CommitteeAppointmentId AppointmentId) : IRequest;

internal sealed class ReinstateAppointmentHandler : IRequestHandler<ReinstateAppointmentCommand>
{
    private readonly ICommitteeRepository _committee;
    private readonly IUnitOfWork _unitOfWork;

    public ReinstateAppointmentHandler(
        ICommitteeRepository committee, IUnitOfWork unitOfWork)
    {
        _committee = committee;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(
        ReinstateAppointmentCommand command, CancellationToken cancellationToken)
    {
        var appointment = await SuspendAppointmentHandler
            .LoadAppointmentAsync(_committee, command.AppointmentId, cancellationToken)
            .ConfigureAwait(false);

        appointment.Reinstate();
        _committee.UpdateAppointment(appointment);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Ends an appointment — the person is no longer in the seat.</summary>
public sealed record EndAppointmentCommand(
    CommitteeAppointmentId AppointmentId,
    DateOnly EndedOn) : IRequest;

internal sealed class EndAppointmentHandler : IRequestHandler<EndAppointmentCommand>
{
    private readonly ICommitteeRepository _committee;
    private readonly IUnitOfWork _unitOfWork;

    public EndAppointmentHandler(
        ICommitteeRepository committee, IUnitOfWork unitOfWork)
    {
        _committee = committee;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(
        EndAppointmentCommand command, CancellationToken cancellationToken)
    {
        var appointment = await SuspendAppointmentHandler
            .LoadAppointmentAsync(_committee, command.AppointmentId, cancellationToken)
            .ConfigureAwait(false);

        appointment.End(command.EndedOn);
        _committee.UpdateAppointment(appointment);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

// ─── Election commands ────────────────────────────────────────────────────────

/// <summary>Opens a new draft election record.</summary>
public sealed record CreateElectionCommand(
    DateOnly HeldOn,
    string Term,
    string? Notes) : IRequest<ElectionId>;

public sealed class CreateElectionValidator : AbstractValidator<CreateElectionCommand>
{
    public CreateElectionValidator() =>
        RuleFor(c => c.Term).NotEmpty().MaximumLength(50);
}

internal sealed class CreateElectionHandler : IRequestHandler<CreateElectionCommand, ElectionId>
{
    private readonly ICommitteeRepository _committee;
    private readonly IUnitOfWork _unitOfWork;

    public CreateElectionHandler(
        ICommitteeRepository committee, IUnitOfWork unitOfWork)
    {
        _committee = committee;
        _unitOfWork = unitOfWork;
    }

    public async Task<ElectionId> Handle(
        CreateElectionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var election = ElectionRecord.Create(command.HeldOn, command.Term, command.Notes);

        _committee.AddElection(election);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return election.Id;
    }
}

/// <summary>Records one elected person for one position on a draft election.</summary>
public sealed record RecordElectionOutcomeCommand(
    ElectionId ElectionId,
    CommitteePositionId PositionId,
    string ElectedName) : IRequest;

public sealed class RecordElectionOutcomeValidator
    : AbstractValidator<RecordElectionOutcomeCommand>
{
    public RecordElectionOutcomeValidator() =>
        RuleFor(c => c.ElectedName).NotEmpty().MaximumLength(200);
}

internal sealed class RecordElectionOutcomeHandler
    : IRequestHandler<RecordElectionOutcomeCommand>
{
    private readonly ICommitteeRepository _committee;
    private readonly IUnitOfWork _unitOfWork;

    public RecordElectionOutcomeHandler(
        ICommitteeRepository committee, IUnitOfWork unitOfWork)
    {
        _committee = committee;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(
        RecordElectionOutcomeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var election = await LoadElectionAsync(
            _committee, command.ElectionId, cancellationToken).ConfigureAwait(false);

        var position = await UpdatePositionAllowanceHandler
            .LoadPositionAsync(_committee, command.PositionId, cancellationToken)
            .ConfigureAwait(false);

        election.RecordOutcome(position.Id, position.Name, command.ElectedName);

        _committee.UpdateElection(election);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ElectionRecord> LoadElectionAsync(
        ICommitteeRepository repo,
        ElectionId id,
        CancellationToken cancellationToken) =>
        await repo.FindElectionAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"No election with id {id}.");
}

/// <summary>Removes one outcome from a draft election.</summary>
public sealed record RemoveElectionOutcomeCommand(
    ElectionId ElectionId,
    Guid OutcomeId) : IRequest;

internal sealed class RemoveElectionOutcomeHandler
    : IRequestHandler<RemoveElectionOutcomeCommand>
{
    private readonly ICommitteeRepository _committee;
    private readonly IUnitOfWork _unitOfWork;

    public RemoveElectionOutcomeHandler(
        ICommitteeRepository committee, IUnitOfWork unitOfWork)
    {
        _committee = committee;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(
        RemoveElectionOutcomeCommand command, CancellationToken cancellationToken)
    {
        var election = await RecordElectionOutcomeHandler
            .LoadElectionAsync(_committee, command.ElectionId, cancellationToken)
            .ConfigureAwait(false);

        election.RemoveOutcome(command.OutcomeId);
        _committee.UpdateElection(election);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Ratifies the election: ends all current appointments and creates new ones.
/// </summary>
/// <param name="ElectionId">The election to ratify.</param>
/// <param name="EffectiveOn">The date the new appointments take effect.</param>
public sealed record RatifyElectionCommand(
    ElectionId ElectionId,
    DateOnly EffectiveOn) : IRequest;

internal sealed class RatifyElectionHandler : IRequestHandler<RatifyElectionCommand>
{
    private readonly ICommitteeRepository _committee;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public RatifyElectionHandler(
        ICommitteeRepository committee, IUnitOfWork unitOfWork, IClock clock)
    {
        _committee = committee;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task Handle(
        RatifyElectionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var election = await RecordElectionOutcomeHandler
            .LoadElectionAsync(_committee, command.ElectionId, cancellationToken)
            .ConfigureAwait(false);

        election.Ratify();

        // End all currently active appointments.
        var active = await _committee
            .AllAppointmentsAsync(activeOnly: true, cancellationToken)
            .ConfigureAwait(false);

        foreach (var existing in active)
        {
            existing.End(command.EffectiveOn);
            _committee.UpdateAppointment(existing);
        }

        // Create a new appointment for each outcome recorded in the election.
        foreach (var outcome in election.Outcomes)
        {
            var appointment = CommitteeAppointment.Appoint(
                outcome.PositionId,
                outcome.PositionName,
                outcome.ElectedName,
                command.EffectiveOn,
                election.Term);

            _committee.AddAppointment(appointment);
        }

        _committee.UpdateElection(election);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
