using System.Globalization;
using Akiba.Application;
using Akiba.Application.Abstractions;
using Akiba.Application.Members;
using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Membership;
using Akiba.Infrastructure;
using Akiba.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

// Akiba - composition root.
//
// This is the ONLY project that references Akiba.Infrastructure, and it does so purely to
// wire up dependency injection. No domain logic lives here. See ARCHITECTURE.md section 5.
//
// Blazor Server with MudBlazor, ASP.NET Core Identity with mandatory TOTP, Serilog and
// Hangfire arrive in their own milestones. Nothing is added ahead of its milestone.

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Akiba")
    ?? throw new InvalidOperationException(
        "No connection string named 'Akiba'. Set the ConnectionStrings__Akiba environment " +
        "variable - see docs/deployment.md.");

builder.Services.AddAkibaApplication();
builder.Services.AddAkibaInfrastructure(connectionString);

// Identity arrives in milestone 15. Until then the panel runs as a fixed clerk, and only
// outside Production - every ledger entry records its author, and attributing them all to the
// same person regardless of who acted would be the opposite of an audit trail.
if (!builder.Environment.IsProduction())
{
    builder.Services.AddAkibaTestUser(
        new Actor(Guid.Parse("0000A11B-0000-0000-0000-000000000001"), "Oliver Kamau (development)"));
}

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AkibaDbContext>("database");

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Akiba.Startup");

if (!app.Environment.IsProduction())
{
    startupLogger.LogWarning(
        "Running as a fixed development user. Every entry will be attributed to the same " +
        "person. This is disabled in Production.");
}

// One database, on one machine, upgraded by one person who is not a DBA. Migrating at
// startup suits that; it would be the wrong call for several instances racing to migrate the
// same database, and Akiba is deliberately not that.
await using (var scope = app.Services.CreateAsyncScope())
{
    await DatabaseStartup.MigrateAndSeedAsync(
        scope.ServiceProvider.GetRequiredService<AkibaDbContext>(),
        scope.ServiceProvider.GetRequiredService<ChartOfAccountsSeeder>(),
        startupLogger);
}

app.MapHealthChecks("/health");

app.MapGet("/", () => Results.Text(
    """
    Akiba Sacco Management System

    /health                          liveness, including the database
    /ledger/accounts                 the chart of accounts
    /ledger/trial-balance?asAt=      the trial balance, which must be zero
    /members?asAt=yyyy-MM-dd         members with their shareholding as at a date
    /members/{id}/statement?asAt=    a member's statement as at any date

    The Blazor panel is still to come; these are a read-only window on the same queries it
    will use.
    """,
    "text/plain"));

app.MapGet("/ledger/accounts", async (IAccountRepository accounts) =>
{
    var chart = await accounts.AllAsync();

    return Results.Ok(chart.Select(account => new
    {
        Code = account.Code.Value,
        account.Name,
        Type = account.Type.ToString(),
        NormalBalance = account.NormalBalance.ToString(),
        account.IsOpen,
    }));
});

app.MapGet("/ledger/trial-balance", async (IBalanceQueries balances, IClock clock, DateOnly? asAt) =>
{
    var date = asAt ?? clock.TodayInNairobi;
    var difference = await balances.TrialBalanceDifferenceAsAtAsync(date);

    // This cannot be anything but zero unless something wrote to the database without going
    // through JournalEntry's constructor - which is exactly the failure worth surfacing.
    return Results.Ok(new
    {
        AsAt = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Difference = difference.ToString(),
        Balances = difference == Money.ZeroKes,
    });
});

app.MapGet("/members", async (IMediator mediator, IClock clock, DateOnly? asAt, bool? includeExited) =>
{
    var members = await mediator.Send(
        new ListMembersQuery(asAt ?? clock.TodayInNairobi, includeExited ?? false));

    return Results.Ok(members.Select(member => new
    {
        Id = member.MemberId.Value,
        member.MembershipNumber,
        member.PayrollNumber,
        member.Name,
        Shareholding = member.Shareholding.ToString(),
        BorrowingLimit = member.BorrowingLimit.ToString(),
        member.RunningLoans,
        member.IsActive,
    }));
});

app.MapGet("/members/{id:guid}/statement", async (
    IMediator mediator, IClock clock, Guid id, DateOnly? asAt) =>
{
    var statement = await mediator.Send(
        new GetMemberStatementQuery(new BorrowerId(id), asAt ?? clock.TodayInNairobi));

    return Results.Ok(new
    {
        statement.MemberName,
        statement.MembershipNumber,
        AsAt = statement.AsAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        MembershipSince = statement.MembershipSince?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Shareholding = statement.Shareholding.ToString(),
        BorrowingLimit = statement.BorrowingLimit.ToString(),
        ShareMovements = statement.ShareMovements.Select(line => new
        {
            Date = line.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            line.Narration,
            line.SourceDocument,
            Movement = line.Movement.ToString(),
            RunningBalance = line.RunningBalance.ToString(),
        }),
        Loans = statement.Loans.Select(loan => new
        {
            loan.LoanNumber,
            Product = loan.Product.ToString(),
            TotalRepayable = loan.TotalRepayable.ToString(),
            Outstanding = loan.OutstandingBalance.ToString(),
            MonthlyInstalment = loan.MonthlyInstalment.ToString(),
            FirstDueDate = loan.FirstDueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }),
    });
});

await app.RunAsync();

/// <summary>
/// Exposed so integration tests can host the application with WebApplicationFactory.
/// Required because top-level statements generate an internal Program class.
/// </summary>
public partial class Program;
