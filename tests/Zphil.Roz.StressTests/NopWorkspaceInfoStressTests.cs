using System.Text.RegularExpressions;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Scale coverage for <see cref="WorkspaceTools.GetWorkspaceInfo" /> against nopCommerce's
///     ~35-project solution — full listing, project-filter narrowing, and reload — none of which
///     the stress suite exercised before.
/// </summary>
[Trait("Category", "Stress")]
[Collection("NopReadOnlyWorkspace")]
public class NopWorkspaceInfoStressTests(NopWorkspaceFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task GetWorkspaceInfo_LargeSolution_ListsProjects()
    {
        // Arrange
        WorkspaceTools tools = CreateWorkspaceTools(fixture);

        // Act — generous timeout: gathering info for ~35 projects competes for CPU under full-suite parallelism
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string result = await TimingHelper.TimeAsync("Nop_GetWorkspaceInfo_LargeSolution",
            () => tools.GetWorkspaceInfo(ct: cts.Token), output);

        // Assert — names the solution, lists projects, and surfaces per-project TFM + type metadata
        output.WriteLine(result[..Math.Min(2000, result.Length)]);
        result.ShouldContain("NopCommerce");
        result.ShouldContain("Nop.Core");
        result.ShouldContain("net9.0"); // TFM surfaced
        result.ShouldContain("classlib"); // project-type tag surfaced

        Match countMatch = Regex.Match(result, @"(\d+)\s+projects");
        countMatch.Success.ShouldBeTrue("workspace info should report a project count");
        var projectCount = Int32.Parse(countMatch.Groups[1].Value);
        output.WriteLine($"Reported project count: {projectCount}");
        projectCount.ShouldBeGreaterThanOrEqualTo(25, "nopCommerce should report at least 25 projects");
    }

    [Fact]
    public async Task GetWorkspaceInfo_ProjectFilter_NarrowsToOne()
    {
        // Arrange — "Nop.Core" is a substring of exactly one project name in the solution.
        WorkspaceTools tools = CreateWorkspaceTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        string result = await TimingHelper.TimeAsync("Nop_GetWorkspaceInfo_ProjectFilter",
            () => tools.GetWorkspaceInfo("Nop.Core", ct: cts.Token), output);

        // Assert — the project-metadata section narrows to the single matching project. (Other
        // project names may still appear in the "Depended on by" reverse-dependency graph.)
        output.WriteLine(result);
        result.ShouldContain("Nop.Core");
        result.ShouldContain("Projects (1");
    }

    [Fact]
    public async Task GetWorkspaceInfo_Reload_Refreshes()
    {
        // Arrange — reload re-reads all ~35 projects from disk; it must complete and still report
        // the solution, not throw or return an empty summary.
        WorkspaceTools tools = CreateWorkspaceTools(fixture);

        // Act — reloading ~35 projects is heavy; allow ample time under full-suite parallelism
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        string result = await TimingHelper.TimeAsync("Nop_GetWorkspaceInfo_Reload",
            () => tools.GetWorkspaceInfo(reload: true, ct: cts.Token), output);

        // Assert
        output.WriteLine(result);
        result.ShouldContain("reloaded");

        Match countMatch = Regex.Match(result, @"(\d+)\s+projects");
        countMatch.Success.ShouldBeTrue("reload summary should report a project count");
        Int32.Parse(countMatch.Groups[1].Value).ShouldBeGreaterThanOrEqualTo(25,
            "reloaded nopCommerce should still report 25+ projects");
    }
}
