using Akiba.Domain.Financial;
using Akiba.Domain.Guaranteeing;
using Akiba.Domain.Lending;
using Akiba.Domain.Membership;

namespace Akiba.Domain.Tests.Guaranteeing;

public sealed class GuarantorLiabilityTests
{
    [Fact]
    public void The_worked_example_from_the_questionnaire_reproduces_exactly()
    {
        // "Where one had guaranteed 50,000/= out of a total guaranteed amount of 200,000/=,
        // the guarantor is only liable to the extent of 0.25 of the outstanding balance."
        var guarantees = new[]
        {
            GuarantorFixture.Guarantee("Peter Mwangi", 50_000m),
            GuarantorFixture.Guarantee("Grace Njeri", 150_000m),
        };

        var liability = new ProRataLiability().Apportion(Money.Kes(120_000m), guarantees);

        liability[0].AmountLiable.Should().Be(Money.Kes(30_000m));
        liability[0].AmountLiable.Should().Be(Money.Kes(120_000m) * 0.25m);
        liability[1].AmountLiable.Should().Be(Money.Kes(90_000m));
    }

    [Fact]
    public void Pro_rata_liabilities_sum_to_the_outstanding_balance_to_the_cent()
    {
        // A lost cent here is a member being pursued for the wrong figure.
        var guarantees = new[]
        {
            GuarantorFixture.Guarantee("Peter Mwangi", 30_000m),
            GuarantorFixture.Guarantee("Grace Njeri", 30_000m),
            GuarantorFixture.Guarantee("Alice Wanjiru", 30_000m),
        };

        var liability = new ProRataLiability().Apportion(Money.Kes(100m), guarantees);

        liability.Select(l => l.AmountLiable).Sum(Currency.Kes).Should().Be(Money.Kes(100m));
        liability.Select(l => l.AmountLiable).Should()
            .Equal(Money.Kes(33.34m), Money.Kes(33.33m), Money.Kes(33.33m));
    }

    [Fact]
    public void Under_joint_and_several_each_guarantor_faces_the_whole_balance()
    {
        // What the signed form says. Note these figures do NOT sum to the balance, and are not
        // meant to: each is a ceiling on what that guarantor can be pursued for, not a slice.
        var guarantees = new[]
        {
            GuarantorFixture.Guarantee("Peter Mwangi", 150_000m),
            GuarantorFixture.Guarantee("Grace Njeri", 150_000m),
        };

        var liability = new JointAndSeveralLiability().Apportion(Money.Kes(120_000m), guarantees);

        liability.Should().OnlyContain(l => l.AmountLiable == Money.Kes(120_000m));
    }

    [Fact]
    public void Joint_and_several_liability_is_capped_at_what_each_one_guaranteed()
    {
        var guarantees = new[] { GuarantorFixture.Guarantee("Peter Mwangi", 50_000m) };

        var liability = new JointAndSeveralLiability().Apportion(Money.Kes(120_000m), guarantees);

        liability[0].AmountLiable.Should().Be(Money.Kes(50_000m));
    }

    [Fact]
    public void The_two_bases_give_materially_different_answers_which_is_why_it_must_be_settled()
    {
        // This test exists to make the open contradiction visible rather than to assert a
        // preference. See docs/open-questions.md, item 2.
        var guarantees = new[]
        {
            GuarantorFixture.Guarantee("Peter Mwangi", 50_000m),
            GuarantorFixture.Guarantee("Grace Njeri", 150_000m),
        };
        var outstanding = Money.Kes(120_000m);

        var proRata = new ProRataLiability().Apportion(outstanding, guarantees);
        var jointAndSeveral = new JointAndSeveralLiability().Apportion(outstanding, guarantees);

        proRata[0].AmountLiable.Should().Be(Money.Kes(30_000m));
        jointAndSeveral[0].AmountLiable.Should().Be(Money.Kes(50_000m));
    }

    [Fact]
    public void A_released_guarantee_bears_nothing()
    {
        var guarantees = new[]
        {
            GuarantorFixture.Guarantee("Peter Mwangi", 50_000m) with { IsReleased = true },
            GuarantorFixture.Guarantee("Grace Njeri", 150_000m),
        };

        var liability = new ProRataLiability().Apportion(Money.Kes(120_000m), guarantees);

        liability.Should().ContainSingle()
            .Which.AmountLiable.Should().Be(Money.Kes(120_000m));
    }

    [Fact]
    public void A_cleared_loan_produces_no_liability()
    {
        var guarantees = new[] { GuarantorFixture.Guarantee("Peter Mwangi", 50_000m) };

        new ProRataLiability().Apportion(Money.ZeroKes, guarantees).Should().BeEmpty();
    }

