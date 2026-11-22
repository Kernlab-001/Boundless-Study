// Architecture conformance tests for the Boundless Study solution.
// Source of truth: docs/04-architecture/system-architecture.md sections 4 (process
// and component model) and 5 (dependency rules). These tests fail the build if a
// project's references, target framework, or purity rules drift from the approved
// architecture. When a milestone legitimately adds a reference or package, the
// approved graph table here is updated with that milestone.

using System.Reflection;
using System.Xml.Linq;

namespace BoundlessStudy.Architecture.Tests;

public class ArchitectureConformanceTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>
    /// The approved project reference graph. Keys are project file names without
    /// extension; values are the exact set of ProjectReference entries each
    /// project is allowed to have.
    /// </summary>
    private static readonly Dictionary<string, string[]> ApprovedReferences = new()
    {
        ["BoundlessStudy.Domain"] = Array.Empty<string>(),
        ["BoundlessStudy.Application"] = new[] { "BoundlessStudy.Domain" },
        ["BoundlessStudy.Infrastructure"] = new[] { "BoundlessStudy.Application", "BoundlessStudy.Domain" },
        ["BoundlessStudy.Contracts"] = Array.Empty<string>(),
        ["BoundlessStudy.Desktop"] = new[] { "BoundlessStudy.Application", "BoundlessStudy.Contracts" },
        ["BoundlessStudy.Agent"] = new[]
        {
            "BoundlessStudy.Application",
            "BoundlessStudy.Contracts",
            "BoundlessStudy.Domain",
            "BoundlessStudy.Infrastructure",
        },
        ["BoundlessStudy.Service"] = new[] { "BoundlessStudy.Contracts" },
        ["BoundlessStudy.Bridge"] = new[] { "BoundlessStudy.Contracts" },
        ["BoundlessStudy.Domain.Tests"] = new[] { "BoundlessStudy.Domain" },
        ["BoundlessStudy.Application.Tests"] = new[] { "BoundlessStudy.Application", "BoundlessStudy.Domain" },
        ["BoundlessStudy.Infrastructure.Tests"] = new[]
        {
            "BoundlessStudy.Application",
            "BoundlessStudy.Domain",
            "BoundlessStudy.Infrastructure",
        },
        ["BoundlessStudy.Architecture.Tests"] = new[]
        {
            "BoundlessStudy.Agent",
            "BoundlessStudy.Application",
            "BoundlessStudy.Bridge",
            "BoundlessStudy.Contracts",
            "BoundlessStudy.Desktop",
            "BoundlessStudy.Domain",
            "BoundlessStudy.Infrastructure",
            "BoundlessStudy.Service",
        },
    };

    /// <summary>
    /// Approved target frameworks: the three Windows-integrated executables target
    /// net10.0-windows; everything else stays portable net10.0.
    /// </summary>
    private static readonly Dictionary<string, string> ApprovedTargetFrameworks = new()
    {
        ["BoundlessStudy.Domain"] = "net10.0",
        ["BoundlessStudy.Application"] = "net10.0",
        ["BoundlessStudy.Infrastructure"] = "net10.0",
        ["BoundlessStudy.Contracts"] = "net10.0",
        ["BoundlessStudy.Desktop"] = "net10.0-windows",
        ["BoundlessStudy.Agent"] = "net10.0-windows",
        ["BoundlessStudy.Service"] = "net10.0-windows",
        ["BoundlessStudy.Bridge"] = "net10.0",
        ["BoundlessStudy.Domain.Tests"] = "net10.0",
        ["BoundlessStudy.Application.Tests"] = "net10.0",
        ["BoundlessStudy.Infrastructure.Tests"] = "net10.0",
        ["BoundlessStudy.Architecture.Tests"] = "net10.0-windows",
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BoundlessStudy.slnx"))
                || File.Exists(Path.Combine(dir.FullName, "BoundlessStudy.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("Could not locate the Boundless Study solution file above the test directory.");
    }

    private static XDocument LoadProject(string projectName)
    {
        var path = Path.Combine(RepoRoot, "src", projectName, projectName + ".csproj");
        if (!File.Exists(path))
        {
            path = Path.Combine(RepoRoot, "tests", projectName, projectName + ".csproj");
        }

        Assert.True(File.Exists(path), $"Project file not found for {projectName}.");
        return XDocument.Load(path);
    }

    private static string? Property(XDocument project, string name)
    {
        return project.Descendants(project.Root!.Name.Namespace + name)
            .Select(e => e.Value.Trim())
            .FirstOrDefault();
    }

    public static IEnumerable<object[]> AllProjects()
    {
        return ApprovedReferences.Keys.Select(name => new object[] { name });
    }

    [Theory]
    [MemberData(nameof(AllProjects))]
    public void ProjectReferencesMatchApprovedGraph(string projectName)
    {
        var project = LoadProject(projectName);
        var references = project.Descendants(project.Root!.Name.Namespace + "ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var approved = ApprovedReferences[projectName].OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(approved, references);
    }

    [Theory]
    [MemberData(nameof(AllProjects))]
    public void TargetFrameworkMatchesApprovedConfiguration(string projectName)
    {
        var project = LoadProject(projectName);
        Assert.Equal(ApprovedTargetFrameworks[projectName], Property(project, "TargetFramework"));
    }

    [Fact]
    public void OnlyTheDesktopUsesWpf()
    {
        foreach (var projectName in ApprovedReferences.Keys)
        {
            var project = LoadProject(projectName);
            var useWpf = Property(project, "UseWPF");
            if (projectName == "BoundlessStudy.Desktop")
            {
                Assert.Equal("true", useWpf);
            }
            else
            {
                Assert.True(useWpf is null, $"{projectName} must not enable UseWPF.");
            }
        }
    }

    [Fact]
    public void DomainAndApplicationAndContractsRemainDependencyFree()
    {
        // Domain references nothing outside the .NET base library; Application
        // contains no database implementation; Contracts contains no business
        // logic. None of the three may take package dependencies.
        foreach (var projectName in new[]
                 {
                     "BoundlessStudy.Domain",
                     "BoundlessStudy.Application",
                     "BoundlessStudy.Contracts",
                 })
        {
            var project = LoadProject(projectName);
            var packages = project.Descendants(project.Root!.Name.Namespace + "PackageReference").ToArray();
            Assert.True(packages.Length == 0, $"{projectName} must not declare PackageReference entries.");
        }
    }

    [Fact]
    public void CentralBuildSettingsAreEnforced()
    {
        var props = XDocument.Load(Path.Combine(RepoRoot, "Directory.Build.props"));
        Assert.Equal("enable", Property(props, "Nullable"));
        Assert.Equal("true", Property(props, "TreatWarningsAsErrors"));
    }

    [Fact]
    public void SolutionContainsAllApprovedProjects()
    {
        var slnxPath = Path.Combine(RepoRoot, "BoundlessStudy.slnx");
        if (File.Exists(slnxPath))
        {
            var solution = XDocument.Load(slnxPath);
            var paths = solution.Descendants(solution.Root!.Name.Namespace + "Project")
                .Select(e => e.Attribute("Path")!.Value.Replace('\\', '/'))
                .ToArray();
            foreach (var projectName in ApprovedReferences.Keys)
            {
                Assert.Contains(paths, p => p.EndsWith(projectName + ".csproj", StringComparison.Ordinal));
            }
        }
        else
        {
            var sln = File.ReadAllLines(Path.Combine(RepoRoot, "BoundlessStudy.sln"));
            foreach (var projectName in ApprovedReferences.Keys)
            {
                Assert.Contains(sln, line => line.Contains(projectName + ".csproj"));
            }
        }
    }

    [Theory]
    [InlineData("BoundlessStudy.Domain")]
    [InlineData("BoundlessStudy.Application")]
    [InlineData("BoundlessStudy.Infrastructure")]
    [InlineData("BoundlessStudy.Contracts")]
    [InlineData("BoundlessStudy.Desktop")]
    [InlineData("BoundlessStudy.Agent")]
    [InlineData("BoundlessStudy.Service")]
    [InlineData("BoundlessStudy.Bridge")]
    public void ProductAssembliesLoadWithApprovedVersion(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);
        Assert.Equal(assemblyName, assembly.GetName().Name);
        Assert.Equal(new Version(0, 1, 0, 0), assembly.GetName().Version);
    }
}
