using Akiba.Application.Abstractions;
using Akiba.Domain.Financial;
using Akiba.Domain.Lending;
using Akiba.Domain.Membership;
using MediatR;

namespace Akiba.Application.Lending;

/// <summary>A loan as the portfolio shows it.</summary>
public sealed record LoanSummary(
    LoanId LoanId,
    string LoanNumber,
    string BorrowerName,
    LoanProduct Product,
    Money Principal,
    Money TotalRepayable,
    Money OutstandingBalance,
    Money MonthlyInstalment,
    DateOnly DisbursedOn,
    DateOnly FirstDueDate,
    LoanStatus Status,
    Money AmountOverdue,
    ArrearsBucket ArrearsBucket,
    int DaysOverdue,
    BorrowerCategory BorrowerCategory)
{
    public bool IsInArrears => AmountOverdue.IsPositive;

    /// <summary>How far the loan has been repaid, for a progress bar.</summary>
    public double ProportionRepaid => TotalRepayable.IsZero
        ? 0d
        : (double)((TotalRepayable - OutstandingBalance).Amount / TotalRepayable.Amount);
}

/// <summary>
/// The loan portfolio as at a date, with each loan's arrears position.
/// </summary>
/// <remarks>
/// Outstanding balances are derived from each loan's receivable account, and arrears come
/// from comparing the schedule with what the ledger says was actually paid. Nothing here is a
/// stored figure.
/// </remarks>
public sealed record GetLoanPortfolioQuery(DateOnly AsAt) : IRequest<IReadOnlyList<LoanSummary>>;

internal sealed class GetLoanPortfolioHandler
    : IRequestHandler<GetLoanPortfolioQuery, IReadOnlyList<LoanSummary>>
{
    private readonly ILoanRepository _loans;
    private readonly IBorrowerRepository _borrowers;
    private readonly IBalanceQueries _balances;

    public GetLoanPortfolioHandler(
        ILoanRepository loans, IBorrowerRepository borrowers, IBalanceQueries balances)
    {
        _loans = loans;
        _borrowers = borrowers;
        _balances = balances;
    }

    public async Task<IReadOnlyList<LoanSummary>> Handle(
        GetLoanPortfolioQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var loans = await _loans.AllRunningAsync(cancellationToken).ConfigureAwait(false);
        var summaries = new List<LoanSummary>(loans.Count);

        foreach (var loan in loans)
        {
            var outstanding = await _balances
                .NaturalBalanceAsAtAsync(loan.ReceivableAccountId, query.AsAt, cancellationToken)
                .ConfigureAwait(false);

            var borrower = await _borrowers
                .FindByIdAsync(loan.BorrowerId, cancellationToken)
                .ConfigureAwait(false);

            var schedule = loan.Schedule;
            var repaid = loan.Terms.TotalRepayable - outstanding;
            var arrears = Arrears.Assess(loan.LoanNumber, schedule, repaid, query.AsAt);

            summaries.Add(new LoanSummary(
                loan.Id,
                loan.LoanNumber,
                borrower?.Name.Full ?? "(unknown borrower)",
                loan.Terms.Product,
                loan.Terms.Principal,
                loan.Terms.TotalRepayable,
                outstanding,
                schedule.Instalments[0].Amount,
                loan.DisbursedOn,
                schedule.FirstDueDate,
                loan.Status,
                arrears.AmountOverdue,
                arrears.Bucket,
                arrears.DaysOverdue,
                BorrowerCategories.Of(borrower)));
        }

        return [.. summaries.OrderByDescending(loan => loan.AmountOverdue.Amount)
            .ThenBy(loan => loan.LoanNumber, StringComparer.Ordinal)];
    }
}

