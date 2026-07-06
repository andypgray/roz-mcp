using Zphil.Roz.Enums;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Scale coverage for <see cref="ReferenceTools.AnalyzeChangeImpact" /> against nopCommerce:
///     proposed changes to widely-referenced cross-project members must enumerate a blast radius
///     that spans projects and complete within a generous time bound. nopCommerce amplifies this —
///     <c>IProductService</c> and <c>IRepository&lt;T&gt;</c> are consumed from controllers,
///     services, and plugins in separate ~300K-LOC projects.
/// </summary>
[Trait("Category", "Stress")]
[Collection("NopReadOnlyWorkspace")]
public class NopChangeImpactStressTests(NopWorkspaceFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task AnalyzeChangeImpact_TypeChange_CrossProjectMethod_ClassifiesAtScale()
    {
        // Arrange — GetProductByIdAsync (IProductService, Nop.Services) is called from many projects.
        ReferenceTools refs = CreateReferenceTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(productServiceFile, "GetProductByIdAsync");

        // Act — propose widening the return value's element type; the tool must classify every site.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string result = await TimingHelper.TimeAsync("Nop_AnalyzeChangeImpact_TypeChange_GetProductByIdAsync",
            () => refs.AnalyzeChangeImpact([Loc(productServiceFile, line, column)],
                changeKind: ChangeKind.TypeChange, newType: "object", maxResults: 100, ct: cts.Token), output);

        // Assert — a structured impact report naming the member, with a per-severity summary.
        output.WriteLine($"Impact result length: {result.Length}");
        output.WriteLine(result[..Math.Min(2000, result.Length)]);
        result.ShouldContain("Impact of TypeChange on 'GetProductByIdAsync'");
        result.ShouldContain("site(s) —");
        result.ShouldContain("Nop.");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_AddOptional_CrossProject_ClassifiesAtScale()
    {
        // Arrange — GetProductByIdAsync (IProductService) is consumed across many projects; adding a
        // trailing optional CancellationToken should leave every existing call site Compatible. Precise
        // mode forks the solution and re-binds each site (dependent-cone recompilation), so this is
        // TypeChange-class-or-higher cost — acceptable for an opt-in, read-only analysis.
        ReferenceTools refs = CreateReferenceTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(productServiceFile, "GetProductByIdAsync");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string result = await TimingHelper.TimeAsync("Nop_AnalyzeChangeImpact_SignaturePrecise_AddOptional_GetProductByIdAsync",
            () => refs.AnalyzeChangeImpact([Loc(productServiceFile, line, column)],
                changeKind: ChangeKind.SignatureChange,
                newSignature: "(int productId, System.Threading.CancellationToken cancellationToken = default)",
                maxResults: 100, ct: cts.Token), output);

        // Assert — a precise (not coarse) cross-project report; eyeball the verdicts in the output.
        output.WriteLine($"Impact result length: {result.Length}");
        output.WriteLine(result[..Math.Min(3000, result.Length)]);
        result.ShouldContain("Impact of SignatureChange on 'GetProductByIdAsync'");
        result.ShouldContain("site(s) —");
        result.ShouldContain("[Compatible:");
        result.ShouldNotContain("coarse");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_RemoveSymbol_WidelyUsedInterface_CrossProjectBlastRadius()
    {
        // Arrange — IRepository<TEntity> is the most widely consumed interface in nopCommerce.
        ReferenceTools refs = CreateReferenceTools(fixture);
        string iRepoFile = IRepositoryFile(fixture.WorkspaceManager);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(iRepoFile, "IRepository");

        // Act — removing the type flags every reference Unsafe across the solution.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string result = await TimingHelper.TimeAsync("Nop_AnalyzeChangeImpact_RemoveSymbol_IRepository",
            () => refs.AnalyzeChangeImpact([Loc(iRepoFile, line, column)],
                changeKind: ChangeKind.RemoveSymbol, maxResults: 200, ct: cts.Token), output);

        // Assert — a large, cross-project blast radius of unsafe sites.
        output.WriteLine($"Impact result length: {result.Length}");
        result.ShouldContain("Impact of RemoveSymbol on 'IRepository'");
        result.ShouldContain("[Unsafe:");
        result.Length.ShouldBeGreaterThan(3000, "IRepository removal should produce a large cross-project report");
    }
}
