using Akiba.Application.Abstractions;
using Akiba.Domain.Financial;
using Akiba.Domain.Lending;
using Akiba.Domain.Membership;
using MediatR;

namespace Akiba.Application.Reporting;

/// <summary>One member's line on the monthly deduction schedule.</summary>
/// <param name="PayrollNumber">What HR matches on. Empty for a member not on the payroll.</param>
/// <param name="MembershipNumber">Akiba's own number.</param>
/// <param name="FullName">The name, as HR expects to read it.</param>
/// <param name="OpeningShareholding">What they held at the end of the previous month.</param>
/// <param name="ShareContribution">The monthly amount they have chosen.</param>
/// <param name="LoanInstalments">Each running loan's instalment falling due this month.</param>
/// <param name="ClosingShareholding">
/// What they will hold once this month's contribution is applied. The register's own
/// "END OF ..." column.
/// </param>
public sealed record DeductionLine(
    string PayrollNumber,
    string MembershipNumber,
    string FullName,
    Money OpeningShareholding,
    Money ShareContribution,
    IReadOnlyList<LoanInstalmentDue> LoanInstalments,
    Money ClosingShareholding)
{
    public Money TotalLoanInstalments =>
        LoanInstalments.Sum(instalment => instalment.Amount, Currency.Kes);

    /// <summary>What HR deducts from this payslip.</summary>
    public Money TotalDeduction => ShareContribution + TotalLoanInstalments;

    public bool IsOnPayroll => !string.IsNullOrWhiteSpace(PayrollNumber);
}

/// <summary>An instalment falling due this month.</summary>
public sealed record LoanInstalmentDue(string LoanNumber, LoanProduct Product, Money Amount);

/// <summary>Which schedule: the employees, or the landlords.</summary>
/// <remarks>
/// Two schedules, always. Employee deductions produce a cheque drawn on the business account
/// and landlord rent offsets one drawn on the main account, and they reconcile against
/// different statements - so mixing them would make both impossible to check.
/// </remarks>
public enum DeductionScheduleKind
{
    Employees = 1,
    Landlords = 2,
}

/// <summary>
/// The monthly deduction schedule.
/// </summary>
/// <param name="Kind">Employees or landlords.</param>
/// <param name="Year">The month it covers.</param>
/// <param name="Month">The month it covers.</param>
/// <param name="MustReachHrBy">
/// The 25th. The clerk's list has to be with HR by then to make that month's payroll.
/// </param>
/// <param name="Lines">One per member, in payroll-number order the way HR reads it.</param>
public sealed record DeductionSchedule(
    DeductionScheduleKind Kind,
    int Year,
    int Month,
    DateOnly MustReachHrBy,
    IReadOnlyList<DeductionLine> Lines)
{
    public DateOnly MonthEnd => new(Year, Month, DateTime.DaysInMonth(Year, Month));

    public DateOnly PreviousMonthEnd => new DateOnly(Year, Month, 1).AddDays(-1);

    public Money TotalShareContributions =>
        Lines.Sum(line => line.ShareContribution, Currency.Kes);

    public Money TotalLoanInstalments =>
        Lines.Sum(line => line.TotalLoanInstalments, Currency.Kes);

    /// <summary>What the cheque to Akiba should come to.</summary>
    public Money TotalDeductions => Lines.Sum(line => line.TotalDeduction, Currency.Kes);

    public Money OpeningShareholding =>
        Lines.Sum(line => line.OpeningShareholding, Currency.Kes);

    public Money ClosingShareholding =>
        Lines.Sum(line => line.ClosingShareholding, Currency.Kes);

    /// <summary>
    /// Members on this schedule who are not on the CAL payroll.
    /// </summary>
    /// <remarks>
    /// HR cannot deduct from somebody who has no payslip. They appear on the schedule so the
    /// clerk can see them and chase the money another way, and the sheet says so rather than
    /// quietly leaving them out.
    /// </remarks>
    public IReadOnlyList<DeductionLine> NotOnPayroll =>
        [.. Lines.Where(line => !line.IsOnPayroll)];

    public string Title =>
        $"{(Kind == DeductionScheduleKind.Employees ? "Employee" : "Landlord")} deductions - " +
        $"{MonthEnd:MMMM yyyy}";
}

