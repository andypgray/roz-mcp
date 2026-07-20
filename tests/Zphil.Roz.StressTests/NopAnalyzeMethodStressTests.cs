using System.Text.RegularExpressions;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Scale-correctness coverage for <see cref="NavigationTools.AnalyzeMethod" /> against
///     nopCommerce: the compound caller/callee report must stay correct and bounded on god-class
///     services, interface dispatch, and batch fan-out at ~300K-LOC scale.
/// </summary>
/// <remarks>
///     Necessary but not sufficient for promoting the tool out of
///     <c>HeldFromDefaultPendingValidation</c> — that hold is gated on an A/B arm confirming the
///     context-cost win (see docs/evidence/ab-test-analyze-method-2026-06-04.md), which these
///     in-process tests cannot answer.
/// </remarks>
[Trait("Category", "Stress")]
[Collection("NopReadOnlyWorkspace")]
public class NopAnalyzeMethodStressTests(NopWorkspaceFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task AnalyzeMethod_GodMethod_SearchProducts_CallersAndCalleesAtScale()
    {
        // Arrange — SearchProductsAsync is nop's ~30-parameter god method: heavy outbound fan-out
        // through repositories and localization, plus real inbound callers in Nop.Web.
        NavigationTools nav = CreateNavigationTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(productServiceFile, "SearchProductsAsync");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string result = await TimingHelper.TimeAsync("Nop_AnalyzeMethod_SearchProductsAsync",
            () => nav.AnalyzeMethod([Loc(productServiceFile, line, column)], ct: cts.Token), output);

        // Assert — one compound report: signature, inbound callers, grouped outbound callees.
        output.WriteLine(result[..Math.Min(3000, result.Length)]);
        result.ShouldContain("SearchProductsAsync");
        result.ShouldContain("Callers of");
        result.ShouldContain("Outbound calls (");
        result.ShouldContain("in-solution");
    }

    [Fact]
    public async Task AnalyzeMethod_InterfaceMember_NameBased_FindsDispatchCallersAcrossProjects()
    {
        // Arrange — IProductService.GetProductByIdAsync is invoked via interface dispatch from
        // controllers/services in separate projects; the interface member itself has no body, so the
        // outbound section must degrade gracefully to an empty in-solution group.
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string result = await TimingHelper.TimeAsync("Nop_AnalyzeMethod_IProductService_GetProductByIdAsync",
            () => nav.AnalyzeMethod(symbolNames: ["GetProductByIdAsync"], containingType: "IProductService", ct: cts.Token), output);

        // Assert — cross-project inbound callers, and a well-formed (not crashed) outbound section.
        output.WriteLine(result[..Math.Min(3000, result.Length)]);
        result.ShouldContain("Callers of 'GetProductByIdAsync'");
        result.ShouldContain("Nop.");
        result.ShouldContain("Outbound calls (");
    }

    [Fact]
    public async Task AnalyzeMethod_ConcreteImplementation_SurfacesInterfaceDispatch()
    {
        // Arrange — most consumers call IProductService, not ProductService directly, so analyzing the
        // concrete member must still surface the interface relationship (dispatch tip or interface
        // callers) instead of silently reporting only same-class calls.
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string result = await TimingHelper.TimeAsync("Nop_AnalyzeMethod_ProductService_GetProductByIdAsync",
            () => nav.AnalyzeMethod(symbolNames: ["GetProductByIdAsync"], containingType: "ProductService", ct: cts.Token), output);

        // Assert
        output.WriteLine(result[..Math.Min(3000, result.Length)]);
        result.ShouldContain("Callers of 'GetProductByIdAsync'");
        result.ShouldContain("IProductService");
    }

    [Fact]
    public async Task AnalyzeMethod_Batch_ThreeInterfaceMembers_OneCallWithSections()
    {
        // Arrange — the batched name mode must produce one `=== name ===` section per item at scale,
        // not require three round trips.
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        string result = await TimingHelper.TimeAsync("Nop_AnalyzeMethod_Batch_ThreeMembers",
            () => nav.AnalyzeMethod(
                symbolNames: ["GetProductByIdAsync", "DeleteProductAsync", "UpdateProductAsync"],
                containingType: "IProductService", ct: cts.Token), output);

        // Assert
        output.WriteLine($"Batch result length: {result.Length}");
        result.ShouldContain("=== GetProductByIdAsync ===");
        result.ShouldContain("=== DeleteProductAsync ===");
        result.ShouldContain("=== UpdateProductAsync ===");
    }

    [Fact]
    public async Task AnalyzeMethod_IncludeExternalCalls_PromotesExternalRows()
    {
        // Arrange — SearchProductsAsync leans on LINQ/EF extension methods, so external callees exist.
        NavigationTools nav = CreateNavigationTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(productServiceFile, "SearchProductsAsync");
        string location = Loc(productServiceFile, line, column);

        // Act — the same method with the collapsed default and with promotion on. The promotion
        // proof is the collapsed "(+N external: …)" remainder disappearing relative to the default
        // run — a bare word-match on "external" holds in both forms and proves nothing.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(240));
        string collapsed = await nav.AnalyzeMethod([location], ct: cts.Token);
        string promoted = await TimingHelper.TimeAsync("Nop_AnalyzeMethod_SearchProducts_ExternalCalls",
            () => nav.AnalyzeMethod([location], includeExternalCalls: true, ct: cts.Token), output);

        // Assert
        output.WriteLine(promoted[..Math.Min(3000, promoted.Length)]);
        int collapsedCount = CollapsedExternalCount(collapsed);
        collapsedCount.ShouldBeGreaterThan(0);
        CollapsedExternalCount(promoted).ShouldBeLessThan(collapsedCount);
        promoted.ShouldContain("Outbound calls (");
        promoted.ShouldContain("external");
    }

    /// <summary>Reads N from the collapsed <c>"(+N external: …)"</c> remainder line; 0 when absent.</summary>
    private static int CollapsedExternalCount(string output)
    {
        Match match = Regex.Match(output, @"\(\+(\d+) external:");
        return match.Success ? Int32.Parse(match.Groups[1].Value) : 0;
    }
}
