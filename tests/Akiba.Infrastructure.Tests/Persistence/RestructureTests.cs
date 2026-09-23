using Akiba.Application;
using Akiba.Application.Abstractions;
using Akiba.Application.Ledger;
using Akiba.Application.Lending;
using Akiba.Application.Members;
using Akiba.Application.Receipting;
using Akiba.Domain.Common;
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
/// Restructuring a loan, against a real database.
/// </summary>
/// <remarks>
/// A restructure rearranges a debt; it does not lend again. The thing worth proving is that
/// nothing touches Bank and the books still balance afterwards - a restructure that quietly
/// moved money would be very hard to spot and very hard to explain.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class RestructureTests : IAsyncLifetime
{
    private static readonly Actor Clerk =
        new(Guid.Parse("0000A11B-0000-0000-0000-000000000001"), "Oliver Kamau");

    private readonly PostgresFixture _postgres;
    private ServiceProvider _services = null!;
    private ZoneId _zoneId;

    public RestructureTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        await _postgres.ResetAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAkibaApplication();
        services.AddAkibaInfrastructure(_postgres.ConnectionString);
        services.AddAkibaTestUser(Clerk);
        _services = services.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ChartOfAccountsSeeder>().SeedAsync();

        _zoneId = await CreateZoneAsync();
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task A_loan_not_paid_down_far_enough_is_refused_with_the_figure()
    {
        // "It cannot be restructured" is not something an official can act on. "Only 12.5% has
        // been repaid, and it must reach 50%" is.
        var (memberId, loanId) = await LendAsync();

        var proposal = await SendAsync(new AssessRestructureQuery(loanId));

        proposal.IsPermitted.Should().BeFalse();
        proposal.Assessment.Refusal.Should().Be(RestructureRefusal.NotPaidDownFarEnough);
        proposal.Assessment.Explanation.Should().Contain("50%");
        proposal.ProposedTerms.Should().BeNull();

        var attempt = () => SendAsync(new RestructureLoanCommand(
            loanId, "AKB-2026-0002R", new DateOnly(2026, 6, 30), "PV-2026-0500"));

        await attempt.Should().ThrowAsync<InvalidOperationException>().WithMessage("*50%*");

        _ = memberId;
    }

    [Fact]
    public async Task A_restructure_moves_the_balance_and_never_touches_the_bank()
    {
        var (memberId, loanId) = await LendAsync();

        // Pay it down past half. 175,000 at a flat 10% is 192,500 repayable.
        await RepayAsync(memberId, loanId, 120_000m, new DateOnly(2026, 6, 30));

        var bankBefore = await BankBalanceAsync(new DateOnly(2026, 6, 30));

        var proposal = await SendAsync(new AssessRestructureQuery(loanId));

        proposal.IsPermitted.Should().BeTrue();
        proposal.Assessment.OutstandingBalance.Should().Be(Money.Kes(72_500m));
        proposal.Assessment.Fee.Should().Be(Money.Kes(3_625m), because: "the fee is 5%");
        proposal.ProposedTerms!.Interest.Should().Be(Money.ZeroKes, because: "no fresh interest");

        var replacementId = await SendAsync(new RestructureLoanCommand(
            loanId, "AKB-2026-0001R", new DateOnly(2026, 6, 30), "PV-2026-0500"));

        // The balance moved, and Bank did not.
        var bankAfter = await BankBalanceAsync(new DateOnly(2026, 6, 30));
        bankAfter.Should().Be(bankBefore, because: "a restructure lends nothing");

        var trialBalance = await SendAsync(new GetTrialBalanceQuery(new DateOnly(2026, 6, 30)));
        trialBalance.Balances.Should().BeTrue();

        await using var scope = _services.CreateAsyncScope();
        var loans = scope.ServiceProvider.GetRequiredService<ILoanRepository>();

        var original = (await loans.FindByIdAsync(loanId))!;
        var replacement = (await loans.FindByIdAsync(replacementId))!;

        original.Status.Should().Be(LoanStatus.Restructured);
        original.HasBeenRestructured.Should().BeTrue();

        replacement.Restructures.Should().Be(loanId);
        replacement.CameFromRestructure.Should().BeTrue();
        replacement.Terms.TotalRepayable.Should().Be(Money.Kes(72_500m));

        // No money moved, so there is no cheque - and no invented one either.
        replacement.Cheque.Should().BeNull();

        // The guarantors carried across rather than re-signing.
        replacement.Guarantees.Should().HaveCount(original.Guarantees.Count);
    }

    [Fact]
    public async Task The_original_stays_readable_after_it_is_closed()
    {
        // A member can still see every payment they made against the loan they had.
        var (memberId, loanId) = await LendAsync();
        await RepayAsync(memberId, loanId, 120_000m, new DateOnly(2026, 6, 30));

        await SendAsync(new RestructureLoanCommand(
            loanId, "AKB-2026-0001R", new DateOnly(2026, 6, 30), "PV-2026-0500"));

        var statement = await SendAsync(
            new GetMemberStatementQuery(memberId, new DateOnly(2026, 5, 31)));

        statement.Loans.Should().Contain(loan => loan.LoanNumber == "AKB-2026-0001",
            because: "as at May it was still running, and the statement must still say so");
    }

    [Fact]
    public async Task A_loan_may_be_restructured_once_only()
    {
        var (memberId, loanId) = await LendAsync();
        await RepayAsync(memberId, loanId, 120_000m, new DateOnly(2026, 6, 30));

        await SendAsync(new RestructureLoanCommand(
            loanId, "AKB-2026-0001R", new DateOnly(2026, 6, 30), "PV-2026-0500"));

        var again = () => SendAsync(new RestructureLoanCommand(
            loanId, "AKB-2026-0001RR", new DateOnly(2026, 7, 31), "PV-2026-0501"));

        await again.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_replacement_cannot_reuse_a_loan_number()
    {
        var (memberId, loanId) = await LendAsync();
        await RepayAsync(memberId, loanId, 120_000m, new DateOnly(2026, 6, 30));

        var clash = () => SendAsync(new RestructureLoanCommand(
            loanId, "AKB-2026-0001", new DateOnly(2026, 6, 30), "PV-2026-0500"));

        await clash.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already used*");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task<Money> BankBalanceAsync(DateOnly asAt)
    {
        await using var scope = _services.CreateAsyncScope();

        var accounts = scope.ServiceProvider.GetRequiredService<IAkibaAccounts>();
        var balances = scope.ServiceProvider.GetRequiredService<IBalanceQueries>();

        return await balances.NaturalBalanceAsAtAsync(await accounts.BankAsync(), asAt);
    }

    private async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
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

    /// <summary>A member with enough shares, and a 175,000 loan running.</summary>
    private async Task<(BorrowerId MemberId, LoanId LoanId)> LendAsync()
    {
        var memberId = await SendAsync(new EnrolMemberCommand(
            "0001", "CAL/0001", "Grace", "Njeri", null,
            "28765432", "0712345678", null, _zoneId, false));

        for (var month = 1; month <= 2; month++)
        {
            await ContributeAsync(memberId, 100_000m, new DateOnly(2026, month, 28));
        }

        var applicationId = await SendAsync(new ReceiveLoanApplicationCommand(
            memberId, LoanProduct.Normal, Money.Kes(175_000m), new DateOnly(2026, 3, 6),
            Money.Kes(300_000m), null));

        await using (var scope = _services.CreateAsyncScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<ILoanApplicationRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();

            var application = (await applications.FindByIdAsync(applicationId))!;

            application.AddGuarantee(new Domain.Guaranteeing.Guarantee(
                BorrowerId.New(), "Peter Mwangi", PayrollNumber.Of("CAL/0099"),
                Money.Kes(300_000m), Money.Kes(400_000m), new DateOnly(2026, 3, 5)));

            application.Submit();
            application.RecordDecision(
                new Actor(Guid.Parse("0000B22C-0000-0000-0000-000000000001"), "Mary Otieno"),
                ApprovalDecisionKind.Approve, clock.UtcNow);

            applications.Update(application);
            await unitOfWork.SaveChangesAsync();
        }

        await SendAsync(new ApproveLoanApplicationCommand(applicationId, null));

        return (memberId, await SendAsync(new DisburseLoanCommand(
            applicationId, "AKB-2026-0001", new DateOnly(2026, 3, 10), "000431", "PV-2026-0112",
            ["Mr. Mutinda", "Mr. Kimathi"])));
    }

    private async Task ContributeAsync(BorrowerId memberId, decimal amount, DateOnly on)
    {
        var receiptId = await SendAsync(new RecordPayrollReceiptCommand(
            Money.Kes(amount), on, $"P{on:yyyyMM}-{memberId.Value.ToString()[..4]}"));

        await SendAsync(new ClearReceiptCommand(receiptId, on));
        await SendAsync(new AllocateReceiptCommand(
            receiptId, AllocationTarget.Shares, memberId.Value, Money.Kes(amount), on));
    }

    private async Task RepayAsync(BorrowerId memberId, LoanId loanId, decimal amount, DateOnly on)
    {
        var receiptId = await SendAsync(new RecordPayrollReceiptCommand(
            Money.Kes(amount), on, $"R{on:yyyyMM}-{loanId.Value.ToString()[..4]}"));

        await SendAsync(new ClearReceiptCommand(receiptId, on));
        await SendAsync(new AllocateReceiptCommand(
            receiptId, AllocationTarget.LoanInstalment, loanId.Value, Money.Kes(amount), on));

        _ = memberId;
    }
}
