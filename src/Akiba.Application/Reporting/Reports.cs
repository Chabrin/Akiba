using Akiba.Application.Abstractions;
using Akiba.Domain.Dividends;
using Akiba.Domain.Financial;
using Akiba.Domain.Guaranteeing;
using Akiba.Domain.Ledger;
using Akiba.Domain.Membership;
using MediatR;

namespace Akiba.Application.Reporting;

/// <summary>A generated report, ready to be written to disk or handed to a browser.</summary>
/// <param name="FileName">What it should be saved as.</param>
/// <param name="ContentType">Its media type.</param>
/// <param name="Content">The bytes.</param>
public sealed record GeneratedReport(string FileName, string ContentType, byte[] Content);

/// <summary>Writes a deduction schedule as the Excel worksheet HR asked for.</summary>
public interface IDeductionScheduleWriter
{
    GeneratedReport Write(DeductionSchedule schedule);
}

/// <summary>Writes a member's statement as a PDF they can be sent.</summary>
public interface IMemberStatementWriter
{
    GeneratedReport Write(Members.MemberStatement statement);
}

/// <summary>Writes the shareholding summary as at a date.</summary>
public interface IShareholdingSummaryWriter
{
    GeneratedReport Write(ShareholdingSummary summary);
}

/// <summary>Writes the AGM pack.</summary>
public interface IAgmPackWriter
{
    GeneratedReport Write(AgmPack pack);
}

/// <summary>
/// Writes the dividend computation schedule.
/// </summary>
/// <remarks>
/// Excel rather than PDF: the committee checks it against the register, sorts it and sums the
/// column. It is the working document a dividend is argued over before it is approved.
/// </remarks>
public interface IDividendScheduleWriter
{
    GeneratedReport Write(DividendRun run);
}

/// <summary>One member on the shareholding summary.</summary>
public sealed record ShareholdingLine(
    string MembershipNumber,
    string PayrollNumber,
    string FullName,
    Money Shareholding,
    Money BorrowingLimit,
    Money LoansOutstanding,
    bool IsActive)
{
    /// <summary>
    /// What the member would still hold if every loan they have were settled from their shares.
    /// </summary>
    /// <remarks>
    /// Not a rule Akiba applies - shares cannot be withdrawn while a member is active, and a
    /// loan is not automatically set against them. It is the figure an official wants when
    /// asking whether a member's own position covers what they owe.
    /// </remarks>
    public Money NetPosition => Shareholding - LoansOutstanding;
}

/// <summary>
/// Every member's shareholding as at a date. The monthly report the office asked for.
/// </summary>
public sealed record ShareholdingSummary(
    DateOnly AsAt,
    IReadOnlyList<ShareholdingLine> Lines)
{
    public Money TotalShareholding => Lines.Sum(line => line.Shareholding, Currency.Kes);

    public Money TotalLoansOutstanding => Lines.Sum(line => line.LoansOutstanding, Currency.Kes);

    public int MemberCount => Lines.Count;
}

/// <summary>Builds the shareholding summary as at any date.</summary>
public sealed record GetShareholdingSummaryQuery(DateOnly AsAt, bool IncludeExited = false)
    : IRequest<ShareholdingSummary>;

internal sealed class GetShareholdingSummaryHandler
    : IRequestHandler<GetShareholdingSummaryQuery, ShareholdingSummary>
{
    private readonly IBorrowerRepository _borrowers;
    private readonly ILoanRepository _loans;
    private readonly IBalanceQueries _balances;

    public GetShareholdingSummaryHandler(
        IBorrowerRepository borrowers, ILoanRepository loans, IBalanceQueries balances)
    {
        _borrowers = borrowers;
        _loans = loans;
        _balances = balances;
    }

    public async Task<ShareholdingSummary> Handle(
        GetShareholdingSummaryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var members = await _borrowers
            .AllMembersAsync(query.IncludeExited, cancellationToken)
            .ConfigureAwait(false);

        var lines = new List<ShareholdingLine>(members.Count);

        foreach (var member in members)
        {
            var shareholding = await _balances
                .NaturalBalanceAsAtAsync(member.SharesAccountId, query.AsAt, cancellationToken)
                .ConfigureAwait(false);

            // The loans that were running as at the date, not the ones running today.
            var loans = await _loans.AllForBorrowerAsync(member.Id, cancellationToken)
                .ConfigureAwait(false);

            var outstanding = Money.ZeroKes;

            foreach (var loan in loans.Where(loan =>
                loan.DisbursedOn <= query.AsAt && (loan.SettledOn is null || loan.SettledOn > query.AsAt)))
            {
                outstanding += await _balances
                    .NaturalBalanceAsAtAsync(loan.ReceivableAccountId, query.AsAt, cancellationToken)
                    .ConfigureAwait(false);
            }

            lines.Add(new ShareholdingLine(
                member.MembershipNumber.Value,
                member.PayrollNumber.IsSpecified ? member.PayrollNumber.Value : string.Empty,
                member.Name.Full,
                shareholding,
                Shareholding.BorrowingLimit(shareholding),
                outstanding,
                member.IsActive));
        }

        return new ShareholdingSummary(
            query.AsAt,
            [.. lines.OrderByDescending(line => line.Shareholding.Amount)]);
    }
}

