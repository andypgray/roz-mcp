namespace Zphil.Roz.Infrastructure;

/// <summary>
///     Resolves a stable session id for this server process so every log line can be told
///     apart from other processes sharing the daily-rolling file, and — when launched by
///     Claude Code — correlated with that client's session.
/// </summary>
/// <remarks>
///     Precedence: <c>ROZ_SESSION_ID</c> (universal project override) →
///     <c>CLAUDE_CODE_SESSION_ID</c> (auto-injected by Claude Code) → a per-process GUID.
///     Only Claude Code auto-delivers a session id to stdio MCP servers; Cursor and VS Code
///     inject none, and Codex's <c>CODEX_THREAD_ID</c> is not exposed to MCP servers
///     (openai/codex#19937) — non-Claude clients route through <c>ROZ_SESSION_ID</c> or
///     fall back to the per-process GUID.
/// </remarks>
internal static class SessionContext
{
    /// <summary>Session id for this process; resolved once on first access.</summary>
    public static string Current { get; } = Resolve();

    /// <summary>
    ///     Resolves the id from the env-var chain, else a short per-process GUID. Reads the
    ///     live environment each call so it is unit-testable; production uses cached <see cref="Current" />.
    /// </summary>
    internal static string Resolve() =>
        EnvParse.RawString(RozEnvVars.SessionId.Name)
        ?? EnvParse.RawString(RozEnvVars.ClaudeSessionId.Name)
        ?? Guid.NewGuid().ToString("N")[..8];
}
