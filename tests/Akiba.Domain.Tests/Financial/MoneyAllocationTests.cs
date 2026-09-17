using Akiba.Domain.Financial;

namespace Akiba.Domain.Tests.Financial;

/// <summary>
/// Example-based tests for <see cref="Money.Allocate(int)"/> and its weighted overload,
/// using figures taken from the existing paper ledger and from the worked examples in the
/// build brief. The general case is proved separately in
/// <see cref="MoneyAllocationProperties"/>.
/// </summary>
public sealed class MoneyAllocationTests
{
    // -----------------------------------------------------------------
    // Even splits: real loans from the ledger
    // -----------------------------------------------------------------

    [Fact]
    public void The_emergency_loan_from_the_ledger_splits_into_five_instalments_of_5500()
    {
        // 25,000 principal + 10% flat = 27,500 over 5 months. The ledger shows 5,500 per
        // month, and this is the figure members quote back at the office, so it must
        // reproduce to the cent.
        var repayable = Money.Kes(27_500m);

        var instalments = repayable.Allocate(5);

        instalments.Should().AllBeEquivalentTo(Money.Kes(5_500m));
        instalments.Sum(Currency.Kes).Should().Be(repayable);
    }

    [Fact]
    public void A_60000_normal_loan_splits_into_twelve_instalments_of_5500()
    {
        var repayable = Money.Kes(66_000m); // 60,000 + 10% flat

        var instalments = repayable.Allocate(12);

        instalments.Should().HaveCount(12).And.AllBeEquivalentTo(Money.Kes(5_500m));
        instalments.Sum(Currency.Kes).Should().Be(repayable);
    }

    [Fact]
    public void A_175000_normal_loan_splits_into_twenty_instalments_of_9625()
    {
        var repayable = Money.Kes(192_500m); // 175,000 + 10% flat

        var instalments = repayable.Allocate(20);

        instalments.Should().HaveCount(20).And.AllBeEquivalentTo(Money.Kes(9_625m));
        instalments.Sum(Currency.Kes).Should().Be(repayable);
    }

    // -----------------------------------------------------------------
    // Uneven splits: where the cents go
    // -----------------------------------------------------------------

    [Fact]
    public void One_hundred_shillings_split_three_ways_does_not_lose_a_cent()
    {
        // The canonical failure. Naive rounding gives 33.33 three times, which is 99.99 -
        // the missing cent unbalances the journal entry and its constructor rejects it.
        var allocation = Money.Kes(100m).Allocate(3);

        allocation.Should().Equal(Money.Kes(33.34m), Money.Kes(33.33m), Money.Kes(33.33m));
        allocation.Sum(Currency.Kes).Should().Be(Money.Kes(100m));
    }

    [Fact]
    public void A_55000_loan_over_twelve_months_puts_the_odd_cents_on_the_earliest_instalments()
    {
        // 55,000 / 12 = 4,583.3333... The four extra cents land on the first four months,
        // which are the ones closest to the disbursement and the easiest for a member to
        // check against their payslip.
        var instalments = Money.Kes(55_000m).Allocate(12);

        instalments.Take(4).Should().AllBeEquivalentTo(Money.Kes(4_583.34m));
        instalments.Skip(4).Should().AllBeEquivalentTo(Money.Kes(4_583.33m));
        instalments.Sum(Currency.Kes).Should().Be(Money.Kes(55_000m));
    }

    [Fact]
    public void Instalments_never_differ_by_more_than_one_cent()
    {
        var instalments = Money.Kes(100_000m).Allocate(30);

        var largest = instalments.Max(instalment => instalment.Amount);
        var smallest = instalments.Min(instalment => instalment.Amount);

        (largest - smallest).Should().Be(0.01m);
    }

    [Fact]
    public void A_negative_amount_splits_symmetrically()
    {
        // Reversing entries carry negative amounts, and a reversal has to undo the original
        // allocation exactly - cent for cent, in the same places.
        var reversal = Money.Kes(-100m).Allocate(3);

        reversal.Should().Equal(Money.Kes(-33.34m), Money.Kes(-33.33m), Money.Kes(-33.33m));
        reversal.Sum(Currency.Kes).Should().Be(Money.Kes(-100m));
    }

    [Fact]
    public void Splitting_into_one_part_returns_the_whole_amount()
    {
        Money.Kes(27_500m).Allocate(1).Should().Equal(Money.Kes(27_500m));
    }

    [Fact]
    public void Splitting_zero_gives_zeros()
    {
        Money.ZeroKes.Allocate(4).Should().AllBeEquivalentTo(Money.ZeroKes);
    }

