using Akiba.Application.Abstractions;
using Akiba.Domain.Ledger;
using Akiba.Domain.Reconciliation;
using Akiba.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Akiba.Infrastructure.Persistence.Repositories;

/// <summary>Stores and retrieves bank reconciliations.</summary>
internal sealed class BankReconciliationRepository : IBankReconciliationRepository
{
    private readonly AkibaDbContext _context;

    public BankReconciliationRepository(AkibaDbContext context) => _context = context;

    public async Task<BankReconciliation?> FindByIdAsync(
        BankStatementId id, CancellationToken cancellationToken = default)
    {
        var row = await _context.BankReconciliations
            .AsNoTracking()
            .FirstOrDefaultAsync(reconciliation => reconciliation.Id == id.Value, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ReconciliationMapper.ToDomain(row);
    }

    public async Task<IReadOnlyList<BankReconciliation>> AllAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.BankReconciliations
            .AsNoTracking()
            .OrderByDescending(reconciliation => reconciliation.To)
            .ThenBy(reconciliation => reconciliation.AccountLabel)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(ReconciliationMapper.ToDomain)];
    }

    public async Task<IReadOnlyList<BankReconciliation>> OverlappingAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            throw new ArgumentException(
                $"The period end {to:yyyy-MM-dd} is before its start {from:yyyy-MM-dd}.", nameof(to));
        }

        var rows = await _context.BankReconciliations
            .AsNoTracking()
            .Where(reconciliation => reconciliation.From <= to && reconciliation.To >= from)
            .OrderBy(reconciliation => reconciliation.From)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(ReconciliationMapper.ToDomain)];
    }

    public async Task<BankReconciliation?> PrecedingAsync(
        AccountId bankAccountId, DateOnly from, CancellationToken cancellationToken = default)
    {
        var row = await _context.BankReconciliations
            .AsNoTracking()
            .Where(reconciliation => reconciliation.BankAccountId == bankAccountId.Value
                && reconciliation.To < from)
            .OrderByDescending(reconciliation => reconciliation.To)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ReconciliationMapper.ToDomain(row);
    }

    public void Add(BankReconciliation reconciliation) =>
        _context.BankReconciliations.Add(ReconciliationMapper.ToRow(reconciliation));

    public void Update(BankReconciliation reconciliation)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);

        // Local first. A reconciliation imported and then re-matched inside one command is
        // already tracked, and querying the database for it would find nothing.
        var existing =
            _context.BankReconciliations.Local
                .FirstOrDefault(row => row.Id == reconciliation.Id.Value)
            ?? _context.BankReconciliations
                .Include(row => row.Lines)
                .First(row => row.Id == reconciliation.Id.Value);

        var updated = ReconciliationMapper.ToRow(reconciliation);

        _context.Entry(existing).CurrentValues.SetValues(updated);
        MergeLines(existing, updated);
    }

    /// <summary>
    /// Brings the stored lines into step with the aggregate's, line number by line number.
    /// </summary>
    /// <remarks>
    /// Updated in place rather than deleted and re-inserted. A statement line keeps its
    /// identity for the life of the statement - matching it is an edit to that line, not a
    /// replacement of it - and deleting a row only to insert another with the same key in the
    /// same transaction is how a working save turns into a primary key violation.
    /// </remarks>
    private void MergeLines(BankReconciliationRow existing, BankReconciliationRow updated)
    {
        var byLineNumber = existing.Lines.ToDictionary(line => line.LineNumber);

        foreach (var line in updated.Lines)
        {
            if (byLineNumber.TryGetValue(line.LineNumber, out var stored))
            {
                // Keep the stored row's key; everything else comes from the aggregate.
                line.Id = stored.Id;
                _context.Entry(stored).CurrentValues.SetValues(line);
                byLineNumber.Remove(line.LineNumber);
            }
            else
            {
                _context.BankStatementLines.Add(line);
            }
        }

        foreach (var orphan in byLineNumber.Values)
        {
            _context.BankStatementLines.Remove(orphan);
        }
    }
}
