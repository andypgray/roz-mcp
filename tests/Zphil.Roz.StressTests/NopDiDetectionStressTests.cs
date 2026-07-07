using Zphil.Roz.Enums;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

/// <summary>
///     DI-registration detection at scale — the headline feature that had zero stress coverage.
///     nopCommerce registers 200+ services via Microsoft.Extensions.DependencyInjection
///     (<c>services.AddScoped&lt;IProductService, ProductService&gt;()</c> in NopStartup, plus test
///     registrations in BaseNopTest), and the scanner runs solution-wide.
/// </summary>
[Trait("Category", "Stress")]
[Collection("NopReadOnlyWorkspace")]
public class NopDiDetectionStressTests(NopWorkspaceFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task FindImplementations_RegisteredService_AppendsDiInfo()
    {
        // Arrange — IProductService is registered AddScoped<IProductService, ProductService>() in
        // Nop.Web.Framework's NopStartup. find_implementations always appends DI registration info.
        ReferenceTools refs = CreateReferenceTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string result = await TimingHelper.TimeAsync("Nop_FindImplementations_IProductService_DiInfo",
            () => refs.FindImplementations(symbolNames: ["IProductService"], kind: SymbolicKind.Interface,
                includeTests: true, maxResults: 50, ct: cts.Token), output);

        // Assert — the implementing type plus a MEDI scoped lifetime tag from the registration
        output.WriteLine(result);
        result.ShouldContain("ProductService");
        result.ShouldContain("DI registrations:");
        result.ShouldContain("(MEDI)");
        result.ShouldContain("scoped"); // AddScoped in NopStartup
    }

    [Fact]
    public async Task FindReferences_Ctor_FallsBackToDiRegistration()
    {
        // Arrange — ProductService is never constructed with `new` (it's DI-only), so a search for
        // constructor invocations finds no direct callers and the scanner surfaces the DI
        // registration as a fallback instead.
        ReferenceTools refs = CreateReferenceTools(fixture);

        // Act — target the constructor via the special ".ctor" symbol name
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string result = await TimingHelper.TimeAsync("Nop_FindReferences_ProductServiceCtor_DiFallback",
            () => refs.FindReferences(symbolNames: [".ctor"], containingType: "ProductService",
                referenceKinds: ReferenceKind.Invocations, maxResults: 50, ct: cts.Token), output);

        // Assert — no direct callers, DI registration surfaced as fallback with container + lifetime
        output.WriteLine(result);
        result.ShouldContain("DI registration");
        result.ShouldContain("MEDI");
        result.ShouldContain("ProductService"); // appears in the registration's source line
    }

    [Fact]
    public async Task FindImplementations_ManyRegisteredServices_Batch()
    {
        // Arrange — eight registered catalog services in one batched call. Each is registered via
        // AddScoped in NopStartup, so each section should carry DI registration info. Doubles as a
        // batch-dispatch exercise (one round-trip, eight type-dispatch lookups + DI scans).
        ReferenceTools refs = CreateReferenceTools(fixture);
        string[] services =
        [
            "IProductService", "ICategoryService", "IManufacturerService", "IProductAttributeService",
            "IProductReviewService", "ISpecificationAttributeService", "IProductTemplateService",
            "IBackInStockSubscriptionService"
        ];

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string result = await TimingHelper.TimeAsync("Nop_FindImplementations_ManyServices_Batch",
            () => refs.FindImplementations(symbolNames: services, kind: SymbolicKind.Interface,
                includeTests: true, maxResults: 20, ct: cts.Token), output);

        // Assert — each interface rendered as its own batch section, and several carry DI tags
        output.WriteLine(result[..Math.Min(3000, result.Length)]);
        result.ShouldContain("=== "); // batched section headers
        result.ShouldContain("ProductService");
        result.ShouldContain("CategoryService");
        result.ShouldContain("ManufacturerService");
        result.ShouldContain("(MEDI)");

        int diSections = result.Split("DI registrations:").Length - 1;
        output.WriteLine($"Sections carrying DI registration info: {diSections}");
        diSections.ShouldBeGreaterThanOrEqualTo(3, "several registered services should carry DI tags");
    }
}
