using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Setup.Clients;

/// <summary>
///     Configures Codex CLI: writes <c>.codex/config.toml</c> with a
///     <c>[mcp_servers.roz]</c> sub-table, plus the project-instructions snippet to
///     <c>AGENTS.md</c>. Codex requires the project to be marked trusted on first launch — a
///     post-install reminder is printed regardless of cold-load time.
/// </summary>
internal sealed class CodexConfigurator : IClientConfigurator
{
    private const string McpServerName = "roz";
    private const string RulesFileName = "AGENTS.md";

    public string ClientKey => "codex";
    public string DisplayName => "Codex CLI";

    public async Task ConfigureAsync(ClientSetupRequest request, CancellationToken ct = default)
    {
        await ConfigureConfigTomlAsync(request.ProjectRoot, request.ToolsValue, ct);
        await ProjectInstructionsConfigurator.WriteSnippetAsync(request.ProjectRoot, RulesFileName, ct);
    }

    public void PrintPostInstallHints(TimeSpan coldLoadTime)
    {
        Console.WriteLine();
        Console.WriteLine("  Tip: Mark the project trusted on first Codex launch (otherwise this config is ignored).");
    }

    public static async Task ConfigureConfigTomlAsync(string projectRoot, string? toolsValue = null, CancellationToken ct = default)
    {
        string configTomlPath = Path.Combine(projectRoot, ".codex", "config.toml");

        Dictionary<string, string>? env = null;
        if (toolsValue is not null)
        {
            env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RozEnvVars.Tools.Name] = toolsValue
            };
        }

        bool written = await CodexTomlWriter.MergeMcpServerTableAsync(
            configTomlPath,
            McpServerName,
            "roz-mcp",
            Array.Empty<string>(),
            env,
            ct);

        if (written)
        {
            Console.WriteLine($"  Updated: {Path.GetRelativePath(projectRoot, configTomlPath)}");
        }
    }
}
