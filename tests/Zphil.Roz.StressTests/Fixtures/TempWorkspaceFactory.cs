using Microsoft.Extensions.Logging.Abstractions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Services;

namespace Zphil.Roz.StressTests.Fixtures;

internal sealed record TestSolutionConfig(string CacheDir, string SolutionRelativePath, string[] SkipDirectories)
{
    internal static TestSolutionConfig NopCommerce => new(
        NopPaths.CacheDir, Path.Combine("src", "NopCommerce.sln"),
        ["bin", ".git", "tests", ".github"]);
}

internal sealed class TempWorkspace(WorkspaceManager workspaceManager, DiagnosticBaselineManager baselineManager, string tempDirectory) : ITestWorkspace, IAsyncDisposable
{
    private string TempDirectory { get; } = tempDirectory;

    public async ValueTask DisposeAsync()
    {
        // Dispose BaselineManager first to drain any in-flight capture and release its BeforeReload
        // subscription against the still-live workspace — mirrors the production DI container's
        // reverse-order disposal (singletons added later are disposed first).
        await BaselineManager.DisposeAsync();
        await WorkspaceManager.DisposeAsync();
        Directory.Delete(TempDirectory, true);
    }

    public WorkspaceManager WorkspaceManager { get; } = workspaceManager;
    public DiagnosticBaselineManager BaselineManager { get; } = baselineManager;
}

internal static class TempWorkspaceFactory
{
    internal static Task<TempWorkspace> CreateAsync(CancellationToken ct = default) =>
        CreateAsync(TestSolutionConfig.NopCommerce, ct);

    internal static async Task<TempWorkspace> CreateAsync(TestSolutionConfig config, CancellationToken ct = default)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "roslyn-stress-tests", Guid.NewGuid().ToString("N"));
        CopyDirectory(config.CacheDir, tempDir, config.SkipDirectories);

        try
        {
            string solutionPath = Path.Combine(tempDir, config.SolutionRelativePath);
            var service = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance, solutionPath);
            await service.GetSolutionAsync(ct);

            // GetSolutionAsync now returns at the usable-snapshot point, before compilation warmup and
            // the external-edit watcher start (the cold-start decoupling). Await the full load so stress
            // tests run against a fully-warm, watcher-armed workspace — matching the pre-decoupling
            // barrier and keeping any edit timings free of background-warmup contention.
            if (service.LoadReadyTask is { } fullLoad)
            {
                await fullLoad;
            }

            var baselineManager = new DiagnosticBaselineManager(service, NullLogger<DiagnosticBaselineManager>.Instance);

            return new TempWorkspace(service, baselineManager, tempDir);
        }
        catch
        {
            Directory.Delete(tempDir, true);
            throw;
        }
    }

    private static void CopyDirectory(string source, string dest, string[] skipDirectories)
    {
        Directory.CreateDirectory(dest);

        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        }

        foreach (string dir in Directory.GetDirectories(source))
        {
            string dirName = Path.GetFileName(dir);

            // Skip directories that are large or unnecessary for Roslyn workspace loading
            if (skipDirectories.Contains(dirName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (dirName is "obj")
            {
                CopyObjDirectory(dir, Path.Combine(dest, dirName));
                continue;
            }

            CopyDirectory(dir, Path.Combine(dest, dirName), skipDirectories);
        }
    }

    /// <summary>
    ///     Copies obj/ directories selectively — includes generated source files (.g.cs)
    ///     and NuGet restore artifacts (project.assets.json, *.nuget.*) needed for
    ///     MSBuildWorkspace to resolve dependencies without re-restoring.
    /// </summary>
    private static void CopyObjDirectory(string source, string dest)
    {
        string[] files = Directory.GetFiles(source);
        List<string> toCopy = [];

        foreach (string file in files)
        {
            string fileName = Path.GetFileName(file);
            bool isNeeded = fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
                            fileName.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase) ||
                            (fileName.StartsWith("project.", StringComparison.OrdinalIgnoreCase) &&
                             (fileName.EndsWith(".nuget.g.props", StringComparison.OrdinalIgnoreCase) ||
                              fileName.EndsWith(".nuget.g.targets", StringComparison.OrdinalIgnoreCase) ||
                              fileName.EndsWith(".nuget.dgspec.json", StringComparison.OrdinalIgnoreCase) ||
                              fileName.EndsWith(".nuget.cache", StringComparison.OrdinalIgnoreCase)));

            if (isNeeded)
            {
                toCopy.Add(file);
            }
        }

        if (toCopy.Count > 0)
        {
            Directory.CreateDirectory(dest);
            foreach (string file in toCopy)
            {
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
            }
        }

        foreach (string dir in Directory.GetDirectories(source))
        {
            CopyObjDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }
}
