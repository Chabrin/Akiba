using Akiba.Domain.Financial;

namespace Akiba.Domain.Tests.Financial;

public sealed class MoneyTests
{
    [Fact]
    public void An_amount_keeps_the_precision_it_was_given()
    {
        // Money does not round on construction. A 3%-per-month client loan calculation
        // produces more than two decimal places, and rounding it here and again at posting
        // is how a figure ends up a cent away from anything reproducible.
        var interest = Money.Kes(12_345.6789m);

        interest.Amount.Should().Be(12_345.6789m);
    }

    [Fact]
    public void Money_cannot_be_created_without_a_currency()
    {
        var create = () => new Money(5_500m, default);

        create.Should().Throw<ArgumentException>()
            .WithMessage("*must have a currency*");
    }

    [Fact]
    public void Arithmetic_on_a_default_Money_fails_loudly_rather_than_behaving_as_zero()
    {
        // default(Money) is what an uninitialised field or a skipped constructor produces.
        // Treating it as zero shillings would let the bug that created it pass unnoticed
        // into a balance.
        Money uninitialised = default;

        var add = () => uninitialised + Money.Kes(100m);

        add.Should().Throw<InvalidOperationException>()
            .WithMessage("*default(Money)*");
    }

    [Fact]
    public void Two_currencies_cannot_be_added()
    {
        var shillings = Money.Kes(1_000m);
        var dollars = new Money(1_000m, Currency.Of("USD"));

        var add = () => shillings + dollars;

        add.Should().Throw<CurrencyMismatchException>()
            .WithMessage("*KES*USD*");
    }

    [Fact]
    public void Equality_ignores_trailing_zeros()
    {
        // decimal carries its scale, so 5500m and 5500.00m are distinct representations.
        // They are the same amount of money, and a balance check must treat them as such.
        Money.Kes(5_500m).Should().Be(Money.Kes(5_500.00m));
    }

    [Fact]
    public void Amounts_in_different_currencies_are_never_equal()
    {
        var shillings = Money.Kes(1_000m);
        var dollars = new Money(1_000m, Currency.Of("USD"));

        shillings.Should().NotBe(dollars);
    }

    [Theory]
    [InlineData(25_000, 0.10, 2_500)]        // flat 10% interest on an emergency loan
    [InlineData(60_000, 0.10, 6_000)]        // flat 10% on a normal loan
    [InlineData(175_000, 0.10, 17_500)]      // flat 10% on a normal loan
    [InlineData(120_000, 0.05, 6_000)]       // 5% restructuring fee
    [InlineData(80_000, 0.03, 2_400)]        // 3% per month, client loan
    public void Multiplying_by_a_rate_gives_the_interest_the_ledger_shows(
        decimal principal, decimal rate, decimal expectedInterest)
    {
        (Money.Kes(principal) * rate).Should().Be(Money.Kes(expectedInterest));
    }

    [Fact]
    public void Addition_and_subtraction_behave()
    {
        var principal = Money.Kes(25_000m);
        var interest = Money.Kes(2_500m);

        (principal + interest).Should().Be(Money.Kes(27_500m));
        (principal + interest - Money.Kes(5_500m)).Should().Be(Money.Kes(22_000m));
        (-principal).Should().Be(Money.Kes(-25_000m));
    }

    [Fact]
    public void Amounts_compare_within_a_currency()
    {
        var shares = Money.Kes(85_000m);
        var loanBalance = Money.Kes(120_000m);

        (loanBalance > shares).Should().BeTrue();
        (shares < loanBalance).Should().BeTrue();
        (shares >= Money.Kes(85_000m)).Should().BeTrue();
        loanBalance.CompareTo(shares).Should().BePositive();
    }

    [Fact]
    public void Comparing_across_currencies_is_refused()
    {
        var shillings = Money.Kes(1_000m);
        var dollars = new Money(1m, Currency.Of("USD"));

        var compare = () => shillings > dollars;

        compare.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void Sign_helpers_read_the_way_a_balance_check_reads()
    {
        Money.Kes(0m).IsZero.Should().BeTrue();
        Money.Kes(1m).IsPositive.Should().BeTrue();
        Money.Kes(-1m).IsNegative.Should().BeTrue();
        Money.Kes(-1_500m).Abs().Should().Be(Money.Kes(1_500m));
    }

    [Fact]
    public void Formatting_is_stable_regardless_of_where_it_runs()
    {
        // This string reaches logs, audit records and assertion messages. It has to mean the
        // same thing on the Nairobi machine and on a CI runner in another timezone and
        // locale, so it is invariant. Formatting for a person to read is the UI's job.
        Money.Kes(27_500m).ToString().Should().Be("KES 27,500.00");
        Money.Kes(-9_625.5m).ToString().Should().Be("KES -9,625.50");
    }

    [Fact]
    public void Summing_an_empty_sequence_gives_zero_rather_than_throwing()
    {
        // A member who joined last week has no ledger entries. Their shareholding is zero
        // shillings - not an exception, and not default(Money).
        var noEntries = Array.Empty<Money>();

        noEntries.Sum(Currency.Kes).Should().Be(Money.ZeroKes);
    }

    [Fact]
    public void Summing_adds_every_entry()
    {
        Money[] contributions =
        [
            Money.Kes(5_500m),
            Money.Kes(5_500m),
            Money.Kes(9_680m),
            Money.Kes(9_630m),
        ];

        contributions.Sum(Currency.Kes).Should().Be(Money.Kes(30_310m));
    }
}
