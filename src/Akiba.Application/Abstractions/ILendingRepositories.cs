using Akiba.Domain.Lending;
using Akiba.Domain.Membership;
using Akiba.Domain.Receipting;

namespace Akiba.Application.Abstractions;

/// <summary>Reads and writes borrowers - members and non-member clients.</summary>
public interface IBorrowerRepository
{
    Task<Borrower?> FindByIdAsync(BorrowerId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a member. Returns null for a non-member client, because a client holds no shares
    /// and most member operations would be meaningless on one.
    /// </summary>
    Task<Member?> FindMemberAsync(BorrowerId id, CancellationToken cancellationToken = default);

    Task<Member?> FindMemberByPayrollNumberAsync(
        PayrollNumber payrollNumber, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Member>> AllMembersAsync(
        bool includeExited = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// The members whose deductions run on the landlord schedule rather than the employee one.
    /// </summary>
    Task<IReadOnlyList<Member>> LandlordMembersAsync(CancellationToken cancellationToken = default);

    void Add(Borrower borrower);

    void Update(Borrower borrower);
}

/// <summary>Reads and writes zones and the office.</summary>
public interface IZoneRepository
{
    Task<Zone?> FindByIdAsync(ZoneId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Zone>> AllAsync(CancellationToken cancellationToken = default);

    void Add(Zone zone);

    void Update(Zone zone);
}

/// <summary>Reads and writes loan applications.</summary>
public interface ILoanApplicationRepository
{
    Task<LoanApplication?> FindByIdAsync(
        LoanApplicationId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LoanApplication>> ByStatusAsync(
        LoanApplicationStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applications approved but not yet disbursed - a queue the office watches, because an
    /// application that missed the 15th waits here for the next cycle.
    /// </summary>
    Task<IReadOnlyList<LoanApplication>> AwaitingDisbursementAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LoanApplication>> ForBorrowerAsync(
        BorrowerId borrowerId, CancellationToken cancellationToken = default);

    void Add(LoanApplication application);

    void Update(LoanApplication application);
}

/// <summary>Reads and writes disbursed loans.</summary>
public interface ILoanRepository
{
    Task<Loan?> FindByIdAsync(LoanId id, CancellationToken cancellationToken = default);

    Task<Loan?> FindByNumberAsync(string loanNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// A borrower's running loans. A member may hold two and no more, so this is checked on
    /// every application.
    /// </summary>
    Task<IReadOnlyList<Loan>> RunningForBorrowerAsync(
        BorrowerId borrowerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every loan a borrower has ever had, settled and written off included.
    /// </summary>
    /// <remarks>
    /// A statement as at a past date needs the loans that were running <i>then</i>, which is
    /// not the same set as the loans running now - a loan settled since must still appear, and
    /// one disbursed since must not.
    /// </remarks>
    Task<IReadOnlyList<Loan>> AllForBorrowerAsync(
        BorrowerId borrowerId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Loan>> AllRunningAsync(CancellationToken cancellationToken = default);

    /// <summary>Every loan a member guarantees, for the exposure report and the exit review.</summary>
    Task<IReadOnlyList<Loan>> GuaranteedByAsync(
        BorrowerId guarantorId, CancellationToken cancellationToken = default);

    void Add(Loan loan);

    void Update(Loan loan);
}

/// <summary>Reads and writes receipts.</summary>
public interface IReceiptRepository
{
    Task<Receipt?> FindByIdAsync(ReceiptId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receipts that have not cleared. A cheque that has not matured has not brought any money
    /// in, and balances must be able to say so.
    /// </summary>
    Task<IReadOnlyList<Receipt>> AwaitingClearanceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleared receipts with money still sitting unallocated. This is the clerk's work queue,
    /// and it is worked by hand because allocation is never inferred.
    /// </summary>
    Task<IReadOnlyList<Receipt>> AwaitingAllocationAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Receipt>> BetweenAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    void Add(Receipt receipt);

    void Update(Receipt receipt);
}
