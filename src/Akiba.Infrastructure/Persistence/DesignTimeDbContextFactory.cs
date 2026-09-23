using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Akiba.Infrastructure.Persistence;

/// <summary>
/// Builds a context for <c>dotnet ef</c>, without starting the application.
/// </summary>
/// <remarks>
/// <para>
/// Adding a migration used to mean booting Akiba.Web, which reads a connection string, applies
/// migrations, seeds the chart of accounts and creates a setup account - all so that a tool
/// could read the model. It also meant the web project carried the migration scaffolder into
/// its published output, which is the sort of thing that is nobody's fault and nobody's
/// intention.
/// </para>
/// <para>
/// This is the documented way round both. <c>dotnet ef migrations add X --project
/// src/Akiba.Infrastructure</c> now needs no startup project and touches no database: the
/// connection string below is only ever used to pick the provider and its version, never to
/// connect.
/// </para>
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AkibaDbContext>
{
    /// <summary>
    /// Enough of a connection string for Npgsql to decide what SQL to generate.
    /// </summary>
    /// <remarks>
    /// Never connected to. Scaffolding a migration compares the model against the previous
    /// migration's snapshot, both of which are files in this repository - a live database would
    /// add nothing and would make adding a migration depend on having one.
    /// </remarks>
    private const string ForScaffoldingOnly =
        "Host=localhost;Database=akiba_design_time;Username=postgres";

    public AkibaDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Akiba") is { Length: > 0 } configured
                ? configured
                : ForScaffoldingOnly;

        var options = new DbContextOptionsBuilder<AkibaDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__migrations", AkibaDbContext.Schema))
            .Options;

        return new AkibaDbContext(options);
    }
}
