using Akiba.Infrastructure;

namespace Akiba.Infrastructure.Tests;

/// <summary>
/// Integration tests against a real PostgreSQL server.
///
/// Never SQLite. Never the in-memory provider. Both differ from PostgreSQL in exactly the
/// places that matter to a ledger - numeric precision, transaction isolation and constraint
/// enforcement - so a green test against either would prove nothing about production.
///
/// See PostgresFixture for how the tests reach a server.
/// </summary>
public sealed class InfrastructureTestSuite
{
    [Fact]
    public void Infrastructure_assembly_is_wired_into_the_test_run()
    {
        typeof(InfrastructureAssemblyMarker).Assembly.GetName().Name
            .Should().Be("Akiba.Infrastructure");
    }
}
