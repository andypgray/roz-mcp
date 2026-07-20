using System.Text.Json.Nodes;

namespace Zphil.Roz.Setup.Clients;

/// <summary>
///     Decides whether a project gets the roz server from the roz-mcp Claude Code plugin — in
///     which case setup must not also register it via <c>.mcp.json</c>. Scans the same settings
///     files Claude Code reads for plugin enablement, nearest scope first:
///     <c>.claude/settings.local.json</c>, <c>.claude/settings.json</c>, then the user-level
///     settings file. The first file whose <c>enabledPlugins</c> mentions any
///     <c>roz-mcp@&lt;marketplace&gt;</c> key decides for all lower-precedence files, mirroring
///     Claude Code's own local &gt; project &gt; user override order.
/// </summary>
internal static class ClaudePluginDetector
{
    private const string PluginKeyPrefix = "roz-mcp@";

    /// <summary>The user-level settings file scanned last: <c>.claude/settings.json</c> under the user profile.</summary>
    internal static string DefaultUserSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// <summary>
    ///     Scans project-then-user settings for roz-mcp plugin enablement. Missing files, files
    ///     that cannot be parsed (a console warning is printed — setup is a console flow), and
    ///     files without an <c>enabledPlugins</c> object are skipped.
    /// </summary>
    internal static async Task<ClaudePluginDetection> DetectAsync(
        string projectRoot,
        string userSettingsPath,
        CancellationToken ct = default)
    {
        string[] scanOrder =
        [
            Path.Combine(projectRoot, ".claude", "settings.local.json"),
            Path.Combine(projectRoot, ".claude", "settings.json"),
            userSettingsPath
        ];

        foreach (string settingsPath in scanOrder)
        {
            JsonObject? root = await McpJsonWriter.ReadJsonFileAsync(settingsPath, ct);
            if (root?["enabledPlugins"] is not JsonObject enabledPlugins)
            {
                continue;
            }

            ClaudePluginDetection? decision = DecideFrom(enabledPlugins, settingsPath);
            if (decision is not null)
            {
                return decision;
            }
        }

        return new ClaudePluginDetection(false, null, null);
    }

    /// <summary>
    ///     One file's verdict: <c>null</c> when it never mentions the plugin; otherwise plugin mode
    ///     iff any <c>roz-mcp@&lt;marketplace&gt;</c> key is strictly boolean <c>true</c>. A
    ///     <c>false</c> (or non-boolean) value is an explicit disable and still decides — a
    ///     project-level disable must override a user-level enable.
    /// </summary>
    private static ClaudePluginDetection? DecideFrom(JsonObject enabledPlugins, string settingsPath)
    {
        string? firstMatch = null;
        foreach ((string key, JsonNode? value) in enabledPlugins)
        {
            if (!key.StartsWith(PluginKeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            firstMatch ??= key;
            if (value is JsonValue jsonValue && jsonValue.TryGetValue(out bool enabled) && enabled)
            {
                return new ClaudePluginDetection(true, key, settingsPath);
            }
        }

        return firstMatch is null ? null : new ClaudePluginDetection(false, firstMatch, settingsPath);
    }
}

/// <summary>
///     Outcome of <see cref="ClaudePluginDetector.DetectAsync" />: whether the roz-mcp plugin is
///     enabled for the project, plus the matched <c>roz-mcp@&lt;marketplace&gt;</c> key and the
///     settings file that decided — both <c>null</c> when no scanned file mentions the plugin.
/// </summary>
internal sealed record ClaudePluginDetection(bool IsPlugin, string? MatchedKey, string? SourceFile);
