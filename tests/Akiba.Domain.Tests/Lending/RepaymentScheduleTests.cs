using Akiba.Domain.Financial;
using Akiba.Domain.Lending;

namespace Akiba.Domain.Tests.Lending;

public sealed class RepaymentScheduleTests
{
    private readonly LoanPricing _pricing = new();

    [Fact]
    public void The_grace_example_from_the_questionnaire_reproduces_exactly()
    {
        // "When a loan is given on September 20th, October is the grace period and the first
        // instalment is done in November."
        var terms = _pricing.Price(LoanProduct.Emergency, Money.Kes(25_000m));

        var schedule = RepaymentSchedule.Generate(terms, new DateOnly(2026, 9, 20));

        schedule.GraceEndsOn.Should().Be(new DateOnly(2026, 10, 31));
        schedule.FirstDueDate.Should().Be(new DateOnly(2026, 11, 30));
    }

    [Fact]
    public void Grace_is_a_whole_month_regardless_of_the_day_of_disbursement()
    {
        // Disbursed on the 1st or the 30th, the first instalment falls in the same month.
        // Grace is a month of the calendar, not thirty days.
        var terms = _pricing.Price(LoanProduct.Emergency, Money.Kes(25_000m));

        RepaymentSchedule.Generate(terms, new DateOnly(2026, 9, 1)).FirstDueDate
            .Should().Be(new DateOnly(2026, 11, 30));

        RepaymentSchedule.Generate(terms, new DateOnly(2026, 9, 30)).FirstDueDate
            .Should().Be(new DateOnly(2026, 11, 30));
    }

    [Fact]
    public void The_emergency_loan_schedules_five_instalments_of_5500()
    {
        var terms = _pricing.Price(LoanProduct.Emergency, Money.Kes(25_000m));

        var schedule = RepaymentSchedule.Generate(terms, new DateOnly(2026, 9, 20));

        schedule.Instalments.Should().HaveCount(5);
        schedule.Instalments.Select(i => i.Amount).Should().AllBeEquivalentTo(Money.Kes(5_500m));
        schedule.TotalScheduled.Should().Be(Money.Kes(27_500m));
        schedule.FinalDueDate.Should().Be(new DateOnly(2027, 3, 31));
    }

    [Fact]
    public void A_60000_normal_loan_schedules_twelve_instalments_of_5500()
    {
        var terms = _pricing.Price(LoanProduct.Normal, Money.Kes(60_000m));

        var schedule = RepaymentSchedule.Generate(terms, new DateOnly(2026, 9, 20));

        schedule.Instalments.Should().HaveCount(12);
        schedule.TotalScheduled.Should().Be(Money.Kes(66_000m));
        schedule.Instalments.Select(i => i.Amount).Should().AllBeEquivalentTo(Money.Kes(5_500m));
    }

    [Fact]
    public void A_175000_normal_loan_schedules_twenty_instalments_of_9625()
    {
        var terms = _pricing.Price(LoanProduct.Normal, Money.Kes(175_000m));

        var schedule = RepaymentSchedule.Generate(terms, new DateOnly(2026, 9, 20));

        schedule.Instalments.Should().HaveCount(20);
        schedule.Instalments.Select(i => i.Amount).Should().AllBeEquivalentTo(Money.Kes(9_625m));
        schedule.TotalScheduled.Should().Be(Money.Kes(192_500m));
    }

    [Fact]
    public void Instalments_always_sum_back_to_the_total_repayable()
    {
        // A loan whose total does not divide evenly. 55,000 + 10% = 60,500 over 12 months is
        // 5,041.666..., and the eight spare cents go on the earliest instalments.
        var terms = _pricing.Price(LoanProduct.Normal, Money.Kes(55_000m));

        var schedule = RepaymentSchedule.Generate(terms, new DateOnly(2026, 9, 20));

        schedule.Terms.TermMonths.Should().Be(12);
        schedule.TotalScheduled.Should().Be(Money.Kes(60_500m));
        schedule.Instalments.Take(8).Select(i => i.Amount)
            .Should().AllBeEquivalentTo(Money.Kes(5_041.67m));
        schedule.Instalments.Skip(8).Select(i => i.Amount)
            .Should().AllBeEquivalentTo(Money.Kes(5_041.66m));
    }

    [Fact]
    public void Instalments_fall_due_on_the_last_day_of_each_month()
    {
        // Deductions run in the payroll of the month; the schedule reaches HR by the 25th.
        // Month ends vary, so the dates are computed rather than assumed to be the 30th.
        var terms = _pricing.Price(LoanProduct.Emergency, Money.Kes(25_000m));

        var schedule = RepaymentSchedule.Generate(terms, new DateOnly(2027, 12, 5));

        schedule.Instalments.Select(i => i.DueDate).Should().Equal(
            new DateOnly(2028, 2, 29),   // leap year
            new DateOnly(2028, 3, 31),
            new DateOnly(2028, 4, 30),
            new DateOnly(2028, 5, 31),
            new DateOnly(2028, 6, 30));
    }

    [Fact]
    public void The_principal_and_interest_portions_each_sum_to_their_whole()
    {
        var terms = _pricing.Price(LoanProduct.Normal, Money.Kes(175_000m));

        var schedule = RepaymentSchedule.Generate(terms, new DateOnly(2026, 9, 20));

        schedule.Instalments.Sum(i => i.PrincipalPortion, Currency.Kes)
            .Should().Be(Money.Kes(175_000m));
        schedule.Instalments.Sum(i => i.InterestPortion, Currency.Kes)
            .Should().Be(Money.Kes(17_500m));
    }

    [Fact]
    public void Nothing_is_expected_during_the_grace_month()
    {
        var terms = _pricing.Price(LoanProduct.Emergency, Money.Kes(25_000m));
        var schedule = RepaymentSchedule.Generate(terms, new DateOnly(2026, 9, 20));

        schedule.ExpectedPaidBy(new DateOnly(2026, 10, 31)).Should().Be(Money.ZeroKes);
        schedule.ExpectedPaidBy(new DateOnly(2026, 11, 30)).Should().Be(Money.Kes(5_500m));
        schedule.ExpectedPaidBy(new DateOnly(2027, 1, 31)).Should().Be(Money.Kes(16_500m));
    }

    [Fact]
    public void The_instalment_due_in_a_month_can_be_looked_up_for_the_deduction_schedule()
    {
        // This is what the monthly deduction list sent to HR is built from.
        var terms = _pricing.Price(LoanProduct.Emergency, Money.Kes(25_000m));
        var schedule = RepaymentSchedule.Generate(terms, new DateOnly(2026, 9, 20));

        schedule.InstalmentDueIn(2026, 11)!.Amount.Should().Be(Money.Kes(5_500m));
        schedule.InstalmentDueIn(2026, 10).Should().BeNull(because: "October is the grace month");
        schedule.InstalmentDueIn(2027, 6).Should().BeNull(because: "the loan has cleared");
    }

    [Fact]
    public void A_schedule_crossing_a_year_end_keeps_going()
    {
        var terms = _pricing.Price(LoanProduct.Normal, Money.Kes(60_000m));

        var schedule = RepaymentSchedule.Generate(terms, new DateOnly(2026, 11, 20));

        schedule.FirstDueDate.Should().Be(new DateOnly(2027, 1, 31));
        schedule.FinalDueDate.Should().Be(new DateOnly(2027, 12, 31));
    }
}