    [Fact]
    public void Splitting_into_fewer_than_one_part_is_refused()
    {
        var split = () => Money.Kes(100m).Allocate(0);

        split.Should().Throw<ArgumentOutOfRangeException>();
    }

    // -----------------------------------------------------------------
    // Weighted splits: guarantor liability
    // -----------------------------------------------------------------

    [Fact]
    public void Guarantor_liability_follows_the_share_each_one_guaranteed()
    {
        // The worked example from the questionnaire: a guarantor who guaranteed 50,000 of a
        // 200,000 total is liable for 0.25 of the outstanding balance.
        var outstanding = Money.Kes(120_000m);
        decimal[] guaranteed = [50_000m, 150_000m];

        var liability = outstanding.Allocate(guaranteed);

        liability.Should().Equal(Money.Kes(30_000m), Money.Kes(90_000m));
        liability[0].Should().Be(outstanding * 0.25m);
        liability.Sum(Currency.Kes).Should().Be(outstanding);
    }

    [Fact]
    public void Guarantor_liability_that_does_not_divide_cleanly_still_sums_to_the_balance()
    {
        var outstanding = Money.Kes(100m);
        decimal[] equalShares = [1m, 1m, 1m];

        var liability = outstanding.Allocate(equalShares);

        liability.Should().Equal(Money.Kes(33.34m), Money.Kes(33.33m), Money.Kes(33.33m));
        liability.Sum(Currency.Kes).Should().Be(outstanding);
    }

    [Fact]
    public void A_guarantor_who_guaranteed_nothing_bears_nothing()
    {
        var liability = Money.Kes(90_000m).Allocate([60_000m, 0m, 30_000m]);

        liability.Should().Equal(Money.Kes(60_000m), Money.ZeroKes, Money.Kes(30_000m));
    }

    // -----------------------------------------------------------------
    // Weighted splits: dividends
    // -----------------------------------------------------------------

    [Fact]
    public void Dividends_allocated_by_shareholding_distribute_the_whole_pot()
    {
        // Interest earned, net of bank charges, divided by total shareholding and allocated
        // by shareholding. What must hold is that the total distributed equals the total
        // available exactly - otherwise the dividend entry does not balance.
        var distributable = Money.Kes(47_512.37m);
        decimal[] shareholdings = [120_000m, 85_000m, 45_000m, 33_500m, 17_250m];

        var dividends = distributable.Allocate(shareholdings);

        dividends.Sum(Currency.Kes).Should().Be(distributable);
        dividends.Should().BeInDescendingOrder(dividend => dividend.Amount);
    }

    [Fact]
    public void A_dividend_run_gives_the_same_answer_every_time_it_is_computed()
    {
        // The treasurer reviews a draft and the chairman approves it, which may be days
        // apart. Recomputing must produce the identical schedule or there is nothing
        // meaningful to approve.
        var distributable = Money.Kes(19_999.99m);
        decimal[] shareholdings = [7_000m, 7_000m, 7_000m, 1m];

        var first = distributable.Allocate(shareholdings);
        var second = distributable.Allocate(shareholdings);

        second.Should().Equal(first);
    }

    [Fact]
    public void Ties_in_the_leftover_cents_go_to_the_earliest_member()
    {
        // Three identical shareholdings and one spare cent. Somebody has to get it, and the
        // rule is "the earliest", not "whichever the sort happened to put first".
        var dividends = Money.Kes(0.10m).Allocate([1m, 1m, 1m]);

        dividends.Should().Equal(Money.Kes(0.04m), Money.Kes(0.03m), Money.Kes(0.03m));
    }

    [Fact]
    public void Weights_that_sum_to_zero_are_refused()
    {
        var allocate = () => Money.Kes(100m).Allocate([0m, 0m]);

        allocate.Should().Throw<ArgumentException>()
            .WithMessage("*sum to zero*");
    }

    [Fact]
    public void Negative_weights_are_refused()
    {
        var allocate = () => Money.Kes(100m).Allocate([50m, -10m]);

        allocate.Should().Throw<ArgumentException>()
            .WithMessage("*cannot be negative*");
    }

    [Fact]
    public void An_empty_weight_list_is_refused()
    {
        var allocate = () => Money.Kes(100m).Allocate(Array.Empty<decimal>());

        allocate.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_allocation_stays_in_the_currency_it_started_in()
    {
        Money.Kes(100m).Allocate(3).Should().OnlyContain(part => part.Currency == Currency.Kes);
        Money.Kes(100m).Allocate([1m, 2m]).Should().OnlyContain(part => part.Currency == Currency.Kes);
    }
}
