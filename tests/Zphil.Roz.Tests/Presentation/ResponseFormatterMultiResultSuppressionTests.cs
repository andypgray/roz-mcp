using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.Presentation;
using Zphil.Roz.Services;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Presentation;

public class ResponseFormatterMultiResultSuppressionTests(WorkspaceFixture fixture)
{
    private readonly NavigationService navigationService = TestFileHelper.CreateNavigationService(fixture);

    [Fact]
    public async Task Format_FindSymbol_MultipleResults_WithDepth_ShowsMembers()
    {
        // Arrange — "Shape" matches many types; suppression hack is removed,
        // so members should now be shown for all results.
        FindSymbolResult result = await navigationService.FindSymbolAsync("Shape", depth: 1, ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert — multiple results should now show member listings (no suppression)
        result.Symbols.Count.ShouldBeGreaterThan(1);
        output.ShouldNotContain("member listings suppressed");
        output.ShouldContain("  Members (");
    }

    [Fact]
    public async Task Format_FindSymbol_SingleResult_WithDepth_ShowsMembers()
    {
        // Arrange — exact match returns single result
        FindSymbolResult result = await navigationService.FindSymbolAsync("Circle", depth: 1, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert — single result should show members
        result.Symbols.Count.ShouldBe(1);
        output.ShouldContain("  Members (");
        output.ShouldNotContain("member listings suppressed");
    }

    [Fact]
    public async Task Format_FindSymbol_MultipleResults_DepthZero_NoMembers()
    {
        // Arrange — depth=0 should not show member listings
        FindSymbolResult result = await navigationService.FindSymbolAsync("Shape", depth: 0, ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert — no member listings since depth=0
        result.Symbols.Count.ShouldBeGreaterThan(1);
        output.ShouldNotContain("  Members (");
        output.ShouldNotContain("member listings suppressed");
    }
}
