using Akiba.Application.Dividends;
using Akiba.Domain.Dividends;
using Microsoft.EntityFrameworkCore;

namespace Akiba.Infrastructure.Persistence.Repositories;

/// <summary>Stores and retrieves dividend runs.</summary>
internal sealed class DividendRunRepository : IDividendRunRepository
{
    private readonly AkibaDbContext _context;

    public DividendRunRepository(AkibaDbContext context) => _context = context;

    public async Task<DividendRun?> FindByIdAsync(
        DividendRunId id, CancellationToken cancellationToken = default)
    {
        var row = await _context.DividendRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(run => run.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : DividendMapper.ToDomain(row);
    }

    public async Task<IReadOnlyList<DividendRun>> AllAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.DividendRuns
            .AsNoTracking()
            .OrderByDescending(run => run.Year)
            .ThenByDescending(run => run.ComputedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(DividendMapper.ToDomain)];
    }

    public async Task<IReadOnlyList<DividendRun>> ForYearAsync(
        int year, CancellationToken cancellationToken = default)
    {
        var rows = await _context.DividendRuns
            .AsNoTracking()
            .Where(run => run.Year == year)
            .OrderBy(run => run.ComputedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(DividendMapper.ToDomain)];
    }

    public void Add(DividendRun run) => _context.DividendRuns.Add(DividendMapper.ToRow(run));

    public void Update(DividendRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var existing =
            _context.DividendRuns.Local.FirstOrDefault(row => row.Id == run.Id.Value)
            ?? _context.DividendRuns
                .Include(row => row.Lines)
                .First(row => row.Id == run.Id.Value);

        var updated = DividendMapper.ToRow(run);

        // Only the header changes after a run is computed - reviewing, approving and posting
        // never touch a member's figure. The lines are deliberately left alone, so an approval
        // cannot quietly alter what somebody is owed.
        _context.Entry(existing).CurrentValues.SetValues(updated);
    }
}
