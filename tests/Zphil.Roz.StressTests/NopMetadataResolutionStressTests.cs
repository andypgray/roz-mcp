using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Metadata, fully-qualified-name, and open-generic resolution at scale. These resolution paths
///     (resolving a BCL interface by name, dotted FQNs, and open-generic arity syntax) had no stress
///     coverage. nopCommerce amplifies them: IDisposable has implementers across many projects and
///     IRepository&lt;TEntity&gt; is the most widely-consumed open generic in the solution.
/// </summary>
[Trait("Category", "Stress")]
[Collection("NopReadOnlyWorkspace")]
public class NopMetadataResolutionStressTests(NopWorkspaceFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task FindImplementations_IDisposable_Metadata_FindsSolutionTypes()
    {
        // Arrange — IDisposable is not declared in the solution; it resolves from metadata. Type
        // dispatch then finds solution types implementing it (cache managers, lock wrappers, etc.).
        ReferenceTools refs = CreateReferenceTools(fixture);

        // Act — scanning every project for IDisposable implementers (incl. metadata) is heavy; allow
        // ample time under full-suite parallelism
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        string result = await TimingHelper.TimeAsync("Nop_FindImplementations_IDisposable_Metadata",
            () => refs.FindImplementations(symbolNames: ["IDisposable"], includeMetadata: true,
                maxResults: 200, ct: cts.Token), output);

        // Assert — resolved the metadata interface and surfaced solution implementers. Solution
        // types contribute "Nop." entries either directly or via the truncation distribution.
        output.WriteLine(result[..Math.Min(2500, result.Length)]);
        result.ShouldContain("Types implementing 'IDisposable'");
        result.ShouldContain("Nop.");
    }

    [Fact]
    public async Task FindSymbol_FullyQualifiedName_ResolvesProduct()
    {
        // Arrange
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act — dotted FQN should resolve straight to the Product entity type
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string result = await TimingHelper.TimeAsync("Nop_FindSymbol_FQN_Product",
            () => nav.FindSymbol(["Nop.Core.Domain.Catalog.Product"], ct: cts.Token), output);

        // Assert
        output.WriteLine(result);
        result.ShouldContain("class Product");
        result.ShouldContain("Catalog"); // the declaration path
    }

    [Fact]
    public async Task FindSymbol_TypeDotMember_FQN()
    {
        // Arrange
        NavigationTools nav = CreateNavigationTools(fixture);

        // Act — Type.Member FQN should resolve the property, not the containing type
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string result = await TimingHelper.TimeAsync("Nop_FindSymbol_FQN_ProductPublished",
            () => nav.FindSymbol(["Nop.Core.Domain.Catalog.Product.Published"], ct: cts.Token), output);

        // Assert — resolved the bool Published member
        output.WriteLine(result);
        result.ShouldContain("Published");
        result.ShouldContain("bool");
    }

    [Fact]
    public async Task GetTypeHierarchy_OpenGenericArity_IRepository()
    {
        // Arrange — IRepository<> (arity 1) open-generic syntax should resolve the interface even
        // though no concrete instantiation is named.
        TypeHierarchyTools types = CreateTypeTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string result = await TimingHelper.TimeAsync("Nop_GetTypeHierarchy_OpenGeneric_IRepository",
            () => types.GetTypeHierarchy(symbolNames: ["IRepository<>"], ct: cts.Token), output);

        // Assert — resolved the open generic, not an error/no-match
        output.WriteLine(result);
        result.ShouldContain("IRepository");
        result.ShouldNotContain("No symbol");
    }
}
