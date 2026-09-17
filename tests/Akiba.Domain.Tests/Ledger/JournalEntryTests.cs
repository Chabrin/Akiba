using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;

namespace Akiba.Domain.Tests.Ledger;

/// <summary>
/// The invariant everything else rests on: an entry whose lines do not sum to zero cannot be
/// constructed.
/// </summary>
public sealed class JournalEntryTests
{
    private static readonly DateOnly September = new(2026, 9, 20);

    [Fact]
    public void An_unbalanced_entry_cannot_be_constructed()
    {
        // Not "is rejected on save". Not "fails validation". Cannot be constructed - there is
        // no moment at which an unbalanced JournalEntry object exists.
        var post = () => JournalEntry.Post(
            September,
            "Share contribution",
            SourceDocument.None,
            LedgerFixture.Clerk,
            LedgerFixture.Now,
            [
                JournalLine.Debit(LedgerFixture.Bank.Id, Money.Kes(5_500m)),
                JournalLine.Credit(LedgerFixture.UnallocatedReceipts.Id, Money.Kes(5_000m)),
            ]);

        post.Should().Throw<UnbalancedJournalEntryException>()
            .Which.Difference.Should().Be(Money.Kes(500m));
    }

    [Fact]
    public void The_failure_says_which_way_it_is_out_and_by_how_much()
    {
        // The message is read by whoever is debugging the code that built the entry, so it
        // states the difference and lists the lines rather than just refusing.
        var post = () => JournalEntry.Post(
            September,
            "Instalment",
            SourceDocument.None,
            LedgerFixture.Clerk,
            LedgerFixture.Now,
            [
                JournalLine.Debit(LedgerFixture.Bank.Id, Money.Kes(5_000m)),
                JournalLine.Credit(LedgerFixture.UnallocatedReceipts.Id, Money.Kes(5_500m)),
            ]);

        post.Should().Throw<UnbalancedJournalEntryException>()
            .WithMessage("*Credits exceed debits by KES 500.00*");
    }

    [Fact]
    public void An_entry_that_is_out_by_a_single_cent_is_still_refused()
    {
        // This is the case Money.Allocate exists to prevent, and the reason it is not
        // optional: a naive three-way split of 100 leaves the entry a cent short.
        var post = () => JournalEntry.Post(
            September,
            "Dividend allocation",
            SourceDocument.None,
            LedgerFixture.Clerk,
            LedgerFixture.Now,
            [
                JournalLine.Debit(LedgerFixture.Bank.Id, Money.Kes(100m)),
                JournalLine.Credit(LedgerFixture.UnallocatedReceipts.Id, Money.Kes(33.33m)),
                JournalLine.Credit(LedgerFixture.UnallocatedReceipts.Id, Money.Kes(33.33m)),
                JournalLine.Credit(LedgerFixture.UnallocatedReceipts.Id, Money.Kes(33.33m)),
            ]);

        post.Should().Throw<UnbalancedJournalEntryException>()
            .Which.Difference.Should().Be(Money.Kes(0.01m));
    }

    [Fact]
    public void An_allocation_produced_by_Money_Allocate_always_balances()
    {
        // The same entry, split properly.
        var shares = Money.Kes(100m).Allocate(3);

        var entry = JournalEntry.Post(
            September,
            "Dividend allocation",
            SourceDocument.None,
            LedgerFixture.Clerk,
            LedgerFixture.Now,
            [
                JournalLine.Debit(LedgerFixture.Bank.Id, Money.Kes(100m)),
                .. shares.Select(share => JournalLine.Credit(LedgerFixture.UnallocatedReceipts.Id, share)),
            ]);

        entry.Total.Should().Be(Money.Kes(100m));
    }

    [Fact]
    public void An_entry_needs_at_least_two_lines()
    {
        var post = () => JournalEntry.Post(
            September,
            "Nothing in particular",
            SourceDocument.None,
            LedgerFixture.Clerk,
            LedgerFixture.Now,
            [JournalLine.Debit(LedgerFixture.Bank.Id, Money.ZeroKes)]);

        post.Should().Throw<ArgumentException>()
            .WithMessage("*at least two lines*");
    }

    [Fact]
    public void An_entry_records_who_posted_it()
    {
        var post = () => JournalEntry.Post(
            September,
            "Share contribution",
            SourceDocument.None,
            default,
            LedgerFixture.Now,
            [
                JournalLine.Debit(LedgerFixture.Bank.Id, Money.Kes(5_500m)),
                JournalLine.Credit(LedgerFixture.UnallocatedReceipts.Id, Money.Kes(5_500m)),
            ]);

        post.Should().Throw<ArgumentException>()
            .WithMessage("*not an audit trail*");
    }

