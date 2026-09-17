using Akiba.Domain.Financial;

namespace Akiba.Domain.Membership;

/// <summary>
/// The outcome of the two-thirds affordability check.
/// </summary>
/// <param name="GrossSalary">The applicant's gross monthly salary, as declared on the form.</param>
/// <param name="ExistingDeductions">
/// Everything already coming off the payslip, including the member's share contribution and
/// any running loan instalments.
/// </param>
/// <param name="ProposedInstalment">The monthly instalment of the loan being applied for.</param>
public sealed record AffordabilityAssessment(
    Money GrossSalary,
    Money ExistingDeductions,
    Money ProposedInstalment)
{
    /// <summary>Total deductions if the loan were approved.</summary>
    public Money TotalDeductions => ExistingDeductions + ProposedInstalment;

    /// <summary>The legal ceiling: two thirds of gross salary.</summary>
    public Money MaximumPermittedDeductions => (GrossSalary * (2m / 3m)).Round();

    public bool IsWithinLimit => TotalDeductions <= MaximumPermittedDeductions;

    /// <summary>How far over the ceiling the deductions would be. Zero when within limit.</summary>
    public Money ExcessOverLimit
    {
        get
        {
            var excess = TotalDeductions - MaximumPermittedDeductions;
            return excess.IsNegative ? Money.Zero(GrossSalary.Currency) : excess;
        }
    }

    /// <summary>
    /// The largest instalment this applicant could afford, given their existing deductions.
    /// Zero where existing deductions already reach the ceiling.
    /// </summary>
    public Money LargestAffordableInstalment
    {
        get
        {
            var headroom = MaximumPermittedDeductions - ExistingDeductions;
            return headroom.IsNegative ? Money.Zero(GrossSalary.Currency) : headroom;
        }
    }
}

/// <summary>
/// The Kenyan two-thirds rule: total deductions from gross salary may not exceed two thirds.
/// </summary>
/// <remarks>
/// <para>
/// Checked at application, before approval, because the consequence of getting it wrong falls
/// on HR at payroll time rather than on Akiba.
/// </para>
/// <para>
/// On a breach there are exactly two sanctioned remedies, both named in the questionnaire:
/// <b>reduce the loan</b>, or <b>reduce the monthly share deduction</b>. Approval is blocked
/// and both are presented. The system does not pick one.
/// </para>
/// <para>
/// Gross salary is an input on the application form. Akiba does not read payroll and has no
/// connection to it, so the figure is whatever the member declared and the clerk entered.
/// </para>
/// </remarks>
public static class Affordability
{
    /// <summary>The statutory ceiling as a proportion of gross salary.</summary>
    public const decimal MaximumDeductionRatio = 2m / 3m;

    public static AffordabilityAssessment Assess(
        Money grossSalary,
        Money existingDeductions,
        Money proposedInstalment)
    {
        if (!grossSalary.IsPositive)
        {
            throw new ArgumentException(
                "Gross salary must be positive. It is declared on the application form; " +
                "Akiba does not read payroll.",
                nameof(grossSalary));
        }

        if (existingDeductions.IsNegative)
        {
            throw new ArgumentException(
                "Existing deductions cannot be negative.", nameof(existingDeductions));
        }

        if (proposedInstalment.IsNegative)
        {
            throw new ArgumentException(
                "A proposed instalment cannot be negative.", nameof(proposedInstalment));
        }

        return new AffordabilityAssessment(grossSalary, existingDeductions, proposedInstalment);
    }
}