    [Fact]
    public void A_guarantee_for_nothing_is_refused()
    {
        var create = () => GuarantorFixture.Guarantee("Peter Mwangi", 0m);

        create.Should().Throw<ArgumentException>()
            .WithMessage("*is not a guarantor*");
    }
}

public sealed class GuarantorRequirementTests
{
    [Fact]
    public void An_emergency_loan_is_unguaranteed_when_the_normal_loan_is_within_shares()
    {
        // The settled rule, stated directly in the questionnaire.
        var requirement = GuarantorRequirementPolicy.For(
            LoanProduct.Emergency,
            loanAmount: Money.Kes(27_500m),
            borrowerShareholding: Money.Kes(85_000m),
            existingNormalLoanBalance: Money.Kes(60_000m));

        requirement.AreGuarantorsRequired.Should().BeFalse();
        requirement.IsSettledRule.Should().BeTrue();
    }

    [Fact]
    public void An_emergency_loan_needs_guarantors_when_the_normal_loan_exceeds_shares()
    {
        var requirement = GuarantorRequirementPolicy.For(
            LoanProduct.Emergency,
            loanAmount: Money.Kes(27_500m),
            borrowerShareholding: Money.Kes(60_000m),
            existingNormalLoanBalance: Money.Kes(85_000m));

        requirement.AreGuarantorsRequired.Should().BeTrue();
        requirement.IsSettledRule.Should().BeTrue();
        requirement.Reason.Should().Contain("exceeds their shareholding");
    }

    [Fact]
    public void A_normal_loan_fully_covered_by_shares_still_requires_a_guarantor_but_says_so_is_unconfirmed()
    {
        // The conservative default. Nothing in any source document says a share-covered normal
        // loan may go unguaranteed, and the form has eighteen guarantor rows.
        // See docs/open-questions.md, item 1.
        var requirement = GuarantorRequirementPolicy.For(
            LoanProduct.Normal,
            loanAmount: Money.Kes(66_000m),
            borrowerShareholding: Money.Kes(120_000m),
            existingNormalLoanBalance: Money.ZeroKes);

        requirement.AreGuarantorsRequired.Should().BeTrue();
        requirement.AmountNotCoveredByOwnShares.Should().Be(Money.ZeroKes);
        requirement.IsSettledRule.Should().BeFalse(
            because: "the committee has not confirmed this, and an approver should see that");
        requirement.Reason.Should().Contain("has not confirmed");
    }

    [Fact]
    public void A_normal_loan_beyond_the_members_shares_requires_guarantors_on_a_settled_basis()
    {
        var requirement = GuarantorRequirementPolicy.For(
            LoanProduct.Normal,
            loanAmount: Money.Kes(192_500m),
            borrowerShareholding: Money.Kes(120_000m),
            existingNormalLoanBalance: Money.ZeroKes);

        requirement.AreGuarantorsRequired.Should().BeTrue();
        requirement.AmountNotCoveredByOwnShares.Should().Be(Money.Kes(72_500m));
        requirement.IsSettledRule.Should().BeTrue();
    }
}

public sealed class GuarantorCoverageTests
{
    [Fact]
    public void Coverage_answers_the_question_the_form_asks()
    {
        // "Do guarantors sufficiently cover the loan? (Yes / No)"
        var guarantees = new[]
        {
            GuarantorFixture.Guarantee("Peter Mwangi", 50_000m),
            GuarantorFixture.Guarantee("Grace Njeri", 150_000m),
        };

        var coverage = GuarantorCoverage.Of(guarantees, Money.Kes(192_500m));

        coverage.TotalGuaranteed.Should().Be(Money.Kes(200_000m));
        coverage.IsSufficient.Should().BeTrue();
        coverage.Shortfall.Should().Be(Money.ZeroKes);
    }

    [Fact]
    public void A_shortfall_is_reported_rather_than_rounded_away()
    {
        var guarantees = new[] { GuarantorFixture.Guarantee("Peter Mwangi", 50_000m) };

        var coverage = GuarantorCoverage.Of(guarantees, Money.Kes(192_500m));

        coverage.IsSufficient.Should().BeFalse();
        coverage.Shortfall.Should().Be(Money.Kes(142_500m));
    }

    [Fact]
    public void An_application_with_no_guarantors_covers_nothing()
    {
        var coverage = GuarantorCoverage.Of([], Money.Kes(66_000m));

        coverage.TotalGuaranteed.Should().Be(Money.ZeroKes);
        coverage.Shortfall.Should().Be(Money.Kes(66_000m));
    }
}

