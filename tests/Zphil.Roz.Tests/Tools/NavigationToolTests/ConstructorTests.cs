using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

public class ConstructorTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = TestFileHelper.CreateNavigationTools(fixture);

    // ── get_symbols_overview ────────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolsOverview_ShowsExplicitConstructors()
    {
        // Act
        string result = await tools.GetSymbolsOverview(["TestFixture/Services/ShapeCalculator.cs"], ct: TestContext.Current.CancellationToken);

        // Assert — both constructors should appear
        result.ShouldContain("[public constructor]");
        result.ShouldContain("ShapeCalculator(IShape shape)");
        result.ShouldContain("ShapeCalculator(double radius)");
    }

    [Fact]
    public async Task GetSymbolsOverview_ExcludesPrimaryConstructors()
    {
        // Act — Circle uses a primary constructor (IsImplicitlyDeclared)
        string result = await tools.GetSymbolsOverview(["TestFixture/Shapes/Circle.cs"], ct: TestContext.Current.CancellationToken);

        // Assert — no constructor should appear
        result.ShouldNotContain("constructor");
    }

    // ── find_symbol with kind: "constructor" ────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithKindConstructor_FindsExplicitConstructors()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeCalculator"], SymbolicKind.Constructor, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("constructor");
        result.ShouldContain("ShapeCalculator(IShape shape)");
        result.ShouldContain("ShapeCalculator(double radius)");
    }

    [Fact]
    public async Task FindSymbol_WithKindConstructor_ExcludesPrimaryConstructors()
    {
        // Act — Circle only has a primary constructor
        string result = await tools.FindSymbol(["Circle"], SymbolicKind.Constructor, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No symbols found");
    }

    // ── find_symbol with depth ──────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithDepth_ShowsConstructorsInMemberList()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeCalculator"], SymbolicKind.Class, 1, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert — constructors should appear in members section
        result.ShouldContain("[public constructor]");
        result.ShouldContain("ShapeCalculator(IShape shape)");
    }

    // ── Static constructors ─────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithKindConstructor_IncludesStaticConstructors()
    {
        // Act — ShapeCalculator has a static constructor
        string result = await tools.FindSymbol(["ShapeCalculator"], SymbolicKind.Constructor, ct: TestContext.Current.CancellationToken);

        // Assert — static constructor should be found alongside instance constructors
        result.ShouldContain("static constructor");
    }

    [Fact]
    public async Task GetSymbolsOverview_ShowsStaticConstructors()
    {
        // Act
        string result = await tools.GetSymbolsOverview(["TestFixture/Services/ShapeCalculator.cs"], ct: TestContext.Current.CancellationToken);

        // Assert — static constructors display with "static constructor" kind;
        // Roslyn reports static constructors as private (their effective accessibility)
        result.ShouldContain("[private static constructor]");
        result.ShouldContain("ShapeCalculator()");
    }

    // ── find_symbol with .ctor / .cctor names ─────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithDotCtor_FindsInstanceConstructors()
    {
        // Act
        string result = await tools.FindSymbol([".ctor"], containingType: "ShapeCalculator", ct: TestContext.Current.CancellationToken);

        // Assert — ShapeCalculator has two explicit instance constructors
        result.ShouldContain("ShapeCalculator(IShape shape)");
        result.ShouldContain("ShapeCalculator(double radius)");
        result.ShouldNotContain("static constructor");
    }

    [Fact]
    public async Task FindSymbol_WithDotCctor_FindsStaticConstructor()
    {
        // Act
        string result = await tools.FindSymbol([".cctor"], containingType: "ShapeCalculator", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("static constructor");
        result.ShouldContain("ShapeCalculator()");
        result.ShouldNotContain("ShapeCalculator(IShape shape)");
    }

    [Fact]
    public async Task FindSymbol_WithDotCtor_WithoutContainingType_FindsConstructors()
    {
        // .ctor without containingType walks all source types and returns constructors
        string result = await tools.FindSymbol([".ctor"], ct: TestContext.Current.CancellationToken);

        // Assert — should find constructors across the solution
        result.ShouldContain("constructor");
        result.ShouldNotContain("No symbols found");
    }

    // ── Implicit constructors ─────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithDotCtor_ImplicitConstructor_Finds()
    {
        // ShapeCollection has no explicit constructor — relies on compiler-generated default
        string result = await tools.FindSymbol([".ctor"], containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert — should find the implicit default constructor
        result.ShouldContain("ShapeCollection()");
        result.ShouldNotContain("No symbols found");
    }

    [Fact]
    public async Task FindSymbol_WithKindConstructor_DoesNotFindImplicitConstructors()
    {
        // kind=Constructor uses GetMembers() which correctly excludes implicit constructors.
        // Only .ctor name-based lookup should find them.
        string result = await tools.FindSymbol(["ShapeCollection"], SymbolicKind.Constructor, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("No symbols found");
    }

    // ── Display formatting ──────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_Constructor_FormatsCorrectly()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeCalculator"], SymbolicKind.Constructor, ct: TestContext.Current.CancellationToken);

        // Assert — no "void" return type, labeled as "constructor" not "method"
        result.ShouldNotContain("void");
        result.ShouldNotContain("public method");
        result.ShouldContain("public constructor");
    }
}
