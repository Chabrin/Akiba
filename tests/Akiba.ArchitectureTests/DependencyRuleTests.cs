namespace Akiba.ArchitectureTests;

/// <summary>
/// The clean-architecture dependency rules, enforced as tests rather than as a diagram
/// nobody reads. A violation fails the build in CI.
///
/// Dependencies point inward, always:
///
///     Akiba.Web  ───────────┐
///         │                 │
///         ▼                 ▼
///     Akiba.Application ◄── Akiba.Infrastructure
///         │
///         ▼
///     Akiba.Domain   (references nothing at all)
/// </summary>
public sealed class DependencyRuleTests
{
    /// <summary>
    /// The only project references each project is permitted to declare.
    /// Anything present that is not on this list, or missing from it, fails.
    /// </summary>
    public static TheoryData<string, string[]> AllowedProjectReferences => new()
    {
        { "Akiba.Domain", [] },
        { "Akiba.Application", ["Akiba.Domain"] },
        { "Akiba.Infrastructure", ["Akiba.Application"] },
        { "Akiba.Web", ["Akiba.Application", "Akiba.Infrastructure"] },
    };

    [Theory]
    [MemberData(nameof(AllowedProjectReferences))]
    public void Project_declares_exactly_the_references_the_architecture_allows(
        string projectName,
        string[] allowed)
    {
        var project = SolutionLayout.Project(projectName);

        project.ProjectReferences.Should().BeEquivalentTo(
            allowed,
            because:
                $"{project.RelativePath} may only depend on [{string.Join(", ", allowed)}]. " +
                "Dependencies in clean architecture point inward; see ARCHITECTURE.md.");
    }

    [Fact]
    public void Domain_references_no_other_project()
    {
        var domain = SolutionLayout.Project("Akiba.Domain");

        domain.ProjectReferences.Should().BeEmpty(
            because:
                "Akiba.Domain is the innermost layer. It models what Akiba is, and must " +
                "stay independent of how anything is stored, dispatched or displayed.");
    }

    [Fact]
    public void Application_does_not_reference_Infrastructure()
    {
        var application = SolutionLayout.Project("Akiba.Application");

        application.ProjectReferences.Should().NotContain(
            "Akiba.Infrastructure",
            because:
                "Akiba.Application defines PORT interfaces (repositories, clock, email, SMS, " +
                "file storage) and Akiba.Infrastructure implements them. The dependency runs " +
                "that way round and never the other.");
    }

    [Fact]
    public void Nothing_references_the_web_project()
    {
        // Test projects may reference it. Akiba.ArchitectureTests reads its project file, and
        // Akiba.Web.Tests hosts the real application so that the security of the pipeline -
        // middleware order, which endpoints answer without a session - is proved by making
        // requests rather than by reading code. Neither ships.
        //
        // The rule that matters is the one below: nothing under src/ may reference it, because
        // a dependency pointing outward from the composition root is how the layering stops
        // meaning anything.
        var offenders = SolutionLayout.Projects.Values
            .Where(project => !IsTestProject(project.RelativePath))
            .Where(project => project.ProjectReferences.Contains("Akiba.Web"))
            .Select(project => project.RelativePath)
            .ToList();

        offenders.Should().BeEmpty(
            because:
                "Akiba.Web is the composition root and the outermost layer. Nothing in src/ " +
                "may reference it; a test project may, and only in order to inspect or host it.");
    }

    /// <summary>Whether a project lives under tests/ and therefore never ships.</summary>
    private static bool IsTestProject(string relativePath) =>
        relativePath.Replace('\\', '/').StartsWith("tests/", StringComparison.Ordinal);

    [Fact]
    public void Every_project_on_disk_is_in_the_solution()
    {
        var solution = File.ReadAllText(Path.Combine(SolutionLayout.RepositoryRoot, "Akiba.sln"));

        var missing = SolutionLayout.Projects.Values
            .Where(project => !solution.Contains(project.Name + ".csproj", StringComparison.Ordinal))
            .Select(project => project.RelativePath)
            .ToList();

        missing.Should().BeEmpty(
            because:
                "a project missing from Akiba.sln is a project CI never builds and never " +
                "runs the tests of. It would be invisible until it broke something.");
    }
}
