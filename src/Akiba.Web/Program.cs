using System.Globalization;
using Akiba.Application.Abstractions;
using Akiba.Domain.Financial;
using Akiba.Infrastructure;
using Akiba.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// Akiba - composition root.
//
// This is the ONLY project that references Akiba.Infrastructure, and it does so purely to
// wire up dependency injection. No domain logic lives here. See ARCHITECTURE.md section 5.
//
// Blazor Server with MudBlazor, Identity with mandatory TOTP, Serilog and Hangfire arrive in
// their own milestones (BUILD_BRIEF.md section 13). Nothing is added ahead of its milestone.

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Akiba")
    ?? throw new InvalidOperationException(
        "No connection string named 'Akiba'. Set the ConnectionStrings__Akiba environment " +
        "variable - see docs/deployment.md.");

builder.Services.AddAkibaInfrastructure(connectionString);

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AkibaDbContext>("database");

var app = builder.Build();

// One database, on one machine, upgraded by one person who is not a DBA. Migrating at
// startup suits that; it would be the wrong call for several instances racing to migrate the
// same database, and Akiba is deliberately not that.
await using (var scope = app.Services.CreateAsyncScope())
{
    await DatabaseStartup.MigrateAndSeedAsync(
        scope.ServiceProvider.GetRequiredService<AkibaDbContext>(),
        scope.ServiceProvider.GetRequiredService<ChartOfAccountsSeeder>(),
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Akiba.Startup"));
}

app.MapHealthChecks("/health");

app.MapGet("/", () => Results.Text(
    """
    Akiba Sacco Management System
    Milestones 1-8 and ledger persistence.

    /health          liveness, including the database
    /ledger/accounts the seeded chart of accounts
    /ledger/trial-balance  the trial balance as at today, which must be zero
    """,
    "text/plain"));

// A read-only window on the ledger while the Blazor panel is still to come. It is the
// quickest honest way to see that what the domain computes is what the database holds.
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

app.MapGet("/ledger/trial-balance", async (IBalanceQueries balances, IClock clock) =>
{
    var asAt = clock.TodayInNairobi;
    var difference = await balances.TrialBalanceDifferenceAsAtAsync(asAt);

    // This cannot be anything but zero unless something wrote to the database without going
    // through JournalEntry's constructor - which is exactly the failure worth surfacing.
    return Results.Ok(new
    {
        AsAt = asAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Difference = difference.ToString(),
        Balances = difference == Money.ZeroKes,
    });
});

await app.RunAsync();

/// <summary>
/// Exposed so integration tests can host the application with WebApplicationFactory.
/// Required because top-level statements generate an internal Program class.
/// </summary>
public partial class Program;
