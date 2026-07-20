using System.Text.Json.Nodes;

namespace Zphil.Roz.Setup.Clients;

/// <summary>
///     Configures VS Code Copilot Chat: writes <c>.vscode/mcp.json</c> using the
///     <c>servers</c> key (not <c>mcpServers</c>) and a per-entry <c>"type": "stdio"</c>
///     field, plus the project-instructions snippet to <c>AGENTS.md</c>.
/// </summary>
internal sealed class VsCodeConfigurator : IClientConfigurator
{
    private const string McpServerName = "roz";
    private const string RulesFileName = "AGENTS.md";

    private static readonly IReadOnlyDictionary<string, JsonNode?> ExtraEntryFields =
        new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["type"] = "stdio"
        };

    public string ClientKey => "vscode";
    public string DisplayName => "VS Code Copilot Chat";

    public async Task ConfigureAsync(ClientSetupRequest request, CancellationToken ct = default)
    {
        await ConfigureMcpJsonAsync(request.ProjectRoot, request.ToolsValue, ct);
        await ProjectInstructionsConfigurator.WriteSnippetAsync(request.ProjectRoot, RulesFileName, ct);
    }

    public void PrintPostInstallHints(TimeSpan coldLoadTime) { }

    public static async Task ConfigureMcpJsonAsync(string projectRoot, string? toolsValue = null, CancellationToken ct = default)
    {
        string mcpJsonPath = Path.Combine(projectRoot, ".vscode", "mcp.json");
        bool written = await McpJsonWriter.MergeMcpServerEntryAsync(
            mcpJsonPath,
            "servers",
            McpServerName,
            toolsValue,
            ExtraEntryFields,
            ct);

        if (written)
        {
            Console.WriteLine($"  Updated: {Path.GetRelativePath(projectRoot, mcpJsonPath)}");
        }
    }
}
