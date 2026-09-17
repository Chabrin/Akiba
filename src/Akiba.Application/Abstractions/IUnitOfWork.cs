namespace Akiba.Application.Abstractions;

/// <summary>
/// Commits everything a command changed, as one transaction.
/// </summary>
/// <remarks>
/// A disbursement writes a loan, a journal entry and an account in one go. Half of that
/// reaching the database would leave the ledger unbalanced, which is precisely the state the
/// whole design exists to make impossible.
/// </remarks>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
