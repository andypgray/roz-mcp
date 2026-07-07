using System.Text.Json.Nodes;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Setup.Clients;

/// <summary>
///     Configures Claude Code: writes <c>.mcp.json</c> (the generic MCP entry),
///     <c>.claude/settings.local.json</c> (the <c>mcp__roz__*</c> allow rule plus <c>ask</c>
///     rules for the mutating tools, so writes prompt), and the project-instructions snippet to
///     <c>CLAUDE.md</c>. Also owns the <c>MCP_TIMEOUT</c> post-install hint, which is gated on
///     cold-load duration.
/// </summary>
internal sealed class ClaudeCodeConfigurator : IClientConfigurator
{
    private const string McpServerName = "roz";
    private const string ToolPrefix = $"mcp__{McpServerName}__";
    private const string PermissionPattern = $"{ToolPrefix}*";

    private const string RulesFileName = "CLAUDE.md";

    // Mutating tools routed to permissions.ask so they prompt before running, even though the
    // PermissionPattern wildcard would otherwise auto-allow them — Claude Code evaluates ask ahead
    // of allow. Read-only tools stay frictionless; writes (incl. the solution-wide rename_symbol,
    // which the server's default preset loads) require confirmation, matching the project's
    // conservative-writes philosophy.
    private static readonly string[] WriteToolAskPatterns =
    [
        $"{ToolPrefix}edit_symbol",
        $"{ToolPrefix}rename_symbol",
        $"{ToolPrefix}replace_content",
        $"{ToolPrefix}apply_code_fix",
        $"{ToolPrefix}change_signature",
        $"{ToolPrefix}add_usings",
        $"{ToolPrefix}remove_unused_usings"
    ];

    private static readonly TimeSpan McpTimeoutHintThreshold = TimeSpan.FromSeconds(20);

    public string ClientKey => "claude";
    public string DisplayName => "Claude Code";

    public async Task ConfigureAsync(string projectRoot, string? toolsValue, CancellationToken ct = default)
    {
        await ConfigureMcpJsonAsync(projectRoot, toolsValue, ct);
        await ConfigureSettingsAsync(projectRoot, ct);
        await ProjectInstructionsConfigurator.AppendSnippetAsync(projectRoot, RulesFileName, ct);
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
        string mcpJsonPath = Path.Combine(projectRoot, ".mcp.json");
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

    /// <summary>
    ///     Creates or updates <c>.claude/settings.local.json</c>: adds the <c>mcp__roz__*</c>
    ///     allow rule and routes the mutating tools to <c>ask</c> so they prompt before running.
    ///     If the file exists but cannot be parsed, prints manual instructions instead of overwriting.
    /// </summary>
    private static async Task ConfigureSettingsAsync(string projectRoot, CancellationToken ct)
    {
        string claudeDir = Path.Combine(projectRoot, ".claude");
        string settingsPath = Path.Combine(claudeDir, "settings.local.json");

        Directory.CreateDirectory(claudeDir);

        JsonObject? root = await McpJsonWriter.ReadJsonFileAsync(settingsPath, ct);
        if (root is null && File.Exists(settingsPath))
        {
            Console.WriteLine("  Skipping settings update to avoid data loss.");
            PrintManualPermissionInstructions(settingsPath);
            return;
        }

        root ??= new JsonObject();

        AddPermission(root);

        string json = root.ToJsonString(McpJsonWriter.IndentedOptions);
        // Atomic write so a crash can't leave a half-written settings.local.json (Utf8NoBom keeps
        // the output byte-identical to the prior plain File.WriteAllTextAsync).
        await AtomicFileWriter.WriteAtomicAsync(settingsPath, json, FileUtility.Utf8NoBom, ct);

        Console.WriteLine($"  Updated: {Path.GetRelativePath(projectRoot, settingsPath)}");
    }

    private static void AddPermission(JsonObject root)
    {
        if (root["permissions"] is not JsonObject permissions)
        {
            permissions = new JsonObject();
            root["permissions"] = permissions;
        }

        AddPatternsToList(permissions, "allow", [PermissionPattern]);
        AddPatternsToList(permissions, "ask", WriteToolAskPatterns);
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

    private static void PrintManualPermissionInstructions(string settingsPath)
    {
        Console.WriteLine();
        Console.WriteLine($"  Please manually add \"{PermissionPattern}\" to permissions.allow,");
        Console.WriteLine("  and the following write tools to permissions.ask (so edits prompt),");
        Console.WriteLine($"  in: {settingsPath}");
        foreach (string pattern in WriteToolAskPatterns)
        {
            Console.WriteLine($"    {pattern}");
        }
    }
}
