using Akiba.Domain.Financial;

namespace Akiba.Domain.Lending;

public enum OffsetRefusal
{
    None = 0,
    NotPaidDownFarEnough = 1,
    NotRunning = 2,
    NothingOutstanding = 3,
}

/// <summary>Whether a running loan can be offset by a new one, and why if not.</summary>
public sealed record OffsetEligibility(
    OffsetRefusal Refusal,
    string LoanNumber,
    LoanId LoanId,
    Money OutstandingBalance,
    decimal ProportionRepaid)
{
    public bool IsEligible => Refusal == OffsetRefusal.None;

    public string Explanation => Refusal switch
    {
        OffsetRefusal.None =>
            $"Eligible. {ProportionRepaid:P0} repaid; {OutstandingBalance} will be cleared by the " +
            "new loan's proceeds.",
        OffsetRefusal.NotPaidDownFarEnough =>
            $"Only {ProportionRepaid:P0} repaid. At least three-quarters must be repaid before a " +
            "new loan can offset the remaining balance.",
        OffsetRefusal.NotRunning =>
            "Only a running loan can be offset.",
        OffsetRefusal.NothingOutstanding =>
            "Nothing is outstanding on this loan — it cannot be offset.",
        _ => "This loan cannot be used for an offset.",
    };
}

/// <summary>
/// Rules for offsetting a running loan with a new one.
/// </summary>
/// <remarks>
/// <para>
/// A member who has repaid at least three-quarters of an existing loan may take a fresh loan.
/// The new loan's proceeds clear the remaining balance of the old one; the member receives only
/// the net — new principal minus old balance outstanding. This is distinct from restructuring,
/// which carries a balance forward with no fresh money.
/// </para>
/// <para>
/// The threshold (75 %) is encoded here rather than in policy configuration, because the
/// questionnaire was explicit: it is three-quarters, and the society has not proposed changing
/// it.
/// </para>
/// </remarks>
public static class LoanOffset
{
    public const decimal MinimumProportionRepaid = 0.75m;

    /// <summary>
    /// Determines whether a loan is eligible to be offset by a new disbursement.
    /// </summary>
    /// <param name="loan">The existing running loan.</param>
    /// <param name="outstandingBalance">
    /// Its receivable account balance as at the proposed disbursement date, derived from the
    /// ledger by the caller.
    /// </param>
    public static OffsetEligibility Assess(Loan loan, Money outstandingBalance)
    {
        ArgumentNullException.ThrowIfNull(loan);

        if (loan.Status != LoanStatus.Running)
        {
            return new OffsetEligibility(
                OffsetRefusal.NotRunning, loan.LoanNumber, loan.Id, outstandingBalance, 0m);
        }

        if (!outstandingBalance.IsPositive)
        {
            return new OffsetEligibility(
                OffsetRefusal.NothingOutstanding, loan.LoanNumber, loan.Id, outstandingBalance, 0m);
        }

        var totalRepayable = loan.Terms.TotalRepayable;
        var repaid = totalRepayable - outstandingBalance;
        var proportionRepaid = totalRepayable.IsZero
            ? 0m
            : repaid.Amount / totalRepayable.Amount;

        var refusal = proportionRepaid < MinimumProportionRepaid
            ? OffsetRefusal.NotPaidDownFarEnough
            : OffsetRefusal.None;

        return new OffsetEligibility(refusal, loan.LoanNumber, loan.Id, outstandingBalance, proportionRepaid);
    }
}
