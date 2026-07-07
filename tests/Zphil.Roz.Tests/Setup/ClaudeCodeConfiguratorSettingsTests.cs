using System.Text.Json.Nodes;
using Zphil.Roz.Setup.Clients;
using Zphil.Roz.Tests.Setup.Fixtures;

namespace Zphil.Roz.Tests.Setup;

/// <summary>
///     Guards <see cref="ClaudeCodeConfigurator" />'s <c>settings.local.json</c> write path:
///     a non-string element in the user's <c>permissions.allow</c> array must not crash setup
///     (CR-16), the atomic write must leave no temp residue (CR-10), and the mutating tools must
///     be routed to <c>permissions.ask</c> (idempotently) so writes prompt while reads stay allowed.
/// </summary>
public class ClaudeCodeConfiguratorSettingsTests : IDisposable
{
    private const string PermissionPattern = "mcp__roz__*";

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

    private readonly TempProjectDirectory _projectRoot = new("roz-mcp-settings");

    public void Dispose() => _projectRoot.Dispose();

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
        await new ClaudeCodeConfigurator().ConfigureAsync(_projectRoot, null, TestContext.Current.CancellationToken);

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
        await new ClaudeCodeConfigurator().ConfigureAsync(_projectRoot, null, TestContext.Current.CancellationToken);

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
        var configurator = new ClaudeCodeConfigurator();
        await configurator.ConfigureAsync(_projectRoot, null, TestContext.Current.CancellationToken);
        await configurator.ConfigureAsync(_projectRoot, null, TestContext.Current.CancellationToken);

        // Assert — the ask list still holds each write tool exactly once.
        string settingsPath = Path.Combine(_projectRoot, ".claude", "settings.local.json");
        var root = JsonNode.Parse(
            await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken))!;

        StringsIn(root, "ask").ShouldBe(WriteToolAskPatterns, true);
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
