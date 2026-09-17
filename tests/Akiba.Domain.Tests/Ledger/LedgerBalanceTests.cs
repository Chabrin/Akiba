using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;

namespace Akiba.Domain.Tests.Ledger;

/// <summary>
/// Balances are derived, never stored. These tests are the proof that the derivation is
/// right, and - in <see cref="A_balance_as_at_June_is_the_same_in_December"/> - that it stays
/// right as the ledger grows past the date being asked about.
/// </summary>
public sealed class LedgerBalanceTests
{
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Account _shares;

    public LedgerBalanceTests() =>
        _shares = LedgerFixture.SharesOf("Grace Njeri", _memberId);

    [Fact]
    public void A_members_shareholding_is_the_sum_of_their_contributions()
    {
        JournalEntry[] ledger =
        [
            LedgerFixture.ShareContribution(_shares, 5_500m, new DateOnly(2026, 1, 31)),
            LedgerFixture.ShareContribution(_shares, 5_500m, new DateOnly(2026, 2, 28)),
            LedgerFixture.ShareContribution(_shares, 5_500m, new DateOnly(2026, 3, 31)),
        ];

        var shareholding = LedgerBalances.NaturalBalanceAsAt(
            ledger, _shares, new DateOnly(2026, 3, 31));

        shareholding.Should().Be(Money.Kes(16_500m));
    }

    [Fact]
    public void Member_shares_are_a_liability_so_the_natural_balance_is_the_positive_one()
    {
        // Akiba owes this money back to the member. The signed balance is negative because
        // credits are negative; the natural balance is what goes on a statement.
        JournalEntry[] ledger = [LedgerFixture.ShareContribution(_shares, 5_500m, new DateOnly(2026, 1, 31))];
        var asAt = new DateOnly(2026, 1, 31);

        LedgerBalances.SignedBalanceAsAt(ledger, _shares.Id, asAt, Currency.Kes)
            .Should().Be(Money.Kes(-5_500m));

        LedgerBalances.NaturalBalanceAsAt(ledger, _shares, asAt)
            .Should().Be(Money.Kes(5_500m));
    }

    [Fact]
    public void A_balance_as_at_a_date_ignores_everything_posted_after_it()
    {
        JournalEntry[] ledger =
        [
            LedgerFixture.ShareContribution(_shares, 5_500m, new DateOnly(2026, 1, 31)),
            LedgerFixture.ShareContribution(_shares, 5_500m, new DateOnly(2026, 2, 28)),
            LedgerFixture.ShareContribution(_shares, 5_500m, new DateOnly(2026, 3, 31)),
        ];

        LedgerBalances.NaturalBalanceAsAt(ledger, _shares, new DateOnly(2026, 2, 28))
            .Should().Be(Money.Kes(11_000m));
    }

    [Fact]
    public void A_balance_as_at_June_is_the_same_in_December()
    {
        // The reason for the whole append-only design. A member asks in December what their
        // shareholding was in June. A stored-balance system cannot answer - the June figure
        // was overwritten in July. This one sums to 30 June and gets the same answer every
        // time, however much has happened since.
        var june = new DateOnly(2026, 6, 30);

        var ledgerInJune = Contributions(monthsFromJanuary: 6);
        var asAtJuneComputedInJune = LedgerBalances.NaturalBalanceAsAt(ledgerInJune, _shares, june);

        var ledgerInDecember = Contributions(monthsFromJanuary: 12);
        var asAtJuneComputedInDecember = LedgerBalances.NaturalBalanceAsAt(ledgerInDecember, _shares, june);

        asAtJuneComputedInDecember.Should().Be(asAtJuneComputedInJune);
        asAtJuneComputedInDecember.Should().Be(Money.Kes(33_000m));
    }

    [Fact]
    public void A_member_with_no_entries_has_a_zero_shareholding_rather_than_an_error()
    {
        var joinedLastWeek = LedgerFixture.SharesOf("Peter Mwangi", Guid.NewGuid());

        LedgerBalances.NaturalBalanceAsAt([], joinedLastWeek, new DateOnly(2026, 9, 30))
            .Should().Be(Money.ZeroKes);
    }