/// <summary>One member's exposure as a guarantor, for the exposure report.</summary>
public sealed record GuarantorExposureLine(
    string GuarantorName,
    int LoansGuaranteed,
    Money TotalGuaranteed,
    Money TotalAtRisk,
    int LoansBlockingRelease)
{
    /// <summary>
    /// Whether this member's own funds could be released if they left CAL today.
    /// </summary>
    public bool FundsMayBeReleased => LoansBlockingRelease == 0;
}

/// <summary>Who has guaranteed what, and what it currently exposes them to.</summary>
public sealed record GuarantorExposureReportView(
    DateOnly AsAt,
    IReadOnlyList<GuarantorExposureLine> Lines)
{
    public Money TotalGuaranteed => Lines.Sum(line => line.TotalGuaranteed, Currency.Kes);

    public Money TotalAtRisk => Lines.Sum(line => line.TotalAtRisk, Currency.Kes);
}

/// <summary>Builds the guarantor exposure report.</summary>
public sealed record GetGuarantorExposureQuery(DateOnly AsAt) : IRequest<GuarantorExposureReportView>;

internal sealed class GetGuarantorExposureHandler
    : IRequestHandler<GetGuarantorExposureQuery, GuarantorExposureReportView>
{
    private readonly IBorrowerRepository _borrowers;
    private readonly ILoanRepository _loans;
    private readonly IBalanceQueries _balances;

    public GetGuarantorExposureHandler(
        IBorrowerRepository borrowers, ILoanRepository loans, IBalanceQueries balances)
    {
        _borrowers = borrowers;
        _loans = loans;
        _balances = balances;
    }

    public async Task<GuarantorExposureReportView> Handle(
        GetGuarantorExposureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var members = await _borrowers.AllMembersAsync(true, cancellationToken).ConfigureAwait(false);
        var liability = new ProRataLiability();
        var lines = new List<GuarantorExposureLine>();

        foreach (var member in members)
        {
            var guaranteed = await _loans
                .GuaranteedByAsync(member.Id, cancellationToken)
                .ConfigureAwait(false);

            if (guaranteed.Count == 0)
            {
                continue;
            }

            var exposures = new List<GuaranteedLoanExposure>(guaranteed.Count);

            foreach (var loan in guaranteed)
            {
                var outstanding = await _balances
                    .NaturalBalanceAsAtAsync(loan.ReceivableAccountId, query.AsAt, cancellationToken)
                    .ConfigureAwait(false);

                var borrower = await _borrowers
                    .FindMemberAsync(loan.BorrowerId, cancellationToken)
                    .ConfigureAwait(false);

                var borrowerShares = borrower is null
                    ? Money.ZeroKes
                    : await _balances
                        .NaturalBalanceAsAtAsync(borrower.SharesAccountId, query.AsAt, cancellationToken)
                        .ConfigureAwait(false);

                var guarantee = loan.Guarantees
                    .First(each => each.GuarantorId == member.Id && !each.IsReleased);

                var share = liability
                    .Apportion(outstanding, loan.Guarantees)
                    .FirstOrDefault(each => each.GuarantorId == member.Id);

                exposures.Add(new GuaranteedLoanExposure(
                    loan.Id.Value,
                    loan.LoanNumber,
                    borrower?.Name.Full ?? "Non-member client",
                    guarantee.GuaranteedAmount,
                    outstanding,
                    borrowerShares,
                    share?.AmountLiable ?? Money.ZeroKes,
                    IsInArrears: false));
            }

            var exposure = GuarantorExposureReport.For(member.Id, member.Name.Full, exposures);

            lines.Add(new GuarantorExposureLine(
                member.Name.Full,
                exposure.Loans.Count,
                exposure.TotalGuaranteed,
                exposure.TotalAtRisk,
                exposure.LoansBlockingRelease.Count));
        }

        return new GuarantorExposureReportView(
            query.AsAt, [.. lines.OrderByDescending(line => line.TotalAtRisk.Amount)]);
    }
}

/// <summary>One line of the income and expenditure account.</summary>
public sealed record IncomeAndExpenditureLine(string Code, string Name, Money Amount);

/// <summary>
/// Income and expenditure for a period.
/// </summary>
/// <remarks>
/// A period, not a position - which is why it takes two dates where the balance sheet takes
/// one. The surplus is what the dividend is calculated from, after bank charges, which is why
/// those are on it rather than buried.
/// </remarks>
public sealed record IncomeAndExpenditure(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<IncomeAndExpenditureLine> Income,
    IReadOnlyList<IncomeAndExpenditureLine> Expenditure)
{
    public Money TotalIncome => Income.Sum(line => line.Amount, Currency.Kes);

    public Money TotalExpenditure => Expenditure.Sum(line => line.Amount, Currency.Kes);

    /// <summary>
    /// Income less expenditure. The figure the dividend is worked out from.
    /// </summary>
    /// <remarks>
    /// "The account does not earn interest but incurs charges. These charges are used in
    /// calculating amount of interest earned from loans issued."
    /// </remarks>
    public Money Surplus => TotalIncome - TotalExpenditure;
}

