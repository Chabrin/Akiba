using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Guaranteeing;
using Akiba.Domain.Ledger;
using Akiba.Domain.Lending;
using Akiba.Domain.Membership;
using Akiba.Domain.Receipting;
using Akiba.Infrastructure.Persistence.Rows;

namespace Akiba.Infrastructure.Persistence;

/// <summary>Translates loans, applications and receipts between storage and the domain.</summary>
internal static class LendingMapper
{
    private static Money Kes(decimal amount) => Money.Kes(amount);

    // -----------------------------------------------------------------
    // Applications
    // -----------------------------------------------------------------

    public static LoanApplicationRow ToRow(LoanApplication application)
    {
        var row = new LoanApplicationRow
        {
            Id = application.Id.Value,
            BorrowerId = application.BorrowerId.Value,
            ZoneId = application.ZoneId.Value,
            Product = (int)application.Product,
            RequestedPrincipal = application.RequestedPrincipal.Amount,
            RequestedTermMonths = application.RequestedTermMonths,
            ReceivedOn = application.ReceivedOn,
            ConsiderationMonth = application.ConsiderationMonth,
            CutoffVersion = application.CutoffVersion,
            Status = (int)application.Status,
            DeclaredGrossSalary = application.DeclaredGrossSalary?.Amount,
            ApprovedPrincipal = application.ApprovedPrincipal?.Amount,
            ApprovedInterest = application.ApprovedTerms?.Interest.Amount,
            ApprovedTermMonths = application.ApprovedTerms?.TermMonths,
            ApprovedTermScaleVersion = application.ApprovedTerms?.TermScaleVersion,
            RejectionReason = application.RejectionReason,
            PropertyName = application.RentalIncome?.PropertyName,
            PropertyLocation = application.RentalIncome?.PropertyLocation,
            MonthlyRentalIncome = application.RentalIncome?.MonthlyRentalIncome.Amount,
            NumberOfRentalUnits = application.RentalIncome?.NumberOfUnits,
            RentStatementsAttached = application.RentalIncome?.RentStatementsAttached ?? false,
        };

        row.Decisions =
        [
            .. application.Decisions.Select(decision => new ApprovalDecisionRow
            {
                Id = Guid.NewGuid(),
                LoanApplicationId = application.Id.Value,
                ApproverUserId = decision.Approver.UserId,
                ApproverName = decision.Approver.DisplayName,
                Decision = (int)decision.Decision,
                DecidedAtUtc = decision.DecidedAtUtc,
                Comment = decision.Comment,
            }),
        ];

        row.Guarantees =
        [
            .. application.Guarantees.Select(guarantee =>
                ToRow(guarantee, application.Id.Value, null)),
        ];

        row.Security =
        [
            .. application.Security.Select(security => new LoanSecurityRow
            {
                Id = Guid.NewGuid(),
                LoanApplicationId = application.Id.Value,
                Kind = (int)security.Kind,
                Details = security.Details,
            }),
        ];

        row.Documents =
        [
            .. application.Documents.Select(document =>
                MembershipMapper.ToRow(document, null, application.Id.Value)),
        ];

        return row;
    }

    public static LoanApplication ToDomain(LoanApplicationRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        // The approved terms are stored rather than recomputed. A member's agreed figures must
        // not change because a band was revised after they signed.
        LoanTerms? approvedTerms =
            row.ApprovedPrincipal is { } principal
            && row.ApprovedInterest is { } interest
            && row.ApprovedTermMonths is { } term
                ? new LoanTerms(
                    (LoanProduct)row.Product,
                    Kes(principal),
                    Kes(interest),
                    term,
                    row.ApprovedTermScaleVersion ?? 0)
                : null;

        var application = LoanApplication.Rehydrate(
            new LoanApplicationId(row.Id),
            new BorrowerId(row.BorrowerId),
            new ZoneId(row.ZoneId),
            (LoanProduct)row.Product,
            Kes(row.RequestedPrincipal),
            row.RequestedTermMonths,
            row.ReceivedOn,
            row.ConsiderationMonth,
            row.CutoffVersion,
            (LoanApplicationStatus)row.Status,
            row.DeclaredGrossSalary is { } salary ? Kes(salary) : null,
            row.ApprovedPrincipal is { } approved ? Kes(approved) : null,
            approvedTerms,
            row.RejectionReason,
            row.PropertyName is null || row.MonthlyRentalIncome is null || row.NumberOfRentalUnits is null
                ? null
                : new RentalIncomeSecurity(
                    row.PropertyName,
                    row.PropertyLocation ?? string.Empty,
                    Kes(row.MonthlyRentalIncome.Value),
                    row.NumberOfRentalUnits.Value,
                    row.RentStatementsAttached),
            row.Decisions
                .OrderBy(decision => decision.DecidedAtUtc)
                .Select(decision => new ApprovalDecision(
                    new Actor(decision.ApproverUserId, decision.ApproverName),
                    (ApprovalDecisionKind)decision.Decision,
                    decision.DecidedAtUtc,
                    decision.Comment)),
            row.Guarantees.Select(ToDomain),
            row.Security.Select(security =>
                new LoanSecurity((LoanSecurityKind)security.Kind, security.Details)),
            row.Documents.Select(MembershipMapper.ToDomain));

        return application;
    }

    // -----------------------------------------------------------------
    // Guarantees
    // -----------------------------------------------------------------

    public static GuaranteeRow ToRow(Guarantee guarantee, Guid? applicationId, Guid? loanId) => new()
    {
        Id = Guid.NewGuid(),
        LoanApplicationId = applicationId,
        LoanId = loanId,
        GuarantorId = guarantee.GuarantorId.Value,
        GuarantorName = guarantee.GuarantorName,
        GuarantorPayrollNumber = guarantee.GuarantorPayrollNumber.Value,
        GuaranteedAmount = guarantee.GuaranteedAmount.Amount,
        ShareValueAtSigning = guarantee.ShareValueAtSigning.Amount,
        SignedOn = guarantee.SignedOn,
        IsReleased = guarantee.IsReleased,
    };

