using Akiba.Application;
using Akiba.Application.Abstractions;
using Akiba.Application.Dividends;
using Akiba.Application.Ledger;
using Akiba.Application.Lending;
using Akiba.Application.Members;
using Akiba.Application.Receipting;
using Akiba.Domain.Common;
using Akiba.Domain.Dividends;
using Akiba.Domain.Financial;
using Akiba.Domain.Lending;
using Akiba.Domain.Membership;
using Akiba.Domain.Receipting;
using Akiba.Infrastructure;
using Akiba.Infrastructure.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Akiba.Infrastructure.Tests.Persistence;

/// <summary>
/// The dividend run against a real database: compute, review, approve, post.
/// </summary>
/// <remarks>
/// The sequence is the point. A dividend is never posted automatically however obviously
/// correct its arithmetic, because the value of the three steps is that three officials looked
/// at the figure before it became every member's entitlement.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class DividendTests : IAsyncLifetime
{
    private static readonly Actor Clerk =
        new(Guid.Parse("0000A11B-0000-0000-0000-000000000001"), "Oliver Kamau");

    private static readonly Actor Treasurer =
        new(Guid.Parse("0000A11B-0000-0000-0000-000000000002"), "Wilfred Wamai");

    private static readonly Actor Chairman =
        new(Guid.Parse("0000A11B-0000-0000-0000-000000000003"), "Dennis Gitonga");

    private readonly PostgresFixture _postgres;
    private ServiceProvider _services = null!;
    private SwitchableCurrentUser _currentUser = null!;
    private ZoneId _zoneId;

    public DividendTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        await _postgres.ResetAsync();

        _currentUser = new SwitchableCurrentUser(Clerk);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAkibaApplication();
        services.AddAkibaInfrastructure(_postgres.ConnectionString);
        services.AddSingleton<ICurrentUser>(_currentUser);
        _services = services.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ChartOfAccountsSeeder>().SeedAsync();

        _zoneId = await CreateZoneAsync();
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task A_dividend_is_computed_reviewed_approved_and_only_then_posted()
    {
        var grace = await EnrolAsync("0001", "Grace", "Njeri");
        var peter = await EnrolAsync("0002", "Peter", "Mwangi");

        await ContributeAsync(grace, 20_000m, months: 12);
        await ContributeAsync(peter, 10_000m, months: 12);

        // One loan, so there is interest to distribute. 60,000 at a flat 10% recognises 6,000.
        await LendAsync(grace, 60_000m, new DateOnly(2026, 3, 10), "AKB-2026-0001");

        var runId = await SendAsync(new ComputeDividendRunCommand(2026));

        var draft = await SendAsync(new GetDividendRunQuery(runId));

        draft.Status.Should().Be(DividendRunStatus.Draft);
        draft.InterestEarned.Should().Be(Money.Kes(6_000m));
        draft.Distributable.Should().Be(Money.Kes(6_000m));
        draft.TotalAllocated.Should().Be(draft.Distributable);
        draft.ComputedBy.Should().Be(Clerk);

        // 240,000 and 120,000 at the year end: two thirds and one third.
        draft.Lines.Single(line => line.MembershipNumber == "0001").Amount
            .Should().Be(Money.Kes(4_000m));
        draft.Lines.Single(line => line.MembershipNumber == "0002").Amount
            .Should().Be(Money.Kes(2_000m));

        var postDraft = () => SendAsync(new PostDividendRunCommand(runId, new DateOnly(2027, 3, 14)));
        await postDraft.Should().ThrowAsync<InvalidOperationException>();

        _currentUser.Become(Treasurer);
        await SendAsync(new ReviewDividendRunCommand(runId));

        _currentUser.Become(Chairman);
        await SendAsync(new ApproveDividendRunCommand(runId));

        _currentUser.Become(Clerk);
        var entryId = await SendAsync(new PostDividendRunCommand(runId, new DateOnly(2027, 3, 14)));

        var posted = await SendAsync(new GetDividendRunQuery(runId));

        posted.Status.Should().Be(DividendRunStatus.Posted);
        posted.ReviewedBy.Should().Be(Treasurer);
        posted.ApprovedBy.Should().Be(Chairman);
        posted.PostedOn.Should().Be(new DateOnly(2027, 3, 14));

        // The entry is real, it balances, and the members are owed.
        await using var scope = _services.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IJournalRepository>();
        var entry = await journal.FindByIdAsync(entryId);

        entry.Should().NotBeNull();
        entry!.Lines.Should().HaveCount(3, because: "one debit and one credit per member");

        var trialBalance = await SendAsync(new GetTrialBalanceQuery(new DateOnly(2027, 3, 14)));
        trialBalance.Balances.Should().BeTrue();

        var dividendsPayable = trialBalance.Accounts
            .Single(account => account.Code == Domain.Ledger.ChartOfAccounts.DividendsPayable);

        dividendsPayable.Balance.Should().Be(Money.Kes(6_000m));
    }

    [Fact]
    public async Task The_same_year_cannot_be_paid_twice()
    {
        var memberId = await EnrolAsync("0001", "Grace", "Njeri");
        await ContributeAsync(memberId, 20_000m, months: 12);
        await LendAsync(memberId, 60_000m, new DateOnly(2026, 3, 10), "AKB-2026-0001");

        var runId = await SendAsync(new ComputeDividendRunCommand(2026));

        _currentUser.Become(Treasurer);
        await SendAsync(new ReviewDividendRunCommand(runId));
        _currentUser.Become(Chairman);
        await SendAsync(new ApproveDividendRunCommand(runId));
        _currentUser.Become(Clerk);
        await SendAsync(new PostDividendRunCommand(runId, new DateOnly(2027, 3, 14)));

        var again = () => SendAsync(new ComputeDividendRunCommand(2026));

        await again.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already been posted*");
    }

    [Fact]
    public async Task The_two_bases_can_be_compared_before_the_committee_picks_one()
    {
        // Built to settle open question 11 by showing what each answer costs a real member.
        var yearRound = await EnrolAsync("0001", "Grace", "Njeri");
        var lateJoiner = await EnrolAsync("0002", "Peter", "Mwangi");

        await ContributeAsync(yearRound, 10_000m, months: 12);
        await ContributeAsync(lateJoiner, 60_000m, months: 2, from: 11);

        await LendAsync(yearRound, 60_000m, new DateOnly(2026, 3, 10), "AKB-2026-0001");

        var runId = await SendAsync(new ComputeDividendRunCommand(2026));
        var comparison = await SendAsync(new CompareDividendBasesQuery(runId));

        comparison.RunsBasisName.Should().Be("Closing shareholding");
        comparison.OtherBasisName.Should().Be("Time-weighted shareholding");
        comparison.Lines.Should().HaveCount(2);

        // Both hold 120,000 at the year end, so closing balance splits it evenly. Time
        // weighting does not, and the comparison is what makes that visible.
        var grace = comparison.Lines.Single(line => line.MembershipNumber == "0001");

        grace.OnTheRunsBasis.Should().Be(Money.Kes(3_000m));
        grace.OnTheOtherBasis.Should().BeGreaterThan(grace.OnTheRunsBasis);
        comparison.LargestDifference.IsPositive.Should().BeTrue();

        // And nothing was posted by asking.
        var run = await SendAsync(new GetDividendRunQuery(runId));
        run.Status.Should().Be(DividendRunStatus.Draft);
    }

    [Fact]
    public async Task A_run_survives_a_round_trip_with_every_member_line_in_its_allocation_order()
    {
        var first = await EnrolAsync("0001", "Grace", "Njeri");
        var second = await EnrolAsync("0002", "Peter", "Mwangi");
        var third = await EnrolAsync("0003", "Alice", "Wanjiru");

        // Amounts chosen so the distribution does not divide evenly and the leftover cents
        // have to land somewhere specific.
        await ContributeAsync(first, 3_333m, months: 12);
        await ContributeAsync(second, 3_333m, months: 12);
        await ContributeAsync(third, 3_334m, months: 12);

        // Late in the year, so the shareholdings above are enough to cover it. 30,000 is the
        // smallest loan the graduated scale defines a term for.
        await LendAsync(first, 30_000m, new DateOnly(2026, 12, 10), "AKB-2026-0001");

        var runId = await SendAsync(new ComputeDividendRunCommand(2026));

        var reloaded = await SendAsync(new GetDividendRunQuery(runId));

        reloaded.TotalAllocated.Should().Be(reloaded.Distributable);
        reloaded.Lines.Select(line => line.MembershipNumber)
            .Should().Equal("0001", "0002", "0003");
    }

    [Fact]
    public async Task A_year_with_no_interest_has_no_dividend_and_says_so_plainly()
    {
        var memberId = await EnrolAsync("0001", "Grace", "Njeri");
        await ContributeAsync(memberId, 20_000m, months: 12);

        var compute = () => SendAsync(new ComputeDividendRunCommand(2026));

        await compute.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*nothing to distribute*");
    }

    [Fact]
    public async Task The_schedule_shows_the_workings_and_who_signed_it()
    {
        var grace = await EnrolAsync("0001", "Grace", "Njeri");
        var peter = await EnrolAsync("0002", "Peter", "Mwangi");

        await ContributeAsync(grace, 20_000m, months: 12);
        await ContributeAsync(peter, 10_000m, months: 12);
        await LendAsync(grace, 60_000m, new DateOnly(2026, 3, 10), "AKB-2026-0001");

        var runId = await SendAsync(new ComputeDividendRunCommand(2026));

        _currentUser.Become(Treasurer);
        await SendAsync(new ReviewDividendRunCommand(runId));

        var run = await SendAsync(new GetDividendRunQuery(runId));

        await using var scope = _services.CreateAsyncScope();
        var file = scope.ServiceProvider
            .GetRequiredService<Application.Reporting.IDividendScheduleWriter>()
            .Write(run);

        file.FileName.Should().Be("akiba-dividend-2026.xlsx");

        // Open it back and read the figures, so this tests the sheet rather than the bytes.
        using var stream = new MemoryStream(file.Content);
        using var workbook = new ClosedXML.Excel.XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();

        sheet.Cell(1, 1).GetString().Should().Be("AKIBA WELFARE SOCIETY");
        sheet.Cell(2, 1).GetString().Should().Contain("REVIEWED");

        var text = sheet.CellsUsed().Select(cell => cell.GetFormattedString()).ToList();

        text.Should().Contain("Interest earned on loans");
        text.Should().Contain("Less bank charges");
        text.Should().Contain("Available to distribute");

        // The approval block shows the gap as well as the signatures - an approval that has
        // not happened should be visible beside the ones that have.
        text.Should().Contain(Treasurer.DisplayName);
        text.Should().Contain("(not yet)");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    private async Task SendAsync(IRequest request)
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    private async Task<ZoneId> CreateZoneAsync()
    {
        await using var scope = _services.CreateAsyncScope();

        var zones = scope.ServiceProvider.GetRequiredService<IZoneRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var zone = Zone.CreateOffice("OFF", "Head office");
        zones.Add(zone);
        await unitOfWork.SaveChangesAsync();

        return zone.Id;
    }

    private Task<BorrowerId> EnrolAsync(string membershipNumber, string given, string family) =>
        SendAsync(new EnrolMemberCommand(
            membershipNumber, $"CAL/{membershipNumber}", given, family, null,
            "28765432", "0712345678", null, _zoneId, false));

    private async Task ContributeAsync(BorrowerId memberId, decimal amount, int months, int from = 1)
    {
        for (var month = from; month < from + months && month <= 12; month++)
        {
            var lastDay = new DateOnly(2026, month, DateTime.DaysInMonth(2026, month));

            var receiptId = await SendAsync(new RecordPayrollReceiptCommand(
                Money.Kes(amount), lastDay, $"P{month:D2}-{memberId.Value.ToString()[..4]}"));

            await SendAsync(new ClearReceiptCommand(receiptId, lastDay));
            await SendAsync(new AllocateReceiptCommand(
                receiptId, AllocationTarget.Shares, memberId.Value, Money.Kes(amount), lastDay));
        }
    }

    private async Task LendAsync(BorrowerId memberId, decimal principal, DateOnly on, string loanNumber)
    {
        var applicationId = await SendAsync(new ReceiveLoanApplicationCommand(
            memberId, LoanProduct.Normal, Money.Kes(principal), on.AddDays(-4),
            Money.Kes(300_000m), null));

        await using (var scope = _services.CreateAsyncScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<ILoanApplicationRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();

            var application = (await applications.FindByIdAsync(applicationId))!;

            application.AddGuarantee(new Domain.Guaranteeing.Guarantee(
                BorrowerId.New(), "Peter Mwangi", PayrollNumber.Of("CAL/0099"),
                Money.Kes(principal * 1.5m), Money.Kes(principal * 2m), on.AddDays(-5)));

            application.Submit();
            application.RecordDecision(
                new Actor(Guid.Parse("0000B22C-0000-0000-0000-000000000001"), "Mary Otieno"),
                ApprovalDecisionKind.Approve, clock.UtcNow);

            applications.Update(application);
            await unitOfWork.SaveChangesAsync();
        }

        await SendAsync(new ApproveLoanApplicationCommand(applicationId, null));

        await SendAsync(new DisburseLoanCommand(
            applicationId, loanNumber, on, "000431", "PV-2026-0112",
            ["Mr. Mutinda", "Mr. Kimathi"]));
    }

    /// <summary>
    /// A signed-in official the test can swap.
    /// </summary>
    /// <remarks>
    /// The dividend sequence is three people, and the aggregate refuses when the reviewer and
    /// the approver are the same. A fixed test user could not exercise it, and a test that
    /// could not exercise it would pass while the control was missing.
    /// </remarks>
    private sealed class SwitchableCurrentUser : ICurrentUser
    {
        private Actor _actor;

        public SwitchableCurrentUser(Actor actor) => _actor = actor;

        public Actor Actor => _actor;

        public bool IsAuthenticated => _actor.IsSpecified;

        public void Become(Actor actor) => _actor = actor;
    }
}
