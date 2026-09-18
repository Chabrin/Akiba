using Akiba.Domain.Financial;

namespace Akiba.Domain.Dividends;

/// <summary>
/// One member's shareholding over the dividend year, as read from the ledger.
/// </summary>
/// <param name="MemberId">Whose.</param>
/// <param name="MembershipNumber">Their number, for the schedule.</param>
/// <param name="FullName">Their name, for the schedule.</param>
/// <param name="SharesAccountId">Their share account.</param>
/// <param name="ClosingShareholding">What they held at the year end.</param>
/// <param name="MonthEndShareholdings">
/// What they held at the end of each of the year's twelve months, in order.
/// </param>
public sealed record MemberShareholdingOverYear(
    Guid MemberId,
    string MembershipNumber,
    string FullName,
    Guid SharesAccountId,
    Money ClosingShareholding,
    IReadOnlyList<Money> MonthEndShareholdings);

/// <summary>
/// How a member's share of the dividend is worked out.
/// </summary>
/// <remarks>
/// <para>
/// <b>The committee has not settled this.</b> The only source is "interest earned divided by
/// the total shareholding", which does not say whether a member who joined in November shares
/// equally with one who has been contributing all year. It is open question 11.
/// </para>
/// <para>
/// So it is a strategy with a documented default rather than an assumption baked into a
/// calculation. The run records which basis it used, so a dividend paid in one year stays
/// explainable even if the rule changes in the next.
/// </para>
/// </remarks>
public interface IDividendBasis
{
    /// <summary>What the basis is called, recorded on the run.</summary>
    string Name { get; }

    /// <summary>How it works, in a sentence an official can read on the schedule.</summary>
    string Explanation { get; }

    /// <summary>
    /// The weight each member's dividend is proportional to.
    /// </summary>
    /// <remarks>
    /// Returned as weights rather than as amounts, because the division itself is
    /// <see cref="Money.Allocate(IReadOnlyList{decimal})"/>'s job - that is what makes the
    /// distributed total equal the available total to the cent.
    /// </remarks>
    IReadOnlyList<decimal> Weigh(IReadOnlyList<MemberShareholdingOverYear> members);
}

/// <summary>
/// Shareholding at the year end. <b>The default.</b>
/// </summary>
/// <remarks>
/// The literal reading of the only rule anybody wrote down: interest earned divided by the
/// total shareholding, allocated by shareholding. It is the easier of the two to check by
/// hand against the register, which matters while the committee is still reading the figures
/// on paper beside the screen.
///
/// It is also the more generous of the two to a member who joined in November, which is the
/// reason to ask the committee rather than to choose for them.
/// </remarks>
public sealed class ClosingShareholdingBasis : IDividendBasis
{
    public string Name => "Closing shareholding";

    public string Explanation =>
        "Each member's dividend is in proportion to what they held at the year end.";

    public IReadOnlyList<decimal> Weigh(IReadOnlyList<MemberShareholdingOverYear> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        return [.. members.Select(member => Math.Max(0m, member.ClosingShareholding.Amount))];
    }
}

/// <summary>
/// The average of the twelve month-end shareholdings.
/// </summary>
/// <remarks>
/// <para>
/// A member who contributed all year earns more than one who joined in November, which most
/// people would call the fairer answer. It is computed from month-end positions rather than
/// day by day because that is what an official can check: twelve figures, each of which
/// appears on a statement, averaged.
/// </para>
/// <para>
/// Not the default only because nobody has adopted it. See open question 11.
/// </para>
/// </remarks>
public sealed class TimeWeightedShareholdingBasis : IDividendBasis
{
    public string Name => "Time-weighted shareholding";

    public string Explanation =>
        "Each member's dividend is in proportion to the average of what they held at the end " +
        "of each of the year's twelve months, so a member who contributed all year earns more " +
        "than one who joined in November.";

    public IReadOnlyList<decimal> Weigh(IReadOnlyList<MemberShareholdingOverYear> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        return
        [
            .. members.Select(member => member.MonthEndShareholdings.Count == 0
                ? 0m
                : Math.Max(
                    0m,
                    member.MonthEndShareholdings.Sum(month => month.Amount)
                        / member.MonthEndShareholdings.Count)),
        ];
    }
}
