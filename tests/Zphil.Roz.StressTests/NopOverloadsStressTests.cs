using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

[Trait("Category", "Stress")]
[Collection("NopReadOnlyWorkspace")]
public class NopOverloadsStressTests(NopWorkspaceFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task FindOverloads_ControllerEdit_ByName_FindsBothOverloads()
    {
        // Arrange — ProductController.Edit has GET (int id) and POST (ProductModel, bool) overloads
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act — use name-based resolution since "Edit" is a common word that appears in non-method contexts
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string result = await TimingHelper.TimeAsync("Nop_FindOverloads_Edit_ByName",
            () => nav.FindOverloads(symbolNames: ["Edit"], containingType: "ProductController", ct: cts.Token), output);

        // Assert
        output.WriteLine($"Edit overloads result length: {result.Length}");
        output.WriteLine(result[..Math.Min(2000, result.Length)]);
        result.ShouldContain("Edit");
        result.ShouldContain("Overloads");
        result.ShouldContain("(2)");
    }

    [Fact]
    public async Task FindOverloads_IRepositoryDeleteAsync_ByName_FindsThreeOverloads()
    {
        // Arrange — IRepository<T>.DeleteAsync has 3 overloads:
        //   DeleteAsync(TEntity, bool)
        //   DeleteAsync(IList<TEntity>, bool)
        //   DeleteAsync(Expression<Func<TEntity, bool>>)
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string result = await TimingHelper.TimeAsync("Nop_FindOverloads_DeleteAsync_ByName",
            () => nav.FindOverloads(symbolNames: ["DeleteAsync"], containingType: "IRepository", ct: cts.Token), output);

        // Assert
        output.WriteLine($"DeleteAsync overloads result length: {result.Length}");
        output.WriteLine(result);
        result.ShouldContain("DeleteAsync");
        result.ShouldContain("Overloads");
        result.ShouldContain("(3)");
    }

    [Fact]
    public async Task FindOverloads_IRepositoryInsertAsync_ByPosition_FindsTwoOverloads()
    {
        // Arrange — IRepository<T>.InsertAsync has 2 overloads
        NavigationTools nav = CreateNavigationTools(fixture);
        string iRepoFile = IRepositoryFile(fixture.WorkspaceManager);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(iRepoFile, "InsertAsync");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string result = await TimingHelper.TimeAsync("Nop_FindOverloads_InsertAsync_ByPosition",
            () => nav.FindOverloads([Loc(iRepoFile, line, column)], ct: cts.Token), output);

        // Assert
        output.WriteLine(result);
        result.ShouldContain("InsertAsync");
        result.ShouldContain("(2)");
    }

    [Fact]
    public async Task FindOverloads_EditProductTag_ByPosition_FindsBothOverloads()
    {
        // Arrange — ProductController.EditProductTag has GET (int id) and POST (ProductTagModel, bool) overloads
        NavigationTools nav = CreateNavigationTools(fixture);
        string controllerFile = ProductControllerFile(fixture.WorkspaceManager);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(controllerFile, "EditProductTag");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string result = await TimingHelper.TimeAsync("Nop_FindOverloads_EditProductTag_ByPosition",
            () => nav.FindOverloads([Loc(controllerFile, line, column)], ct: cts.Token), output);

        // Assert
        output.WriteLine(result);
        result.ShouldContain("EditProductTag");
        result.ShouldContain("(2)");
    }

    [Fact]
    public async Task FindOverloads_SingleMethod_ReportsNoOverloads()
    {
        // Arrange — GetProductByIdAsync has no overloads in ProductService
        NavigationTools nav = CreateNavigationTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(productServiceFile, "GetProductByIdAsync");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string result = await TimingHelper.TimeAsync("Nop_FindOverloads_NoOverloads",
            () => nav.FindOverloads([Loc(productServiceFile, line, column)], ct: cts.Token), output);

        // Assert — single method, should report no overloads
        output.WriteLine(result);
        result.ShouldContain("has no overloads");
        result.ShouldContain("GetProductByIdAsync");
    }

    [Fact]
    public async Task FindOverloads_WithIncludeBody_ControllerEdit_ReturnsSource()
    {
        // Arrange — test includeBody on a large controller method
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act — use name-based resolution since "Edit" is a common word that appears in non-method contexts
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string result = await TimingHelper.TimeAsync("Nop_FindOverloads_Edit_WithBody",
            () => nav.FindOverloads(symbolNames: ["Edit"], containingType: "ProductController", includeBody: true, ct: cts.Token), output);

        // Assert — body should be present for both overloads
        output.WriteLine($"Edit overloads with body result length: {result.Length}");
        result.ShouldContain("Body:");
        result.Length.ShouldBeGreaterThan(500, "Edit overloads with body should produce substantial output");
    }
}
