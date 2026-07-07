namespace Zphil.Roz.Infrastructure;

/// <summary>
///     Single source of truth for every environment variable the roz-mcp server reads.
///     Each variable is a nested static class exposing its <c>Name</c> and a
///     <c>DefaultDescription</c> for setup/status surfaces; simple cases also expose a
///     <c>Read()</c> shortcut over <see cref="EnvParse" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Invariants</b>:
///     </para>
///     <list type="number">
///         <item>
///             <description>
///                 <b>No nested class here may depend on another nested class.</b> Each is a leaf
///                 whose only downstream dependencies are <see cref="EnvParse" /> and
///                 <see cref="Environment" />. Prevents static-init ordering bugs.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b><c>ROZ_TOOLS</c> resolution stays in <c>ToolSelector</c>.</b> The registry
///                 owns the name; the resolved <c>IReadOnlyList&lt;MethodInfo&gt;</c> result would force
///                 a Reflection/MCP SDK dependency on the registry.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b><c>MAX_MCP_OUTPUT_TOKENS</c> is included</b> despite its non-<c>ROZ_</c>
///                 prefix — the registry's intent is "every env var the server reads", regardless
///                 of who sets it. The drift test treats it as an explicit allow-list entry.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>This is not a DI service.</b> It is a static config inventory; no
///                 registration in the host builder.
///             </description>
///         </item>
///     </list>
/// </remarks>
internal static class RozEnvVars
{
    /// <summary>
    ///     Flat inventory used by <c>setup</c> and any future <c>--status</c> surface.
    ///     One entry per nested class; ordered for stable display, not alphabetised.
    /// </summary>
    public static IReadOnlyList<EnvVarInfo> All { get; } =
    [
        new(Tools.Name, Tools.DefaultDescription),
        new(SolutionPath.Name, SolutionPath.DefaultDescription),
        new(VsInstallPath.Name, VsInstallPath.DefaultDescription),
        new(LogLevel.Name, LogLevel.DefaultDescription),
        new(SessionId.Name, SessionId.DefaultDescription),
        new(ClaudeSessionId.Name, ClaudeSessionId.DefaultDescription),
        new(IdleTimeoutMinutes.Name, IdleTimeoutMinutes.DefaultDescription),
        new(DisableParentWatch.Name, DisableParentWatch.DefaultDescription),
        new(DisableAutoRefresh.Name, DisableAutoRefresh.DefaultDescription),
        new(DisableAnalyzers.Name, DisableAnalyzers.DefaultDescription),
        new(TestPaths.Name, TestPaths.DefaultDescription),
        new(TestNamespaces.Name, TestNamespaces.DefaultDescription),
        new(MaxResponseChars.Name, MaxResponseChars.DefaultDescription),
        new(MaxMcpOutputTokens.Name, MaxMcpOutputTokens.DefaultDescription)
    ];

    /// <summary>Selects which MCP tool methods get registered for this session.</summary>
    internal static class Tools
    {
        public const string Name = "ROZ_TOOLS";

        public const string DefaultDescription = "default preset (11 tools; excludes the risky write tools and unvalidated analyze_method)";
        // No Read() — Pipeline/ToolSelector.GetEnabledTools resolves preset/category/tool tokens.
    }

    /// <summary>Idle-timeout window before the server self-exits to drop orphaned BuildHost state.</summary>
    internal static class IdleTimeoutMinutes
    {
        public const string Name = "ROZ_IDLE_TIMEOUT_MINUTES";

        public const string DefaultDescription = "30 (0 disables watchdog; unset/blank/invalid falls back to 30)";
        // No Read() — Infrastructure/IdleTimeoutWatchdog.ParseTimeoutMinutes owns the "0 disables / warn on invalid" rule.
    }

    /// <summary>Opt-out for the Windows-only parent-process death watcher.</summary>
    internal static class DisableParentWatch
    {
        public const string Name = "ROZ_DISABLE_PARENT_WATCH";
        public const string DefaultDescription = "false (watcher enabled on Windows)";
        public static bool Read() => EnvParse.BoolTrue(Name);
    }

    /// <summary>Opt-out for the FileSystemWatcher / entry-time mtime sweep that reconciles external edits.</summary>
    internal static class DisableAutoRefresh
    {
        public const string Name = "ROZ_DISABLE_AUTO_REFRESH";
        public const string DefaultDescription = "false (auto-refresh enabled)";
        public static bool Read() => EnvParse.BoolTrue(Name);
    }

    /// <summary>Explicit solution-file path; bypasses CWD-walk discovery.</summary>
    internal static class SolutionPath
    {
        public const string Name = "ROZ_SOLUTION_PATH";

