using System.Text.RegularExpressions;
using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.Presentation;
using Zphil.Roz.Services;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Presentation;

public class ResponseFormatterTruncationHintTests(WorkspaceFixture fixture)
{
    private readonly NavigationService navigationService = TestFileHelper.CreateNavigationService(fixture);
    private readonly ReferenceService referenceService = TestFileHelper.CreateReferenceService(fixture);

    // --- FindSymbol ---

    [Fact]
    public async Task Format_FindSymbol_Truncated_ShowsCountAndIncreaseHint()
    {
        // Arrange — maxResults=1 forces truncation since "Shape" matches multiple symbols
        FindSymbolResult result = await navigationService.FindSymbolAsync("Shape", maxResults: 1, ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert — concise hint: count, source/tests split, increase suggestion
        output.ShouldContain("showing 1 of");
        output.ShouldContain("source");
        output.ShouldContain("tests");
        output.ShouldContain("increase maxResults");
        // Filter suggestions are no longer enumerated in the hint
        output.ShouldNotContain("containingType");
        output.ShouldNotContain("includeTests");
    }

    [Fact]
    public async Task Format_FindSymbol_Truncated_ExcludeTests_HidesSplit()
    {
        // Arrange — same search but with excludeTests=true; the source/tests split must not appear
        FindSymbolResult result = await navigationService.FindSymbolAsync("Shape", maxResults: 1, excludeTests: true, ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert
        output.ShouldContain("showing 1 of");
        output.ShouldContain("increase maxResults");
        output.ShouldNotContain("source");
        output.ShouldNotContain("tests —");
    }

    [Fact]
    public async Task Format_FindSymbol_Truncated_HintOnOwnLine()
    {
        // Arrange — maxResults=1 forces truncation since "Shape" matches multiple symbols
        FindSymbolResult result = await navigationService.FindSymbolAsync("Shape", maxResults: 1, ct: TestContext.Current.CancellationToken);

        // Act
        string normalized = NavigationResultFormatter.Format(result).Replace("\r\n", "\n");

        // Assert — truncation footer must be on its own line, not glued to the last symbol line
        Regex.IsMatch(normalized, @"(?<!\n)\(\d+ total")
            .ShouldBeFalse("truncation footer must be on its own line");
    }

    [Fact]
    public async Task Format_FindSymbol_BroadBodySearch_HintHasBlankLineSeparator()
    {
        // Arrange — Contains + includeBody with >3 matches (Shape, IShape, ShapeService,
        // ShapeCollection, ShapeCalculator) triggers the broad-search hint
        FindSymbolResult result = await navigationService.FindSymbolAsync(
            "Shape", matchMode: SymbolMatchMode.Contains, includeBody: true, ct: TestContext.Current.CancellationToken);

        // Act
        string normalized = NavigationResultFormatter.Format(result).Replace("\r\n", "\n");

        // Assert — a blank line must separate the symbol list from the broad-search hint
        normalized.ShouldContain("\n\nHint: Broad search");
    }

    // --- FindImplementations on a type (derived-types dispatch) ---

    [Fact]
    public async Task Format_FindImplementations_OnType_Truncated_ShowsCountAndIncreaseHint()
    {
        // Arrange — Shape has multiple subtypes; type dispatch uses find_implementations now
        FindImplementationsResult result = await referenceService.FindImplementationsAsync(null, null, null, "Shape", maxResults: 1, ct: TestContext.Current.CancellationToken);

        // Act
        string output = ReferenceResultFormatter.Format(result);

        // Assert — TestShape lives in TestFixture.Tests, so split must surface
        output.ShouldContain("showing 1 of");
        output.ShouldContain("source");
        output.ShouldContain("tests");
        output.ShouldContain("increase maxResults");
        output.ShouldNotContain("includeTests");
    }

    [Fact]
    public async Task Format_FindImplementations_OnType_Truncated_ExcludeTests_HidesSplit()
    {
        // Arrange — same search with excludeTests=true; split must not appear
        FindImplementationsResult result = await referenceService.FindImplementationsAsync(null, null, null, "Shape", excludeTests: true, maxResults: 1, ct: TestContext.Current.CancellationToken);

        // Act
        string output = ReferenceResultFormatter.Format(result);

        // Assert
        output.ShouldContain("showing 1 of");
        output.ShouldContain("increase maxResults");
        output.ShouldNotContain("source");
        output.ShouldNotContain("tests —");
    }

    // --- FindReferences referenceKinds=invocations ---

    [Fact]
    public async Task Format_FindReferences_Invocations_Truncated_ShowsCountAndIncreaseHint()
    {
        // Arrange — Describe has multiple callers; maxResults=1 forces truncation
        var result = (FindCallersResult)await referenceService.FindReferencesAsync(null, null, null, "Describe", "IShape", ReferenceKind.Invocations, maxResults: 1, ct: TestContext.Current.CancellationToken);

        // Act
        string output = ReferenceResultFormatter.Format(result);

        // Assert — ShapeTestHelper.DescribeAll and Circle_Describe_ReturnsExpected call into Describe
        output.ShouldContain("showing 1 of");
        output.ShouldContain("source");
        output.ShouldContain("tests");
        output.ShouldContain("increase maxResults");
        output.ShouldNotContain("excludeBaseCalls");
        output.ShouldNotContain("includeTests");
    }

    [Fact]
    public async Task Format_FindReferences_Invocations_Truncated_ExcludeTests_HidesSplit()
    {
        // Arrange — same search with excludeTests=true
        var result = (FindCallersResult)await referenceService.FindReferencesAsync(null, null, null, "Describe", "IShape", ReferenceKind.Invocations, excludeTests: true, maxResults: 1, ct: TestContext.Current.CancellationToken);

        // Act
        string output = ReferenceResultFormatter.Format(result);

        // Assert — the truncation footer fires (multiple source callers remain), but because
        // excludeTests already removed the test callers, the "source … / tests —" split is suppressed.
        output.ShouldContain("increase maxResults");
        output.ShouldNotContain("source");
        output.ShouldNotContain("tests —");
    }

    // --- FindReferences ---

    [Fact]
    public async Task Format_FindReferences_Truncated_ShowsCountAndIncreaseHint()
    {
        // Arrange — IShape has many references; maxResults=1 forces truncation
        var result = (FindReferencesResult)await referenceService.FindReferencesAsync(null, null, null, "IShape", maxResults: 1, ct: TestContext.Current.CancellationToken);

        // Act
        string output = ReferenceResultFormatter.Format(result);

        // Assert — IShape is referenced from both source (Circle, Shape, ShapeService) and tests
        output.ShouldContain("showing 1 of");
        output.ShouldContain("source");
        output.ShouldContain("tests");
        output.ShouldContain("increase maxResults");
        output.ShouldNotContain("includeTests");
        output.ShouldNotContain("a more specific symbol");
    }

    [Fact]
    public async Task Format_FindReferences_Truncated_ExcludeTests_HidesSplit()
    {
        // Arrange — same search with excludeTests=true
        var result = (FindReferencesResult)await referenceService.FindReferencesAsync(null, null, null, "IShape", excludeTests: true, maxResults: 1, ct: TestContext.Current.CancellationToken);

        // Act
        string output = ReferenceResultFormatter.Format(result);

        // Assert
        output.ShouldContain("showing 1 of");
        output.ShouldContain("increase maxResults");
        output.ShouldNotContain("source");
        output.ShouldNotContain("tests —");
    }

    // --- Test-only edge case: 0 source, N tests ---

    [Fact]
    public async Task Format_FindSymbol_Truncated_TestsOnly_ShowsZeroSource()
    {
        // Arrange — "ShapeTest" matches ShapeTests + ShapeTestHelper, both in TestFixture.Tests.
        // maxResults=1 forces truncation; all matches are test-only so the split must be 0/N.
        FindSymbolResult result = await navigationService.FindSymbolAsync(
            "ShapeTest", matchMode: SymbolMatchMode.StartsWith, maxResults: 1, ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert — split must show 0 source and N tests
        result.TotalCount.ShouldBeGreaterThan(result.Symbols.Count);
        output.ShouldContain("0 source");
        output.ShouldContain($"{result.IncludedTestCount} tests");
    }
}
