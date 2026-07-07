using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Models;
using Zphil.Roz.Services;
using Zphil.Roz.Tests.Fixtures;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Services;

/// <summary>
///     Unit coverage for the fork-side persist+verify contract
///     (<see cref="EditVerificationService.FinalizeForkAsync" />) that <c>rename_symbol</c> and
///     <c>apply_code_fix</c> share: the empty-change skip (no delta, no committed-fault risk) and the
///     None commit path (write + null verification), exercised directly without a pathological fixer.
/// </summary>
public class EditVerificationServiceTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task FinalizeForkAsync_NoChanges_DryRun_ReturnsSkippedWithoutDelta()
    {
        // Arrange — base and fork are the same snapshot, modelling a no-op fixer.
        CancellationToken ct = TestContext.Current.CancellationToken;
        var service = new EditVerificationService(Fixture.WorkspaceManager);
        Solution solution = await Fixture.WorkspaceManager.GetSolutionAsync(ct);

        // Act
        ForkFinalizeOutcome outcome = await service.FinalizeForkAsync(solution, solution, VerifyMode.DryRun, null, ct);

        // Assert — skipped result, no delta, nothing committed.
        outcome.ChangedDocs.ShouldBeEmpty();
        outcome.Verification.ShouldNotBeNull();
        outcome.Verification!.SkippedReason.ShouldBe("no content changed");
        outcome.Verification.Delta.ShouldBeNull();
        outcome.Verification.Committed.ShouldBeFalse();
    }

    [Fact]
    public async Task FinalizeForkAsync_NoChanges_Delta_ReturnsSkippedWithoutCommit()
    {
        // Arrange — same-snapshot fork under Delta must skip rather than call the committed-delta path
        // with zero files (which risks the committed-fault InvalidOperationException wrapper).
        CancellationToken ct = TestContext.Current.CancellationToken;
        var service = new EditVerificationService(Fixture.WorkspaceManager);
        Solution solution = await Fixture.WorkspaceManager.GetSolutionAsync(ct);

        // Act
        ForkFinalizeOutcome outcome = await service.FinalizeForkAsync(solution, solution, VerifyMode.Delta, null, ct);

        // Assert
        outcome.ChangedDocs.ShouldBeEmpty();
        outcome.Verification.ShouldNotBeNull();
        outcome.Verification!.SkippedReason.ShouldBe("no content changed");
        outcome.Verification.Delta.ShouldBeNull();
        outcome.Verification.Committed.ShouldBeFalse();
    }

    [Fact]
    public async Task FinalizeForkAsync_None_WritesChangesAndReturnsNullVerification()
    {
        // Arrange — fork one document by appending a trailing comment (a valid, harmless change).
        CancellationToken ct = TestContext.Current.CancellationToken;
        var service = new EditVerificationService(Fixture.WorkspaceManager);
        Solution solution = await Fixture.WorkspaceManager.GetSolutionAsync(ct);
        string circleFile = CircleFile(Fixture);
        Document doc = solution.GetDocumentByPath(circleFile)!;
        SourceText original = await doc.GetTextAsync(ct);
        var forked = SourceText.From($"{original}\n// fork marker\n", original.Encoding);
        Solution fork = solution.WithDocumentText(doc.Id, forked);

        // Act — None commits the fork and returns no verification.
        ForkFinalizeOutcome outcome = await service.FinalizeForkAsync(solution, fork, VerifyMode.None, null, ct);

        // Assert — committed to disk, null verification, changed list names the file.
        outcome.Verification.ShouldBeNull();
        outcome.ChangedDocs.ShouldContain(d => d.Contains("Circle.cs"));
        (await File.ReadAllTextAsync(circleFile, ct)).ShouldContain("// fork marker");
    }
}
