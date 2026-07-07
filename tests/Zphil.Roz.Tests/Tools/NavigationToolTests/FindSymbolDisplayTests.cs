using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

public class FindSymbolDisplayTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = TestFileHelper.CreateNavigationTools(fixture);

    // ── includeBody ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithIncludeBody_ShowsSymbolBody()
    {
        // Act
        string result = await tools.FindSymbol(["Circle"], SymbolicKind.Class, matchMode: SymbolMatchMode.Exact, includeBody: true, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Body:");
        result.ShouldContain("radius");
    }

    [Fact]
    public async Task FindSymbol_WithoutIncludeBody_DoesNotShowBody()
    {
        // Act
        string result = await tools.FindSymbol(["Circle"], SymbolicKind.Class, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("Body:");
    }

    [Fact]
    public async Task FindSymbol_WithIncludeBodyAndDepth_SuppressesMembersForTypes()
    {
        // Act — depth=1 would normally show members, but includeBody suppresses them for types
        string result = await tools.FindSymbol(["Circle"], SymbolicKind.Class, 1, matchMode: SymbolMatchMode.Exact, includeBody: true, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Body:");
        result.ShouldNotContain("Members (");
    }

    // ── maxBodyLines ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithMaxBodyLines_TruncatesBody()
    {
        // Act
        string result = await tools.FindSymbol(["Circle"], SymbolicKind.Class, matchMode: SymbolMatchMode.Exact, includeBody: true, maxBodyLines: 2, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Body:");
        result.ShouldContain("body truncated at 2 lines");
    }

    // ── maxResults ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithMaxResults_LimitsOutput()
    {
        // Act — "Shape" matches IShape, Shape, ShapeService, etc. — limit to 2
        string result = await tools.FindSymbol(["Shape"], maxResults: 2, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("showing 2 of");
        result.ShouldContain("increase maxResults");
    }

    [Fact]
    public async Task FindSymbol_WithMaxResults_GreaterThanTotal_ReturnsAll()
    {
        // Act — maxResults larger than actual matches
        string result = await tools.FindSymbol(["Shape"], maxResults: 100, ct: TestContext.Current.CancellationToken);

        // Assert — no truncation notice
        result.ShouldNotContain("showing");
    }

    // ── includeTests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_DefaultExcludesTests_ExcludesTestProjectSymbols()
    {
        // Act — ShapeTests is defined in the test project
        string result = await tools.FindSymbol(["ShapeTests"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No symbols found");
    }

    [Fact]
    public async Task FindSymbol_IncludeTests_IncludesTestProjectSymbols()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeTests"], includeTests: true, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("ShapeTests");
    }

    // ── Batch tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_MultipleNames_ReturnsSectionPerName()
    {
        // Act
        string result = await tools.FindSymbol(["IShape", "ShapeService"], ct: TestContext.Current.CancellationToken);

        // Assert — section headers for each search
        result.ShouldContain("=== Search: \"IShape\" ===");
        result.ShouldContain("=== Search: \"ShapeService\" ===");

        // Assert — symbols from both searches present
        result.ShouldContain("interface");
        result.ShouldContain("ShapeService");
    }

    [Fact]
    public async Task FindSymbol_MultipleNames_OneNotFound_StillReturnsOther()
    {
        // Act
        string result = await tools.FindSymbol(["IShape", "NonExistentXyz"], ct: TestContext.Current.CancellationToken);

        // Assert — both sections present
        result.ShouldContain("=== Search: \"IShape\" ===");
        result.ShouldContain("=== Search: \"NonExistentXyz\" ===");

        // Assert — IShape found, other not
        result.ShouldContain("interface");
        result.ShouldContain("No symbols found");
    }

    // ── Modifiers ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Shape", "public abstract class Shape")]
    [InlineData("ShapeHelper", "public static class ShapeHelper")]
    public async Task FindSymbol_ClassModifier_ShowsInDisplay(string className, string expectedSignature)
    {
        // Act
        string result = await tools.FindSymbol([className], SymbolicKind.Class, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain(expectedSignature);
    }

    // ── Generics ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_GenericClass_ShowsTypeParametersAndConstraints()
    {
        // Act
        string result = await tools.FindSymbol(["Repository"], SymbolicKind.Class, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Repository<T>");
        result.ShouldContain("where T : class, new()");
    }

    [Fact]
    public async Task FindSymbol_GenericClass_WithDepth_ShowsMemberAccessModifiers()
    {
        // Act
        string result = await tools.FindSymbol(["Repository"], SymbolicKind.Class, 1, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert — members show access modifiers
        result.ShouldContain("[public method]");
    }

    [Fact]
    public async Task FindSymbol_GenericInterface_ShowsConstraints()
    {
        // Act
        string result = await tools.FindSymbol(["IGenericRepository"], SymbolicKind.Interface, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("IGenericRepository<T>");
        result.ShouldContain("where T : class, IShape");
    }

    // ── Namespace display ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Circle")]
    [InlineData("IShape")]
    public async Task FindSymbol_TopLevelSymbol_ShowsNamespace(string symbolName)
    {
        // Act
        string result = await tools.FindSymbol([symbolName], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert — top-level types show namespace
        result.ShouldContain("Namespace: TestFixture.Shapes");
    }

    [Fact]
    public async Task FindSymbol_MemberSymbol_ShowsContainingTypeNotNamespace()
    {
        // Act — search for a member (method) inside a type, without containingType filter
        string result = await tools.FindSymbol(["ProcessShape"], ct: TestContext.Current.CancellationToken);

        // Assert — members show containing type, not namespace
        result.ShouldContain("Containing type: ShapeService");
        result.ShouldNotContain("Namespace:");
    }

    [Fact]
    public async Task FindSymbol_WithContainingTypeFilter_SuppressesContainingType()
    {
        // Act — search with containingType filter: containing type is redundant
        string result = await tools.FindSymbol(["ProcessShape"], containingType: "ShapeService", ct: TestContext.Current.CancellationToken);

        // Assert — containing type is suppressed since it was used as a filter
        result.ShouldNotContain("Containing type:");
        result.ShouldNotContain("Namespace:");
    }

    [Fact]
    public async Task FindSymbol_NestedType_ShowsContainingTypeNotNamespace()
    {
        // Act — InnerProcessor is nested inside OuterContainer
        string result = await tools.FindSymbol(["InnerProcessor"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert — nested type shows containing type, not namespace
        result.ShouldContain("Containing type: OuterContainer");
        result.ShouldNotContain("Namespace:");
    }

    [Fact]
    public async Task FindSymbol_MultipleTopLevelTypes_AllShowNamespace()
    {
        // Act — search for types in different namespaces
        string result = await tools.FindSymbol(["Circle", "ShapeService"], ct: TestContext.Current.CancellationToken);

        // Assert — both should show their respective namespaces
        result.ShouldContain("Namespace: TestFixture.Shapes");
        result.ShouldContain("Namespace: TestFixture.Services");
    }

    // ── Namespace rendering ──────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_Namespace_ShowsSummaryInsteadOfFileList()
    {
        // Act — search for a namespace that spans multiple files
        string result = await tools.FindSymbol(["TestFixture.Shapes"], SymbolicKind.Namespace, ct: TestContext.Current.CancellationToken);

        // Assert — should show summary, not individual file locations
        result.ShouldContain("Spans");
        result.ShouldContain("files");
        result.ShouldContain("Projects:");
        result.ShouldNotContain("Locations (");
    }
}
