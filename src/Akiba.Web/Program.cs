using System.Globalization;
using Akiba.Application;
using Akiba.Application.Abstractions;
using Akiba.Application.Members;
using Akiba.Application.Ledger;
using Akiba.Application.Reconciliation;
using Akiba.Application.Auditing;
using Akiba.Application.Dividends;
using Akiba.Domain.Dividends;
using Akiba.Application.Reporting;
using Akiba.Domain.Common;
using Akiba.Domain.Financial;
using Akiba.Domain.Membership;
using Akiba.Infrastructure;
using Akiba.Infrastructure.Notifications;
using Akiba.Infrastructure.Persistence;
using Akiba.Web;
using Akiba.Web.Components;
using Akiba.Infrastructure.Identity;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

// Akiba - composition root.
//
// This is the ONLY project that references Akiba.Infrastructure, and it does so purely to
// wire up dependency injection. No domain logic lives here. See ARCHITECTURE.md section 5.
//
// Blazor Server with MudBlazor, ASP.NET Core Identity with mandatory TOTP, Serilog and
// Hangfire arrive in their own milestones. Nothing is added ahead of its milestone.

// Pin the culture.
//
// Left alone, .NET takes the culture from the machine's Windows locale, and the panel's date
// and number fields then behave differently on different boxes - a date an official typed
// happily on one machine is rejected on another, because one abbreviates September as "Sep"
// and the other as "Sept". Akiba serves one office in Nairobi, so the culture is a property of
// the software rather than of whatever the machine was set up as.
//
// Money is unaffected either way: Money.ToString formats invariantly by design.
var akibaCulture = new CultureInfo("en-KE");
CultureInfo.DefaultThreadCurrentCulture = akibaCulture;
CultureInfo.DefaultThreadCurrentUICulture = akibaCulture;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Akiba")
    ?? throw new InvalidOperationException(
        "No connection string named 'Akiba'. Set the ConnectionStrings__Akiba environment " +
        "variable - see docs/deployment.md.");

// Whether the session cookie may only travel over HTTPS. On by default: over plain HTTP the
// cookie and the password that produced it cross the office network in clear text, and every
// member's financial position is behind that cookie. An administrator who genuinely cannot
// serve HTTPS turns it off knowingly - and startup says so, loudly, every time.
var requireHttps = builder.Configuration.GetValue("Akiba:RequireHttps", true);

// Kestrel announces itself by name and version in every response by default, which tells an
// attacker which advisories to read. It costs nothing to stop.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

builder.Services.AddAkibaApplication();
builder.Services.AddAkibaInfrastructure(connectionString);
builder.Services.AddAkibaRateLimiting();

// Notifications. Sending is allowed in Production and nowhere else - everywhere else every
// message is written to the outbox, shown on the notifications screen, and never sent. A
// development database full of invented members with plausible phone numbers is exactly the
// thing that must never be texted.
builder.Services.AddAkibaNotifications(
    builder.Configuration,
    sendingIsAllowed: builder.Environment.IsProduction(),
    suppressionReason:
        $"Nothing is sent from the {builder.Environment.EnvironmentName} environment. " +
        "This is what would have gone out.");

// Blazor Server. Four officials on a LAN: rendering on the server keeps one language across
// the whole system and means no figure is ever computed twice, once here and once in a
// browser.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();
builder.Services.AddCascadingAuthenticationState();

// Authentication, TOTP and the role policies. Registered unconditionally - there is no
// environment in which Akiba is open, because the same code runs on the machine holding the
// society's records.
builder.Services.AddAkibaIdentity(
    builder.Environment.IsProduction()
        ? null
        : new Actor(Guid.Parse("0000A11B-0000-0000-0000-00000000DE11"), "Development seeder"),
    requireHttps);
builder.Services.AddScoped<
    IUserClaimsPrincipalFactory<AkibaUser>, AkibaClaimsPrincipalFactory>();

// Needed by the audit trail, which records the address a change came from.
builder.Services.AddHttpContextAccessor();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AkibaDbContext>("database");

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Akiba.Startup");

if (!requireHttps)
{
    startupLogger.LogWarning(
        "Akiba:RequireHttps is off. Passwords and session cookies cross the network in clear " +
        "text, and anybody on the same network can read a member's position or take over a " +
        "session. Turn it back on as soon as the machine has a certificate.");
}

