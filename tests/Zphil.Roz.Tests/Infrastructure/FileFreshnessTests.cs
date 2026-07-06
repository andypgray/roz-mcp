using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;
using Zphil.Roz.Services;
using Zphil.Roz.Symbols;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Infrastructure;

/// <summary>
///     Tests that externally modified files are automatically detected and reloaded
///     before tool operations, without requiring an explicit get_workspace_info reload=true call.
/// </summary>
public class FileFreshnessTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task EnsureFilesFresh_ExternalModification_ReloadsFile()
    {
        // Arrange — load solution so timestamps are recorded
        WorkspaceManager ws = Fixture.WorkspaceManager;
        string circleFile = TestFileHelper.CircleFile(Fixture);
        await ws.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Externally modify the file (bypassing workspace notification)
        string originalContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string modifiedContent = originalContent.Replace("Math.PI", "3.14159");
        await File.WriteAllTextAsync(circleFile, modifiedContent, TestContext.Current.CancellationToken);

        // Act — ensure freshness, then get solution
        await ws.EnsureFilesFreshAsync([circleFile], TestContext.Current.CancellationToken);
        Solution solution = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Assert — workspace sees the externally modified content
        Document? doc = solution.GetDocumentByPath(circleFile);
        doc.ShouldNotBeNull();
        SourceText text = await doc.GetTextAsync(TestContext.Current.CancellationToken);
        text.ToString().ShouldContain("3.14159");
        text.ToString().ShouldNotContain("Math.PI");
    }

    [Fact]
    public async Task EnsureFilesFresh_UnmodifiedFile_DoesNotReload()
    {
        // Arrange — load solution so timestamps are recorded
        WorkspaceManager ws = Fixture.WorkspaceManager;
        string circleFile = TestFileHelper.CircleFile(Fixture);
        Solution originalSolution = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
        Document? originalDoc = originalSolution.GetDocumentByPath(circleFile);
        originalDoc.ShouldNotBeNull();
        SourceText originalText = await originalDoc.GetTextAsync(TestContext.Current.CancellationToken);

        // Act — call EnsureFilesFreshAsync without modifying the file
        await ws.EnsureFilesFreshAsync([circleFile], TestContext.Current.CancellationToken);
        Solution solution = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Assert — document should be unchanged (same content)
        Document? doc = solution.GetDocumentByPath(circleFile);
        doc.ShouldNotBeNull();
        SourceText text = await doc.GetTextAsync(TestContext.Current.CancellationToken);
        text.ToString().ShouldBe(originalText.ToString());
    }

    [Fact]
    public async Task GetSymbolsOverview_AfterExternalEdit_SeesNewContent()
    {
        // Arrange - external edit, then Roslyn tool call
        WorkspaceManager ws = Fixture.WorkspaceManager;
        string circleFile = TestFileHelper.CircleFile(Fixture);
        await ws.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Add a new using directive externally (simulating Claude Code's Edit tool)
        string originalContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string modifiedContent = "using System.Text.Json;\n" + originalContent;
        await File.WriteAllTextAsync(circleFile, modifiedContent, TestContext.Current.CancellationToken);

        // Act — capture the overview so we can assert the tool itself (not just the workspace) saw fresh content
        var navigationService = new NavigationService(ws, new SymbolResolver(ws));
        string relPath = ws.GetRelativePath(circleFile);
        SymbolsOverviewResult overview = await navigationService.GetSymbolsOverviewAsync(relPath, ct: TestContext.Current.CancellationToken);

        // Assert — the prepended using shifted Circle's declaration from line 3 to line 4; the overview
        // reflects the post-edit position, proving it ran on the freshly-reloaded file (not a stale snapshot).
        ISymbol circle = overview.Symbols.Single(s => s.Name == "Circle");
        int circleLine = circle.Locations[0].GetLineSpan().StartLinePosition.Line + 1;
        circleLine.ShouldBe(4);

        // And the workspace document reflects the edit too.
        Solution solution = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
        Document? doc = solution.GetDocumentByPath(circleFile);
        doc.ShouldNotBeNull();
        SourceText text = await doc.GetTextAsync(TestContext.Current.CancellationToken);
        text.ToString().ShouldContain("using System.Text.Json;");
    }

    [Fact]
    public async Task RemoveUnusedUsings_AfterExternalEdit_DetectsAddedUsing()
    {
        // Arrange
        WorkspaceManager ws = Fixture.WorkspaceManager;
        string circleFile = TestFileHelper.CircleFile(Fixture);
        await ws.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Add an unused using directive externally
        string originalContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string modifiedContent = "using System.Text.Json;\n" + originalContent;
        await File.WriteAllTextAsync(circleFile, modifiedContent, TestContext.Current.CancellationToken);

        // Act — call remove_unused_usings (which should detect the stale file)
        var usingService = new UsingDirectiveService(ws, Fixture.BaselineManager);
        string relPath = ws.GetRelativePath(circleFile);
        RemoveUnusedUsingsResult result = await usingService.RemoveUnusedUsingsAsync([relPath], ct: TestContext.Current.CancellationToken);

        // Assert — the externally added unused using should be detected and removed
        result.Files.ShouldHaveSingleItem();
        result.Files[0].Removed.ShouldContain("System.Text.Json");
    }

    [Fact]
    public async Task EnsureFilesFresh_ExternalDeletion_TriggersReloadAndRemovesDoc()
    {
        // Arrange
        WorkspaceManager ws = Fixture.WorkspaceManager;
        string circleFile = TestFileHelper.CircleFile(Fixture);
        await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
        File.Delete(circleFile);

        // Act
        await ws.EnsureFilesFreshAsync([circleFile], TestContext.Current.CancellationToken);

        // Assert — post-reload solution no longer contains the deleted document
        Solution solution = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
        solution.GetDocumentIdsWithFilePath(circleFile).ShouldBeEmpty();
    }

    [Fact]
    public async Task EnsureFilesFresh_DeletedUntrackedPath_DoesNotReload()
    {
        // Arrange — a path that was never part of any project, so never recorded
        WorkspaceManager ws = Fixture.WorkspaceManager;
        await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
        string strayPath = Path.Combine(ws.SolutionDirectory!, "DoesNotExist.cs");

        var reloadCount = 0;
        using IDisposable sub = ws.RegisterBeforeReload(() => Interlocked.Increment(ref reloadCount));

        // Act
        await ws.EnsureFilesFreshAsync([strayPath], TestContext.Current.CancellationToken);

        // Assert — no reload triggered for an unknown path
        reloadCount.ShouldBe(0);
    }

    [Fact]
    public async Task EnsureFilesFresh_DeletedFile_FollowedBySymbolLookup_NoStaleSymbol()
    {
        // Symmetry with ReconcileAllExternalEditsAsync_FileDeletedExternally_FollowedByFindSymbol_NoStaleSymbol
        WorkspaceManager ws = Fixture.WorkspaceManager;
        string circleFile = TestFileHelper.CircleFile(Fixture);
        await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
        File.Delete(circleFile);

        await ws.EnsureFilesFreshAsync([circleFile], TestContext.Current.CancellationToken);

        var navigationService = new NavigationService(ws, new SymbolResolver(ws));
        FindSymbolResult result = await navigationService.FindSymbolAsync("Circle", matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);
        result.Symbols.ShouldNotContain(s => s.Name == "Circle");
    }

    [Fact]
    public async Task EnsureFilesFresh_BatchWithDeleteAndModify_SingleReloadCoversBoth()
    {
        // Symmetry with ReconcileAllExternalEditsAsync_DeleteAndModify_DeleteShortCircuits.
        // Modify shape, delete circle. Reload short-circuit absorbs both changes.
        WorkspaceManager ws = Fixture.WorkspaceManager;
        string shapeFile = TestFileHelper.ShapeFile(Fixture);
        string circleFile = TestFileHelper.CircleFile(Fixture);
        await ws.GetSolutionAsync(TestContext.Current.CancellationToken);

        string original = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(shapeFile, original + "\n// external-edit-marker", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(shapeFile, DateTime.UtcNow.AddSeconds(1));
        File.Delete(circleFile);

        var reloadCount = 0;
        using IDisposable sub = ws.RegisterBeforeReload(() => Interlocked.Increment(ref reloadCount));

        // Act — modify-then-delete order so the loop hits modify first, then short-circuits on delete
        await ws.EnsureFilesFreshAsync([shapeFile, circleFile], TestContext.Current.CancellationToken);

        // Assert — single reload absorbed both changes
        reloadCount.ShouldBe(1);
        Solution solution = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
        solution.GetDocumentIdsWithFilePath(circleFile).ShouldBeEmpty();
        Document? shapeDoc = solution.GetDocumentByPath(shapeFile);
        shapeDoc.ShouldNotBeNull();
        (await shapeDoc.GetTextAsync(TestContext.Current.CancellationToken)).ToString().ShouldContain("external-edit-marker");
    }
}