    [Fact]
    public void An_entry_needs_a_narration()
    {
        var post = () => JournalEntry.Post(
            September,
            "   ",
            SourceDocument.None,
            LedgerFixture.Clerk,
            LedgerFixture.Now,
            [
                JournalLine.Debit(LedgerFixture.Bank.Id, Money.Kes(5_500m)),
                JournalLine.Credit(LedgerFixture.UnallocatedReceipts.Id, Money.Kes(5_500m)),
            ]);

        post.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Lines_must_all_be_in_one_currency()
    {
        var post = () => JournalEntry.Post(
            September,
            "Mixed",
            SourceDocument.None,
            LedgerFixture.Clerk,
            LedgerFixture.Now,
            [
                JournalLine.Debit(LedgerFixture.Bank.Id, Money.Kes(100m)),
                JournalLine.Credit(LedgerFixture.UnallocatedReceipts.Id, new Money(100m, Currency.Of("USD"))),
            ]);

        post.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void A_line_will_not_take_a_negative_amount()
    {
        // Debit and Credit both take a positive figure and apply the sign themselves, so a
        // negative argument means the caller has the direction confused.
        var line = () => JournalLine.Debit(LedgerFixture.Bank.Id, Money.Kes(-100m));

        line.Should().Throw<ArgumentException>()
            .WithMessage("*other side*");
    }

    [Fact]
    public void A_line_is_rounded_when_it_is_posted()
    {
        // Posting is the point at which rounding happens, so the stored figure is already at
        // posting precision and every derived balance is an exact sum of exact figures.
        var line = JournalLine.Debit(LedgerFixture.Bank.Id, Money.Kes(2_500.005m));

        line.SignedAmount.Should().Be(Money.Kes(2_500.01m));
    }

    [Fact]
    public void A_three_line_disbursement_balances()
    {
        // The real shape of a disbursement: the member owes principal plus flat interest, the
        // bank pays out the principal, and the difference is recognised as income at once.
        var receivable = LedgerFixture.ReceivableFor("AKB-2026-0007", Guid.NewGuid());

        var entry = LedgerFixture.Disbursement(receivable, principal: 25_000m, interest: 2_500m, September);

        entry.Lines.Should().HaveCount(3);
        entry.Total.Should().Be(Money.Kes(27_500m));
        entry.Lines.Select(line => line.SignedAmount).Sum(Currency.Kes).Should().Be(Money.ZeroKes);
    }

    [Fact]
    public void Posting_an_entry_raises_an_event()
    {
        var entry = LedgerFixture.ShareContribution(
            LedgerFixture.SharesOf("Grace Njeri", Guid.NewGuid()), 5_500m, September);

        entry.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<JournalEntryPosted>()
            .Which.Total.Should().Be(Money.Kes(5_500m));
    }

    [Fact]
    public void The_value_date_defaults_to_the_entry_date_but_can_differ()
    {
        // A cheque deposited in June and confirmed on a quarterly statement in September has
        // a June value date and a September entry date.
        var deposited = new DateOnly(2026, 6, 12);
        var confirmed = new DateOnly(2026, 9, 30);

        var entry = JournalEntry.Post(
            confirmed,
            "Cheque cleared",
            SourceDocument.Cheque("000512"),
            LedgerFixture.Clerk,
            LedgerFixture.Now,
            [
                JournalLine.Debit(LedgerFixture.Bank.Id, Money.Kes(20_000m)),
                JournalLine.Credit(LedgerFixture.UnallocatedReceipts.Id, Money.Kes(20_000m)),
            ],
            valueDate: deposited);

        entry.EntryDate.Should().Be(confirmed);
        entry.ValueDate.Should().Be(deposited);
    }

    [Fact]
    public void Rehydrating_an_entry_that_no_longer_balances_fails_loudly()
    {
        // A stored entry that does not balance means the data was tampered with or a
        // migration went wrong. That has to surface on read, not as a quietly wrong trial
        // balance three months later.
        var rehydrate = () => JournalEntry.Rehydrate(
            JournalEntryId.New(),
            September,
            September,
            "Tampered",
            SourceDocument.None,
            LedgerFixture.Clerk,
            LedgerFixture.Now,
            [
                JournalLine.Rehydrate(LedgerFixture.Bank.Id, Money.Kes(5_500m), null),
                JournalLine.Rehydrate(LedgerFixture.UnallocatedReceipts.Id, Money.Kes(-5_000m), null),
            ],
            reverses: null);

        rehydrate.Should().Throw<UnbalancedJournalEntryException>();
    }
}
