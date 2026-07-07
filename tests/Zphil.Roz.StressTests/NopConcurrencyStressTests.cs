using Zphil.Roz.Infrastructure;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

[Trait("Category", "Stress")]
[Collection("NopReadOnlyWorkspace")]
public class NopConcurrencyStressTests(NopWorkspaceFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task ConcurrentReads_50ParallelFindSymbol_AllSucceed()
    {
        // Arrange
        NavigationTools nav = CreateNavigationTools(fixture);
        string[] searchTerms =
        [
            "Product", "Customer", "Order", "Category", "Manufacturer",
            "Discount", "ShoppingCartItem", "Address", "Country", "Currency",
            "ProductService", "CustomerService", "OrderService", "CategoryService", "ShippingService",
            "TaxService", "PriceCalculationService", "PictureService", "DiscountService", "LocalizationService",
            "Product", "Customer", "Order", "Category", "Manufacturer",
            "Discount", "ShoppingCartItem", "Address", "Country", "Currency",
            "ProductService", "CustomerService", "OrderService", "CategoryService", "ShippingService",
            "TaxService", "PriceCalculationService", "PictureService", "DiscountService", "LocalizationService",
            "Product", "Customer", "Order", "Category", "Manufacturer",
            "Discount", "ShoppingCartItem", "Address", "Country", "Currency"
        ];

        // Act — 50 parallel searches
        string[] results = await TimingHelper.TimeAsync("Nop_ConcurrentReads_50ParallelFindSymbol", async () =>
        {
            Task<string>[] tasks = searchTerms
                .Select(term => nav.FindSymbol([term]))
                .ToArray();

            return await Task.WhenAll(tasks);
        }, output);

        // Assert — each should find at least one result
        for (var i = 0; i < results.Length; i++)
        {
            results[i].ShouldContain(searchTerms[i]);
        }
    }

    [Fact]
    public async Task ParallelReads_MixedToolTypes_AllSucceed()
    {
        // Arrange — run different tool types concurrently against the large workspace
        NavigationTools nav = CreateNavigationTools(fixture);
        ReferenceTools refs = CreateReferenceTools(fixture);
        TypeHierarchyTools typesHierarchy = CreateTypeTools(fixture);

        string baseEntityFile = BaseEntityFile(fixture.WorkspaceManager);
        string productFile = ProductFile(fixture.WorkspaceManager);

        Task<(int line, int column)> beTask = SymbolPositionHelper.FindSymbolPositionAsync(baseEntityFile, "BaseEntity");
        Task<(int line, int column)> prodTask = SymbolPositionHelper.FindSymbolPositionAsync(productFile, "Product");
        (int beLine, int beColumn) = await beTask;
        (int prodLine, int prodColumn) = await prodTask;

        // Act — mix of find_symbol, find_implementations (type dispatch), type_hierarchy,
        // find_references in parallel
        List<Task<string>> tasks = new()
        {
            nav.FindSymbol(["ProductService"], ct: TestContext.Current.CancellationToken),
            nav.FindSymbol(["CustomerService"], ct: TestContext.Current.CancellationToken),
            nav.FindSymbol(["OrderService"], ct: TestContext.Current.CancellationToken),
            refs.FindImplementations([Loc(baseEntityFile, beLine, beColumn)], maxResults: 50, ct: TestContext.Current.CancellationToken),
            typesHierarchy.GetTypeHierarchy([Loc(productFile, prodLine, prodColumn)], ct: TestContext.Current.CancellationToken),
            refs.FindReferences([Loc(baseEntityFile, beLine, beColumn)], maxResults: 50, ct: TestContext.Current.CancellationToken),
            nav.GetSymbolsOverview([productFile], ct: TestContext.Current.CancellationToken)
        };

        string[] results = await TimingHelper.TimeAsync("Nop_ParallelReads_MixedToolTypes",
            () => Task.WhenAll(tasks), output);

        // Assert — every parallel op resolved the symbol it was asked about, not merely returned
        // something non-empty. Each index maps to a specific tool call above.
        results[0].ShouldContain("ProductService"); // find_symbol ProductService
        results[1].ShouldContain("CustomerService"); // find_symbol CustomerService
        results[2].ShouldContain("OrderService"); // find_symbol OrderService
        results[3].ShouldContain("BaseEntity"); // find_implementations: "Derived classes of 'BaseEntity'"
        results[3].ShouldContain("Product"); // ...and Product is one of the subtypes
        results[4].ShouldContain("BaseEntity"); // type hierarchy of Product climbs to BaseEntity
        results[5].ShouldContain("BaseEntity"); // find_references to BaseEntity
        results[6].ShouldContain("Product"); // get_symbols_overview of Product.cs
    }

    [Fact]
    public async Task ConcurrentReads_RepeatedGetSymbolsOverview_LargeFiles()
    {
        // Arrange — hit GetSymbolsOverview on multiple large files concurrently
        NavigationTools nav = CreateNavigationTools(fixture);
        WorkspaceManager ws = fixture.WorkspaceManager;

        string[] files =
        [
            ProductServiceFile(ws),
            ProductFile(ws),
            CustomerFile(ws),
            OrderFile(ws)
        ];

        // Act — 4 parallel overview requests
        string[] results = await TimingHelper.TimeAsync("Nop_ConcurrentReads_GetSymbolsOverview", async () =>
        {
            Task<string>[] tasks = files.Select(f => nav.GetSymbolsOverview([f])).ToArray();
            return await Task.WhenAll(tasks);
        }, output);

        // Assert
        results[0].ShouldContain("ProductService");
        results[1].ShouldContain("Product");
        results[2].ShouldContain("Customer");
        results[3].ShouldContain("Order");
    }
}
