using System.Diagnostics;

namespace Zphil.Roz.Tests.Fixtures;

/// <summary>
///     Restores the checked-in fixture solutions in the test output directory before any
///     workspace fixture opens them.
/// </summary>
/// <remarks>
///     <para>
///         NuGet restore state (<c>obj/project.assets.json</c>) is gitignored, so a fresh clone —
///         and therefore CI and the public snapshot — arrives with unrestored fixture projects. An
///         unrestored fixture loads without its package references, which silently degrades every
///         package-dependent behavior under test: <c>IsTestProject</c> stops seeing the xUnit
///         reference, DI-container registrations stop resolving, and analyzer packs are not
///         discovered. Local checkouts only pass by accident, because previously restored
///         <c>obj/</c> directories are swept into the output copy by the
///         <c>Fixtures/TestSolutions/**</c> content glob.
///     </para>
///     <para>
///         <b>
///             Driven from <see cref="FixtureRestoreStartup" /> (an xUnit v3 pipeline-startup hook),
///             not a <c>[ModuleInitializer]</c>:
///         </b>
///         module initializers run on every test-process
///         launch, including the runner's lightweight assembly-info probe, which is aborted if the
///         process doesn't respond within 60 seconds. A cold restore takes minutes, so doing it at
///         module-init time timed out that probe ("Test process did not respond within 60 seconds")
///         and zero tests ran. Pipeline startup runs only in the discover/run pass, which has no such
///         deadline. <see cref="EnsureRestored" /> restores exactly once per process, thread-safely.
///     </para>
/// </remarks>
internal static class FixtureRestorer
{
    // Run-once, thread-safe. Tests run in parallel and workspace fixtures/temp workspaces can
    // initialize concurrently, so Lazy (ExecutionAndPublication) guarantees exactly one restore
    // even if EnsureRestored is also called from more than one place; repeat calls are free.
    private static readonly Lazy<bool> RestoreGate = new(RestoreAll);

    /// <summary>
    ///     Restores every fixture solution once, the first time it is called; thread-safe and free on
    ///     repeat calls. No-ops when everything is already restored.
    /// </summary>
    internal static void EnsureRestored() => _ = RestoreGate.Value;

    private static bool RestoreAll()
    {
        string baseDirectory = Path.GetDirectoryName(typeof(FixtureRestorer).Assembly.Location)!;
        string testSolutionsDir = Path.Combine(baseDirectory, "Fixtures", "TestSolutions");
        if (!Directory.Exists(testSolutionsDir))
        {
            return true;
        }

        bool anyUnrestored = Directory
            .EnumerateFiles(testSolutionsDir, "*.csproj", SearchOption.AllDirectories)
            .Any(projectPath => !File.Exists(
                Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json")));
        if (!anyUnrestored)
        {
            return true;
        }

        foreach (string solutionPath in Directory
                     .EnumerateFiles(testSolutionsDir, "*.sln", SearchOption.AllDirectories))
        {
            Restore(solutionPath);
        }

        return true;
    }

    private static void Restore(string solutionPath)
    {
        ProcessStartInfo startInfo = new("dotnet", $"restore \"{solutionPath}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(solutionPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        // The test process has Visual Studio's MSBuild registered (MSBuildLocator +
        // MsBuildBootstrap set MSBUILD_EXE_PATH / VSINSTALLDIR / VSCMD_VER process-wide for the
        // Roslyn BuildHost). A child dotnet-CLI restore inheriting those resolves the wrong
        // MSBuild and fails immediately, so give it a clean SDK environment.
        foreach (string poisonedVariable in new[]
                 {
                     "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath",
                     "VSINSTALLDIR", "VSCMD_VER", "VisualStudioVersion"
                 })
        {
            startInfo.Environment.Remove(poisonedVariable);
        }

        using Process process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException(
                                    $"Failed to start 'dotnet restore' for {solutionPath}.");

        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        string error = errorTask.GetAwaiter().GetResult();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'dotnet restore {solutionPath}' failed with exit code {process.ExitCode}."
                + $"{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
    }
}
