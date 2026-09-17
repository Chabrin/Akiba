using Akiba.Domain.Financial;
using Akiba.Domain.Lending;
using Akiba.Domain.Membership;
using Akiba.Domain.Receipting;
using Akiba.Domain.Tests.Ledger;

namespace Akiba.Domain.Tests.Receipting;

public sealed class ReceiptTests
{
    [Fact]
    public void A_cheque_needs_a_clearance_date_because_Akiba_waits_for_it_to_mature()
    {
        // "As for cheques it is recommended that the cheque matures first before any action is
        // taken."
        var record = () => Receipt.FromDirectDeposit(
            Money.Kes(20_000m),
            ReceiptMethod.Cheque,
            new DateOnly(2026, 9, 12),
            "000512",
            "Grace Njeri");

        record.Should().Throw<ArgumentException>().WithMessage("*matured*");
    }

    [Fact]
    public void A_recorded_receipt_is_not_yet_money_Akiba_can_rely_on()
    {
        var receipt = ReceiptFixture.Cheque();

        receipt.Status.Should().Be(ReceiptStatus.Recorded);
        receipt.IsConfirmed.Should().BeFalse();
    }

    [Fact]
    public void A_receipt_moves_from_recorded_to_cleared_to_reconciled()
    {
        // Bank statements arrive quarterly, so a deposit can sit unconfirmed for up to 90 days.
        var receipt = ReceiptFixture.Cheque();

        receipt.Clear(new DateOnly(2026, 9, 19));
        receipt.Status.Should().Be(ReceiptStatus.Cleared);
        receipt.IsConfirmed.Should().BeTrue();

        receipt.Reconcile(new DateOnly(2026, 12, 31), "STMT-2026-Q4 line 41");
        receipt.Status.Should().Be(ReceiptStatus.Reconciled);
        receipt.BankStatementReference.Should().Be("STMT-2026-Q4 line 41");
    }

    [Fact]
    public void A_receipt_cannot_be_reconciled_before_it_has_cleared()
    {
        var receipt = ReceiptFixture.Cheque();

        var reconcile = () => receipt.Reconcile(new DateOnly(2026, 12, 31), "STMT-2026-Q4");

        reconcile.Should().Throw<InvalidOperationException>().WithMessage("*has not matured*");
    }

    [Fact]
    public void The_name_on_the_slip_is_kept_as_written()
    {
        // When the slip and the member record disagree, the clerk needs to see what the slip
        // actually said.
        var receipt = Receipt.FromDirectDeposit(
            Money.Kes(5_500m), ReceiptMethod.Mpesa, new DateOnly(2026, 9, 12), "SJ45KL9P0Q", "G. NJERI");

        receipt.PayerNameOnSlip.Should().Be("G. NJERI");
        receipt.IdentifiedBorrower.Should().BeNull(because: "nobody has decided whose it is yet");
    }

    [Fact]
    public void Identifying_the_payer_is_a_separate_decision_from_allocating_the_money()
    {
        var receipt = ReceiptFixture.Mpesa();
        var member = BorrowerId.New();

        receipt.IdentifyPayer(member);

        receipt.IdentifiedBorrower.Should().Be(member);
        receipt.AllocatedAmount.Should().Be(Money.ZeroKes, because: "identifying is not allocating");
    }

    [Fact]
    public void Everything_received_starts_out_unallocated()
    {
        var receipt = ReceiptFixture.Mpesa();

        receipt.UnallocatedAmount.Should().Be(Money.Kes(5_500m));
        receipt.IsFullyAllocated.Should().BeFalse();
    }

    [Fact]
    public void Allocating_records_who_decided_and_what_it_was_for()
    {
        var receipt = ReceiptFixture.Mpesa();
        var sharesAccount = Guid.NewGuid();

        receipt.Allocate(
            AllocationTarget.Shares, sharesAccount, Money.Kes(5_500m),
            LedgerFixture.Clerk, LedgerFixture.Now);

        receipt.IsFullyAllocated.Should().BeTrue();
        receipt.Allocations.Should().ContainSingle()
            .Which.AllocatedBy.Should().Be(LedgerFixture.Clerk);
    }

