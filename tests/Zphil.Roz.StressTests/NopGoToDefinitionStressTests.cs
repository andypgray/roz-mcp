using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Scale coverage for <see cref="NavigationTools.GoToDefinition" /> — cross-project interface
///     resolution, metadata (BCL) resolution with auto-docs, and line-only snap-to-member — none of
///     which the stress suite exercised before. nopCommerce amplifies cross-project resolution
///     (the interface and its consumer live in different ~300K-LOC projects).
/// </summary>
[Trait("Category", "Stress")]
[Collection("NopReadOnlyWorkspace")]
public class NopGoToDefinitionStressTests(NopWorkspaceFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task GoToDefinition_CrossProjectUsage_ResolvesInterface()
    {
        // Arrange — ProductController (Nop.Web) calls _productService.GetProductPicturesByProductIdAsync,
        // an IProductService member declared in Nop.Services. Navigating from the call site must cross
        // the project boundary to the interface member. (A cursor on a type *within a declaration*
        // snaps to the enclosing member by design, so we navigate from an expression instead.)
        NavigationTools nav = CreateNavigationTools(fixture);
        string controllerFile = ProductControllerFile(fixture.WorkspaceManager);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(controllerFile, "GetProductPicturesByProductIdAsync");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string result = await TimingHelper.TimeAsync("Nop_GoToDefinition_IProductServiceMember_CrossProject",
            () => nav.GoToDefinition(Loc(controllerFile, line, column), ct: cts.Token), output);

        // Assert — resolves to the IProductService member declared in Nop.Services, not the Nop.Web call
        output.WriteLine(result);
        result.ShouldContain("GetProductPicturesByProductIdAsync");
        result.ShouldContain("IProductService");
        result.ShouldContain("Nop.Services"); // the declaration's project/path, not Nop.Web
    }

    [Fact]
    public async Task GoToDefinition_OnMetadataType_AutoIncludesDocs()
    {
        // Arrange — ProductService bodies construct `new List<...>()`. Navigating onto the BCL List<T>
        // in that expression resolves to a metadata symbol with no source in the solution; docs are
        // auto-included for metadata symbols regardless of the includeDocs flag. (A cursor on a type
        // within a method signature would snap to the enclosing member, so we use a body expression.)
        NavigationTools nav = CreateNavigationTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(productServiceFile, "List");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string result = await TimingHelper.TimeAsync("Nop_GoToDefinition_List_Metadata",
            () => nav.GoToDefinition(Loc(productServiceFile, line, column), ct: cts.Token), output);

        // Assert — resolved to metadata (no source) with auto-included documentation
        output.WriteLine(result);
        result.ShouldContain("List");
        result.ShouldContain("Metadata-only type");
        result.ShouldContain("Documentation:");
    }

    [Fact]
    public async Task GoToDefinition_LineOnly_SnapsToMember()
    {
        // Arrange — a path:line location (no column) on a god-class method declaration line should
        // snap to the member declared there, not to its return type (Task<Product?>).
        NavigationTools nav = CreateNavigationTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);
        (int line, int _) = await SymbolPositionHelper.FindSymbolPositionAsync(productServiceFile, "GetProductByIdAsync");

        // Act — pass line only, no column
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string result = await TimingHelper.TimeAsync("Nop_GoToDefinition_LineOnly_SnapsToMember",
            () => nav.GoToDefinition(Loc(productServiceFile, line), ct: cts.Token), output);

        // Assert — snapped to the method itself (at its declaration), not the return type
        output.WriteLine(result);
        result.ShouldContain("GetProductByIdAsync");
        result.ShouldContain("At declaration");
    }
}
