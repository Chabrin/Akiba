using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Guaranteeing;
using Akiba.Domain.Membership;

namespace Akiba.Domain.Lending;

/// <summary>Identifies a loan application.</summary>
public readonly record struct LoanApplicationId(Guid Value)
{
    public static LoanApplicationId New() => new(Guid.NewGuid());

    public bool IsSpecified => Value != Guid.Empty;

    public override string ToString() => Value.ToString();
}

/// <summary>Where an application has got to.</summary>
public enum LoanApplicationStatus
{
    /// <summary>Being entered by the accounts clerk from the paper form.</summary>
    Draft = 1,

    /// <summary>Received at the office and awaiting the zone representatives' decisions.</summary>
    Submitted = 2,

    /// <summary>
    /// Approved and awaiting a cheque. This is a queue the office watches, because an
    /// application that missed the lock period waits here for the next cycle.
    /// </summary>
    Approved = 3,

    /// <summary>The cheque has been drawn and the loan exists.</summary>
    Disbursed = 4,

    Rejected = 5,

    /// <summary>Withdrawn by the applicant before a decision.</summary>
    Withdrawn = 6,
}

public enum ApprovalDecisionKind
{
    Approve = 1,
    Reject = 2,
}

/// <summary>One representative's decision on an application.</summary>
/// <param name="Approver">The representative.</param>
/// <param name="Decision">What they decided.</param>
/// <param name="DecidedAtUtc">When.</param>
/// <param name="Comment">Their remarks, where they made any.</param>
public sealed record ApprovalDecision(
    Actor Approver,
    ApprovalDecisionKind Decision,
    DateTimeOffset DecidedAtUtc,
    string? Comment);

/// <summary>
/// A member's loan application, from the paper form to the cheque.
/// </summary>
/// <remarks>
/// <para>
/// Approval is by the member representatives for the applicant's zone and the office, so an
/// application collects several decisions rather than one. There is no single official who
/// can approve alone.
/// </para>
/// <para>
/// The original signed form is scanned and attached. The system records the figures; the scan
/// is the evidence.
/// </para>
/// </remarks>
public sealed class LoanApplication : AggregateRoot<LoanApplicationId>
{
    private readonly List<ApprovalDecision> _decisions = [];
    private readonly List<Guarantee> _guarantees = [];
    private readonly List<LoanSecurity> _security = [];
    private readonly List<AttachedDocument> _documents = [];

    private LoanApplication(
        LoanApplicationId id,
        BorrowerId borrowerId,
        ZoneId zoneId,
        LoanProduct product,
        Money requestedPrincipal,
        int? requestedTermMonths,
        DateOnly receivedOn,
        DateOnly considerationMonth,
        int cutoffVersion)
        : base(id)
    {
        BorrowerId = borrowerId;
        ZoneId = zoneId;
        Product = product;
        RequestedPrincipal = requestedPrincipal.Round();
        RequestedTermMonths = requestedTermMonths;
        ReceivedOn = receivedOn;
        ConsiderationMonth = considerationMonth;
        CutoffVersion = cutoffVersion;
        Status = LoanApplicationStatus.Draft;
    }

    public BorrowerId BorrowerId { get; }

    /// <summary>The zone whose representatives decide this application.</summary>
    public ZoneId ZoneId { get; }

    public LoanProduct Product { get; }

    public Money RequestedPrincipal { get; private set; }

    /// <summary>The repayment period the applicant wrote on the form, where they stated one.</summary>
    public int? RequestedTermMonths { get; private set; }

    /// <summary>The date the form was received at the office.</summary>
    public DateOnly ReceivedOn { get; }

    /// <summary>
    /// The month this application is considered in. The month it was received where it made
    /// the cutoff, the following month where it did not.
    /// </summary>
    public DateOnly ConsiderationMonth { get; }

    /// <summary>The version of the cutoff rule that decided the consideration month.</summary>
    public int CutoffVersion { get; }

    /// <summary>True where the form arrived after the cutoff and waits for the next cycle.</summary>
    public bool MissedTheCutoff => ConsiderationMonth.Month != ReceivedOn.Month
        || ConsiderationMonth.Year != ReceivedOn.Year;

    public LoanApplicationStatus Status { get; private set; }

    /// <summary>The applicant's declared gross monthly salary, from the form.</summary>
    public Money? DeclaredGrossSalary { get; private set; }

    /// <summary>Rental income details, on a rental-income application.</summary>
    public RentalIncomeSecurity? RentalIncome { get; private set; }

