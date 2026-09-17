namespace Akiba.Infrastructure.Persistence.Rows;

/// <summary>The stored shape of a loan application.</summary>
internal sealed class LoanApplicationRow
{
    public Guid Id { get; set; }

    public Guid BorrowerId { get; set; }

    public Guid ZoneId { get; set; }

    public int Product { get; set; }

    public decimal RequestedPrincipal { get; set; }

    public int? RequestedTermMonths { get; set; }

    public DateOnly ReceivedOn { get; set; }

    /// <summary>
    /// The month the application is considered in - the month it arrived, or the next one
    /// where it missed the 15th.
    /// </summary>
    public DateOnly ConsiderationMonth { get; set; }

    public int CutoffVersion { get; set; }

    public int Status { get; set; }

    public decimal? DeclaredGrossSalary { get; set; }

    public decimal? ApprovedPrincipal { get; set; }

    // The approved terms, flattened. Stored rather than recomputed, because the terms a
    // member agreed to must not change when a band is revised afterwards.
    public decimal? ApprovedInterest { get; set; }

    public int? ApprovedTermMonths { get; set; }

    public int? ApprovedTermScaleVersion { get; set; }

    public string? RejectionReason { get; set; }

    // --- Rental income security, from the revised form ----------------
    public string? PropertyName { get; set; }

    public string? PropertyLocation { get; set; }

    public decimal? MonthlyRentalIncome { get; set; }

    public int? NumberOfRentalUnits { get; set; }

    public bool RentStatementsAttached { get; set; }

    public List<ApprovalDecisionRow> Decisions { get; set; } = [];

    public List<GuaranteeRow> Guarantees { get; set; } = [];

    public List<LoanSecurityRow> Security { get; set; } = [];

    public List<AttachedDocumentRow> Documents { get; set; } = [];
}

/// <summary>One zone or office representative's decision.</summary>
internal sealed class ApprovalDecisionRow
{
    public Guid Id { get; set; }

    public Guid LoanApplicationId { get; set; }

    public Guid ApproverUserId { get; set; }

    public string ApproverName { get; set; } = string.Empty;

    public int Decision { get; set; }

    public DateTimeOffset DecidedAtUtc { get; set; }

    public string? Comment { get; set; }
}

/// <summary>
/// One guarantor's row from the form's guarantor schedule.
/// </summary>
/// <remarks>
/// A guarantee belongs to an application and is copied onto the loan at disbursement, so it
/// carries both keys and exactly one of them is set.
/// </remarks>
internal sealed class GuaranteeRow
{
    public Guid Id { get; set; }

    public Guid? LoanApplicationId { get; set; }

    public Guid? LoanId { get; set; }

    public Guid GuarantorId { get; set; }

    public string GuarantorName { get; set; } = string.Empty;

    public string GuarantorPayrollNumber { get; set; } = string.Empty;

    public decimal GuaranteedAmount { get; set; }

    /// <summary>What the officials saw when the form was signed, not a live figure.</summary>
    public decimal ShareValueAtSigning { get; set; }

    public DateOnly SignedOn { get; set; }

    public bool IsReleased { get; set; }
}

internal sealed class LoanSecurityRow
{
    public Guid Id { get; set; }

    public Guid LoanApplicationId { get; set; }

    public int Kind { get; set; }

    public string Details { get; set; } = string.Empty;
}

/// <summary>
/// The stored shape of a disbursed loan.
/// </summary>
/// <remarks>
/// There is no outstanding balance column here and there never will be. What a borrower owes
/// is the balance of <see cref="ReceivableAccountId"/>, derived as at a date.
/// </remarks>
internal sealed class LoanRow
{
    public Guid Id { get; set; }

    public string LoanNumber { get; set; } = string.Empty;

    public Guid ApplicationId { get; set; }

    public Guid BorrowerId { get; set; }

    public Guid ReceivableAccountId { get; set; }

    public int Product { get; set; }

    public decimal Principal { get; set; }

    public decimal Interest { get; set; }

    public int TermMonths { get; set; }

    public int TermScaleVersion { get; set; }

    public DateOnly DisbursedOn { get; set; }

    public int Status { get; set; }

    public Guid? RestructuresLoanId { get; set; }

    public bool HasBeenRestructured { get; set; }

    public DateOnly? SettledOn { get; set; }

    // --- The cheque ---------------------------------------------------
    public string ChequeNumber { get; set; } = string.Empty;

    public string VoucherReference { get; set; } = string.Empty;

    public decimal ChequeAmount { get; set; }

    public DateOnly ChequeDrawnOn { get; set; }

    /// <summary>The two signatories, stored newline-separated.</summary>
    public string ChequeSignatories { get; set; } = string.Empty;

    public List<GuaranteeRow> Guarantees { get; set; } = [];
}

/// <summary>The stored shape of a receipt.</summary>
internal sealed class ReceiptRow
{
    public Guid Id { get; set; }

    public int Channel { get; set; }

    public int Method { get; set; }

    public decimal Amount { get; set; }

    public DateOnly ReceivedOn { get; set; }

    public string Reference { get; set; } = string.Empty;

    /// <summary>Kept as written on the slip, not normalised to a member's record.</summary>
    public string? PayerNameOnSlip { get; set; }

    public Guid? IdentifiedBorrowerId { get; set; }

    public DateOnly? ExpectedClearanceOn { get; set; }

    public int Status { get; set; }

    public DateOnly? ClearedOn { get; set; }

    public DateOnly? ReconciledOn { get; set; }

    public string? BankStatementReference { get; set; }

    public List<ReceiptAllocationRow> Allocations { get; set; } = [];
}

/// <summary>
/// One decision to apply part of a receipt to something.
/// </summary>
/// <remarks>
/// A reversed allocation is marked, never deleted, so the record shows that a decision was
/// made and then changed.
/// </remarks>
internal sealed class ReceiptAllocationRow
{
    public Guid Id { get; set; }

    public Guid ReceiptId { get; set; }

    public int Sequence { get; set; }

    public int Target { get; set; }

    public Guid TargetId { get; set; }

    public decimal Amount { get; set; }

    public Guid AllocatedByUserId { get; set; }

    public string AllocatedByName { get; set; } = string.Empty;

    public DateTimeOffset AllocatedAtUtc { get; set; }

    public string? ReversedReason { get; set; }
}
