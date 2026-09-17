using Akiba.Domain.Financial;
using Akiba.Domain.Lending;

namespace Akiba.Domain.Receipting;

/// <summary>
/// A loan competing for a partial deduction, with what it is owed this month.
/// </summary>
/// <param name="LoanId">The loan.</param>
/// <param name="LoanNumber">Its number.</param>
/// <param name="Product">Which product it is.</param>
/// <param name="DisbursedOn">When it started. Used by the oldest-first rule.</param>
/// <param name="InstalmentDue">The instalment falling due.</param>
public sealed record CompetingLoan(
    LoanId LoanId,
    string LoanNumber,
    LoanProduct Product,
    DateOnly DisbursedOn,
    Money InstalmentDue);

/// <summary>What a proposed allocation would put against one loan.</summary>
public sealed record ProposedLoanAllocation(CompetingLoan Loan, Money Amount)
{
    public Money Shortfall => Loan.InstalmentDue - Amount;

    public bool IsFullyCovered => Shortfall.IsZero || Shortfall.IsNegative;
}

/// <summary>
/// Decides which loan gets paid first when a member holds two and the deduction only covers
/// part of what is due.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is an open question and the code does not settle it.</b> The questionnaire asked
/// exactly this - "If a member has more than one running loan and only part of the deduction
/// can be made, which loan is paid first?" - and the answer that came back described loan
/// restructuring, which is a different thing. So there is no rule, only a default.
/// </para>
/// <para>
/// The default is <see cref="OldestFirst"/>. It is the least surprising choice and the one
/// that clears a member's obligations in the order they took them on. It is a strategy, not a
/// constant, precisely so that the committee's answer is a one-line change.
/// See docs/open-questions.md, item 3.
/// </para>
/// <para>
/// A member cannot in practice miss a payment, since deduction is at source - so this bites
/// mainly where the two-thirds rule has squeezed the deduction, and for exited staff and
/// client borrowers paying directly.
/// </para>
/// </remarks>
public interface IPaymentAllocationOrder
{
    /// <summary>How the rule is described to an official.</summary>
    string Description { get; }

    /// <summary>
    /// Splits what was actually received across the loans that are due.
    /// </summary>
    /// <param name="available">What came in, after any share contribution.</param>
    /// <param name="loans">The loans with an instalment due.</param>
    IReadOnlyList<ProposedLoanAllocation> Apportion(
        Money available,
        IReadOnlyList<CompetingLoan> loans);
}

/// <summary>
/// Pays the loan taken out earliest first, in full, before anything goes to the next.
/// </summary>
/// <remarks>
/// TODO: confirm with committee - open question 3. This is a default, not a rule.
/// </remarks>
public sealed class OldestFirst : IPaymentAllocationOrder
{
    public string Description =>
        "The oldest running loan is paid first, in full, before anything goes to the next " +
        "(default - not confirmed by the committee)";

    public IReadOnlyList<ProposedLoanAllocation> Apportion(
        Money available,
        IReadOnlyList<CompetingLoan> loans)
    {
        ArgumentNullException.ThrowIfNull(loans);

        if (available.IsNegative)
        {
            throw new ArgumentException("A receipt cannot be negative.", nameof(available));
        }

        var ordered = loans
            .OrderBy(loan => loan.DisbursedOn)
            .ThenBy(loan => loan.LoanNumber, StringComparer.Ordinal)
            .ToList();

        var remaining = available;
        var proposals = new List<ProposedLoanAllocation>(ordered.Count);

        foreach (var loan in ordered)
        {
            var applied = remaining < loan.InstalmentDue ? remaining : loan.InstalmentDue;

            proposals.Add(new ProposedLoanAllocation(loan, applied));
            remaining -= applied;
        }

        return proposals;
    }
}

/// <summary>
/// Clears the emergency loan before the normal one.
/// </summary>
/// <remarks>
/// Offered as an alternative because an emergency loan is short - five months - and letting
/// it run late while a twenty-month loan is serviced would be an odd result. Not the default,
/// and not a rule either. See docs/open-questions.md, item 3.
/// </remarks>
public sealed class EmergencyLoanFirst : IPaymentAllocationOrder
{
    public string Description =>
        "The emergency loan is cleared before the normal loan (alternative - not confirmed " +
        "by the committee)";

    public IReadOnlyList<ProposedLoanAllocation> Apportion(
        Money available,
        IReadOnlyList<CompetingLoan> loans)
    {
        ArgumentNullException.ThrowIfNull(loans);

        var ordered = loans
            .OrderByDescending(loan => loan.Product == LoanProduct.Emergency)
            .ThenBy(loan => loan.DisbursedOn)
            .ThenBy(loan => loan.LoanNumber, StringComparer.Ordinal)
            .ToList();

        return new OldestFirst().Apportion(available, [.. ordered.Select((loan, index) =>
            loan with { DisbursedOn = DateOnly.MinValue.AddDays(index) })])
            .Select((proposal, index) => new ProposedLoanAllocation(ordered[index], proposal.Amount))
            .ToList();
    }
}

/// <summary>
/// Splits what is available across the loans in proportion to what each is owed.
/// </summary>
/// <remarks>
/// Offered because it is the third obvious reading of "which loan is paid first" - namely,
/// neither. Uses <see cref="Money.Allocate(IReadOnlyList{decimal})"/> so the split is exact.
/// Not the default. See docs/open-questions.md, item 3.
/// </remarks>
public sealed class ProRataAcrossLoans : IPaymentAllocationOrder
{
    public string Description =>
        "What is available is split across the loans in proportion to what each is owed " +
        "(alternative - not confirmed by the committee)";

    public IReadOnlyList<ProposedLoanAllocation> Apportion(
        Money available,
        IReadOnlyList<CompetingLoan> loans)
    {
        ArgumentNullException.ThrowIfNull(loans);

        if (loans.Count == 0)
        {
            return [];
        }

        var totalDue = loans.Sum(loan => loan.InstalmentDue, available.Currency);

        // Where there is enough for everybody, nobody needs a proportion of anything.
        if (available >= totalDue)
        {
            return [.. loans.Select(loan => new ProposedLoanAllocation(loan, loan.InstalmentDue))];
        }

        var shares = available.Allocate([.. loans.Select(loan => loan.InstalmentDue.Amount)]);

        return [.. loans.Select((loan, index) => new ProposedLoanAllocation(loan, shares[index]))];
    }
}