// Host header filtering. A request whose Host is not on this list is refused before it reaches
// anything, which stops cache-poisoning and password-reset-style tricks that turn on a forged
// host. It has to name the address officials actually use - see docs/deployment.md.
if (builder.Configuration["AllowedHosts"] is null or "" or "*")
{
    startupLogger.LogWarning(
        "AllowedHosts is not restricted, so Akiba answers to any Host header. Set it to the " +
        "address officials use, e.g. AllowedHosts=\"akiba.local;192.168.1.40\".");
}

if (!app.Environment.IsProduction())
{
    startupLogger.LogWarning(
        "Running in the {Environment} environment. Detailed errors are shown, the development " +
        "seed endpoint is mapped, and entries made with nobody signed in are attributed to a " +
        "placeholder. Never do this on the society's machine.",
        app.Environment.EnvironmentName);
}

// Before anything writes to the database, including the migrations and the startup seed, so
// that the very first changes are recorded too.
app.UseAkibaAuditTrail();

// One database, on one machine, upgraded by one person who is not a DBA. Migrating at
// startup suits that; it would be the wrong call for several instances racing to migrate the
// same database, and Akiba is deliberately not that.
await using (var scope = app.Services.CreateAsyncScope())
{
    await AkibaStartup.PrepareAsync(
        scope.ServiceProvider.GetRequiredService<AkibaDbContext>(),
        scope.ServiceProvider.GetRequiredService<ChartOfAccountsSeeder>(),
        scope.ServiceProvider.GetRequiredService<RoleManager<AkibaRole>>(),
        scope.ServiceProvider.GetRequiredService<UserManager<AkibaUser>>(),
        startupLogger,
        builder.Configuration["Akiba:SetupPassword"]);
}

// First, so that a response refused by authorisation carries them too.
app.UseAkibaSecurityHeaders();

if (requireHttps)
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();

app.UseRouting();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// Anonymous on purpose: the deployment guide tells an administrator to check it, and a probe
// that needs a password is a probe nobody runs. It says Healthy or Unhealthy and nothing else.
app.MapHealthChecks("/health").AllowAnonymous();

app.MapAkibaAuthentication();

// The JSON endpoints read the same figures the panel does, so they need the same permission.
// Left open they would have been a way to read every member's position without signing in.
var api = app.MapGroup("/api").RequireAuthorization(AkibaPolicies.ViewsLedger);

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

