using Akiba.Domain.Financial;
using FsCheck.Xunit;

namespace Akiba.Domain.Tests.Financial;

/// <summary>
/// Property-based proof that <see cref="Money.Allocate(int)"/> never loses or invents a
/// cent.
/// </summary>
/// <remarks>
/// <para>
/// Example tests cover the cases somebody thought of. The failure mode of an allocation
/// routine is the case nobody thought of - a particular amount against a particular number
/// of parts where the remainder arithmetic goes one cent astray. So the invariant is
/// asserted across hundreds of generated inputs instead: for ANY amount and ANY number of
/// parts, the parts sum exactly to the whole.
/// </para>
/// <para>
/// If one of these ever fails, FsCheck shrinks the counterexample to the smallest amount and
/// part count that still breaks it, and that pair goes straight into
/// <see cref="MoneyAllocationTests"/> as a permanent regression test.
/// </para>
/// </remarks>
[Properties(MaxTest = 500)]
public sealed class MoneyAllocationProperties
{
    [Property]
    public bool Equal_parts_always_sum_to_the_whole(decimal rawAmount, int rawParts)
    {
        var amount = AnAmount(rawAmount);
        var parts = APartCount(rawParts);

        var allocation = amount.Allocate(parts);

        return allocation.Sum(Currency.Kes) == amount.Round();
    }

    [Property]
    public bool Equal_parts_never_differ_by_more_than_one_cent(decimal rawAmount, int rawParts)
    {
        var allocation = AnAmount(rawAmount).Allocate(APartCount(rawParts));

        var largest = allocation.Max(part => part.Amount);
        var smallest = allocation.Min(part => part.Amount);

        return largest - smallest <= 0.01m;
    }

    [Property]
    public bool Equal_parts_come_back_in_the_currency_they_went_in(decimal rawAmount, int rawParts)
    {
        var parts = APartCount(rawParts);

        var allocation = AnAmount(rawAmount).Allocate(parts);

        return allocation.Count == parts
            && allocation.All(part => part.Currency == Currency.Kes);
    }

    [Property]
    public bool Splitting_into_one_part_is_just_rounding(decimal rawAmount)
    {
        var amount = AnAmount(rawAmount);

        return amount.Allocate(1).Single() == amount.Round();
    }

    [Property]
    public bool Weighted_parts_always_sum_to_the_whole(decimal rawAmount, int[] rawWeights)
    {
        var amount = AnAmount(rawAmount);
        var weights = SomeWeights(rawWeights);

        var allocation = amount.Allocate(weights);

        return allocation.Sum(Currency.Kes) == amount.Round();
    }

    [Property]
    public bool A_weighted_allocation_gives_the_same_answer_twice(decimal rawAmount, int[] rawWeights)
    {
        var amount = AnAmount(rawAmount);
        var weights = SomeWeights(rawWeights);

        return amount.Allocate(weights).SequenceEqual(amount.Allocate(weights));
    }

    [Property]
    public bool A_zero_weight_never_receives_a_cent(decimal rawAmount, int[] rawWeights)
    {
        var weights = SomeWeights(rawWeights);

        // Guarantee at least one zero weight is present to make the property meaningful.
        var withAZero = weights.Append(0m).ToList();

        var allocation = AnAmount(rawAmount).Allocate(withAZero);

        return allocation[^1].IsZero;
    }

    [Property]
    public bool Rounding_twice_is_the_same_as_rounding_once(decimal rawAmount)
    {
        var rounded = AnAmount(rawAmount).Round();

        return rounded.Round() == rounded;
    }

    // -----------------------------------------------------------------
    // Input shaping
    //
    // FsCheck generates the whole range of decimal and int, including values that are not
    // money and counts that are not instalments. These map the generated values onto the
    // domain rather than discarding them, so every generated case still exercises the
    // invariant.
    // -----------------------------------------------------------------

    /// <summary>
    /// An amount within the range Akiba actually transacts in - the largest real loan is in
    /// the hundreds of thousands - at posting precision.
    /// </summary>
    private static Money AnAmount(decimal raw) =>
        Money.Kes(Math.Round(raw % 10_000_000m, 2, MidpointRounding.AwayFromZero));

    /// <summary>
    /// A plausible number of instalments. The longest term in the graduated scale is 30
    /// months, and the proposed tenure-based terms go to 48.
    /// </summary>
    /// <remarks>
    /// <c>raw % 60</c> before <see cref="Math.Abs(int)"/> on purpose: <c>Math.Abs</c> throws
    /// on <see cref="int.MinValue"/>, and FsCheck will generate it.
    /// </remarks>
    private static int APartCount(int raw) => Math.Abs(raw % 60) + 1;

    /// <summary>
    /// A set of non-negative weights with at least one positive value - shareholdings, or
    /// amounts guaranteed.
    /// </summary>
    private static List<decimal> SomeWeights(int[]? raw)
    {
        var weights = (raw ?? [])
            .Select(weight => (decimal)Math.Abs(weight % 1_000_000))
            .ToList();

        // Degenerate inputs - no weights at all, or every weight zero - are rejected by
        // Allocate by design and are covered by example tests instead.
        if (weights.Count == 0 || weights.Sum() == 0m)
        {
            weights = [1m];
        }

        return weights;
    }
}
