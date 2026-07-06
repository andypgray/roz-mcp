using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.Presentation;
using Zphil.Roz.Services;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Presentation;

public class ResponseFormatterDetailLevelTests(WorkspaceFixture fixture)
{
    private readonly NavigationService navigationService = TestFileHelper.CreateNavigationService(fixture);
    private readonly NavigationTools navigationTools = TestFileHelper.CreateNavigationTools(fixture);
    private readonly ReferenceService referenceService = TestFileHelper.CreateReferenceService(fixture);
    private readonly ReferenceTools referenceTools = TestFileHelper.CreateReferenceTools(fixture);

    [Fact]
    public async Task Format_FindSymbol_Full_MatchesDefaultBehavior()
    {
        // Arrange
        FindSymbolResult result = await navigationService.FindSymbolAsync("Circle", depth: 1, includeBody: true, ct: TestContext.Current.CancellationToken);

        // Act — explicit DetailLevel.Full vs the default level parameter. The explicit argument is
        // deliberately "redundant": the whole point is to guard that the default level stays Full,
        // so do NOT let cleanup strip it (that would restore the original tautology).
        // ReSharper disable once RedundantArgumentDefaultValue
        string atFull = NavigationResultFormatter.Format(result, true, DetailLevel.Full);
        string atDefault = NavigationResultFormatter.Format(result, true);

        // Assert — the default level IS Full, so the two must match
        atFull.ShouldBe(atDefault);
    }

    [Fact]
    public async Task Format_FindSymbol_DropBodies_OmitsBody()
    {
        // Arrange
        FindSymbolResult result = await navigationService.FindSymbolAsync("Circle", includeBody: true, ct: TestContext.Current.CancellationToken);

        // Act
        string atFull = NavigationResultFormatter.Format(result, level: DetailLevel.Full);
        string atDropBodies = NavigationResultFormatter.Format(result, level: DetailLevel.High);

        // Assert
        atFull.ShouldContain("Body:");
        atDropBodies.ShouldNotContain("Body:");
        atDropBodies.Length.ShouldBeLessThan(atFull.Length);
    }