    /// <summary>The terms the loan was approved on. Set at approval.</summary>
    public LoanTerms? ApprovedTerms { get; private set; }

    public Money? ApprovedPrincipal { get; private set; }

    public IReadOnlyList<ApprovalDecision> Decisions => _decisions;

    public IReadOnlyList<Guarantee> Guarantees => _guarantees;

    public IReadOnlyList<LoanSecurity> Security => _security;

    public IReadOnlyList<AttachedDocument> Documents => _documents;

    public string? RejectionReason { get; private set; }

    /// <summary>
    /// Starts an application from the paper form.
    /// </summary>
    /// <param name="borrowerId">Who is applying.</param>
    /// <param name="zoneId">Their zone, whose representatives will decide.</param>
    /// <param name="product">The product ticked on the form.</param>
    /// <param name="requestedPrincipal">The amount applied for.</param>
    /// <param name="receivedOn">The date the office received the form.</param>
    /// <param name="cutoff">The lock period rule in force.</param>
    /// <param name="requestedTermMonths">The repayment period stated on the form, where stated.</param>
    public static LoanApplication Receive(
        BorrowerId borrowerId,
        ZoneId zoneId,
        LoanProduct product,
        Money requestedPrincipal,
        DateOnly receivedOn,
        ApplicationCutoff cutoff,
        int? requestedTermMonths = null)
    {
        ArgumentNullException.ThrowIfNull(cutoff);

        if (!borrowerId.IsSpecified)
        {
            throw new ArgumentException("An application must name its applicant.", nameof(borrowerId));
        }

        if (!requestedPrincipal.IsPositive)
        {
            throw new ArgumentException(
                "An application must be for a positive amount.", nameof(requestedPrincipal));
        }

        return new LoanApplication(
            LoanApplicationId.New(),
            borrowerId,
            zoneId,
            product,
            requestedPrincipal,
            requestedTermMonths,
            receivedOn,
            cutoff.ConsiderationMonth(receivedOn),
            cutoff.Version);
    }

    /// <summary>Rebuilds an application from storage. For the persistence layer only.</summary>
    public static LoanApplication Rehydrate(
        LoanApplicationId id,
        BorrowerId borrowerId,
        ZoneId zoneId,
        LoanProduct product,
        Money requestedPrincipal,
        int? requestedTermMonths,
        DateOnly receivedOn,
        DateOnly considerationMonth,
        int cutoffVersion,
        LoanApplicationStatus status,
        Money? declaredGrossSalary,
        Money? approvedPrincipal,
        LoanTerms? approvedTerms,
        string? rejectionReason,
        IEnumerable<ApprovalDecision> decisions,
        IEnumerable<Guarantee> guarantees,
        IEnumerable<LoanSecurity> security,
        IEnumerable<AttachedDocument> documents)
    {
        var application = new LoanApplication(
            id, borrowerId, zoneId, product, requestedPrincipal, requestedTermMonths,
            receivedOn, considerationMonth, cutoffVersion)
        {
            Status = status,
            DeclaredGrossSalary = declaredGrossSalary,
            ApprovedPrincipal = approvedPrincipal,
            ApprovedTerms = approvedTerms,
            RejectionReason = rejectionReason,
        };

        application._decisions.AddRange(decisions);
        application._guarantees.AddRange(guarantees);
        application._security.AddRange(security);
        application._documents.AddRange(documents);

        return application;
    }

    public void DeclareGrossSalary(Money grossSalary)
    {
        EnsureEditable();

        if (!grossSalary.IsPositive)
        {
            throw new ArgumentException(
                "Gross salary is declared on the form and must be positive.", nameof(grossSalary));
        }

        DeclaredGrossSalary = grossSalary.Round();
    }

    public void OfferSecurity(LoanSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        EnsureEditable();
        _security.Add(security);
    }

    /// <summary>Records the rental income details a rental-income application requires.</summary>
    public void RecordRentalIncome(RentalIncomeSecurity rentalIncome)
    {
        ArgumentNullException.ThrowIfNull(rentalIncome);
        EnsureEditable();

        if (Product != LoanProduct.RentalIncome)
        {
            throw new InvalidOperationException(
                "Rental income details belong on a rental income loan application.");
        }

        RentalIncome = rentalIncome;
        _security.Add(rentalIncome.AsSecurity());
    }

