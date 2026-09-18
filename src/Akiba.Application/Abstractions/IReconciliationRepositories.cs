using Akiba.Domain.Ledger;
using Akiba.Domain.Reconciliation;

namespace Akiba.Application.Abstractions;

/// <summary>Reads and writes bank reconciliations.</summary>
public interface IBankReconciliationRepository
{
    Task<BankReconciliation?> FindByIdAsync(
        BankStatementId id, CancellationToken cancellationToken = default);

    /// <summary>Every reconciliation, newest statement first, for the work list.</summary>
    Task<IReadOnlyList<BankReconciliation>> AllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciliations whose statement period overlaps a date range.
    /// </summary>
    /// <remarks>
    /// This is what the period close asks: before closing September, what reconciliations
    /// cover any part of September, and are they all signed off?
    /// </remarks>
    Task<IReadOnlyList<BankReconciliation>> OverlappingAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>
    /// The statement immediately preceding this one on the same account, if there is one.
    /// </summary>
    /// <remarks>
    /// Used to check that one statement's closing balance is the next one's opening balance. A
    /// gap between them means a statement was never imported, and everything in it is
    /// unreconciled without anything on screen saying so.
    /// </remarks>
    Task<BankReconciliation?> PrecedingAsync(
        AccountId bankAccountId, DateOnly from, CancellationToken cancellationToken = default);

    void Add(BankReconciliation reconciliation);

    void Update(BankReconciliation reconciliation);
}

/// <summary>One row as it was read out of a bank statement file.</summary>
/// <param name="LineNumber">Its position in the file, counting from 1.</param>
/// <param name="ValueDate">The date the bank gave it.</param>
/// <param name="Description">What the bank printed, untouched.</param>
/// <param name="Amount">A positive amount.</param>
/// <param name="Direction">Whether it went in or out.</param>
/// <param name="BankReference">A cheque number or transaction reference, where the file has one.</param>
public sealed record BankStatementRow(
    int LineNumber,
    DateOnly ValueDate,
    string Description,
    decimal Amount,
    StatementDirection Direction,
    string? BankReference);

/// <summary>What came out of a statement file, including what could not be read.</summary>
/// <param name="Rows">The lines that were understood.</param>
/// <param name="Problems">
/// Rows that were not. Reported rather than skipped: a statement missing three lines
/// reconciles to the wrong figure, and silently dropping them is how that happens.
/// </param>
public sealed record BankStatementFile(
    IReadOnlyList<BankStatementRow> Rows,
    IReadOnlyList<string> Problems);

/// <summary>
/// Reads a bank statement out of a CSV or Excel file.
/// </summary>
/// <remarks>
/// Kenyan banks export statements in their own shapes and change them without notice, so this
/// is deliberately forgiving about column names and strict about figures: a row it cannot read
/// becomes a reported problem, never a silently dropped line.
/// </remarks>
public interface IBankStatementReader
{
    /// <summary>Reads a statement file.</summary>
    /// <param name="content">The file's bytes.</param>
    /// <param name="fileName">Used to tell a CSV from a workbook.</param>
    BankStatementFile Read(byte[] content, string fileName);
}
