using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Domain.Lending;
using Akiba.Domain.Tests.Ledger;

namespace Akiba.Domain.Tests.Lending;

public sealed class RestructuringTests
{
    [Fact]
    public void A_loan_less_than_half_repaid_cannot_be_restructured()
    {
        // "The existing loan must have been paid down to at least 50%, inclusive of interest."
        var loan = RestructureFixture.Loan(principal: 200_000m);   // 220,000 repayable
        var assessment = Restructuring.Assess(loan, outstandingBalance: Money.Kes(150_000m));

        assessment.IsPermitted.Should().BeFalse();
        assessment.Refusal.Should().Be(RestructureRefusal.NotPaidDownFarEnough);
        assessment.Explanation.Should().Contain("at least 50%");
    }

    [Fact]
    public void The_fifty_percent_test_is_against_principal_plus_interest_not_principal_alone()
    {
        // 200,000 + 10% = 220,000. Exactly half of that is 110,000 repaid, leaving 110,000.
        // Measured against the principal alone, 110,000 repaid would look like 55% and pass
        // too early.
        var loan = RestructureFixture.Loan(principal: 200_000m);

        Restructuring.Assess(loan, Money.Kes(110_000m)).IsPermitted
            .Should().BeTrue(because: "exactly half of principal plus interest has been repaid");

        Restructuring.Assess(loan, Money.Kes(110_000.01m)).IsPermitted
            .Should().BeFalse(because: "a cent less than half has been repaid");
    }

    [Fact]
    public void The_fee_is_five_percent_of_the_outstanding_balance()
    {
        var loan = RestructureFixture.Loan(principal: 200_000m);

        var assessment = Restructuring.Assess(loan, Money.Kes(110_000m));

        assessment.Fee.Should().Be(Money.Kes(5_500m));
    }

    [Fact]
    public void The_fee_is_not_rolled_into_the_restructured_balance()
    {
        // "Deductable upfront and the restructured instalment starts after the 5% fee."
        var loan = RestructureFixture.Loan(principal: 200_000m);

        var assessment = Restructuring.Assess(loan, Money.Kes(110_000m));

        assessment.RestructuredBalance.Should().Be(Money.Kes(110_000m));
        (assessment.RestructuredBalance + assessment.Fee).Should().NotBe(assessment.RestructuredBalance);
    }

    [Fact]
    public void The_restructured_balance_attracts_no_fresh_interest()
    {
        var loan = RestructureFixture.Loan(principal: 200_000m);
        var assessment = Restructuring.Assess(loan, Money.Kes(110_000m));

        var terms = Restructuring.TermsFor(assessment);

        terms.Interest.Should().Be(Money.ZeroKes);
        terms.TotalRepayable.Should().Be(Money.Kes(110_000m));
    }

    [Fact]
    public void The_new_term_comes_from_the_graduated_scale_applied_to_the_balance()
    {
        var loan = RestructureFixture.Loan(principal: 200_000m);
        var assessment = Restructuring.Assess(loan, Money.Kes(110_000m));

        assessment.TermMonths.Should().Be(16, because: "110,000 falls in the 100,001-150,000 band");
        Restructuring.TermsFor(assessment).TermMonths.Should().Be(16);
    }

    [Fact]
    public void A_loan_can_be_restructured_once_only()
    {
        var original = RestructureFixture.Loan(principal: 200_000m);
        var replacement = RestructureFixture.Loan(principal: 110_000m);

        Restructuring.CarryOver(original, replacement, new DateOnly(2027, 3, 31));

        original.Status.Should().Be(LoanStatus.Restructured);
        original.HasBeenRestructured.Should().BeTrue();
        replacement.Restructures.Should().Be(original.Id);

        Restructuring.Assess(original, Money.Kes(110_000m)).Refusal
            .Should().Be(RestructureRefusal.NotRunning);
    }

    [Fact]
    public void The_original_guarantors_carry_over()
    {
        // "The terms of the loan still remain" - read as the guarantees continuing unchanged
        // rather than the guarantors re-signing.
        var original = RestructureFixture.Loan(principal: 200_000m, withGuarantor: true);

        original.Guarantees.Should().ContainSingle();
    }

    [Fact]
    public void A_settled_loan_cannot_be_restructured()
    {
        var loan = RestructureFixture.Loan(principal: 200_000m);
        loan.Settle(new DateOnly(2027, 3, 31));

        Restructuring.Assess(loan, Money.ZeroKes).Refusal.Should().Be(RestructureRefusal.NotRunning);
    }

    [Fact]
    public void A_balance_below_the_scale_floor_cannot_be_restructured()
    {
        // The graduated scale starts at 30,000, and the restructured term comes from it. A
        // balance below that has no term, which is open question 5 rather than a choice to make.
        var loan = RestructureFixture.Loan(principal: 40_000m);   // 44,000 repayable

        var assessment = Restructuring.Assess(loan, Money.Kes(20_000m));

        assessment.IsPermitted.Should().BeFalse();
        assessment.TermMonths.Should().BeNull();
    }

    [Fact]
    public void Asking_for_terms_on_a_refused_restructure_throws_rather_than_inventing_them()
    {
        var loan = RestructureFixture.Loan(principal: 200_000m);
        var assessment = Restructuring.Assess(loan, Money.Kes(150_000m));

        var terms = () => Restructuring.TermsFor(assessment);

        terms.Should().Throw<InvalidOperationException>().WithMessage("*at least 50%*");
    }
}

public sealed class ArrearsTests
{
    private readonly RepaymentSchedule _schedule = RepaymentSchedule.Generate(
        new LoanPricing().Price(LoanProduct.Emergency, Money.Kes(25_000m)),
        new DateOnly(2026, 9, 20));

