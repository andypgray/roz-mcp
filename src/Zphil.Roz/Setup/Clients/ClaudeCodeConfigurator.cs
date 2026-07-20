using System.Text.Json.Nodes;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Setup.Clients;

/// <summary>
///     Configures Claude Code. Classic mode writes <c>.mcp.json</c> (the generic MCP entry),
///     <c>.claude/settings.local.json</c> (a wildcard allow rule plus <c>ask</c> rules for the
///     mutating tools, so writes prompt), and the project-instructions snippet to <c>CLAUDE.md</c>.
///     Plugin mode — the roz-mcp plugin already registers the server — skips <c>.mcp.json</c>
///     (warning if an existing entry would double-register), routes <c>--tools</c> into
///     <c>.roz.json</c>, and writes the same permission split under the plugin tool-name prefix.
///     Also owns the <c>MCP_TIMEOUT</c> post-install hint, which is gated on cold-load duration.
/// </summary>
internal sealed class ClaudeCodeConfigurator : IClientConfigurator
{
    private const string McpServerName = "roz";
    private const string McpJsonFileName = ".mcp.json";
    private const string McpServersKey = "mcpServers";

    /// <summary>Tool-name prefix for a classic install: a project <c>.mcp.json</c> entry named <c>roz</c>.</summary>
    internal const string ClassicToolPrefix = $"mcp__{McpServerName}__";

    /// <summary>
    ///     Tool-name prefix under the roz-mcp plugin. Empirically pinned (plugin-readiness phase 3,
    ///     Claude Code 2.1.212): plugin MCP servers are keyed <c>plugin:roz-mcp:roz</c>, and tool
    ///     names embed the normalized key (characters outside <c>[a-zA-Z0-9_-]</c> become <c>_</c>).
    ///     The single production string to touch if the client ever changes the form — the plugin
    ///     skill and the setup tests carry independent copies that cross-check against this one.
    /// </summary>
    internal const string PluginToolPrefix = "mcp__plugin_roz-mcp_roz__";

    private const string RulesFileName = "CLAUDE.md";

    // Mutating tools routed to permissions.ask so they prompt before running, even though the
    // wildcard allow would otherwise auto-allow them — Claude Code evaluates ask ahead of allow.
    // Read-only tools stay frictionless; writes (incl. the solution-wide rename_symbol, which the
    // server's default preset loads) require confirmation, matching the project's
    // conservative-writes philosophy.
    private static readonly string[] WriteToolNames =
    [
        "edit_symbol",
        "rename_symbol",
        "replace_content",
        "apply_code_fix",
        "change_signature",
        "add_usings",
        "remove_unused_usings"
    ];

    private static readonly TimeSpan McpTimeoutHintThreshold = TimeSpan.FromSeconds(20);

    public string ClientKey => "claude";
    public string DisplayName => "Claude Code";

    public async Task ConfigureAsync(ClientSetupRequest request, CancellationToken ct = default)
    {
        if (request.PluginMode == ClaudePluginMode.Plugin)
        {
            await WarnIfClassicEntryDoubleRegistersAsync(request.ProjectRoot, ct);
            if (request.ToolsValue is not null)
            {
                await ProjectConfigWriter.MergeKeyAsync(
                    request.ProjectRoot, RozEnvVars.Tools.Name, request.ToolsValue, ct);
            }
        }
        else
        {
            await ConfigureMcpJsonAsync(request.ProjectRoot, request.ToolsValue, ct);
        }

        await ConfigureSettingsAsync(request.ProjectRoot, request.PluginMode, ct);
        await ProjectInstructionsConfigurator.WriteSnippetAsync(request.ProjectRoot, RulesFileName, ct);
    }

    public void PrintPostInstallHints(TimeSpan coldLoadTime)
    {
        if (coldLoadTime < McpTimeoutHintThreshold)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  Tip: cold workspace load took {coldLoadTime.TotalSeconds:F1}s, which is close to Claude Code's default");
        Console.WriteLine("       MCP_TIMEOUT (30s). Consider adding to .claude/settings.local.json:");
        Console.WriteLine("         \"env\": { \"MCP_TIMEOUT\": \"300000\" }");
        Console.WriteLine("       Note: MCP_TIMEOUT is global and affects all your MCP servers.");
    }

    /// <summary>The read-only wildcard allow pattern for <paramref name="mode" />'s tool-name prefix.</summary>
    internal static string WildcardFor(ClaudePluginMode mode) => $"{PrefixFor(mode)}*";

    /// <summary>The seven mutating-tool <c>ask</c> patterns for <paramref name="mode" />'s tool-name prefix.</summary>
    internal static string[] AskPatternsFor(ClaudePluginMode mode) =>
        WriteToolNames.Select(name => $"{PrefixFor(mode)}{name}").ToArray();

    private static string PrefixFor(ClaudePluginMode mode) =>
        mode == ClaudePluginMode.Plugin ? PluginToolPrefix : ClassicToolPrefix;

