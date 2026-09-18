using Akiba.Application;
using Akiba.Application.Abstractions;
using Akiba.Application.Lending;
using Akiba.Application.Members;
using Akiba.Application.Receipting;
using Akiba.Application.Reporting;
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
/// The reporting suite against a real database.
/// </summary>
/// <remarks>
/// The test that matters most here is
/// <see cref="A_June_report_produced_in_December_says_what_it_said_in_June"/>. The brief calls
/// it the proof the append-only design works, and it is the one an official would actually
/// rely on: a member queries a figure months later, and the statement reproduces.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class ReportingTests : IAsyncLifetime
{
    private static readonly Actor Clerk = new(Guid.Parse("0000A11B-0000-0000-0000-000000000001"), "Oliver Kamau");

    private readonly PostgresFixture _postgres;
    private ServiceProvider _services = null!;
    private ZoneId _zoneId;

    public ReportingTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task A_June_report_produced_in_December_says_what_it_said_in_June()
    {
        // The proof the whole design exists for. Six months of contributions, a statement taken
        // in June, six more months of activity, then the same statement asked for again.
        var memberId = await EnrolAsync("0001", "CAL/0001", "Grace", "Njeri");
        var june = new DateOnly(2026, 6, 30);

        await ContributeAsync(memberId, 6_000m, months: 1, to: 6);

        var takenInJune = await SendAsync(new GetMemberStatementQuery(memberId, june));
        var shareholdingSummaryInJune = await SendAsync(new GetShareholdingSummaryQuery(june));

        // Half a year passes, with larger contributions so December is unmistakably different.
        await ContributeAsync(memberId, 9_000m, months: 7, to: 12);

        var takenInDecember = await SendAsync(new GetMemberStatementQuery(memberId, june));
        var shareholdingSummaryInDecember = await SendAsync(new GetShareholdingSummaryQuery(june));

        takenInDecember.Shareholding.Should().Be(takenInJune.Shareholding);
        takenInDecember.BorrowingLimit.Should().Be(takenInJune.BorrowingLimit);
        takenInDecember.MembershipSince.Should().Be(takenInJune.MembershipSince);
        takenInDecember.ShareMovements.Should().HaveCount(takenInJune.ShareMovements.Count);

        shareholdingSummaryInDecember.TotalShareholding
            .Should().Be(shareholdingSummaryInJune.TotalShareholding);

        // And December really has moved on, so the test is not passing by accident.
        var takenForDecember = await SendAsync(
            new GetMemberStatementQuery(memberId, new DateOnly(2026, 12, 31)));

        takenForDecember.Shareholding.Should().Be(Money.Kes(90_000m));
        takenInJune.Shareholding.Should().Be(Money.Kes(36_000m));
    }

    [Fact]
    public async Task The_same_statement_produces_byte_for_byte_identical_figures_in_its_PDF()
    {
        // Generating the document twice must not change what it says. The bytes differ - a PDF
        // carries a creation timestamp - so this compares the figures the writer was handed.
        var memberId = await EnrolAsync("0001", "CAL/0001", "Grace", "Njeri");
        await ContributeAsync(memberId, 6_000m, months: 1, to: 4);

        var asAt = new DateOnly(2026, 4, 30);

        await using var scope = _services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IMemberStatementWriter>();

        var first = await SendAsync(new GetMemberStatementQuery(memberId, asAt));
        var second = await SendAsync(new GetMemberStatementQuery(memberId, asAt));

        second.Should().BeEquivalentTo(first);

        var pdf = writer.Write(first);

        pdf.FileName.Should().Be("akiba-statement-0001-2026-04-30.pdf");
        pdf.ContentType.Should().Be("application/pdf");
        pdf.Content.Should().NotBeEmpty();

        // A real PDF, not an empty file with the right name.
        System.Text.Encoding.ASCII.GetString(pdf.Content, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public async Task The_deduction_schedule_carries_the_columns_the_society_already_uses()
    {
        var memberId = await EnrolAsync("0001", "CAL/0001", "Grace", "Njeri");
        await ContributeAsync(memberId, 6_000m, months: 1, to: 8);

        var schedule = await SendAsync(
            new GetDeductionScheduleQuery(DeductionScheduleKind.Employees, 2026, 9));

        var line = schedule.Lines.Single();

        // Opening is what they held at the end of August; the contribution is what they have
        // been paying; closing is the two added - the register's own three columns.
        line.OpeningShareholding.Should().Be(Money.Kes(48_000m));
        line.ShareContribution.Should().Be(Money.Kes(6_000m));
        line.ClosingShareholding.Should().Be(Money.Kes(54_000m));
        line.TotalDeduction.Should().Be(Money.Kes(6_000m));

        schedule.MustReachHrBy.Should().Be(
            new DateOnly(2026, 9, 25), because: "the list must be with HR by the 25th");
    }

    [Fact]
    public async Task The_deduction_schedule_takes_each_members_own_chosen_contribution()
    {
        // Contributions are member-chosen. The schedule must not assume a society-wide figure.
        var grace = await EnrolAsync("0001", "CAL/0001", "Grace", "Njeri");
        var peter = await EnrolAsync("0002", "CAL/0002", "Peter", "Mwangi");

        await ContributeAsync(grace, 1_000m, months: 1, to: 3);
        await ContributeAsync(peter, 25_000m, months: 1, to: 3);

        var schedule = await SendAsync(
            new GetDeductionScheduleQuery(DeductionScheduleKind.Employees, 2026, 4));

        schedule.Lines.Single(line => line.FullName == "Grace Njeri")
            .ShareContribution.Should().Be(Money.Kes(1_000m));

        schedule.Lines.Single(line => line.FullName == "Peter Mwangi")
            .ShareContribution.Should().Be(Money.Kes(25_000m));

        schedule.TotalShareContributions.Should().Be(Money.Kes(26_000m));
    }

    [Fact]
    public async Task A_member_not_on_the_payroll_appears_on_the_schedule_and_is_flagged()
    {
        // HR cannot deduct from somebody with no payslip. Leaving them off would hide them;
        // they are listed so the office can collect another way.
        var onPayroll = await EnrolAsync("0001", "CAL/0001", "Grace", "Njeri");
        var notOnPayroll = await EnrolAsync("0002", null, "Francis", "Chabari");

        await ContributeAsync(onPayroll, 6_000m, months: 1, to: 2);
        await ContributeAsync(notOnPayroll, 10_000m, months: 1, to: 2);

        var schedule = await SendAsync(
            new GetDeductionScheduleQuery(DeductionScheduleKind.Employees, 2026, 3));

        schedule.Lines.Should().HaveCount(2);
        schedule.NotOnPayroll.Should().ContainSingle()
            .Which.FullName.Should().Be("Francis Chabari");

        // And they sort to the bottom, after everybody HR can actually deduct from.
        schedule.Lines[^1].FullName.Should().Be("Francis Chabari");
    }

    [Fact]
    public async Task Employees_and_landlords_get_separate_schedules()
    {
        // Different accounts, different cheques, different statements to reconcile against.
        var employee = await EnrolAsync("0001", "CAL/0001", "Grace", "Njeri");
        var landlord = await EnrolAsync("0002", "CAL/0002", "Alice", "Wanjiru", isLandlord: true);

        await ContributeAsync(employee, 6_000m, months: 1, to: 2);
        await ContributeAsync(landlord, 8_000m, months: 1, to: 2);

        var employees = await SendAsync(
            new GetDeductionScheduleQuery(DeductionScheduleKind.Employees, 2026, 3));

        var landlords = await SendAsync(
            new GetDeductionScheduleQuery(DeductionScheduleKind.Landlords, 2026, 3));

        employees.Lines.Should().ContainSingle().Which.FullName.Should().Be("Grace Njeri");
        landlords.Lines.Should().ContainSingle().Which.FullName.Should().Be("Alice Wanjiru");
    }

    [Fact]
    public async Task The_deduction_schedule_writes_a_real_workbook()
    {
        var memberId = await EnrolAsync("0001", "CAL/0001", "Grace", "Njeri");
        await ContributeAsync(memberId, 6_000m, months: 1, to: 3);

        var schedule = await SendAsync(
            new GetDeductionScheduleQuery(DeductionScheduleKind.Employees, 2026, 4));

        await using var scope = _services.CreateAsyncScope();
        var file = scope.ServiceProvider.GetRequiredService<IDeductionScheduleWriter>().Write(schedule);

        file.FileName.Should().Be("akiba-deductions-employees-2026-04.xlsx");
        file.Content.Should().NotBeEmpty();

        // Open it back and read the figures, so this tests the sheet rather than the bytes.
        using var stream = new MemoryStream(file.Content);
        using var workbook = new ClosedXML.Excel.XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();

        sheet.Cell(1, 1).GetString().Should().Be("AKIBA WELFARE SOCIETY");
        sheet.Cell(5, 1).GetString().Should().Be("STAFF NO.");
        sheet.Cell(5, 3).GetString().Should().Be("SHARE HOLDER");
        sheet.Cell(6, 3).GetString().Should().Be("Grace Njeri");

        // Money is a number, not text - HR has to be able to sum the column.
        sheet.Cell(6, 5).DataType.Should().Be(ClosedXML.Excel.XLDataType.Number);
        sheet.Cell(6, 5).GetDouble().Should().Be(6_000d);
    }

    [Fact]
    public async Task Income_and_expenditure_reports_a_period_and_nets_off_bank_charges()
    {
        var memberId = await EnrolAsync("0001", "CAL/0001", "Grace", "Njeri");
        await ContributeAsync(memberId, 200_000m, months: 1, to: 2);
        await LendAsync(memberId, 60_000m, new DateOnly(2026, 3, 10), "AKB-2026-0001");

        var year = await SendAsync(new GetIncomeAndExpenditureQuery(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));

        // A 60,000 loan at a flat 10% recognises 6,000 of interest at disbursement.
        year.TotalIncome.Should().Be(Money.Kes(6_000m));
        year.Surplus.Should().Be(Money.Kes(6_000m));

        // And a period that ends before the loan shows nothing, because it is a period rather
        // than a position.
        var beforeTheLoan = await SendAsync(new GetIncomeAndExpenditureQuery(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 28)));

        beforeTheLoan.TotalIncome.Should().Be(Money.ZeroKes);
    }

    [Fact]
    public async Task The_AGM_pack_carries_the_officials_own_words_and_the_figures_around_them()
    {
        var memberId = await EnrolAsync("0001", "CAL/0001", "Grace", "Njeri");
        await ContributeAsync(memberId, 200_000m, months: 1, to: 2);
        await LendAsync(memberId, 60_000m, new DateOnly(2026, 3, 10), "AKB-2026-0001");

        var pack = await SendAsync(new GetAgmPackQuery(
            2026,
            "The society's funds grew steadily through the year.",
            "Thank you to the committee for their service."));

        pack.TreasurersReport.Should().Contain("grew steadily");
        pack.ChairmansReport.Should().Contain("Thank you");
        pack.IncomeAndExpenditure.TotalIncome.Should().Be(Money.Kes(6_000m));
        pack.MembersAtYearEnd.Should().Be(1);
        pack.LoansRunningAtYearEnd.Should().Be(1);
        pack.TrialBalance.Balances.Should().BeTrue();

        await using var scope = _services.CreateAsyncScope();
        var file = scope.ServiceProvider.GetRequiredService<IAgmPackWriter>().Write(pack);

        file.FileName.Should().Be("akiba-agm-pack-2026.pdf");
        System.Text.Encoding.ASCII.GetString(file.Content, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public async Task The_shareholding_summary_shows_who_owes_more_than_they_hold()
    {
        var memberId = await EnrolAsync("0001", "CAL/0001", "Grace", "Njeri");
        await ContributeAsync(memberId, 40_000m, months: 1, to: 2);
        await LendAsync(memberId, 60_000m, new DateOnly(2026, 3, 10), "AKB-2026-0001");

        var summary = await SendAsync(
            new GetShareholdingSummaryQuery(new DateOnly(2026, 3, 31)));

        var line = summary.Lines.Single();

        line.Shareholding.Should().Be(Money.Kes(80_000m));
        line.LoansOutstanding.Should().Be(Money.Kes(66_000m));
        line.NetPosition.Should().Be(Money.Kes(14_000m));
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

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

    private Task<BorrowerId> EnrolAsync(
        string membershipNumber, string? payrollNumber, string given, string family,
        bool isLandlord = false) =>
        SendAsync(new EnrolMemberCommand(
            membershipNumber,
            payrollNumber ?? string.Empty,
            given,
            family,
            null,
            "28765432",
            "0712345678",
            null,
            _zoneId,
            isLandlord));

    private async Task ContributeAsync(BorrowerId memberId, decimal amount, int months, int to)
    {
        for (var month = months; month <= to; month++)
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
}
