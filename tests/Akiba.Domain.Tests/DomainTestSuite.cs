using Akiba.Domain;

namespace Akiba.Domain.Tests;

/// <summary>
/// Pure unit tests for the domain model. No database, no containers, no test doubles for
/// infrastructure - if a test here needs any of those, the logic under test is in the
/// wrong layer.
///
/// What lands here, in milestone order (BUILD_BRIEF.md section 13):
///   2. Money: arithmetic, rounding policy, and Allocate (property-based, via FsCheck).
///   3. JournalEntry: an unbalanced entry cannot be constructed.
///   4. Shareholding as at a date, derived from ledger entries.
///   5. Interest strategies against real figures from the existing ledger:
///        25,000 emergency  -> 27,500 over 5 months at 5,500/month
///        60,000 normal     -> 66,000 over 12 months
///        175,000 normal    -> 192,500 over 20 months
///   6. Schedule generation with the one-month grace period.
///   7. Guarantor coverage, liability strategies, exit flagging.
/// </summary>
public sealed class DomainTestSuite
{
    [Fact]
    public void Domain_assembly_is_wired_into_the_test_run()
    {
        typeof(DomainAssemblyMarker).Assembly.GetName().Name
            .Should().Be("Akiba.Domain");
    }
}