/// <summary>
/// The arrears report: what is overdue, aged.
/// </summary>
/// <param name="AsAt">The date.</param>
/// <param name="Loans">Every loan behind its schedule.</param>
/// <param name="Totals">The total overdue in each ageing band.</param>
public sealed record ArrearsReport(
    DateOnly AsAt,
    IReadOnlyList<LoanSummary> Loans,
    IReadOnlyDictionary<ArrearsBucket, Money> Totals)
{
    public Money TotalOverdue => Loans.Sum(loan => loan.AmountOverdue, Currency.Kes);
}

/// <summary>
/// Builds the arrears report.
/// </summary>
/// <remarks>
/// <b>No penalty is calculated, because Akiba has no penalty rule.</b> The questionnaire asked
/// what counts as a missed payment and whether there is a penalty, and the question came back
/// unanswered. See docs/open-questions.md, item 12.
/// </remarks>
public sealed record GetArrearsReportQuery(DateOnly AsAt) : IRequest<ArrearsReport>;

internal sealed class GetArrearsReportHandler : IRequestHandler<GetArrearsReportQuery, ArrearsReport>
{
    private readonly IMediator _mediator;

    public GetArrearsReportHandler(IMediator mediator) => _mediator = mediator;

    public async Task<ArrearsReport> Handle(
        GetArrearsReportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var portfolio = await _mediator
            .Send(new GetLoanPortfolioQuery(query.AsAt), cancellationToken)
            .ConfigureAwait(false);

        var behind = portfolio.Where(loan => loan.IsInArrears).ToList();

        var totals = behind
            .GroupBy(loan => loan.ArrearsBucket)
            .ToDictionary(
                group => group.Key,
                group => group.Select(loan => loan.AmountOverdue).Sum(Currency.Kes));

        foreach (var bucket in Enum.GetValues<ArrearsBucket>())
        {
            totals.TryAdd(bucket, Money.ZeroKes);
        }

        return new ArrearsReport(query.AsAt, behind, totals);
    }
}

/// <summary>An application as the applications list shows it.</summary>
public sealed record ApplicationSummary(
    LoanApplicationId ApplicationId,
    BorrowerId BorrowerId,
    string BorrowerName,
    LoanProduct Product,
    Money RequestedPrincipal,
    Money? ApprovedPrincipal,
    DateOnly ReceivedOn,
    DateOnly ConsiderationMonth,
    bool MissedTheCutoff,
    LoanApplicationStatus Status,
    int DecisionsRecorded,
    int Guarantors,
    Money TotalGuaranteed,
    BorrowerCategory BorrowerCategory,
    PreparedVoucherSummary? PreparedVoucher = null);

/// <summary>
/// A payment voucher prepared for an approved application, and the cheque reserved for it.
/// </summary>
/// <remarks>
/// On the summary so the screen can pick up where the clerk left off. Preparing a voucher and
/// writing the cheque are hours apart in practice - the cheque goes to the chairman for a
/// signature - and without this a reopened panel had no idea a voucher existed, offered to
/// prepare another, was refused, and left the loan unpayable and the cheque unvoidable.
/// </remarks>
/// <param name="VoucherReference">The voucher's own reference.</param>
/// <param name="ChequeNumber">The cheque leaf reserved for it.</param>
/// <param name="LoanNumber">The loan number the voucher was prepared under.</param>
/// <param name="PreparedOn">The voucher date, which is also the date on the cheque.</param>
public sealed record PreparedVoucherSummary(
    string VoucherReference, string ChequeNumber, string LoanNumber, DateOnly PreparedOn);

/// <summary>
/// Applications by status.
/// </summary>
/// <remarks>
/// The <see cref="LoanApplicationStatus.Approved"/> list is the queue the office watches: a
/// form that arrived after the 15th waits there for the next cycle.
/// </remarks>
public sealed record ListApplicationsQuery(LoanApplicationStatus? Status)
    : IRequest<IReadOnlyList<ApplicationSummary>>;

