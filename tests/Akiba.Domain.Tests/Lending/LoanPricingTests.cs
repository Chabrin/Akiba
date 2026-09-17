using Akiba.Domain.Financial;
using Akiba.Domain.Lending;

namespace Akiba.Domain.Tests.Lending;

/// <summary>
/// The figures in these tests come from the paper ledger and the answered questionnaire.
/// They are not illustrations - they are what members quote back at the office, so they must
/// reproduce to the cent.
/// </summary>
public sealed class LoanPricingTests
{
    private readonly LoanPricing _pricing = new();

    [Fact]
    public void The_emergency_loan_from_the_ledger_prices_to_27500_over_five_months()
    {
        // 25,000 + 10% flat = 27,500, repaid at 5,500 a month. This is the single most
        // recognisable figure in the ledger.
        var terms = _pricing.Price(LoanProduct.Emergency, Money.Kes(25_000m));

        terms.Interest.Should().Be(Money.Kes(2_500m));
        terms.TotalRepayable.Should().Be(Money.Kes(27_500m));
        terms.TermMonths.Should().Be(5);
        terms.NominalInstalment.Should().Be(Money.Kes(5_500m));
        terms.TotalRepayable.Allocate(terms.TermMonths)
            .Should().AllBeEquivalentTo(Money.Kes(5_500m));
    }

    [Fact]
    public void A_60000_normal_loan_prices_to_66000_over_twelve_months()
    {
        var terms = _pricing.Price(LoanProduct.Normal, Money.Kes(60_000m));

        terms.Interest.Should().Be(Money.Kes(6_000m));
        terms.TotalRepayable.Should().Be(Money.Kes(66_000m));
        terms.TermMonths.Should().Be(12);
        terms.NominalInstalment.Should().Be(Money.Kes(5_500m));
    }

    [Fact]
    public void A_175000_normal_loan_prices_to_192500_over_twenty_months()
    {
        var terms = _pricing.Price(LoanProduct.Normal, Money.Kes(175_000m));

        terms.Interest.Should().Be(Money.Kes(17_500m));
        terms.TotalRepayable.Should().Be(Money.Kes(192_500m));
        terms.TermMonths.Should().Be(20);
        terms.NominalInstalment.Should().Be(Money.Kes(9_625m));
    }

    [Theory]
    [InlineData(30_000, 8)]
    [InlineData(50_000, 8)]
    [InlineData(50_001, 12)]
    [InlineData(100_000, 12)]
    [InlineData(100_001, 16)]
    [InlineData(150_000, 16)]
    [InlineData(150_001, 20)]
    [InlineData(200_000, 20)]
    [InlineData(200_001, 24)]
    [InlineData(400_000, 24)]
    [InlineData(400_001, 30)]
    [InlineData(1_000_000, 30)]
    public void Every_band_boundary_of_the_graduated_scale_is_where_the_form_says(
        decimal principal, int expectedMonths)
    {
        _pricing.Price(LoanProduct.Normal, Money.Kes(principal)).TermMonths
            .Should().Be(expectedMonths);
    }

    [Fact]
    public void Interest_is_flat_so_it_does_not_change_with_the_term()
    {
        // Deliberate on Akiba's part, not an oversight: the charge is for the loan, not for
        // the time. A 60,000 loan over eight months costs the same 6,000 as over twelve.
        var full = _pricing.Price(LoanProduct.Normal, Money.Kes(60_000m));
        var shortened = _pricing.Price(LoanProduct.Normal, Money.Kes(60_000m), requestedTermMonths: 8);

        shortened.Interest.Should().Be(full.Interest);
        shortened.TermMonths.Should().Be(8);
    }

    [Fact]
    public void A_member_may_ask_for_a_shorter_term_than_the_scale_allows()
    {
        // The form's duration guide is headed "Maximum Repayment Period" and the form asks the
        // applicant to state the period they want, so the scale is a ceiling rather than a
        // fixed term.
        _pricing.Price(LoanProduct.Normal, Money.Kes(60_000m), requestedTermMonths: 6)
            .TermMonths.Should().Be(6);
    }

    [Fact]
    public void A_member_may_not_ask_for_longer_than_the_scale_allows()
    {
        var price = () => _pricing.Price(LoanProduct.Normal, Money.Kes(60_000m), requestedTermMonths: 16);

        price.Should().Throw<ArgumentException>().WithMessage("*at most 12 months*");
    }

    [Fact]
    public void An_emergency_loan_runs_five_months_whatever_the_form_says()
    {
        _pricing.Price(LoanProduct.Emergency, Money.Kes(20_000m), requestedTermMonths: 12)
            .TermMonths.Should().Be(5);
    }

    [Fact]
    public void An_emergency_loan_above_the_maximum_is_refused()
    {
        var price = () => _pricing.Price(LoanProduct.Emergency, Money.Kes(25_000.01m));

        price.Should().Throw<ArgumentException>().WithMessage("*may not exceed KES 25,000.00*");
    }

    [Fact]
    public void A_loan_below_the_bottom_of_the_scale_is_refused_rather_than_guessed_at()
    {
        // The scale starts at 30,000 and no term is defined below it, on the form or in the
        // minutes. See docs/open-questions.md, item 5.
        var price = () => _pricing.Price(LoanProduct.Normal, Money.Kes(20_000m));

        price.Should().Throw<TermNotDefinedException>()
            .WithMessage("*open question 5*");
    }

    [Fact]
    public void A_client_loan_charges_three_percent_a_month_on_the_principal()
    {
        // 80,000 over five months at 3% a month = 12,000 interest, not compounded and not on
        // the reducing balance.
        var terms = _pricing.Price(LoanProduct.Client, Money.Kes(80_000m), requestedTermMonths: 5);

        terms.Interest.Should().Be(Money.Kes(12_000m));
        terms.TotalRepayable.Should().Be(Money.Kes(92_000m));
    }

