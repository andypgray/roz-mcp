using System.Reflection;
using ModelContextProtocol.Server;
using Zphil.Roz.Prompts;

namespace Zphil.Roz.Tests.Prompts;

/// <summary>
///     Snapshot pin for every <c>[McpServerPrompt]</c>: its name, title, and JSON-bound argument set.
///     Prompts surface to the user as <c>/mcp__roz__&lt;name&gt;</c> slash commands, so a rename or
///     an argument change silently alters the client's command UI — this trips a row so the change is
///     reviewed rather than drifting unnoticed. Analog of
///     <see cref="Zphil.Roz.Tests.Pipeline.ToolParameterSnapshotTests" />.
/// </summary>
public class PromptSnapshotTests
{
    [Theory]
    [InlineData("cleanup_dead_code", "Clean up dead code", "scope,publicApiHandling")]
    [InlineData("assess_impact", "Assess change impact", "target,change,project")]
    [InlineData("tighten_accessibility", "Tighten accessibility", "scope,publicApiHandling")]
    [InlineData("decompile_symbol", "Decompile and explain an external symbol", "symbol,focus")]
    [InlineData("fix_diagnostics", "Fix diagnostics", "scope,severity,diagnosticIds")]
    [InlineData("check_breaking_changes", "Check for breaking API changes", "baseline,scope")]
    [InlineData("triage_coverage", "Triage coverage gaps", "scope,baseline")]
    [InlineData("triage_complexity", "Triage complexity hotspots", "scope,baseline")]
    [InlineData("trim_dependencies", "Trim unused dependencies", "scope,kind")]
    [InlineData("assess_upgrade", "Assess a package upgrade", "package,targetVersion,project")]
    public void Prompt_NameTitleAndArgs_MatchSnapshot(string promptName, string expectedTitle, string expectedArgsCsv)
    {
        // Arrange
        MethodInfo method = GetPromptMethods()
            .Single(m => m.GetCustomAttribute<McpServerPromptAttribute>()?.Name == promptName);
        McpServerPromptAttribute attr = method.GetCustomAttribute<McpServerPromptAttribute>()!;

        string[] actualArgs = method.GetParameters()
            .Where(IsBound)
            .Select(p => p.Name!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        string[] expectedArgs = expectedArgsCsv.Split(',')
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Assert
        attr.Title.ShouldBe(expectedTitle);
        actualArgs.ShouldBe(expectedArgs,
            $"Argument snapshot drift on prompt '{promptName}'. Update the InlineData row to match the signature, or revert the rename.");
    }

    [Fact]
    public void Prompts_RegisteredSet_MatchesSnapshot()
    {
        // Act — the full set of shipped prompt names.
        HashSet<string> names = GetPromptMethods()
            .Select(m => m.GetCustomAttribute<McpServerPromptAttribute>()?.Name!)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.Ordinal);

        // Assert — additions/removals must update this list (and the rows above). Order-insensitive:
        // the set's enumeration order is incidental.
        string[] expected =
        [
            "cleanup_dead_code", "assess_impact", "tighten_accessibility",
            "decompile_symbol", "fix_diagnostics", "check_breaking_changes",
            "triage_coverage", "triage_complexity", "trim_dependencies", "assess_upgrade"
        ];
        // ReSharper disable once ArgumentsStyleLiteral — keep `ignoreOrder:` self-documenting.
        names.ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public void EveryPrompt_OpensWithToolPreflight()
    {
        // Arrange / Act — render every recipe and find any that omit the shared preflight phrase.
        string[] missingPreflight = GetPromptMethods()
            .Where(m => !RenderPrompt(m).Contains("restricted `ROZ_TOOLS` subset", StringComparison.Ordinal))
            .Select(m => m.GetCustomAttribute<McpServerPromptAttribute>()!.Name!)
            .ToArray();

        // Assert — every recipe must carry the shared tool-availability preflight
        // (PromptFragments.ToolPreflight) so a scoped ROZ_TOOLS subset surfaces an actionable message
        // instead of failing mid-recipe (the trim_dependencies/get_unused_references trap).
        missingPreflight.ShouldBe([],
            $"Prompts missing {nameof(PromptFragments)}.{nameof(PromptFragments.ToolPreflight)}: {String.Join(", ", missingPreflight)}.");
    }

    [Fact]
    public void AssessUpgrade_WithoutPackage_RendersOutdatedPickFlow()
    {
        // Naming a package renders a direct assessment; omitting it renders the discover-and-pick flow.
        string named = AssessUpgradePrompt.AssessUpgrade("Newtonsoft.Json");
        string discover = AssessUpgradePrompt.AssessUpgrade();

        named.ShouldContain("Newtonsoft.Json");
        named.ShouldNotContain("didn't name");

        discover.ShouldContain("dotnet list package --outdated");
        discover.ShouldContain("pick-one selection");
    }

    [Fact]
    public void TriageComplexity_Default_RendersMetricSourcesAndRoutes()
    {
        // The recipe's contract: source the metric from a provider the user already has (never compute it),
        // then route each hotspot to the specialist prompt rather than refactoring itself.
        string recipe = TriageComplexityPrompt.TriageComplexity();

        // Provider-agnostic metric acquisition — at least the CRAP, CA15xx-diagnostics, and metrics-report paths.
        recipe.ShouldContain("CRAP");
        recipe.ShouldContain("get_diagnostics");
        recipe.ShouldContain("Microsoft.CodeAnalysis.Metrics");

        // Routes to the specialist prompts (the value-add over a bare metrics table).
        recipe.ShouldContain("cleanup_dead_code");
        recipe.ShouldContain("assess_impact");
        recipe.ShouldContain("tighten_accessibility");
        recipe.ShouldContain("fix_diagnostics");

        // The blast-radius differentiator: high-impact hotspots are quantified inline via analyze_change_impact.
        recipe.ShouldContain("analyze_change_impact");
    }

    [Fact]
    public void CheckBreakingChanges_CensusesViaFindReferences_NotImpactAnalysis()
    {
        // The redesigned recipe censuses consumers with find_references and classifies by hand;
        // analyze_change_impact is deliberately unused because it models a *proposed* change against the
        // current code and so misreports already-made edits. Pin the guard sentence itself, not name-absence.
        string recipe = CheckBreakingChangesPrompt.CheckBreakingChanges();

        recipe.ShouldContain("find_references");
        recipe.ShouldContain("referenceKinds=all");
        recipe.ShouldContain("includeTests=true");
        recipe.ShouldContain("Do NOT run `analyze_change_impact`");
        recipe.ShouldContain("whose **body** changed");
    }

    [Fact]
    public void TrimDependencies_VerifyIsBuildFirst_NeverIncremental()
    {
        // trim_dependencies verifies with dotnet build, never get_diagnostics incremental: dotnet remove
        // edits the .csproj behind the workspace and never recaptures the baseline, so an incremental verify
        // would read false-clean.
        string recipe = TrimDependenciesPrompt.TrimDependencies();

        recipe.ShouldContain("dotnet build");
        recipe.ShouldNotContain("incremental");
    }

    [Fact]
    public void MutatingPrompts_VerifyBaselineRef_MatchesCaptureSite()
    {
        // Every mutating recipe's verify text must name the baseline it actually captured — not the step-1
        // default when the baseline was taken elsewhere (or, for trim_dependencies, not at all).
        string cleanup = DeadCodeCleanupPrompt.CleanupDeadCode("scope");
        string tighten = AccessibilityTightenPrompt.TightenAccessibility("scope");
        string fix = FixDiagnosticsPrompt.FixDiagnostics();
        string assessImpact = AssessImpactPrompt.AssessImpact("Order.Total", "make it a long");
        string assessUpgrade = AssessUpgradePrompt.AssessUpgrade("Newtonsoft.Json");

        cleanup.ShouldContain("the step-1 baseline");
        tighten.ShouldContain("the step-1 baseline");
        fix.ShouldContain("the step-1 baseline");

        // assess_impact captures its baseline in the apply branch, strictly before the edits.
        assessImpact.ShouldContain("the baseline you just captured");
        assessImpact.IndexOf("resetBaseline=true", StringComparison.Ordinal)
            .ShouldBeLessThan(assessImpact.IndexOf("make the edits", StringComparison.Ordinal));

        assessUpgrade.ShouldContain("the baseline you captured before upgrading");
    }

    [Fact]
    public void AsMultipleChoice_CarriesOverflowAndHeadlessFallbacks()
    {
        // Both the pick-one and tick-many renderings carry the overflow (too many options) and headless
        // (no user to answer) fallbacks, so every ask site degrades gracefully.
        foreach (string rendering in new[] { PromptFragments.AsMultipleChoice(true), PromptFragments.AsMultipleChoice(false) })
        {
            rendering.ShouldContain("most conservative option");
            rendering.ShouldContain("several consecutive questions");
        }
    }

    [Fact]
    public void AssessImpact_OffersRenameAndBehavioralEscapeHatches()
    {
        // A rename routes to rename_symbol (atomic solution-wide rewrite); a behavioral change the tool
        // can't model is assessed by hand rather than forced into a changeKind.
        string recipe = AssessImpactPrompt.AssessImpact("Order.Total", "rename it");

        recipe.ShouldContain("rename_symbol");
        recipe.ShouldContain("can't model");
    }

    private static string RenderPrompt(MethodInfo method)
    {
        // Supply a placeholder for each required string arg; optional args take their default.
        object?[] args = method.GetParameters()
            .Select(p => p.HasDefaultValue
                ? p.DefaultValue
                : p.ParameterType == typeof(string)
                    ? "scope"
                    : null)
            .ToArray();
        return (string)method.Invoke(null, args)!;
    }

    private static IEnumerable<MethodInfo> GetPromptMethods()
    {
        return typeof(DeadCodeCleanupPrompt).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerPromptTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() is not null);
    }

    private static bool IsBound(ParameterInfo p) =>
        p.Name is not null && p.ParameterType != typeof(CancellationToken);
}
