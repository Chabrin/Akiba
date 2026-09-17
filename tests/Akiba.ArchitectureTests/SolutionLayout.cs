using System.Xml.Linq;

namespace Akiba.ArchitectureTests;

/// <summary>
/// Reads the solution's project files off disk so the architecture tests can assert
/// on what was actually declared, not on what survived compilation.
/// </summary>
/// <remarks>
/// Inspecting the compiled assembly is not enough on its own: the C# compiler omits
/// a reference to an assembly whose types are never used, so a stray
/// <c>&lt;PackageReference Include="Microsoft.EntityFrameworkCore" /&gt;</c> in
/// Akiba.Domain could sit there unnoticed until the day somebody used it. Reading the
/// .csproj catches it the moment it is added.
/// </remarks>
internal static class SolutionLayout
{
    /// <summary>The directory containing Akiba.sln.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>Every project file under src/ and tests/, keyed by project name.</summary>
    public static IReadOnlyDictionary<string, ProjectFile> Projects { get; } = LoadProjects();

    public static ProjectFile Project(string name) =>
        Projects.TryGetValue(name, out var project)
            ? project
            : throw new InvalidOperationException(
                $"Project '{name}' was not found under {RepositoryRoot}. " +
                $"Known projects: {string.Join(", ", Projects.Keys.Order())}.");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.EnumerateFiles("Akiba.sln").Any())
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate Akiba.sln walking up from {AppContext.BaseDirectory}.");
    }

    private static Dictionary<string, ProjectFile> LoadProjects()
    {
        var projects = new[] { "src", "tests" }
            .Select(folder => Path.Combine(RepositoryRoot, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.csproj", SearchOption.AllDirectories))
            .Select(ProjectFile.Load);

        return projects.ToDictionary(project => project.Name, StringComparer.Ordinal);
    }
}

/// <summary>A parsed .csproj: what it is called, and what it declares a dependency on.</summary>
internal sealed record ProjectFile(
    string Name,
    string Path,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences)
{
    public static ProjectFile Load(string path)
    {
        var document = XDocument.Load(path);

        var projectReferences = document
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => System.IO.Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
            .Order(StringComparer.Ordinal)
            .ToList();

        var packageReferences = document
            .Descendants("PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => include!)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new ProjectFile(
            System.IO.Path.GetFileNameWithoutExtension(path),
            path,
            projectReferences,
            packageReferences);
    }

    /// <summary>Path relative to the repository root, for readable assertion messages.</summary>
    public string RelativePath =>
        System.IO.Path.GetRelativePath(SolutionLayout.RepositoryRoot, Path).Replace('\\', '/');
}