    [Fact]
    public void An_allocation_with_no_author_is_refused()
    {
        // Allocation is never silently inferred. A wrong guess in a system of record is worse
        // than no guess, because it looks like a decision somebody made.
        var receipt = ReceiptFixture.Mpesa();

        var allocate = () => receipt.Allocate(
            AllocationTarget.Shares, Guid.NewGuid(), Money.Kes(5_500m), default, LedgerFixture.Now);

        allocate.Should().Throw<ArgumentException>().WithMessage("*never inferred*");
    }

    [Fact]
    public void Allocating_more_than_was_received_is_refused()
    {
        var receipt = ReceiptFixture.Mpesa();

        var allocate = () => receipt.Allocate(
            AllocationTarget.Shares, Guid.NewGuid(), Money.Kes(6_000m),
            LedgerFixture.Clerk, LedgerFixture.Now);

        allocate.Should().Throw<InvalidOperationException>().WithMessage("*still unallocated*");
    }

    [Fact]
    public void A_receipt_can_be_split_between_shares_and_a_loan_instalment()
    {
        // The ordinary payroll case: part of the deduction is the share contribution and the
        // rest is the instalment.
        var receipt = Receipt.FromPayroll(Money.Kes(11_000m), new DateOnly(2026, 9, 30), "000512");

        receipt.Allocate(AllocationTarget.Shares, Guid.NewGuid(), Money.Kes(5_500m), LedgerFixture.Clerk, LedgerFixture.Now);
        receipt.Allocate(AllocationTarget.LoanInstalment, Guid.NewGuid(), Money.Kes(5_500m), LedgerFixture.Clerk, LedgerFixture.Now);

        receipt.IsFullyAllocated.Should().BeTrue();
        receipt.LiveAllocations.Should().HaveCount(2);
    }

    [Fact]
    public void A_reversed_allocation_stays_visible_and_frees_the_money_again()
    {
        var receipt = ReceiptFixture.Mpesa();
        receipt.Allocate(AllocationTarget.Shares, Guid.NewGuid(), Money.Kes(5_500m), LedgerFixture.Clerk, LedgerFixture.Now);

        receipt.ReverseAllocation(0, "Allocated against the wrong member");

        receipt.UnallocatedAmount.Should().Be(Money.Kes(5_500m));
        receipt.LiveAllocations.Should().BeEmpty();
        receipt.Allocations.Should().ContainSingle(because: "the record shows a decision was made and changed")
            .Which.ReversedReason.Should().Be("Allocated against the wrong member");
    }

    [Fact]
    public void An_overpayment_can_be_turned_into_shareholding()
    {
        // One of the two sanctioned outcomes: "we usually increase the member shareholding by
        // the overpaid amount or even refund to the member via cheque."
        var receipt = Receipt.FromDirectDeposit(
            Money.Kes(6_000m), ReceiptMethod.BankDeposit, new DateOnly(2026, 9, 12), "DEP-4471", "Grace Njeri");

        receipt.Allocate(AllocationTarget.LoanInstalment, Guid.NewGuid(), Money.Kes(5_500m), LedgerFixture.Clerk, LedgerFixture.Now);
        receipt.Allocate(AllocationTarget.OverpaymentToShares, Guid.NewGuid(), Money.Kes(500m), LedgerFixture.Clerk, LedgerFixture.Now);

        receipt.IsFullyAllocated.Should().BeTrue();
    }

    [Fact]
    public void An_overpayment_can_be_refunded_by_cheque_instead()
    {
        var receipt = Receipt.FromDirectDeposit(
            Money.Kes(6_000m), ReceiptMethod.BankDeposit, new DateOnly(2026, 9, 12), "DEP-4471", "Grace Njeri");

        receipt.Allocate(AllocationTarget.LoanInstalment, Guid.NewGuid(), Money.Kes(5_500m), LedgerFixture.Clerk, LedgerFixture.Now);
        receipt.Allocate(AllocationTarget.OverpaymentRefund, Guid.NewGuid(), Money.Kes(500m), LedgerFixture.Clerk, LedgerFixture.Now);

        receipt.IsFullyAllocated.Should().BeTrue();
    }

    [Fact]
    public void The_two_payroll_channels_stay_distinguishable()
    {
        // Employee deductions are drawn on the business account and landlord offsets on the
        // main account, and they reconcile against different statements.
        Receipt.FromPayroll(Money.Kes(120_000m), new DateOnly(2026, 9, 30), "000512")
            .Channel.Should().Be(ReceiptChannel.PayrollDeduction);

        Receipt.FromLandlordSchedule(Money.Kes(80_000m), new DateOnly(2026, 9, 30), "000513")
            .Channel.Should().Be(ReceiptChannel.LandlordRentOffset);
    }
}

