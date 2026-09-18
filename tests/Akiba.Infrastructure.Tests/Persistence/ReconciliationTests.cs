using System.Text;
using Akiba.Application;
using Akiba.Application.Abstractions;
using Akiba.Application.Ledger;
using Akiba.Application.Members;
using Akiba.Application.Receipting;
using Akiba.Application.Reconciliation;
using Akiba.Application.Reporting;
using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Membership;
using Akiba.Domain.Receipting;
using Akiba.Domain.Reconciliation;
using Akiba.Infrastructure;
using Akiba.Infrastructure.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Akiba.Infrastructure.Tests.Persistence;

/// <summary>
/// Reconciliation against a real database.
/// </summary>
/// <remarks>
/// Statements arrive quarterly, so this is the control that catches everything the office
/// never saw - and the gate a month has to pass before it can be closed.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class ReconciliationTests : IAsyncLifetime
{
    private static readonly Actor Clerk =
        new(Guid.Parse("0000A11B-0000-0000-0000-000000000001"), "Oliver Kamau");

    private readonly PostgresFixture _postgres;
    private ServiceProvider _services = null!;
    private ZoneId _zoneId;

    public ReconciliationTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task A_quarterly_statement_reconciles_and_survives_a_round_trip()
    {
        var memberId = await EnrolAsync("0001", "CAL/0001", "Grace", "Njeri");
        await ContributeAsync(memberId, 6_000m, new DateOnly(2026, 7, 31));
        await ContributeAsync(memberId, 6_000m, new DateOnly(2026, 8, 31));

        var statement = Csv(
            "Value Date,Description,Debit,Credit",
            "31/07/2026,TRF FRM CAL PAYROLL JULY,,6000.00",
            "31/08/2026,\"TRF FRM CAL PAYROLL, AUGUST\",,6000.00");

        var imported = await SendAsync(new ImportBankStatementCommand(
            "statement-q3.csv", statement,
            new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            OpeningBalance: 0m, ClosingBalance: 12_000m));

        imported.Problems.Should().BeEmpty();
        imported.LinesImported.Should().Be(2);
        imported.IsSelfConsistent.Should().BeTrue();
        imported.SuggestedMatches.Should().Be(2);

        // Confirm the suggestions, which is a person's job and not the system's.
        var view = await SendAsync(new GetBankReconciliationQuery(imported.ReconciliationId));

        foreach (var line in view.Reconciliation.Lines)
        {
            await SendAsync(new ConfirmStatementMatchCommand(
                imported.ReconciliationId, line.LineNumber,
                new Domain.Ledger.JournalEntryId(line.MatchedToId!.Value)));
        }

        var reconciled = await SendAsync(new GetBankReconciliationQuery(imported.ReconciliationId));

        reconciled.Result.Balances.Should().BeTrue();
        reconciled.Result.MayClosePeriod.Should().BeTrue();

        await SendAsync(new SignOffReconciliationCommand(imported.ReconciliationId));

        // Read it back from the database, not from the object that was just saved.
        var reloaded = await SendAsync(new GetBankReconciliationQuery(imported.ReconciliationId));

        reloaded.Reconciliation.IsSignedOff.Should().BeTrue();
        reloaded.Reconciliation.SignedOffBy.Should().Be(Clerk);
        reloaded.Reconciliation.Lines.Should().AllSatisfy(
            line => line.State.Should().Be(MatchState.Matched));

        // A narration containing a comma survived the CSV reader intact.
        reloaded.Reconciliation.Line(2).Description
            .Should().Be("TRF FRM CAL PAYROLL, AUGUST");
    }

    [Fact]
    public async Task A_bank_charge_nobody_knew_about_is_found_posted_and_matched()
    {
        // The whole reason quarterly reconciliation exists. The charge is three months old by
        // the time anybody sees it, and it posts into the month the bank took it.
        var memberId = await EnrolAsync("0001", "CAL/0001", "Grace", "Njeri");
        await ContributeAsync(memberId, 6_000m, new DateOnly(2026, 7, 31));

        var statement = Csv(
            "Date,Narration,Amount",
            "31/07/2026,TRF FRM CAL PAYROLL,6000.00",
            "30/09/2026,LEDGER FEES AND COMMISSION,-400.00");

        var imported = await SendAsync(new ImportBankStatementCommand(
            "statement-q3.csv", statement,
            new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            OpeningBalance: 0m, ClosingBalance: 5_600m));

        imported.SuggestedMatches.Should().Be(1, because: "Akiba has never heard of the charge");

        var before = await SendAsync(new GetBankReconciliationQuery(imported.ReconciliationId));

        before.Result.Balances.Should().BeTrue(
            because: "the books and the statement agree once the charge is allowed for");

        before.Result.MayClosePeriod.Should().BeFalse(
            because: "nobody has posted it yet");

        await SendAsync(new ConfirmStatementMatchCommand(
            imported.ReconciliationId, 1,
            new Domain.Ledger.JournalEntryId(before.Reconciliation.Line(1).MatchedToId!.Value)));

        var chargeId = await SendAsync(new RecordBankChargeCommand(
            400m, new DateOnly(2026, 9, 30),
            "Ledger fees and commission, quarter to September",
            "STMT-2026-Q3"));

        await SendAsync(new ConfirmStatementMatchCommand(imported.ReconciliationId, 2, chargeId));

        var after = await SendAsync(new GetBankReconciliationQuery(imported.ReconciliationId));

        after.Result.Balances.Should().BeTrue();
        after.Result.MayClosePeriod.Should().BeTrue();
        after.Result.Difference.Should().Be(Money.ZeroKes);
    }

    [Fact]
    public async Task A_month_cannot_be_closed_until_its_statement_has_been_reconciled()
    {
        var memberId = await EnrolAsync("0001", "CAL/0001", "Grace", "Njeri");
        await ContributeAsync(memberId, 6_000m, new DateOnly(2026, 7, 31));

        var blocked = await SendAsync(new GetPeriodClosePreflightQuery(2026, 7));

        blocked.MayClose.Should().BeFalse();
        blocked.TrialBalanceDifference.Should().Be(Money.ZeroKes);
        blocked.Obstacles.Should().ContainSingle()
            .Which.Problem.Should().Contain("No signed-off bank statement covers");

        var close = () => SendAsync(new ClosePeriodCommand(2026, 7));
        await close.Should().ThrowAsync<PeriodNotReadyToCloseException>();

        // Reconcile it, and the month opens up.
        await ReconcileAndSignOffAsync(
            Csv("Date,Narration,Amount", "31/07/2026,TRF FRM CAL PAYROLL,6000.00"),
            new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), 0m, 6_000m);

        var ready = await SendAsync(new GetPeriodClosePreflightQuery(2026, 7));
        ready.MayClose.Should().BeTrue();

        await SendAsync(new ClosePeriodCommand(2026, 7));

        // And the ledger now refuses to be written to in that month.
        var late = () => ContributeAsync(memberId, 1_000m, new DateOnly(2026, 7, 15));
        await late.Should().ThrowAsync<Domain.Ledger.ClosedPeriodException>();
    }

    [Fact]
    public async Task An_unsigned_statement_overlapping_the_month_blocks_the_close()
    {
        var memberId = await EnrolAsync("0001", "CAL/0001", "Grace", "Njeri");
        await ContributeAsync(memberId, 6_000m, new DateOnly(2026, 7, 31));

        // Imported but never worked through: one line is still unmatched.
        await SendAsync(new ImportBankStatementCommand(
            "statement.csv",
            Csv(
                "Date,Narration,Amount",
                "31/07/2026,TRF FRM CAL PAYROLL,6000.00",
                "15/07/2026,UNKNOWN CREDIT,2500.00"),
            new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31),
            OpeningBalance: 0m, ClosingBalance: 8_500m));

        var preflight = await SendAsync(new GetPeriodClosePreflightQuery(2026, 7));

        preflight.MayClose.Should().BeFalse();
        preflight.Obstacles.Should().Contain(
            obstacle => obstacle.Problem.Contains("has not been signed off"));
    }

    [Fact]
    public async Task A_statement_that_does_not_start_where_the_last_one_ended_says_so()
    {
        await ReconcileAndSignOffAsync(
            Csv("Date,Narration,Amount", "15/01/2026,OPENING TRANSFER,10000.00"),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), 0m, 10_000m,
            markNotOurs: true);

        var next = await SendAsync(new ImportBankStatementCommand(
            "statement-q2.csv",
            Csv("Date,Narration,Amount", "15/04/2026,SOMETHING,500.00"),
            new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30),
            OpeningBalance: 7_500m, ClosingBalance: 8_000m));

        next.ContinuityWarning.Should().Contain("A statement is missing");
    }

    [Fact]
    public async Task The_reader_reports_a_row_it_cannot_understand_rather_than_dropping_it()
    {
        // A statement quietly missing a line reconciles to a wrong figure and looks tidy doing
        // it, which is worse than a file that refuses to import.
        var imported = await SendAsync(new ImportBankStatementCommand(
            "statement.csv",
            Csv(
                "Date,Narration,Amount",
                "31/07/2026,TRF FRM CAL PAYROLL,6000.00",
                "not a date,SOMETHING,500.00",
                "31/08/2026,NO AMOUNT HERE,"),
            new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            OpeningBalance: 0m, ClosingBalance: 6_000m));

        imported.LinesImported.Should().Be(1);
        imported.Problems.Should().HaveCount(2);
        imported.Problems[0].Should().Contain("not a date");
        imported.Problems[1].Should().Contain("no amount could be read");
    }

    [Fact]
    public async Task The_payroll_reconciliation_sets_the_schedule_against_what_HR_actually_deducted()
    {
        var grace = await EnrolAsync("0001", "CAL/0001", "Grace", "Njeri");
        var peter = await EnrolAsync("0002", "CAL/0002", "Peter", "Mwangi");

        await ContributeAsync(grace, 6_000m, new DateOnly(2026, 7, 31));
        await ContributeAsync(peter, 2_000m, new DateOnly(2026, 7, 31));

        var reconciliation = await SendAsync(new GetPayrollReconciliationQuery(
            DeductionScheduleKind.Employees, 2026, 8,
            [
                new PayrollReturnLine("CAL/0001", "NJERI GRACE", 6_000m),
                new PayrollReturnLine("CAL/0002", "MWANGI PETER", 500m),
                new PayrollReturnLine("CAL/9999", "SOMEBODY ELSE", 1_000m),
            ],
            ChequeReceived: 7_500m));

        reconciliation.Agrees.Should().BeFalse();
        reconciliation.TotalExpected.Should().Be(Money.Kes(8_000m));
        reconciliation.TotalDeducted.Should().Be(Money.Kes(7_500m));
        reconciliation.ChequeDifference.Should().Be(Money.ZeroKes);

        reconciliation.NeedingAttention.Should().HaveCount(2);

        reconciliation.NeedingAttention
            .Should().ContainSingle(line => line.Kind == PayrollVarianceKind.NotOnSchedule)
            .Which.PayrollNumber.Should().Be("CAL/9999");

        reconciliation.NeedingAttention
            .Should().ContainSingle(line => line.Kind == PayrollVarianceKind.UnderDeducted)
            .Which.Variance.Should().Be(Money.Kes(-1_500m));
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task ReconcileAndSignOffAsync(
        byte[] statement,
        DateOnly from,
        DateOnly to,
        decimal opening,
        decimal closing,
        bool markNotOurs = false)
    {
        var imported = await SendAsync(new ImportBankStatementCommand(
            "statement.csv", statement, from, to, opening, closing));

        var view = await SendAsync(new GetBankReconciliationQuery(imported.ReconciliationId));

        foreach (var line in view.Reconciliation.Lines)
        {
            if (line.MatchedToId is { } entryId)
            {
                await SendAsync(new ConfirmStatementMatchCommand(
                    imported.ReconciliationId, line.LineNumber,
                    new Domain.Ledger.JournalEntryId(entryId)));
            }
            else if (markNotOurs)
            {
                await SendAsync(new MarkStatementLineNotOursCommand(
                    imported.ReconciliationId, line.LineNumber, "Not Akiba's; queried with the bank."));
            }
        }

        await SendAsync(new SignOffReconciliationCommand(imported.ReconciliationId));
    }

    private static byte[] Csv(params string[] lines) =>
        Encoding.UTF8.GetBytes(string.Join("\n", lines));

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

    private Task<BorrowerId> EnrolAsync(
        string membershipNumber, string payrollNumber, string given, string family) =>
        SendAsync(new EnrolMemberCommand(
            membershipNumber, payrollNumber, given, family, null,
            "28765432", "0712345678", null, _zoneId, false));

    private async Task ContributeAsync(BorrowerId memberId, decimal amount, DateOnly on)
    {
        var receiptId = await SendAsync(new RecordPayrollReceiptCommand(
            Money.Kes(amount), on, $"P{on:yyyyMM}-{memberId.Value.ToString()[..4]}"));

        await SendAsync(new ClearReceiptCommand(receiptId, on));
        await SendAsync(new AllocateReceiptCommand(
            receiptId, AllocationTarget.Shares, memberId.Value, Money.Kes(amount), on));
    }
}