/// <summary>Builds the income and expenditure account for a period.</summary>
public sealed record GetIncomeAndExpenditureQuery(DateOnly From, DateOnly To)
    : IRequest<IncomeAndExpenditure>;

internal sealed class GetIncomeAndExpenditureHandler
    : IRequestHandler<GetIncomeAndExpenditureQuery, IncomeAndExpenditure>
{
    private readonly IAccountRepository _accounts;
    private readonly IJournalRepository _journal;

    public GetIncomeAndExpenditureHandler(IAccountRepository accounts, IJournalRepository journal)
    {
        _accounts = accounts;
        _journal = journal;
    }

    public async Task<IncomeAndExpenditure> Handle(
        GetIncomeAndExpenditureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var accounts = await _accounts.AllAsync(cancellationToken).ConfigureAwait(false);
        var entries = await _journal
            .BetweenAsync(query.From, query.To, cancellationToken)
            .ConfigureAwait(false);

        var income = new List<IncomeAndExpenditureLine>();
        var expenditure = new List<IncomeAndExpenditureLine>();

        foreach (var account in accounts.Where(account =>
            account.Type is AccountType.Income or AccountType.Expense))
        {
            var movement = LedgerBalances.MovementBetween(
                entries, account.Id, query.From, query.To, Currency.Kes);

            // Income is credit-normal, so its movement is negative; expenses are debit-normal.
            var amount = account.Type == AccountType.Income ? -movement : movement;

            if (amount.IsZero)
            {
                continue;
            }

            var line = new IncomeAndExpenditureLine(account.Code.Value, account.Name, amount);

            if (account.Type == AccountType.Income)
            {
                income.Add(line);
            }
            else
            {
                expenditure.Add(line);
            }
        }

        return new IncomeAndExpenditure(
            query.From,
            query.To,
            [.. income.OrderBy(line => line.Code, StringComparer.Ordinal)],
            [.. expenditure.OrderBy(line => line.Code, StringComparer.Ordinal)]);
    }
}

/// <summary>
/// The AGM pack: what the members are shown once a year.
/// </summary>
/// <remarks>
/// The questionnaire asked what the AGM report must show and the answer was "treasurer's
/// report, chairman's report, a summary of incomes and expenses". The first two are written by
/// people, so Akiba carries them as text the officials supply and prints the figures around
/// them rather than generating prose nobody signed.
/// </remarks>
public sealed record AgmPack(
    int Year,
    DateOnly From,
    DateOnly To,
    string TreasurersReport,
    string ChairmansReport,
    IncomeAndExpenditure IncomeAndExpenditure,
    ShareholdingSummary Shareholding,
    Ledger.TrialBalance TrialBalance,
    int MembersAtYearEnd,
    int LoansRunningAtYearEnd,
    Money LoansOutstandingAtYearEnd);

/// <summary>Assembles the AGM pack for a year.</summary>
public sealed record GetAgmPackQuery(int Year, string TreasurersReport, string ChairmansReport)
    : IRequest<AgmPack>;

internal sealed class GetAgmPackHandler : IRequestHandler<GetAgmPackQuery, AgmPack>
{
    private readonly IMediator _mediator;
    private readonly ILoanRepository _loans;
    private readonly IBalanceQueries _balances;

    public GetAgmPackHandler(IMediator mediator, ILoanRepository loans, IBalanceQueries balances)
    {
        _mediator = mediator;
        _loans = loans;
        _balances = balances;
    }

    public async Task<AgmPack> Handle(GetAgmPackQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Financial periods follow the calendar year.
        var from = new DateOnly(query.Year, 1, 1);
        var to = new DateOnly(query.Year, 12, 31);

        var incomeAndExpenditure = await _mediator
            .Send(new GetIncomeAndExpenditureQuery(from, to), cancellationToken)
            .ConfigureAwait(false);

        var shareholding = await _mediator
            .Send(new GetShareholdingSummaryQuery(to), cancellationToken)
            .ConfigureAwait(false);

        var trialBalance = await _mediator
            .Send(new Ledger.GetTrialBalanceQuery(to), cancellationToken)
            .ConfigureAwait(false);

        var loans = await _loans.AllRunningAsync(cancellationToken).ConfigureAwait(false);

        var runningAtYearEnd = loans
            .Where(loan => loan.DisbursedOn <= to && (loan.SettledOn is null || loan.SettledOn > to))
            .ToList();

        var outstanding = Money.ZeroKes;

        foreach (var loan in runningAtYearEnd)
        {
            outstanding += await _balances
                .NaturalBalanceAsAtAsync(loan.ReceivableAccountId, to, cancellationToken)
                .ConfigureAwait(false);
        }

        return new AgmPack(
            query.Year,
            from,
            to,
            query.TreasurersReport,
            query.ChairmansReport,
            incomeAndExpenditure,
            shareholding,
            trialBalance,
            shareholding.MemberCount,
            runningAtYearEnd.Count,
            outstanding);
    }
}
