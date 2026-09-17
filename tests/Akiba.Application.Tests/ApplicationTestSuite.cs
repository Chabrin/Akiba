using Akiba.Application;

namespace Akiba.Application.Tests;

/// <summary>
/// Tests for command and query handlers, with ports substituted by in-memory fakes.
/// Anything that needs a real PostgreSQL instance belongs in Akiba.Infrastructure.Tests.
/// </summary>
public sealed class ApplicationTestSuite
{
    [Fact]
    public void Application_assembly_is_wired_into_the_test_run()
    {
        typeof(ApplicationAssemblyMarker).Assembly.GetName().Name
            .Should().Be("Akiba.Application");
    }
}
