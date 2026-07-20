namespace Zphil.Roz.Setup.Clients;

/// <summary>
///     Inputs shared by every client configurator run: the project root receiving config files,
///     the parsed <c>--tools=</c> value (flows into <c>ROZ_TOOLS</c>), and — Claude Code only —
///     whether the project gets the server from the roz-mcp plugin instead of a project
///     <c>.mcp.json</c> entry. The other clients ignore <see cref="PluginMode" />.
/// </summary>
internal sealed record ClientSetupRequest(
    string ProjectRoot,
    string? ToolsValue,
    ClaudePluginMode PluginMode = ClaudePluginMode.Classic);
