using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

[Trait("Category", "Stress")]
[Collection("NopReadOnlyWorkspace")]
public class NopScaleStressTests(NopWorkspaceFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task SolutionLoad_LargeProject_CompletesAndHasProjects()
    {
        // Assert — fixture already loaded the solution; verify it has the expected project count
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        int projectCount = solution.Projects.Count();
        output.WriteLine($"nopCommerce loaded with {projectCount} projects");
        projectCount.ShouldBeGreaterThanOrEqualTo(25, "nopCommerce should have at least 25 projects");
    }

    [Fact]
    public async Task FindSymbol_Product_AcrossAllProjects()
    {
        // Arrange
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act — "Product" is used across nearly every nopCommerce project
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string result = await TimingHelper.TimeAsync("Nop_FindSymbol_Product",
            () => nav.FindSymbol(["Product"], SymbolicKind.Class,
                matchMode: SymbolMatchMode.Exact, maxResults: 50, ct: cts.Token), output);

        // Assert
        result.ShouldContain("Product");
        result.ShouldContain("Nop.Core");
    }

    [Fact]
    public async Task FindReferences_IRepository_CrossProjectReferences()
    {
        // Arrange — IRepository<TEntity> is the most widely used interface in nopCommerce
        ReferenceTools refs = CreateReferenceTools(fixture);
        string iRepoFile = IRepositoryFile(fixture.WorkspaceManager);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(iRepoFile, "IRepository");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string result = await TimingHelper.TimeAsync("Nop_FindReferences_IRepository",
            () => refs.FindReferences([Loc(iRepoFile, line, column)], maxResults: 200, ct: cts.Token), output);

        // Assert — IRepository should have references across many projects
        output.WriteLine($"IRepository references result length: {result.Length}");
        result.ShouldContain("IRepository");
        // Should find references in Services, Data, and plugin projects
        result.ShouldContain("Nop.");
    }

    [Fact]
    public async Task FindImplementations_OnBaseEntity_ManyImplementations()
    {
        // Arrange — BaseEntity has 50+ subtypes across Nop.Core.Domain.
        // Type dispatch: find_implementations on a class runs derived-types logic.
        ReferenceTools refs = CreateReferenceTools(fixture);
        string baseEntityFile = BaseEntityFile(fixture.WorkspaceManager);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(baseEntityFile, "BaseEntity");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string result = await TimingHelper.TimeAsync("Nop_FindImplementations_BaseEntity",
            () => refs.FindImplementations([Loc(baseEntityFile, line, column)], maxResults: 100, ct: cts.Token), output);

        // Assert — should find many entity subtypes
        output.WriteLine($"BaseEntity subtypes result length: {result.Length}");
        output.WriteLine(result[..Math.Min(2000, result.Length)]);
        result.ShouldContain("Product");
        result.ShouldContain("Customer");
        result.ShouldContain("Order");

        // The result should be substantial (many subtypes found)
        result.Length.ShouldBeGreaterThan(3000, "BaseEntity should have many subtypes producing a large result");
    }

    [Fact]
    public async Task GetSymbolsOverview_GodClassService_ManyMembers()
    {
        // Arrange — ProductService.cs is ~2500 lines with many methods
        NavigationTools nav = CreateNavigationTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string overview = await TimingHelper.TimeAsync("Nop_GetSymbolsOverview_ProductService",
            () => nav.GetSymbolsOverview([productServiceFile], ct: cts.Token), output);

        // Assert — ProductService should have many members
        output.WriteLine($"ProductService overview length: {overview.Length}");
        overview.ShouldContain("ProductService");

        // Count method listings
        int methodCount = overview.Split('\n').Count(l => l.Contains("[public") || l.Contains("[protected") || l.Contains("[private"));
        output.WriteLine($"Found {methodCount} members in ProductService");
        methodCount.ShouldBeGreaterThanOrEqualTo(30, "ProductService should have at least 30 members");
    }

    [Fact]
    public async Task FindReferences_Invocations_WidelyUsedMethod_ManyCallers()
    {
        // Arrange — find invocations of a commonly used method. find_callers was merged into
        // find_references referenceKinds=invocations (204162e), so this exercises the invocation-only path.
        ReferenceTools refs = CreateReferenceTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        // GetProductByIdAsync is likely called from many places
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(productServiceFile, "GetProductByIdAsync");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string result = await TimingHelper.TimeAsync("Nop_FindReferences_Invocations_GetProductByIdAsync",
            () => refs.FindReferences([Loc(productServiceFile, line, column)], referenceKinds: ReferenceKind.Invocations,
                maxResults: 100, ct: cts.Token), output);

        // Assert — invocation results are rendered as a "Callers of '<method>'" block
        output.WriteLine($"GetProductByIdAsync callers result length: {result.Length}");
        result.ShouldContain("GetProductByIdAsync");
        result.ShouldContain("Callers of");
    }

    [Fact]
    public async Task FindSymbol_WildcardSearch_LargeSolution()
    {
        // Arrange — search with a broad term across the entire solution
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act — "Service" should match many classes (Contains mode is slow on large solutions)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string result = await TimingHelper.TimeAsync("Nop_FindSymbol_Wildcard_Service",
            () => nav.FindSymbol(["Service"], matchMode: SymbolMatchMode.Contains,
                kind: SymbolicKind.Class, maxResults: 100, ct: cts.Token), output);

        // Assert — should find many service classes
        result.ShouldContain("ProductService");
        result.ShouldContain("CustomerService");

        int matchCount = result.Split('\n').Count(l => l.Contains("Service"));
        output.WriteLine($"Found {matchCount} lines containing 'Service'");
        matchCount.ShouldBeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public async Task GetTypeHierarchy_Product_ShowsInterfaceChain()
    {
        // Arrange — Product implements 7 interfaces: ILocalizedEntity, ISlugSupported, etc.
        TypeHierarchyTools typesHierarchy = CreateTypeTools(fixture);
        string productFile = ProductFile(fixture.WorkspaceManager);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(productFile, "Product");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string result = await TimingHelper.TimeAsync("Nop_TypeHierarchy_Product",
            () => typesHierarchy.GetTypeHierarchy([Loc(productFile, line, column)], ct: cts.Token), output);

        // Assert — should show Product's base class and implemented interfaces
        output.WriteLine(result);
        result.ShouldContain("Product");
        result.ShouldContain("BaseEntity");
    }

    [Fact]
    public async Task FindImplementations_IRepository_CrossProject()
    {
        // Arrange — IRepository<TEntity> may have implementations across many projects
        ReferenceTools refs = CreateReferenceTools(fixture);
        string iRepoFile = IRepositoryFile(fixture.WorkspaceManager);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(iRepoFile, "IRepository");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string result = await TimingHelper.TimeAsync("Nop_FindImplementations_IRepository",
            () => refs.FindImplementations([Loc(iRepoFile, line, column)], maxResults: 100, ct: cts.Token), output);

        // Assert — should find implementations in Nop.Data
        output.WriteLine($"IRepository implementations result length: {result.Length}");
        result.ShouldContain("IRepository");
        result.Length.ShouldBeGreaterThan(100, "IRepository should have implementations producing substantial output");
    }

    [Fact]
    public async Task FindSymbol_ContainsMode_Entity_HighMaxResults()
    {
        // Arrange — "Entity" appears in hundreds of class/interface names across nopCommerce
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act — Contains mode forces full symbol table scan
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string result = await TimingHelper.TimeAsync("Nop_FindSymbol_Contains_Entity",
            () => nav.FindSymbol(["Entity"], matchMode: SymbolMatchMode.Contains,
                maxResults: 200, ct: cts.Token), output);

        // Assert — should find many entity-related symbols
        int matchCount = result.Split('\n').Count(l => l.Contains("Entity"));
        output.WriteLine($"Found {matchCount} lines containing 'Entity'");
        matchCount.ShouldBeGreaterThanOrEqualTo(10, "nopCommerce should have many Entity-related symbols");
    }

    [Fact]
    public async Task FindReferences_Invocations_ExcludeBaseCalls_CrossProject_ReducesResults()
    {
        // Arrange — BasePlugin.InstallAsync is a virtual method in Nop.Core that many plugin
        // projects override with base.InstallAsync() calls. This tests cross-compilation
        // symbol comparison in IsOverrideOf (the bug: SymbolEqualityComparer.Default fails
        // when comparing symbols from different project compilations). excludeBaseCalls is an
        // invocation-only refinement of find_references referenceKinds=invocations (formerly find_callers).
        ReferenceTools refs = CreateReferenceTools(fixture);

        // Act — run with and without excludeBaseCalls
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string resultAll = await refs.FindReferences(
            symbolNames: ["InstallAsync"], containingType: "BasePlugin",
            referenceKinds: ReferenceKind.Invocations, excludeBaseCalls: false,
            maxResults: 200, ct: cts.Token);
        string resultFiltered = await refs.FindReferences(
            symbolNames: ["InstallAsync"], containingType: "BasePlugin",
            referenceKinds: ReferenceKind.Invocations, excludeBaseCalls: true,
            maxResults: 200, ct: cts.Token);

        // Assert — excludeBaseCalls should remove plugin overrides that call base.InstallAsync()
        output.WriteLine($"All callers length: {resultAll.Length}");
        output.WriteLine($"Filtered callers length: {resultFiltered.Length}");
        output.WriteLine($"--- All callers ---\n{resultAll[..Math.Min(2000, resultAll.Length)]}");
        output.WriteLine($"--- Filtered callers ---\n{resultFiltered[..Math.Min(2000, resultFiltered.Length)]}");

        int allCount = CountCallerEntries(resultAll);
        int filteredCount = CountCallerEntries(resultFiltered);
        output.WriteLine($"All caller count: {allCount}, Filtered caller count: {filteredCount}");

        filteredCount.ShouldBeLessThan(allCount,
            "excludeBaseCalls=true should produce fewer callers than false for a virtual method with cross-project overrides");
    }

    [Fact]
    public async Task FindReferences_Reads_HotProperty()
    {
        // Arrange — Product.Published is read in many filters across the solution and written when
        // (un)publishing. Only referenceKinds=Invocations was covered before; reads/writes were untested.
        ReferenceTools refs = CreateReferenceTools(fixture);

        // Act — count reads and writes of the same property
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string readsResult = await TimingHelper.TimeAsync("Nop_FindReferences_Reads_ProductPublished",
            () => refs.FindReferences(symbolNames: ["Published"], containingType: "Product",
                referenceKinds: ReferenceKind.Reads, maxResults: 50, ct: cts.Token), output);
        string writesResult = await refs.FindReferences(symbolNames: ["Published"], containingType: "Product",
            referenceKinds: ReferenceKind.Writes, maxResults: 50, ct: cts.Token);

        // Assert — reads are labeled distinctly and outnumber writes for a read-mostly flag
        readsResult.ShouldContain("Reads of 'Published'");
        int readsTotal = ExtractLocationTotal(readsResult);
        int writesTotal = ExtractLocationTotal(writesResult);
        output.WriteLine($"Product.Published reads={readsTotal}, writes={writesTotal}");
        readsTotal.ShouldBeGreaterThan(0, "Product.Published should be read somewhere");
        readsTotal.ShouldBeGreaterThan(writesTotal, "a read-mostly flag should be read more often than written");
    }

    [Fact]
    public async Task FindReferences_Writes_HotProperty()
    {
        // Arrange — writes must be a strict subset of all references to the same property.
        ReferenceTools refs = CreateReferenceTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string writesResult = await TimingHelper.TimeAsync("Nop_FindReferences_Writes_ProductPublished",
            () => refs.FindReferences(symbolNames: ["Published"], containingType: "Product",
                referenceKinds: ReferenceKind.Writes, maxResults: 50, ct: cts.Token), output);
        string allResult = await refs.FindReferences(symbolNames: ["Published"], containingType: "Product",
            referenceKinds: ReferenceKind.All, maxResults: 50, ct: cts.Token);

        // Assert — writes are labeled distinctly, non-zero, and fewer than all references
        writesResult.ShouldContain("Writes to 'Published'");
        int writesTotal = ExtractLocationTotal(writesResult);
        int allTotal = ExtractLocationTotal(allResult);
        output.WriteLine($"Product.Published writes={writesTotal}, all={allTotal}");
        writesTotal.ShouldBeGreaterThan(0, "Product.Published should be written somewhere");
        writesTotal.ShouldBeLessThan(allTotal, "writes must be a strict subset of all references");
    }

    private static int ExtractLocationTotal(string result)
    {
        // Header forms: "(5 location(s))" or, when truncated, "(showing 50 of 1898 location(s) ...)".
        // The total is the number immediately before "location(s)".
        Match match = Regex.Match(result, @"\((?:showing \d+ of )?(\d+) location\(s\)");
        return match.Success ? Int32.Parse(match.Groups[1].Value) : 0;
    }

    private static int CountCallerEntries(string result)
    {
        // find_references referenceKinds=invocations renders an invocation block whose header counts
        // callers, e.g. "Callers of 'InstallAsync' (11):" (the term predates the find_callers merge).
        Match match = Regex.Match(result, @"Callers of '.+?' \((\d+)\):");
        return match.Success ? Int32.Parse(match.Groups[1].Value) : 0;
    }
}