/// <summary>
/// Builds the monthly deduction schedule.
/// </summary>
/// <remarks>
/// <para>
/// Every figure comes from the ledger and from each loan's own schedule. The opening
/// shareholding is that member's share account as at the end of the previous month, and the
/// closing figure is it plus this month's contribution - the same two columns the society's
/// own register carries, so a clerk comparing the two is comparing like with like.
/// </para>
/// <para>
/// The share contribution is the member's chosen monthly amount, which the register shows
/// varies from 1,000 to 25,000 and is steady per member. Akiba takes it from what they last
/// contributed rather than from a society-wide figure, because there is no society-wide figure.
/// </para>
/// </remarks>
public sealed record GetDeductionScheduleQuery(DeductionScheduleKind Kind, int Year, int Month)
    : IRequest<DeductionSchedule>;

internal sealed class GetDeductionScheduleHandler
    : IRequestHandler<GetDeductionScheduleQuery, DeductionSchedule>
{
    private readonly IBorrowerRepository _borrowers;
    private readonly ILoanRepository _loans;
    private readonly IJournalRepository _journal;
    private readonly IBalanceQueries _balances;

    public GetDeductionScheduleHandler(
        IBorrowerRepository borrowers,
        ILoanRepository loans,
        IJournalRepository journal,
        IBalanceQueries balances)
    {
        _borrowers = borrowers;
        _loans = loans;
        _journal = journal;
        _balances = balances;
    }

    public async Task<DeductionSchedule> Handle(
        GetDeductionScheduleQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var monthEnd = new DateOnly(query.Year, query.Month, DateTime.DaysInMonth(query.Year, query.Month));
        var previousMonthEnd = new DateOnly(query.Year, query.Month, 1).AddDays(-1);

        var members = await _borrowers.AllMembersAsync(false, cancellationToken).ConfigureAwait(false);

        var forThisSchedule = members
            .Where(member => query.Kind == DeductionScheduleKind.Landlords
                ? member.IsLandlord
                : !member.IsLandlord)
            .ToList();

        var lines = new List<DeductionLine>(forThisSchedule.Count);

        foreach (var member in forThisSchedule)
        {
            var opening = await _balances
                .NaturalBalanceAsAtAsync(member.SharesAccountId, previousMonthEnd, cancellationToken)
                .ConfigureAwait(false);

            var contribution = await MonthlyContributionAsync(member, previousMonthEnd, cancellationToken)
                .ConfigureAwait(false);

            var loans = await _loans
                .RunningForBorrowerAsync(member.Id, cancellationToken)
                .ConfigureAwait(false);

            var instalments = loans
                .Select(loan => new
                {
                    loan,
                    due = loan.Schedule.InstalmentDueIn(query.Year, query.Month),
                })
                .Where(entry => entry.due is not null)
                .Select(entry => new LoanInstalmentDue(
                    entry.loan.LoanNumber, entry.loan.Terms.Product, entry.due!.Amount))
                .ToList();

            lines.Add(new DeductionLine(
                member.PayrollNumber.IsSpecified ? member.PayrollNumber.Value : string.Empty,
                member.MembershipNumber.Value,
                member.Name.Full,
                opening,
                contribution,
                instalments,
                opening + contribution));
        }

        return new DeductionSchedule(
            query.Kind,
            query.Year,
            query.Month,
            // "The list should be with the HR on the 25th of every month."
            new DateOnly(query.Year, query.Month, 25),
            [
                .. lines
                    .OrderBy(line => line.IsOnPayroll ? 0 : 1)
                    .ThenBy(line => line.PayrollNumber, StringComparer.Ordinal)
                    .ThenBy(line => line.FullName, StringComparer.Ordinal),
            ]);
    }

    /// <summary>
    /// What this member contributes a month.
    /// </summary>
    /// <remarks>
    /// Taken from their most recent contribution rather than from a fixed figure, because
    /// contributions are member-chosen and there is no society-wide amount. A member who has
    /// never contributed has nothing to go on, and appears with zero for the clerk to fill in.
    /// </remarks>
    private async Task<Money> MonthlyContributionAsync(
        Member member, DateOnly asAt, CancellationToken cancellationToken)
    {
        var entries = await _journal
            .ForAccountAsOfAsync(member.SharesAccountId, asAt, cancellationToken)
            .ConfigureAwait(false);

        var lastContribution = entries
            .Where(entry => entry.Narration.StartsWith("Share contribution", StringComparison.Ordinal))
            .OrderByDescending(entry => entry.EntryDate)
            .ThenByDescending(entry => entry.PostedAtUtc)
            .SelectMany(entry => entry.Lines)
            .FirstOrDefault(line =>
                line.AccountId == member.SharesAccountId
                && line.Side == Domain.Ledger.BalanceSide.Credit);

        return lastContribution?.Magnitude ?? Money.ZeroKes;
    }
}
