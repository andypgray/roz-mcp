using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

public class ExplicitInterfaceImplementationTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = TestFileHelper.CreateNavigationTools(fixture);

    // ── GetSymbolsOverview ──────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolsOverview_ShowsExplicitInterfaceMethod()
    {
        // Arrange
        string filePath = fixture.ServicesFile("ExplicitImplementations.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert — explicit method implementations should be visible (fully qualified names)
        result.ShouldContain("IResettable.Reset");
        result.ShouldContain("IDisposable.Dispose");
    }

    [Fact]
    public async Task GetSymbolsOverview_ShowsExplicitInterfaceProperty()
    {
        // Arrange
        string filePath = fixture.ServicesFile("ExplicitImplementations.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert — explicit property implementation should be visible
        result.ShouldContain("IResettable.ResetCount");
    }

    [Fact]
    public async Task GetSymbolsOverview_ShowsExplicitInterfaceEvent()
    {
        // Arrange
        string filePath = fixture.ServicesFile("ExplicitImplementations.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert — explicit event implementation should be visible
        result.ShouldContain("IResettable.Resetting");
    }

    // ── FindSymbol with depth ───────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithDepth_ShowsExplicitImplementationsInMemberList()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeManager"], SymbolicKind.Class, 1, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert — all explicit interface implementations should appear in the member list
        // Roslyn uses fully qualified interface names for explicit implementations
        result.ShouldContain("IResettable.Reset");
        result.ShouldContain("IResettable.ResetCount");
        result.ShouldContain("IResettable.Resetting");
        result.ShouldContain("IDisposable.Dispose");

        // Regular members should also be present
        result.ShouldContain("IsDisposed");
    }

    [Fact]
    public async Task FindSymbol_WithDepth_ExplicitMethodsLabeledAsMethod()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeManager"], SymbolicKind.Class, 1, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert — explicit interface methods are private and labeled as "method"
        result.ShouldContain("[private method] void");
        result.ShouldContain("IResettable.Reset()");
        result.ShouldContain("IDisposable.Dispose()");
    }

    // ── FindSymbol with includeBody ─────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithBody_ContainsExplicitImplementationSource()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeManager"], SymbolicKind.Class, matchMode: SymbolMatchMode.Exact, includeBody: true, ct: TestContext.Current.CancellationToken);

        // Assert — body should include explicit implementation source
        result.ShouldContain("IResettable.Reset");
        result.ShouldContain("_resetCount++");
    }
}
