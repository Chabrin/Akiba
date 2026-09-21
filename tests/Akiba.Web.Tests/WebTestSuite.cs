using Xunit;

// One host at a time.
//
// Akiba applies its migrations at startup, which suits one application on one machine and does
// not suit several starting at once - the comment in Program.cs says exactly that, and running
// these tests in parallel proved it: two hosts raced and the loser failed with
// 'relation "accounting_periods" already exists'.
//
// The fix belongs here rather than in the application. Akiba is deliberately a single instance
// against a single database, and making startup safe for a race that only a test harness
// creates would be solving a problem the society does not have.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Akiba.Web.Tests;

/// <summary>
/// Security tests against the real pipeline.
///
/// What lands here:
///   - the headers every response carries, and the content security policy
///   - which endpoints answer without a session, and which never do
///   - antiforgery on the form posts that sign somebody in or out
///   - rate limiting on the endpoints worth guessing at
///
/// These need the same PostgreSQL the other integration tests use. Point them somewhere with
/// AKIBA_TEST_POSTGRES, the same as Akiba.Infrastructure.Tests.
/// </summary>
public sealed class WebTestSuite
{
    [Fact]
    public void The_web_assembly_is_wired_into_the_test_run()
    {
        typeof(Program).Assembly.GetName().Name.Should().Be("Akiba.Web");
    }
}
