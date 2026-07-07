using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

public class FindSymbolTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = TestFileHelper.CreateNavigationTools(fixture);

    [Fact]
    public async Task FindSymbol_ByExactName_FindsInterface()
    {
        // Act
        string result = await tools.FindSymbol(["IShape"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("IShape");
        result.ShouldContain("interface");
    }

    [Fact]
    public async Task FindSymbol_BySubstring_FindsMultipleTypes()
    {
        // Act — increase maxResults so all Shape-related types appear
        string result = await tools.FindSymbol(["Shape"], maxResults: 30, ct: TestContext.Current.CancellationToken);

        // Assert — IShape, Shape, ShapeService all match
        result.ShouldContain("Found");
        result.ShouldContain("IShape");
        result.ShouldContain("ShapeService");
    }

    [Fact]
    public async Task FindSymbol_WithKindFilter_ReturnsOnlyMatchingKind()
    {
        // Act — filter to interfaces only
        string result = await tools.FindSymbol(["Shape"], SymbolicKind.Interface, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("IShape");
        result.ShouldNotContain("ShapeService");
    }

    [Fact]
    public async Task FindSymbol_KindFilterExcludesAllMatches_ReportsActualKinds()
    {
        // Act — "Metric" exists as both an interface and a class (Metric<T>); kind=Struct excludes both
        string result = await tools.FindSymbol(["Metric"], SymbolicKind.Struct, matchMode: SymbolMatchMode.Exact,
            ct: TestContext.Current.CancellationToken);

        // Assert — the empty result should hint that Metric exists as Class and Interface
        result.ShouldContain("No symbols found");
        result.ShouldContain("\"Metric\" exists as");
        result.ShouldContain("Class");
        result.ShouldContain("Interface");
    }

    [Fact]
    public async Task FindSymbol_NoNameMatch_DoesNotReportFilteredKinds()
    {
        // Act — "Xyzzy123" matches nothing; the kind-filter hint should not appear
        string result = await tools.FindSymbol(["Xyzzy123"], SymbolicKind.Struct,
            ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No symbols found");
        result.ShouldNotContain("exists as");
    }

    [Fact]
    public async Task FindSymbol_RazorComponent_IsFilteredFromResults()
    {
        // Act — ShapeCard is a Razor component; its generated class should be excluded
        string result = await tools.FindSymbol(["ShapeCard"], ct: TestContext.Current.CancellationToken);

        // Assert — generated Razor source is filtered out
        result.ShouldContain("No symbols found");
    }

    [Fact]
    public async Task FindSymbol_IncludeGenerated_DoesNotBreakNormalResults()
    {
        // Act — includeGenerated should not affect non-generated symbols
        string result = await tools.FindSymbol(["IShape"], includeGenerated: true, ct: TestContext.Current.CancellationToken);

        // Assert — normal symbols still found
        result.ShouldContain("IShape");
        result.ShouldContain("interface");
    }

    // ── excludePattern ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithExcludePattern_FiltersMatchingNames()
    {
        // Act — search "Shape" classes but exclude anything with "Service" in the name
        string result = await tools.FindSymbol(["Shape"], SymbolicKind.Class, excludePattern: "*Service*", ct: TestContext.Current.CancellationToken);

        // Assert — Shape is kept, ShapeService is excluded
        result.ShouldContain("Shape");
        result.ShouldNotContain("ShapeService");
    }

    [Fact]
    public async Task FindSymbol_ExcludePatternNoMatch_ReturnsAllResults()
    {
        // Act — increase maxResults so ShapeService isn't truncated
        string result = await tools.FindSymbol(["Shape"], excludePattern: "*Zzz*", maxResults: 30, ct: TestContext.Current.CancellationToken);

        // Assert — ShapeService should still be present
        result.ShouldContain("ShapeService");
    }

    // ── containingType ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithContainingType_ScopesToSpecificClass()
    {
        // Act — search "Area" but only inside Circle
        string result = await tools.FindSymbol(["Area"], containingType: "Circle", ct: TestContext.Current.CancellationToken);

        // Assert — Circle.Area should be found, not Rectangle.Area
        result.ShouldContain("Circle");
        result.ShouldNotContain("Rectangle");
    }

    [Fact]
    public async Task FindSymbol_WithContainingType_NoMatch_ReturnsNotFound()
    {
        // Act
        string result = await tools.FindSymbol(["Area"], containingType: "Nonexistent", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No symbols found");
    }

    [Fact]
    public async Task FindSymbol_WithContainingType_IsNamespace_ReturnsNamespaceHint()
    {
        // Act — pass a namespace (TestFixture.Shapes) as containingType
        string result = await tools.FindSymbol(["Circle"], containingType: "TestFixture.Shapes", ct: TestContext.Current.CancellationToken);

        // Assert — should detect it's a namespace and provide a helpful error
        result.ShouldContain("is a namespace, not a type");
        result.ShouldContain("omit containingType");
    }

    // ── matchMode: exact ──────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithMatchModeExact_ExcludesSubstringMatches()
    {
        // Act — "Shape" with matchMode should return only the Shape class, not IShape or ShapeService
        string result = await tools.FindSymbol(["Shape"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert — only 1 symbol found (the abstract Shape class)
        result.ShouldContain("(1)");
        result.ShouldContain("Found symbol(s)");
        result.ShouldNotContain("ShapeService");
    }

    [Theory]
    [InlineData("EndsWith", "service", 50, "ShapeService")]
    [InlineData("StartsWith", "shape", 10, "Shape")]
    public async Task FindSymbol_MatchMode_CaseInsensitive_FindsSymbol(
        string modeName, string search, int maxResults, string expected)
    {
        // Arrange
        SymbolMatchMode mode = Enum.Parse<SymbolMatchMode>(modeName);

        // Act — lowercase search with specified matchMode
        string result = await tools.FindSymbol([search], matchMode: mode, maxResults: maxResults, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain(expected);
    }

    [Fact]
    public async Task FindSymbol_ExactMode_IsCaseSensitive()
    {
        // Act — lowercase "ishape" should NOT match "IShape" in Exact mode
        string result = await tools.FindSymbol(["ishape"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No symbols found");
    }

    [Fact]
    public async Task FindSymbol_ExactMode_CaseSensitiveMatch()
    {
        // Act — correct case "IShape" should match in Exact mode
        string result = await tools.FindSymbol(["IShape"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("IShape");
        result.ShouldContain("Found symbol(s)");
    }

    [Theory]
    [InlineData("Exact", "NonExistentSymbol")]
    [InlineData("EndsWith", "Zzzzz")]
    public async Task FindSymbol_MatchMode_NoMatch_ReturnsNotFoundWithModeContext(
        string modeName, string search)
    {
        // Arrange
        SymbolMatchMode mode = Enum.Parse<SymbolMatchMode>(modeName);

        // Act
        string result = await tools.FindSymbol([search], matchMode: mode, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No symbols found");
        result.ShouldContain($"matchMode={mode}");
    }

    // ── matchMode: endsWith ────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithMatchModeEndsWith_FindsMatchingSymbols()
    {
        // Act — "Service" with endsWith should match ShapeService
        string result = await tools.FindSymbol(["Service"], matchMode: SymbolMatchMode.EndsWith, maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert — only types ending in "Service" should appear
        result.ShouldContain("ShapeService");
        result.ShouldNotContain("IShape");
        result.ShouldNotContain("Circle");
    }

    // ── matchMode: startsWith ───────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithMatchModeStartsWith_ExcludesNonPrefixMatches()
    {
        // Act — "Shape" with startsWith + kind=interface: IShape starts with "I" not "Shape", so no match
        string result = await tools.FindSymbol(["Shape"], SymbolicKind.Interface, matchMode: SymbolMatchMode.StartsWith, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No symbols found");
    }

    [Fact]
    public async Task FindSymbol_WithMatchModeStartsWith_FindsPrefixMatches()
    {
        // Act — "Shape" with startsWith should match ShapeService (increase maxResults to cover all Shape* types)
        string result = await tools.FindSymbol(["Shape"], matchMode: SymbolMatchMode.StartsWith, maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("ShapeService");
    }

    // ── glob wildcards in names ─────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_GlobSuffix_FindsMatchingSymbols()
    {
        // Act — "*Service" should find ShapeService
        string result = await tools.FindSymbol(["*Service"], maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("ShapeService");
        result.ShouldNotContain("IShape");
        result.ShouldNotContain("Circle");
    }

    [Fact]
    public async Task FindSymbol_GlobPrefix_FindsMatchingSymbols()
    {
        // Act — "Shape*" should find Shape, ShapeService, etc.
        string result = await tools.FindSymbol(["Shape*"], maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert — Shape and ShapeService match; Circle does not
        result.ShouldContain("ShapeService");
        result.ShouldNotContain("Circle");
    }

    [Fact]
    public async Task FindSymbol_GlobOverridesMatchMode_StillWorks()
    {
        // Act — glob in the name should override matchMode (previously returned 0 results)
        string result = await tools.FindSymbol(["*Service"], matchMode: SymbolMatchMode.EndsWith, maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert — glob takes precedence over matchMode
        result.ShouldContain("ShapeService");
    }

    [Fact]
    public async Task FindSymbol_StarWildcard_ReturnsResults()
    {
        // Act — standalone "*" should match all symbols
        string result = await tools.FindSymbol(["*"], maxResults: 5, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Found symbol(s)");
        result.ShouldNotContain("No symbols found");
    }

    [Fact]
    public async Task FindSymbol_StarWildcard_WithKindOperator_FindsOperators()
    {
        // Act — "*" with kind filter should find operators in ShapeCollection
        string result = await tools.FindSymbol(["*"], SymbolicKind.Operator, ct: TestContext.Current.CancellationToken);

        // Assert — ShapeCollection defines operator + and implicit operator int
        result.ShouldContain("operator +");
    }

    [Fact]
    public async Task FindSymbol_StarWildcard_WithProjectFilter_ScopesToProject()
    {
        // Act — "*" scoped to TestFixture project
        string result = await tools.FindSymbol(["*"], project: "TestFixture", maxResults: 5, ct: TestContext.Current.CancellationToken);

        // Assert — should find symbols known to be in the TestFixture project
        result.ShouldContain("Found symbol(s)");
        result.ShouldContain("TestFixture");
    }

    // ── project filter ────────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithProjectFilter_ScopesToProject()
    {
        // Act — search for "Shape" scoped to "TestFixture" project only
        string result = await tools.FindSymbol(["Shape"], project: "TestFixture", ct: TestContext.Current.CancellationToken);

        // Assert — shapes from the TestFixture project should be found
        result.ShouldContain("Shape");
        result.ShouldNotContain("No symbols found");
    }

    [Fact]
    public async Task FindSymbol_WithProjectFilter_NonExistentProject_ReportsUnknownProject()
    {
        // Act
        string result = await tools.FindSymbol(["Shape"], project: "NonExistentProject", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No project matching 'NonExistentProject' found in solution");
    }

    // ── fuzzy suggestions ────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_Typo_ShowsSuggestions()
    {
        // Act — "Circl" is a typo of "Circle"
        string result = await tools.FindSymbol(["Circl"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No symbols found");
        result.ShouldContain("Did you mean");
        result.ShouldContain("Circle");
    }

    [Fact]
    public async Task FindSymbol_TypoWithKindFilter_SuggestsOnlyMatchingKind()
    {
        // Act — "IShap" with kind=Interface should suggest IShape but not Shape (class)
        string result = await tools.FindSymbol(["IShap"], SymbolicKind.Interface, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Did you mean");
        result.ShouldContain("IShape");
    }

    [Fact]
    public async Task FindSymbol_CompletelyUnrelatedName_NoSuggestions()
    {
        // Act — "Xyzzy123" has no close matches
        string result = await tools.FindSymbol(["Xyzzy123"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No symbols found");
        result.ShouldNotContain("Did you mean");
    }

    [Fact]
    public async Task FindSymbol_ShortName_NoSuggestions()
    {
        // Act — 2-char search skips fuzzy matching
        string result = await tools.FindSymbol(["Zq"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No symbols found");
        result.ShouldNotContain("Did you mean");
    }

    [Fact]
    public async Task FindSymbol_Contains_RanksExactMatchFirst()
    {
        // Act — search for "Shape" with Contains mode and Class kind filter
        string result = await tools.FindSymbol(["Shape"], SymbolicKind.Class, maxResults: 20, matchMode: SymbolMatchMode.Contains, ct: TestContext.Current.CancellationToken);

        // Assert — exact match "Shape" should appear before prefix match "ShapeHelper"
        int shapePos = result.IndexOf("class Shape", StringComparison.Ordinal);
        int shapeHelperPos = result.IndexOf("ShapeHelper", StringComparison.Ordinal);

        shapePos.ShouldBeGreaterThan(-1, "Shape should appear in results");
        shapeHelperPos.ShouldBeGreaterThan(-1, "ShapeHelper should appear in results");
        shapePos.ShouldBeLessThan(shapeHelperPos,
            "Exact match (Shape) should rank before prefix match (ShapeHelper)");
    }

    [Fact]
    public async Task FindSymbol_Contains_RanksTypesBeforeMembers()
    {
        // Act — search for "Shape" without kind filter to get types and members
        string result = await tools.FindSymbol(["Shape"], maxResults: 30, ct: TestContext.Current.CancellationToken);

        // Assert — ShapeRequest (type) should appear before ShapeName (property member)
        int shapeRequestPos = result.IndexOf("ShapeRequest", StringComparison.Ordinal);
        int shapeNamePos = result.IndexOf("ShapeName", StringComparison.Ordinal);

        shapeRequestPos.ShouldBeGreaterThan(-1, "ShapeRequest type should appear in results");
        shapeNamePos.ShouldBeGreaterThan(-1, "ShapeName property should appear in results");
        shapeRequestPos.ShouldBeLessThan(shapeNamePos,
            "Types should rank before members within the same match tier");
    }

    // ── memberKinds ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_MemberKindsMethod_ShowsOnlyMethods()
    {
        // Act — Shape has properties (Area, Perimeter) and a method (Describe)
        string result = await tools.FindSymbol(["Shape"], SymbolicKind.Class, 1, matchMode: SymbolMatchMode.Exact, memberKinds: [SymbolicKind.Method], ct: TestContext.Current.CancellationToken);

        // Assert — only the method should appear, not properties
        result.ShouldContain("Describe");
        result.ShouldNotContain("Area");
        result.ShouldNotContain("Perimeter");
    }

    [Fact]
    public async Task FindSymbol_MemberKindsProperty_ShowsOnlyProperties()
    {
        // Act
        string result = await tools.FindSymbol(["Shape"], SymbolicKind.Class, 1, matchMode: SymbolMatchMode.Exact, memberKinds: [SymbolicKind.Property], ct: TestContext.Current.CancellationToken);

        // Assert — only properties should appear, not methods
        result.ShouldContain("Area");
        result.ShouldContain("Perimeter");
        result.ShouldNotContain("Describe");
    }

    [Fact]
    public async Task FindSymbol_MemberKindsMultiple_ShowsAllRequestedKinds()
    {
        // Act — request both methods and properties
        string result = await tools.FindSymbol(["Shape"], SymbolicKind.Class, 1, matchMode: SymbolMatchMode.Exact, memberKinds: [SymbolicKind.Method, SymbolicKind.Property], ct: TestContext.Current.CancellationToken);

        // Assert — both methods and properties should appear
        result.ShouldContain("Describe");
        result.ShouldContain("Area");
        result.ShouldContain("Perimeter");
    }

    [Fact]
    public async Task FindSymbol_MemberKindsOmitted_ShowsAllMembers()
    {
        // Act — no memberKinds filter, should show all members (current behavior)
        string result = await tools.FindSymbol(["Shape"], SymbolicKind.Class, 1, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert — all members should appear
        result.ShouldContain("Describe");
        result.ShouldContain("Area");
        result.ShouldContain("Perimeter");
    }

    [Fact]
    public async Task FindSymbol_MemberKindsAtDepthZero_HasNoEffect()
    {
        // Act — memberKinds should have no effect at depth=0
        string result = await tools.FindSymbol(["Shape"], SymbolicKind.Class, matchMode: SymbolMatchMode.Exact, memberKinds: [SymbolicKind.Method], ct: TestContext.Current.CancellationToken);

        // Assert — at depth=0, no members are shown at all
        result.ShouldNotContain("Members");
        result.ShouldContain("Shape");
    }

    [Fact]
    public async Task FindSymbol_EnumWithDepth_ShowsEnumValueNotField()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeColor"], depth: 1, ct: TestContext.Current.CancellationToken);

        // Assert — enum members should display as [enum value], not [public static field]
        result.ShouldContain("[enum value]");
        result.ShouldNotContain("[public static field]");
        result.ShouldContain("Red");
        result.ShouldContain("Green");
    }

    [Fact]
    public async Task FindSymbol_Namespace_DeduplicatesAcrossProjects()
    {
        // Act — TestFixture.Shapes namespace exists in both TestFixture and TestFixture.Tests projects
        string result = await tools.FindSymbol(["Shapes"], SymbolicKind.Namespace, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert — should return exactly one result with merged locations spanning both projects
        result.ShouldContain("Found symbol(s)");
        result.ShouldContain("(1)");
        result.ShouldContain("Spans");
        result.ShouldContain("TestFixture");
    }

    // ── distribution ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_WithMaxResults_ShowsDistribution()
    {
        // Act — "Shape" matches many symbols, truncation triggers distribution
        string result = await tools.FindSymbol(["Shape"], maxResults: 1, ct: TestContext.Current.CancellationToken);

        // Assert — distribution block appears when truncated
        result.ShouldContain("Distribution:");
        result.ShouldContain("Total:");
    }

    [Fact]
    public async Task FindSymbol_WithoutTruncation_NoDistribution()
    {
        // Act — high maxResults prevents truncation
        string result = await tools.FindSymbol(["Shape"], maxResults: 50, ct: TestContext.Current.CancellationToken);

        // Assert — no distribution when not truncated
        result.ShouldNotContain("Distribution:");
    }

    // ── maxResults validation ────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task FindSymbol_MaxResultsLessThanOne_ReturnsError(int maxResults)
    {
        // Act — validation error is captured inline per name
        string result = await tools.FindSymbol(["IShape"], maxResults: maxResults, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("maxResults");
    }

    // ── Metadata-symbol docs auto-inclusion ─────────────────────────────

    [Fact]
    public async Task FindSymbol_MetadataType_ByFqn_AutoIncludesDocs()
    {
        // Act — resolving a BCL type by FQN (no source in the solution). Docs should be
        // included automatically, mirroring go_to_definition's metadata-only behavior.
        string result = await tools.FindSymbol(["System.IDisposable"], ct: TestContext.Current.CancellationToken);

        // Assert — a Documentation: section appears even though includeDocs=false
        result.ShouldContain("IDisposable");
        result.ShouldContain("Documentation:");
    }

    [Fact]
    public async Task FindSymbol_SourceType_DoesNotAutoIncludeDocs()
    {
        // Act — source type without includeDocs: no Documentation section
        string result = await tools.FindSymbol(["IShape"], ct: TestContext.Current.CancellationToken);

        // Assert — no auto-included docs for solution-source symbols
        result.ShouldNotContain("Documentation:");
    }
}