    [Fact]
    public async Task Format_FindSymbol_DropDocs_OmitsDocs()
    {
        // Arrange — IShape carries XML docs (Circle does not), so "Documentation:" can actually
        // appear at Full and the Medium drop is observable.
        FindSymbolResult result = await navigationService.FindSymbolAsync("IShape", matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Act
        string atFull = NavigationResultFormatter.Format(result, true);
        string atDropDocs = NavigationResultFormatter.Format(result, true, DetailLevel.Medium);

        // Assert — docs present at Full, dropped at Medium
        atFull.ShouldContain("Documentation:");
        atDropDocs.ShouldNotContain("Documentation:");
        atDropDocs.Length.ShouldBeLessThan(atFull.Length);
    }

    [Fact]
    public async Task Format_FindSymbol_SignaturesOnly_ForcesDepthZero()
    {
        // Arrange — request depth=1 to get members
        FindSymbolResult result = await navigationService.FindSymbolAsync("Shape", depth: 1, ct: TestContext.Current.CancellationToken);

        // Act
        string atFull = NavigationResultFormatter.Format(result, level: DetailLevel.Full);
        string atSignatures = NavigationResultFormatter.Format(result, level: DetailLevel.Low);

        // Assert — SignaturesOnly should not show members
        atFull.ShouldContain("Members");
        atSignatures.ShouldNotContain("Members (");
        atSignatures.Length.ShouldBeLessThan(atFull.Length);
    }

    [Fact]
    public async Task Format_FindSymbol_NamesOnly_MinimalOutput()
    {
        // Arrange
        FindSymbolResult result = await navigationService.FindSymbolAsync("Shape", depth: 1, includeBody: true, ct: TestContext.Current.CancellationToken);

        // Act
        string atFull = NavigationResultFormatter.Format(result, true);
        string atNamesOnly = NavigationResultFormatter.Format(result, true, DetailLevel.Minimal);

        // Assert — NamesOnly should be drastically smaller and contain just names
        atNamesOnly.ShouldContain("Shape");
        atNamesOnly.ShouldNotContain("Members");
        atNamesOnly.ShouldNotContain("Body:");
        atNamesOnly.ShouldNotContain("Documentation:");
        atNamesOnly.Length.ShouldBeLessThan(atFull.Length);
    }

    [Fact]
    public async Task Format_FindReferences_SignaturesOnly_NoSourceSnippets()
    {
        // Arrange — name-based resolution avoids hardcoded fixture coordinates
        var result = (FindReferencesResult)await referenceService.FindReferencesAsync(null, null, null, "IShape", contextLines: 1, ct: TestContext.Current.CancellationToken);

        // Act
        string atFull = ReferenceResultFormatter.Format(result);
        string atSignatures = ReferenceResultFormatter.Format(result, DetailLevel.Low);

        // Assert — SignaturesOnly should not have source snippet lines
        atFull.ShouldContain(" | ");
        atSignatures.ShouldNotContain(" | ");
        atSignatures.ShouldContain("Line ");
        atSignatures.Length.ShouldBeLessThan(atFull.Length);
    }

    [Fact]
    public async Task Format_FindReferences_Invocations_SignaturesOnly_NoSourceSnippets()
    {
        // Arrange — name-based resolution avoids hardcoded fixture coordinates
        var result = (FindCallersResult)await referenceService.FindReferencesAsync(null, null, null, "Describe", "IShape", ReferenceKind.Invocations, contextLines: 1, ct: TestContext.Current.CancellationToken);

        // Act
        string atFull = ReferenceResultFormatter.Format(result);
        string atSignatures = ReferenceResultFormatter.Format(result, DetailLevel.Low);

        // Assert — at least one caller, so the detail levels are actually exercised; the source-snippet
        // gutter (" | ") shows at Full and is dropped at SignaturesOnly (Low), shrinking the output.
        result.Callers.Count.ShouldBeGreaterThan(0);
        atFull.ShouldContain(" | ");
        atSignatures.ShouldNotContain(" | ");
        atSignatures.Length.ShouldBeLessThan(atFull.Length);
    }

    [Fact]
    public async Task Format_FindSymbol_NoBodyRequested_DropBodiesIsNoOp()
    {
        // Arrange — no body requested, so DropBodies should produce same output as Full
        FindSymbolResult result = await navigationService.FindSymbolAsync("Circle", includeBody: false, ct: TestContext.Current.CancellationToken);

        // Act
        string atFull = NavigationResultFormatter.Format(result, level: DetailLevel.Full);
        string atDropBodies = NavigationResultFormatter.Format(result, level: DetailLevel.High);

        // Assert — same output since no bodies to drop
        atFull.ShouldBe(atDropBodies);
    }

    [Fact]
    public async Task Format_SymbolsOverview_NamesOnly_JustNames()
    {
        // Arrange
        SymbolsOverviewResult[] results = await Task.WhenAll(
            navigationService.GetSymbolsOverviewAsync(fixture.ShapesFile("Circle.cs")));

        // Act
        string atFull = ResponseFormatter.Format(results, true);
        string atNamesOnly = ResponseFormatter.Format(results, true, DetailLevel.Minimal);

        // Assert
        atNamesOnly.ShouldContain("Circle");
        atNamesOnly.ShouldNotContain("Members");
        atNamesOnly.Length.ShouldBeLessThan(atFull.Length);
    }

    // ── Minimal detail level for GoToDefinition, FindOverloads, FindImplementations ──

    [Fact]
    public async Task Format_GoToDefinition_Minimal_ReturnsNameOnly()
    {
        // Arrange
        SymbolAtPositionResult result = await navigationService.GoToDefinitionAsync(fixture.ShapesFile("Circle.cs"), 3, 14, ct: TestContext.Current.CancellationToken);

        // Act
        string formatted = ResponseFormatter.Format(result, level: DetailLevel.Minimal);

        // Assert — Minimal returns just the name line, no Location: label or members
        formatted.ShouldContain("Circle");
        formatted.ShouldNotContain("Location:");
        formatted.ShouldNotContain("Members");
    }

    [Fact]
    public async Task Format_FindOverloads_Minimal_ReturnsNamesOnly()
    {
        // Arrange
        FindOverloadsResult result = await navigationService.FindOverloadsAsync(null, null, null, "Format", "ShapeService", ct: TestContext.Current.CancellationToken);

        // Act
        string formatted = NavigationResultFormatter.Format(result, level: DetailLevel.Minimal);

        // Assert
        formatted.ShouldContain("Format");
        formatted.ShouldNotContain("Location:");
    }

    [Fact]
    public async Task Format_FindOverloads_Batch_Minimal_SectionsSeparatedBySingleBlankLine()
    {
        // Arrange — two-item batch so FormatBatchWithErrors emits === sections.
        // Minimal level exercises the early-return branch of Format(FindOverloadsResult).
        FindOverloadsResult calc = await navigationService.FindOverloadsAsync(null, null, null, "Calculate", "MultiTfmService", ct: TestContext.Current.CancellationToken);
        FindOverloadsResult get = await navigationService.FindOverloadsAsync(null, null, null, "GetValue", "MultiTfmService", ct: TestContext.Current.CancellationToken);
        IReadOnlyList<BatchItem<FindOverloadsResult>> batch =
        [
            new BatchItemSuccess<FindOverloadsResult>("Calculate", calc),
            new BatchItemSuccess<FindOverloadsResult>("GetValue", get)
        ];

        // Act
        string normalized = NavigationResultFormatter.Format(batch, level: DetailLevel.Minimal).Replace("\r\n", "\n");

        // Assert — exactly one blank line between === sections, not two
        normalized.ShouldContain("=== Calculate ===");
        normalized.ShouldContain("=== GetValue ===");
        normalized.ShouldNotContain("\n\n\n===");
    }

    [Fact]
    public async Task Format_FindImplementations_Minimal_ReturnsNamesOnly()
    {
        // Arrange
        FindImplementationsResult result = await referenceService.FindImplementationsAsync(null, null, null, "Area", "IShape", ct: TestContext.Current.CancellationToken);

        // Act
        string formatted = ReferenceResultFormatter.Format(result, level: DetailLevel.Minimal);

        // Assert
        formatted.ShouldContain("Area");
        formatted.ShouldNotContain("Location:");
    }

    [Fact]
    public async Task Format_FindImplementations_OnType_Minimal_ReturnsNamesOnly()
    {
        // Arrange — resolving a type dispatches to derived-types logic inside FindImplementationsAsync
        FindImplementationsResult result = await referenceService.FindImplementationsAsync(null, null, null, "Shape", ct: TestContext.Current.CancellationToken);

        // Act
        string formatted = ReferenceResultFormatter.Format(result, level: DetailLevel.Minimal);

        // Assert
        formatted.ShouldContain("Circle");
        formatted.ShouldNotContain("Location:");
    }

    // ── Empty result paths ──

    [Fact]
    public void Format_FindOverloads_Empty_ReturnsNoOverloadsMessage()
    {
        // Arrange — construct an empty result directly
        var result = new FindOverloadsResult("Foo", "Bar", [], "/dir");

        // Act
        string formatted = NavigationResultFormatter.Format(result);

        // Assert
        formatted.ShouldContain("No overloads found for 'Foo'");
    }

    [Fact]
    public void Format_FindImplementations_Empty_ReturnsNoImplementationsMessage()
    {
        // Arrange
        var result = new FindImplementationsResult("Foo", SymbolQualifiers.Bare("Foo"), [], "/dir", 0);

        // Act
        string formatted = ReferenceResultFormatter.Format(result);

        // Assert
        formatted.ShouldContain("No implementations found for 'Foo'");
    }

    [Fact]
    public async Task Format_FindImplementations_OnLeafClass_ReturnsNoDerivedMessage()
    {
        // Act — Pentagon has no derived classes; type dispatch uses find_implementations now
        string result = await referenceTools.FindImplementations(symbolNames: ["Pentagon"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No derived classes");
    }

    [Fact]
    public async Task Format_SymbolsOverview_FileWithNoTypes_ReturnsNoDeclarationsMessage()
    {
        // Act — GlobalUsings.cs has only using directives, no type declarations
        string result = await navigationTools.GetSymbolsOverview([TestFileHelper.GlobalUsingsFile(fixture)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No type declarations found");
    }
}