api.MapGet("/ledger/accounts", async (IAccountRepository accounts) =>
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

api.MapGet("/ledger/trial-balance", async (IBalanceQueries balances, IClock clock, DateOnly? asAt) =>
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

api.MapGet("/members", async (IMediator mediator, IClock clock, DateOnly? asAt, bool? includeExited) =>
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

api.MapGet("/members/{id:guid}/statement", async (
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

api.MapGet("/reconciliations", async (IMediator mediator) =>
{
    var summaries = await mediator.Send(new ListBankReconciliationsQuery());

    return Results.Ok(summaries.Select(summary => new
    {
        Id = summary.ReconciliationId.Value,
        summary.AccountLabel,
        From = summary.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        To = summary.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        summary.LineCount,
        summary.UnresolvedLineCount,
        Difference = summary.Difference.ToString(),
        summary.IsSignedOff,
    }));
});

// What is stopping a month being closed, which is the question an official actually has. It
// answers with the obstacles rather than a yes or no, because "no" is not actionable.
api.MapGet("/ledger/period-close/{year:int}/{month:int}", async (
    IMediator mediator, int year, int month) =>
{
    var preflight = await mediator.Send(new GetPeriodClosePreflightQuery(year, month));

    return Results.Ok(new
    {
        preflight.Year,
        preflight.Month,
        MonthEnd = preflight.MonthEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        preflight.AlreadyClosed,
        preflight.MayClose,
        TrialBalanceDifference = preflight.TrialBalanceDifference.ToString(),
        Obstacles = preflight.Obstacles.Select(obstacle => new
        {
            obstacle.Problem,
            obstacle.Remedy,
        }),
    });
});

// Development-only. Fills an empty database with plausible activity so the panel has
// something to show. It drives the same commands the panel does, so anything it creates got
// there the way an official would have put it there - which also makes it a smoke test of the
// whole stack. Never mapped in Production.
if (!app.Environment.IsProduction())
{
    app.MapPost("/api/dev/seed-demo", async (IServiceProvider services, AkibaDbContext database) =>
    {
        // The same guard tools/seed-demo.ps1 has. This endpoint invents members, and the one
        // mistake worth defending against is somebody running a development build against the
        // society's own database - a mistake that is one stale environment variable away.
        var name = database.Database.GetDbConnection().Database;

        if (string.Equals(name, "akiba", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(
                "Refusing to seed a database called 'akiba' - that is the production name. " +
                "This endpoint creates people who do not exist.");
        }

        return Results.Ok(await DemoData.SeedAsync(services));
    }).AllowAnonymous();
}

// Reports. Each returns a file, and each is reproducible as at a past date because every
// figure in it is summed from the ledger rather than read from a stored total.
// Every report is generated rather than fetched - an AGM pack is a whole PDF and the
// shareholding summary sums every member's ledger - so this group is rate limited as well as
// authorised.
var reports = app.MapGroup("/reports").RequireRateLimiting(RateLimiting.Reports);

reports.MapGet("/deductions/{kind}/{year:int}/{month:int}", async (
    IMediator mediator,
    IDeductionScheduleWriter writer,
    string kind,
    int year,
    int month) =>
{
    var scheduleKind = string.Equals(kind, "landlords", StringComparison.OrdinalIgnoreCase)
        ? DeductionScheduleKind.Landlords
        : DeductionScheduleKind.Employees;

    var schedule = await mediator.Send(new GetDeductionScheduleQuery(scheduleKind, year, month));
    var file = writer.Write(schedule);

    return Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization(AkibaPolicies.DownloadsSchedules);

reports.MapGet("/shareholding", async (
    IMediator mediator, IShareholdingSummaryWriter writer, IClock clock, DateOnly? asAt) =>
{
    var summary = await mediator.Send(
        new GetShareholdingSummaryQuery(asAt ?? clock.TodayInNairobi));

    var file = writer.Write(summary);

    return Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization(AkibaPolicies.ViewsLedger);

reports.MapGet("/members/{id:guid}/statement", async (
    IMediator mediator, IMemberStatementWriter writer, IClock clock, Guid id, DateOnly? asAt) =>
{
    var statement = await mediator.Send(
        new GetMemberStatementQuery(new BorrowerId(id), asAt ?? clock.TodayInNairobi));

    var file = writer.Write(statement);

    return Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization(AkibaPolicies.ViewsLedger);

// The audit trail as CSV. The brief requires it exportable, and CSV is what an auditor asks
// for - it opens in anything and nothing about it depends on Akiba still running.
reports.MapGet("/audit", async (
    IMediator mediator, DateOnly? from, DateOnly? to, string? table, int? take) =>
{
    var entries = await mediator.Send(
        new ListAuditEntriesQuery(from, to, table, null, take ?? 5_000));

    var csv = Akiba.Infrastructure.Auditing.AuditTrailQueries.ToCsv(entries);

    return Results.File(
        csv,
        "text/csv",
        $"akiba-audit-trail-{DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.csv");
}).RequireAuthorization(AkibaPolicies.ViewsLedger);

reports.MapGet("/dividends/{id:guid}", async (
    IMediator mediator, IDividendScheduleWriter writer, Guid id) =>
{
    var run = await mediator.Send(new GetDividendRunQuery(new DividendRunId(id)));
    var file = writer.Write(run);

    return Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization(AkibaPolicies.ViewsLedger);

reports.MapGet("/agm/{year:int}", async (
    IMediator mediator,
    IAgmPackWriter writer,
    int year,
    string? treasurersReport,
    string? chairmansReport) =>
{
    // The two narrative reports are written by officials and printed as supplied. Akiba does
    // not generate prose that somebody then has to stand behind.
    var pack = await mediator.Send(new GetAgmPackQuery(
        year, treasurersReport ?? string.Empty, chairmansReport ?? string.Empty));

    var file = writer.Write(pack);

    return Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization(AkibaPolicies.ViewsLedger);

await app.RunAsync();

/// <summary>
/// Exposed so integration tests can host the application with WebApplicationFactory.
/// Required because top-level statements generate an internal Program class.
/// </summary>
public partial class Program;
