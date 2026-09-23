using Akiba.Application.Abstractions;
using Akiba.Application.Auditing;
using Akiba.Application.Dividends;
using Akiba.Application.Notifications;
using Akiba.Application.Reporting;
using Akiba.Domain.Common;
using Akiba.Infrastructure.Auditing;
using Akiba.Infrastructure.Persistence;
using Akiba.Infrastructure.Persistence.Repositories;
using Akiba.Infrastructure.Reconciliation;
using Akiba.Infrastructure.Reporting;
using Akiba.Infrastructure.Identity;
using Akiba.Infrastructure.Notifications;
using Akiba.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Akiba.Infrastructure;

/// <summary>
/// Wires up the infrastructure. Called from Akiba.Web, which is the composition root and the
/// only project allowed to reference this one.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the database, the repositories and the clock.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    public static IServiceCollection AddAkibaInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<AkibaDbContext>(options => options
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__migrations", AkibaDbContext.Schema))
            // Every write to every table is recorded: actor, time, before and after values, IP.
            // The host decides who the actor is - see AuditTrail.Configure.
            .AddInterceptors(AuditTrail.Interceptor()));

        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<AkibaDbContext>());

        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IAccountingPeriodRepository, AccountingPeriodRepository>();
        services.AddScoped<IJournalRepository, JournalRepository>();
        services.AddScoped<IBalanceQueries, BalanceQueries>();

        services.AddScoped<IBorrowerRepository, BorrowerRepository>();
        services.AddScoped<IZoneRepository, ZoneRepository>();
        services.AddScoped<ILoanApplicationRepository, LoanApplicationRepository>();
        services.AddScoped<ILoanRepository, LoanRepository>();
        services.AddScoped<IReceiptRepository, ReceiptRepository>();
        services.AddScoped<IBankReconciliationRepository, BankReconciliationRepository>();
        services.AddScoped<IDividendRunRepository, DividendRunRepository>();
        services.AddScoped<IAuditTrailQueries, AuditTrailQueries>();
        services.AddScoped<INotificationOutbox, NotificationOutbox>();

        // A policy that sends nothing, registered here so that anything queuing a message works
        // without the host having opted into sending. TryAdd, so AddAkibaNotifications can
        // replace it with the real one.
        //
        // The same reasoning as the audit trail's default, pointing the other way. A host that
        // forgets to wire the trail should still have a trail; a host that has not asked to
        // send should not send. Both defaults are the safe answer to "what if somebody forgets".
        services.TryAddSingleton<INotificationPolicy>(new NotificationPolicy(
            sendingIsAllowed: false,
            suppressionReason:
                "This host has not been configured to send anything. " +
                "See AddAkibaNotifications in Akiba.Web."));

        // The trail is on from the moment the infrastructure is registered, recording changes
        // with no actor against them. A host that knows who is signed in refines that by
        // calling AuditTrail.Configure with its own subject - see Akiba.Web. Doing it this way
        // round means a host that forgets still has a trail, rather than silently having none.
        AuditTrail.Configure(() => null);
        services.AddScoped<IAkibaAccounts, AkibaAccounts>();

        services.AddSingleton<IBankStatementReader, BankStatementReader>();

        services.AddScoped<ChartOfAccountsSeeder>();

        services.AddSingleton<IDeductionScheduleWriter, DeductionScheduleWriter>();
        services.AddSingleton<IMemberStatementWriter, MemberStatementWriter>();
        services.AddSingleton<IShareholdingSummaryWriter, ShareholdingSummaryWriter>();
        services.AddSingleton<IAgmPackWriter, AgmPackWriter>();
        services.AddSingleton<IDividendScheduleWriter, DividendScheduleWriter>();

        // QuestPDF is MIT below a revenue threshold Akiba is far beneath. Declaring it is a
        // licence term, not a formality.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        services.AddSingleton<IClock, SystemClock>();

        return services;
    }

    /// <summary>
    /// Registers a fixed signed-in official.
    /// </summary>
    /// <remarks>
    /// For tests, and for development before ASP.NET Core Identity arrives in milestone 15.
    /// <b>Never call this in Production</b> - every ledger entry would be attributed to the
    /// same person regardless of who acted, which is the opposite of an audit trail. The host
    /// guards the call and logs plainly when it is in use.
    /// </remarks>
    public static IServiceCollection AddAkibaTestUser(this IServiceCollection services, Actor actor)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ICurrentUser>(new FixedCurrentUser(actor));

        return services;
    }
}
