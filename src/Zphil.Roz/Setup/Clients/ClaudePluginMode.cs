namespace Zphil.Roz.Setup.Clients;

/// <summary>
///     How Claude Code gets the roz server in a project: <see cref="Classic" /> — setup registers
///     it via a project <c>.mcp.json</c> entry; <see cref="Plugin" /> — the roz-mcp plugin already
///     provides it, so setup writes plugin-prefixed permission rules and per-project config only.
/// </summary>
internal enum ClaudePluginMode
{
    Classic,
    Plugin
}
