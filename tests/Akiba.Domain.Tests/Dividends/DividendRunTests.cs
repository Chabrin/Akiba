using Akiba.Domain.Dividends;
using Akiba.Domain.Financial;
using Akiba.Domain.Tests.Ledger;

namespace Akiba.Domain.Tests.Dividends;

/// <summary>
/// Interest earned, net of bank charges, divided by total shareholding and allocated by
/// shareholding. Computed, reviewed, approved, and only then posted.
/// </summary>
public sealed class DividendRunTests
{
    [Fact]
    public void Bank_charges_are_deducted_before_anything_is_shared_out()
    {
        // The account earns no interest but incurs charges, and the charges come off the
        // interest earned before the dividend is worked out.
        var run = Compute(interestEarned: 120_000m, bankCharges: 4_800m, Member("0001", 100_000m));

        run.InterestEarned.Should().Be(Money.Kes(120_000m));
        run.BankCharges.Should().Be(Money.Kes(4_800m));
        run.Distributable.Should().Be(Money.Kes(115_200m));
    }

    [Fact]
    public void What_is_distributed_equals_what_is_available_to_the_cent()
    {
        // Three members, a total that does not divide evenly, and the distribution must still
        // sum back exactly - the posting is a journal entry, and a lost cent would simply fail
        // to post.
        var run = Compute(
            interestEarned: 100_000m,
            bankCharges: 0m,
            Member("0001", 33_333m),
            Member("0002", 33_333m),
            Member("0003", 33_334m));

        run.TotalAllocated.Should().Be(run.Distributable);
        run.Lines.Should().HaveCount(3);
    }

    [Fact]
    public void The_same_figures_produce_the_same_split_every_time()
    {
        // The treasurer reviews a computed run and then the chairman approves it, possibly
        // days later. If recomputing moved a cent between two members, neither could review it.
        var first = Compute(100_000m, 0m, Member("0001", 7m), Member("0002", 11m), Member("0003", 13m));
        var second = Compute(100_000m, 0m, Member("0001", 7m), Member("0002", 11m), Member("0003", 13m));

        first.Lines.Select(line => line.Amount)
            .Should().Equal(second.Lines.Select(line => line.Amount));
    }

    [Fact]
    public void A_year_with_no_surplus_has_no_dividend_and_says_so()
    {
        var compute = () => Compute(interestEarned: 3_000m, bankCharges: 5_000m, Member("0001", 100_000m));

        compute.Should().Throw<InvalidOperationException>()
            .WithMessage("*nothing to distribute*");
    }

    [Fact]
    public void The_closing_basis_pays_a_November_joiner_the_same_rate_as_a_year_round_member()
    {
        // Both hold 60,000 at the year end, so on closing balance they get the same. That is
        // the literal reading of the only rule anybody wrote down, and it is why the committee
        // needs to look at it.
        var yearRound = Member("0001", 60_000m, Enumerable.Repeat(60_000m, 12).ToArray());
        var novemberJoiner = Member("0002", 60_000m,
            [0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 30_000m, 60_000m]);

        var run = DividendRun.Compute(
            2026, Money.Kes(100_000m), Money.ZeroKes,
            [yearRound, novemberJoiner],
            new ClosingShareholdingBasis(),
            LedgerFixture.Clerk, LedgerFixture.Now);

        run.Lines[0].Amount.Should().Be(run.Lines[1].Amount);
        run.BasisName.Should().Be("Closing shareholding");
    }

    [Fact]
    public void The_time_weighted_basis_pays_the_year_round_member_more()
    {
        var yearRound = Member("0001", 60_000m, Enumerable.Repeat(60_000m, 12).ToArray());
        var novemberJoiner = Member("0002", 60_000m,
            [0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 30_000m, 60_000m]);

        var run = DividendRun.Compute(
            2026, Money.Kes(100_000m), Money.ZeroKes,
            [yearRound, novemberJoiner],
            new TimeWeightedShareholdingBasis(),
            LedgerFixture.Clerk, LedgerFixture.Now);

        run.Lines[0].Amount.Should().BeGreaterThan(run.Lines[1].Amount);
        run.TotalAllocated.Should().Be(run.Distributable);
        run.BasisName.Should().Be("Time-weighted shareholding");
    }

