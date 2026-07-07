using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;
using Zphil.Roz.Services;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Tools.WorkspaceToolTests;

/// <summary>
///     Integration tests for WorkspaceService error/fallback paths.
///     Most tests share an <see cref="EditWorkspaceFixture" /> for speed;
///     only the disposed-access test needs an isolated workspace.
/// </summary>
public class WorkspaceManagerTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task GetSolutionAsync_AfterDispose_ThrowsInvalidOperationException()
    {
        // This test disposes the workspace — must use an isolated instance
        await using TempWorkspace temp = await TempWorkspaceFactory.CreateAsync(ct: TestContext.Current.CancellationToken);
        WorkspaceManager ws = temp.WorkspaceManager;
        await ws.DisposeAsync();

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(ws.GetSolutionAsync());
    }

    [Fact]
    public async Task NotifyFileChangedAsync_FileNotInSolution_CompletesWithoutError()
    {
        // Arrange
        WorkspaceManager ws = Fixture.WorkspaceManager;

        Solution before = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
        int docCountBefore = before.Projects.Sum(p => p.Documents.Count());

        // Act — notify about a file that isn't part of any project
        string strayFile = Path.Combine(Path.GetTempPath(), "nonexistent", "stray.cs");
        await ws.NotifyFileChangedAsync(strayFile, "// not in solution", ct: TestContext.Current.CancellationToken);

        // Assert — solution is unchanged
        Solution after = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
        int docCountAfter = after.Projects.Sum(p => p.Documents.Count());
        docCountAfter.ShouldBe(docCountBefore);
    }

    [Fact]
    public async Task NotifyFileChangedAsync_WithNullContent_ReadsFromDisk()
    {
        // Arrange
        WorkspaceManager ws = Fixture.WorkspaceManager;
        string circleFile = TestFileHelper.CircleFile(Fixture);

        // Modify the file on disk
        string original = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string modified = original.Replace("Math.PI", "3.14159");
        await File.WriteAllTextAsync(circleFile, modified, TestContext.Current.CancellationToken);

        // Act — notify with null content so it reads from disk
        await ws.NotifyFileChangedAsync(circleFile, null, ct: TestContext.Current.CancellationToken);

        // Assert — in-memory solution reflects the disk change
        Solution solution = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
        Document? doc = solution.GetDocumentByPath(circleFile);
        doc.ShouldNotBeNull();
        SourceText text = await doc.GetTextAsync(TestContext.Current.CancellationToken);
        var content = text.ToString();
        content.ShouldContain("3.14159");
        content.ShouldNotContain("Math.PI");
    }

    [Fact]
    public async Task ScheduleFileChanged_WhenNotifyThrows_DoesNotBreakSubsequentOperations()
    {
        // Arrange
        WorkspaceManager ws = Fixture.WorkspaceManager;
        string circleFile = TestFileHelper.CircleFile(Fixture);

        // Delete the file from disk so the null-content fallback triggers FileNotFoundException
        File.Delete(circleFile);

        // Act — schedule with null content (forces disk read on a deleted file)
        ws.ScheduleFileChanged(circleFile, null);

        // Assert — GetSolutionAsync still works; the exception was swallowed
        Solution solution = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
        solution.ShouldNotBeNull();
        solution.Projects.Count().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ReloadAsync_ClearsBaselineAndReloadsFromDisk()
    {
        // Arrange
        WorkspaceManager ws = Fixture.WorkspaceManager;
        DiagnosticBaselineManager bm = Fixture.BaselineManager;
        string circleFile = TestFileHelper.CircleFile(Fixture);

        // Capture a baseline
        await bm.CaptureBaselineIfNeededAsync(TestContext.Current.CancellationToken);
        bm.GetBaseline().ShouldNotBeNull();

        // Modify file on disk without notifying the workspace
        string original = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string modified = original.Replace("Math.PI", "3.14159");
        await File.WriteAllTextAsync(circleFile, modified, TestContext.Current.CancellationToken);

        // Act
        bm.ClearBaseline();
        Solution reloaded = await ws.ReloadAsync(ct: TestContext.Current.CancellationToken);

        // Assert — baseline is cleared
        bm.GetBaseline().ShouldBeNull();

        // Assert — reloaded solution picks up on-disk changes
        Document? doc = reloaded.GetDocumentByPath(circleFile);
        doc.ShouldNotBeNull();
        SourceText text = await doc.GetTextAsync(TestContext.Current.CancellationToken);
        text.ToString().ShouldContain("3.14159");
    }

    [Fact]
    public async Task CaptureBaselineIfNeededAsync_CalledTwice_OnlyCapturesOnce()
    {
        // Arrange
        DiagnosticBaselineManager bm = Fixture.BaselineManager;

        // Act
        await bm.CaptureBaselineIfNeededAsync(TestContext.Current.CancellationToken);
        DiagnosticBaseline? first = bm.GetBaseline();

        await bm.CaptureBaselineIfNeededAsync(TestContext.Current.CancellationToken);
        DiagnosticBaseline? second = bm.GetBaseline();

        // Assert — same instance, second call was a no-op
        first.ShouldNotBeNull();
        second.ShouldBeSameAs(first);
    }

    [Fact]
    public async Task ReloadAsync_DoesNotReformatCsprojFiles()
    {
        // Arrange — isolated workspace so we can inject an unresolved analyzer reference
        await using TempWorkspace temp = await TempWorkspaceFactory.CreateAsync(ct: TestContext.Current.CancellationToken);
        WorkspaceManager ws = temp.WorkspaceManager;
        string solutionDir = await ws.GetRequiredSolutionDirectoryAsync(TestContext.Current.CancellationToken);

        // Inject an unresolved analyzer reference to trigger StripUnresolvedReferences path
        string csprojPath = Path.Combine(solutionDir, "TestFixture", "TestFixture.csproj");
        string originalXml = await File.ReadAllTextAsync(csprojPath, TestContext.Current.CancellationToken);
        string modifiedXml = originalXml.Replace(
            "</Project>",
            """

                <ItemGroup>
                    <Analyzer Include="NonExistent.Analyzer.dll" />
                </ItemGroup>

            </Project>
            """);
        await File.WriteAllTextAsync(csprojPath, modifiedXml, TestContext.Current.CancellationToken);

        // Snapshot all .csproj bytes before reload
        string[] csprojFiles = Directory.GetFiles(solutionDir, "*.csproj", SearchOption.AllDirectories);
        Dictionary<string, byte[]> before = [];
        foreach (string file in csprojFiles)
        {
            before[file] = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
        }

        // Act — reload triggers LoadSolutionInternalAsync which strips unresolved refs
        await ws.ReloadAsync(ct: TestContext.Current.CancellationToken);

        // Assert — every .csproj file is byte-identical after reload
        foreach (string file in csprojFiles)
        {
            byte[] after = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
            after.ShouldBe(before[file], $"File was modified by reload: {Path.GetFileName(file)}");
        }
    }

    [Fact]
    public async Task ClearBaseline_ThenRecapture_GetsNewBaseline()
    {
        // Arrange
        DiagnosticBaselineManager bm = Fixture.BaselineManager;

        await bm.CaptureBaselineIfNeededAsync(TestContext.Current.CancellationToken);
        DiagnosticBaseline? first = bm.GetBaseline();
        first.ShouldNotBeNull();

        // Act
        bm.ClearBaseline();
        bm.GetBaseline().ShouldBeNull();

        await bm.CaptureBaselineIfNeededAsync(TestContext.Current.CancellationToken);
        DiagnosticBaseline? second = bm.GetBaseline();

        // Assert — different instance after clear + recapture
        second.ShouldNotBeNull();
        second.ShouldNotBeSameAs(first);
    }
}