    public static Guarantee ToDomain(GuaranteeRow row) => new(
        new BorrowerId(row.GuarantorId),
        row.GuarantorName,
        PayrollNumber.Of(row.GuarantorPayrollNumber),
        Kes(row.GuaranteedAmount),
        Kes(row.ShareValueAtSigning),
        row.SignedOn)
    {
        IsReleased = row.IsReleased,
    };

    // -----------------------------------------------------------------
    // Loans
    // -----------------------------------------------------------------

    public static LoanRow ToRow(Loan loan)
    {
        var row = new LoanRow
        {
            Id = loan.Id.Value,
            LoanNumber = loan.LoanNumber,
            ApplicationId = loan.ApplicationId.Value,
            BorrowerId = loan.BorrowerId.Value,
            ReceivableAccountId = loan.ReceivableAccountId.Value,
            Product = (int)loan.Terms.Product,
            Principal = loan.Terms.Principal.Amount,
            Interest = loan.Terms.Interest.Amount,
            TermMonths = loan.Terms.TermMonths,
            TermScaleVersion = loan.Terms.TermScaleVersion,
            DisbursedOn = loan.DisbursedOn,
            Status = (int)loan.Status,
            RestructuresLoanId = loan.Restructures?.Value,
            HasBeenRestructured = loan.HasBeenRestructured,
            SettledOn = loan.SettledOn,
            ChequeNumber = loan.Cheque.ChequeNumber,
            VoucherReference = loan.Cheque.VoucherReference,
            ChequeAmount = loan.Cheque.Amount.Amount,
            ChequeDrawnOn = loan.Cheque.DrawnOn,
            ChequeSignatories = string.Join('\n', loan.Cheque.Signatories),
        };

        row.Guarantees = [.. loan.Guarantees.Select(guarantee => ToRow(guarantee, null, loan.Id.Value))];

        return row;
    }

    public static Loan ToDomain(LoanRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var terms = new LoanTerms(
            (LoanProduct)row.Product,
            Kes(row.Principal),
            Kes(row.Interest),
            row.TermMonths,
            row.TermScaleVersion);

        var cheque = new ChequeDetails(
            row.ChequeNumber,
            row.VoucherReference,
            Kes(row.ChequeAmount),
            row.ChequeDrawnOn,
            row.ChequeSignatories.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        return Loan.Rehydrate(
            new LoanId(row.Id),
            row.LoanNumber,
            new LoanApplicationId(row.ApplicationId),
            new BorrowerId(row.BorrowerId),
            new AccountId(row.ReceivableAccountId),
            terms,
            row.DisbursedOn,
            cheque,
            (LoanStatus)row.Status,
            row.RestructuresLoanId is { } restructures ? new LoanId(restructures) : null,
            row.HasBeenRestructured,
            row.SettledOn,
            row.Guarantees.Select(ToDomain));
    }

    // -----------------------------------------------------------------
    // Receipts
    // -----------------------------------------------------------------

    public static ReceiptRow ToRow(Receipt receipt)
    {
        var row = new ReceiptRow
        {
            Id = receipt.Id.Value,
            Channel = (int)receipt.Channel,
            Method = (int)receipt.Method,
            Amount = receipt.Amount.Amount,
            ReceivedOn = receipt.ReceivedOn,
            Reference = receipt.Reference,
            PayerNameOnSlip = receipt.PayerNameOnSlip,
            IdentifiedBorrowerId = receipt.IdentifiedBorrower?.Value,
            ExpectedClearanceOn = receipt.ExpectedClearanceOn,
            Status = (int)receipt.Status,
            ClearedOn = receipt.ClearedOn,
            ReconciledOn = receipt.ReconciledOn,
            BankStatementReference = receipt.BankStatementReference,
        };

        row.Allocations =
        [
            .. receipt.Allocations.Select((allocation, index) => new ReceiptAllocationRow
            {
                Id = Guid.NewGuid(),
                ReceiptId = receipt.Id.Value,
                Sequence = index,
                Target = (int)allocation.Target,
                TargetId = allocation.TargetId,
                Amount = allocation.Amount.Amount,
                AllocatedByUserId = allocation.AllocatedBy.UserId,
                AllocatedByName = allocation.AllocatedBy.DisplayName,
                AllocatedAtUtc = allocation.AllocatedAtUtc,
                ReversedReason = allocation.ReversedReason,
            }),
        ];

        return row;
    }

    public static Receipt ToDomain(ReceiptRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return Receipt.Rehydrate(
            new ReceiptId(row.Id),
            (ReceiptChannel)row.Channel,
            (ReceiptMethod)row.Method,
            Kes(row.Amount),
            row.ReceivedOn,
            row.Reference,
            row.PayerNameOnSlip,
            row.IdentifiedBorrowerId is { } borrower ? new BorrowerId(borrower) : null,
            row.ExpectedClearanceOn,
            (ReceiptStatus)row.Status,
            row.ClearedOn,
            row.ReconciledOn,
            row.BankStatementReference,
            row.Allocations
                .OrderBy(allocation => allocation.Sequence)
                .Select(allocation => new ReceiptAllocation(
                    (AllocationTarget)allocation.Target,
                    allocation.TargetId,
                    Kes(allocation.Amount),
                    new Actor(allocation.AllocatedByUserId, allocation.AllocatedByName),
                    allocation.AllocatedAtUtc,
                    allocation.ReversedReason)));
    }
}