internal sealed class ListApplicationsHandler
    : IRequestHandler<ListApplicationsQuery, IReadOnlyList<ApplicationSummary>>
{
    private readonly ILoanApplicationRepository _applications;
    private readonly IBorrowerRepository _borrowers;

    public ListApplicationsHandler(
        ILoanApplicationRepository applications, IBorrowerRepository borrowers)
    {
        _applications = applications;
        _borrowers = borrowers;
    }

    public async Task<IReadOnlyList<ApplicationSummary>> Handle(
        ListApplicationsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var applications = new List<LoanApplication>();

        if (query.Status is { } status)
        {
            applications.AddRange(
                await _applications.ByStatusAsync(status, cancellationToken).ConfigureAwait(false));
        }
        else
        {
            foreach (var each in Enum.GetValues<LoanApplicationStatus>())
            {
                applications.AddRange(
                    await _applications.ByStatusAsync(each, cancellationToken).ConfigureAwait(false));
            }
        }

        var summaries = new List<ApplicationSummary>(applications.Count);

        foreach (var application in applications)
        {
            var borrower = await _borrowers
                .FindByIdAsync(application.BorrowerId, cancellationToken)
                .ConfigureAwait(false);

            summaries.Add(new ApplicationSummary(
                application.Id,
                application.BorrowerId,
                borrower?.Name.Full ?? "(unknown applicant)",
                application.Product,
                application.RequestedPrincipal,
                application.ApprovedPrincipal,
                application.ReceivedOn,
                application.ConsiderationMonth,
                application.MissedTheCutoff,
                application.Status,
                application.Decisions.Count,
                application.Guarantees.Count,
                application.Guarantees.Sum(g => g.GuaranteedAmount, Currency.Kes),
                BorrowerCategories.Of(borrower),
                application is
                {
                    PaymentVoucherReference: { } voucher,
                    ReservedChequeNumber: { } cheque,
                    PreparedLoanNumber: { } loanNumber,
                    VoucherPreparedOn: { } preparedOn,
                }
                    ? new PreparedVoucherSummary(voucher, cheque, loanNumber, preparedOn)
                    : null));
        }

        return [.. summaries.OrderByDescending(application => application.ReceivedOn)];
    }
}

/// <summary>
/// What the officials see at a glance.
/// </summary>
/// <param name="AsAt">The date everything is stated as at.</param>
/// <param name="BankBalance">What the ledger says is in the bank.</param>
/// <param name="TotalShareholding">What Akiba owes its members.</param>
/// <param name="TotalLoansOutstanding">What members owe Akiba.</param>
/// <param name="UnallocatedReceipts">Money in that nobody has said what it is for yet.</param>
/// <param name="InterestEarned">Income from loans, net of nothing - bank charges are separate.</param>
/// <param name="BankCharges">Deducted when working out what the dividend is calculated on.</param>
/// <param name="ActiveMembers">Members still with CAL.</param>
/// <param name="RunningLoans">Loans being repaid.</param>
/// <param name="LoansInArrears">Loans behind their schedule.</param>
/// <param name="TotalOverdue">How much is behind.</param>
/// <param name="ApplicationsAwaitingDecision">Submitted, waiting on representatives.</param>
/// <param name="ApplicationsAwaitingCheque">Approved, waiting on a cheque.</param>
/// <param name="ReceiptsAwaitingClearance">Cheques that have not matured.</param>
/// <param name="ReceiptsAwaitingAllocation">Cleared money the clerk has not applied yet.</param>
/// <param name="TrialBalanceDifference">Zero, or something wrote to the database directly.</param>
public sealed record DashboardSummary(
    DateOnly AsAt,
    Money BankBalance,
    Money TotalShareholding,
    Money TotalLoansOutstanding,
    Money UnallocatedReceipts,
    Money InterestEarned,
    Money BankCharges,
    int ActiveMembers,
    int RunningLoans,
    int LoansInArrears,
    Money TotalOverdue,
    int ApplicationsAwaitingDecision,
    int ApplicationsAwaitingCheque,
    int ReceiptsAwaitingClearance,
    int ReceiptsAwaitingAllocation,
    Money TrialBalanceDifference)
{
    public bool BooksBalance => TrialBalanceDifference.IsZero;
}

