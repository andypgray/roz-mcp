namespace Zphil.Roz.Setup.Clients;

/// <summary>
///     Configures Cursor: writes <c>.cursor/mcp.json</c> with the same <c>mcpServers</c> shape
///     Claude uses, plus the project-instructions snippet to <c>AGENTS.md</c>.
/// </summary>
internal sealed class CursorConfigurator : IClientConfigurator
{
    private const string McpServerName = "roz";
    private const string RulesFileName = "AGENTS.md";

    public string ClientKey => "cursor";
    public string DisplayName => "Cursor";

    public async Task ConfigureAsync(string projectRoot, string? toolsValue, CancellationToken ct = default)
    {
        await ConfigureMcpJsonAsync(projectRoot, toolsValue, ct);
        await ProjectInstructionsConfigurator.AppendSnippetAsync(projectRoot, RulesFileName, ct);
    }

    public void PrintPostInstallHints(TimeSpan coldLoadTime) { }

    public static async Task ConfigureMcpJsonAsync(string projectRoot, string? toolsValue = null, CancellationToken ct = default)
    {
        string mcpJsonPath = Path.Combine(projectRoot, ".cursor", "mcp.json");
        bool written = await McpJsonWriter.MergeMcpServerEntryAsync(
            mcpJsonPath,
            "mcpServers",
            McpServerName,
            toolsValue,
            ct: ct);

        if (written)
        {
            Console.WriteLine($"  Updated: {Path.GetRelativePath(projectRoot, mcpJsonPath)}");
        }
    }
}
