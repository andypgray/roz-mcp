using Zphil.Roz.Enums;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;
using Zphil.Roz.Presentation;

namespace Zphil.Roz.Tests.Presentation;

public class WorkspaceInfoDetailLevelTests
{
    private static WorkspaceInfoResult CreateSmallResult()
    {
        List<ProjectInfo> projects =
        [
            new("MyApp", "console", "C#", "net10.0", "14.0", "enable", 15),
            new("MyApp.Tests", "test", "C#", "net10.0", "14.0", "enable", 8)
        ];

        List<ProjectDependencyInfo> deps =
        [
            new("MyApp", [], ["MyApp.Tests"]),
            new("MyApp.Tests", ["MyApp"], [])
        ];

        return new WorkspaceInfoResult("MyApp", "C:\\test\\MyApp.sln", projects, 23, deps);
    }

    private static WorkspaceInfoResult CreateLargeMultiTfmResult()
    {
        List<ProjectInfo> projects = [];
        List<ProjectDependencyInfo> deps = [];

        string[] tfms = ["net8.0", "net10.0"];
        string[] types = ["classlib", "classlib", "classlib", "test"];

        for (var i = 0; i < 80; i++)
        {
            var baseName = $"Orleans.Project{i}";
            string projectType = types[i % types.Length];

            foreach (string tfm in tfms)
            {
                var name = $"{baseName}({tfm})";
                projects.Add(new ProjectInfo(name, projectType, "C#", tfm, "13", "enable", 20 + i));

                // Add dependencies to first few projects
                List<string> references = i > 2
                    ? [$"Orleans.Project0({tfm})", $"Orleans.Project1({tfm})"]
                    : [];
                deps.Add(new ProjectDependencyInfo(name, references, []));
            }
        }

        int totalDocs = projects.Sum(p => p.DocCount);
        return new WorkspaceInfoResult("Orleans", "C:\\repos\\orleans\\Orleans.sln", projects, totalDocs, deps);
    }

    // Full detail

    [Fact]
    public void Full_MatchesParameterlessOverload()
    {
        // Arrange
        WorkspaceInfoResult result = CreateSmallResult();

        // Act
        // ReSharper disable once RedundantArgumentDefaultValue
        string withLevel = ResponseFormatter.Format(result, DetailLevel.Full);
        string withoutLevel = ResponseFormatter.Format(result);

        // Assert
        withLevel.ShouldBe(withoutLevel);
    }

    [Fact]
    public void Full_ContainsDependencyGraph()
    {
        // Arrange
        WorkspaceInfoResult result = CreateSmallResult();

        // Act
        string output = ResponseFormatter.Format(result);

        // Assert
        output.ShouldContain("Project Dependencies:");
        output.ShouldContain("MyApp.Tests \u2192 MyApp");
    }

    [Fact]
    public void Full_ContainsPerProjectMetadata()
    {
        // Arrange
        WorkspaceInfoResult result = CreateSmallResult();

        // Act
        string output = ResponseFormatter.Format(result);

        // Assert
        output.ShouldContain("MyApp [console] (C# 14.0, net10.0, nullable=enable, 15 files)");
        output.ShouldContain("MyApp.Tests [test] (C# 14.0, net10.0, nullable=enable, 8 files)");
    }


    // High detail

    [Fact]
    public void High_OmitsDependencyGraph()
    {
        // Arrange
        WorkspaceInfoResult result = CreateSmallResult();

        // Act
        string output = ResponseFormatter.Format(result, DetailLevel.High);

        // Assert
        output.ShouldNotContain("Project Dependencies:");
        output.ShouldContain("Dependencies omitted");
    }

    [Fact]
    public void High_RetainsPerProjectMetadata()
    {
        // Arrange
        WorkspaceInfoResult result = CreateSmallResult();

        // Act
        string output = ResponseFormatter.Format(result, DetailLevel.High);

        // Assert
        output.ShouldContain("MyApp [console] (C# 14.0, net10.0, nullable=enable, 15 files)");
    }

    [Fact]
    public void High_SuggestsProjectParameter()
    {
        // Arrange
        WorkspaceInfoResult result = CreateSmallResult();

        // Act
        string output = ResponseFormatter.Format(result, DetailLevel.High);

        // Assert
        output.ShouldContain("project parameter");
    }

    // Medium detail

    [Fact]
    public void Medium_GroupsMultiTfmProjects()
    {
        // Arrange
        WorkspaceInfoResult result = CreateLargeMultiTfmResult();

        // Act
        string output = ResponseFormatter.Format(result, DetailLevel.Medium);

        // Assert — should show merged TFMs with pipe separator (alphabetical order)
        output.ShouldContain("net10.0 | net8.0");
        // Should not contain TFM-suffixed project names
        output.ShouldNotContain("Orleans.Project0(net8.0)");
        // Compact format omits language version and nullable context
        output.ShouldNotContain("C# 13");
        output.ShouldNotContain("nullable=");
    }

