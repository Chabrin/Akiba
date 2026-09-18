using Akiba.Domain.Financial;
using Akiba.Domain.Reconciliation;

namespace Akiba.Domain.Tests.Reconciliation;

/// <summary>
/// The schedule reaches HR by the 25th, HR runs the payroll, a cheque comes back. This is
/// where the office finds out whose instalment did not come off.
/// </summary>
public sealed class PayrollReconciliationTests
{
    [Fact]
    public void A_month_HR_ran_exactly_as_asked_agrees()
    {
        var reconciliation = PayrollReconciliation.Compare(
            2026, 9,
            [Expect("1042", "Grace Njeri", 5_000m, 11_000m)],
            [Actual("1042", "NJERI GRACE", 16_000m)],
            Money.Kes(16_000m));

        reconciliation.Agrees.Should().BeTrue();
        reconciliation.NeedingAttention.Should().BeEmpty();
        reconciliation.TotalVariance.Should().Be(Money.ZeroKes);
    }

    [Fact]
    public void A_short_deduction_is_flagged_because_the_loan_falls_into_arrears_either_way()
    {
        var reconciliation = PayrollReconciliation.Compare(
            2026, 9,
            [Expect("1042", "Grace Njeri", 5_000m, 11_000m)],
            [Actual("1042", "NJERI GRACE", 5_000m)],
            Money.Kes(5_000m));

        var line = reconciliation.NeedingAttention.Should().ContainSingle().Subject;

        line.Kind.Should().Be(PayrollVarianceKind.UnderDeducted);
        line.Variance.Should().Be(Money.Kes(-11_000m));
        line.ClerkTask.Should().Contain("arrears");
    }

    [Fact]
    public void Somebody_HR_deducted_from_whom_Akiba_never_asked_about_comes_first()
    {
        // The one to look at first: either a member is missing from the schedule, or money has
        // been taken from an employee who never agreed to it.
        var reconciliation = PayrollReconciliation.Compare(
            2026, 9,
            [Expect("1042", "Grace Njeri", 5_000m, 0m)],
            [
                Actual("1042", "NJERI GRACE", 5_000m),
                Actual("2210", "OTIENO BRIAN", 3_000m),
            ],
            Money.Kes(8_000m));

        reconciliation.Lines[0].Kind.Should().Be(PayrollVarianceKind.NotOnSchedule);
        reconciliation.Lines[0].MemberName.Should().Be("OTIENO BRIAN");
        reconciliation.Lines[0].ClerkTask.Should().Contain("before receipting it");
    }

    [Fact]
    public void A_member_HR_skipped_entirely_is_not_lost_in_the_total()
    {
        var reconciliation = PayrollReconciliation.Compare(
            2026, 9,
            [
                Expect("1042", "Grace Njeri", 5_000m, 11_000m),
                Expect("1077", "Peter Wanjohi", 2_000m, 0m),
            ],
            [Actual("1042", "NJERI GRACE", 16_000m)],
            Money.Kes(16_000m));

        var missing = reconciliation.NeedingAttention.Should().ContainSingle().Subject;

        missing.Kind.Should().Be(PayrollVarianceKind.NotDeducted);
        missing.MemberName.Should().Be("Peter Wanjohi");
        missing.Deducted.Should().Be(Money.ZeroKes);
    }

    [Fact]
    public void A_shareholder_who_is_not_on_the_payroll_is_left_out_rather_than_shown_as_a_failure()
    {
        // The society's own register carries shareholders with no CAL staff number. HR cannot
        // deduct from somebody who has no payslip, and listing them as failures would bury the
        // real ones.
        var reconciliation = PayrollReconciliation.Compare(
            2026, 9,
            [
                Expect("1042", "Grace Njeri", 5_000m, 0m),
                Expect(string.Empty, "Samuel Kariuki", 10_000m, 0m),
            ],
            [Actual("1042", "NJERI GRACE", 5_000m)],
            Money.Kes(5_000m));

        reconciliation.Lines.Should().ContainSingle();
        reconciliation.Agrees.Should().BeTrue();
    }

    [Fact]
    public void A_cheque_that_does_not_match_HRs_own_figures_is_a_separate_question()
    {
        // A cheque that does not equal HR's schedule is an error in the payment; a schedule
        // that does not equal Akiba's is an error in the deduction. Worth telling apart.
        var reconciliation = PayrollReconciliation.Compare(
            2026, 9,
            [Expect("1042", "Grace Njeri", 5_000m, 11_000m)],
            [Actual("1042", "NJERI GRACE", 16_000m)],
            Money.Kes(15_500m));

        reconciliation.NeedingAttention.Should().BeEmpty();
        reconciliation.TotalVariance.Should().Be(Money.ZeroKes);
        reconciliation.ChequeDifference.Should().Be(Money.Kes(-500m));
        reconciliation.Agrees.Should().BeFalse();
        reconciliation.Verdict.Should().Contain("cheque is out by");
    }

    [Fact]
    public void Payroll_numbers_are_matched_without_regard_to_case_or_stray_spaces()
    {
        var reconciliation = PayrollReconciliation.Compare(
            2026, 9,
            [Expect("CAL/1042", "Grace Njeri", 5_000m, 0m)],
            [Actual(" cal/1042 ", "NJERI GRACE", 5_000m)],
            Money.Kes(5_000m));

        reconciliation.Agrees.Should().BeTrue();
    }

    private static PayrollExpectation Expect(
        string payrollNumber, string name, decimal shares, decimal instalments) =>
        new(payrollNumber, name, Money.Kes(shares), Money.Kes(instalments));

    private static PayrollActual Actual(string payrollNumber, string name, decimal deducted) =>
        new(payrollNumber, name, Money.Kes(deducted));
}
