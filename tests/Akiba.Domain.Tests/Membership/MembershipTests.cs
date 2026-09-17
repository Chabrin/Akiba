using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Domain.Membership;
using Akiba.Domain.Tests.Ledger;

namespace Akiba.Domain.Tests.Membership;

public sealed class MemberTests
{
    [Fact]
    public void A_member_needs_a_share_account_from_the_outset()
    {
        // Their shareholding is that account's balance, so a member without one has no way to
        // have a shareholding at all.
        var join = () => MemberFixture.Join(sharesAccountId: new AccountId(Guid.Empty));

        join.Should().Throw<ArgumentException>()
            .WithMessage("*share account*");
    }

    [Fact]
    public void A_member_belongs_to_a_zone()
    {
        var join = () => MemberFixture.Join(zoneId: new ZoneId(Guid.Empty));

        join.Should().Throw<ArgumentException>()
            .WithMessage("*zone or office*");
    }

    [Fact]
    public void A_new_member_is_employed_and_holds_shares()
    {
        var member = MemberFixture.Join();

        member.IsActive.Should().BeTrue();
        member.HoldsShares.Should().BeTrue();
        member.ExitedOn.Should().BeNull();
    }

    [Fact]
    public void Recording_an_exit_raises_the_event_the_guarantor_rules_depend_on()
    {
        var member = MemberFixture.Join();
        var exitDate = new DateOnly(2026, 9, 30);

        member.ExitEmployment(exitDate, LedgerFixture.Now);

        member.IsActive.Should().BeFalse();
        member.ExitedOn.Should().Be(exitDate);
        member.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<MemberExitedEmployment>()
            .Which.ExitedOn.Should().Be(exitDate);
    }

    [Fact]
    public void A_member_cannot_exit_twice()
    {
        var member = MemberFixture.Join();
        member.ExitEmployment(new DateOnly(2026, 9, 30), LedgerFixture.Now);

        var exitAgain = () => member.ExitEmployment(new DateOnly(2026, 10, 31), LedgerFixture.Now);

        exitAgain.Should().Throw<InvalidOperationException>().WithMessage("*already*");
    }

    [Fact]
    public void A_client_borrower_holds_no_shares()
    {
        var client = ClientBorrower.Register(
            new PersonName("Alice", "Wanjiru"),
            NationalId.Of("28765432"),
            PhoneNumber.Of("0722334455"));

        client.HoldsShares.Should().BeFalse();
    }
}

public sealed class ValueObjectTests
{
    [Theory]
    [InlineData("0712345678")]
    [InlineData("+254712345678")]
    [InlineData("254712345678")]
    [InlineData("712345678")]
    [InlineData("0712 345 678")]
    public void Kenyan_mobile_numbers_normalise_to_one_form(string written)
    {
        // The same number appears as 0712..., +254712... and 254712... on different forms.
        // SMS dispatch needs one of them, so the decision is made on the way in rather than
        // at every send.
        PhoneNumber.Of(written).Value.Should().Be("+254712345678");
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("not a number")]
    [InlineData("")]
    public void An_unrecognisable_phone_number_is_refused(string written)
    {
        var parse = () => PhoneNumber.Of(written);

        parse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_three_part_name_keeps_its_middle_name()
    {
        var name = new PersonName("Grace", "Njeri", "Wambui");

        name.Full.Should().Be("Grace Wambui Njeri");
    }

    [Fact]
    public void Payroll_numbers_are_upper_cased_so_they_match_what_HR_sends()
    {
        PayrollNumber.Of(" cal/0042 ").Value.Should().Be("CAL/0042");
    }
}

public sealed class ShareholdingTests
{
    private readonly Account _shares = LedgerFixture.SharesOf("Grace Njeri", Guid.NewGuid());

    [Fact]
    public void Membership_begins_at_the_first_contribution_not_the_application_letter()
    {
        // The questionnaire is explicit: a member becomes part of Akiba when they make their
        // first contribution in shares.
        JournalEntry[] ledger =
        [
            LedgerFixture.ShareContribution(_shares, 5_500m, new DateOnly(2026, 3, 31)),
            LedgerFixture.ShareContribution(_shares, 5_500m, new DateOnly(2026, 4, 30)),
        ];

        Shareholding.MembershipStartDate(ledger, _shares.Id)
            .Should().Be(new DateOnly(2026, 3, 31));
    }

    [Fact]
    public void A_member_enrolled_but_not_yet_contributing_has_no_start_date()
    {
        // A real state between writing to the chairman and the first payroll run, not an error.
        Shareholding.MembershipStartDate([], _shares.Id).Should().BeNull();
    }