    [Fact]
    public void Medium_ShowsLogicalProjectCount()
    {
        // Arrange
        WorkspaceInfoResult result = CreateLargeMultiTfmResult();

        // Act
        string output = ResponseFormatter.Format(result, DetailLevel.Medium);

        // Assert — 80 logical projects, 160 total
        output.ShouldContain("80 logical");
        output.ShouldContain("160 total");
    }

    [Fact]
    public void Medium_OmitsDependencyGraph()
    {
        // Arrange
        WorkspaceInfoResult result = CreateLargeMultiTfmResult();

        // Act
        string output = ResponseFormatter.Format(result, DetailLevel.Medium);

        // Assert
        output.ShouldNotContain("Project Dependencies:");
    }

    // Low detail

    [Fact]
    public void Low_GroupsByProjectType()
    {
        // Arrange
        WorkspaceInfoResult result = CreateLargeMultiTfmResult();

        // Act
        string output = ResponseFormatter.Format(result, DetailLevel.Low);

        // Assert — groups by type with counts
        output.ShouldContain("classlib (60):");
        output.ShouldContain("test (20):");
    }

    [Fact]
    public void Low_ShowsProjectNamesWithoutMetadata()
    {
        // Arrange
        WorkspaceInfoResult result = CreateLargeMultiTfmResult();

        // Act
        string output = ResponseFormatter.Format(result, DetailLevel.Low);

        // Assert — names present, metadata absent
        output.ShouldContain("Orleans.Project0");
        output.ShouldNotContain("nullable=");
        output.ShouldNotContain("C# 13");
    }

    [Fact]
    public void Low_ShowsSolutionHeaderWithTotals()
    {
        // Arrange
        WorkspaceInfoResult result = CreateLargeMultiTfmResult();

        // Act
        string output = ResponseFormatter.Format(result, DetailLevel.Low);

        // Assert
        output.ShouldContain("160 projects");
        output.ShouldContain("documents");
    }

    // Minimal detail

    [Fact]
    public void Minimal_ShowsOnlyTotalsAndTypeCounts()
    {
        // Arrange
        WorkspaceInfoResult result = CreateLargeMultiTfmResult();

        // Act
        string output = ResponseFormatter.Format(result, DetailLevel.Minimal);

        // Assert
        output.ShouldContain("160 projects");
        output.ShouldContain("Types:");
        output.ShouldContain("60 classlib");
        output.ShouldContain("20 test");
    }

    [Fact]
    public void Minimal_DoesNotListIndividualProjects()
    {
        // Arrange
        WorkspaceInfoResult result = CreateLargeMultiTfmResult();

        // Act
        string output = ResponseFormatter.Format(result, DetailLevel.Minimal);

        // Assert
        output.ShouldNotContain("Orleans.Project0");
    }

    // Config file provenance line

    [Fact]
    public void Full_WithConfigFileAndAppliedKeys_RendersConfigLine()
    {
        // Arrange
        WorkspaceInfoResult result = CreateSmallResult() with
        {
            Config = new ProjectConfigSeedResult(
                "C:\\test\\.roz.json",
                [new AppliedSetting("ROZ_TOOLS", "read"), new AppliedSetting("ROZ_LOG_LEVEL", "Debug")],
                [], [])
        };

        // Act
        string output = ResponseFormatter.Format(result);

        // Assert
        output.ShouldContain("Config file: C:\\test\\.roz.json (applied: ROZ_TOOLS, ROZ_LOG_LEVEL)");
    }

    [Fact]
    public void Full_WithConfigFileAllOverridden_RendersOverriddenList()
    {
        // Arrange — a file was found but every key lost to a live env var.
        WorkspaceInfoResult result = CreateSmallResult() with
        {
            Config = new ProjectConfigSeedResult("C:\\test\\.roz.json", [], ["ROZ_TOOLS"], [])
        };

        // Act
        string output = ResponseFormatter.Format(result);

        // Assert
        output.ShouldContain("Config file: C:\\test\\.roz.json (applied: none; overridden by env: ROZ_TOOLS)");
    }