/// <summary>Builds the dashboard as at a date.</summary>
public sealed record GetDashboardQuery(DateOnly AsAt) : IRequest<DashboardSummary>;

internal sealed class GetDashboardHandler : IRequestHandler<GetDashboardQuery, DashboardSummary>
{
    private readonly IMediator _mediator;
    private readonly IBorrowerRepository _borrowers;
    private readonly ILoanApplicationRepository _applications;
    private readonly IReceiptRepository _receipts;

    public GetDashboardHandler(
        IMediator mediator,
        IBorrowerRepository borrowers,
        ILoanApplicationRepository applications,
        IReceiptRepository receipts)
    {
        _mediator = mediator;
        _borrowers = borrowers;
        _applications = applications;
        _receipts = receipts;
    }

    public async Task<DashboardSummary> Handle(
        GetDashboardQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var trial = await _mediator
            .Send(new Ledger.GetTrialBalanceQuery(query.AsAt), cancellationToken)
            .ConfigureAwait(false);

        var arrears = await _mediator
            .Send(new GetArrearsReportQuery(query.AsAt), cancellationToken)
            .ConfigureAwait(false);

        var portfolio = await _mediator
            .Send(new GetLoanPortfolioQuery(query.AsAt), cancellationToken)
            .ConfigureAwait(false);

        var members = await _borrowers.AllMembersAsync(false, cancellationToken).ConfigureAwait(false);

        var submitted = await _applications
            .ByStatusAsync(LoanApplicationStatus.Submitted, cancellationToken)
            .ConfigureAwait(false);

        var approved = await _applications
            .AwaitingDisbursementAsync(cancellationToken)
            .ConfigureAwait(false);

        var awaitingClearance = await _receipts
            .AwaitingClearanceAsync(cancellationToken)
            .ConfigureAwait(false);

        var awaitingAllocation = await _receipts
            .AwaitingAllocationAsync(cancellationToken)
            .ConfigureAwait(false);

        return new DashboardSummary(
            query.AsAt,
            BalanceOf(trial, Domain.Ledger.ChartOfAccounts.Bank),
            SumOfPrefix(trial, Domain.Ledger.ChartOfAccounts.MemberSharesPrefix),
            SumOfPrefix(trial, Domain.Ledger.ChartOfAccounts.LoansReceivablePrefix),
            BalanceOf(trial, Domain.Ledger.ChartOfAccounts.UnallocatedReceipts),
            BalanceOf(trial, Domain.Ledger.ChartOfAccounts.LoanInterestIncome),
            BalanceOf(trial, Domain.Ledger.ChartOfAccounts.BankCharges),
            members.Count,
            portfolio.Count,
            arrears.Loans.Count,
            arrears.TotalOverdue,
            submitted.Count,
            approved.Count,
            awaitingClearance.Count,
            awaitingAllocation.Count,
            trial.Difference);
    }

    private static Money BalanceOf(Ledger.TrialBalance trial, string code) =>
        trial.Accounts.FirstOrDefault(account => account.Code == code)?.Balance ?? Money.ZeroKes;

    /// <summary>
    /// Totals the per-member or per-loan accounts, which share a code prefix.
    /// </summary>
    private static Money SumOfPrefix(Ledger.TrialBalance trial, string prefix) =>
        trial.Accounts
            .Where(account => account.Code.StartsWith(prefix, StringComparison.Ordinal))
            .Sum(account => account.Balance, Currency.Kes);
}

/// <summary>A guarantor as listed on a loan application.</summary>
public sealed record GuarantorView(
    BorrowerId GuarantorId,
    string GuarantorName,
    string PayrollNumber,
    Money GuaranteedAmount,
    Money ShareValueAtSigning,
    DateOnly SignedOn);

