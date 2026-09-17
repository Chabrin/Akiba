using Akiba.Application;
using Akiba.Application.Abstractions;
using Akiba.Application.Lending;
using Akiba.Application.Members;
using Akiba.Application.Receipting;
using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Ledger;
using Akiba.Domain.Lending;
using Akiba.Domain.Membership;
using Akiba.Domain.Receipting;
using Akiba.Infrastructure;
using Akiba.Infrastructure.Persistence;
using Akiba.Infrastructure.Persistence.Repositories;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Akiba.Infrastructure.Tests.Persistence;

/// <summary>
/// The whole system, driven through the application layer against a real PostgreSQL.
/// </summary>
/// <remarks>
/// Every other test checks one thing. These walk the paths an official actually walks - enrol
/// a member, take their deductions, lend to them, take the instalments back - and assert that
/// the ledger tells the truth at each step. It is the closest thing to watching Oliver use the
/// system.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class EndToEndFlowTests : IAsyncLifetime
{
    private static readonly Actor Clerk = new(Guid.Parse("0000A11B-0000-0000-0000-000000000001"), "Oliver Kamau");
    private static readonly Actor ZoneRep = new(Guid.Parse("0000B22C-0000-0000-0000-000000000001"), "Mary Otieno");
    private static readonly Actor OfficeRep = new(Guid.Parse("0000B22C-0000-0000-0000-000000000002"), "Samuel Kiptoo");

    private readonly PostgresFixture _postgres;
    private ServiceProvider _services = null!;

    public EndToEndFlowTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        await _postgres.ResetAsync();

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddAkibaApplication();
        services.AddAkibaInfrastructure(_postgres.ConnectionString);
        services.AddAkibaTestUser(Clerk);

        _services = services.BuildServiceProvider();

        // Seed the chart of accounts, exactly as startup does.
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ChartOfAccountsSeeder>().SeedAsync();
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task A_member_joins_contributes_borrows_and_repays_and_the_books_balance_throughout()
    {
        // The whole life of a small loan, end to end. Every assertion is a figure derived from
        // the ledger, never a stored total.
        var zoneId = await CreateZoneAsync();

        var memberId = await SendAsync(new EnrolMemberCommand(
            "0042", "CAL/0042", "Grace", "Njeri", "Wambui",
            "28765432", "0712345678", "grace.njeri@example.com", zoneId, IsLandlord: false));

        // --- Four months of share contributions ---------------------------
        foreach (var month in Enumerable.Range(1, 4))
        {
            var lastDay = new DateOnly(2026, month, DateTime.DaysInMonth(2026, month));

            var receiptId = await SendAsync(new RecordPayrollReceiptCommand(
                Money.Kes(20_000m), lastDay, $"00050{month}"));

            await SendAsync(new ClearReceiptCommand(receiptId, lastDay));
            await SendAsync(new AllocateReceiptCommand(
                receiptId, AllocationTarget.Shares, memberId.Value, Money.Kes(20_000m), lastDay));
        }

        var statement = await SendAsync(
            new GetMemberStatementQuery(memberId, new DateOnly(2026, 4, 30)));

        statement.Shareholding.Should().Be(Money.Kes(80_000m));
        statement.BorrowingLimit.Should().Be(Money.Kes(160_000m));
        statement.MembershipSince.Should().Be(
            new DateOnly(2026, 1, 31), because: "membership begins at the first contribution");
        statement.ShareMovements.Should().HaveCount(4);
        statement.ShareMovements[^1].RunningBalance.Should().Be(Money.Kes(80_000m));

        // --- Applies for a loan -------------------------------------------
        var applicationId = await SendAsync(new ReceiveLoanApplicationCommand(
            memberId, LoanProduct.Normal, Money.Kes(60_000m),
            new DateOnly(2026, 9, 10), Money.Kes(90_000m), RequestedTermMonths: null));

        var assessment = await SendAsync(
            new AssessLoanApplicationQuery(applicationId, new DateOnly(2026, 9, 10)));

        assessment.Terms.TotalRepayable.Should().Be(Money.Kes(66_000m));
        assessment.Terms.TermMonths.Should().Be(12);
        assessment.IsWithinBorrowingLimit.Should().BeTrue();
        assessment.Affordability!.IsWithinLimit.Should().BeTrue();

        // A normal loan needs a guarantor under the conservative default, and none has signed.
        assessment.CanBeApproved.Should().BeFalse();
        assessment.Blockers.Should().ContainSingle().Which.Should().Contain("uncovered");

        await AddGuarantorAsync(applicationId, "Peter Mwangi", Money.Kes(70_000m));
        await SubmitAsync(applicationId);
        await DecideAsync(applicationId, ZoneRep, ApprovalDecisionKind.Approve);
        await DecideAsync(applicationId, OfficeRep, ApprovalDecisionKind.Approve);

        var terms = await SendAsync(
            new ApproveLoanApplicationCommand(applicationId, ApprovedPrincipal: null));

        terms.TotalRepayable.Should().Be(Money.Kes(66_000m));

        // --- Disbursed ------------------------------------------------------
        var loanId = await SendAsync(new DisburseLoanCommand(
            applicationId, "AKB-2026-0007", new DateOnly(2026, 9, 20),
            "000431", "PV-2026-0112", ["Mr. Mutinda", "Mr. Kimathi"]));

        await using (var scope = _services.CreateAsyncScope())
        {
            var loans = scope.ServiceProvider.GetRequiredService<ILoanRepository>();
            var balances = scope.ServiceProvider.GetRequiredService<IBalanceQueries>();

            var loan = (await loans.FindByIdAsync(loanId))!;

            // The member owes principal plus interest from the day the cheque is drawn.
            (await balances.NaturalBalanceAsAtAsync(loan.ReceivableAccountId, new DateOnly(2026, 9, 30)))
                .Should().Be(Money.Kes(66_000m));

            // October is the grace month, so nothing is due and the balance has not moved.
            loan.Schedule.FirstDueDate.Should().Be(new DateOnly(2026, 11, 30));
            loan.Schedule.ExpectedPaidBy(new DateOnly(2026, 10, 31)).Should().Be(Money.ZeroKes);
        }

        // --- Two instalments -------------------------------------------------
        foreach (var due in new[] { new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 31) })
        {
            var receiptId = await SendAsync(new RecordPayrollReceiptCommand(
                Money.Kes(5_500m), due, $"0006{due.Month:D2}"));

            await SendAsync(new ClearReceiptCommand(receiptId, due));
            await SendAsync(new AllocateReceiptCommand(
                receiptId, AllocationTarget.LoanInstalment, loanId.Value, Money.Kes(5_500m), due));
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var loans = scope.ServiceProvider.GetRequiredService<ILoanRepository>();
            var balances = scope.ServiceProvider.GetRequiredService<IBalanceQueries>();
            var loan = (await loans.FindByIdAsync(loanId))!;

            (await balances.NaturalBalanceAsAtAsync(loan.ReceivableAccountId, new DateOnly(2026, 12, 31)))
                .Should().Be(Money.Kes(55_000m));

            // And the books balance across everything that has happened.
            (await balances.TrialBalanceDifferenceAsAtAsync(new DateOnly(2026, 12, 31)))
                .Should().Be(Money.ZeroKes);
        }

        // --- The statement from before has not changed ------------------------
        var aprilAgain = await SendAsync(
            new GetMemberStatementQuery(memberId, new DateOnly(2026, 4, 30)));

        aprilAgain.Shareholding.Should().Be(
            statement.Shareholding,
            because: "a figure stated as at April does not change because December happened");
        aprilAgain.Loans.Should().BeEmpty(because: "the loan did not exist in April");
    }

    [Fact]
    public async Task A_member_cannot_hold_more_than_two_running_loans()
    {
        var zoneId = await CreateZoneAsync();
        var memberId = await EnrolAndFundAsync(zoneId, shares: 500_000m);

        for (var index = 1; index <= 2; index++)
        {
            var id = await SendAsync(new ReceiveLoanApplicationCommand(
                memberId, LoanProduct.Normal, Money.Kes(60_000m),
                new DateOnly(2026, 9, 10), Money.Kes(400_000m), null));

            await AddGuarantorAsync(id, "Peter Mwangi", Money.Kes(70_000m));
            await SubmitAsync(id);
            await DecideAsync(id, ZoneRep, ApprovalDecisionKind.Approve);
            await SendAsync(new ApproveLoanApplicationCommand(id, null));
            await SendAsync(new DisburseLoanCommand(
                id, $"AKB-2026-001{index}", new DateOnly(2026, 9, 20),
                $"00043{index}", $"PV-2026-011{index}", ["Mr. Mutinda", "Mr. Kimathi"]));
        }

        var third = async () => await SendAsync(new ReceiveLoanApplicationCommand(
            memberId, LoanProduct.Normal, Money.Kes(60_000m),
            new DateOnly(2026, 9, 10), Money.Kes(400_000m), null));

        await third.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*and no more*");
    }

    [Fact]
    public async Task An_application_that_breaches_the_two_thirds_rule_is_refused_with_both_remedies()
    {
        var zoneId = await CreateZoneAsync();
        var memberId = await EnrolAndFundAsync(zoneId, shares: 500_000m);

        // 400,000 + 10% over 24 months is about 18,333 a month against a 20,000 salary.
        var applicationId = await SendAsync(new ReceiveLoanApplicationCommand(
            memberId, LoanProduct.Normal, Money.Kes(400_000m),
            new DateOnly(2026, 9, 10), Money.Kes(20_000m), null));

        await AddGuarantorAsync(applicationId, "Peter Mwangi", Money.Kes(440_000m));
        await SubmitAsync(applicationId);
        await DecideAsync(applicationId, ZoneRep, ApprovalDecisionKind.Approve);

        var approve = async () => await SendAsync(
            new ApproveLoanApplicationCommand(applicationId, null));

        var thrown = await approve.Should().ThrowAsync<InvalidOperationException>();

        // Both sanctioned remedies, and the figure that makes the first one actionable.
        thrown.Which.Message.Should().Contain("two-thirds limit");
        thrown.Which.Message.Should().Contain("largest affordable instalment");
        thrown.Which.Message.Should().Contain("reduce the monthly share deduction");
    }

    [Fact]
    public async Task Money_cannot_be_allocated_before_it_has_cleared()
    {
        // A cheque that has not matured has not brought any money in.
        var zoneId = await CreateZoneAsync();
        var memberId = await SendAsync(new EnrolMemberCommand(
            "0042", "CAL/0042", "Grace", "Njeri", null,
            "28765432", "0712345678", null, zoneId, false));

        var receiptId = await SendAsync(new RecordDirectDepositCommand(
            Money.Kes(5_500m), ReceiptMethod.Cheque, new DateOnly(2026, 9, 12),
            "000512", "G. NJERI", new DateOnly(2026, 9, 19)));

        var allocate = async () => await SendAsync(new AllocateReceiptCommand(
            receiptId, AllocationTarget.Shares, memberId.Value,
            Money.Kes(5_500m), new DateOnly(2026, 9, 12)));

        await allocate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*has not cleared*");
    }

    [Fact]
    public async Task Nothing_reaches_the_ledger_until_a_receipt_clears()
    {
        var zoneId = await CreateZoneAsync();
        await SendAsync(new EnrolMemberCommand(
            "0042", "CAL/0042", "Grace", "Njeri", null,
            "28765432", "0712345678", null, zoneId, false));

        await SendAsync(new RecordDirectDepositCommand(
            Money.Kes(5_500m), ReceiptMethod.Cheque, new DateOnly(2026, 9, 12),
            "000512", "G. NJERI", new DateOnly(2026, 9, 19)));

        await using var scope = _services.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IJournalRepository>();

        // The ledger holds money Akiba has, not money it expects.
        (await journal.AsOfAsync(new DateOnly(2026, 12, 31))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_member_leaving_CAL_is_told_whether_their_funds_can_be_released()
    {
        var zoneId = await CreateZoneAsync();
        var memberId = await SendAsync(new EnrolMemberCommand(
            "0042", "CAL/0042", "Grace", "Njeri", null,
            "28765432", "0712345678", null, zoneId, false));

        var outcome = await SendAsync(
            new RecordMemberExitCommand(memberId, new DateOnly(2026, 9, 30)));

        outcome.MemberName.Should().Be("Grace Njeri");
        outcome.FundsMayBeReleased.Should().BeTrue(because: "she guarantees nothing");
        outcome.ClerkTask.Should().Contain("may be released");
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

        var zone = Zone.Create("KLE", "Kileleshwa");
        zones.Add(zone);
        await unitOfWork.SaveChangesAsync();

        return zone.Id;
    }

    private async Task<BorrowerId> EnrolAndFundAsync(ZoneId zoneId, decimal shares)
    {
        var memberId = await SendAsync(new EnrolMemberCommand(
            "0042", "CAL/0042", "Grace", "Njeri", null,
            "28765432", "0712345678", null, zoneId, false));

        var on = new DateOnly(2026, 1, 31);
        var receiptId = await SendAsync(new RecordPayrollReceiptCommand(Money.Kes(shares), on, "000501"));

        await SendAsync(new ClearReceiptCommand(receiptId, on));
        await SendAsync(new AllocateReceiptCommand(
            receiptId, AllocationTarget.Shares, memberId.Value, Money.Kes(shares), on));

        return memberId;
    }

    /// <summary>
    /// Adds a guarantor straight through the repository. Recording guarantors from the form is
    /// a panel concern that has no command yet.
    /// </summary>
    private async Task AddGuarantorAsync(LoanApplicationId applicationId, string name, Money guaranteed)
    {
        await using var scope = _services.CreateAsyncScope();

        var applications = scope.ServiceProvider.GetRequiredService<ILoanApplicationRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var application = (await applications.FindByIdAsync(applicationId))!;

        application.AddGuarantee(new Domain.Guaranteeing.Guarantee(
            BorrowerId.New(), name, PayrollNumber.Of("CAL/0099"),
            guaranteed, guaranteed * 2m, new DateOnly(2026, 9, 9)));

        applications.Update(application);
        await unitOfWork.SaveChangesAsync();
    }

    private async Task SubmitAsync(LoanApplicationId applicationId)
    {
        await using var scope = _services.CreateAsyncScope();

        var applications = scope.ServiceProvider.GetRequiredService<ILoanApplicationRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var application = (await applications.FindByIdAsync(applicationId))!;
        application.Submit();

        applications.Update(application);
        await unitOfWork.SaveChangesAsync();
    }

    /// <summary>
    /// Records a decision as a particular representative, since the shared test user is the
    /// clerk and a representative cannot decide twice.
    /// </summary>
    private async Task DecideAsync(
        LoanApplicationId applicationId, Actor approver, ApprovalDecisionKind decision)
    {
        await using var scope = _services.CreateAsyncScope();

        var applications = scope.ServiceProvider.GetRequiredService<ILoanApplicationRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var application = (await applications.FindByIdAsync(applicationId))!;
        application.RecordDecision(approver, decision, clock.UtcNow);

        applications.Update(application);
        await unitOfWork.SaveChangesAsync();
    }
}