    [Fact]
    public void Membership_length_counts_whole_years_from_the_first_contribution()
    {
        JournalEntry[] ledger =
        [
            LedgerFixture.ShareContribution(_shares, 5_500m, new DateOnly(2016, 6, 30)),
        ];

        Shareholding.MembershipYearsAsAt(ledger, _shares.Id, new DateOnly(2026, 6, 29))
            .Should().Be(9, because: "the tenth anniversary has not been reached");

        Shareholding.MembershipYearsAsAt(ledger, _shares.Id, new DateOnly(2026, 6, 30))
            .Should().Be(10);
    }

    [Fact]
    public void The_borrowing_limit_is_twice_the_shareholding()
    {
        JournalEntry[] ledger =
        [
            LedgerFixture.ShareContribution(_shares, 60_000m, new DateOnly(2026, 1, 31)),
            LedgerFixture.ShareContribution(_shares, 27_500m, new DateOnly(2026, 2, 28)),
        ];

        var shareholding = Shareholding.AsAt(ledger, _shares, new DateOnly(2026, 2, 28));

        shareholding.Should().Be(Money.Kes(87_500m));
        Shareholding.BorrowingLimit(shareholding).Should().Be(Money.Kes(175_000m));
    }

    [Fact]
    public void A_loan_above_twice_the_shareholding_is_outside_the_limit()
    {
        var shareholding = Money.Kes(87_500m);

        Shareholding.IsWithinBorrowingLimit(shareholding, Money.Kes(175_000m)).Should().BeTrue();
        Shareholding.IsWithinBorrowingLimit(shareholding, Money.Kes(175_000.01m)).Should().BeFalse();
    }

    [Fact]
    public void The_uncovered_amount_is_what_the_shares_do_not_reach()
    {
        Shareholding.AmountNotCoveredByShares(Money.Kes(85_000m), Money.Kes(120_000m))
            .Should().Be(Money.Kes(35_000m));
    }

    [Fact]
    public void A_loan_within_the_shareholding_is_fully_covered()
    {
        // Note what this does NOT say: it does not say such a loan needs no guarantors. That
        // rule is confirmed only for emergency loans. See docs/open-questions.md, item 1.
        Shareholding.AmountNotCoveredByShares(Money.Kes(120_000m), Money.Kes(85_000m))
            .Should().Be(Money.ZeroKes);
    }
}

public sealed class AffordabilityTests
{
    [Fact]
    public void Deductions_up_to_two_thirds_of_gross_are_allowed()
    {
        var assessment = Affordability.Assess(
            grossSalary: Money.Kes(90_000m),
            existingDeductions: Money.Kes(5_500m),
            proposedInstalment: Money.Kes(54_500m));

        assessment.MaximumPermittedDeductions.Should().Be(Money.Kes(60_000m));
        assessment.TotalDeductions.Should().Be(Money.Kes(60_000m));
        assessment.IsWithinLimit.Should().BeTrue();
    }

    [Fact]
    public void Deductions_beyond_two_thirds_block_the_application()
    {
        var assessment = Affordability.Assess(
            grossSalary: Money.Kes(90_000m),
            existingDeductions: Money.Kes(5_500m),
            proposedInstalment: Money.Kes(60_000m));

        assessment.IsWithinLimit.Should().BeFalse();
        assessment.ExcessOverLimit.Should().Be(Money.Kes(5_500m));
    }

    [Fact]
    public void The_assessment_says_what_the_member_could_afford_instead()
    {
        // On a breach the two sanctioned remedies are to reduce the loan or reduce the share
        // deduction. This figure is what makes the first one actionable rather than a guess.
        var assessment = Affordability.Assess(
            grossSalary: Money.Kes(60_000m),
            existingDeductions: Money.Kes(10_000m),
            proposedInstalment: Money.Kes(40_000m));

        assessment.LargestAffordableInstalment.Should().Be(Money.Kes(30_000m));
    }

    [Fact]
    public void A_member_already_at_the_ceiling_can_afford_nothing_further()
    {
        var assessment = Affordability.Assess(
            grossSalary: Money.Kes(60_000m),
            existingDeductions: Money.Kes(45_000m),
            proposedInstalment: Money.Kes(1_000m));

        assessment.LargestAffordableInstalment.Should().Be(Money.ZeroKes);
        assessment.IsWithinLimit.Should().BeFalse();
    }

    [Fact]
    public void A_gross_salary_of_zero_is_refused()
    {
        var assess = () => Affordability.Assess(Money.ZeroKes, Money.ZeroKes, Money.Kes(5_500m));

        assess.Should().Throw<ArgumentException>().WithMessage("*Gross salary*");
    }
}

internal static class MemberFixture
{
    public static Member Join(AccountId? sharesAccountId = null, ZoneId? zoneId = null) =>
        Member.Join(
            MembershipNumber.Of("0042"),
            PayrollNumber.Of("CAL/0042"),
            new PersonName("Grace", "Njeri"),
            NationalId.Of("28765432"),
            PhoneNumber.Of("0712345678"),
            "grace.njeri@example.com",
            zoneId ?? ZoneId.New(),
            sharesAccountId ?? AccountId.New());
}
