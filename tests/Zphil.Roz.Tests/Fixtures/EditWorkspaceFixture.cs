using Microsoft.Extensions.Logging.Abstractions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Services;

namespace Zphil.Roz.Tests.Fixtures;

/// <summary>
///     Shared xUnit class fixture that creates a single temp workspace per test class.
///     Between tests, <see cref="ResetAsync" /> restores all files to their original state
///     using cheap
///     <see cref="Zphil.Roz.Infrastructure.WorkspaceManager.NotifyFileChangedAsync(string, CancellationToken)" />
///     calls instead of a full MSBuild reload.
/// </summary>
public sealed class EditWorkspaceFixture : ITestWorkspace, IAsyncLifetime
{
    private static readonly string FixturesDirectory =
        Path.GetDirectoryName(WorkspaceFixture.FixtureSolutionPath)!;

    private static readonly HashSet<string> TrackedExtensions = [".cs", ".csproj", ".sln", ".razor", ".resx"];

    private readonly Dictionary<string, byte[]> originalFiles = new();
    private string tempDirectory = null!;

    internal WorkspaceManager WorkspaceManager { get; private set; } = null!;
    internal DiagnosticBaselineManager BaselineManager { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "roslyn-tests", Guid.NewGuid().ToString("N"));
        TestFileHelper.CopyDirectory(FixturesDirectory, tempDirectory);

        // Snapshot all source files (raw bytes to preserve encoding/BOM)
        IEnumerable<string> trackedFiles = Directory.GetFiles(tempDirectory, "*.*", SearchOption.AllDirectories)
            .Where(f => TrackedExtensions.Contains(Path.GetExtension(f)));

        foreach (string file in trackedFiles)
        {
            originalFiles[file] = await File.ReadAllBytesAsync(file);
        }

        // Load via a solution filter so MSBuildWorkspace only spins up the 5 projects edit tests
        // actually touch (TestFixture + .Legacy + .MultiTfm + .Friend + .Tests). The full TestFixture.sln
        // has 10 projects; the read-only WorkspaceFixture still loads the full sln for navigation/diagnostic tests.
        string solutionPath = Path.Combine(tempDirectory, "EditFixture.slnf");
        // autoRefreshDisabled: true — keep the FileSystemWatcher off in tests so background reloads
        // don't race with fixture file operations.
        WorkspaceManager = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance, solutionPath, true);
        BaselineManager = new DiagnosticBaselineManager(WorkspaceManager, NullLogger<DiagnosticBaselineManager>.Instance);
        await WorkspaceManager.GetSolutionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // Dispose BaselineManager first to drain any in-flight capture and release its BeforeReload
        // subscription against the still-live workspace — mirrors the production DI container's
        // reverse-order disposal (singletons added later are disposed first).
        await BaselineManager.DisposeAsync();
        await WorkspaceManager.DisposeAsync();
        try
        {
            Directory.Delete(tempDirectory, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; temp directory may be locked briefly
        }
    }

    WorkspaceManager ITestWorkspace.WorkspaceManager => WorkspaceManager;
    DiagnosticBaselineManager ITestWorkspace.BaselineManager => BaselineManager;

    /// <summary>
    ///     Restores all files to their original state and updates the in-memory workspace.
    ///     Call this at the start of each test via <see cref="IAsyncLifetime.InitializeAsync" />
    ///     on the test class.
    /// </summary>
    internal async Task ResetAsync()
    {
        var needsReload = false;

        // Restore modified files sequentially — NotifyFileChangedAsync serializes on
        // an internal semaphore, so concurrent calls would just queue anyway.
        foreach ((string path, byte[] originalBytes) in originalFiles)
        {
            if (!File.Exists(path))
            {
                // File was renamed or deleted — restore on disk. NotifyFileChangedAsync
                // won't find it in the solution (the document was removed during rename),
                // so flag for a full reload instead.
                await File.WriteAllBytesAsync(path, originalBytes);
                needsReload = true;
                continue;
            }

            byte[] currentBytes = await File.ReadAllBytesAsync(path);
            if (!currentBytes.AsSpan().SequenceEqual(originalBytes))
            {
                await File.WriteAllBytesAsync(path, originalBytes);
                await WorkspaceManager.NotifyFileChangedAsync(path);
            }
        }

        // Delete any files added during the test (all tracked extensions, not just .cs)
        foreach (string file in Directory.GetFiles(tempDirectory, "*.*", SearchOption.AllDirectories))
        {
            if (TrackedExtensions.Contains(Path.GetExtension(file)) && !originalFiles.ContainsKey(file))
            {
                File.Delete(file);
            }
        }

        // A renamed/deleted file can't be restored via NotifyFileChangedAsync because the
        // Roslyn solution no longer has a document at that path. Full reload re-discovers it.
        if (needsReload)
        {
            await WorkspaceManager.ReloadAsync();
        }

        BaselineManager.ClearBaseline();
    }
}
