using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.ReferenceToolTests;

/// <summary>
///     Precise <c>SignatureChange</c> (analyze_change_impact with <c>newSignature</c>): per-site
///     Compatible/RequiresUpdate/Unsafe verdicts via real overload resolution in a forked solution.
/// </summary>
public class AnalyzeChangeImpactSignatureTests(WorkspaceFixture fixture)
{
    private readonly ReferenceTools tools = CreateReferenceTools(fixture);

    private string SurfaceFile =>
        Path.Combine(fixture.WorkspaceManager.SolutionDirectory!, "TestFixture", "Services", "SignatureChangeSurface.cs");

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_AddTrailingOptional_AllCompatible()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Greet"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(string name, int count = 5)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("Impact of SignatureChange on 'Greet' → (string name, int count = 5)");
        result.ShouldContain("[Compatible:");
        result.ShouldNotContain("[Unsafe:");
        result.ShouldNotContain("[RequiresUpdate:");
        result.ShouldNotContain("coarse");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_AddRequiredParam_RequiresUpdate()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Greet"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(string name, int count)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[RequiresUpdate:");
        result.ShouldContain("required parameter 'count'");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_ReorderSameTypeParams_Unsafe()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Move"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(int y, int x)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Unsafe:");
        result.ShouldContain("silently bind to different parameters");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_ReorderNamedArgs_Compatible()
    {
        // MoveNamed() calls Move(x: 1, y: 2) — a fully-named site is reorder-safe.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Move"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(int y, int x)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Compatible:"); // the named site survives the reorder
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_RemoveParam_RequiresUpdate_ShowsDroppedArg()
    {
        // LogPassed() calls Log("a", 3); removing level drops the 3.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Log"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(string msg)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[RequiresUpdate:");
        result.ShouldContain("update call to:");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_Nameof_Compatible()
    {
        // PingGroup() (method group) + PingNameof() (nameof). Keeping types identical → both Compatible.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Ping"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(int n)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Compatible:");
        result.ShouldContain("nameof");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_MethodGroupChangedTypes_Unsafe()
    {
        // Ping referenced as Action<int>; changing the parameter type breaks the delegate conversion.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Ping"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(long n)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Unsafe:");
        result.ShouldContain("method-group");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_MalformedDescriptor_Error()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Greet"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(int a) bogus",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("unexpected text");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_BatchMultipleNames_Error()
    {
        // The batch-arity guard runs ahead of BatchOrSingle's per-item fan-out, so it throws directly
        // (surfaced as IsError by GlobalCallToolFilter in the real pipeline) rather than inline.
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.AnalyzeChangeImpact(
            symbolNames: ["Greet", "Move"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(string name, int count = 5)",
            ct: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("one method");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_NewSignatureWithTypeChangeKind_Error()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Greet"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.TypeChange, newSignature: "(string name)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("newSignature requires changeKind=SignatureChange");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_NewTypeWithSignatureChangeKind_Error()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Greet"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newType: "long",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("newType requires changeKind=TypeChange");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_RetypeExplicit_RequiresUpdateCastHint()
    {
        // WidenCall() passes a long to Widen(long); retyping to int needs an explicit (narrowing) cast.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Widen"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(int value)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[RequiresUpdate:");
        result.ShouldContain("explicit cast");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_RetypeIncompatible_Unsafe()
    {
        // A long argument does not convert to string at all.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Widen"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(string value)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Unsafe:");
        result.ShouldContain("does not convert");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_OverloadCollision_Unsafe()
    {
        // Cursor on Handle(int); retyping its parameter to string retargets Handle(1) to Handle(long).
        string result = await tools.AnalyzeChangeImpact(
            [Loc(SurfaceFile, 27)],
            changeKind: ChangeKind.SignatureChange, newSignature: "(string value)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Unsafe:");
        result.ShouldContain("silently retargets");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_InterfaceMember_ClassifiesSites()
    {
        // Adding a trailing optional keeps interface- and concrete-typed call sites Compatible.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Do"], containingType: "ISigSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(int n, int extra = 0)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("Impact of SignatureChange on 'Do'");
        result.ShouldContain("[Compatible:");
        result.ShouldNotContain("[Unsafe:");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_OverrideTarget_EmitsLockstepNote()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Calc"], containingType: "SigDerived",
            changeKind: ChangeKind.SignatureChange, newSignature: "(int a, int b = 0)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("lockstep");
        result.ShouldContain("Calc"); // names the base member
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_MetadataSlot_Error()
    {
        // ToString overrides object.ToString (metadata) — the external contract can't be modeled.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["ToString"], containingType: "SigMetaOverride",
            changeKind: ChangeKind.SignatureChange, newSignature: "()",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("metadata");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_MultiTfmConsumer_ClassifiedOnce()
    {
        // Scale is compiled under net8.0 and net10.0; the shared call site must be classified once.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Scale"], containingType: "SigMultiTfmSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(int value, int factor = 1)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("1 site(s)");
        result.ShouldContain("[Compatible:");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_RemovedThenAddedSameType_SuggestsRename()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Greet"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(string label)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("rename_symbol");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_AmbiguousOverloads_ErrorListsParamLists()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Handle"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(int value, int extra = 0)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("overloads");
        result.ShouldContain(":line:col");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_UnresolvableNewType_Error()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Greet"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(NonExistentType name)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("Could not resolve type");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_OnProperty_ErrorNamesScope()
    {
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Id"], containingType: "SigCtorBase",
            changeKind: ChangeKind.SignatureChange, newSignature: "(int x)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("method or constructor");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_CtorInitializerBaseCall_Classified()
    {
        // Census must include the : base(id) initializer plus new SigCtorBase(5); adding an optional keeps both Compatible.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: [".ctor"], containingType: "SigCtorBase",
            changeKind: ChangeKind.SignatureChange, newSignature: "(int id, int version = 1)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("2 site(s)");
        result.ShouldContain("[Compatible:");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_ParamsAffected_Conservative()
    {
        // Adding a parameter before a params array touches params — conservative, no silent rewrite.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Sum"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(int header, params int[] xs)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("params");
        result.ShouldNotContain("update call to:");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_MethodGroupSameTypes_Compatible()
    {
        // Rename-shape delta: the ordered parameter types stay (int), so the Action<int> conversion holds.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Ping"], containingType: "SignatureChangeSurface",
            changeKind: ChangeKind.SignatureChange, newSignature: "(int m)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Compatible:");
        result.ShouldContain("conversion holds");
        result.ShouldNotContain("[Unsafe:");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_ExtensionAddTrailingOptional_ReducedSiteCompatible()
    {
        // A descriptor is naturally written without `this`; the fork must keep Tag an extension method
        // or the reduced site s.Tag("x") false-fails its re-bind and reads as Unsafe.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Tag"], containingType: "SigExtensions",
            changeKind: ChangeKind.SignatureChange, newSignature: "(ISigSurface s, string t, string suffix = \"\")",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Compatible:");
        result.ShouldNotContain("[Unsafe:");
        result.ShouldNotContain("[RequiresUpdate:");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_SignaturePrecise_ReducedExtensionTouchesReceiver_Conservative()
    {
        // Moving the receiver un-extensions the reduced site s.Tag("x") — conservative verdict, no rewrite.
        string result = await tools.AnalyzeChangeImpact(
            symbolNames: ["Tag"], containingType: "SigExtensions",
            changeKind: ChangeKind.SignatureChange, newSignature: "(string t, ISigSurface s)",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[RequiresUpdate:");
        result.ShouldContain("touches the receiver");
    }
}
