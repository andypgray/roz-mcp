using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Models;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Covers the <c>verify</c> parameter (DryRun / Delta) on the mutating tools: the compiler-error
///     delta, the DryRun "write nothing" guarantee, cross-project blast radius, the no-op skip, and the
///     byte-identical <c>verify=None</c> path.
/// </summary>
public class VerifiedEditTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    private static string ImpactSurfaceFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Services", "ImpactSurface.cs");

    private static string FriendConsumerFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture.Friend", "FriendConsumer.cs");

    private static async Task<string> DocTextAsync(ITestWorkspace ws, string filePath)
    {
        Solution solution = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Document? doc = solution.GetDocumentByPath(filePath);
        SourceText text = await doc!.GetTextAsync(TestContext.Current.CancellationToken);
        return text.ToString();
    }

    [Fact]
    public async Task EditSymbol_DryRun_IntroducesError_ReportsDeltaAndLeavesDiskAndWorkspaceUntouched()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        byte[] before = await File.ReadAllBytesAsync(circleFile, TestContext.Current.CancellationToken);

        // Act — replace Area with a body that references an undefined name (CS0103), dry-run only.
        string result = await tools.EditSymbol(
            [new EditSymbolRequest(EditSymbolAction.Replace, circleFile, "Area", "public override double Area => Math.PI * Bogus;")],
            VerifyMode.DryRun, ct: TestContext.Current.CancellationToken);

        // Assert — the delta names the introduced error, banner marks it a dry run.
        result.ShouldContain("DRY RUN — no files written.");
        result.ShouldContain("new error");
        result.ShouldContain("CS0103");

        // Disk is untouched — byte-for-byte (a text compare would mask BOM/line-ending drift).
        byte[] afterDisk = await File.ReadAllBytesAsync(circleFile, TestContext.Current.CancellationToken);
        afterDisk.ShouldBe(before);

        // Workspace is untouched — the fork never reached CurrentSolution.
        string afterWorkspace = await DocTextAsync(Fixture, circleFile);
        afterWorkspace.ShouldContain("Math.PI * Radius * Radius");
        afterWorkspace.ShouldNotContain("Bogus");
    }

    [Fact]
    public async Task EditSymbol_Delta_CleanEdit_CommitsAndReportsNoNewErrors()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — a valid edit with Delta: commit, then verify.
        string result = await tools.EditSymbol(
            [new EditSymbolRequest(EditSymbolAction.Replace, circleFile, "Radius", "public double Radius { get; } = radius * 1.0;")],
            VerifyMode.Delta, ct: TestContext.Current.CancellationToken);

        // Assert — committed (disk changed) and reported clean.
        result.ShouldContain("Verification: no new errors");
        result.ShouldContain("scope:");
        result.ShouldContain("TestFixture");

        string afterDisk = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        afterDisk.ShouldContain("radius * 1.0");
    }

    [Fact]
    public async Task EditSymbol_Delta_IntroducesError_StillCommits_ReportDontPolice()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — Delta commits BEFORE verifying, so a breaking edit still lands on disk.
        string result = await tools.EditSymbol(
            [new EditSymbolRequest(EditSymbolAction.Replace, circleFile, "Area", "public override double Area => Math.PI * Bogus;")],
            VerifyMode.Delta, ct: TestContext.Current.CancellationToken);

        // Assert — reported the break AND wrote it (report, don't police — no auto-revert).
        result.ShouldContain("new error");
        result.ShouldContain("CS0103");
        result.ShouldNotContain("DRY RUN");

        string afterDisk = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        afterDisk.ShouldContain("Bogus");
    }

    [Fact]
    public async Task EditSymbol_DryRun_CrossProjectBreak_SurfacesInDependentProject()
    {
        // Arrange — ImpactSurface.FriendVisible() is consumed only by TestFixture.Friend.
        CodeEditTools tools = CreateEditTools(Fixture);
        string impactFile = ImpactSurfaceFile(Fixture);
        string friendFile = FriendConsumerFile(Fixture);
        byte[] impactBefore = await File.ReadAllBytesAsync(impactFile, TestContext.Current.CancellationToken);
        byte[] friendBefore = await File.ReadAllBytesAsync(friendFile, TestContext.Current.CancellationToken);

        // Act — removing it breaks FriendConsumer in the dependent project.
        string result = await tools.EditSymbol(
            [new EditSymbolRequest(EditSymbolAction.Remove, impactFile, "FriendVisible")],
            VerifyMode.DryRun, ct: TestContext.Current.CancellationToken);

        // Assert — the break is attributed to the dependent project and its file.
        result.ShouldContain("new error");
        result.ShouldContain("FriendConsumer.cs");

        // The scope line itself must name the dependent project — independently of the error row,
        // whose file path also happens to contain the "TestFixture.Friend" substring.
        string headline = result.Replace("\r\n", "\n").Split('\n')
            .First(l => l.StartsWith("Verification:", StringComparison.Ordinal));
        headline.ShouldContain("TestFixture.Friend");

        // Nothing was written to either file — byte-for-byte.
        (await File.ReadAllBytesAsync(impactFile, TestContext.Current.CancellationToken)).ShouldBe(impactBefore);
        (await File.ReadAllBytesAsync(friendFile, TestContext.Current.CancellationToken)).ShouldBe(friendBefore);
    }

    [Fact]
    public async Task ReplaceContent_Delta_FixingError_ReportsResolved()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Step 1 (verify=None): commit a break so the baseline carries the error.
        await tools.ReplaceContent(
            [new ReplaceContentRequest(circleFile, "Math.PI * Radius * Radius", "Math.PI * Bogus")],
            ct: TestContext.Current.CancellationToken);

        // Act — Step 2 (verify=Delta): fix it; the delta should show one resolved error.
        string result = await tools.ReplaceContent(
            [new ReplaceContentRequest(circleFile, "Math.PI * Bogus", "Math.PI * Radius * Radius")],
            VerifyMode.Delta, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("no new errors");
        result.ShouldContain("-1 resolved");
    }

    [Fact]
    public async Task ReplaceContent_DryRun_NoOp_SkipsDelta()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — search == replace is a guaranteed no-op; nothing is staged.
        string result = await tools.ReplaceContent(
            [new ReplaceContentRequest(circleFile, "Radius", "Radius")],
            VerifyMode.DryRun, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Verification skipped — no content changed.");
    }

    [Fact]
    public async Task EditSymbol_None_OutputExactlyMatchesBareOpFormat()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — the default (None) path must render the bare op output, no verification text.
        string result = await tools.EditSymbol(
            [new EditSymbolRequest(EditSymbolAction.Replace, circleFile, "Radius", "public double Radius { get; } = radius * 1.0;")],
            ct: TestContext.Current.CancellationToken);

        // Assert — exact-match pin: any None-path drift (prepend residue, trims, added notes) fails here.
        result.Replace('\\', '/').ShouldBe(
            "Replaced 'Radius' (1 -> 1 lines)\n" +
            "File: TestFixture/Shapes/Circle.cs\n" +
            "Starting at line 5.");
    }

    [Fact]
    public async Task EditSymbol_DryRun_BatchWithMidBatchUserError_ComputesDeltaOnce()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        byte[] before = await File.ReadAllBytesAsync(circleFile, TestContext.Current.CancellationToken);

        // Act — a valid remove, a user-error op (symbol not found), and another valid remove.
        // The delta must be computed once over the successful ops; the failed op stages nothing.
        string result = await tools.EditSymbol(
        [
            new EditSymbolRequest(EditSymbolAction.Remove, circleFile, "Perimeter"),
            new EditSymbolRequest(EditSymbolAction.Remove, circleFile, "NoSuchSymbol"),
            new EditSymbolRequest(EditSymbolAction.Remove, circleFile, "Area")
        ], VerifyMode.DryRun, ct: TestContext.Current.CancellationToken);

        // Assert — exactly one dry-run banner (single batch-level delta), and the user error is inline.
        result.Split("DRY RUN — no files written.").Length.ShouldBe(2);
        result.ShouldContain("not found");

        // Disk untouched (dry run) — byte-for-byte.
        (await File.ReadAllBytesAsync(circleFile, TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    [Fact]
    public async Task EditSymbol_DryRun_MultiTfm_DedupesErrorAcrossFrameworks()
    {
        // Arrange — TestFixture.MultiTfm targets net8.0 AND net10.0; one physical file, two compilations.
        CodeEditTools tools = CreateEditTools(Fixture);
        string serviceFile = MultiTfmFile(Fixture, "MultiTfmService.cs");
        byte[] before = await File.ReadAllBytesAsync(serviceFile, TestContext.Current.CancellationToken);

        // Act — break Calculate; both TFMs report the same CS0103, which must dedupe to one row.
        string result = await tools.EditSymbol(
            [new EditSymbolRequest(EditSymbolAction.Replace, serviceFile, "Calculate", "public int Calculate(int x, int y) => Bogus;")],
            VerifyMode.DryRun, ct: TestContext.Current.CancellationToken);

        // Assert — a single CS0103 error line, scope collapses the multi-TFM project to one name.
        result.ShouldContain("+1 new error");
        result.ShouldContain("CS0103");
        result.ShouldContain("TestFixture.MultiTfm");

        // Disk untouched (dry run) — byte-for-byte.
        (await File.ReadAllBytesAsync(serviceFile, TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    [Fact]
    public async Task ReplaceContent_NonWorkspaceFile_NotWrittenOnDryRun_WrittenOnDelta_WithNote()
    {
        // Arrange — a .cs file on disk but outside every loaded project (no delta coverage).
        CodeEditTools tools = CreateEditTools(Fixture);
        string scratchFile = Path.Combine(Fixture.WorkspaceManager.SolutionDirectory!, "ScratchOutsideSolution.cs");
        await File.WriteAllTextAsync(scratchFile, "// marker ALPHA\n", TestContext.Current.CancellationToken);
        byte[] scratchBefore = await File.ReadAllBytesAsync(scratchFile, TestContext.Current.CancellationToken);

        // Act 1 — DryRun must not write, but must flag the uncovered file.
        string dryRun = await tools.ReplaceContent(
            [new ReplaceContentRequest(scratchFile, "ALPHA", "BRAVO")],
            VerifyMode.DryRun, ct: TestContext.Current.CancellationToken);

        // Assert 1
        dryRun.ShouldContain("no delta coverage");
        dryRun.ShouldContain("ScratchOutsideSolution.cs");
        (await File.ReadAllBytesAsync(scratchFile, TestContext.Current.CancellationToken)).ShouldBe(scratchBefore);

        // Act 2 — Delta writes the file (it just carries no compiler coverage).
        string delta = await tools.ReplaceContent(
            [new ReplaceContentRequest(scratchFile, "ALPHA", "BRAVO")],
            VerifyMode.Delta, ct: TestContext.Current.CancellationToken);

        // Assert 2
        delta.ShouldContain("no delta coverage");
        (await File.ReadAllTextAsync(scratchFile, TestContext.Current.CancellationToken)).ShouldContain("BRAVO");
    }

    [Fact]
    public async Task RenameSymbol_DryRun_WritesNothing_DefersFileRename()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string renamedFile = Path.Combine(Path.GetDirectoryName(circleFile)!, "CircleRenamed.cs");
        string shapeServiceFile = Path.Combine(
            Fixture.WorkspaceManager.SolutionDirectory!, "TestFixture", "Services", "ShapeService.cs");
        byte[] circleBefore = await File.ReadAllBytesAsync(circleFile, TestContext.Current.CancellationToken);
        byte[] shapeServiceBefore = await File.ReadAllBytesAsync(shapeServiceFile, TestContext.Current.CancellationToken);

        // Act — rename the Circle type with renameFile, dry-run only.
        string result = await tools.RenameSymbol(
            circleFile, "Circle", "CircleRenamed",
            renameFile: true, verify: VerifyMode.DryRun, ct: TestContext.Current.CancellationToken);

        // Assert — worded as a preview, banner present, file rename deferred to a note.
        result.ShouldContain("Would rename 'Circle' to 'CircleRenamed'");
        result.ShouldContain("DRY RUN — no files written.");
        result.ShouldContain("CircleRenamed.cs");
        result.ShouldContain("applies on commit only");

        // Nothing was moved or written — byte-for-byte across the declaring and a referencing file.
        File.Exists(renamedFile).ShouldBeFalse();
        (await File.ReadAllBytesAsync(circleFile, TestContext.Current.CancellationToken)).ShouldBe(circleBefore);
        (await File.ReadAllBytesAsync(shapeServiceFile, TestContext.Current.CancellationToken)).ShouldBe(shapeServiceBefore);
    }

    [Fact]
    public async Task RenameSymbol_Delta_CleanRename_CommitsAndReportsNoNewErrors()
    {
        // Arrange — closes the gap of no rename-Delta coverage. Renaming a property updates every
        // in-solution reference, so the delta is clean.
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — rename Circle.Radius with Delta: commit first, then diff.
        string result = await tools.RenameSymbol(
            Loc(circleFile, 5, 19), "Radius", "Rad", verify: VerifyMode.Delta, ct: TestContext.Current.CancellationToken);

        // Assert — worded as applied (committed), reported clean, no dry-run banner.
        result.ShouldContain("Renamed");
        result.ShouldContain("Verification: no new errors");
        result.ShouldNotContain("DRY RUN");

        string afterDisk = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        afterDisk.ShouldContain("public double Rad");
        afterDisk.ShouldNotContain("Radius");
    }

    [Fact]
    public async Task RenameSymbol_Delta_WithFileRename_MovesFileAndKeepsDelta()
    {
        // Arrange — pins the FinalizeForkAsync → file-move ordering: the delta is computed on the fork
        // BEFORE the physical move + reload, so both the delta block and the move must be present.
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string renamedFile = Path.Combine(Path.GetDirectoryName(circleFile)!, "CircleRenamed.cs");

        // Act — rename the Circle type with renameFile under Delta.
        string result = await tools.RenameSymbol(
            circleFile, "Circle", "CircleRenamed",
            renameFile: true, verify: VerifyMode.Delta, ct: TestContext.Current.CancellationToken);

        // Assert — committed, clean delta present, and the changed list names the new path.
        result.ShouldContain("Renamed 'Circle' to 'CircleRenamed'");
        result.ShouldContain("Verification: no new errors");
        result.ShouldNotContain("DRY RUN");
        result.ShouldContain("CircleRenamed.cs");

        // The file was physically moved.
        File.Exists(renamedFile).ShouldBeTrue();
        File.Exists(circleFile).ShouldBeFalse();
    }

    [Fact]
    public async Task RenameSymbol_DryRun_StrayReference_StillFlagged()
    {
        // Arrange — plant a stray referencing Radius outside any project's compile globs (pattern from
        // RenameSymbolStrayReferenceTests). The stray scan must run under DryRun too, not just None.
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string strayFile = Path.Combine(Fixture.WorkspaceManager.SolutionDirectory!, "Strays", "StrayConsumer.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(strayFile)!);
        await File.WriteAllTextAsync(strayFile, "var Radius = 5.0;\n", TestContext.Current.CancellationToken);
        byte[] circleBefore = await File.ReadAllBytesAsync(circleFile, TestContext.Current.CancellationToken);

        // Act — DryRun rename of the Radius property.
        string result = await tools.RenameSymbol(
            Loc(circleFile, 5, 19), "Radius", "R", verify: VerifyMode.DryRun, ct: TestContext.Current.CancellationToken);

        // Assert — preview banner, stray flagged, nothing written (byte-for-byte).
        result.ShouldContain("DRY RUN — no files written.");
        result.ShouldContain("WARNING:");
        result.ShouldContain("StrayConsumer.cs");
        (await File.ReadAllBytesAsync(circleFile, TestContext.Current.CancellationToken)).ShouldBe(circleBefore);
    }

    [Fact]
    public async Task RenameSymbol_DryRun_InactivePreprocessorBranch_CountsFixupsWithoutWriting()
    {
        // Arrange — ConditionalField.cs declares lockObj in both #if and #else branches; the inactive
        // one is DisabledTextTrivia. The fold counts the fixup a commit would apply.
        CodeEditTools tools = CreateEditTools(Fixture);
        string conditionalFile = MultiTfmFile(Fixture, "ConditionalField.cs");
        byte[] before = await File.ReadAllBytesAsync(conditionalFile, TestContext.Current.CancellationToken);

        // Act — DryRun rename lockObj → syncRoot.
        string result = await tools.RenameSymbol(
            conditionalFile, "lockObj", "syncRoot", "ConditionalField",
            verify: VerifyMode.DryRun, ct: TestContext.Current.CancellationToken);

        // Assert — preview wording, the disabled-branch fixup counted ("would be updated"), no write.
        result.ShouldContain("Would rename 'lockObj' to 'syncRoot'");
        result.ShouldContain("inactive preprocessor branches");
        result.ShouldContain("would be updated");
        (await File.ReadAllBytesAsync(conditionalFile, TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    [Fact]
    public async Task RenameSymbol_Delta_InactivePreprocessorBranch_CommitsBothBranchesAtomically()
    {
        // Arrange — the disabled-branch fixup is folded into the fork, so it rides the single commit batch
        // under Delta; the reported delta stays clean.
        CodeEditTools tools = CreateEditTools(Fixture);
        string conditionalFile = MultiTfmFile(Fixture, "ConditionalField.cs");

        // Act — Delta rename lockObj → syncRoot.
        string result = await tools.RenameSymbol(
            conditionalFile, "lockObj", "syncRoot", "ConditionalField",
            verify: VerifyMode.Delta, ct: TestContext.Current.CancellationToken);

        // Assert — committed, clean delta, no dry-run banner.
        result.ShouldContain("Renamed 'lockObj' to 'syncRoot'");
        result.ShouldContain("Verification: no new errors");
        result.ShouldNotContain("DRY RUN");

        // Both branches were rewritten on disk in the one commit.
        string content = await File.ReadAllTextAsync(conditionalFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("lockObj");
        content.ShouldContain("Lock syncRoot");
        content.ShouldContain("object syncRoot");
    }
}
