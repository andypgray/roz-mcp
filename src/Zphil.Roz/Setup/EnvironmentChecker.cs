using System.Diagnostics;
using System.Text;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Setup;

/// <summary>
///     Validates the development environment for running the roz-mcp server.
/// </summary>
internal static class EnvironmentChecker
{
    /// <summary>
    ///     Runs all environment checks and returns the results.
    /// </summary>
    public static async Task<List<CheckResult>> RunAllChecksAsync(
        string workingDirectory, ProjectConfigSeedResult configSeed)
    {
        List<CheckResult> results =
        [
            await CheckDotNetSdkAsync(),
            CheckMSBuild(),
            CheckSolutionFile(workingDirectory),
            CheckProjectConfig(workingDirectory, configSeed)
        ];

        return results;
    }

    /// <summary>
    ///     Formats check results for console output.
    /// </summary>
    public static string FormatResults(List<CheckResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Environment Check Results");
        sb.AppendLine(new string('=', 40));

        foreach (CheckResult result in results)
        {
            string icon = result.Passed ? "[PASS]" : "[FAIL]";
            sb.AppendLine($"  {icon} {result.Name}");
            sb.AppendLine($"         {result.Detail}");
        }

        bool allPassed = results.All(r => r.Passed);
        sb.AppendLine();
        sb.AppendLine(allPassed
            ? "All checks passed."
            : "Some checks failed. Fix the issues above before proceeding.");

        return sb.ToString();
    }

    private static async Task<CheckResult> CheckDotNetSdkAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return new CheckResult(false, ".NET SDK", "Could not start 'dotnet' process.");
            }

            string version = (await process.StandardOutput.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                return new CheckResult(false, ".NET SDK",
                    "dotnet --version failed. Install .NET SDK: https://dotnet.microsoft.com/download");
            }

            // Check for .NET 10+
            string versionCore = version.Split('-')[0];
            if (Version.TryParse(versionCore, out Version? parsed) && parsed.Major >= 10)
            {
                return new CheckResult(true, ".NET SDK", $"Version {version}");
            }

            return new CheckResult(false, ".NET SDK",
                $"Version {version} found, but .NET 10.0+ is required. Download: https://dotnet.microsoft.com/download");
        }
        catch (Exception ex)
        {
            return new CheckResult(false, ".NET SDK",
                $"'dotnet' not found on PATH. Install .NET SDK: https://dotnet.microsoft.com/download ({ex.Message})");
        }
    }

    private static CheckResult CheckMSBuild()
    {
        try
        {
            string? overridePath = EnvParse.RawString(RozEnvVars.VsInstallPath.Name);
            if (overridePath is not null)
            {
                return new CheckResult(true, "MSBuild",
                    $"{RozEnvVars.VsInstallPath.Name} is set: {overridePath}");
            }

            if (!OperatingSystem.IsWindows())
            {
                return new CheckResult(true, "MSBuild",
                    "Non-Windows platform; will use MSBuildLocator default.");
            }

            IReadOnlyList<VsInstance> instances = VsWhereLocator.Query();
            if (instances.Count == 0)
            {
                return new CheckResult(false, "MSBuild",
                    "No Visual Studio installs found via vswhere. Install .NET SDK or Visual Studio.");
            }

            VsInstance? selected = MsBuildBootstrap.SelectBestInstance(instances);
            return new CheckResult(true, "MSBuild",
                $"Found {instances.Count} VS install(s). Will use: {selected!.Name} ({selected.Version})");
        }
        catch (Exception ex)
        {
            return new CheckResult(false, "MSBuild", $"Error querying MSBuild: {ex.Message}");
        }
    }

    /// <summary>
    ///     Reports what the optional <c>.roz.json</c> seeding pass did.
    /// </summary>
    /// <remarks>
    ///     Always passes: the file is optional and an unparseable one is ignored with a warning, so
    ///     it must never block setup.
    /// </remarks>
    internal static CheckResult CheckProjectConfig(string workingDirectory, ProjectConfigSeedResult configSeed)
    {
        const string checkName = "Project Config";

        if (configSeed.ConfigFilePath is null)
        {
            // No file — or the seeding pass itself failed, in which case the warning says so.
            string detail = configSeed.Warnings.Count > 0
                ? configSeed.Warnings[0]
                : $"none found (optional {ProjectConfigSeeder.FileName})";
            return new CheckResult(true, checkName, detail);
        }

        string relPath = Path.GetRelativePath(workingDirectory, configSeed.ConfigFilePath);

        if (configSeed.IsIgnored)
        {
            return new CheckResult(true, checkName, $"Found {relPath} but {configSeed.Summary()}");
        }

        return new CheckResult(true, checkName, $"Found: {relPath} ({configSeed.Summary()})");
    }

    private static CheckResult CheckSolutionFile(string workingDirectory)
    {
        try
        {
            string slnPath = FileUtility.DiscoverSolution();
            string relPath = Path.GetRelativePath(workingDirectory, slnPath);
            return new CheckResult(true, "Solution File", $"Found: {relPath}");
        }
        catch (UserErrorException ex)
        {
            return new CheckResult(false, "Solution File", ex.Message);
        }
    }

    internal sealed record CheckResult(bool Passed, string Name, string Detail);
}
