using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Domain.Reconciliation;
using Akiba.Domain.Tests.Ledger;

namespace Akiba.Domain.Tests.Reconciliation;

/// <summary>
/// The control that catches everything the office never saw. Statements arrive quarterly, so
/// a bank charge can be three months old before anybody looks at it.
/// </summary>
public sealed class BankReconciliationTests
{
    private static readonly AccountId BankAccount = LedgerFixture.Bank.Id;

    [Fact]
    public void A_statement_line_is_positive_and_its_direction_says_which_way_the_money_went()
    {
        var import = () => BankStatementLine.Import(
            1, new DateOnly(2026, 7, 3), "BANK CHARGES", Money.Kes(-400m), StatementDirection.Debit);

        import.Should().Throw<ArgumentException>().WithMessage("*positive*");
    }

    [Fact]
    public void Money_in_is_a_debit_to_the_bank_account_the_way_the_ledger_signs_it()
    {
        var credit = Line(1, new DateOnly(2026, 7, 3), "TRF FRM CAL PAYROLL", 480_000m, StatementDirection.Credit);
        var debit = Line(2, new DateOnly(2026, 7, 5), "CHQ 000431", 60_000m, StatementDirection.Debit);

        credit.SignedAmount.Should().Be(Money.Kes(480_000m));
        debit.SignedAmount.Should().Be(Money.Kes(-60_000m));
    }

    [Fact]
    public void Two_lines_cannot_share_a_line_number_because_that_is_how_a_clerk_points_at_one()
    {
        var import = () => BankReconciliation.Import(
            BankAccount, "Bank", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            Money.Kes(100_000m), Money.Kes(100_000m),
            [
                Line(1, new DateOnly(2026, 7, 3), "ONE", 1_000m, StatementDirection.Credit),
                Line(1, new DateOnly(2026, 7, 4), "TWO", 2_000m, StatementDirection.Credit),
            ]);

        import.Should().Throw<ArgumentException>().WithMessage("*line 1*");
    }

    [Fact]
    public void A_statement_whose_lines_do_not_come_to_its_closing_balance_says_so()
    {
        // The import lost a line, or a column was read wrongly. There is no point matching
        // anything until somebody looks at the file.
        var reconciliation = BankReconciliation.Import(
            BankAccount, "Bank", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            Money.Kes(100_000m), Money.Kes(500_000m),
            [Line(1, new DateOnly(2026, 7, 3), "TRF", 300_000m, StatementDirection.Credit)]);

        reconciliation.IsSelfConsistent.Should().BeFalse();
    }

    [Fact]
    public void A_matched_statement_reconciles_to_zero()
    {
        var payroll = Movement("Payroll deductions, June 2026", new DateOnly(2026, 7, 2), 480_000m);
        var cheque = Movement("Loan to Grace Njeri", new DateOnly(2026, 7, 5), -60_000m);

        var reconciliation = BankReconciliation.Import(
            BankAccount, "Bank", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            Money.Kes(100_000m), Money.Kes(520_000m),
            [
                Line(1, new DateOnly(2026, 7, 3), "TRF FRM CAL PAYROLL", 480_000m, StatementDirection.Credit),
                Line(2, new DateOnly(2026, 7, 5), "CHQ 000431", 60_000m, StatementDirection.Debit),
            ]);

        reconciliation.IsSelfConsistent.Should().BeTrue();

        reconciliation.SuggestMatches([payroll, cheque]).Should().Be(2);

        reconciliation.ConfirmMatch(
            1, payroll.JournalEntryId.Value, payroll.Narration, LedgerFixture.Clerk);
        reconciliation.ConfirmMatch(
            2, cheque.JournalEntryId.Value, cheque.Narration, LedgerFixture.Clerk);

        var result = reconciliation.Reconcile(Money.Kes(520_000m), [payroll, cheque]);

        result.Balances.Should().BeTrue();
        result.EveryLineResolved.Should().BeTrue();
        result.MayClosePeriod.Should().BeTrue();
    }

    [Fact]
    public void A_bank_charge_Akiba_never_recorded_leaves_the_reconciliation_balanced_but_unfinished()
    {
        // This is the point of the whole exercise. The books and the statement agree once the
        // charge is allowed for - but until somebody posts it, the month is not finished.
        var payroll = Movement("Payroll deductions, June 2026", new DateOnly(2026, 7, 2), 480_000m);

        var reconciliation = BankReconciliation.Import(
            BankAccount, "Bank", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            Money.Kes(100_000m), Money.Kes(579_600m),
            [
                Line(1, new DateOnly(2026, 7, 3), "TRF FRM CAL PAYROLL", 480_000m, StatementDirection.Credit),
                Line(2, new DateOnly(2026, 9, 30), "LEDGER FEES", 400m, StatementDirection.Debit),
            ]);

        reconciliation.ConfirmMatch(
            1, payroll.JournalEntryId.Value, payroll.Narration, LedgerFixture.Clerk);

        var result = reconciliation.Reconcile(Money.Kes(580_000m), [payroll]);

        result.Balances.Should().BeTrue();
        result.UnreconciledStatementMovement.Should().Be(Money.Kes(-400m));
        result.EveryLineResolved.Should().BeFalse();
        result.MayClosePeriod.Should().BeFalse();
        result.Verdict.Should().Contain("1 statement line");
    }

