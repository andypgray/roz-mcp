using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Pipeline;
using Zphil.Roz.Setup.Clients;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Setup;

/// <summary>
///     Orchestrates the full onboarding flow for a new project:
///     environment checks, per-client MCP configuration, and workspace validation.
/// </summary>
internal static class SetupCommand
{
    private const string AllClientsToken = "all";

    private static readonly IReadOnlyList<IClientConfigurator> AllConfigurators =
    [
        new ClaudeCodeConfigurator(),
        new CursorConfigurator(),
        new VsCodeConfigurator(),
        new CodexConfigurator()
    ];

    private static readonly IReadOnlyDictionary<string, string> ClientKeyToMarkerDirectory =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = ".claude",
            ["cursor"] = ".cursor",
            ["vscode"] = ".vscode",
            ["codex"] = ".codex"
        };

    /// <summary>
    ///     Resolves <c>--client=</c> argument into the configurators to run. <c>all</c> expands to
    ///     every configurator. Comma- or semicolon-separated keys are matched case-insensitively
    ///     against <see cref="IClientConfigurator.ClientKey" />. Throws <see cref="UserErrorException" />
    ///     for unknown keys, listing the valid set.
    /// </summary>
    internal static IReadOnlyList<IClientConfigurator> ResolveClientsFromArg(string clientArg)
    {
        if (String.Equals(clientArg.Trim(), AllClientsToken, StringComparison.OrdinalIgnoreCase))
        {
            return AllConfigurators;
        }

        string[] tokens = clientArg
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            throw new UserErrorException($"--client value is empty. Valid keys: {ValidClientKeys()}.");
        }

        List<IClientConfigurator> selected = new();
        foreach (string token in tokens)
        {
            IClientConfigurator? match = AllConfigurators
                .FirstOrDefault(c => String.Equals(c.ClientKey, token, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                throw new UserErrorException($"Unknown --client value '{token}'. Valid keys: {ValidClientKeys()}, or 'all'.");
            }

            if (!selected.Contains(match))
            {
                selected.Add(match);
            }
        }

        return selected;
    }

    /// <summary>
    ///     Auto-detects which clients to configure from marker directories in
    ///     <paramref name="projectRoot" />. Returns the unique list of matching configurators.
    /// </summary>
    internal static IReadOnlyList<IClientConfigurator> AutoDetectClients(string projectRoot)
    {
        List<IClientConfigurator> detected = new();
        foreach (IClientConfigurator configurator in AllConfigurators)
        {
            string markerDir = ClientKeyToMarkerDirectory[configurator.ClientKey];
            if (Directory.Exists(Path.Combine(projectRoot, markerDir)))
            {
                detected.Add(configurator);
            }
        }

        return detected;
    }

    public static async Task RunAsync(string? toolsValue, string? clientArg)
    {
        string workingDirectory = Directory.GetCurrentDirectory();

        if (toolsValue is not null && !ToolSelector.IsValid(toolsValue, out string? validationError))
        {
            Console.Error.WriteLine(validationError);
            Environment.ExitCode = 1;
            return;
        }

        IReadOnlyList<IClientConfigurator>? configurators = ResolveConfigurators(clientArg, workingDirectory);
        if (configurators is null)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("roz-mcp - Setup");
        Console.WriteLine(new string('=', 40));
        Console.WriteLine($"Working directory: {workingDirectory}");

        PrintStepHeader("Step 1: Checking environment...");

        List<EnvironmentChecker.CheckResult> checks =
            await EnvironmentChecker.RunAllChecksAsync(workingDirectory);
        Console.WriteLine(EnvironmentChecker.FormatResults(checks));

        bool allPassed = checks.All(r => r.Passed);
        if (!allPassed)
        {
            Console.WriteLine("Fix the issues above and re-run 'roz-mcp setup'.");
            Environment.ExitCode = 1;
            return;
        }

        var stepIndex = 2;
        foreach (IClientConfigurator configurator in configurators)
        {
            PrintStepHeader($"Step {stepIndex}: Configuring {configurator.DisplayName}...");
            await configurator.ConfigureAsync(workingDirectory, toolsValue);
            stepIndex++;
        }

        PrintStepHeader($"Step {stepIndex}: Validating workspace...");

        (bool validationPassed, TimeSpan coldLoadTime) = await ValidateWorkspaceAsync();

        foreach (IClientConfigurator configurator in configurators)
        {
            configurator.PrintPostInstallHints(coldLoadTime);
        }

        Console.WriteLine();
        if (validationPassed)
        {
            Console.WriteLine(BuildFinalMessage(configurators));
        }
        else
        {
            Console.WriteLine("Setup completed with warnings. The MCP server config has been created,");
            Console.WriteLine("but the workspace had loading issues. Check the diagnostics above.");
        }

        Console.WriteLine();
        Console.WriteLine($"Logs: {SerilogConfiguration.LogDirectory}\\roz-mcp-<date>.log");
        Console.WriteLine($"(Default level is Warning. Set {RozEnvVars.LogLevel.Name}=Information for lifecycle events.)");

        PrintCurrentEnvVars();
    }

    private static void PrintCurrentEnvVars()
    {
        List<string> set = RozEnvVars.All
            .Where(v => v.CurrentValue is not null)
            .Select(v => $"{v.Name}={v.CurrentValue}")
            .ToList();

        if (set.Count > 0)
        {
            Console.WriteLine($"Custom env vars: {String.Join(", ", set)}");
        }
    }

    private static IReadOnlyList<IClientConfigurator>? ResolveConfigurators(string? clientArg, string workingDirectory)
    {
        if (clientArg is not null)
        {
            try
            {
                return ResolveClientsFromArg(clientArg);
            }
            catch (UserErrorException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
                return null;
            }
        }

        IReadOnlyList<IClientConfigurator> detected = AutoDetectClients(workingDirectory);
        if (detected.Count == 0)
        {
            Console.WriteLine($"No client marker directories detected. Defaulting to Claude Code. Use --client=<{ValidClientKeys()}|all> to override.");
            return [AllConfigurators[0]];
        }

        if (detected.Count == 1)
        {
            return detected;
        }

        var detectedKeys = String.Join(", ", detected.Select(c => c.ClientKey));
        Console.Error.WriteLine($"Multiple client marker directories detected: {detectedKeys}.");
        Console.Error.WriteLine($"Re-run with --client=<{ValidClientKeys()}|all> (comma-separate for multiple).");
        Environment.ExitCode = 1;
        return null;
    }

    private static string BuildFinalMessage(IReadOnlyList<IClientConfigurator> configurators)
    {
        if (configurators.Count == 1)
        {
            return $"Setup complete. Restart {configurators[0].DisplayName} to activate the roz-mcp server.";
        }

        var list = String.Join(", ", configurators.Select(c => c.DisplayName));
        return $"Setup complete. Restart the configured clients ({list}) to activate the roz-mcp server.";
    }

    private static string ValidClientKeys() =>
        String.Join("|", AllConfigurators.Select(c => c.ClientKey));

    private static async Task<(bool Passed, TimeSpan ColdLoadTime)> ValidateWorkspaceAsync()
    {
        try
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MsBuildBootstrap.Initialize();
            }

            return await ValidateWorkspaceInternalAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: Could not validate workspace: {ex.Message}");
            return (false, TimeSpan.Zero);
        }
    }

    // NoInlining prevents the JIT from loading Roslyn workspace types before MsBuildBootstrap.Initialize()
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(bool Passed, TimeSpan ColdLoadTime)> ValidateWorkspaceInternalAsync()
    {
        string slnPath = FileUtility.DiscoverSolution();

        var sw = Stopwatch.StartNew();

        using var workspace = MSBuildWorkspace.Create();

        List<string> diagnostics = new();
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                diagnostics.Add(e.Diagnostic.Message);
            }
        });

        Solution solution = await workspace.OpenSolutionAsync(slnPath);
        sw.Stop();

        int projectCount = solution.Projects.Count();
        int docCount = solution.Projects.Sum(p => p.Documents.Count());

        Console.WriteLine($"  Loaded: {projectCount} projects, {docCount} documents");
        Console.WriteLine($"  Time: {sw.Elapsed.TotalSeconds:F1}s");

        PrintDiagnosticWarnings(diagnostics);

        return (diagnostics.Count == 0, sw.Elapsed);
    }

    private static void PrintStepHeader(string message)
    {
        Console.WriteLine();
        Console.WriteLine(message);
        Console.WriteLine();
    }

    private static void PrintDiagnosticWarnings(List<string> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return;
        }

        Console.WriteLine($"  Warnings ({diagnostics.Count}):");
        foreach (string diag in diagnostics.Take(5))
        {
            Console.WriteLine($"    - {diag}");
        }

        if (diagnostics.Count > 5)
        {
            Console.WriteLine($"    ... and {diagnostics.Count - 5} more");
        }
    }
}
