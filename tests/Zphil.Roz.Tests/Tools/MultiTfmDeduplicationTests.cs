using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools;

/// <summary>
///     Verifies that symbols from multi-TFM projects (e.g. net8.0 + net10.0) are deduplicated
///     correctly across all tool categories.
/// </summary>
/// <remarks>
///     Without deduplication, every symbol appears once per TFM compilation, causing duplicate
///     results and false "Ambiguous" errors.
/// </remarks>
public class MultiTfmDeduplicationTests(WorkspaceFixture fixture)
{
    private readonly DiagnosticTools diagnosticTools = CreateDiagnosticTools(fixture);
    private readonly NavigationTools navigationTools = CreateNavigationTools(fixture);
    private readonly ReferenceTools referenceTools = CreateReferenceTools(fixture);
    private readonly TypeHierarchyTools typeTools = CreateTypeTools(fixture);

    // ── find_symbol ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("MultiTfmService")]
    [InlineData("IMultiTfmService")]
    [InlineData("MultiTfmBase")]
    [InlineData("MultiTfmDerived")]
    [InlineData("MultiTfmConsumer")]
    public async Task FindSymbol_MultiTfm_ExactMatch_SingleResult(string symbolName)
    {
        // Act
        string result = await navigationTools.FindSymbol([symbolName], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert — "(1):" in header means single deduplicated result, not 2 (one per TFM)
        result.ShouldContain(symbolName);
        result.ShouldContain("(1):");
        result.ShouldNotContain("(2):");
    }

    // ── find_references referenceKinds=invocations (name-based — previously threw "Ambiguous") ─

    [Fact]
    public async Task FindReferences_Invocations_MultiTfm_ByName_NoAmbiguityError()
    {
        // Act — name-based lookup should not throw "Ambiguous: 2 symbols match"
        // Use IMultiTfmService as containingType since callers go through the interface.
        string result = await referenceTools.FindReferences(symbolNames: ["GetValue"], containingType: "IMultiTfmService", referenceKinds: ReferenceKind.Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — should find callers without ambiguity error
        result.ShouldContain("UseService");
    }

    [Fact]
    public async Task FindReferences_Invocations_MultiTfm_ByName_NoDuplicateCallers()
    {
        // Act — Use IMultiTfmService since callers go through the interface.
        string result = await referenceTools.FindReferences(symbolNames: ["Calculate"], containingType: "IMultiTfmService", referenceKinds: ReferenceKind.Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — "(1):" in header means single deduplicated caller
        result.ShouldContain("(1):");
        result.ShouldNotContain("(2):");
        result.ShouldContain("UseService");
    }

    [Fact]
    public async Task FindReferences_Invocations_MultiTfm_ByPosition_NoDuplicateCallers()
    {
        // Arrange — find the line of Calculate method
        string filePath = MultiTfmFile(fixture, "MultiTfmService.cs");
        string content = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);
        int line = GetLineNumber(content, "public int Calculate");

        // Act
        string result = await referenceTools.FindReferences([Loc(filePath, line)], referenceKinds: ReferenceKind.Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — single caller
        result.ShouldContain("(1):");
        result.ShouldNotContain("(2):");
    }

    // ── find_references ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_MultiTfm_NoDuplicateLocations()
    {
        // Act — find references to the GetValue interface member
        string result = await referenceTools.FindReferences(symbolNames: ["GetValue"], containingType: "IMultiTfmService", ct: TestContext.Current.CancellationToken);

        // Assert — "1 location" means single deduplicated reference
        result.ShouldContain("1 location");
        result.ShouldNotContain("2 location");
    }

    // ── find_implementations ─────────────────────────────────────────────────────

    [Fact]
    public async Task FindImplementations_MultiTfm_InterfaceMethodNoDuplicates()
    {
        // Act
        string result = await referenceTools.FindImplementations(symbolNames: ["GetValue"], containingType: "IMultiTfmService", ct: TestContext.Current.CancellationToken);

        // Assert — "(1):" means single deduplicated implementation
        result.ShouldContain("(1):");
        result.ShouldNotContain("(2):");
    }

    [Fact]
    public async Task FindImplementations_MultiTfm_AbstractMemberNoDuplicates()
    {
        // Act
        string result = await referenceTools.FindImplementations(symbolNames: ["Name"], containingType: "MultiTfmBase", ct: TestContext.Current.CancellationToken);

        // Assert — single implementation
        result.ShouldContain("(1):");
        result.ShouldNotContain("(2):");
    }

    // ── find_implementations on types (derived-types dispatch) ──────────────────

    [Fact]
    public async Task FindImplementations_OnType_MultiTfm_InterfaceNoDuplicates()
    {
        // Act
        string result = await referenceTools.FindImplementations(symbolNames: ["IMultiTfmService"], ct: TestContext.Current.CancellationToken);

        // Assert — exactly 1 implementation
        result.ShouldContain("(1):");
        result.ShouldNotContain("(2):");
    }

    [Fact]
    public async Task FindImplementations_OnType_MultiTfm_AbstractClassNoDuplicates()
    {
        // Act
        string result = await referenceTools.FindImplementations(symbolNames: ["MultiTfmBase"], ct: TestContext.Current.CancellationToken);

        // Assert — single derived class
        result.ShouldContain("(1):");
        result.ShouldNotContain("(2):");
    }

    // ── get_type_hierarchy ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetTypeHierarchy_MultiTfm_ByName_NoAmbiguityError()
    {
        // Act — should not throw "Ambiguous: 2 symbols match"
        string result = await typeTools.GetTypeHierarchy(symbolNames: ["MultiTfmDerived"], ct: TestContext.Current.CancellationToken);

        // Assert — shows both types in the hierarchy
        result.ShouldContain("MultiTfmBase");
        result.ShouldContain("MultiTfmDerived");
    }

    [Fact]
    public async Task GetTypeHierarchy_MultiTfm_ByName_ForInterface()
    {
        // Act — resolving a type by name in a multi-TFM project
        string result = await typeTools.GetTypeHierarchy(symbolNames: ["MultiTfmService"], ct: TestContext.Current.CancellationToken);

        // Assert — shows the interface in the hierarchy
        result.ShouldContain("IMultiTfmService");
    }

    // ── get_symbols_overview ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolsOverview_MultiTfm_NoDuplicateTypes()
    {
        // Act
        string filePath = MultiTfmFile(fixture, "MultiTfmService.cs");
        string result = await navigationTools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert — class listed once: "public class MultiTfmService" appears once as a declaration
        int declarationCount = CountOccurrences(result, "public class MultiTfmService");
        declarationCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetSymbolsOverview_MultiTfm_GlobNoDuplicateFiles()
    {
        // Act — use glob to match all MultiTfm .cs files
        string result = await navigationTools.GetSymbolsOverview(["TestFixture.MultiTfm/*.cs"], ct: TestContext.Current.CancellationToken);

        // Assert — each file appears exactly once as a "=== ... ===" section header
        int serviceCount = CountOccurrences(result, $"=== {Path.Combine("TestFixture.MultiTfm", "MultiTfmService.cs")} ===");
        serviceCount.ShouldBe(1);

        int baseCount = CountOccurrences(result, $"=== {Path.Combine("TestFixture.MultiTfm", "MultiTfmBase.cs")} ===");
        baseCount.ShouldBe(1);

        int consumerCount = CountOccurrences(result, $"=== {Path.Combine("TestFixture.MultiTfm", "MultiTfmConsumer.cs")} ===");
        consumerCount.ShouldBe(1);
    }

    // ── find_overloads (already works — regression test) ─────────────────────────

    [Fact]
    public async Task FindOverloads_MultiTfm_StillWorks()
    {
        // Act
        string result = await navigationTools.FindOverloads(symbolNames: ["Calculate"], containingType: "MultiTfmService", ct: TestContext.Current.CancellationToken);

        // Assert — should work without ambiguity error
        result.ShouldContain("Calculate");
    }

    // ── get_diagnostics ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDiagnostics_MultiTfm_NoDuplicateWarnings()
    {
        // Act — get diagnostics scoped to the multi-TFM project
        string result = await diagnosticTools.GetDiagnostics(severity: DiagnosticSeverity.Warning, project: "TestFixture.MultiTfm", ct: TestContext.Current.CancellationToken);

        // Assert — CS0219 (unused variable) should appear once, not once per TFM
        int cs0219Count = CountOccurrences(result, "CS0219");
        cs0219Count.ShouldBe(1);
        result.ShouldContain("1 warning");
    }

    // ── excludeTests count invariant (bug: per-TFM pre-dedup count inflated hint) ──

    [Fact]
    public async Task FindReferences_MultiTfm_ExcludedTestCount_MatchesLocationDelta()
    {
        // Act
        string withTests = await referenceTools.FindReferences(symbolNames: ["TestReferenceTarget"], containingType: "MultiTfmConsumer", includeTests: true, ct: TestContext.Current.CancellationToken);
        string withoutTests = await referenceTools.FindReferences(symbolNames: ["TestReferenceTarget"], containingType: "MultiTfmConsumer", ct: TestContext.Current.CancellationToken);

        int withCount = ExtractReferenceCount(withTests);
        int withoutCount = ExtractReferenceCount(withoutTests);
        int excludedHint = ExtractExcludedHint(withoutTests);

        // Assert — invariant: the excluded hint must equal the count difference,
        // not an inflated per-TFM pre-dedup count.
        excludedHint.ShouldBeGreaterThan(0, "fixture regression: no test-project references to TestReferenceTarget");
        excludedHint.ShouldBe(withCount - withoutCount);
    }

    [Fact]
    public async Task FindReferences_Invocations_MultiTfm_ExcludedTestCount_MatchesCallerDelta()
    {
        // Act
        string withTests = await referenceTools.FindReferences(symbolNames: ["TestReferenceTarget"], containingType: "MultiTfmConsumer", includeTests: true, referenceKinds: ReferenceKind.Invocations, ct: TestContext.Current.CancellationToken);
        string withoutTests = await referenceTools.FindReferences(symbolNames: ["TestReferenceTarget"], containingType: "MultiTfmConsumer", referenceKinds: ReferenceKind.Invocations, ct: TestContext.Current.CancellationToken);

        int withCount = ExtractCallerCount(withTests);
        int withoutCount = ExtractCallerCount(withoutTests);
        int excludedHint = ExtractExcludedHint(withoutTests);

        // Assert — invariant: the excluded hint must equal the count difference,
        // not an inflated per-TFM pre-dedup count.
        excludedHint.ShouldBeGreaterThan(0, "fixture regression: no test-project callers of TestReferenceTarget");
        excludedHint.ShouldBe(withCount - withoutCount);
    }

    // ── skipped-test-project count (bug: multi-TFM test projects counted per-TFM) ──

    [Fact]
    public async Task FindSymbol_MultiTfm_SkippedTestProjectCount_DedupedByStrippedName()
    {
        // Act — solution has 2 distinct test projects: TestFixture.Tests (1 TFM)
        // and TestFixture.MultiTfm.Tests (2 TFMs). The skipped hint must not inflate
        // the 2-TFM project to count as 2.
        // maxResults is set high so no truncation footer intervenes — the skipped-projects
        // footer must then sit directly after the symbol list, exercising the gluing bug.
        string result = await navigationTools.FindSymbol(["Shape"], matchMode: SymbolMatchMode.Contains, maxResults: 1000, ct: TestContext.Current.CancellationToken);

        // Assert — exactly "skipped 2 test project(s)", not 3
        int skipped = ExtractSkippedProjectCount(result);
        skipped.ShouldBe(2);

        // Assert — the hint must be on its own line, not glued to the last symbol line
        string normalized = result.Replace("\r\n", "\n");
        Regex.IsMatch(normalized, @"(?<!\n)\(skipped \d+ test project\(s\)\)")
            .ShouldBeFalse("skipped-projects hint must be on its own line");
    }

    [Fact]
    public async Task GetDiagnostics_MultiTfm_SkippedTestProjectCount_DedupedByStrippedName()
    {
        // Act — solution-wide diagnostics with tests excluded
        string result = await diagnosticTools.GetDiagnostics(severity: DiagnosticSeverity.Warning, ct: TestContext.Current.CancellationToken);

        // Assert — exactly "skipped 2 test project(s)", not 3
        int skipped = ExtractSkippedProjectCount(result);
        skipped.ShouldBe(2);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Parses the <c>(skipped N test project(s))</c> hint, returning 0 if absent.</summary>
    private static int ExtractSkippedProjectCount(string output)
    {
        Match match = Regex.Match(output, @"\(skipped (\d+) test project\(s\)\)");
        return match.Success ? Int32.Parse(match.Groups[1].Value) : 0;
    }

    private static int CountOccurrences(string text, string substring) =>
        text.Split(substring).Length - 1;

    /// <summary>
    ///     Parses <c>"References to 'X' (N location(s)...)"</c> or <c>"showing N of M location(s)"</c>.
    ///     Returns the total count (M if truncated, otherwise N).
    /// </summary>
    private static int ExtractReferenceCount(string output)
    {
        Match truncated = Regex.Match(output, @"\(showing \d+ of (\d+) location\(s\)");
        if (truncated.Success)
        {
            return Int32.Parse(truncated.Groups[1].Value);
        }

        Match simple = Regex.Match(output, @"\((\d+) location\(s\)");
        if (!simple.Success)
        {
            throw new InvalidOperationException($"Could not parse reference count from: {output}");
        }

        return Int32.Parse(simple.Groups[1].Value);
    }

    /// <summary>
    ///     Parses <c>"Callers of 'X' (N):"</c> or <c>"Callers of 'X' (showing N of M):"</c>.
    ///     Returns the total count (M if truncated, otherwise N).
    /// </summary>
    private static int ExtractCallerCount(string output)
    {
        Match truncated = Regex.Match(output, @"Callers of '[^']+' \(showing \d+ of (\d+)\):");
        if (truncated.Success)
        {
            return Int32.Parse(truncated.Groups[1].Value);
        }

        Match simple = Regex.Match(output, @"Callers of '[^']+' \((\d+)\):");
        if (!simple.Success)
        {
            throw new InvalidOperationException($"Could not parse caller count from: {output}");
        }

        return Int32.Parse(simple.Groups[1].Value);
    }

    /// <summary>Parses the <c>(+N in test projects)</c> hint, returning 0 if absent.</summary>
    private static int ExtractExcludedHint(string output)
    {
        Match match = Regex.Match(output, @"\(\+(\d+) in test projects\)");
        return match.Success ? Int32.Parse(match.Groups[1].Value) : 0;
    }

    private static int GetLineNumber(string content, string searchText)
    {
        string[] lines = content.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(searchText))
            {
                return i + 1; // 1-based
            }
        }

        throw new InvalidOperationException($"Could not find '{searchText}' in content");
    }
}