    public void AddGuarantee(Guarantee guarantee)
    {
        ArgumentNullException.ThrowIfNull(guarantee);
        EnsureEditable();

        if (_guarantees.Any(existing => existing.GuarantorId == guarantee.GuarantorId))
        {
            throw new InvalidOperationException(
                $"{guarantee.GuarantorName} has already guaranteed this application.");
        }

        if (guarantee.GuarantorId == BorrowerId)
        {
            throw new InvalidOperationException("A borrower cannot guarantee their own loan.");
        }

        _guarantees.Add(guarantee);
    }

    public void Attach(AttachedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _documents.Add(document);
    }

    /// <summary>
    /// Submits the completed form for the representatives' decisions.
    /// </summary>
    public void Submit()
    {
        if (Status != LoanApplicationStatus.Draft)
        {
            throw new InvalidOperationException(
                $"Only a draft application can be submitted; this one is {Status}.");
        }

        Status = LoanApplicationStatus.Submitted;
    }

    /// <summary>Records one representative's decision.</summary>
    public void RecordDecision(
        Actor approver,
        ApprovalDecisionKind decision,
        DateTimeOffset decidedAtUtc,
        string? comment = null)
    {
        if (Status != LoanApplicationStatus.Submitted)
        {
            throw new InvalidOperationException(
                $"Decisions can only be recorded on a submitted application; this one is {Status}.");
        }

        if (!approver.IsSpecified)
        {
            throw new ArgumentException("A decision records who made it.", nameof(approver));
        }

        if (_decisions.Any(existing => existing.Approver.UserId == approver.UserId))
        {
            throw new InvalidOperationException(
                $"{approver.DisplayName} has already decided on this application.");
        }

        _decisions.Add(new ApprovalDecision(approver, decision, decidedAtUtc, comment?.Trim()));
    }

    /// <summary>
    /// Approves the application on stated terms.
    /// </summary>
    /// <remarks>
    /// The approved principal can differ from what was applied for - reducing the loan is one
    /// of the two sanctioned remedies when the two-thirds rule would be breached.
    /// </remarks>
    public void Approve(LoanTerms terms, DateTimeOffset approvedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(terms);

        if (Status != LoanApplicationStatus.Submitted)
        {
            throw new InvalidOperationException(
                $"Only a submitted application can be approved; this one is {Status}.");
        }

        if (_decisions.Count == 0)
        {
            throw new InvalidOperationException(
                "An application is approved by the zone and office representatives. Record " +
                "their decisions before approving it.");
        }

        if (_decisions.Any(decision => decision.Decision == ApprovalDecisionKind.Reject))
        {
            throw new InvalidOperationException(
                "A representative has rejected this application. Reject it, or have them " +
                "revisit their decision.");
        }

        Status = LoanApplicationStatus.Approved;
        ApprovedTerms = terms;
        ApprovedPrincipal = terms.Principal;

        Raise(new LoanApplicationApproved(Id, BorrowerId, terms, approvedAtUtc));
    }

    public void Reject(string reason, DateTimeOffset rejectedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status is not (LoanApplicationStatus.Submitted or LoanApplicationStatus.Draft))
        {
            throw new InvalidOperationException($"An application that is {Status} cannot be rejected.");
        }

        Status = LoanApplicationStatus.Rejected;
        RejectionReason = reason.Trim();

        Raise(new LoanApplicationRejected(Id, BorrowerId, RejectionReason, rejectedAtUtc));
    }

    public void Withdraw()
    {
        if (Status is not (LoanApplicationStatus.Draft or LoanApplicationStatus.Submitted))
        {
            throw new InvalidOperationException($"An application that is {Status} cannot be withdrawn.");
        }

        Status = LoanApplicationStatus.Withdrawn;
    }

    /// <summary>Marks the application as disbursed, once the cheque has been drawn.</summary>
    internal void MarkDisbursed()
    {
        if (Status != LoanApplicationStatus.Approved)
        {
            throw new InvalidOperationException(
                $"Only an approved application can be disbursed; this one is {Status}.");
        }

        Status = LoanApplicationStatus.Disbursed;
    }

    private void EnsureEditable()
    {
        if (Status != LoanApplicationStatus.Draft)
        {
            throw new InvalidOperationException(
                $"An application that is {Status} can no longer be edited. Applications are " +
                "entered from the signed paper form, and the form does not change after it " +
                "has been submitted.");
        }
    }
}

/// <summary>Raised when an application is approved and joins the awaiting-cheque queue.</summary>
public sealed record LoanApplicationApproved(
    LoanApplicationId ApplicationId,
    BorrowerId BorrowerId,
    LoanTerms Terms,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>Raised when an application is rejected.</summary>
public sealed record LoanApplicationRejected(
    LoanApplicationId ApplicationId,
    BorrowerId BorrowerId,
    string Reason,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
