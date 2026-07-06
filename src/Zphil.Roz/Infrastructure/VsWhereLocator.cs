using System.Diagnostics;
using System.Text.Json;
using Serilog;

namespace Zphil.Roz.Infrastructure;

/// <summary>
///     Discovers installed Visual Studio instances via the standard <c>vswhere.exe</c> tool.
/// </summary>
/// <remarks>
///     Used in place of <c>MSBuildLocator.QueryVisualStudioInstances()</c> because on .NET 5+
///     that API only returns DotNetSdk and DevConsole entries — never VS Setup instances. This
///     server runs on .NET 10, so we'd otherwise miss every installed VS. <c>vswhere.exe</c>
///     ships with the VS Installer at a fixed path and reliably enumerates all VS installs
///     (stable + preview) including ones not yet picked up by MSBuildLocator.
/// </remarks>
internal static class VsWhereLocator
{
    private static readonly string VsWherePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Microsoft Visual Studio",
        "Installer",
        "vswhere.exe");

    /// <summary>
    ///     Runs vswhere and returns all discovered VS instances.
    /// </summary>
    /// <returns>
    ///     The discovered instances, or an empty list when vswhere is unavailable (non-Windows
    ///     hosts, missing install) or fails. Callers should treat empty as a signal to fall back
    ///     to <see cref="Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults" />.
    /// </returns>
    public static IReadOnlyList<VsInstance> Query()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(VsWherePath))
        {
            Log.Debug("vswhere.exe not available at {Path}", VsWherePath);
            return [];
        }

        try
        {
            string json = RunVsWhere();
            return ParseInstances(json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to enumerate Visual Studio instances via vswhere");
            return [];
        }
    }

    /// <summary>
    ///     Parses vswhere's JSON output into <see cref="VsInstance" /> records.
    /// </summary>
    /// <param name="json">JSON array as emitted by <c>vswhere -format json</c>.</param>
    /// <returns>
    ///     One record per array element with valid <c>installationPath</c> and parseable
    ///     <c>installationVersion</c>. Empty when <paramref name="json" /> is blank, not an array,
    ///     or contains no usable entries.
    /// </returns>
    /// <remarks>
    ///     Exposed (rather than kept private) so unit tests can feed JSON directly without
    ///     spawning the vswhere process.
    /// </remarks>
    public static IReadOnlyList<VsInstance> ParseInstances(string json)
    {
        if (String.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<VsInstance> result = [];
        foreach (JsonElement element in doc.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("installationPath", out JsonElement pathProp) ||
                !element.TryGetProperty("installationVersion", out JsonElement versionProp))
            {
                continue;
            }

            string? path = pathProp.GetString();
            string? versionString = versionProp.GetString();
            if (path is null || versionString is null || !Version.TryParse(versionString, out Version? version))
            {
                continue;
            }

            string name = element.TryGetProperty("displayName", out JsonElement nameProp) && nameProp.GetString() is { } n
                ? n
                : "Visual Studio";

            result.Add(new VsInstance(path, version, name));
        }

        return result;
    }

    private static string RunVsWhere()
    {
        ProcessStartInfo psi = new(VsWherePath, "-all -prerelease -format json -products *")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process proc = Process.Start(psi)
                             ?? throw new InvalidOperationException($"Failed to start {VsWherePath}");

        // Drain stderr concurrently so a chatty child can't fill its pipe buffer and block while
        // we're synchronously consuming stdout.
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        string output = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(10_000))
        {
            proc.Kill();
            throw new TimeoutException("vswhere did not complete within 10 seconds");
        }

        if (proc.ExitCode != 0)
        {
            string err = stderrTask.GetAwaiter().GetResult();
            throw new InvalidOperationException($"vswhere exited with code {proc.ExitCode}: {err}");
        }

        return output;
    }
}

/// <summary>
///     Subset of <c>vswhere</c>'s output: the fields we need for MSBuild selection.
/// </summary>
internal sealed record VsInstance(string InstallationPath, Version Version, string Name);
