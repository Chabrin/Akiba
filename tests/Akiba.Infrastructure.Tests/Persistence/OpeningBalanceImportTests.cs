using Akiba.Application;
using Akiba.Application.Abstractions;
using Akiba.Application.Migration;
using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Membership;
using Akiba.Infrastructure;
using Akiba.Infrastructure.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Akiba.Infrastructure.Tests.Persistence;

/// <summary>
/// Migrating the society's opening balances, using the shape of its real deduction register.
/// </summary>
/// <remarks>
/// <para>
/// The rows here reproduce the register's structure and its awkward cases - the two
/// shareholders on staff number <c>0</c>, the wide spread of holdings, the single-word and
/// three-word names. <b>The figures are altered and the names are not the society's.</b> Real
/// member data does not belong in a test file, or in this repository at all.
/// </para>
/// <para>
/// What is faithful is the shape, which is what the importer has to survive.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class OpeningBalanceImportTests : IAsyncLifetime
{
    private static readonly Actor Clerk = new(Guid.Parse("0000A11B-0000-0000-0000-000000000001"), "Oliver Kamau");
    private static readonly DateOnly GoLive = new(2026, 11, 30);

    private readonly PostgresFixture _postgres;
    private ServiceProvider _services = null!;

    public OpeningBalanceImportTests(PostgresFixture postgres) => _postgres = postgres;

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
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    /// <summary>
    /// The register's shape, with invented names and altered figures.
    /// </summary>
    private static IReadOnlyList<RegisterRow> Register() =>
    [
        // Two shareholders who are not on the CAL payroll, both written as staff number 0.
        // This pair is why the import exists in this form: a required, unique payroll number
        // refused the second one.
        new(1, "0", "MERCY WAIRIMU GITHINJI", Money.Kes(1_979_000m)),
        new(2, "0", "BENARD OTIENO ODUOR", Money.Kes(2_355_000m)),

        new(3, "2", "CAROLINE NDUTA KAMAU", Money.Kes(1_266_000m)),
        new(4, "4", "TABITHA AKOTH ONYANGO", Money.Kes(170_224m)),
        new(5, "19", "PATRICK GITHAIGA", Money.Kes(451_000m)),
        new(6, "22", "SALOME WANGECHI NDERITU", Money.Kes(388_540m)),
        new(7, "37", "KIPROTICH", Money.Kes(512_003m)),
        new(8, "333", "AGNES NYAMBURA WAWERU", Money.Kes(2_862m)),
    ];

    [Fact]
    public async Task The_dry_run_accepts_the_registers_shape_including_shareholders_with_no_payroll_number()
    {
        var dryRun = await SendAsync(new DryRunOpeningBalancesCommand(
            Register(), GoLive, Money.Kes(7_200_000m)));

        dryRun.CanCommit.Should().BeTrue();
        dryRun.FatalProblems.Should().BeEmpty();
        dryRun.Rows.Should().HaveCount(8);
        dryRun.TotalShareholding.Should().Be(Money.Kes(7_124_629m));

        // Not fatal, but the clerk is told: these two cannot be deducted at source.
        dryRun.Warnings.Should().Contain(problem =>
            problem.Problem.Contains("Not on the CAL payroll", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_payroll_number_that_really_is_shared_stops_the_import()
    {
        // "0" means "no payroll number" and may repeat. A genuine number may not - HR matches
        // the deduction schedule on it.
        var rows = Register().ToList();
        rows.Add(new RegisterRow(9, "2", "ANOTHER PERSON ENTIRELY", Money.Kes(10_000m)));

        var dryRun = await SendAsync(new DryRunOpeningBalancesCommand(
            rows, GoLive, Money.Kes(7_200_000m)));

        dryRun.CanCommit.Should().BeFalse();
        dryRun.FatalProblems.Should().Contain(problem =>
            problem.Problem.Contains("Payroll number 2 is used by 2 shareholders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_an_opening_bank_balance_the_import_refuses()
    {
        // The register says what members hold. It says nothing about what is in the account,
        // and the opening entry needs both.
        var dryRun = await SendAsync(new DryRunOpeningBalancesCommand(
            Register(), GoLive, Money.ZeroKes));

        dryRun.CanCommit.Should().BeFalse();
        dryRun.FatalProblems.Should().Contain(problem =>
            problem.Problem.Contains("No opening bank balance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_dry_run_reports_what_the_bank_is_short_of_what_members_hold()
    {
        // Not an error - the difference is loans outstanding, or a figure nobody has reconciled.
        // Either way the treasurer sees it before signing.
        var dryRun = await SendAsync(new DryRunOpeningBalancesCommand(
            Register(), GoLive, Money.Kes(7_000_000m)));

        dryRun.BankLessShareholding.Should().Be(Money.Kes(-124_629m));
    }

    [Fact]
    public async Task Committing_creates_every_member_and_one_balanced_opening_entry()
    {
        var result = await SendAsync(new CommitOpeningBalancesCommand(
            Register(), GoLive, Money.Kes(7_200_000m), await CreateZoneAsync(),
            "Wilfred Wamai, 30 November 2026"));

        result.MembersCreated.Should().Be(8);
        result.TotalShareholding.Should().Be(Money.Kes(7_124_629m));

        await using var scope = _services.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<IJournalRepository>();
        var balances = scope.ServiceProvider.GetRequiredService<IBalanceQueries>();

        // One entry, not one per member. The whole migration either happened or it did not.
        var entries = await journal.AsOfAsync(GoLive);
        entries.Should().ContainSingle();

        var entry = entries[0];
        entry.Narration.Should().Contain("Wilfred Wamai");
        entry.Lines.Should().HaveCount(10, because: "bank, eight shareholders, and the equity balance");

        // And the books balance, because JournalEntry would not have been constructible otherwise.
        (await balances.TrialBalanceDifferenceAsAtAsync(GoLive)).Should().Be(Money.ZeroKes);
    }

    [Fact]
    public async Task An_imported_members_shareholding_is_derived_from_the_ledger_like_any_other()
    {
        await SendAsync(new CommitOpeningBalancesCommand(
            Register(), GoLive, Money.Kes(7_200_000m), await CreateZoneAsync(), "Wilfred Wamai"));

        await using var scope = _services.CreateAsyncScope();
        var borrowers = scope.ServiceProvider.GetRequiredService<IBorrowerRepository>();
        var balances = scope.ServiceProvider.GetRequiredService<IBalanceQueries>();

        var members = await borrowers.AllMembersAsync();
        var imported = members.First(member => member.Name.Full == "Caroline Nduta Kamau");

        (await balances.NaturalBalanceAsAtAsync(imported.SharesAccountId, GoLive))
            .Should().Be(Money.Kes(1_266_000m));

        // Nothing before go-live. Akiba's history starts there; what came before lives in the
        // books it replaces.
        (await balances.NaturalBalanceAsAtAsync(imported.SharesAccountId, GoLive.AddDays(-1)))
            .Should().Be(Money.ZeroKes);
    }

    [Fact]
    public async Task A_name_written_as_one_string_is_split_the_way_the_register_writes_it()
    {
        await SendAsync(new CommitOpeningBalancesCommand(
            Register(), GoLive, Money.Kes(7_200_000m), await CreateZoneAsync(), "Wilfred Wamai"));

        await using var scope = _services.CreateAsyncScope();
        var members = await scope.ServiceProvider
            .GetRequiredService<IBorrowerRepository>().AllMembersAsync();

        members.Select(member => member.Name.Full).Should()
            .Contain("Mercy Wairimu Githinji")
            .And.Contain("Kiprotich Kiprotich", because: "a single-word name has nothing else to use");
    }

    [Fact]
    public async Task Members_imported_without_a_payroll_number_are_flagged_as_not_deducted_at_source()
    {
        await SendAsync(new CommitOpeningBalancesCommand(
            Register(), GoLive, Money.Kes(7_200_000m), await CreateZoneAsync(), "Wilfred Wamai"));

        await using var scope = _services.CreateAsyncScope();
        var members = await scope.ServiceProvider
            .GetRequiredService<IBorrowerRepository>().AllMembersAsync();

        members.Count(member => !member.IsOnPayroll).Should().Be(2);
        members.Count(member => member.IsOnPayroll).Should().Be(6);
    }

    [Fact]
    public async Task Committing_without_a_treasurers_sign_off_is_refused()
    {
        var commit = async () => await SendAsync(new CommitOpeningBalancesCommand(
            Register(), GoLive, Money.Kes(7_200_000m), await CreateZoneAsync(), "   "));

        await commit.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*treasurer has signed*");
    }

    [Fact]
    public async Task Importing_somebody_already_in_Akiba_is_refused()
    {
        var zoneId = await CreateZoneAsync();

        await SendAsync(new CommitOpeningBalancesCommand(
            Register(), GoLive, Money.Kes(7_200_000m), zoneId, "Wilfred Wamai"));

        var again = await SendAsync(new DryRunOpeningBalancesCommand(
            Register(), GoLive, Money.Kes(7_200_000m)));

        again.CanCommit.Should().BeFalse();
        again.FatalProblems.Should().Contain(problem =>
            problem.Problem.Contains("already in Akiba", StringComparison.Ordinal));
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
}
