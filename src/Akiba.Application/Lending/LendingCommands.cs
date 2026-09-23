using Akiba.Application.Abstractions;
using Akiba.Application.Posting;
using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Guaranteeing;
using Akiba.Domain.Ledger;
using Akiba.Domain.Lending;
using Akiba.Domain.Membership;
using FluentValidation;
using MediatR;

namespace Akiba.Application.Lending;

/// <summary>Records a loan application from the signed paper form.</summary>
/// <param name="BorrowerId">Who applied.</param>
/// <param name="Product">The product ticked on the form.</param>
/// <param name="RequestedPrincipal">The amount applied for.</param>
/// <param name="ReceivedOn">The date the office received the form.</param>
/// <param name="DeclaredGrossSalary">Gross salary, as declared on the form.</param>
/// <param name="RequestedTermMonths">The repayment period stated on the form, where stated.</param>
public sealed record ReceiveLoanApplicationCommand(
    BorrowerId BorrowerId,
    LoanProduct Product,
    Money RequestedPrincipal,
    DateOnly ReceivedOn,
    Money? DeclaredGrossSalary,
    int? RequestedTermMonths) : IRequest<LoanApplicationId>;

public sealed class ReceiveLoanApplicationValidator : AbstractValidator<ReceiveLoanApplicationCommand>
{
    public ReceiveLoanApplicationValidator()
    {
        RuleFor(command => command.RequestedPrincipal.Amount).GreaterThan(0m);
        RuleFor(command => command.BorrowerId).Must(id => id.IsSpecified);
    }
}