        public const string DefaultDescription = "unset (CWD-walk discovery)";
        // No Read() — FileUtility.DiscoverSolution validates the path and throws on a bad value.
    }

    /// <summary>Minimum Serilog log level written to the daily-rolling file sink.</summary>
    internal static class LogLevel
    {
        public const string Name = "ROZ_LOG_LEVEL";

        public const string DefaultDescription = "Warning (Serilog or Microsoft.Extensions.Logging names accepted)";
        // No Read() — SerilogConfiguration.ParseLogLevel accepts both enum families.
    }

    /// <summary>Project-controlled session-id override; wins over the Claude-injected id (log correlation).</summary>
    internal static class SessionId
    {
        public const string Name = "ROZ_SESSION_ID";

        public const string DefaultDescription = "unset (overrides CLAUDE_CODE_SESSION_ID; per-process GUID when unset)";
        // No Read() — Infrastructure/SessionContext.Resolve() owns the precedence chain.
    }

    /// <summary>Session id auto-injected by Claude Code into stdio MCP servers; consumed for log correlation.</summary>
    internal static class ClaudeSessionId
    {
        public const string Name = "CLAUDE_CODE_SESSION_ID";

        public const string DefaultDescription = "set by Claude Code, not the roz-mcp server (log correlation; GUID fallback when unset)";
        // No Read() — Infrastructure/SessionContext.Resolve() owns the precedence chain.
    }

    /// <summary>Overrides the auto-selected Visual Studio install root for MSBuild registration.</summary>
    internal static class VsInstallPath
    {
        public const string Name = "ROZ_VS_INSTALL_PATH";

        public const string DefaultDescription = "unset (vswhere auto-selects, preferring stable VS 16/17 over preview)";
        // No Read() — MsBuildBootstrap throws InvalidOperationException with a path-specific hint.
    }

    /// <summary>Hard cap on a single tool response's character count before truncation.</summary>
    internal static class MaxResponseChars
    {
        public const string Name = "ROZ_MAX_RESPONSE_CHARS";

        public const string DefaultDescription = "25,000 chars (falls back to MAX_MCP_OUTPUT_TOKENS * 2.5 when unset)";
        // No Read() — ResponseTruncator owns the two-step fallback chain to MaxMcpOutputTokens.
    }

    /// <summary>
    ///     Token budget the MCP client (e.g. Claude Code) sets for tool output; used as a fallback
    ///     character cap when <see cref="MaxResponseChars" /> is unset.
    /// </summary>
    internal static class MaxMcpOutputTokens
    {
        public const string Name = "MAX_MCP_OUTPUT_TOKENS";

        public const string DefaultDescription = "set by MCP client, not the roz-mcp server (fallback when ROZ_MAX_RESPONSE_CHARS is unset)";
        // No Read() — ResponseTruncator owns the two-step fallback chain.
    }

    /// <summary>Kill-switch for analyzer execution in <c>get_diagnostics</c>.</summary>
    internal static class DisableAnalyzers
    {
        public const string Name = "ROZ_DISABLE_ANALYZERS";
        public const string DefaultDescription = "false (analyzers enabled)";
        public static bool Read() => EnvParse.BoolTrue(Name);
    }

    /// <summary>User-configured path-prefix overrides for test-project classification.</summary>
    internal static class TestPaths
    {
        public const string Name = "ROZ_TEST_PATHS";

        public const string DefaultDescription = "empty (no path-prefix overrides)";
        // No Read() — TestClassifier consumes EnvParse.DelimitedList and applies path normalisation.
    }

    /// <summary>User-configured namespace-prefix overrides for test-project classification.</summary>
    internal static class TestNamespaces
    {
        public const string Name = "ROZ_TEST_NAMESPACES";

        public const string DefaultDescription = "empty (no namespace-prefix overrides)";
        // No Read() — TestClassifier consumes EnvParse.DelimitedList directly.
    }
}

/// <summary>
///     Display-friendly snapshot of an env var: its <see cref="Name" />, the prose description
///     of its default, and the live <see cref="CurrentValue" /> (read on access, so the
///     inventory itself doesn't cache shell state).
/// </summary>
/// <remarks>
///     <see cref="DefaultDescription" /> exists for a future <c>setup --status</c> surface that
///     lists every var with its default; today's <c>SetupCommand</c> only prints
///     <see cref="Name" /> and <see cref="CurrentValue" />.
/// </remarks>
// ReSharper disable once NotAccessedPositionalProperty.Global -- consumed by docs and future setup --status.
internal sealed record EnvVarInfo(string Name, string DefaultDescription)
{
    public string? CurrentValue => Environment.GetEnvironmentVariable(Name);
}
