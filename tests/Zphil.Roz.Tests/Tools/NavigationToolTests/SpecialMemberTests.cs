using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

public class SpecialMemberTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = TestFileHelper.CreateNavigationTools(fixture);

    // ── FindSymbol with depth (member listing) ──────────────────────────────

    [Fact]
    public async Task FindSymbol_WithDepth_ShowsIndexerInMemberList()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeCollection"], SymbolicKind.Class, 1, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("[public indexer]");
        result.ShouldContain("this[int index]");
    }

    [Fact]
    public async Task FindSymbol_WithDepth_ShowsOperatorInMemberList()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeCollection"], SymbolicKind.Class, 1, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("[public static operator]");
        result.ShouldContain("[public static]");
        result.ShouldContain("operator +");
    }

    [Fact]
    public async Task FindSymbol_WithDepth_ShowsConversionOperatorInMemberList()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeCollection"], SymbolicKind.Class, 1, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("implicit operator int");
    }

    [Fact]
    public async Task FindSymbol_WithDepth_ShowsDestructorInMemberList()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeCollection"], SymbolicKind.Class, 1, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert — destructors have no access modifier (Accessibility.NotApplicable)
        result.ShouldContain("destructor]");
        result.ShouldContain("~ShapeCollection()");
    }

    // ── GetSymbolsOverview ──────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolsOverview_ShapeCollection_ShowsIndexer()
    {
        // Arrange
        string filePath = fixture.ServicesFile("ShapeCollection.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("indexer");
        result.ShouldContain("this[int index]");
    }

    [Fact]
    public async Task GetSymbolsOverview_ShapeCollection_ShowsOperators()
    {
        // Arrange
        string filePath = fixture.ServicesFile("ShapeCollection.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("operator +");
        result.ShouldContain("implicit operator int");
    }

    [Fact]
    public async Task GetSymbolsOverview_ShapeCollection_ShowsDestructor()
    {
        // Arrange
        string filePath = fixture.ServicesFile("ShapeCollection.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("destructor");
        result.ShouldContain("~ShapeCollection");
    }

    // ── FindSymbol with kind filter (member extraction pivot) ─────────────

    [Fact]
    public async Task FindSymbol_WithKindOperator_FindsOperatorsInType()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeCollection"], SymbolicKind.Operator, ct: TestContext.Current.CancellationToken);

        // Assert — should find the + operator and the implicit conversion
        result.ShouldContain("operator +");
        result.ShouldContain("implicit operator int");
    }

    [Fact]
    public async Task FindSymbol_WithKindDestructor_FindsDestructorInType()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeCollection"], SymbolicKind.Destructor, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("~ShapeCollection()");
    }

    [Fact]
    public async Task FindSymbol_WithNameFinalizeAndKindDestructor_FindsDestructor()
    {
        // Act — "Finalize" is the Roslyn metadata name for destructors
        string result = await tools.FindSymbol(["Finalize"], SymbolicKind.Destructor, containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("~ShapeCollection()");
    }

    [Fact]
    public async Task FindSymbol_WithKindIndexer_FindsIndexerInType()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeCollection"], SymbolicKind.Indexer, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("this[int index]");
    }

    // ── FindSymbol by indexer name (this[] / this) ─────────────────────────

    [Fact]
    public async Task FindSymbol_ThisBrackets_FindsIndexer()
    {
        // Act — "this[]" is the user-facing name for indexers
        string result = await tools.FindSymbol(["this[]"], containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("this[int index]");
    }

    [Fact]
    public async Task FindSymbol_This_FindsIndexer()
    {
        // Act — "this" without brackets should also work
        string result = await tools.FindSymbol(["this"], containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("this[int index]");
    }

    [Fact]
    public async Task FindSymbol_ThisBrackets_WithKindIndexer_FindsIndexer()
    {
        // Act — explicit kind filter combined with indexer name
        string result = await tools.FindSymbol(["this[]"], SymbolicKind.Indexer, containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("this[int index]");
    }

    // ── FindSymbol by operator metadata name (op_*) ───────────────────────

    [Fact]
    public async Task FindSymbol_ByOpAdditionName_FindsOperator()
    {
        // Act
        string result = await tools.FindSymbol(["op_Addition"], containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("operator +");
    }

    [Fact]
    public async Task FindSymbol_ByOpImplicitName_FindsConversionOperator()
    {
        // Act
        string result = await tools.FindSymbol(["op_Implicit"], containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("implicit operator int");
    }

    // ── FindSymbol by special member name WITHOUT containingType ──────────

    [Theory]
    [InlineData("op_Addition", "operator +")]
    [InlineData("op_Implicit", "implicit operator int")]
    [InlineData("this[]", "this[int index]")]
    [InlineData("Finalize", "~ShapeCollection()")]
    public async Task FindSymbol_SpecialMemberName_WithoutContainingType_ResolvesGlobally(
        string symbolName, string expected)
    {
        string result = await tools.FindSymbol([symbolName], ct: TestContext.Current.CancellationToken);
        result.ShouldContain(expected);
    }

    [Fact]
    public async Task FindSymbol_OpAddition_WithKindOperator_WithoutContainingType_FindsOperator()
    {
        string result = await tools.FindSymbol(["op_Addition"], SymbolicKind.Operator, ct: TestContext.Current.CancellationToken);
        result.ShouldContain("operator +");
    }

    [Fact]
    public async Task FindSymbol_Finalize_WithKindDestructor_WithoutContainingType_FindsDestructor()
    {
        string result = await tools.FindSymbol(["Finalize"], SymbolicKind.Destructor, ct: TestContext.Current.CancellationToken);
        result.ShouldContain("~ShapeCollection()");
    }

    [Fact]
    public async Task FindSymbol_ThisBrackets_WithKindIndexer_WithoutContainingType_FindsIndexer()
    {
        string result = await tools.FindSymbol(["this[]"], SymbolicKind.Indexer, ct: TestContext.Current.CancellationToken);
        result.ShouldContain("this[int index]");
    }

    // ── FindSymbol with includeBody ────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_ShapeCollection_WithBody_ContainsIndexerDeclaration()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeCollection"], SymbolicKind.Class, matchMode: SymbolMatchMode.Exact, includeBody: true, ct: TestContext.Current.CancellationToken);

        // Assert — body should include the indexer source
        result.ShouldContain("this[int index]");
    }

    [Fact]
    public async Task FindSymbol_ShapeCollection_WithBody_ContainsOperatorDeclaration()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeCollection"], SymbolicKind.Class, matchMode: SymbolMatchMode.Exact, includeBody: true, ct: TestContext.Current.CancellationToken);

        // Assert — body should include the operator source
        result.ShouldContain("operator +");
    }

    [Fact]
    public async Task FindSymbol_ShapeCollection_WithBody_ContainsDestructorDeclaration()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeCollection"], SymbolicKind.Class, matchMode: SymbolMatchMode.Exact, includeBody: true, ct: TestContext.Current.CancellationToken);

        // Assert — body should include the destructor source
        result.ShouldContain("~ShapeCollection()");
    }

    // ── ref struct display ───────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_RefStruct_ShowsRefModifier()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeSpan"], SymbolicKind.Struct, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert — "ref struct" not plain "struct"
        result.ShouldContain("ref struct ShapeSpan");
    }

    // ── ref/out/in parameter modifiers ───────────────────────────────────────

    [Fact]
    public async Task FindSymbol_MethodWithRefOutInParams_ShowsModifiers()
    {
        // Act
        string result = await tools.FindSymbol(["SplitCount"], containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert — all three ref kind modifiers should appear
        result.ShouldContain("in ShapeCollection source");
        result.ShouldContain("ref int even");
        result.ShouldContain("out int odd");
    }

    // ── Operator tag does NOT include redundant "operator" keyword ───────────

    [Fact]
    public async Task FindSymbol_OpAddition_TagDoesNotDuplicateOperatorKeyword()
    {
        // Act
        string result = await tools.FindSymbol(["op_Addition"], containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert — "operator" should appear exactly once (in the signature, not in the tag)
        result.ShouldNotContain("operator operator");
        result.ShouldNotContain("operator ShapeCollection operator");
        result.ShouldContain("operator +");
    }

    [Fact]
    public async Task FindSymbol_OpImplicit_TagDoesNotDuplicateOperatorKeyword()
    {
        // Act
        string result = await tools.FindSymbol(["op_Implicit"], containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert — "operator" should appear exactly once
        result.ShouldNotContain("operator implicit operator");
        result.ShouldContain("implicit operator");
    }
}