public sealed class GuarantorExposureTests
{
    [Fact]
    public void Exposure_totals_what_a_member_has_put_their_name_to_across_every_loan()
    {
        var exposure = GuarantorExposureReport.For(
            BorrowerId.New(),
            "Peter Mwangi",
            [
                GuarantorFixture.Exposure("AKB-2026-0007", guaranteed: 50_000m, outstanding: 120_000m, borrowerShares: 40_000m, atRisk: 30_000m),
                GuarantorFixture.Exposure("AKB-2026-0011", guaranteed: 80_000m, outstanding: 60_000m, borrowerShares: 90_000m, atRisk: 20_000m),
            ]);

        exposure.TotalGuaranteed.Should().Be(Money.Kes(130_000m));
        exposure.TotalAtRisk.Should().Be(Money.Kes(50_000m));
    }

    [Fact]
    public void A_loan_within_the_borrowers_own_shares_does_not_block_release()
    {
        var exposure = GuarantorExposureReport.For(
            BorrowerId.New(),
            "Peter Mwangi",
            [
                GuarantorFixture.Exposure("AKB-2026-0011", guaranteed: 80_000m, outstanding: 60_000m, borrowerShares: 90_000m, atRisk: 20_000m),
            ]);

        exposure.FundsMayBeReleased.Should().BeTrue();
        exposure.LoansBlockingRelease.Should().BeEmpty();
    }

    [Fact]
    public void An_exit_holds_the_members_funds_where_a_borrowers_shares_fall_short()
    {
        // "Until such a replacement is found, we do not release funds to the exiting member."
        var exposure = GuarantorExposureReport.For(
            BorrowerId.New(),
            "Peter Mwangi",
            [
                GuarantorFixture.Exposure("AKB-2026-0007", guaranteed: 50_000m, outstanding: 120_000m, borrowerShares: 40_000m, atRisk: 30_000m),
                GuarantorFixture.Exposure("AKB-2026-0011", guaranteed: 80_000m, outstanding: 60_000m, borrowerShares: 90_000m, atRisk: 20_000m),
            ]);

        var review = GuarantorExposureReport.ReviewExit(exposure, new DateOnly(2026, 9, 30));

        review.FundsMayBeReleased.Should().BeFalse();
        review.LoansNeedingReplacement.Should().ContainSingle()
            .Which.LoanNumber.Should().Be("AKB-2026-0007");
        review.ClerkTask.Should().Contain("Do not release this member's funds");
    }

    [Fact]
    public void An_exit_with_every_loan_covered_releases_the_funds()
    {
        var exposure = GuarantorExposureReport.For(
            BorrowerId.New(),
            "Peter Mwangi",
            [
                GuarantorFixture.Exposure("AKB-2026-0011", guaranteed: 80_000m, outstanding: 60_000m, borrowerShares: 90_000m, atRisk: 20_000m),
            ]);

        var review = GuarantorExposureReport.ReviewExit(exposure, new DateOnly(2026, 9, 30));

        review.FundsMayBeReleased.Should().BeTrue();
        review.ClerkTask.Should().Contain("may be released");
    }

    [Fact]
    public void Every_guaranteed_loan_is_flagged_for_review_not_just_the_blocking_ones()
    {
        // A guarantor must be a current CAL employee, so an exit puts every loan they
        // guarantee in question even where no replacement is demanded.
        var exposure = GuarantorExposureReport.For(
            BorrowerId.New(),
            "Peter Mwangi",
            [
                GuarantorFixture.Exposure("AKB-2026-0007", guaranteed: 50_000m, outstanding: 120_000m, borrowerShares: 40_000m, atRisk: 30_000m),
                GuarantorFixture.Exposure("AKB-2026-0011", guaranteed: 80_000m, outstanding: 60_000m, borrowerShares: 90_000m, atRisk: 20_000m),
            ]);

        var review = GuarantorExposureReport.ReviewExit(exposure, new DateOnly(2026, 9, 30));

        review.AllGuaranteedLoans.Should().HaveCount(2);
    }
}

internal static class GuarantorFixture
{
    public static Guarantee Guarantee(string name, decimal guaranteed) =>
        new(
            BorrowerId.New(),
            name,
            PayrollNumber.Of("CAL/0099"),
            Money.Kes(guaranteed),
            Money.Kes(guaranteed * 2m),
            new DateOnly(2026, 9, 15));

    public static GuaranteedLoanExposure Exposure(
        string loanNumber,
        decimal guaranteed,
        decimal outstanding,
        decimal borrowerShares,
        decimal atRisk,
        bool inArrears = false) =>
        new(
            Guid.NewGuid(),
            loanNumber,
            "Grace Njeri",
            Money.Kes(guaranteed),
            Money.Kes(outstanding),
            Money.Kes(borrowerShares),
            Money.Kes(atRisk),
            inArrears);
}
