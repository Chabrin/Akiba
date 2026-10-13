using Akiba.Application.Committee;
using Akiba.Domain.Committee;
using Microsoft.EntityFrameworkCore;

namespace Akiba.Infrastructure.Persistence.Repositories;

internal sealed class CommitteeRepository : ICommitteeRepository
{
    private readonly AkibaDbContext _context;

    public CommitteeRepository(AkibaDbContext context) => _context = context;

    // ─── Positions ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<CommitteePosition>> AllPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.CommitteePositions
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(CommitteeMapper.ToDomain)];
    }

    public async Task<CommitteePosition?> FindPositionAsync(
        CommitteePositionId id, CancellationToken cancellationToken = default)
    {
        var row = await _context.CommitteePositions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : CommitteeMapper.ToDomain(row);
    }

    public void AddPosition(CommitteePosition position) =>
        _context.CommitteePositions.Add(CommitteeMapper.ToRow(position));

    public void UpdatePosition(CommitteePosition position)
    {
        ArgumentNullException.ThrowIfNull(position);

        var existing =
            _context.CommitteePositions.Local.FirstOrDefault(r => r.Id == position.Id.Value)
            ?? _context.CommitteePositions.First(r => r.Id == position.Id.Value);

        _context.Entry(existing).CurrentValues.SetValues(CommitteeMapper.ToRow(position));
    }

    // ─── Appointments ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<CommitteeAppointment>> AllAppointmentsAsync(
        bool activeOnly = false, CancellationToken cancellationToken = default)
    {
        var query = _context.CommitteeAppointments.AsNoTracking();

        if (activeOnly)
        {
            query = query.Where(a => a.Status == (int)AppointmentStatus.Active);
        }

        var rows = await query
            .OrderBy(a => a.PositionName)
            .ThenByDescending(a => a.AppointedOn)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(CommitteeMapper.ToDomain)];
    }

    public async Task<CommitteeAppointment?> FindAppointmentAsync(
        CommitteeAppointmentId id, CancellationToken cancellationToken = default)
    {
        var row = await _context.CommitteeAppointments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : CommitteeMapper.ToDomain(row);
    }

    public void AddAppointment(CommitteeAppointment appointment) =>
        _context.CommitteeAppointments.Add(CommitteeMapper.ToRow(appointment));

    public void UpdateAppointment(CommitteeAppointment appointment)
    {
        ArgumentNullException.ThrowIfNull(appointment);

        var existing =
            _context.CommitteeAppointments.Local.FirstOrDefault(r => r.Id == appointment.Id.Value)
            ?? _context.CommitteeAppointments.First(r => r.Id == appointment.Id.Value);

        _context.Entry(existing).CurrentValues.SetValues(CommitteeMapper.ToRow(appointment));
    }

    // ─── Elections ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ElectionRecord>> AllElectionsAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.ElectionRecords
            .AsNoTracking()
            .OrderByDescending(e => e.HeldOn)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(CommitteeMapper.ToDomain)];
    }

    public async Task<ElectionRecord?> FindElectionAsync(
        ElectionId id, CancellationToken cancellationToken = default)
    {
        var row = await _context.ElectionRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : CommitteeMapper.ToDomain(row);
    }

    public void AddElection(ElectionRecord election) =>
        _context.ElectionRecords.Add(CommitteeMapper.ToRow(election));

    public void UpdateElection(ElectionRecord election)
    {
        ArgumentNullException.ThrowIfNull(election);

        var existing =
            _context.ElectionRecords.Local.FirstOrDefault(r => r.Id == election.Id.Value)
            ?? _context.ElectionRecords
                .Include(r => r.Outcomes)
                .First(r => r.Id == election.Id.Value);

        var updated = CommitteeMapper.ToRow(election);
        _context.Entry(existing).CurrentValues.SetValues(updated);

        // Sync outcome rows: add new, update existing, remove deleted.
        foreach (var outcomeRow in updated.Outcomes)
        {
            var existingOutcome = existing.Outcomes.FirstOrDefault(o => o.Id == outcomeRow.Id);
            if (existingOutcome is null)
            {
                existing.Outcomes.Add(outcomeRow);
                _context.ElectionOutcomes.Add(outcomeRow);
            }
            else
            {
                _context.Entry(existingOutcome).CurrentValues.SetValues(outcomeRow);
            }
        }

        var toRemove = existing.Outcomes
            .Where(o => !updated.Outcomes.Any(u => u.Id == o.Id))
            .ToList();

        foreach (var gone in toRemove)
        {
            existing.Outcomes.Remove(gone);
            _context.ElectionOutcomes.Remove(gone);
        }
    }
}
