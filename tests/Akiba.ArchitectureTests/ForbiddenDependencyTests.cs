using System.Reflection;
using Akiba.Domain;

namespace Akiba.ArchitectureTests;

/// <summary>
/// The brief's headline architectural constraint: nothing in Akiba.Domain may reference
/// Entity Framework Core. These tests enforce that, and the related rules that keep each
/// layer's concerns where they belong.
///
/// Two complementary checks run for the domain layer:
///
///   1. The .csproj check catches a forbidden dependency the moment it is DECLARED.
///   2. The compiled-assembly check catches one that is actually USED, including any
///      that arrived transitively.
///
/// Either alone leaves a gap. Both together do not.
/// </summary>
public sealed class ForbiddenDependencyTests
{
    /// <summary>
    /// Package prefixes that must never appear in a given project, with the reason.
    /// Matching is by prefix, so "Npgsql" also bars "Npgsql.EntityFrameworkCore.PostgreSQL".
    /// </summary>
    public static TheoryData<string, string, string> ForbiddenPackages => new()
    {
        // Akiba.Application owns use cases, not machinery.
        { "Akiba.Application", "Microsoft.EntityFrameworkCore", "persistence belongs in Akiba.Infrastructure, behind a repository port" },
        { "Akiba.Application", "Npgsql", "the database driver belongs in Akiba.Infrastructure" },
        { "Akiba.Application", "Hangfire", "define an IBackgroundJobScheduler port; Akiba.Infrastructure implements it with Hangfire" },
        { "Akiba.Application", "MudBlazor", "presentation belongs in Akiba.Web" },
        { "Akiba.Application", "ClosedXML", "report writers belong in Akiba.Infrastructure" },
        { "Akiba.Application", "QuestPDF", "report writers belong in Akiba.Infrastructure" },

        // Akiba.Web is a composition root and a view layer. It never touches the database directly.
        { "Akiba.Web", "Microsoft.EntityFrameworkCore", "Blazor components dispatch commands and queries; they do not open a DbContext" },
        { "Akiba.Web", "Npgsql", "Blazor components dispatch commands and queries; they do not open a connection" },
    };

    [Fact]
    public void Domain_declares_no_package_references_at_all()
    {
        var domain = SolutionLayout.Project("Akiba.Domain");

        domain.PackageReferences.Should().BeEmpty(
            because:
                "Akiba.Domain has no NuGet dependencies by design - not Entity Framework Core, " +
                "not MediatR, not ASP.NET Core, not Npgsql, not a JSON library. The domain " +
                "model is plain C#. If something here seems to need a package, the behaviour " +
                "belongs in Akiba.Application or Akiba.Infrastructure instead.");
    }

    [Theory]
    [MemberData(nameof(ForbiddenPackages))]
    public void Project_does_not_declare_a_forbidden_package(
        string projectName,
        string forbiddenPrefix,
        string reason)
    {
        var project = SolutionLayout.Project(projectName);

        var offenders = project.PackageReferences
            .Where(package => package.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        offenders.Should().BeEmpty(
            because: $"{project.RelativePath} must not depend on {forbiddenPrefix}: {reason}.");
    }

    [Fact]
    public void Domain_assembly_references_nothing_but_the_base_class_library()
    {
        var referenced = typeof(DomainAssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .Where(name => !IsBaseClassLibrary(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        referenced.Should().BeEmpty(
            because:
                "the compiled Akiba.Domain assembly should bind to nothing beyond the .NET base " +
                "class library. Anything else listed here is a framework that has crept into the " +
                "domain model.");
    }

    [Fact]
    public void Domain_assembly_does_not_reference_EntityFrameworkCore()
    {
        var referenced = typeof(DomainAssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToList();

        referenced.Should().NotContain(
            name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase),
            because:
                "persistence concerns must not leak into the domain model. Balances are derived " +
                "from the ledger by domain queries; they are not loaded from mapped columns.");
    }

    /// <summary>
    /// Akiba.Web is allowed to reference Akiba.Infrastructure, so Entity Framework Core will
    /// appear in its dependency graph transitively. That is expected and fine - it is how the
    /// composition root wires up DI. What must not happen is Akiba.Web declaring the dependency
    /// itself, which is covered by <see cref="Project_does_not_declare_a_forbidden_package"/>.
    /// This test documents the distinction so nobody "fixes" it later.
    /// </summary>
    [Fact]
    public void Web_reaches_Infrastructure_only_as_the_composition_root()
    {
        var web = SolutionLayout.Project("Akiba.Web");

        web.ProjectReferences.Should().Contain("Akiba.Infrastructure");
        web.PackageReferences.Should().NotContain(
            package => package.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBaseClassLibrary(string assemblyName) =>
        assemblyName is "netstandard" or "mscorlib"
        || assemblyName.StartsWith("System", StringComparison.Ordinal)
        || assemblyName.StartsWith("Microsoft.CSharp", StringComparison.Ordinal)
        || assemblyName.StartsWith("Microsoft.VisualBasic", StringComparison.Ordinal)
        || assemblyName.StartsWith("Microsoft.Win32", StringComparison.Ordinal);
}
