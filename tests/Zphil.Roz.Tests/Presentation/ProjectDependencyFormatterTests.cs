using Zphil.Roz.Models;
using Zphil.Roz.Presentation;

namespace Zphil.Roz.Tests.Presentation;

public class ProjectDependencyFormatterTests
{
    private static string FormatWithDependencies(List<ProjectDependencyInfo> dependencies)
    {
        var result = new WorkspaceInfoResult(
            "TestSolution", "C:\\test\\TestSolution.sln",
            [new ProjectInfo("Dummy", null, "C#", null, null, null, 0)],
            0, dependencies);
        return ResponseFormatter.Format(result);
    }

    [Fact]
    public void Format_NoDependencies_OmitsDependencySection()
    {
        // Arrange — all projects have empty dependencies
        List<ProjectDependencyInfo> deps =
        [
            new("ProjectA", [], []),
            new("ProjectB", [], [])
        ];

        // Act
        string result = FormatWithDependencies(deps);

        // Assert
        result.ShouldNotContain("Project Dependencies:");
    }

    [Fact]
    public void Format_SingleDependency_ShowsArrowFormat()
    {
        // Arrange — A depends on B
        List<ProjectDependencyInfo> deps =
        [
            new("A", ["B"], []),
            new("B", [], ["A"])
        ];

        // Act
        string result = FormatWithDependencies(deps);

        // Assert
        result.ShouldContain("Project Dependencies:");
        result.ShouldContain("A \u2192 B");
    }

    [Fact]
    public void Format_MultipleDependencies_ShowsCommaSeparated()
    {
        // Arrange — A depends on B, C, D
        List<ProjectDependencyInfo> deps =
        [
            new("A", ["B", "C", "D"], [])
        ];

        // Act
        string result = FormatWithDependencies(deps);

        // Assert
        result.ShouldContain("A \u2192 B, C, D");
    }

    [Fact]
    public void Format_ProjectsWithNoDeps_OmittedFromDependencySection()
    {
        // Arrange — A has deps, B does not
        List<ProjectDependencyInfo> deps =
        [
            new("A", ["B"], []),
            new("B", [], ["A"])
        ];

        // Act
        string result = FormatWithDependencies(deps);

        // Assert — dependency section should mention A but not list B as a row
        string depsSection = result[result.IndexOf("Project Dependencies:", StringComparison.Ordinal)..];
        depsSection.ShouldContain("A \u2192 B");
        depsSection.ShouldNotContain("  B\n");
        depsSection.ShouldNotContain("  B\r");
    }

    [Fact]
    public void Format_MultipleProjectsWithDeps_EachGetsOwnLine()
    {
        // Arrange — A → C, B → C
        List<ProjectDependencyInfo> deps =
        [
            new("A", ["C"], []),
            new("B", ["C"], []),
            new("C", [], ["A", "B"])
        ];

        // Act
        string result = FormatWithDependencies(deps);

        // Assert
        result.ShouldContain("A \u2192 C");
        result.ShouldContain("B \u2192 C");
    }
}