/// <summary>Returns every guarantor on a loan application.</summary>
public sealed record ListApplicationGuaranteesQuery(LoanApplicationId ApplicationId)
    : IRequest<IReadOnlyList<GuarantorView>>;

internal sealed class ListApplicationGuaranteesHandler
    : IRequestHandler<ListApplicationGuaranteesQuery, IReadOnlyList<GuarantorView>>
{
    private readonly ILoanApplicationRepository _applications;

    public ListApplicationGuaranteesHandler(ILoanApplicationRepository applications)
        => _applications = applications;

    public async Task<IReadOnlyList<GuarantorView>> Handle(
        ListApplicationGuaranteesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var application = await _applications.FindByIdAsync(query.ApplicationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No application with id {query.ApplicationId}.");

        return [.. application.Guarantees
            .Where(g => !g.IsReleased)
            .Select(g => new GuarantorView(
                g.GuarantorId,
                g.GuarantorName,
                g.GuarantorPayrollNumber.Value,
                g.GuaranteedAmount,
                g.ShareValueAtSigning,
                g.SignedOn))];
    }
}

/// <summary>One row of a loan's expected repayment schedule, as displayed to the clerk.</summary>
public sealed record ScheduledInstalmentView(
    int Number,
    DateOnly DueDate,
    Money Amount,
    Money PrincipalPortion,
    Money InterestPortion);

/// <summary>A single loan's repayment schedule and current position.</summary>
public sealed record LoanScheduleView(
    string LoanNumber,
    string BorrowerName,
    LoanProduct Product,
    Money Principal,
    Money Interest,
    Money TotalRepayable,
    Money OutstandingBalance,
    DateOnly DisbursedOn,
    DateOnly FinalDueDate,
    LoanStatus Status,
    IReadOnlyList<ScheduledInstalmentView> Instalments)
{
    public Money Repaid => TotalRepayable - OutstandingBalance;
}

/// <summary>Returns the full repayment schedule for one loan.</summary>
public sealed record GetLoanScheduleQuery(LoanId LoanId, DateOnly AsAt) : IRequest<LoanScheduleView?>;

internal sealed class GetLoanScheduleHandler : IRequestHandler<GetLoanScheduleQuery, LoanScheduleView?>
{
    private readonly ILoanRepository _loans;
    private readonly IBorrowerRepository _borrowers;
    private readonly IBalanceQueries _balances;

    public GetLoanScheduleHandler(
        ILoanRepository loans, IBorrowerRepository borrowers, IBalanceQueries balances)
    {
        _loans = loans;
        _borrowers = borrowers;
        _balances = balances;
    }

    public async Task<LoanScheduleView?> Handle(
        GetLoanScheduleQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var loan = await _loans.FindByIdAsync(query.LoanId, cancellationToken).ConfigureAwait(false);
        if (loan is null)
        {
            return null;
        }

        var borrower = await _borrowers
            .FindByIdAsync(loan.BorrowerId, cancellationToken)
            .ConfigureAwait(false);

        var outstanding = await _balances
            .NaturalBalanceAsAtAsync(loan.ReceivableAccountId, query.AsAt, cancellationToken)
            .ConfigureAwait(false);

        var schedule = loan.Schedule;

        return new LoanScheduleView(
            loan.LoanNumber,
            borrower?.Name.Full ?? "(unknown borrower)",
            loan.Terms.Product,
            loan.Terms.Principal,
            loan.Terms.Interest,
            loan.Terms.TotalRepayable,
            outstanding,
            loan.DisbursedOn,
            schedule.FinalDueDate,
            loan.Status,
            [.. schedule.Instalments.Select(i => new ScheduledInstalmentView(
                i.Number,
                i.DueDate,
                i.Amount,
                i.PrincipalPortion,
                i.InterestPortion))]);
    }
}
