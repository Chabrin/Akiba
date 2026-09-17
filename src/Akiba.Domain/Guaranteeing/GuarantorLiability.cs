using Akiba.Domain.Financial;
using Akiba.Domain.Membership;

namespace Akiba.Domain.Guaranteeing;

/// <summary>What one guarantor is liable for on a defaulting loan.</summary>
/// <param name="GuarantorId">Who.</param>
/// <param name="GuarantorName">Their name as it was written on the form.</param>
/// <param name="GuaranteedAmount">What they put their name to.</param>
/// <param name="AmountLiable">What they are being pursued for.</param>
public sealed record GuarantorLiability(
    BorrowerId GuarantorId,
    string GuarantorName,
    Money GuaranteedAmount,
    Money AmountLiable);

/// <summary>
/// How an outstanding balance is apportioned across guarantors on default.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which of these applies is an open contradiction in the source documents, and the code
/// does not resolve it.</b> The answered questionnaire describes pro-rata liability with a
/// worked example. The loan application form that guarantors actually sign says they "accept
/// joint and several liability for repayment of this loan". Those are materially different
/// positions, and the form is the document with signatures on it.
/// </para>
/// <para>
/// Both are implemented and selected by versioned configuration.
/// <see cref="ProRataLiability"/> is the default because it is what the officials described
/// in answer to a direct question. See docs/open-questions.md, item 2 - this must be settled
/// before anybody is actually pursued.
/// </para>
/// </remarks>
public interface IGuarantorLiabilityStrategy
{
    /// <summary>How the basis is described on a report or a demand letter.</summary>
    string Description { get; }

    /// <summary>
    /// Works out what each guarantor is liable for.
    /// </summary>
    /// <param name="outstandingBalance">What the borrower still owes.</param>
    /// <param name="guarantees">The live guarantees on the loan.</param>
    IReadOnlyList<GuarantorLiability> Apportion(
        Money outstandingBalance,
        IReadOnlyList<Guarantee> guarantees);
}

/// <summary>
/// Each guarantor bears the share of the balance that their guarantee bore of the total
/// guaranteed.
/// </summary>
/// <remarks>
/// The questionnaire's worked example: a guarantor who guaranteed 50,000 out of a total
/// guaranteed amount of 200,000 is liable for 0.25 of the outstanding balance.
///
/// The split uses <see cref="Money.Allocate(IReadOnlyList{decimal})"/>, so the liabilities
/// sum to the outstanding balance exactly. A lost cent here is a member being pursued for
/// the wrong figure.
/// </remarks>
public sealed class ProRataLiability : IGuarantorLiabilityStrategy
{
    public string Description =>
        "Each guarantor is liable for the share of the balance that their guarantee bore of " +
        "the total guaranteed";

    public IReadOnlyList<GuarantorLiability> Apportion(
        Money outstandingBalance,
        IReadOnlyList<Guarantee> guarantees)
    {
        var live = GuaranteeRules.Live(guarantees);

        if (live.Count == 0 || !outstandingBalance.IsPositive)
        {
            return [];
        }

        var shares = outstandingBalance.Allocate(
            [.. live.Select(guarantee => guarantee.GuaranteedAmount.Amount)]);

        return [.. live.Select((guarantee, index) => new GuarantorLiability(
            guarantee.GuarantorId,
            guarantee.GuarantorName,
            guarantee.GuaranteedAmount,
            shares[index]))];
    }
}

/// <summary>
/// Each guarantor is liable for the whole outstanding balance, up to what they guaranteed.
/// </summary>
/// <remarks>
/// <para>
/// This is what the signed application form says: "The undersigned guarantors accept joint
/// and several liability for repayment of this loan in the event of default."
/// </para>
/// <para>
/// Note that the figures here <b>do not sum to the outstanding balance</b>, and are not meant
/// to. Under joint and several liability each guarantor's figure is a ceiling on what they
/// can be pursued for, not a slice of a pie - recovery stops once the balance is met, from
/// whichever guarantors paid.
/// </para>
/// <para>
/// The cap at the guaranteed amount is a reading, not a quotation: the form says "joint and
/// several" without saying whether the guaranteed amount column limits it. Capping is the
/// narrower and fairer interpretation, and it is flagged with the rest of open question 2.
/// </para>
/// </remarks>
public sealed class JointAndSeveralLiability : IGuarantorLiabilityStrategy
{
    public string Description =>
        "Each guarantor is liable for the whole balance, up to the amount they guaranteed";

    public IReadOnlyList<GuarantorLiability> Apportion(
        Money outstandingBalance,
        IReadOnlyList<Guarantee> guarantees)
    {
        var live = GuaranteeRules.Live(guarantees);

        if (live.Count == 0 || !outstandingBalance.IsPositive)
        {
            return [];
        }

        return
        [
            .. live.Select(guarantee => new GuarantorLiability(
                guarantee.GuarantorId,
                guarantee.GuarantorName,
                guarantee.GuaranteedAmount,
                guarantee.GuaranteedAmount < outstandingBalance
                    ? guarantee.GuaranteedAmount
                    : outstandingBalance)),
        ];
    }
}

internal static class GuaranteeRules
{
    /// <summary>The guarantees still in force: everything not released.</summary>
    public static IReadOnlyList<Guarantee> Live(IReadOnlyList<Guarantee> guarantees)
    {
        ArgumentNullException.ThrowIfNull(guarantees);

        return [.. guarantees.Where(guarantee => !guarantee.IsReleased)];
    }
}