    /// <summary>
    ///     Creates or updates <c>.mcp.json</c> in the project root to register the roz-mcp server.
    /// </summary>
    /// <remarks>
    ///     Sets <c>ROZ_TOOLS=&lt;value&gt;</c> on the server entry's env block when
    ///     <paramref name="toolsValue" /> is non-null, shrinking the advertised tool surface.
    ///     When omitted, <c>ROZ_TOOLS</c> is left unset so the server applies its <c>default</c>
    ///     preset. Preserves sibling env keys (e.g. <c>ROZ_SOLUTION_PATH</c>) and sibling MCP
    ///     server entries across re-runs.
    /// </remarks>
    public static async Task ConfigureMcpJsonAsync(string projectRoot, string? toolsValue = null, CancellationToken ct = default)
    {
        string mcpJsonPath = Path.Combine(projectRoot, McpJsonFileName);
        bool written = await McpJsonWriter.MergeMcpServerEntryAsync(
            mcpJsonPath,
            McpServersKey,
            McpServerName,
            toolsValue,
            ct: ct);

        if (written)
        {
            Console.WriteLine($"  Updated: {Path.GetRelativePath(projectRoot, mcpJsonPath)}");
        }
    }

    /// <summary>
    ///     Plugin mode never touches <c>.mcp.json</c>, but an entry left behind by an earlier
    ///     classic setup would register the server twice (once via the plugin, once via the
    ///     project entry). Warn — never delete: removing user config is not setup's call.
    /// </summary>
    private static async Task WarnIfClassicEntryDoubleRegistersAsync(string projectRoot, CancellationToken ct)
    {
        string mcpJsonPath = Path.Combine(projectRoot, McpJsonFileName);
        JsonObject? root = await McpJsonWriter.ReadJsonFileAsync(mcpJsonPath, ct);
        if (root?[McpServersKey] is not JsonObject servers || servers[McpServerName] is null)
        {
            return;
        }

        Console.WriteLine($"  Warning: {McpJsonFileName} also registers '{McpServerName}'; with the plugin enabled, the server is registered twice.");
        Console.WriteLine($"  Remove the '{McpServerName}' entry from {McpJsonFileName} to fix (the file was left untouched).");
        Console.WriteLine("  For user-scope registrations, check 'claude mcp list'.");
    }

    /// <summary>
    ///     Creates or updates <c>.claude/settings.local.json</c>: adds the wildcard allow rule for
    ///     <paramref name="mode" />'s tool-name prefix and routes the mutating tools to <c>ask</c>
    ///     so they prompt before running. If the file exists but cannot be parsed, prints manual
    ///     instructions instead of overwriting.
    /// </summary>
    private static async Task ConfigureSettingsAsync(string projectRoot, ClaudePluginMode mode, CancellationToken ct)
    {
        string claudeDir = Path.Combine(projectRoot, ".claude");
        string settingsPath = Path.Combine(claudeDir, "settings.local.json");

        Directory.CreateDirectory(claudeDir);

        JsonObject? root = await McpJsonWriter.ReadJsonFileAsync(settingsPath, ct);
        if (root is null && File.Exists(settingsPath))
        {
            Console.WriteLine("  Skipping settings update to avoid data loss.");
            PrintManualPermissionInstructions(settingsPath, mode);
            return;
        }

        root ??= new JsonObject();

        AddPermission(root, mode);

        string json = root.ToJsonString(McpJsonWriter.IndentedOptions);
        // Atomic write so a crash can't leave a half-written settings.local.json (Utf8NoBom keeps
        // the output byte-identical to the prior plain File.WriteAllTextAsync).
        await AtomicFileWriter.WriteAtomicAsync(settingsPath, json, FileUtility.Utf8NoBom, ct);

        Console.WriteLine($"  Updated: {Path.GetRelativePath(projectRoot, settingsPath)}");
    }

    private static void AddPermission(JsonObject root, ClaudePluginMode mode)
    {
        if (root["permissions"] is not JsonObject permissions)
        {
            permissions = new JsonObject();
            root["permissions"] = permissions;
        }

        AddPatternsToList(permissions, "allow", [WildcardFor(mode)]);
        AddPatternsToList(permissions, "ask", AskPatternsFor(mode));
    }

    private static void AddPatternsToList(JsonObject permissions, string listName, IReadOnlyList<string> patterns)
    {
        if (permissions[listName] is not JsonArray array)
        {
            array = new JsonArray();
            permissions[listName] = array;
        }

        foreach (string pattern in patterns)
        {
            // A non-string element (number, bool, object) in the user's array must not crash the
            // whole setup — GetValue<string>() would throw. OfType<JsonValue> + TryGetValue skips
            // anything that isn't a string value.
            bool alreadyExists = array
                .OfType<JsonValue>()
                .Any(v => v.TryGetValue(out string? s) && s == pattern);

            if (!alreadyExists)
            {
                array.Add(pattern);
                Console.WriteLine($"  Added permission ({listName}): {pattern}");
            }
            else
            {
                Console.WriteLine($"  Permission already configured ({listName}): {pattern}");
            }
        }
    }

    private static void PrintManualPermissionInstructions(string settingsPath, ClaudePluginMode mode)
    {
        Console.WriteLine();
        Console.WriteLine($"  Please manually add \"{WildcardFor(mode)}\" to permissions.allow,");
        Console.WriteLine("  and the following write tools to permissions.ask (so edits prompt),");
        Console.WriteLine($"  in: {settingsPath}");
        foreach (string pattern in AskPatternsFor(mode))
        {
            Console.WriteLine($"    {pattern}");
        }
    }
}
