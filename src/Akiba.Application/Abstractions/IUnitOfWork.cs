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

/// <summary>
/// The database refused a save because somebody else got there first.
/// </summary>
/// <remarks>
/// <para>
/// Two cases, both ordinary on a system four people use at once: another official changed the
/// same record between this one reading it and saving it, or recorded the same thing - a cheque
/// book, a reference - a moment earlier. Nothing from the refused command was written.
/// </para>
/// <para>
/// An <see cref="InvalidOperationException"/>, so every screen shows the message inline, in the
/// panel where the official pressed the button, the way it shows any other refusal. Left as the
/// database's own exception it reached the error boundary and replaced the whole screen with
/// "an unexpected error occurred" - which reads as a fault, when it is the system doing its job.
/// </para>
/// </remarks>
public sealed class ChangeConflictException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
