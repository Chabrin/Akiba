using Akiba.Domain.Financial;

namespace Akiba.Domain.Lending;

/// <summary>Why a loan may not be restructured.</summary>
public enum RestructureRefusal
{
    None = 0,

    /// <summary>Less than half of principal plus interest has been repaid.</summary>
    NotPaidDownFarEnough = 1,

    /// <summary>A loan may be restructured once only.</summary>
    AlreadyRestructured = 2,

    /// <summary>The loan is settled, written off, or already closed into a restructure.</summary>
    NotRunning = 3,

    /// <summary>Nothing is owed, so there is nothing to restructure.</summary>
    NothingOutstanding = 4,
}

/// <summary>
/// Whether a loan may be restructured, and on what terms.
/// </summary>
/// <param name="Refusal">Why not, where it may not.</param>
/// <param name="OutstandingBalance">What is owed before the fee.</param>
/// <param name="AmountRepaid">What has been repaid so far.</param>
/// <param name="ProportionRepaid">
/// How far the loan has been paid down, as a proportion of principal plus interest.
/// </param>
/// <param name="Fee">5% of the outstanding balance, deducted upfront.</param>
/// <param name="RestructuredBalance">
/// What is carried into the new loan. The fee is deducted upfront and the restructured
/// instalments start after it, so the fee is not rolled into the balance.
/// </param>
/// <param name="TermMonths">The new term, from the graduated scale applied to the balance.</param>
public sealed record RestructureAssessment(
    RestructureRefusal Refusal,
    Money OutstandingBalance,
    Money AmountRepaid,
    decimal ProportionRepaid,
    Money Fee,
    Money RestructuredBalance,
    int? TermMonths)
{
    public bool IsPermitted => Refusal == RestructureRefusal.None;

    /// <summary>What an official is told when the answer is no.</summary>
    public string Explanation => Refusal switch
    {
        RestructureRefusal.None =>
            $"May be restructured. {ProportionRepaid:P1} of principal plus interest has been " +
            $"repaid, the fee is {Fee}, and {RestructuredBalance} would run for {TermMonths} months.",
        RestructureRefusal.NotPaidDownFarEnough =>
            $"Only {ProportionRepaid:P1} of principal plus interest has been repaid. A loan must " +
            "be paid down to at least 50% before it can be restructured.",
        RestructureRefusal.AlreadyRestructured =>
            "This loan has already been restructured. A loan may be restructured once only.",
        RestructureRefusal.NotRunning =>
            "Only a running loan can be restructured.",
        RestructureRefusal.NothingOutstanding =>
            "Nothing is outstanding on this loan.",
        _ => "Cannot be restructured.",
    };
}

/// <summary>
/// The restructuring rules.
/// </summary>
/// <remarks>
/// <para>
/// Every rule here is stated in the questionnaire or the minutes:
/// </para>
/// <list type="bullet">
/// <item>Only once the loan is paid down to at least 50% of principal plus interest.</item>
/// <item>A fee of 5% of the outstanding balance, <b>deducted upfront</b>, with the
/// restructured instalments starting after the fee.</item>
/// <item>The restructured balance attracts <b>no fresh interest</b>.</item>
/// <item>Once only per loan.</item>
/// <item>The original guarantors carry over - "the terms of the loan still remain".</item>
/// <item>The new term comes from the graduated scale applied to the restructured balance.</item>
/// </list>
/// <para>
/// The old loan is closed into the new one <b>through the ledger</b>. The original is never
/// mutated: its receivable account is credited down to zero and the new loan's account is
/// debited, so both loans' histories stay readable and the member can see what happened.
/// </para>
/// </remarks>
public static class Restructuring
{
    /// <summary>The loan must be paid down to at least this proportion of principal plus interest.</summary>
    public const decimal MinimumProportionRepaid = 0.50m;

    /// <summary>The fee, as a proportion of the outstanding balance.</summary>
    public const decimal FeeRate = 0.05m;

    /// <summary>
    /// Assesses whether a loan may be restructured.
    /// </summary>
    /// <param name="loan">The loan.</param>
    /// <param name="outstandingBalance">
    /// What is still owed, derived from the loan's receivable account as at today. The loan
    /// aggregate does not hold a balance, so the caller derives it.
    /// </param>
    /// <param name="termScale">The graduated scale in force.</param>
    public static RestructureAssessment Assess(
        Loan loan,
        Money outstandingBalance,
        GraduatedTermScale? termScale = null)
    {
        ArgumentNullException.ThrowIfNull(loan);

        var scale = termScale ?? GraduatedTermScale.Version1;
        var totalRepayable = loan.Terms.TotalRepayable;
        var repaid = totalRepayable - outstandingBalance;

        var proportionRepaid = totalRepayable.IsZero
            ? 0m
            : repaid.Amount / totalRepayable.Amount;

        var fee = (outstandingBalance * FeeRate).Round();

        var refusal = Refuse(loan, outstandingBalance, proportionRepaid);

        // The term is only worked out where the restructure is permitted; a balance below the
        // scale's floor would otherwise throw while explaining a refusal.
        int? termMonths = refusal == RestructureRefusal.None
            ? scale.TermFor(outstandingBalance)
            : null;

        return new RestructureAssessment(
            refusal == RestructureRefusal.None && termMonths is null
                ? RestructureRefusal.NotPaidDownFarEnough
                : refusal,
            outstandingBalance,
            repaid,
            proportionRepaid,
            fee,
            outstandingBalance,
            termMonths);
    }

    /// <summary>
    /// The terms of the loan that replaces a restructured one.
    /// </summary>
    /// <remarks>
    /// No fresh interest, so the total repayable equals the balance carried over. The fee is
    /// not part of it: the fee is deducted upfront and the instalments start afterwards.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The loan may not be restructured.</exception>
    public static LoanTerms TermsFor(RestructureAssessment assessment, GraduatedTermScale? termScale = null)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        if (!assessment.IsPermitted)
        {
            throw new InvalidOperationException(assessment.Explanation);
        }

        var scale = termScale ?? GraduatedTermScale.Version1;

        return new LoanTerms(
            LoanProduct.Normal,
            assessment.RestructuredBalance,
            Money.Zero(assessment.RestructuredBalance.Currency),
            assessment.TermMonths!.Value,
            scale.Version);
    }

    /// <summary>
    /// Closes the original loan and hands its guarantees to the replacement.
    /// </summary>
    /// <remarks>
    /// The original guarantors carry over rather than re-signing. The questionnaire's answer
    /// to whether they must re-sign was "the terms of the loan still remain", which is read
    /// here as the guarantees continuing unchanged.
    /// </remarks>
    public static void CarryOver(Loan original, Loan replacement, DateOnly restructuredOn)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(replacement);

        original.CloseIntoRestructure(restructuredOn);
        replacement.RecordAsRestructureOf(original.Id);
    }

    private static RestructureRefusal Refuse(Loan loan, Money outstandingBalance, decimal proportionRepaid)
    {
        if (loan.Status != LoanStatus.Running)
        {
            return RestructureRefusal.NotRunning;
        }

        if (loan.HasBeenRestructured)
        {
            return RestructureRefusal.AlreadyRestructured;
        }

        if (!outstandingBalance.IsPositive)
        {
            return RestructureRefusal.NothingOutstanding;
        }

        return proportionRepaid < MinimumProportionRepaid
            ? RestructureRefusal.NotPaidDownFarEnough
            : RestructureRefusal.None;
    }
}
