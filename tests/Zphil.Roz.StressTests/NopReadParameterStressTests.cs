using System.Text.RegularExpressions;
using Zphil.Roz.Enums;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Read-only output-shaping parameters (includeBody, maxBodyLines, includeDocs, contextLines,
///     project filter, memberKinds) at god-class scale. The only one with prior stress coverage was
///     a single includeBody in the overloads tests; the rest were untested.
/// </summary>
[Trait("Category", "Stress")]
[Collection("NopReadOnlyWorkspace")]
public class NopReadParameterStressTests(NopWorkspaceFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task FindSymbol_IncludeBody_GodClassMethod()
    {
        // Arrange
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act — includeBody should return the method's full source from the ~2500-line god class
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        string result = await TimingHelper.TimeAsync("Nop_FindSymbol_IncludeBody_GodClassMethod",
            () => nav.FindSymbol(["GetProductByIdAsync"], containingType: "ProductService",
                matchMode: SymbolMatchMode.Exact, includeBody: true, ct: cts.Token), output);

        // Assert
        output.WriteLine(result[..Math.Min(1500, result.Length)]);
        result.ShouldContain("GetProductByIdAsync");
        result.ShouldContain("Body:");
    }

    [Fact]
    public async Task FindSymbol_MaxBodyLines_TruncatesLargeBody()
    {
        // Arrange — SearchProductsAsync is one of the longest methods in nopCommerce (hundreds of
        // lines of filtering), so maxBodyLines must clip it and report the total line count.
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        string result = await TimingHelper.TimeAsync("Nop_FindSymbol_MaxBodyLines_TruncatesLargeBody",
            () => nav.FindSymbol(["SearchProductsAsync"], containingType: "ProductService",
                matchMode: SymbolMatchMode.Exact, includeBody: true, maxBodyLines: 20, ct: cts.Token), output);

        // Assert — body clipped at 20 lines with a total-line note
        output.WriteLine(result[..Math.Min(1500, result.Length)]);
        result.ShouldContain("SearchProductsAsync");
        result.ShouldContain("body truncated at 20 lines");
    }

    [Fact]
    public async Task FindSymbol_IncludeDocs_DocumentedInterface()
    {
        // Arrange — IProductService carries XML doc comments ("Product service").
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        string result = await TimingHelper.TimeAsync("Nop_FindSymbol_IncludeDocs_IProductService",
            () => nav.FindSymbol(["IProductService"], matchMode: SymbolMatchMode.Exact,
                includeDocs: true, ct: cts.Token), output);

        // Assert — the parsed documentation section is present
        output.WriteLine(result);
        result.ShouldContain("Documentation:");
        result.ShouldContain("Product service");
    }

    [Fact]
    public async Task FindReferences_ContextLines_ShowsSurroundingSource()
    {
        // Arrange — BaseEntity is referenced by every entity ("class X : BaseEntity"), giving plenty
        // of match sites to show context around.
        ReferenceTools refs = CreateReferenceTools(fixture);

        // Act — same query with and without surrounding context (two solution-wide scans; generous
        // timeout for full-suite parallelism)
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        string noContext = await TimingHelper.TimeAsync("Nop_FindReferences_ContextLines_None",
            () => refs.FindReferences(symbolNames: ["BaseEntity"], maxResults: 5, contextLines: 0, ct: cts.Token), output);
        string withContext = await TimingHelper.TimeAsync("Nop_FindReferences_ContextLines_Three",
            () => refs.FindReferences(symbolNames: ["BaseEntity"], maxResults: 5, contextLines: 3, ct: cts.Token), output);

        // Assert — context expands the output and marks matched lines with a ">" gutter marker
        output.WriteLine($"noContext={noContext.Length} chars, withContext={withContext.Length} chars");
        withContext.Length.ShouldBeGreaterThan(noContext.Length,
            "contextLines=3 should surround each match with extra source lines");
        Regex.IsMatch(withContext, @">\s+\d+ \|").ShouldBeTrue(
            "matched lines should be marked with a '>' gutter when context is shown");
    }

    [Fact]
    public async Task FindSymbol_ProjectFilter_ResolvesNameClash()
    {
        // Arrange — the project filter is applied at resolution time, narrowing which ProductService
        // is resolved rather than filtering results after the fact.
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        string result = await TimingHelper.TimeAsync("Nop_FindSymbol_ProjectFilter_ProductService",
            () => nav.FindSymbol(["ProductService"], project: "Nop.Services",
                matchMode: SymbolMatchMode.Exact, ct: cts.Token), output);

        // Assert — resolved the Nop.Services implementation
        output.WriteLine(result);
        result.ShouldContain("class ProductService");
        result.ShouldContain("Nop.Services");
    }

    [Fact]
    public async Task FindSymbol_MemberKinds_FiltersGodClassMembers()
    {
        // Arrange — ProductService has methods, properties, and fields. memberKinds=[Method] should
        // list only methods in the depth-1 member listing.
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        string result = await TimingHelper.TimeAsync("Nop_FindSymbol_MemberKinds_ProductService",
            () => nav.FindSymbol(["ProductService"], depth: 1, matchMode: SymbolMatchMode.Exact,
                memberKinds: [SymbolicKind.Method], ct: cts.Token), output);

        // Assert — listed members are methods; no property/field member tags
        output.WriteLine(result[..Math.Min(2000, result.Length)]);
        result.ShouldContain("Members");
        result.ShouldContain("method]");
        result.ShouldNotContain("property]");
        result.ShouldNotContain("field]");
    }

    [Fact]
    public async Task GetSymbolsOverview_MemberKinds_Filtered()
    {
        // Arrange — two catalog god-class services; member listing filtered to methods only.
        NavigationTools nav = CreateNavigationTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);
        string categoryServiceFile = CategoryServiceFile(fixture.WorkspaceManager);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        string result = await TimingHelper.TimeAsync("Nop_GetSymbolsOverview_MemberKinds_Filtered",
            () => nav.GetSymbolsOverview([productServiceFile, categoryServiceFile],
                memberKinds: [SymbolicKind.Method], ct: cts.Token), output);

        // Assert — both files listed, members filtered to methods
        output.WriteLine(result[..Math.Min(2000, result.Length)]);
        result.ShouldContain("ProductService");
        result.ShouldContain("CategoryService");
        result.ShouldContain("method]");
        result.ShouldNotContain("property]");
    }
}
