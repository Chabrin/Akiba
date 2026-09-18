using Akiba.Application.Abstractions;
using Akiba.Application.Reporting;
using Akiba.Domain.Common;
using Akiba.Infrastructure.Persistence;
using Akiba.Infrastructure.Persistence.Repositories;
using Akiba.Infrastructure.Reconciliation;
using Akiba.Infrastructure.Reporting;
using Akiba.Infrastructure.Identity;
using Akiba.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

        services.AddDbContext<AkibaDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__migrations", AkibaDbContext.Schema)));

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
        services.AddScoped<IAkibaAccounts, AkibaAccounts>();

        services.AddSingleton<IBankStatementReader, BankStatementReader>();

        services.AddScoped<ChartOfAccountsSeeder>();

        services.AddSingleton<IDeductionScheduleWriter, DeductionScheduleWriter>();
        services.AddSingleton<IMemberStatementWriter, MemberStatementWriter>();
        services.AddSingleton<IShareholdingSummaryWriter, ShareholdingSummaryWriter>();
        services.AddSingleton<IAgmPackWriter, AgmPackWriter>();

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