    [Fact]
    public void A_loans_outstanding_balance_falls_as_instalments_are_paid()
    {
        // The emergency loan from the ledger: 25,000 + 10% over five months at 5,500.
        var receivable = LedgerFixture.ReceivableFor("AKB-2026-0007", Guid.NewGuid());

        JournalEntry[] ledger =
        [
            LedgerFixture.Disbursement(receivable, 25_000m, 2_500m, new DateOnly(2026, 9, 20)),
            LedgerFixture.Repayment(receivable, 5_500m, new DateOnly(2026, 11, 30)),
            LedgerFixture.Repayment(receivable, 5_500m, new DateOnly(2026, 12, 31)),
        ];

        // October is the grace month, so nothing is due and the balance is still the full
        // amount repayable.
        LedgerBalances.NaturalBalanceAsAt(ledger, receivable, new DateOnly(2026, 10, 31))
            .Should().Be(Money.Kes(27_500m));

        LedgerBalances.NaturalBalanceAsAt(ledger, receivable, new DateOnly(2026, 12, 31))
            .Should().Be(Money.Kes(16_500m));
    }

    [Fact]
    public void Movement_between_two_dates_reports_a_period_rather_than_a_position()
    {
        // Income and expenditure reports a period; the balance sheet reports a position.
        JournalEntry[] ledger =
        [
            LedgerFixture.ShareContribution(_shares, 5_500m, new DateOnly(2026, 1, 31)),
            LedgerFixture.ShareContribution(_shares, 5_500m, new DateOnly(2026, 2, 28)),
            LedgerFixture.ShareContribution(_shares, 5_500m, new DateOnly(2026, 3, 31)),
        ];

        LedgerBalances.MovementBetween(
                ledger, _shares.Id, new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 31), Currency.Kes)
            .Should().Be(Money.Kes(-11_000m));
    }

    [Fact]
    public void A_period_that_ends_before_it_starts_is_refused()
    {
        var movement = () => LedgerBalances.MovementBetween(
            [], _shares.Id, new DateOnly(2026, 3, 31), new DateOnly(2026, 2, 1), Currency.Kes);

        movement.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_trial_balance_is_zero_because_it_cannot_be_anything_else()
    {
        // Every entry balances by construction, so every sum of entries balances. This test
        // can only fail if something wrote to the database without going through
        // JournalEntry - which is exactly the failure worth catching.
        var receivable = LedgerFixture.ReceivableFor("AKB-2026-0007", Guid.NewGuid());

        JournalEntry[] ledger =
        [
            LedgerFixture.ShareContribution(_shares, 5_500m, new DateOnly(2026, 1, 31)),
            LedgerFixture.Disbursement(receivable, 25_000m, 2_500m, new DateOnly(2026, 9, 20)),
            LedgerFixture.Repayment(receivable, 5_500m, new DateOnly(2026, 11, 30)),
        ];

        LedgerBalances.TrialBalanceDifferenceAsAt(ledger, new DateOnly(2026, 12, 31), Currency.Kes)
            .Should().Be(Money.ZeroKes);
    }

    [Fact]
    public void A_reversal_puts_the_balance_back_exactly_where_it_was()
    {
        var original = LedgerFixture.ShareContribution(_shares, 5_500m, new DateOnly(2026, 9, 30));

        var reversal = original.Reverse(
            new DateOnly(2026, 10, 3),
            "Posted against the wrong member",
            LedgerFixture.Clerk,
            LedgerFixture.Now);

        JournalEntry[] ledger = [original, reversal];

        // As at September the contribution still stands: September's figures do not change
        // because October found a mistake.
        LedgerBalances.NaturalBalanceAsAt(ledger, _shares, new DateOnly(2026, 9, 30))
            .Should().Be(Money.Kes(5_500m));

        LedgerBalances.NaturalBalanceAsAt(ledger, _shares, new DateOnly(2026, 10, 31))
            .Should().Be(Money.ZeroKes);
    }

    private JournalEntry[] Contributions(int monthsFromJanuary) =>
        [
            .. Enumerable.Range(1, monthsFromJanuary).Select(month =>
                LedgerFixture.ShareContribution(
                    _shares,
                    5_500m,
                    new DateOnly(2026, month, DateTime.DaysInMonth(2026, month)))),
        ];
}