internal sealed class ReceiveLoanApplicationHandler
    : IRequestHandler<ReceiveLoanApplicationCommand, LoanApplicationId>
{
    private readonly IBorrowerRepository _borrowers;
    private readonly ILoanRepository _loans;
    private readonly ILoanApplicationRepository _applications;
    private readonly IUnitOfWork _unitOfWork;

    public ReceiveLoanApplicationHandler(
        IBorrowerRepository borrowers,
        ILoanRepository loans,
        ILoanApplicationRepository applications,
        IUnitOfWork unitOfWork)
    {
        _borrowers = borrowers;
        _loans = loans;
        _applications = applications;
        _unitOfWork = unitOfWork;
    }

    public async Task<LoanApplicationId> Handle(
        ReceiveLoanApplicationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var borrower = await _borrowers.FindByIdAsync(command.BorrowerId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No borrower with id {command.BorrowerId}.");

        if (command.Product.RequiresMembership() && borrower is not Member)
        {
            throw new InvalidOperationException(
                $"A {command.Product.DisplayName().ToLowerInvariant()} is for members only. " +
                "A non-member client borrows on the client product.");
        }

        var running = await _loans.RunningForBorrowerAsync(command.BorrowerId, cancellationToken)
            .ConfigureAwait(false);

        if (running.Count >= LoanProductCatalogue.MaximumConcurrentLoansPerMember)
        {
            throw new InvalidOperationException(
                $"{borrower.Name} already holds {running.Count} running loans " +
                $"({string.Join(", ", running.Select(loan => loan.LoanNumber))}). A member may " +
                $"hold {LoanProductCatalogue.MaximumConcurrentLoansPerMember} and no more.");
        }

        var zoneId = borrower is Member member ? member.ZoneId : default;

        var application = LoanApplication.Receive(
            command.BorrowerId,
            zoneId,
            command.Product,
            command.RequestedPrincipal,
            command.ReceivedOn,
            ApplicationCutoff.Version1,
            command.RequestedTermMonths);

        if (command.DeclaredGrossSalary is { } salary)
        {
            application.DeclareGrossSalary(salary);
        }

        _applications.Add(application);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return application.Id;
    }
}

/// <summary>Records one zone or office representative's decision on an application.</summary>
public sealed record RecordApprovalDecisionCommand(
    LoanApplicationId ApplicationId,
    ApprovalDecisionKind Decision,
    string? Comment) : IRequest;

internal sealed class RecordApprovalDecisionHandler : IRequestHandler<RecordApprovalDecisionCommand>
{
    private readonly ILoanApplicationRepository _applications;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public RecordApprovalDecisionHandler(
        ILoanApplicationRepository applications,
        ICurrentUser currentUser,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _applications = applications;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(RecordApprovalDecisionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var application = await _applications.FindByIdAsync(command.ApplicationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No application with id {command.ApplicationId}.");

        application.RecordDecision(_currentUser.Actor, command.Decision, _clock.UtcNow, command.Comment);

        _applications.Update(application);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// What the office needs to see before approving: the figures, and every rule that bites.
/// </summary>
/// <param name="Terms">What the loan would cost and how long it would run.</param>
/// <param name="Shareholding">The applicant's shares.</param>
/// <param name="BorrowingLimit">Twice their shareholding.</param>
/// <param name="IsWithinBorrowingLimit">Whether the loan is inside it.</param>
/// <param name="Affordability">The two-thirds check.</param>
/// <param name="GuarantorRequirement">Whether guarantors are needed, and whether that is a settled rule.</param>
/// <param name="GuarantorCoverage">The form's "do guarantors sufficiently cover the loan?" question.</param>
public sealed record LoanAssessment(
    LoanTerms Terms,
    Money Shareholding,
    Money BorrowingLimit,
    bool IsWithinBorrowingLimit,
    AffordabilityAssessment? Affordability,
    GuarantorRequirement GuarantorRequirement,
    GuarantorCoverage GuarantorCoverage)
{
    /// <summary>Everything that would stop this application being approved.</summary>
    public IReadOnlyList<string> Blockers
    {
        get
        {
            var blockers = new List<string>();

            if (!IsWithinBorrowingLimit)
            {
                blockers.Add(
                    $"{Terms.Principal} exceeds the borrowing limit of {BorrowingLimit} " +
                    $"(twice the shareholding of {Shareholding}).");
            }

            if (Affordability is { IsWithinLimit: false } affordability)
            {
                blockers.Add(
                    $"Total deductions would be {affordability.TotalDeductions}, which is " +
                    $"{affordability.ExcessOverLimit} above the two-thirds limit of " +
                    $"{affordability.MaximumPermittedDeductions}. Either reduce the loan - the " +
                    $"largest affordable instalment is {affordability.LargestAffordableInstalment} - " +
                    "or reduce the monthly share deduction.");
            }

            if (GuarantorRequirement.AreGuarantorsRequired && !GuarantorCoverage.IsSufficient)
            {
                blockers.Add(
                    $"Guarantors cover {GuarantorCoverage.TotalGuaranteed} of " +
                    $"{GuarantorCoverage.AmountToCover}, leaving {GuarantorCoverage.Shortfall} " +
                    "uncovered.");
            }

            return blockers;
        }
    }

    public bool CanBeApproved => Blockers.Count == 0;
}

/// <summary>
/// Works out the figures and the rules for an application, without changing anything.
/// </summary>
/// <remarks>
/// Separate from approving so the office can see the position before deciding - and so the
/// panel can show the same numbers the approval will use.
/// </remarks>
public sealed record AssessLoanApplicationQuery(LoanApplicationId ApplicationId, DateOnly AsAt)
    : IRequest<LoanAssessment>;

internal sealed class AssessLoanApplicationHandler
    : IRequestHandler<AssessLoanApplicationQuery, LoanAssessment>
{
    private readonly ILoanApplicationRepository _applications;
    private readonly IBorrowerRepository _borrowers;
    private readonly ILoanRepository _loans;
    private readonly IJournalRepository _journal;
    private readonly IBalanceQueries _balances;

    public AssessLoanApplicationHandler(
        ILoanApplicationRepository applications,
        IBorrowerRepository borrowers,
        ILoanRepository loans,
        IJournalRepository journal,
        IBalanceQueries balances)
    {
        _applications = applications;
        _borrowers = borrowers;
        _loans = loans;
        _journal = journal;
        _balances = balances;
    }

    public async Task<LoanAssessment> Handle(
        AssessLoanApplicationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var application = await _applications.FindByIdAsync(query.ApplicationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No application with id {query.ApplicationId}.");

        var member = await _borrowers.FindMemberAsync(application.BorrowerId, cancellationToken)
            .ConfigureAwait(false);

        var shareholding = member is null
            ? Money.ZeroKes
            : await _balances
                .NaturalBalanceAsAtAsync(member.SharesAccountId, query.AsAt, cancellationToken)
                .ConfigureAwait(false);

        var membershipYears = member is null
            ? null
            : Shareholding.MembershipYearsAsAt(
                await _journal
                    .ForAccountAsOfAsync(member.SharesAccountId, query.AsAt, cancellationToken)
                    .ConfigureAwait(false),
                member.SharesAccountId,
                query.AsAt);

        var terms = new LoanPricing().Price(
            application.Product,
            application.RequestedPrincipal,
            membershipYears,
            application.RequestedTermMonths);

        var running = await _loans
            .RunningForBorrowerAsync(application.BorrowerId, cancellationToken)
            .ConfigureAwait(false);

        var existingInstalments = Money.ZeroKes;
        var normalLoanBalance = Money.ZeroKes;

        foreach (var loan in running)
        {
            existingInstalments += loan.Schedule.Instalments[0].Amount;

            if (loan.Terms.Product == LoanProduct.Normal)
            {
                normalLoanBalance += await _balances
                    .NaturalBalanceAsAtAsync(loan.ReceivableAccountId, query.AsAt, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var schedule = RepaymentSchedule.Generate(terms, query.AsAt);
        var proposedInstalment = schedule.Instalments[0].Amount;

        var affordability = application.DeclaredGrossSalary is { } salary
            ? Affordability.Assess(salary, existingInstalments, proposedInstalment)
            : null;

        var requirement = GuarantorRequirementPolicy.For(
            application.Product, terms.TotalRepayable, shareholding, normalLoanBalance);

        var coverage = GuarantorCoverage.Of(application.Guarantees, terms.TotalRepayable);

        return new LoanAssessment(
            terms,
            shareholding,
            Shareholding.BorrowingLimit(shareholding),
            Shareholding.IsWithinBorrowingLimit(shareholding, application.RequestedPrincipal),
            affordability,
            requirement,
            coverage);
    }
}

/// <summary>
/// Approves an application on stated terms.
/// </summary>
/// <remarks>
/// The approved principal can differ from what was applied for: reducing the loan is one of
/// the two sanctioned remedies when the two-thirds rule would be breached.
/// </remarks>
/// <param name="ApplicationId">The application.</param>
/// <param name="ApprovedPrincipal">
/// The amount approved. Null approves what was applied for.
/// </param>
public sealed record ApproveLoanApplicationCommand(
    LoanApplicationId ApplicationId, Money? ApprovedPrincipal) : IRequest<LoanTerms>;

internal sealed class ApproveLoanApplicationHandler
    : IRequestHandler<ApproveLoanApplicationCommand, LoanTerms>
{
    private readonly ILoanApplicationRepository _applications;
    private readonly IMediator _mediator;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public ApproveLoanApplicationHandler(
        ILoanApplicationRepository applications,
        IMediator mediator,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _applications = applications;
        _mediator = mediator;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task<LoanTerms> Handle(
        ApproveLoanApplicationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var application = await _applications.FindByIdAsync(command.ApplicationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No application with id {command.ApplicationId}.");

        var today = _clock.TodayInNairobi;

        var assessment = await _mediator
            .Send(new AssessLoanApplicationQuery(command.ApplicationId, today), cancellationToken)
            .ConfigureAwait(false);

        if (!assessment.CanBeApproved)
        {
            throw new InvalidOperationException(
                "This application cannot be approved as it stands:" + Environment.NewLine +
                string.Join(Environment.NewLine, assessment.Blockers.Select(blocker => "  - " + blocker)));
        }

        // Approving a reduced amount re-prices it, because the term band may change with the
        // principal - a 60,000 loan runs twelve months and a 45,000 one runs eight.
        var terms = command.ApprovedPrincipal is { } reduced && reduced != application.RequestedPrincipal
            ? new LoanPricing().Price(application.Product, reduced, null, application.RequestedTermMonths)
            : assessment.Terms;

        application.Approve(terms, _clock.UtcNow);

        _applications.Update(application);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return terms;
    }
}

/// <summary>
/// Draws the cheque and brings the loan into being.
/// </summary>
/// <remarks>
/// Opens the loan's receivable account, creates the loan, marks the application disbursed and
/// posts the disbursement entry - all in one transaction. Half of that reaching the database
/// would leave a loan with no ledger position, or a ledger position with no loan.
/// </remarks>
/// <param name="ApplicationId">The approved application.</param>
/// <param name="LoanNumber">The number written in the LOAN NO. box on the form.</param>
/// <param name="DisbursedOn">The date the cheque was drawn.</param>
/// <param name="ChequeNumber">The cheque number.</param>
/// <param name="VoucherReference">The payment voucher filed with it.</param>
/// <param name="Signatories">The two signatories.</param>
public sealed record DisburseLoanCommand(
    LoanApplicationId ApplicationId,
    string LoanNumber,
    DateOnly DisbursedOn,
    string ChequeNumber,
    string VoucherReference,
    IReadOnlyList<string> Signatories) : IRequest<LoanId>;

public sealed class DisburseLoanValidator : AbstractValidator<DisburseLoanCommand>
{
    public DisburseLoanValidator()
    {
        RuleFor(command => command.LoanNumber).NotEmpty();
        RuleFor(command => command.ChequeNumber).NotEmpty();
        RuleFor(command => command.VoucherReference).NotEmpty();

        RuleFor(command => command.Signatories)
            .Must(signatories => signatories is { Count: >= 2 })
            .WithMessage("Every Akiba cheque carries two signatories.");
    }
}

internal sealed class DisburseLoanHandler : IRequestHandler<DisburseLoanCommand, LoanId>
{
    private readonly ILoanApplicationRepository _applications;
    private readonly ILoanRepository _loans;
    private readonly IJournalRepository _journal;
    private readonly IAkibaAccounts _accounts;
    private readonly IBorrowerRepository _borrowers;
    private readonly IMediator _mediator;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public DisburseLoanHandler(
        ILoanApplicationRepository applications,
        ILoanRepository loans,
        IJournalRepository journal,
        IAkibaAccounts accounts,
        IBorrowerRepository borrowers,
        IMediator mediator,
        ICurrentUser currentUser,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _applications = applications;
        _loans = loans;
        _journal = journal;
        _accounts = accounts;
        _borrowers = borrowers;
        _mediator = mediator;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public async Task<LoanId> Handle(DisburseLoanCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var application = await _applications.FindByIdAsync(command.ApplicationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No application with id {command.ApplicationId}.");

        var existing = await _loans.FindByNumberAsync(command.LoanNumber, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"Loan number {command.LoanNumber} is already used by a loan disbursed on " +
                $"{existing.DisbursedOn:yyyy-MM-dd}. Officials quote these, so they cannot repeat.");
        }

        var terms = application.ApprovedTerms
            ?? throw new InvalidOperationException(
                "An application must be approved on terms before it can be disbursed.");

        var loanId = Guid.NewGuid();

        var receivable = await _accounts
            .OpenLoanReceivableAccountAsync(command.LoanNumber, loanId, cancellationToken)
            .ConfigureAwait(false);

        var cheque = new ChequeDetails(
            command.ChequeNumber,
            command.VoucherReference,
            terms.Principal,
            command.DisbursedOn,
            command.Signatories);

        var loan = Loan.Disburse(
            application, command.LoanNumber, receivable.Id, command.DisbursedOn, cheque, _clock.UtcNow);

        _loans.Add(loan);
        _applications.Update(application);

        var entry = AkibaPostings.Disbursement(
            receivable.Id,
            await _accounts.BankAsync(cancellationToken).ConfigureAwait(false),
            await _accounts.LoanInterestIncomeAsync(cancellationToken).ConfigureAwait(false),
            terms.Principal,
            terms.Interest,
            command.DisbursedOn,
            command.LoanNumber,
            SourceDocument.PaymentVoucher(command.VoucherReference),
            _currentUser.Actor,
            _clock.UtcNow);

        await _journal.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Queued, not sent. If the mail server is down the message waits in the outbox; a loan
        // is never held up by a notification, and a notification is never lost because a loan
        // went through.
        await TellTheBorrowerAsync(loan, terms, cancellationToken).ConfigureAwait(false);

        return loan.Id;
    }

    private async Task TellTheBorrowerAsync(
        Loan loan, LoanTerms terms, CancellationToken cancellationToken)
    {
        var borrower = await _borrowers.FindByIdAsync(loan.BorrowerId, cancellationToken)
            .ConfigureAwait(false);

        if (borrower is null)
        {
            return;
        }

        var firstDue = loan.Schedule.Instalments.Count > 0
            ? loan.Schedule.Instalments[0].DueDate
            : loan.DisbursedOn.AddMonths(1);

        var instalment = loan.Schedule.Instalments.Count > 0
            ? loan.Schedule.Instalments[0].Amount
            : Domain.Financial.Money.ZeroKes;

        await _mediator.Send(
            new Notifications.QueueNotificationCommand(
                Domain.Notifications.NotificationKind.LoanDisbursed,
                loan.BorrowerId,
                Notifications.NotificationComposer.LoanDisbursed(
                    borrower.Name.Full, loan.LoanNumber, terms.Principal, instalment, firstDue),
                [
                    Domain.Notifications.NotificationChannel.Email,
                    Domain.Notifications.NotificationChannel.Sms,
                ]),
            cancellationToken)
            .ConfigureAwait(false);
    }
}
