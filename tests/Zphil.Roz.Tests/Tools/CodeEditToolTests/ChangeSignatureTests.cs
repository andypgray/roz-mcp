using Zphil.Roz.Enums;
using Zphil.Roz.Resources;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     <c>change_signature</c>: applies the deterministic subset (add-with-default, remove-unused,
///     reorder-with-named-args) across a method's family and call sites, and refuses (writing nothing) on
///     any unsafe or un-rewritable site. On-disk byte assertions via <see cref="EditTestBase" />.
/// </summary>
public class ChangeSignatureTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    private string SurfaceFile =>
        Path.Combine(Fixture.WorkspaceManager.SolutionDirectory!, "TestFixture", "Services", "SignatureChangeSurface.cs");

    private string FamilyFile =>
        Path.Combine(Fixture.WorkspaceManager.SolutionDirectory!, "TestFixture", "Services", "SignatureChangeFamily.cs");

    private string CrossAssemblyFile =>
        Path.Combine(Fixture.WorkspaceManager.SolutionDirectory!, "TestFixture.Tests", "SignatureCrossAssemblyConsumer.cs");

    private string MultiTfmFile =>
        Path.Combine(Fixture.WorkspaceManager.SolutionDirectory!, "TestFixture.MultiTfm", "SigMultiTfmSurface.cs");

    [Fact]
    public async Task ChangeSignature_AddTrailingOptional_RewritesDeclaration()
    {
        CodeEditTools tools = CreateEditTools(Fixture);

        string result = await tools.ChangeSignature(
            SurfaceFile, "Greet", "(string name, int count = 5)", "SignatureChangeSurface",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("Changed signature of 'Greet'");
        string content = await File.ReadAllTextAsync(SurfaceFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Greet(string name, int count = 5)");
        // A trailing optional leaves every existing call site valid — none is rewritten.
        result.ShouldContain("0 call site(s)");
    }

    [Fact]
    public async Task ChangeSignature_RemoveUnusedParam_DropsArg()
    {
        CodeEditTools tools = CreateEditTools(Fixture);

        // Prune has an in-project-only consumer, so this exercises remove-drops-arg without a
        // cross-assembly census (kept separate from the cross-assembly test's target).
        await tools.ChangeSignature(
            SurfaceFile, "Prune", "(string text)", "SignatureChangeSurface",
            ct: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(SurfaceFile, TestContext.Current.CancellationToken);
        content.ShouldContain("public int Prune(string text)");
        content.ShouldContain("surface.Prune(\"z\")");
        content.ShouldNotContain("surface.Prune(\"z\", 5)");
    }

    [Fact]
    public async Task ChangeSignature_ReorderDifferentTypes_AddsNamesPreservesOrder()
    {
        CodeEditTools tools = CreateEditTools(Fixture);

        await tools.ChangeSignature(
            SurfaceFile, "Rank", "(string label, int score)", "SignatureChangeSurface",
            ct: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(SurfaceFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Rank(string label, int score)");
        // Names added, original left-to-right expression order (1 then "top") preserved.
        content.ShouldContain("surface.Rank(score: 1, label: \"top\")");
    }

    [Fact]
    public async Task ChangeSignature_DryRun_WritesNothing()
    {
        CodeEditTools tools = CreateEditTools(Fixture);
        byte[] before = await File.ReadAllBytesAsync(SurfaceFile, TestContext.Current.CancellationToken);

        string result = await tools.ChangeSignature(
            SurfaceFile, "Greet", "(string name, int count = 5)", "SignatureChangeSurface",
            verify: VerifyMode.DryRun, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("DRY RUN — no files written.");
        (await File.ReadAllBytesAsync(SurfaceFile, TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    [Fact]
    public async Task ChangeSignature_Delta_CommitsAndReportsCleanDelta()
    {
        CodeEditTools tools = CreateEditTools(Fixture);

        string result = await tools.ChangeSignature(
            SurfaceFile, "Greet", "(string name, int count = 5)", "SignatureChangeSurface",
            verify: VerifyMode.Delta, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("Verification:");
        result.ShouldContain("no new errors");
        (await File.ReadAllTextAsync(SurfaceFile, TestContext.Current.CancellationToken))
            .ShouldContain("int count = 5");
    }

    [Fact]
    public async Task ChangeSignature_UnsafeSite_RefusesWithBlockerList()
    {
        CodeEditTools tools = CreateEditTools(Fixture);
        byte[] before = await File.ReadAllBytesAsync(SurfaceFile, TestContext.Current.CancellationToken);

        // Reordering two same-type params silently rebinds the positional MovePositional() call.
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.ChangeSignature(
            SurfaceFile, "Move", "(int y, int x)", "SignatureChangeSurface",
            ct: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("refused");
        ex.Message.ShouldContain("bind to different parameters");
        ex.Message.ShouldContain("SignatureChangeSurface.cs:");
        ex.Message.ShouldContain(RozResources.EditingGuideUri);
        // Nothing written — the file is byte-identical.
        (await File.ReadAllBytesAsync(SurfaceFile, TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    [Fact]
    public async Task ChangeSignature_RemoveUsedParam_Refuses()
    {
        CodeEditTools tools = CreateEditTools(Fixture);

        // Log's body uses `level` (msg.Length + level), so it cannot be dropped.
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.ChangeSignature(
            SurfaceFile, "Log", "(string msg)", "SignatureChangeSurface",
            ct: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("used in the body");
        ex.Message.ShouldContain("level");
    }

    [Fact]
    public async Task ChangeSignature_DropSideEffectingArg_Refuses()
    {
        CodeEditTools tools = CreateEditTools(Fixture);

        // EmitCall() passes Emit(1, Next()); dropping Next() (a method call) is not side-effect-free.
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.ChangeSignature(
            SurfaceFile, "Emit", "(int keep)", "SignatureChangeSurface",
            ct: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("side effect");
        ex.Message.ShouldContain("Next()");
    }

    [Fact]
    public async Task ChangeSignature_Retype_Refuses()
    {
        CodeEditTools tools = CreateEditTools(Fixture);

        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.ChangeSignature(
            SurfaceFile, "Widen", "(int value)", "SignatureChangeSurface",
            ct: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("analysis-only in v1");
    }

    [Fact]
    public async Task ChangeSignature_TestProjectCallSite_IsRewritten()
    {
        CodeEditTools tools = CreateEditTools(Fixture);

        // The apply-gate census keeps test projects (excludeTests=false), so the cross-assembly
        // UseTrim() call site in TestFixture.Tests is rewritten too.
        await tools.ChangeSignature(
            SurfaceFile, "Trim", "(string text)", "SignatureChangeSurface",
            ct: TestContext.Current.CancellationToken);

        string crossContent = await File.ReadAllTextAsync(CrossAssemblyFile, TestContext.Current.CancellationToken);
        crossContent.ShouldContain("Trim(\"x\")");
        crossContent.ShouldNotContain("Trim(\"x\", 9)");
    }

    [Fact]
    public async Task ChangeSignature_XmlDocParamSync_AddsPlaceholder()
    {
        CodeEditTools tools = CreateEditTools(Fixture);

        await tools.ChangeSignature(
            SurfaceFile, "Greet", "(string name, int count = 5)", "SignatureChangeSurface",
            ct: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(SurfaceFile, TestContext.Current.CancellationToken);
        content.ShouldContain("<param name=\"count\">");
    }

    [Fact]
    public async Task ChangeSignature_MultiTfm_RewritesDeclarationFile()
    {
        CodeEditTools tools = CreateEditTools(Fixture);

        // SigMultiTfmSurface is compiled under net8.0 and net10.0; the one physical declaration file must
        // be rewritten (BuildForkAsync covers every DocumentId at the path).
        await tools.ChangeSignature(
            MultiTfmFile, "Scale", "(int value, int factor = 1)", "SigMultiTfmSurface",
            ct: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(MultiTfmFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Scale(int value, int factor = 1)");
    }

    [Fact]
    public async Task ChangeSignature_InterfaceFamily_RewritesInterfaceAndImplementation()
    {
        CodeEditTools tools = CreateEditTools(Fixture);

        await tools.ChangeSignature(
            FamilyFile, "Do", "(int n, int extra = 0)", "ISigSurface",
            ct: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(FamilyFile, TestContext.Current.CancellationToken);
        content.ShouldContain("int Do(int n, int extra = 0)"); // interface declaration
        content.ShouldContain("public int Do(int n, int extra = 0)"); // implementation
    }

    [Fact]
    public async Task ChangeSignature_InterfaceFamily_RemoveUnused_RewritesDispatchCallSites()
    {
        // Anchored at the CONCRETE implementation while the call sites bind the interface member —
        // the planner must map the delta by parameter ordinal across the slot family, not by the
        // anchor's parameter symbol identity (which bogus-blocked every dispatch site as a
        // params-expanded call — found by the nopCommerce change_signature gate stress test).
        CodeEditTools tools = CreateEditTools(Fixture);

        await tools.ChangeSignature(
            FamilyFile, "Trim", "(string text)", "SigSurfaceImpl",
            ct: TestContext.Current.CancellationToken);

        string content = await File.ReadAllTextAsync(FamilyFile, TestContext.Current.CancellationToken);
        content.ShouldContain("int Trim(string text);"); // interface declaration
        content.ShouldContain("public int Trim(string text)"); // implementation
        content.ShouldContain("s.Trim(\"abc\")"); // interface-dispatch call: arg dropped
        content.ShouldContain("impl.Trim(\"abcd\")"); // concrete call: arg dropped
    }

    [Fact]
    public async Task ChangeSignature_OnProperty_RefusesNamingScope()
    {
        CodeEditTools tools = CreateEditTools(Fixture);

        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.ChangeSignature(
            FamilyFile, "Id", "(int x)", "SigCtorBase",
            ct: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("method or constructor");
    }

    [Fact]
    public async Task ChangeSignature_SameSignature_NoOp()
    {
        CodeEditTools tools = CreateEditTools(Fixture);
        byte[] before = await File.ReadAllBytesAsync(SurfaceFile, TestContext.Current.CancellationToken);

        string result = await tools.ChangeSignature(
            SurfaceFile, "Greet", "(string name)", "SignatureChangeSurface",
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("matches the current one");
        (await File.ReadAllBytesAsync(SurfaceFile, TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    [Fact]
    public async Task ChangeSignature_NestedCallSites_Refuses()
    {
        CodeEditTools tools = CreateEditTools(Fixture);
        byte[] before = await File.ReadAllBytesAsync(SurfaceFile, TestContext.Current.CancellationToken);

        // WrapNested() calls Wrap(Wrap("a", 1), 2) — each plan derives from the base tree, so applying
        // the outer rewrite would silently discard the inner one; the pair must refuse instead.
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.ChangeSignature(
            SurfaceFile, "Wrap", "(string s)", "SignatureChangeSurface",
            ct: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("refused");
        ex.Message.ShouldContain("nests inside another rewritten call site");
        (await File.ReadAllBytesAsync(SurfaceFile, TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    [Fact]
    public async Task ChangeSignature_GeneratedCallSite_RefusesNamingFile()
    {
        CodeEditTools tools = CreateEditTools(Fixture);
        byte[] before = await File.ReadAllBytesAsync(SurfaceFile, TestContext.Current.CancellationToken);

        // Stamp's only consumer lives in SignatureChangeGenerated.g.cs; even a Compatible-everywhere
        // add-optional refuses, because a generated file must be regenerated, not edited.
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.ChangeSignature(
            SurfaceFile, "Stamp", "(int a, int b, int extra = 0)", "SignatureChangeSurface",
            ct: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("generated file");
        ex.Message.ShouldContain("SignatureChangeGenerated.g.cs");
        (await File.ReadAllBytesAsync(SurfaceFile, TestContext.Current.CancellationToken)).ShouldBe(before);
    }
}
