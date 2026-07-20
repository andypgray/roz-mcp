using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Zphil.Roz.Setup.Clients;
using Zphil.Roz.Tests.Setup.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Setup;

/// <summary>
///     Guards <see cref="ClaudeCodeConfigurator" />'s <c>settings.local.json</c> write path:
///     a non-string element in the user's <c>permissions.allow</c> array must not crash setup
///     (CR-16), the atomic write must leave no temp residue (CR-10), and the mutating tools must
///     be routed to <c>permissions.ask</c> (idempotently) so writes prompt while reads stay allowed.
///     Plugin mode writes the same split under the plugin tool-name prefix and never creates or
///     mutates <c>.mcp.json</c>.
/// </summary>
public class ClaudeCodeConfiguratorSettingsTests : IDisposable
{
    private const string PermissionPattern = "mcp__roz__*";
    private const string PluginPermissionPattern = "mcp__plugin_roz-mcp_roz__*";

    // Pinned independently of the production list: if a wrong edit drops or renames a write tool,
    // these tests must fail rather than mirror the mistake.
    private static readonly string[] WriteToolAskPatterns =
    [
        "mcp__roz__edit_symbol",
        "mcp__roz__rename_symbol",
        "mcp__roz__replace_content",
        "mcp__roz__apply_code_fix",
        "mcp__roz__change_signature",
        "mcp__roz__add_usings",
        "mcp__roz__remove_unused_usings"
    ];

    // Same drift-catch idiom for the plugin prefix (empirically pinned: Claude Code keys plugin
    // servers `plugin:roz-mcp:roz` and normalizes `:` to `_` in tool names). These literals also
    // cross-check ClaudeCodeConfigurator.PluginToolPrefix against the plugin skill's copies.
    private static readonly string[] PluginWriteToolAskPatterns =
    [
        "mcp__plugin_roz-mcp_roz__edit_symbol",
        "mcp__plugin_roz-mcp_roz__rename_symbol",
        "mcp__plugin_roz-mcp_roz__replace_content",
        "mcp__plugin_roz-mcp_roz__apply_code_fix",
        "mcp__plugin_roz-mcp_roz__change_signature",
        "mcp__plugin_roz-mcp_roz__add_usings",
        "mcp__plugin_roz-mcp_roz__remove_unused_usings"
    ];

    private readonly TempProjectDirectory _projectRoot = new("roz-mcp-settings");

    public void Dispose() => _projectRoot.Dispose();

    private Task ConfigureAsync(ClaudePluginMode mode = ClaudePluginMode.Classic, string? toolsValue = null) =>
        new ClaudeCodeConfigurator().ConfigureAsync(
            new ClientSetupRequest(_projectRoot, toolsValue, mode), TestContext.Current.CancellationToken);

    [Fact]
    public async Task ConfigureAsync_AllowArrayHasNonStringElement_DoesNotThrowAndAddsPermission()
    {
        // Arrange — the user's allow array mixes a number, a bool, and a string. The old code's
        // n.GetValue<string>() threw on the non-string elements — and only AFTER .mcp.json had
        // already been written, leaving setup half-applied.
        string claudeDir = Path.Combine(_projectRoot, ".claude");
        Directory.CreateDirectory(claudeDir);
        string settingsPath = Path.Combine(claudeDir, "settings.local.json");
        JsonObject initial = new()
        {
            ["permissions"] = new JsonObject
            {
                ["allow"] = new JsonArray(42, true, "Bash(ls)")
            }
        };
        await File.WriteAllTextAsync(settingsPath, initial.ToJsonString(), TestContext.Current.CancellationToken);

        // Act — must not throw (this is the regression assertion for CR-16).
        await ConfigureAsync();

        // Assert — the roslyn permission was added alongside the preserved non-string siblings.
        var root = JsonNode.Parse(
            await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken))!;
        JsonArray allow = root["permissions"]!["allow"]!.AsArray();
        allow.OfType<JsonValue>()
            .Any(v => v.TryGetValue(out string? s) && s == PermissionPattern)
            .ShouldBeTrue();

