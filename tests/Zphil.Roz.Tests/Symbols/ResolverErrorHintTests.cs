using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Symbols;

public class ResolverErrorHintTests(WorkspaceFixture fixture)
{
    private readonly ReferenceTools referenceTools = CreateReferenceTools(fixture);
    private readonly TypeHierarchyTools typeHierarchyTools = CreateTypeTools(fixture);

    // ── Special-member resolution requires containingType ───────────────────

    [Theory]
    [InlineData(".ctor")]
    [InlineData(".cctor")]
    public async Task FindReferences_Invocations_SpecialMemberWithoutContainingType_ReturnsError(string memberName)
    {
        // Act — .ctor/.cctor require containingType; per-name error is captured inline
        string result = await referenceTools.FindReferences(symbolNames: [memberName], referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("containingType is required");
    }

    // ── containingType-with-dots hint ───────────────────────────────────────

    [Fact]
    public async Task FindReferences_Invocations_ContainingTypeWithDots_GivesHelpfulErrorMessage()
    {
        // Act — pass a namespace-like containingType with dots
        string result = await referenceTools.FindReferences(symbolNames: ["Describe"], containingType: "TestFixture.Shapes.IShape", referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — error message should explain that containingType is not a namespace
        result.ShouldContain("containingType filters by enclosing type");
        result.ShouldContain("not by namespace");
    }

    [Fact]
    public async Task FindReferences_ContainingTypeWithDots_GivesHelpfulErrorMessage()
    {
        // Act — pass a fully-qualified name as containingType
        string result = await referenceTools.FindReferences(symbolNames: ["Area"], containingType: "TestFixture.Shapes.Circle", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("containingType filters by enclosing type");
        result.ShouldContain("not by namespace");
    }

    [Fact]
    public async Task FindReferences_Invocations_ContainingTypeWithoutDots_DoesNotShowNamespaceHint()
    {
        // Act — pass a non-existent simple type name (no dots)
        string result = await referenceTools.FindReferences(symbolNames: ["Describe"], containingType: "NonExistentType", referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — error should NOT contain the namespace hint since there are no dots
        result.ShouldContain("No symbol found");
        result.ShouldNotContain("not by namespace");
    }

    [Fact]
    public async Task FindImplementations_OnType_ContainingTypeWithDots_GivesHelpfulErrorMessage()
    {
        // Act — pass a namespace-qualified name as containingType
        string result = await referenceTools.FindImplementations(symbolNames: ["Shape"], containingType: "TestFixture.Shapes.Shape", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("containingType filters by enclosing type");
        result.ShouldContain("not by namespace");
    }

    // ── Near-miss suggestions in symbol-not-found errors ───────────────

    [Fact]
    public async Task FindReferences_Invocations_MisspelledMemberWithContainingType_ShowsSuggestions()
    {
        // Act — "Describ" is close to "Describe" in IShape
        string result = await referenceTools.FindReferences(symbolNames: ["Describ"], containingType: "IShape", referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Did you mean");
        result.ShouldContain("Describe");
    }

    [Fact]
    public async Task FindReferences_MisspelledSymbolNoContainingType_ShowsSuggestions()
    {
        // Act — "Circl" is close to "Circle"
        string result = await referenceTools.FindReferences(symbolNames: ["Circl"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Did you mean");
        result.ShouldContain("Circle");
    }

    [Fact]
    public async Task FindReferences_Invocations_CompletelyUnrelatedName_NoSuggestions()
    {
        // Act — "Xyzzy123" has no close matches
        string result = await referenceTools.FindReferences(symbolNames: ["Xyzzy123"], referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No symbol found");
        result.ShouldNotContain("Did you mean");
    }

    [Fact]
    public async Task FindReferences_ShortName_NoSuggestions()
    {
        // Act — 2-char search skips fuzzy matching
        string result = await referenceTools.FindReferences(symbolNames: ["Zq"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No symbol found");
        result.ShouldNotContain("Did you mean");
    }

    // ── symbolName == containingType self-referencing hint ─────────────

    [Fact]
    public async Task GetTypeHierarchy_SymbolNameEqualsContainingType_AutoCorrected()
    {
        // Act — user passes the type name as both symbolName and containingType;
        // tool should silently drop containingType and resolve the type itself
        string result = await typeHierarchyTools.GetTypeHierarchy(symbolNames: ["Shape"], containingType: "Shape", ct: TestContext.Current.CancellationToken);

        // Assert — succeeds and returns the hierarchy
        result.ShouldContain("Shape");
        result.ShouldContain("IShape");
    }

    [Fact]
    public async Task FindReferences_Invocations_SymbolNameEqualsContainingType_NonExistentType_NoSelfReferenceHint()
    {
        // Act — both are "Nonexistent" which doesn't exist as a type
        string result = await referenceTools.FindReferences(symbolNames: ["Nonexistent"], containingType: "Nonexistent", referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — should NOT contain the self-reference hint since the type doesn't exist
        result.ShouldContain("No symbol found");
        result.ShouldNotContain("Omit containingType");
    }
}
