using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Akiba.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build a context at design time, for creating migrations.
/// </summary>
/// <remarks>
/// The connection string here is only ever used to work out the provider's SQL dialect - no
/// migration command connects to a real Akiba database, and this one points at nothing.
/// </remarks>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AkibaDbContext>
{
    public AkibaDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AkibaDbContext>()
            .UseNpgsql("Host=localhost;Database=akiba_design_time;Username=akiba;Password=none")
            .Options;

        return new AkibaDbContext(options);
    }
}