        // CR-10: the atomic write left exactly settings.local.json in .claude — no temp residue.
        string[] files = Directory.GetFiles(claudeDir);
        files.ShouldHaveSingleItem();
        Path.GetFileName(files[0]).ShouldBe("settings.local.json");
    }

    [Fact]
    public async Task ConfigureAsync_FreshProject_AllowsReadsAndRoutesWritesToAsk()
    {
        // Arrange — a project with no prior settings.local.json.
        string settingsPath = Path.Combine(_projectRoot, ".claude", "settings.local.json");

        // Act
        await ConfigureAsync();

        // Assert — the read-only wildcard is allowed, and every mutating tool is routed to ask so it
        // prompts before running (Claude Code evaluates ask ahead of the wildcard allow).
        var root = JsonNode.Parse(
            await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken))!;

        StringsIn(root, "allow").ShouldContain(PermissionPattern);
        StringsIn(root, "ask").ShouldBe(WriteToolAskPatterns, true);
    }

    [Fact]
    public async Task ConfigureAsync_RunTwice_DoesNotDuplicateAskRules()
    {
        // Arrange / Act — running setup twice must be idempotent.
        await ConfigureAsync();
        await ConfigureAsync();

        // Assert — the ask list still holds each write tool exactly once.
        string settingsPath = Path.Combine(_projectRoot, ".claude", "settings.local.json");
        var root = JsonNode.Parse(
            await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken))!;

        StringsIn(root, "ask").ShouldBe(WriteToolAskPatterns, true);
    }

    /// <summary>
    ///     A newly added editing/usings tool must not escape the ask gate: the wildcard allow
    ///     would auto-approve it, and without a matching ask entry it would run unprompted,
    ///     silently breaking the conservative-writes design. <c>WriteToolNames</c> is maintained
    ///     by hand, so derive the expected set from the same <c>[McpServerTool]</c> attributes
    ///     <c>ToolSelector</c>'s category map reads.
    /// </summary>
    [Fact]
    public void AskPatternsFor_CoverEveryEditingAndUsingsTool()
    {
        string[] mutatingToolNames = new[] { typeof(CodeEditTools), typeof(UsingDirectiveTools) }
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .ToArray();

        mutatingToolNames.ShouldNotBeEmpty();

        string[] askPatterns = ClaudeCodeConfigurator.AskPatternsFor(ClaudePluginMode.Classic);

        askPatterns.ShouldBe(mutatingToolNames.Select(n => $"mcp__roz__{n}").ToArray(), true);
    }

    [Fact]
    public async Task ConfigureAsync_PluginMode_WritesPluginPrefixedPermissionSplit()
    {
        // Act
        await ConfigureAsync(ClaudePluginMode.Plugin);

        // Assert — same allow/ask split as classic, under the plugin tool-name prefix.
        string settingsPath = Path.Combine(_projectRoot, ".claude", "settings.local.json");
        var root = JsonNode.Parse(
            await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken))!;

        StringsIn(root, "allow").ShouldContain(PluginPermissionPattern);
        StringsIn(root, "ask").ShouldBe(PluginWriteToolAskPatterns, true);
    }

    [Fact]
    public async Task ConfigureAsync_PluginModeRunTwice_DoesNotDuplicateRules()
    {
        // Arrange / Act
        await ConfigureAsync(ClaudePluginMode.Plugin);
        await ConfigureAsync(ClaudePluginMode.Plugin);

        // Assert
        string settingsPath = Path.Combine(_projectRoot, ".claude", "settings.local.json");
        var root = JsonNode.Parse(
            await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken))!;

        StringsIn(root, "allow").Count(p => p == PluginPermissionPattern).ShouldBe(1);
        StringsIn(root, "ask").ShouldBe(PluginWriteToolAskPatterns, true);
    }

    [Fact]
    public async Task ConfigureAsync_PluginMode_DoesNotCreateMcpJson()
    {
        // Act — the plugin already registers the server; a .mcp.json entry would double-register.
        await ConfigureAsync(ClaudePluginMode.Plugin);

        // Assert
        File.Exists(Path.Combine(_projectRoot, ".mcp.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task ConfigureAsync_PluginModeWithExistingRozEntry_WarnsButLeavesMcpJsonByteIdentical()
    {
        // Arrange — a classic entry left behind by an earlier setup run (deliberate formatting
        // quirks: single-space indent, trailing newline — a rewrite would normalize them away).
        string mcpJsonPath = Path.Combine(_projectRoot, ".mcp.json");
        const string Existing = "{\n \"mcpServers\": { \"roz\": { \"command\": \"roz-mcp\" } }\n}\n";
        await File.WriteAllTextAsync(mcpJsonPath, Existing, TestContext.Current.CancellationToken);

        // Act
        await ConfigureAsync(ClaudePluginMode.Plugin);

        // Assert — warn-only: the file is not rewritten, reformatted, or deleted.
        string after = await File.ReadAllTextAsync(mcpJsonPath, TestContext.Current.CancellationToken);
        after.ShouldBe(Existing);
    }

    [Fact]
    public async Task ConfigureAsync_PluginModeWithTools_WritesRozJson()
    {
        // Act — the plugin's launch command is global, so --tools has no env block to land in;
        // it goes to .roz.json for the seeder to pick up.
        await ConfigureAsync(ClaudePluginMode.Plugin, "read");

        // Assert
        string configPath = Path.Combine(_projectRoot, ".roz.json");
        var root = JsonNode.Parse(
            await File.ReadAllTextAsync(configPath, TestContext.Current.CancellationToken))!;
        root["ROZ_TOOLS"]!.GetValue<string>().ShouldBe("read");
    }

    [Fact]
    public async Task ConfigureAsync_PluginModeWithoutTools_WritesNoRozJson()
    {
        // Act
        await ConfigureAsync(ClaudePluginMode.Plugin);

        // Assert — no --tools, no file: an empty .roz.json would still shadow a parent's config.
        File.Exists(Path.Combine(_projectRoot, ".roz.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task ConfigureAsync_PluginModeSettingsUnparseable_LeavesFileUntouched()
    {
        // Arrange — an unparseable settings.local.json must never be overwritten; setup prints
        // manual instructions (with the plugin prefix in plugin mode) instead.
        string claudeDir = Path.Combine(_projectRoot, ".claude");
        Directory.CreateDirectory(claudeDir);
        string settingsPath = Path.Combine(claudeDir, "settings.local.json");
        const string Broken = "{ not json !";
        await File.WriteAllTextAsync(settingsPath, Broken, TestContext.Current.CancellationToken);

        // Act — must not throw and must not rewrite.
        await ConfigureAsync(ClaudePluginMode.Plugin);

        // Assert
        string after = await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken);
        after.ShouldBe(Broken);
    }

    [Fact]
    public async Task ConfigureAsync_ClassicMode_StillWritesClassicPrefixAndMcpJson()
    {
        // Act — the classic path must be unchanged by the plugin-mode split.
        await ConfigureAsync();

        // Assert
        File.Exists(Path.Combine(_projectRoot, ".mcp.json")).ShouldBeTrue();
        File.Exists(Path.Combine(_projectRoot, ".roz.json")).ShouldBeFalse();

        string settingsPath = Path.Combine(_projectRoot, ".claude", "settings.local.json");
        var root = JsonNode.Parse(
            await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken))!;
        StringsIn(root, "allow").ShouldContain(PermissionPattern);
        StringsIn(root, "allow").ShouldNotContain(PluginPermissionPattern);
    }

    private static List<string> StringsIn(JsonNode root, string listName)
    {
        List<string> values = [];
        foreach (JsonValue value in root["permissions"]![listName]!.AsArray().OfType<JsonValue>())
        {
            if (value.TryGetValue(out string? s))
            {
                values.Add(s);
            }
        }

        return values;
    }
}