public sealed class PaymentAllocationOrderTests
{
    private static readonly CompetingLoan Normal = new(
        LoanId.New(), "AKB-2025-0031", LoanProduct.Normal, new DateOnly(2025, 6, 20), Money.Kes(9_625m));

    private static readonly CompetingLoan Emergency = new(
        LoanId.New(), "AKB-2026-0007", LoanProduct.Emergency, new DateOnly(2026, 9, 20), Money.Kes(5_500m));

    [Fact]
    public void The_default_pays_the_oldest_loan_first_in_full()
    {
        // TODO: confirm with committee - open question 3. The questionnaire asked exactly this
        // and the answer described restructuring, which is a different thing.
        var proposals = new OldestFirst().Apportion(Money.Kes(12_000m), [Emergency, Normal]);

        proposals[0].Loan.LoanNumber.Should().Be("AKB-2025-0031");
        proposals[0].Amount.Should().Be(Money.Kes(9_625m));
        proposals[0].IsFullyCovered.Should().BeTrue();

        proposals[1].Amount.Should().Be(Money.Kes(2_375m));
        proposals[1].Shortfall.Should().Be(Money.Kes(3_125m));
    }

    [Fact]
    public void When_there_is_enough_for_both_nothing_falls_short()
    {
        var proposals = new OldestFirst().Apportion(Money.Kes(15_125m), [Emergency, Normal]);

        proposals.Should().OnlyContain(proposal => proposal.IsFullyCovered);
    }

    [Fact]
    public void The_emergency_first_alternative_clears_the_short_loan_instead()
    {
        // A five-month loan running late while a twenty-month loan is serviced would be an odd
        // result, which is why this alternative exists. It is still not the default.
        var proposals = new EmergencyLoanFirst().Apportion(Money.Kes(12_000m), [Normal, Emergency]);

        proposals[0].Loan.Product.Should().Be(LoanProduct.Emergency);
        proposals[0].Amount.Should().Be(Money.Kes(5_500m));
        proposals[1].Amount.Should().Be(Money.Kes(6_500m));
    }

    [Fact]
    public void The_pro_rata_alternative_splits_exactly_with_no_lost_cent()
    {
        var proposals = new ProRataAcrossLoans().Apportion(Money.Kes(10_000m), [Normal, Emergency]);

        proposals.Sum(proposal => proposal.Amount, Currency.Kes).Should().Be(Money.Kes(10_000m));
    }

    [Fact]
    public void Every_strategy_allocates_exactly_what_came_in_and_no_more()
    {
        // Whichever rule the committee picks, this must hold: a deduction cannot pay out more
        // than it brought in.
        IPaymentAllocationOrder[] strategies =
            [new OldestFirst(), new EmergencyLoanFirst(), new ProRataAcrossLoans()];

        foreach (var strategy in strategies)
        {
            var proposals = strategy.Apportion(Money.Kes(8_000m), [Normal, Emergency]);

            proposals.Sum(proposal => proposal.Amount, Currency.Kes)
                .Should().Be(Money.Kes(8_000m), because: $"{strategy.GetType().Name} must balance");
        }
    }

    [Fact]
    public void A_member_with_nothing_deducted_gets_nothing_allocated()
    {
        var proposals = new OldestFirst().Apportion(Money.ZeroKes, [Normal, Emergency]);

        proposals.Should().OnlyContain(proposal => proposal.Amount == Money.ZeroKes);
    }
}

internal static class ReceiptFixture
{
    public static Receipt Cheque() => Receipt.FromDirectDeposit(
        Money.Kes(20_000m),
        ReceiptMethod.Cheque,
        new DateOnly(2026, 9, 12),
        "000512",
        "Grace Njeri",
        expectedClearanceOn: new DateOnly(2026, 9, 19));

    public static Receipt Mpesa() => Receipt.FromDirectDeposit(
        Money.Kes(5_500m),
        ReceiptMethod.Mpesa,
        new DateOnly(2026, 9, 12),
        "SJ45KL9P0Q",
        "G. NJERI");
}