    [Fact]
    public void Posting_the_charge_and_matching_it_does_not_change_whether_it_balances()
    {
        // The arithmetic is written so that posting something cannot improve the answer. A
        // formula that rewards posting things would eventually be used to make a difference go
        // away.
        var payroll = Movement("Payroll deductions, June 2026", new DateOnly(2026, 7, 2), 480_000m);
        var charge = Movement("Bank charges, quarter to September", new DateOnly(2026, 9, 30), -400m);

        var reconciliation = BankReconciliation.Import(
            BankAccount, "Bank", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            Money.Kes(100_000m), Money.Kes(579_600m),
            [
                Line(1, new DateOnly(2026, 7, 3), "TRF FRM CAL PAYROLL", 480_000m, StatementDirection.Credit),
                Line(2, new DateOnly(2026, 9, 30), "LEDGER FEES", 400m, StatementDirection.Debit),
            ]);

        reconciliation.ConfirmMatch(
            1, payroll.JournalEntryId.Value, payroll.Narration, LedgerFixture.Clerk);

        var before = reconciliation.Reconcile(Money.Kes(580_000m), [payroll]);

        reconciliation.ConfirmMatch(
            2, charge.JournalEntryId.Value, charge.Narration, LedgerFixture.Clerk);

        // The ledger balance now carries the charge.
        var after = reconciliation.Reconcile(Money.Kes(579_600m), [payroll, charge]);

        before.Difference.Should().Be(after.Difference);
        after.MayClosePeriod.Should().BeTrue();
    }

    [Fact]
    public void A_cheque_written_but_not_presented_is_not_a_difference()
    {
        var payroll = Movement("Payroll deductions, June 2026", new DateOnly(2026, 7, 2), 480_000m);
        var unpresented = Movement("Loan to Peter Wanjohi", new DateOnly(2026, 9, 28), -75_000m);

        var reconciliation = BankReconciliation.Import(
            BankAccount, "Bank", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            Money.Kes(100_000m), Money.Kes(580_000m),
            [Line(1, new DateOnly(2026, 7, 3), "TRF FRM CAL PAYROLL", 480_000m, StatementDirection.Credit)]);

        reconciliation.ConfirmMatch(
            1, payroll.JournalEntryId.Value, payroll.Narration, LedgerFixture.Clerk);

        var result = reconciliation.Reconcile(Money.Kes(505_000m), [payroll, unpresented]);

        result.Balances.Should().BeTrue();
        result.UnpresentedLedgerMovement.Should().Be(Money.Kes(-75_000m));
        result.UnmatchedMovements.Should().ContainSingle();
        result.MayClosePeriod.Should().BeTrue();
    }

    [Fact]
    public void A_line_the_bank_credited_in_error_still_counts_against_the_reconciliation()
    {
        // Written off as not ours, but the bank's balance carries it and Akiba's never will.
        // It stays a reconciling item until the bank corrects it.
        var payroll = Movement("Payroll deductions, June 2026", new DateOnly(2026, 7, 2), 480_000m);

        var reconciliation = BankReconciliation.Import(
            BankAccount, "Bank", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            Money.Kes(100_000m), Money.Kes(585_000m),
            [
                Line(1, new DateOnly(2026, 7, 3), "TRF FRM CAL PAYROLL", 480_000m, StatementDirection.Credit),
                Line(2, new DateOnly(2026, 8, 11), "TRF FRM KIMATHI HARDWARE", 5_000m, StatementDirection.Credit),
            ]);

        reconciliation.ConfirmMatch(
            1, payroll.JournalEntryId.Value, payroll.Narration, LedgerFixture.Clerk);

        reconciliation.MarkLineNotOurs(
            2, "Credited in error; the bank has been written to.", LedgerFixture.Clerk);

        var result = reconciliation.Reconcile(Money.Kes(580_000m), [payroll]);

        result.Balances.Should().BeTrue();
        result.EveryLineResolved.Should().BeTrue();
        result.MayClosePeriod.Should().BeTrue();
    }

    [Fact]
    public void Nothing_is_suggested_when_two_movements_would_fit()
    {
        // Two members paying 5,000 on the same day is ordinary. Picking one would be a guess
        // wearing a decision's clothes.
        var first = Movement("Share contribution - A", new DateOnly(2026, 7, 10), 5_000m);
        var second = Movement("Share contribution - B", new DateOnly(2026, 7, 10), 5_000m);

        var reconciliation = BankReconciliation.Import(
            BankAccount, "Bank", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            Money.Kes(0m), Money.Kes(5_000m),
            [Line(1, new DateOnly(2026, 7, 10), "DEPOSIT", 5_000m, StatementDirection.Credit)]);

        reconciliation.SuggestMatches([first, second]).Should().Be(0);
        reconciliation.Line(1).State.Should().Be(MatchState.Unmatched);
    }

