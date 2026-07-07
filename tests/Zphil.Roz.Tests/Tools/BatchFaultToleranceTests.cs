using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools;

/// <summary>
///     Read-only batches are fault-tolerant: when one name fails, the others still render.
///     Mixing a valid name ("Circle") with an invalid one ("DoesNotExist") should produce
///     both the success block and an inline error block instead of faulting the whole batch.
/// </summary>
public class BatchFaultToleranceTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools navigationTools = TestFileHelper.CreateNavigationTools(fixture);
    private readonly ReferenceTools referenceTools = TestFileHelper.CreateReferenceTools(fixture);
    private readonly TypeHierarchyTools typeHierarchyTools = TestFileHelper.CreateTypeTools(fixture);

    [Fact]
    public async Task FindSymbol_MixedBatch_RendersBothSuccessAndErrorBlocks()
    {
        // Act
        string result = await navigationTools.FindSymbol(["Circle", "DoesNotExist"], ct: TestContext.Current.CancellationToken);

        // Assert — both blocks present
        result.ShouldContain("=== Search: \"Circle\" ===");
        result.ShouldContain("Circle");
        result.ShouldContain("=== Search: \"DoesNotExist\" ===");
        result.ShouldContain("No symbols found");
    }

    [Fact]
    public async Task FindOverloads_MixedBatch_RendersBothSuccessAndErrorBlocks()
    {
        // Act — ShapeCalculator has constructor overloads; NonExistentMethod fails
        string result = await navigationTools.FindOverloads(symbolNames: [".ctor", "NonExistentMethod"], containingType: "ShapeCalculator", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("===");
        result.ShouldContain("Overloads of");
        result.ShouldContain("=== Error: NonExistentMethod ===");
        result.ShouldContain("No method found");
    }

    [Fact]
    public async Task FindReferences_MixedBatch_RendersBothSuccessAndErrorBlocks()
    {
        // Act
        string result = await referenceTools.FindReferences(symbolNames: ["Circle", "DoesNotExist"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("=== Circle ===");
        result.ShouldContain("=== Error: DoesNotExist ===");
        result.ShouldContain("No symbol found");
    }

    [Fact]
    public async Task FindImplementations_MixedBatch_RendersBothSuccessAndErrorBlocks()
    {
        // Act — IShape has implementations; DoesNotExist fails
        string result = await referenceTools.FindImplementations(symbolNames: ["IShape", "DoesNotExist"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("=== IShape ===");
        result.ShouldContain("=== Error: DoesNotExist ===");
        result.ShouldContain("No symbol found");
    }

    [Fact]
    public async Task FindReferences_Invocations_MixedBatch_RendersBothSuccessAndErrorBlocks()
    {
        // Act — Describe on IShape has callers; DoesNotExist fails
        string result = await referenceTools.FindReferences(symbolNames: ["Describe", "DoesNotExist"], containingType: "IShape", referenceKinds: ReferenceKind.Invocations, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("=== Describe ===");
        result.ShouldContain("=== Error: DoesNotExist ===");
        result.ShouldContain("No symbol found");
    }

    [Fact]
    public async Task GetTypeHierarchy_MixedBatch_RendersBothSuccessAndErrorBlocks()
    {
        // Act
        string result = await typeHierarchyTools.GetTypeHierarchy(symbolNames: ["Circle", "DoesNotExist"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("=== Circle ===");
        result.ShouldContain("Type hierarchy for 'Circle'");
        result.ShouldContain("=== Error: DoesNotExist ===");
        result.ShouldContain("No symbol found");
    }

    [Fact]
    public async Task FindImplementations_OnType_MixedBatch_RendersBothSuccessAndErrorBlocks()
    {
        // Act — Shape has derived classes (type dispatch); DoesNotExist fails
        string result = await referenceTools.FindImplementations(symbolNames: ["Shape", "DoesNotExist"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("=== Shape ===");
        result.ShouldContain("=== Error: DoesNotExist ===");
        result.ShouldContain("No symbol found");
    }
}
