using Zphil.Roz.Enums;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Wall-clock stress for <see cref="WorkspaceTools.GetUnusedReferences" /> across
///     nopCommerce's ~35 projects, ~300K LOC. Uses the same baseline-comparison pattern as the
///     other Nop scale tests via <see cref="TimingHelper" />.
/// </summary>
[Trait("Category", "Stress")]
[Collection("NopReadOnlyWorkspace")]
public class NopUnusedReferenceStressTests(NopWorkspaceFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task GetUnusedReferences_KindAll_WholeSolution_CompletesWithinBudget()
    {
        // Arrange
        WorkspaceTools tools = CreateWorkspaceTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        string result = await TimingHelper.TimeAsync("Nop_GetUnusedReferences_All_WholeSolution",
            () => tools.GetUnusedReferences(UnusedReferencesKind.All, ct: cts.Token), output);

        // Assert
        output.WriteLine($"Result length: {result.Length}");
        output.WriteLine(result[..Math.Min(2000, result.Length)]);

        // kind=All analyzes both project and package references solution-wide. The report must be
        // structured — it either names a real project with unused refs or explicitly reports a
        // clean result — not merely non-empty whitespace.
        result.ShouldContain("references");
        bool structured = result.Contains("Nop.", StringComparison.Ordinal)
                          || result.Contains("No unused", StringComparison.Ordinal);
        structured.ShouldBeTrue(
            $"kind=All output should name a project or report a clean result. Got: {result[..Math.Min(500, result.Length)]}");

        // Framework refs must never be flagged regardless of kind.
        result.ShouldNotContain("Microsoft.NETCore.App.Ref");
        result.ShouldNotContain("Microsoft.AspNetCore.App.Ref");
    }

    [Fact]
    public async Task GetUnusedReferences_KindProjects_ConfidentSignal()
    {
        // Arrange — kind=Projects is the confident signal: <ProjectReference> entries genuinely
        // unused by source. It must not carry the package weak-signal caveat.
        WorkspaceTools tools = CreateWorkspaceTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        string result = await TimingHelper.TimeAsync("Nop_GetUnusedReferences_Projects_WholeSolution",
            () => tools.GetUnusedReferences(ct: cts.Token), output);

        // Assert — project-framed, no weak-signal caveat, framework refs never flagged
        output.WriteLine(result[..Math.Min(2000, result.Length)]);
        result.ShouldContain("project");
        result.ShouldNotContain("weak"); // the weak-signal caveat is packages-only
        result.ShouldNotContain("Microsoft.NETCore.App.Ref");
        result.ShouldNotContain("Microsoft.AspNetCore.App.Ref");
    }

    [Fact]
    public async Task GetUnusedReferences_KindPackages_WeakSignal()
    {
        // Arrange — kind=Packages is the weak signal: a 35-project solution pulls in analyzer,
        // generator, and runtime-only packages that don't appear in source, so hits are expected
        // and must be framed with the weak-signal caveat.
        WorkspaceTools tools = CreateWorkspaceTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        string result = await TimingHelper.TimeAsync("Nop_GetUnusedReferences_Packages_WholeSolution",
            () => tools.GetUnusedReferences(UnusedReferencesKind.Packages, ct: cts.Token), output);

        // Assert — package-framed with the weak-signal caveat; framework refs never flagged
        output.WriteLine(result[..Math.Min(2000, result.Length)]);
        result.ShouldContain("package");
        result.ShouldContain("weak");
        result.ShouldNotContain("Microsoft.NETCore.App.Ref");
        result.ShouldNotContain("Microsoft.AspNetCore.App.Ref");
    }
}