    [Fact]
    public void A_suggestion_is_not_a_match()
    {
        var movement = Movement("Share contribution", new DateOnly(2026, 7, 10), 5_000m);

        var reconciliation = BankReconciliation.Import(
            BankAccount, "Bank", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            Money.Kes(0m), Money.Kes(5_000m),
            [Line(1, new DateOnly(2026, 7, 12), "DEPOSIT", 5_000m, StatementDirection.Credit)]);

        reconciliation.SuggestMatches([movement]).Should().Be(1);

        var line = reconciliation.Line(1);
        line.State.Should().Be(MatchState.Suggested);
        line.IsResolved.Should().BeFalse();

        var result = reconciliation.Reconcile(Money.Kes(5_000m), [movement]);
        result.MayClosePeriod.Should().BeFalse();
    }

    [Fact]
    public void One_ledger_entry_cannot_be_matched_to_two_statement_lines()
    {
        var movement = Movement("Share contribution", new DateOnly(2026, 7, 10), 5_000m);

        var reconciliation = BankReconciliation.Import(
            BankAccount, "Bank", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            Money.Kes(0m), Money.Kes(10_000m),
            [
                Line(1, new DateOnly(2026, 7, 10), "DEPOSIT", 5_000m, StatementDirection.Credit),
                Line(2, new DateOnly(2026, 7, 11), "DEPOSIT", 5_000m, StatementDirection.Credit),
            ]);

        reconciliation.ConfirmMatch(
            1, movement.JournalEntryId.Value, movement.Narration, LedgerFixture.Clerk);

        var again = () => reconciliation.ConfirmMatch(
            2, movement.JournalEntryId.Value, movement.Narration, LedgerFixture.Clerk);

        again.Should().Throw<InvalidOperationException>().WithMessage("*already matched*");
    }

    [Fact]
    public void Signing_off_is_refused_while_anything_is_outstanding()
    {
        var reconciliation = BankReconciliation.Import(
            BankAccount, "Bank", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            Money.Kes(0m), Money.Kes(5_000m),
            [Line(1, new DateOnly(2026, 7, 10), "DEPOSIT", 5_000m, StatementDirection.Credit)]);

        var result = reconciliation.Reconcile(Money.Kes(5_000m), []);

        var sign = () => reconciliation.SignOff(result, LedgerFixture.Treasurer, LedgerFixture.Now);

        sign.Should().Throw<ReconciliationNotBalancedException>();
        reconciliation.IsSignedOff.Should().BeFalse();
    }

    [Fact]
    public void A_signed_off_reconciliation_cannot_be_altered_afterwards()
    {
        var movement = Movement("Share contribution", new DateOnly(2026, 7, 10), 5_000m);

        var reconciliation = BankReconciliation.Import(
            BankAccount, "Bank", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            Money.Kes(0m), Money.Kes(5_000m),
            [Line(1, new DateOnly(2026, 7, 10), "DEPOSIT", 5_000m, StatementDirection.Credit)]);

        reconciliation.ConfirmMatch(
            1, movement.JournalEntryId.Value, movement.Narration, LedgerFixture.Clerk);

        var result = reconciliation.Reconcile(Money.Kes(5_000m), [movement]);
        reconciliation.SignOff(result, LedgerFixture.Treasurer, LedgerFixture.Now);

        reconciliation.IsSignedOff.Should().BeTrue();
        reconciliation.SignedOffBy.Should().Be(LedgerFixture.Treasurer);

        var reopen = () => reconciliation.ClearLine(1);

        reopen.Should().Throw<InvalidOperationException>().WithMessage("*cannot be altered*");
    }

    [Fact]
    public void A_line_written_off_cannot_be_matched_without_clearing_it_first()
    {
        var reconciliation = BankReconciliation.Import(
            BankAccount, "Bank", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            Money.Kes(0m), Money.Kes(5_000m),
            [Line(1, new DateOnly(2026, 7, 10), "DEPOSIT", 5_000m, StatementDirection.Credit)]);

        reconciliation.MarkLineNotOurs(1, "Not Akiba's.", LedgerFixture.Clerk);

        var match = () => reconciliation.ConfirmMatch(
            1, Guid.NewGuid(), "Share contribution", LedgerFixture.Clerk);

        match.Should().Throw<InvalidOperationException>().WithMessage("*Clear that first*");

        reconciliation.ClearLine(1);
        reconciliation.Line(1).State.Should().Be(MatchState.Unmatched);
    }

    private static BankStatementLine Line(
        int number, DateOnly date, string description, decimal amount, StatementDirection direction) =>
        BankStatementLine.Import(number, date, description, Money.Kes(amount), direction);

    private static LedgerMovement Movement(string narration, DateOnly date, decimal signedAmount) =>
        new(JournalEntryId.New(), date, narration, Money.Kes(signedAmount));
}