    [Fact]
    public void A_loan_paid_up_to_date_is_current()
    {
        // Two instalments due by 31 December, both paid.
        var arrears = Arrears.Assess("AKB-2026-0007", _schedule, Money.Kes(11_000m), new DateOnly(2026, 12, 31));

        arrears.IsInArrears.Should().BeFalse();
        arrears.Bucket.Should().Be(ArrearsBucket.Current);
    }

    [Fact]
    public void Nothing_is_overdue_during_the_grace_month()
    {
        var arrears = Arrears.Assess("AKB-2026-0007", _schedule, Money.ZeroKes, new DateOnly(2026, 10, 31));

        arrears.ExpectedPaidBy.Should().Be(Money.ZeroKes);
        arrears.IsInArrears.Should().BeFalse();
    }

    [Fact]
    public void A_missed_instalment_ages_from_its_own_due_date()
    {
        // The November instalment was never paid. As at 15 December that is 15 days overdue,
        // measured from 30 November - not from today, and not from the loan's start.
        var arrears = Arrears.Assess("AKB-2026-0007", _schedule, Money.ZeroKes, new DateOnly(2026, 12, 15));

        arrears.AmountOverdue.Should().Be(Money.Kes(5_500m));
        arrears.OldestUnpaidDueDate.Should().Be(new DateOnly(2026, 11, 30));
        arrears.DaysOverdue.Should().Be(15);
        arrears.Bucket.Should().Be(ArrearsBucket.Days1To30);
    }

    [Fact]
    public void Ageing_is_measured_from_the_oldest_unpaid_instalment_not_the_newest()
    {
        // Nothing paid since disbursement. By 28 February the November instalment is 90 days
        // overdue, and that is the figure that matters - the oldest debt, as an official reads
        // a ledger page.
        var arrears = Arrears.Assess("AKB-2026-0007", _schedule, Money.ZeroKes, new DateOnly(2027, 2, 28));

        arrears.OldestUnpaidDueDate.Should().Be(new DateOnly(2026, 11, 30));
        arrears.DaysOverdue.Should().Be(90);
        arrears.Bucket.Should().Be(ArrearsBucket.Days61To90);
        arrears.AmountOverdue.Should().Be(Money.Kes(22_000m), because: "four instalments have fallen due");
    }

    [Fact]
    public void A_partial_payment_moves_the_ageing_to_the_next_unpaid_instalment()
    {
        // One instalment paid, so the ageing runs from December rather than November.
        var arrears = Arrears.Assess("AKB-2026-0007", _schedule, Money.Kes(5_500m), new DateOnly(2027, 1, 15));

        arrears.OldestUnpaidDueDate.Should().Be(new DateOnly(2026, 12, 31));
        arrears.DaysOverdue.Should().Be(15);
    }

    [Theory]
    [InlineData(0, ArrearsBucket.Current)]
    [InlineData(1, ArrearsBucket.Days1To30)]
    [InlineData(30, ArrearsBucket.Days1To30)]
    [InlineData(31, ArrearsBucket.Days31To60)]
    [InlineData(60, ArrearsBucket.Days31To60)]
    [InlineData(61, ArrearsBucket.Days61To90)]
    [InlineData(90, ArrearsBucket.Days61To90)]
    [InlineData(91, ArrearsBucket.Over90Days)]
    public void The_ageing_bands_are_current_then_thirty_day_steps(int days, ArrearsBucket expected)
    {
        Arrears.BucketFor(days).Should().Be(expected);
    }

    [Fact]
    public void The_arrears_report_totals_each_band()
    {
        LoanArrears[] loans =
        [
            new("AKB-2026-0007", Money.Kes(11_000m), Money.Kes(5_500m), Money.Kes(5_500m), new DateOnly(2026, 12, 31), 15, ArrearsBucket.Days1To30),
            new("AKB-2026-0011", Money.Kes(20_000m), Money.Kes(10_000m), Money.Kes(10_000m), new DateOnly(2026, 10, 31), 75, ArrearsBucket.Days61To90),
            new("AKB-2026-0014", Money.Kes(9_625m), Money.Kes(9_625m), Money.ZeroKes, null, 0, ArrearsBucket.Current),
        ];

        var aged = Arrears.Age(loans);

        aged[ArrearsBucket.Days1To30].Should().Be(Money.Kes(5_500m));
        aged[ArrearsBucket.Days61To90].Should().Be(Money.Kes(10_000m));
        aged[ArrearsBucket.Over90Days].Should().Be(Money.ZeroKes);
        aged[ArrearsBucket.Current].Should().Be(Money.ZeroKes, because: "a loan that is current owes nothing");
    }
}

internal static class RestructureFixture
{
    public static Loan Loan(decimal principal, bool withGuarantor = false)
    {
        var application = ApplicationFixture.Received(new DateOnly(2026, 9, 10));

        if (withGuarantor)
        {
            application.AddGuarantee(ApplicationFixture.Guarantee("Peter Mwangi", 100_000m));
        }

        application.Submit();
        application.RecordDecision(ApplicationFixture.ZoneRep, ApprovalDecisionKind.Approve, LedgerFixture.Now);
        application.Approve(new LoanPricing().Price(LoanProduct.Normal, Money.Kes(principal)), LedgerFixture.Now);

        return Domain.Lending.Loan.Disburse(
            application,
            "AKB-2026-0031",
            AccountId.New(),
            new DateOnly(2026, 9, 20),
            ApplicationFixture.Cheque(principal),
            LedgerFixture.Now);
    }
}