    [Fact]
    public void A_run_cannot_be_posted_until_it_has_been_reviewed_and_approved()
    {
        var run = Compute(100_000m, 0m, Member("0001", 50_000m));

        var postDraft = () => run.MarkPosted(new DateOnly(2027, 3, 14), LedgerFixture.Now);
        postDraft.Should().Throw<InvalidOperationException>().WithMessage("*reviewed*");

        run.Review(LedgerFixture.Treasurer, LedgerFixture.Now);

        var postReviewed = () => run.MarkPosted(new DateOnly(2027, 3, 14), LedgerFixture.Now);
        postReviewed.Should().Throw<InvalidOperationException>().WithMessage("*approved*");

        run.Approve(LedgerFixture.Chairman, LedgerFixture.Now);
        run.MayBePosted.Should().BeTrue();

        run.MarkPosted(new DateOnly(2027, 3, 14), LedgerFixture.Now);
        run.Status.Should().Be(DividendRunStatus.Posted);
        run.DomainEvents.OfType<DividendDeclared>().Should().ContainSingle();
    }

    [Fact]
    public void The_chairman_cannot_approve_a_run_they_reviewed_themselves()
    {
        // The whole value of the sequence is that two people looked at the figure.
        var run = Compute(100_000m, 0m, Member("0001", 50_000m));

        run.Review(LedgerFixture.Treasurer, LedgerFixture.Now);

        var approve = () => run.Approve(LedgerFixture.Treasurer, LedgerFixture.Now);

        approve.Should().Throw<InvalidOperationException>().WithMessage("*cannot also approve*");
    }

    [Fact]
    public void Approving_before_reviewing_is_refused()
    {
        var run = Compute(100_000m, 0m, Member("0001", 50_000m));

        var approve = () => run.Approve(LedgerFixture.Chairman, LedgerFixture.Now);

        approve.Should().Throw<InvalidOperationException>()
            .WithMessage("*treasurer reviews*");
    }

    [Fact]
    public void A_posted_dividend_cannot_be_posted_twice_or_withdrawn()
    {
        var run = Compute(100_000m, 0m, Member("0001", 50_000m));

        run.Review(LedgerFixture.Treasurer, LedgerFixture.Now);
        run.Approve(LedgerFixture.Chairman, LedgerFixture.Now);
        run.MarkPosted(new DateOnly(2027, 3, 14), LedgerFixture.Now);

        var again = () => run.MarkPosted(new DateOnly(2027, 3, 15), LedgerFixture.Now);
        again.Should().Throw<InvalidOperationException>().WithMessage("*already posted*");

        var withdraw = () => run.Withdraw("Changed our minds.");
        withdraw.Should().Throw<InvalidOperationException>().WithMessage("*Reverse the journal entry*");
    }

    [Fact]
    public void A_withdrawn_run_is_kept_with_its_reason()
    {
        var run = Compute(100_000m, 0m, Member("0001", 50_000m));

        run.Withdraw("Computed before the December statement had been reconciled.");

        run.Status.Should().Be(DividendRunStatus.Withdrawn);
        run.Verdict.Should().Contain("December statement");
    }

    [Fact]
    public void The_rate_shown_on_the_schedule_is_never_what_an_entitlement_is_computed_from()
    {
        // The rate is rounded for display. Multiplying by it would be how a distribution stops
        // summing to the total, so the entitlements come from Allocate and the rate is only
        // ever printed.
        var run = Compute(
            interestEarned: 100_000m,
            bankCharges: 0m,
            Member("0001", 333_333m),
            Member("0002", 666_667m));

        run.RatePerShilling.Should().BeApproximately(0.1m, 0.000001m);
        run.TotalAllocated.Should().Be(Money.Kes(100_000m));
        run.TotalBasis.Should().Be(Money.Kes(1_000_000m));
    }

    private static DividendRun Compute(
        decimal interestEarned, decimal bankCharges, params MemberShareholdingOverYear[] members) =>
        DividendRun.Compute(
            2026,
            Money.Kes(interestEarned),
            Money.Kes(bankCharges),
            members,
            new ClosingShareholdingBasis(),
            LedgerFixture.Clerk,
            LedgerFixture.Now);

    private static MemberShareholdingOverYear Member(
        string membershipNumber, decimal closing, decimal[]? monthEnds = null) =>
        new(
            Guid.NewGuid(),
            membershipNumber,
            $"Member {membershipNumber}",
            Guid.NewGuid(),
            Money.Kes(closing),
            [.. (monthEnds ?? [.. Enumerable.Repeat(closing, 12)]).Select(Money.Kes)]);
}
