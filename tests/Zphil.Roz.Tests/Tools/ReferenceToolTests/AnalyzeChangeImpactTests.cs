using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.ReferenceToolTests;

public class AnalyzeChangeImpactTests(WorkspaceFixture fixture)
{
    private readonly ReferenceTools tools = CreateReferenceTools(fixture);

    // ── TypeChange ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeChangeImpact_TypeChangeWidening_ReturnsCompatible()
    {
        // WidelyConsumed() is consumed only into a double context; widening int → long still converts.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["WidelyConsumed"], changeKind: ChangeKind.TypeChange, newType: "long",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("Impact of TypeChange on 'WidelyConsumed' → long");
        result.ShouldContain("[Compatible:");
        result.ShouldNotContain("[Unsafe:");
        result.ShouldNotContain("[RequiresUpdate:");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_TypeChangeNarrowing_ReturnsRequiresUpdateWithCastHint()
    {
        // NarrowlyConsumed() is consumed into an int context; long → int needs an explicit cast.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["NarrowlyConsumed"], changeKind: ChangeKind.TypeChange, newType: "long",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[RequiresUpdate:");
        result.ShouldContain("needs cast");
        result.ShouldContain("(int)");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_TypeChangeIncompatible_ReturnsUnsafe()
    {
        // string does not convert to the int consumer context at all.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["NarrowlyConsumed"], changeKind: ChangeKind.TypeChange, newType: "string",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Unsafe:");
        result.ShouldContain("does not convert");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_TypeChangePropertyWrite_ReturnsCompatible()
    {
        // Setting is written `surface.Setting = 1000`; the int literal still converts to long.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Setting"], changeKind: ChangeKind.TypeChange, newType: "long",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Compatible:");
        result.ShouldContain("assigned value");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_TypeChangeVarAdoption_CompatibleWithRippleNote()
    {
        // Untyped() flows into `var value = ...`; one-hop analysis can't trace the adopted type.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Untyped"], changeKind: ChangeKind.TypeChange, newType: "long",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Compatible:");
        result.ShouldContain("ripple");
        result.ShouldContain("Note:");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_TypeChangeOnType_ReturnsInlineError()
    {
        // A class has no "type" to change — TypeChange is rejected with an actionable error.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["ImpactSurface"], changeKind: ChangeKind.TypeChange, newType: "long",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("typed member");
    }

    // ── RemoveSymbol ───────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeChangeImpact_RemoveSymbol_AllSitesUnsafe()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["WidelyConsumed"], changeKind: ChangeKind.RemoveSymbol,
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Unsafe:");
        result.ShouldContain("will not compile");
        result.ShouldNotContain("[Compatible:");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_RemoveInterfaceMember_AddsOrphanNote()
    {
        // Removing IShape.Describe orphans its implementation in Shape.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Describe"], containingType: "IShape", changeKind: ChangeKind.RemoveSymbol,
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("orphaned");
    }

    // ── AccessibilityNarrow ────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeChangeImpact_AccessibilityNarrowToInternal_CrossAssemblyUnsafe()
    {
        // Shared() is referenced from TestFixture (same assembly) and TestFixture.Tests (second assembly).
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Shared"], containingType: "ImpactSurface",
            changeKind: ChangeKind.AccessibilityNarrow, newAccessibility: AccessibilityLevel.Internal,
            includeTests: true, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Compatible:"); // same-assembly references stay in scope
        result.ShouldContain("[Unsafe:"); // the second-assembly reference breaks
    }

    [Fact]
    public async Task AnalyzeChangeImpact_AccessibilityNarrowToInternal_FriendAssemblyCompatible()
    {
        // FriendVisible() is referenced only from TestFixture.Friend, which TestFixture grants
        // [InternalsVisibleTo]. Narrowing to internal keeps that friend reference in scope.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["FriendVisible"], containingType: "ImpactSurface",
            changeKind: ChangeKind.AccessibilityNarrow, newAccessibility: AccessibilityLevel.Internal,
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Compatible:"); // fails today: friend ref is wrongly "[Unsafe:"
        result.ShouldNotContain("[Unsafe:");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_AccessibilityNarrowToProtectedInternal_FriendAssemblyCompatible()
    {
        // protected internal = internal OR derived; the friend reference satisfies the internal arm
        // via [InternalsVisibleTo] even though FriendConsumer derives from nothing.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["FriendVisible"], containingType: "ImpactSurface",
            changeKind: ChangeKind.AccessibilityNarrow, newAccessibility: AccessibilityLevel.ProtectedInternal,
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Compatible:");
        result.ShouldNotContain("[Unsafe:");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_AccessibilityNarrowToPrivateProtected_FriendButNotDerivedUnsafe()
    {
        // private protected = internal AND derived; the friend reference passes the internal arm but
        // FriendConsumer is not derived from ImpactSurface, so access breaks.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["FriendVisible"], containingType: "ImpactSurface",
            changeKind: ChangeKind.AccessibilityNarrow, newAccessibility: AccessibilityLevel.PrivateProtected,
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Unsafe:");
        result.ShouldNotContain("[Compatible:");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_AccessibilityNarrowToPrivate_OnlySameTypeCompatible()
    {
        // private → only the same-type caller (ImpactSurface.SameTypeUser) survives.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Shared"], containingType: "ImpactSurface",
            changeKind: ChangeKind.AccessibilityNarrow, newAccessibility: AccessibilityLevel.Private,
            includeTests: true, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("1 compatible");
        result.ShouldContain("2 unsafe");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_AccessibilityNarrowNotNarrower_ReturnsInlineError()
    {
        // Shared is public; "narrowing" to public is not a narrowing.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Shared"], containingType: "ImpactSurface",
            changeKind: ChangeKind.AccessibilityNarrow, newAccessibility: AccessibilityLevel.Public,
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("not strictly narrower");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_AccessibilityNarrowWithoutTarget_ReturnsInlineError()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Shared"], containingType: "ImpactSurface",
            changeKind: ChangeKind.AccessibilityNarrow,
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("requires newAccessibility");
    }

    // ── SignatureChange ────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeChangeImpact_SignatureChange_AllCallSitesRequiresUpdate()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Shared"], containingType: "ImpactSurface", changeKind: ChangeKind.SignatureChange,
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[RequiresUpdate:");
        result.ShouldContain("coarse");
        result.ShouldNotContain("[Compatible:");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignatureChange_MethodGroupCounted_NameofDropped()
    {
        // Callback is referenced as a method group (Func<int,int> AsDelegate() => Callback) and via
        // nameof(Callback). A signature change can break the method-group conversion, so it must be
        // counted; the nameof operand is a compile-time string and stays dropped.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Callback"], containingType: "MethodGroupSurface",
            changeKind: ChangeKind.SignatureChange, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[RequiresUpdate:"); // fails today: "No impacted sites found …"
        result.ShouldContain("method-group");
        result.ShouldContain("1 requires-update"); // exactly the method group; nameof excluded
        result.ShouldNotContain("[Compatible:");
    }

    // ── Validation & batching ──────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeChangeImpact_TypeChangeWithoutNewType_ReturnsInlineError()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["WidelyConsumed"], changeKind: ChangeKind.TypeChange,
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("requires newType");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_Batch_MultipleNames_ProducesSectionedOutput()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["WidelyConsumed", "NarrowlyConsumed"], changeKind: ChangeKind.TypeChange,
            newType: "long", ct: TestContext.Current.CancellationToken);

        result.ShouldContain("=== WidelyConsumed ===");
        result.ShouldContain("=== NarrowlyConsumed ===");
        result.ShouldContain("[Compatible:"); // WidelyConsumed
        result.ShouldContain("[RequiresUpdate:"); // NarrowlyConsumed
    }

    [Fact]
    public async Task AnalyzeChangeImpact_WithProject_NarrowsResolution()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Shared"], containingType: "ImpactSurface", changeKind: ChangeKind.SignatureChange,
            project: "TestFixture", ct: TestContext.Current.CancellationToken);

        result.ShouldContain("Impact of SignatureChange on 'Shared'");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_CursorMode_WithProject_IgnoresFilterAndNotes()
    {
        // Arrange — IShape at line 6, col 18, used in TestFixture; TestFixture.Minimal has none.
        // A cursor already targets one symbol, so `project` has no resolution role — the blast
        // radius must cover all projects (the old result-filter would have emptied it), with a note.
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.AnalyzeChangeImpact(
            [Loc(filePath, 6, 18)], changeKind: ChangeKind.SignatureChange,
            project: "TestFixture.Minimal", maxResults: null, ct: TestContext.Current.CancellationToken);

        // Assert — impact spans TestFixture (the filter did not narrow it) and the note explains why.
        result.ShouldContain("Impact of SignatureChange on 'IShape'");
        result.ShouldContain("[TestFixture]");
        result.ShouldContain("project filter ignored");
    }
}
