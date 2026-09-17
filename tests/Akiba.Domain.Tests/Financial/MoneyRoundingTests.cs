using Akiba.Domain.Financial;

namespace Akiba.Domain.Tests.Financial;

public sealed class MoneyRoundingTests
{
    [Theory]
    [InlineData(0.005, 0.01)]
    [InlineData(0.015, 0.02)]      // .NET's default ToEven would give 0.02 here too
    [InlineData(0.025, 0.03)]      // ...but ToEven would give 0.02 here. This is the difference.
    [InlineData(2.675, 2.68)]
    [InlineData(-0.005, -0.01)]
    [InlineData(-2.675, -2.68)]
    public void Midpoints_round_away_from_zero(decimal amount, decimal expected)
    {
        Money.Kes(amount).Round().Should().Be(Money.Kes(expected));
    }

    [Fact]
    public void The_policy_is_away_from_zero_and_not_bankers_rounding()
    {
        // Pinned deliberately. Math.Round's default is ToEven, so if someone ever calls it
        // without passing the policy, this test is what tells them.
        MoneyRounding.Policy.Should().Be(MidpointRounding.AwayFromZero);

        var midpoint = 0.025m;
        Math.Round(midpoint, 2).Should().Be(0.02m);                     // .NET default
        MoneyRounding.Round(midpoint, Currency.Kes).Should().Be(0.03m); // Akiba's policy
    }

    [Fact]
    public void Rounding_is_idempotent()
    {
        var once = Money.Kes(9_625.4567m).Round();

        once.Round().Should().Be(once);
    }

    [Fact]
    public void Rounding_leaves_an_already_exact_amount_alone()
    {
        Money.Kes(5_500m).Round().Should().Be(Money.Kes(5_500m));
    }

    [Theory]
    [InlineData(5_500, 550_000)]
    [InlineData(27_500.5, 2_750_050)]
    [InlineData(0.01, 1)]
    [InlineData(-9_625.25, -962_525)]
    public void Amounts_convert_to_whole_cents(decimal amount, decimal expectedCents)
    {
        MoneyRounding.ToMinorUnits(amount, Currency.Kes).Should().Be(expectedCents);
    }

    [Fact]
    public void Minor_units_round_trip()
    {
        var amount = 192_500.75m;

        var cents = MoneyRounding.ToMinorUnits(amount, Currency.Kes);

        MoneyRounding.FromMinorUnits(cents, Currency.Kes).Should().Be(amount);
    }

    [Fact]
    public void The_shilling_has_a_hundred_cents()
    {
        Currency.Kes.DecimalPlaces.Should().Be(2);
        MoneyRounding.MinorUnitFactor(Currency.Kes).Should().Be(100m);
    }

    [Fact]
    public void An_unspecified_currency_cannot_be_rounded()
    {
        var round = () => MoneyRounding.Round(1m, default);

        round.Should().Throw<ArgumentException>()
            .WithMessage("*unspecified currency*");
    }
}
