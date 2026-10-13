using Akiba.Domain.Committee;
using Akiba.Domain.Financial;
using MediatR;

namespace Akiba.Application.Committee;

// ─── Position summaries ───────────────────────────────────────────────────────

public sealed record CommitteePositionSummary(
    CommitteePositionId Id,
    string Name,
    string? Description,
    decimal AllowanceAmount,
    AllowanceFrequency Frequency,
    bool IsActive);

/// <summary>Returns all committee positions.</summary>
public sealed record ListCommitteePositionsQuery : IRequest<IReadOnlyList<CommitteePositionSummary>>;

internal sealed class ListCommitteePositionsHandler
    : IRequestHandler<ListCommitteePositionsQuery, IReadOnlyList<CommitteePositionSummary>>
{
    private readonly ICommitteeRepository _committee;

    public ListCommitteePositionsHandler(ICommitteeRepository committee) =>
        _committee = committee;

    public async Task<IReadOnlyList<CommitteePositionSummary>> Handle(
        ListCommitteePositionsQuery request, CancellationToken cancellationToken)
    {
        var positions = await _committee
            .AllPositionsAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. positions
            .OrderBy(p => p.Name)
            .Select(p => new CommitteePositionSummary(
                p.Id, p.Name, p.Description, p.Allowance.Amount, p.Frequency, p.IsActive))];
    }
}

// ─── Appointment summaries ────────────────────────────────────────────────────

public sealed record CommitteeAppointmentSummary(
    CommitteeAppointmentId Id,
    CommitteePositionId PositionId,
    string PositionName,
    string HolderName,
    DateOnly AppointedOn,
    string Term,
    AppointmentStatus Status,
    string? SuspendedReason,
    DateOnly? EndedOn);

/// <summary>Returns committee appointments, optionally only active ones.</summary>
public sealed record ListCommitteeAppointmentsQuery(
    bool ActiveOnly = false)
    : IRequest<IReadOnlyList<CommitteeAppointmentSummary>>;

internal sealed class ListCommitteeAppointmentsHandler
    : IRequestHandler<ListCommitteeAppointmentsQuery, IReadOnlyList<CommitteeAppointmentSummary>>
{
    private readonly ICommitteeRepository _committee;

    public ListCommitteeAppointmentsHandler(ICommitteeRepository committee) =>
        _committee = committee;

    public async Task<IReadOnlyList<CommitteeAppointmentSummary>> Handle(
        ListCommitteeAppointmentsQuery request, CancellationToken cancellationToken)
    {
        var appointments = await _committee
            .AllAppointmentsAsync(request.ActiveOnly, cancellationToken)
            .ConfigureAwait(false);

        return [.. appointments
            .OrderBy(a => a.PositionName)
            .ThenByDescending(a => a.AppointedOn)
            .Select(a => new CommitteeAppointmentSummary(
                a.Id, a.PositionId, a.PositionName, a.HolderName,
                a.AppointedOn, a.Term, a.Status, a.SuspendedReason, a.EndedOn))];
    }
}

/// <summary>
/// Estimates the total annual allowances for all active appointments.
/// Monthly positions are multiplied by 12; per-sitting positions by the given sitting count.
/// </summary>
public sealed record EstimateAnnualAllowancesQuery(int SittingsPerYear = 12)
    : IRequest<decimal>;

internal sealed class EstimateAnnualAllowancesHandler
    : IRequestHandler<EstimateAnnualAllowancesQuery, decimal>
{
    private readonly ICommitteeRepository _committee;

    public EstimateAnnualAllowancesHandler(ICommitteeRepository committee) =>
        _committee = committee;

    public async Task<decimal> Handle(
        EstimateAnnualAllowancesQuery request, CancellationToken cancellationToken)
    {
        var appointments = await _committee
            .AllAppointmentsAsync(activeOnly: true, cancellationToken)
            .ConfigureAwait(false);

        var positions = await _committee
            .AllPositionsAsync(cancellationToken)
            .ConfigureAwait(false);

        var positionMap = positions.ToDictionary(p => p.Id);

        var total = 0m;

        foreach (var appointment in appointments)
        {
            if (!positionMap.TryGetValue(appointment.PositionId, out var position))
            {
                continue;
            }

            var multiplier = position.Frequency == AllowanceFrequency.Monthly
                ? 12m
                : request.SittingsPerYear;

            total += position.Allowance.Amount * multiplier;
        }

        return total;
    }
}

// ─── Election summaries ───────────────────────────────────────────────────────

public sealed record ElectionOutcomeSummary(
    Guid Id,
    CommitteePositionId PositionId,
    string PositionName,
    string ElectedName);

public sealed record ElectionSummary(
    ElectionId Id,
    DateOnly HeldOn,
    string Term,
    string? Notes,
    ElectionStatus Status,
    IReadOnlyList<ElectionOutcomeSummary> Outcomes);

/// <summary>Returns all election records.</summary>
public sealed record ListElectionsQuery : IRequest<IReadOnlyList<ElectionSummary>>;

internal sealed class ListElectionsHandler
    : IRequestHandler<ListElectionsQuery, IReadOnlyList<ElectionSummary>>
{
    private readonly ICommitteeRepository _committee;

    public ListElectionsHandler(ICommitteeRepository committee) =>
        _committee = committee;

    public async Task<IReadOnlyList<ElectionSummary>> Handle(
        ListElectionsQuery request, CancellationToken cancellationToken)
    {
        var elections = await _committee
            .AllElectionsAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. elections
            .OrderByDescending(e => e.HeldOn)
            .Select(e => new ElectionSummary(
                e.Id,
                e.HeldOn,
                e.Term,
                e.Notes,
                e.Status,
                [.. e.Outcomes.Select(o => new ElectionOutcomeSummary(
                    o.Id, o.PositionId, o.PositionName, o.ElectedName))]))];
    }
}
