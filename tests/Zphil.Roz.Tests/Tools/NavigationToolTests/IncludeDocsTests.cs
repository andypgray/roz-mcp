using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

/// <summary>
///     Integration tests for the <c>includeDocs</c> parameter across navigation tools.
/// </summary>
public class IncludeDocsTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools navTools = CreateNavigationTools(fixture);
    private readonly ReferenceTools refTools = CreateReferenceTools(fixture);
    private readonly TypeHierarchyTools typeHierarchyTools = CreateTypeTools(fixture);

    // ── find_symbol ──────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithIncludeDocs_ShowsDocumentation()
    {
        // Act
        string result = await navTools.FindSymbol(["IShape"], includeDocs: true, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Documentation:");
        result.ShouldContain("Represents a geometric shape with calculable area and perimeter.");
    }

    [Fact]
    public async Task FindSymbol_WithoutIncludeDocs_OmitsDocumentation()
    {
        // Act
        string result = await navTools.FindSymbol(["IShape"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("Documentation:");
        result.ShouldNotContain("Represents a geometric shape");
    }

    [Fact]
    public async Task FindSymbol_WithIncludeDocs_UndocumentedSymbol_OmitsDocSection()
    {
        // Act — DescribeFirst in ShapeService has no XML docs
        string result = await navTools.FindSymbol(["DescribeFirst"], containingType: "ShapeService", includeDocs: true, ct: TestContext.Current.CancellationToken);

        // Assert — found, but with no docs to render the Documentation section is omitted
        result.ShouldContain("DescribeFirst");
        result.ShouldNotContain("Documentation:");
    }

    [Fact]
    public async Task FindSymbol_WithIncludeDocsAndDepth_MembersHaveInlineSummaries()
    {
        // Act
        string result = await navTools.FindSymbol(["IShape"], depth: 1, includeDocs: true, ct: TestContext.Current.CancellationToken);

        // Assert — member summaries appear inline
        result.ShouldContain("double Area  :9 \u2014 Gets the area of the shape in square units.");
        result.ShouldContain("double Perimeter  :12 \u2014 Gets the perimeter of the shape in linear units.");
        result.ShouldContain("string Describe()  :18 \u2014 Returns a human-readable description of the shape.");
    }

    [Fact]
    public async Task FindSymbol_WithDepthButNoDocs_MembersHaveNoSummaries()
    {
        // Act
        string result = await navTools.FindSymbol(["IShape"], depth: 1, includeDocs: false, ct: TestContext.Current.CancellationToken);

        // Assert — member summaries not shown
        result.ShouldNotContain("\u2014 Gets the area");
        result.ShouldNotContain("\u2014 Gets the perimeter");
    }

    [Fact]
    public async Task FindSymbol_WithIncludeDocs_ShowsParamsAndReturns()
    {
        // Act
        string result = await navTools.FindSymbol(["ProcessShape"], includeDocs: true, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Documentation:");
        result.ShouldContain("Parameters:");
        result.ShouldContain("shape \u2014 The shape to process.");
        result.ShouldContain("Returns: A formatted string with the shape's description.");
        result.ShouldContain("Exceptions:");
        result.ShouldContain("ArgumentNullException");
    }

    // ── inheritdoc resolution ────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithIncludeDocs_InheritDoc_ResolvesFromInterface()
    {
        // Shape.Describe has <inheritdoc/> — should resolve to IShape.Describe's docs
        // Act
        string result = await navTools.FindSymbol(["Describe"], containingType: "Shape", includeDocs: true, ct: TestContext.Current.CancellationToken);

        // Assert — resolved from IShape
        result.ShouldContain("Documentation:");
        result.ShouldContain("Returns a human-readable description of the shape.");
    }

    [Fact]
    public async Task FindSymbol_WithIncludeDocs_InheritDocOnType_ResolvesFromInterface()
    {
        // Shape has <inheritdoc/> on the class itself — should resolve from IShape
        // Act
        string result = await navTools.FindSymbol(["Shape"], SymbolicKind.Class, matchMode: SymbolMatchMode.Exact, includeDocs: true, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Documentation:");
        result.ShouldContain("Represents a geometric shape with calculable area and perimeter.");
    }

    // ── go_to_definition ─────────────────────────────────────────────────

    [Fact]
    public async Task GoToDefinition_WithIncludeDocs_ShowsDocumentation()
    {
        // Arrange — `IShape` (a documented source type) is referenced as ProcessShape's
        // parameter type; navigating to its declaration with includeDocs must surface the
        // interface's XML <summary>.
        string serviceFile = fixture.ServicesFile("ShapeService.cs");

        // Act — cursor on the `IShape` reference in `public string ProcessShape(IShape shape)`
        string result = await navTools.GoToDefinition(Loc(serviceFile, 16, 34), includeDocs: true, ct: TestContext.Current.CancellationToken);

        // Assert — go_to_definition threads includeDocs and renders the target's documentation
        result.ShouldContain("Documentation:");
        result.ShouldContain("Represents a geometric shape");
    }

    // ── go_to_definition — metadata auto-docs ──────────────────────────

    [Fact]
    public async Task GoToDefinition_MetadataSymbol_AutoIncludesDocs()
    {
        // Arrange — ShapeService.cs line 24: `public IShape GetLargest(IEnumerable<IShape> shapes)`
        // cursor on IEnumerable — a metadata-only type from System.Runtime
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act — includeDocs defaults to false, but metadata symbols should auto-enable docs
        string result = await navTools.GoToDefinition(Loc(filePath, 24, 30), ct: TestContext.Current.CancellationToken);

        // Assert — docs should appear automatically for metadata-only symbols
        result.ShouldContain("Assembly:");
        result.ShouldContain("Documentation:");
    }

    [Fact]
    public async Task GoToDefinition_SourceSymbol_DefaultDocsOff()
    {
        // Arrange — ShapeService.cs line 24: cursor on IShape (a source symbol)
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act — includeDocs defaults to false; source symbols should NOT auto-enable docs
        string result = await navTools.GoToDefinition(Loc(filePath, 24, 18), ct: TestContext.Current.CancellationToken);

        // Assert — no docs for source symbols unless explicitly requested
        result.ShouldNotContain("Documentation:");
    }

    // ── get_symbols_overview ─────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolsOverview_WithIncludeDocs_ShowsDocumentation()
    {
        // Act
        string shapesFile = fixture.ShapesFile("IShape.cs");
        string result = await navTools.GetSymbolsOverview([shapesFile], includeDocs: true, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Documentation:");
        result.ShouldContain("Represents a geometric shape");
    }

    [Fact]
    public async Task GetSymbolsOverview_WithoutIncludeDocs_OmitsDocumentation()
    {
        // Act
        string shapesFile = fixture.ShapesFile("IShape.cs");
        string result = await navTools.GetSymbolsOverview([shapesFile], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("Documentation:");
    }

    // ── get_type_hierarchy ───────────────────────────────────────────────

    [Fact]
    public async Task GetTypeHierarchy_WithIncludeDocs_ShowsDocumentation()
    {
        // Act — IShape at line 6
        string shapeFile = fixture.ShapesFile("IShape.cs");
        string result = await typeHierarchyTools.GetTypeHierarchy([Loc(shapeFile, 6, 18)], includeDocs: true, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Documentation:");
        result.ShouldContain("Represents a geometric shape");
    }

    [Fact]
    public async Task GetTypeHierarchy_MetadataType_ByFqn_AutoIncludesDocs()
    {
        // Act — BCL type resolved by FQN; docs auto-enable without includeDocs=true
        string result = await typeHierarchyTools.GetTypeHierarchy(symbolNames: ["System.IDisposable"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Documentation:");
    }

    [Fact]
    public async Task GetTypeHierarchy_SourceType_DefaultDocsOff()
    {
        // Act — source type; docs should NOT auto-enable
        string shapeFile = fixture.ShapesFile("IShape.cs");
        string result = await typeHierarchyTools.GetTypeHierarchy([Loc(shapeFile, 6, 18)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("Documentation:");
    }

    // ── find_implementations on an interface member ──────────────────────

    [Fact]
    public async Task FindImplementations_OnMember_WithIncludeDocs_ShowsImplementations()
    {
        // Act — IShape.Describe() member at line 18 (not the interface type itself)
        string shapeFile = fixture.ShapesFile("IShape.cs");
        string result = await refTools.FindImplementations([Loc(shapeFile, 18, 12)], includeDocs: true, ct: TestContext.Current.CancellationToken);

        // Assert — member-level implementation dispatch
        result.ShouldContain("Implementations of 'IShape.Describe'");
    }

    // ── find_implementations on a type (derived-types dispatch) ──────────────

    [Fact]
    public async Task FindImplementations_OnType_WithIncludeDocs_ShowsImplementingTypes()
    {
        // Act — IShape type at line 6; resolving a type dispatches to derived-types logic
        string shapeFile = fixture.ShapesFile("IShape.cs");
        string result = await refTools.FindImplementations([Loc(shapeFile, 6, 18)], includeDocs: true, ct: TestContext.Current.CancellationToken);

        // Assert — type dispatch header
        result.ShouldContain("Types implementing 'IShape'");
    }

    // ── find_overloads — metadata auto-docs ──────────────────────────────

    [Fact]
    public async Task FindOverloads_MetadataMethod_AutoIncludesDocs()
    {
        // Act — ShapeService.cs line 25 `shapes.MaxBy(s => s.Area)!;` — cursor on `MaxBy`,
        // a BCL extension method in System.Linq with 2 metadata-only overloads.
        string servicesFile = fixture.ServicesFile("ShapeService.cs");
        string result = await navTools.FindOverloads([Loc(servicesFile, 25, 17)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Documentation:");
    }
}
