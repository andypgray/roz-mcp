using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.Presentation;
using Zphil.Roz.Services;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Presentation;

public class CompactMemberListingTests(WorkspaceFixture fixture)
{
    private readonly NavigationService navigationService = TestFileHelper.CreateNavigationService(fixture);

    // ── Compact signatures ────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_Constructor_WithManyParams_ShowsCompactSignature()
    {
        // Arrange — ManyParamsService has a 4-param explicit constructor
        FindSymbolResult result = await navigationService.FindSymbolAsync("ManyParamsService", depth: 1, matchMode: SymbolMatchMode.Exact, memberKinds: [SymbolicKind.Constructor], ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert — constructor with 4 params should show compact form
        output.ShouldContain("ManyParamsService(IShape, ShapeCalculator, ShapeService, string)");
    }

    [Fact]
    public async Task FindSymbol_ManyParamsService_MethodsWithFewParams_ShowFullSignature()
    {
        // Arrange
        FindSymbolResult result = await navigationService.FindSymbolAsync("ManyParamsService", depth: 1, matchMode: SymbolMatchMode.Exact, memberKinds: [SymbolicKind.Method], ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert — methods with ≤3 params show full signatures
        output.ShouldContain("string Simple()");
        output.ShouldContain("string OneParam(string name)");
        output.ShouldContain("string TwoParams(string name, int count)");
        output.ShouldContain("string ThreeParams(string name, int count, bool flag)");
    }

    [Fact]
    public async Task FindSymbol_ManyParamsService_MethodsWithManyParams_ShowCompactSignature()
    {
        // Arrange
        FindSymbolResult result = await navigationService.FindSymbolAsync("ManyParamsService", depth: 1, matchMode: SymbolMatchMode.Exact, memberKinds: [SymbolicKind.Method], ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert — methods with >3 params show compact form with types only (no param names)
        output.ShouldContain("string FourParams(string, int, bool, double)");
        output.ShouldContain("string FiveParams(string, int, bool, double, IShape)");
    }

    [Fact]
    public async Task FindSymbol_ManyParamsService_PropertiesUnaffected()
    {
        // Arrange — properties should always show full signatures
        FindSymbolResult result = await navigationService.FindSymbolAsync("ManyParamsService", depth: 1, matchMode: SymbolMatchMode.Exact, memberKinds: [SymbolicKind.Property], ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert
        output.ShouldContain("double Area");
    }

    // ── maxMembers cap ────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_MaxMembers_CapsListingWithMessage()
    {
        // Arrange — ManyParamsService has many members; cap to 3
        FindSymbolResult result = await navigationService.FindSymbolAsync("ManyParamsService", depth: 1, matchMode: SymbolMatchMode.Exact, maxMembers: 3, ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert — should show exactly 3 members and a truncation message
        output.ShouldContain("  Members (");
        output.ShouldContain("... and ");
        output.ShouldContain("filter with memberKinds or increase maxMembers)");
    }

    [Fact]
    public async Task FindSymbol_MaxMembers_Null_ShowsAllMembers()
    {
        // Arrange — no maxMembers cap
        FindSymbolResult result = await navigationService.FindSymbolAsync("ManyParamsService", depth: 1, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert — all members shown, no truncation message
        output.ShouldContain("  Members (");
        output.ShouldNotContain("... and ");
    }

    [Fact]
    public async Task FindSymbol_MaxMembers_LargerThanCount_ShowsAllMembers()
    {
        // Arrange — cap larger than actual member count
        FindSymbolResult result = await navigationService.FindSymbolAsync("ManyParamsService", depth: 1, matchMode: SymbolMatchMode.Exact, maxMembers: 100, ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert — all members shown, no truncation message
        output.ShouldContain("  Members (");
        output.ShouldNotContain("... and ");
    }

    // ── get_symbols_overview ──────────────────────────────────────────

    [Fact]
    public async Task GetSymbolsOverview_MaxMembers_CapsListing()
    {
        // Arrange
        SymbolsOverviewResult result = await navigationService.GetSymbolsOverviewAsync("TestFixture/Services/ManyParamsService.cs", maxMembers: 2, ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert
        output.ShouldContain("... and ");
        output.ShouldContain("filter with memberKinds or increase maxMembers)");
    }

    [Fact]
    public async Task FindSymbol_MaxMembers_TruncationShowsKindBreakdown()
    {
        // Arrange — ManyParamsService has 1 ctor + 7 methods + 1 property = 9 members; cap to 2
        FindSymbolResult result = await navigationService.FindSymbolAsync("ManyParamsService", depth: 1, matchMode: SymbolMatchMode.Exact, maxMembers: 2, ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert — truncation message includes kind breakdown
        output.ShouldContain("... and 7 more (");
        output.ShouldContain("methods");
    }

    [Fact]
    public async Task GetSymbolsOverview_CompactSignatures_Applied()
    {
        // Arrange
        SymbolsOverviewResult result = await navigationService.GetSymbolsOverviewAsync("TestFixture/Services/ManyParamsService.cs", ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert — compact signatures in overview too
        output.ShouldContain("FourParams(string, int, bool, double)");
        output.ShouldContain("string Simple()");
    }
}