    [Fact]
    public void A_client_loan_defaults_to_its_five_month_maximum()
    {
        _pricing.Price(LoanProduct.Client, Money.Kes(80_000m)).TermMonths.Should().Be(5);
    }

    [Fact]
    public void A_client_loan_cannot_run_beyond_five_months()
    {
        var price = () => _pricing.Price(LoanProduct.Client, Money.Kes(80_000m), requestedTermMonths: 6);

        price.Should().Throw<ArgumentException>().WithMessage("*at most 5 months*");
    }

    [Fact]
    public void A_client_loan_is_not_governed_by_the_graduated_scale()
    {
        // A client borrowing 20,000 is fine, where a member could not - the scale's 30,000
        // floor applies to member loans only.
        _pricing.Price(LoanProduct.Client, Money.Kes(20_000m), requestedTermMonths: 3)
            .Interest.Should().Be(Money.Kes(1_800m));
    }

    [Fact]
    public void A_rental_income_loan_refuses_to_price_because_nobody_set_its_rate()
    {
        // The form establishes the product exists and what evidence is collected. It does not
        // state the rate. Defaulting to the 10% that normal loans use would be
        // indistinguishable, six months later, from a rate the committee agreed.
        var price = () => _pricing.Price(LoanProduct.RentalIncome, Money.Kes(200_000m));

        price.Should().Throw<InterestRateNotSetException>()
            .WithMessage("*open-questions.md item 7*");
    }

    [Fact]
    public void A_restructured_balance_attracts_no_fresh_interest()
    {
        var terms = _pricing.PriceRestructure(Money.Kes(120_000m));

        terms.Interest.Should().Be(Money.ZeroKes);
        terms.TotalRepayable.Should().Be(Money.Kes(120_000m));
        terms.TermMonths.Should().Be(16, because: "120,000 falls in the 100,001-150,000 band");
    }

    [Fact]
    public void A_loan_records_which_version_of_the_scale_it_was_written_under()
    {
        // When the committee changes a band, existing loans keep the version they were written
        // under. A member's agreed instalment does not change because a rule changed after
        // they signed.
        _pricing.Price(LoanProduct.Normal, Money.Kes(60_000m)).TermScaleVersion
            .Should().Be(GraduatedTermScale.Version1.Version);
    }

    [Fact]
    public void A_zero_or_negative_principal_is_refused()
    {
        var zero = () => _pricing.Price(LoanProduct.Normal, Money.ZeroKes);

        zero.Should().Throw<ArgumentException>().WithMessage("*must be positive*");
    }
}

public sealed class TenureBasedTermsTests
{
    [Fact]
    public void The_tenure_rule_is_off_by_default()
    {
        // Proposed in the minutes, never recorded as adopted - but already printed on the live
        // application form. Built, tested, and off until the committee says otherwise.
        TenureBasedTerms.Disabled.IsEnabled.Should().BeFalse();

        new LoanPricing().Price(LoanProduct.Normal, Money.Kes(500_000m), membershipYears: 12)
            .TermMonths.Should().Be(30, because: "the graduated scale still applies");
    }

    [Theory]
    [InlineData(7, 36)]
    [InlineData(9, 36)]
    [InlineData(10, 40)]
    [InlineData(14, 40)]
    [InlineData(15, 48)]
    [InlineData(30, 48)]
    public void When_enabled_long_standing_members_get_longer_terms_on_large_loans(
        int membershipYears, int expectedMonths)
    {
        var pricing = new LoanPricing(tenureTerms: TenureBasedTerms.Enabled);

        pricing.Price(LoanProduct.Normal, Money.Kes(500_000m), membershipYears)
            .TermMonths.Should().Be(expectedMonths);
    }

    [Fact]
    public void The_tenure_rule_does_not_reach_loans_below_400001()
    {
        var pricing = new LoanPricing(tenureTerms: TenureBasedTerms.Enabled);

        pricing.Price(LoanProduct.Normal, Money.Kes(400_000m), membershipYears: 20)
            .TermMonths.Should().Be(24);
    }

    [Fact]
    public void A_member_of_under_seven_years_gets_the_ordinary_thirty_months()
    {
        var pricing = new LoanPricing(tenureTerms: TenureBasedTerms.Enabled);

        pricing.Price(LoanProduct.Normal, Money.Kes(500_000m), membershipYears: 6)
            .TermMonths.Should().Be(30);
    }
}

public sealed class InterestStrategyTests
{
    [Fact]
    public void A_flat_rate_ignores_the_term()
    {
        var strategy = new FlatRateInterestStrategy(0.10m);

        strategy.InterestOn(Money.Kes(60_000m), 8).Should().Be(Money.Kes(6_000m));
        strategy.InterestOn(Money.Kes(60_000m), 30).Should().Be(Money.Kes(6_000m));
    }

    [Fact]
    public void A_monthly_rate_multiplies_by_the_term()
    {
        var strategy = new MonthlyRateInterestStrategy(0.03m);

        strategy.InterestOn(Money.Kes(100_000m), 5).Should().Be(Money.Kes(15_000m));
    }

    [Fact]
    public void A_restructured_balance_uses_the_no_further_interest_strategy()
    {
        new NoFurtherInterestStrategy().InterestOn(Money.Kes(120_000m), 16)
            .Should().Be(Money.ZeroKes);
    }

    [Fact]
    public void A_negative_rate_is_refused()
    {
        var create = () => new FlatRateInterestStrategy(-0.10m);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }
}
