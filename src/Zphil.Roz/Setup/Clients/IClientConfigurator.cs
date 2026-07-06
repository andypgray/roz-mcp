namespace Zphil.Roz.Setup.Clients;

/// <summary>
///     One implementation per supported MCP client. <see cref="ConfigureAsync" /> writes the
///     client's MCP server entry and the project-instructions snippet to that client's native
///     rules file. <see cref="PrintPostInstallHints" /> emits any client-specific guidance
///     (e.g. Claude Code's <c>MCP_TIMEOUT</c> hint, Codex's "mark project trusted" reminder).
/// </summary>
internal interface IClientConfigurator
{
    /// <summary>Stable CLI key (e.g. <c>claude</c>, <c>cursor</c>, <c>vscode</c>, <c>codex</c>).</summary>
    public string ClientKey { get; }

    /// <summary>Human-readable name used in console step headers and final messages.</summary>
    public string DisplayName { get; }

    /// <summary>
    ///     Writes this client's MCP server entry and project-instructions snippet under
    ///     <paramref name="projectRoot" />. <paramref name="toolsValue" /> is the parsed value of
    ///     <c>--tools=</c> and flows into <c>ROZ_TOOLS</c> on the server's env block.
    /// </summary>
    public Task ConfigureAsync(string projectRoot, string? toolsValue, CancellationToken ct = default);

    /// <summary>
    ///     Prints any post-install guidance specific to this client. Called after the workspace
    ///     validation step, so <paramref name="coldLoadTime" /> is available for clients that
    ///     gate hints on it (Claude Code).
    /// </summary>
    public void PrintPostInstallHints(TimeSpan coldLoadTime);
}
