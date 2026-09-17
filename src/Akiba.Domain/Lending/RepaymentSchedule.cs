using Akiba.Domain.Financial;

namespace Akiba.Domain.Lending;

/// <summary>
/// One instalment of a repayment schedule.
/// </summary>
/// <param name="Number">Which instalment this is, counting from one.</param>
/// <param name="DueDate">
/// The last day of the month the deduction runs in. Repayment is by payroll deduction, and
/// the deduction list must reach HR by the 25th of that month.
/// </param>
/// <param name="Amount">What is due. The instalments sum exactly to the total repayable.</param>
/// <param name="PrincipalPortion">
/// The share of this instalment attributable to principal, for a member's statement.
/// </param>
/// <param name="InterestPortion">The share attributable to interest.</param>
public sealed record ScheduledInstalment(
    int Number,
    DateOnly DueDate,
    Money Amount,
    Money PrincipalPortion,
    Money InterestPortion);

/// <summary>
/// A loan's instalments: what is due, and when.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every loan gets one full month of grace.</b> The questionnaire's own example: a loan
/// disbursed on 20 September has October as its grace month and its first instalment in
/// November. So the first deduction falls in the second month after disbursement, whatever
/// day of the month the cheque was drawn.
/// </para>
/// <para>
/// The instalments are produced by <see cref="Money.Allocate(int)"/>, so they sum back to
/// principal plus interest exactly, and any odd cents land on the earliest instalments -
/// the ones closest to disbursement and easiest for a member to check against a payslip.
/// </para>
/// <para>
/// The principal and interest portions are for a member's statement only. Akiba's ledger
/// carries one receivable balance per loan, because the interest is added once at
/// disbursement rather than accruing - so nothing in the accounting depends on this split.
/// </para>
/// </remarks>
public sealed class RepaymentSchedule
{
    private readonly List<ScheduledInstalment> _instalments;

    private RepaymentSchedule(
        LoanTerms terms,
        DateOnly disbursedOn,
        DateOnly graceEndsOn,
        List<ScheduledInstalment> instalments)
    {
        Terms = terms;
        DisbursedOn = disbursedOn;
        GraceEndsOn = graceEndsOn;
        _instalments = instalments;
    }

    public LoanTerms Terms { get; }

    public DateOnly DisbursedOn { get; }

    /// <summary>The last day of the grace month. Nothing is due on or before this date.</summary>
    public DateOnly GraceEndsOn { get; }

    public IReadOnlyList<ScheduledInstalment> Instalments => _instalments;

    public DateOnly FirstDueDate => _instalments[0].DueDate;

    public DateOnly FinalDueDate => _instalments[^1].DueDate;

    /// <summary>
    /// The sum of every instalment, which equals <see cref="LoanTerms.TotalRepayable"/>.
    /// </summary>
    public Money TotalScheduled =>
        _instalments.Sum(instalment => instalment.Amount, Terms.Principal.Currency);

    /// <summary>
    /// Builds the schedule for a loan disbursed on a date.
    /// </summary>
    /// <param name="terms">The priced loan.</param>
    /// <param name="disbursedOn">The date the cheque was drawn.</param>
    public static RepaymentSchedule Generate(LoanTerms terms, DateOnly disbursedOn)
    {
        ArgumentNullException.ThrowIfNull(terms);

        // One full month of grace: the month after disbursement. A loan disbursed on
        // 20 September has October free and first pays in November.
        var graceMonth = FirstOfMonth(disbursedOn).AddMonths(1);
        var graceEndsOn = LastDayOfMonth(graceMonth);
        var firstDueMonth = graceMonth.AddMonths(1);

        var amounts = terms.TotalRepayable.Allocate(terms.TermMonths);
        var principalPortions = terms.Principal.Allocate(terms.TermMonths);
        var interestPortions = terms.Interest.Allocate(terms.TermMonths);

        var instalments = new List<ScheduledInstalment>(terms.TermMonths);

        for (var index = 0; index < terms.TermMonths; index++)
        {
            instalments.Add(new ScheduledInstalment(
                Number: index + 1,
                DueDate: LastDayOfMonth(firstDueMonth.AddMonths(index)),
                Amount: amounts[index],
                PrincipalPortion: principalPortions[index],
                InterestPortion: interestPortions[index]));
        }

        return new RepaymentSchedule(terms, disbursedOn, graceEndsOn, instalments);
    }

    /// <summary>
    /// How much should have been paid by a date, if every instalment fell due on time.
    /// </summary>
    /// <remarks>
    /// This is the expected figure that arrears is measured against. What was actually paid
    /// comes from the ledger; the difference is what is overdue.
    /// </remarks>
    public Money ExpectedPaidBy(DateOnly asAt) =>
        _instalments
            .Where(instalment => instalment.DueDate <= asAt)
            .Sum(instalment => instalment.Amount, Terms.Principal.Currency);

    /// <summary>The instalment due in a given month, where there is one.</summary>
    public ScheduledInstalment? InstalmentDueIn(int year, int month) =>
        _instalments.FirstOrDefault(instalment =>
            instalment.DueDate.Year == year && instalment.DueDate.Month == month);

    private static DateOnly FirstOfMonth(DateOnly date) => new(date.Year, date.Month, 1);

    private static DateOnly LastDayOfMonth(DateOnly date) =>
        new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
}
