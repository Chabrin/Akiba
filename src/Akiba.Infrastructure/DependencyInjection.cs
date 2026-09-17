using Akiba.Application.Abstractions;
using Akiba.Infrastructure.Persistence;
using Akiba.Infrastructure.Persistence.Repositories;
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

        services.AddScoped<ChartOfAccountsSeeder>();

        services.AddSingleton<IClock, SystemClock>();

        return services;
    }
}
