using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Batch-dispatch coverage at scale. The batch arrays (symbolNames[]/locations[]) that CLAUDE.md
///     tells callers to use for one-round-trip fan-out were only ever exercised with single-element
///     arrays in the stress suite. These pack many lookups into one call and assert per-symbol
///     isolation and fault tolerance.
/// </summary>
[Trait("Category", "Stress")]
[Collection("NopReadOnlyWorkspace")]
public class NopBatchStressTests(NopWorkspaceFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task FindReferences_BatchSymbolNames_TenInOneCall()
    {
        // Arrange — ten service interfaces in a single batched call. Interface names resolve
        // unambiguously (unlike bare entity names like "Product", which match dozens of symbols).
        ReferenceTools refs = CreateReferenceTools(fixture);
        string[] names =
        [
            "IProductService", "ICategoryService", "IManufacturerService", "IProductAttributeService",
            "IProductReviewService", "ISpecificationAttributeService", "IProductTemplateService",
            "IBackInStockSubscriptionService", "IRecentlyViewedProductsService", "ICompareProductsService"
        ];

        // Act — one round-trip, ten symbol resolutions
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string result = await TimingHelper.TimeAsync("Nop_FindReferences_Batch_TenNames",
            () => refs.FindReferences(symbolNames: names, maxResults: 5, ct: cts.Token), output);

        // Assert — one labeled section per name, each resolving to a real references block
        output.WriteLine(result[..Math.Min(3000, result.Length)]);
        int sectionCount = result.Split("=== ").Length - 1;
        output.WriteLine($"Batch produced {sectionCount} sections");
        sectionCount.ShouldBe(names.Length, "each batched name should produce its own section");

        foreach (string name in names)
        {
            result.ShouldContain(name);
        }

        result.ShouldContain("References to"); // names resolved to references, not errors
    }

    [Fact]
    public async Task GetTypeHierarchy_BatchLocations_ManyInOneCall()
    {
        // Arrange — eight Catalog BaseEntity subtypes, located by cursor, batched as locations[]
        TypeHierarchyTools types = CreateTypeTools(fixture);
        string[] entityTypes =
        [
            "Product", "Category", "Manufacturer", "ProductAttribute",
            "ProductTag", "ProductReview", "SpecificationAttribute", "TierPrice"
        ];

        List<string> locations = new();
        foreach (string typeName in entityTypes)
        {
            string file = CatalogEntityFile(fixture.WorkspaceManager, typeName);
            (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(file, typeName);
            locations.Add(Loc(file, line, column));
        }

        // Act — one round-trip, eight type hierarchies
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string result = await TimingHelper.TimeAsync("Nop_GetTypeHierarchy_Batch_EightEntities",
            () => types.GetTypeHierarchy(locations.ToArray(), ct: cts.Token), output);

        // Assert — every entity rendered its own hierarchy, all climbing to BaseEntity
        output.WriteLine(result[..Math.Min(3000, result.Length)]);
        foreach (string typeName in entityTypes)
        {
            result.ShouldContain(typeName);
        }

        int baseEntityMentions = result.Split("BaseEntity").Length - 1;
        output.WriteLine($"BaseEntity mentions across hierarchies: {baseEntityMentions}");
        baseEntityMentions.ShouldBeGreaterThanOrEqualTo(entityTypes.Length,
            "each Catalog entity hierarchy should reach BaseEntity");
    }

    [Fact]
    public async Task FindReferences_Batch_FaultTolerant_OneBadName()
    {
        // Arrange — a bogus name mixed with valid, unambiguous interface names. The read fan-out is
        // fault-tolerant: the bad name becomes an inline "=== Error: <name> ===" block while the valid
        // names still resolve.
        ReferenceTools refs = CreateReferenceTools(fixture);
        string[] names = ["IProductService", "ICategoryService", "ZzzNoSuchSymbolEverInNop", "IManufacturerService"];

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string result = await TimingHelper.TimeAsync("Nop_FindReferences_Batch_FaultTolerant",
            () => refs.FindReferences(symbolNames: names, maxResults: 5, ct: cts.Token), output);

        // Assert — the bad name is reported inline; the valid ones still resolve
        output.WriteLine(result[..Math.Min(2000, result.Length)]);
        result.ShouldContain("=== Error: ZzzNoSuchSymbolEverInNop ===");
        result.ShouldContain("IProductService");
        result.ShouldContain("ICategoryService");
        result.ShouldContain("References to"); // valid names produced real reference blocks
    }

    [Fact]
    public async Task FindOverloads_BatchSymbolNames()
    {
        // Arrange — three overloaded IRepository<T> methods in one batched call
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string result = await TimingHelper.TimeAsync("Nop_FindOverloads_Batch_IRepository",
            () => nav.FindOverloads(symbolNames: ["DeleteAsync", "InsertAsync", "UpdateAsync"],
                containingType: "IRepository", ct: cts.Token), output);

        // Assert — each method gets its own section with its overload count
        output.WriteLine(result);
        result.ShouldContain("Overloads of 'DeleteAsync' in IRepository (3)");
        result.ShouldContain("Overloads of 'InsertAsync' in IRepository (2)");
        result.ShouldContain("Overloads of 'UpdateAsync' in IRepository (2)");
    }
}
