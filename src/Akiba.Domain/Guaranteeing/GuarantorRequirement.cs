using Akiba.Domain.Financial;
using Akiba.Domain.Lending;

namespace Akiba.Domain.Guaranteeing;

/// <summary>Whether a loan needs guarantors, and how firmly.</summary>
public enum GuarantorRequirementLevel
{
    /// <summary>No guarantors needed. Confirmed only for emergency loans meeting the rule.</summary>
    NotRequired = 0,

    /// <summary>
    /// Guarantors are required and the application cannot proceed without them.
    /// </summary>
    Required = 1,
}

/// <summary>
/// The outcome of the guarantor check on an application.
/// </summary>
/// <param name="Level">Whether guarantors are needed.</param>
/// <param name="AmountNotCoveredByOwnShares">
/// How much of the loan the borrower's own shares do not reach. Shown to the approver as the
/// form's "Do guarantors sufficiently cover the loan?" indicator.
/// </param>
/// <param name="Reason">Why, in the words an official would use.</param>
/// <param name="IsSettledRule">
/// False where the requirement rests on a default rather than on a rule the committee has
/// confirmed. The panel shows this as a warning so an approver knows the difference.
/// </param>
public sealed record GuarantorRequirement(
    GuarantorRequirementLevel Level,
    Money AmountNotCoveredByOwnShares,
    string Reason,
    bool IsSettledRule)
{
    public bool AreGuarantorsRequired => Level == GuarantorRequirementLevel.Required;
}

/// <summary>
/// Decides whether a loan needs guarantors.
/// </summary>
/// <remarks>
/// <para>
/// One rule here is settled and one is not, and the difference is carried through into the
/// result rather than smoothed over.
/// </para>
/// <para>
/// <b>Settled:</b> an emergency loan is unguaranteed unless the member's normal loan balance
/// exceeds their total shares. The questionnaire states this directly.
/// </para>
/// <para>
/// <b>Not settled:</b> whether a normal loan fully covered by the borrower's own shares needs
/// guarantors at all. Nothing in any source document says so. The application form has
/// eighteen guarantor rows and asks whether the guarantors sufficiently cover the loan, which
/// suggests guarantors are expected regardless - so the default requires at least one and
/// reports coverage as an indicator rather than treating full cover as a waiver.
/// See docs/open-questions.md, item 1.
/// </para>
/// </remarks>
public static class GuarantorRequirementPolicy
{
    /// <summary>
    /// Decides the requirement for an application.
    /// </summary>
    /// <param name="product">The product applied for.</param>
    /// <param name="loanAmount">The total repayable on the new loan.</param>
    /// <param name="borrowerShareholding">The borrower's shares as at the application date.</param>
    /// <param name="existingNormalLoanBalance">
    /// What the borrower still owes on any running normal loan. Used by the emergency rule.
    /// </param>
    public static GuarantorRequirement For(
        LoanProduct product,
        Money loanAmount,
        Money borrowerShareholding,
        Money existingNormalLoanBalance)
    {
        var uncovered = Membership.Shareholding.AmountNotCoveredByShares(borrowerShareholding, loanAmount);

        if (product == LoanProduct.Emergency)
        {
            // "Normally emergency loans are not guaranteed unless the normal loan balance
            // exceeds the total number of shares the member has."
            var normalLoanExceedsShares = existingNormalLoanBalance > borrowerShareholding;

            return normalLoanExceedsShares
                ? new GuarantorRequirement(
                    GuarantorRequirementLevel.Required,
                    uncovered,
                    $"The member's normal loan balance of {existingNormalLoanBalance} exceeds " +
                    $"their shareholding of {borrowerShareholding}, so this emergency loan " +
                    "must be guaranteed.",
                    IsSettledRule: true)
                : new GuarantorRequirement(
                    GuarantorRequirementLevel.NotRequired,
                    uncovered,
                    "Emergency loans are unguaranteed unless the member's normal loan balance " +
                    "exceeds their shares, and it does not.",
                    IsSettledRule: true);
        }

        // TODO: confirm with committee - open question 1. Whether a normal loan fully covered
        // by the borrower's own shares needs guarantors is not stated anywhere. Requiring one
        // is the conservative default; do not relax it without an answer.
        return new GuarantorRequirement(
            GuarantorRequirementLevel.Required,
            uncovered,
            uncovered.IsZero
                ? "The loan is fully covered by the member's own shares, but the committee has " +
                  "not confirmed that such a loan may go unguaranteed, so a guarantor is still " +
                  "required."
                : $"{uncovered} of this loan is not covered by the member's own shares.",
            IsSettledRule: uncovered.IsPositive);
    }
}

/// <summary>
/// Whether the guarantors on an application cover it, which is the question the form's
/// official-use section asks.
/// </summary>
/// <param name="TotalGuaranteed">The sum of the guaranteed amounts.</param>
/// <param name="AmountToCover">What needs covering.</param>
/// <param name="Shortfall">How far short the guarantees fall. Zero when they suffice.</param>
public sealed record GuarantorCoverage(Money TotalGuaranteed, Money AmountToCover, Money Shortfall)
{
    /// <summary>The Yes/No the accounts clerk writes on the form.</summary>
    public bool IsSufficient => Shortfall.IsZero;

    /// <summary>
    /// Works out the coverage on an application.
    /// </summary>
    /// <param name="guarantees">The guarantees offered.</param>
    /// <param name="amountToCover">
    /// What must be covered. Where the committee confirms that a borrower's own shares count
    /// towards cover, this is the uncovered portion; until then it is the whole loan.
    /// </param>
    public static GuarantorCoverage Of(IReadOnlyList<Guarantee> guarantees, Money amountToCover)
    {
        var live = GuaranteeRules.Live(guarantees);

        var total = live.Sum(guarantee => guarantee.GuaranteedAmount, amountToCover.Currency);
        var shortfall = amountToCover - total;

        return new GuarantorCoverage(
            total,
            amountToCover,
            shortfall.IsNegative ? Money.Zero(amountToCover.Currency) : shortfall);
    }
}