    [Fact]
    public void Full_WithConfigFileAppliedAndWarnings_RendersWarningsClause()
    {
        // Arrange — a partially-valid file: one key applied, one skipped with a warning. The line
        // must surface the warning so an agent sees the skipped key without reading the log.
        WorkspaceInfoResult result = CreateSmallResult() with
        {
            Config = new ProjectConfigSeedResult(
                "C:\\test\\.roz.json",
                [new AppliedSetting("ROZ_TOOLS", "read")],
                [],
                ["Unknown key 'PATH' skipped — only ROZ_-prefixed variables from the registry are honored."])
        };

        // Act
        string output = ResponseFormatter.Format(result);

        // Assert
        output.ShouldContain("Config file: C:\\test\\.roz.json (applied: ROZ_TOOLS; warnings: Unknown key 'PATH' skipped");
    }

    [Fact]
    public void Full_WithConfigFileIgnored_RendersIgnoredNotEnvPrecedence()
    {
        // Arrange — an unparseable file: found, nothing applied, nothing overridden, one warning.
        // The line must say the file was ignored, not imply an environment-precedence outcome.
        WorkspaceInfoResult result = CreateSmallResult() with
        {
            Config = new ProjectConfigSeedResult(
                "C:\\test\\.roz.json", [], [],
                [".roz.json is not valid JSON and was ignored: bad token"])
        };

        // Act
        string output = ResponseFormatter.Format(result);

        // Assert
        output.ShouldContain("Config file: C:\\test\\.roz.json (ignored: .roz.json is not valid JSON");
        output.ShouldNotContain("applied: none");
    }

    [Fact]
    public void Full_NoConfigFile_OmitsConfigLine()
    {
        // Arrange
        WorkspaceInfoResult result = CreateSmallResult();

        // Act
        string output = ResponseFormatter.Format(result);

        // Assert
        output.ShouldNotContain("Config file:");
    }

    // Multi-TFM grouping

    [Fact]
    public void GroupMultiTfmProjects_MergesTfmVariants()
    {
        // Arrange
        List<ProjectInfo> projects =
        [
            new("MyLib(net8.0)", "classlib", "C#", "net8.0", "13", "enable", 10),
            new("MyLib(net10.0)", "classlib", "C#", "net10.0", "13", "enable", 10)
        ];

        // Act
        List<GroupedProjectInfo> grouped = WorkspaceInfoFormatter.GroupMultiTfmProjects(projects);

        // Assert
        grouped.Count.ShouldBe(1);
        grouped[0].BaseName.ShouldBe("MyLib");
        grouped[0].Tfms.ShouldBe(["net10.0", "net8.0"]);
    }

    [Fact]
    public void GroupMultiTfmProjects_SingleTfm_PassesThrough()
    {
        // Arrange
        List<ProjectInfo> projects =
        [
            new("MyApp", "console", "C#", "net10.0", "14.0", "enable", 15)
        ];

        // Act
        List<GroupedProjectInfo> grouped = WorkspaceInfoFormatter.GroupMultiTfmProjects(projects);

        // Assert
        grouped.Count.ShouldBe(1);
        grouped[0].BaseName.ShouldBe("MyApp");
        grouped[0].Tfms.ShouldBe(["net10.0"]);
    }

    [Fact]
    public void GroupMultiTfmProjects_UsesMaxDocCount()
    {
        // Arrange — same source files, same count across TFMs
        List<ProjectInfo> projects =
        [
            new("MyLib(net8.0)", "classlib", "C#", "net8.0", "13", "enable", 10),
            new("MyLib(net10.0)", "classlib", "C#", "net10.0", "13", "enable", 10)
        ];

        // Act
        List<GroupedProjectInfo> grouped = WorkspaceInfoFormatter.GroupMultiTfmProjects(projects);

        // Assert — max, not sum
        grouped[0].DocCount.ShouldBe(10);
    }

    // Progressive rendering integration

    [Fact]
    public void ProgressiveRenderer_SmallSolution_ReturnsFull()
    {
        // Arrange
        WorkspaceInfoResult result = CreateSmallResult();

        // Act
        string output = ProgressiveRenderer.Render(result, ResponseFormatter.Format);

        // Assert — Full detail, no reduction note
        output.ShouldContain("Project Dependencies:");
        output.ShouldNotContain("DETAIL REDUCED");
    }

    [Fact]
    public void ProgressiveRenderer_LargeSolution_ReducesDetail()
    {
        // Arrange — create a result that exceeds 80K at Full detail
        WorkspaceInfoResult result = CreateLargeMultiTfmResult();
        string fullOutput = ResponseFormatter.Format(result);

        // Act
        string rendered = ProgressiveRenderer.Render(result, ResponseFormatter.Format, fullOutput.Length / 2);

        // Assert — should be reduced (no dependency graph)
        rendered.ShouldNotContain("Project Dependencies:");
        rendered.Length.ShouldBeLessThan(fullOutput.Length);
    }
}
