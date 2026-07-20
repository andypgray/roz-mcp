using Zphil.Roz.Setup.Clients;
using Zphil.Roz.Tests.Setup.Fixtures;

namespace Zphil.Roz.Tests.Setup;

/// <summary>
///     Pins <see cref="ClaudePluginDetector" />'s scan semantics: nearest scope decides
///     (settings.local.json &gt; settings.json &gt; user settings), any
///     <c>roz-mcp@&lt;marketplace&gt;</c> key counts, only a strict boolean <c>true</c> enables,
///     and unparseable files are skipped rather than deciding. All paths are injected temp dirs —
///     no real user settings are read.
/// </summary>
public class ClaudePluginDetectorTests : IDisposable
{
    private readonly TempProjectDirectory _projectRoot = new("roz-mcp-plugin-detect");
    private readonly TempProjectDirectory _userDir = new("roz-mcp-plugin-user");

    private string UserSettingsPath => Path.Combine(_userDir, "settings.json");

    public void Dispose()
    {
        _projectRoot.Dispose();
        _userDir.Dispose();
    }

    private async Task WriteProjectSettingsAsync(string fileName, string json)
    {
        string claudeDir = Path.Combine(_projectRoot, ".claude");
        Directory.CreateDirectory(claudeDir);
        await File.WriteAllTextAsync(Path.Combine(claudeDir, fileName), json, TestContext.Current.CancellationToken);
    }

    private Task<ClaudePluginDetection> DetectAsync() =>
        ClaudePluginDetector.DetectAsync(_projectRoot, UserSettingsPath, TestContext.Current.CancellationToken);

    [Fact]
    public async Task DetectAsync_NoSettingsFiles_ReturnsClassicWithNoSource()
    {
        ClaudePluginDetection detection = await DetectAsync();

        detection.IsPlugin.ShouldBeFalse();
        detection.MatchedKey.ShouldBeNull();
        detection.SourceFile.ShouldBeNull();
    }

    [Fact]
    public async Task DetectAsync_UserSettingsEnabled_ReturnsPlugin()
    {
        await File.WriteAllTextAsync(
            UserSettingsPath,
            """{ "enabledPlugins": { "roz-mcp@roz-mcp": true } }""",
            TestContext.Current.CancellationToken);

        ClaudePluginDetection detection = await DetectAsync();

        detection.IsPlugin.ShouldBeTrue();
        detection.MatchedKey.ShouldBe("roz-mcp@roz-mcp");
        detection.SourceFile.ShouldBe(UserSettingsPath);
    }

    [Fact]
    public async Task DetectAsync_LocalDisableOverridesUserEnable_ReturnsClassic()
    {
        await WriteProjectSettingsAsync("settings.local.json", """{ "enabledPlugins": { "roz-mcp@roz-mcp": false } }""");
        await File.WriteAllTextAsync(
            UserSettingsPath,
            """{ "enabledPlugins": { "roz-mcp@roz-mcp": true } }""",
            TestContext.Current.CancellationToken);

        ClaudePluginDetection detection = await DetectAsync();

        detection.IsPlugin.ShouldBeFalse();
        detection.MatchedKey.ShouldBe("roz-mcp@roz-mcp");
        detection.SourceFile.ShouldBe(Path.Combine(_projectRoot, ".claude", "settings.local.json"));
    }

    [Fact]
    public async Task DetectAsync_ProjectSharedSettingsEnabled_ReturnsPlugin()
    {
        await WriteProjectSettingsAsync("settings.json", """{ "enabledPlugins": { "roz-mcp@roz-mcp": true } }""");

        ClaudePluginDetection detection = await DetectAsync();

        detection.IsPlugin.ShouldBeTrue();
        detection.SourceFile.ShouldBe(Path.Combine(_projectRoot, ".claude", "settings.json"));
    }

    [Fact]
    public async Task DetectAsync_LocalEnableBeatsSharedDisable_ReturnsPlugin()
    {
        await WriteProjectSettingsAsync("settings.local.json", """{ "enabledPlugins": { "roz-mcp@roz-mcp": true } }""");
        await WriteProjectSettingsAsync("settings.json", """{ "enabledPlugins": { "roz-mcp@roz-mcp": false } }""");

        ClaudePluginDetection detection = await DetectAsync();

        detection.IsPlugin.ShouldBeTrue();
        detection.SourceFile.ShouldBe(Path.Combine(_projectRoot, ".claude", "settings.local.json"));
    }

    [Fact]
    public async Task DetectAsync_KeyFalse_ReturnsClassicButNamesTheKey()
    {
        await WriteProjectSettingsAsync("settings.local.json", """{ "enabledPlugins": { "roz-mcp@roz-mcp": false } }""");

        ClaudePluginDetection detection = await DetectAsync();

        detection.IsPlugin.ShouldBeFalse();
        detection.MatchedKey.ShouldBe("roz-mcp@roz-mcp");
    }

    [Fact]
    public async Task DetectAsync_OtherPluginsOnly_ReturnsClassicWithNoSource()
    {
        await WriteProjectSettingsAsync(
            "settings.local.json",
            """{ "enabledPlugins": { "other-plugin@official": true, "roz-mcpish@x": true } }""");

        ClaudePluginDetection detection = await DetectAsync();

        detection.IsPlugin.ShouldBeFalse();
        detection.MatchedKey.ShouldBeNull();
        detection.SourceFile.ShouldBeNull();
    }

    [Fact]
    public async Task DetectAsync_AnyMarketplaceSuffix_Matches()
    {
        await WriteProjectSettingsAsync("settings.local.json", """{ "enabledPlugins": { "roz-mcp@my-fork": true } }""");

        ClaudePluginDetection detection = await DetectAsync();

        detection.IsPlugin.ShouldBeTrue();
        detection.MatchedKey.ShouldBe("roz-mcp@my-fork");
    }

    [Fact]
    public async Task DetectAsync_MixedKeysInOneFile_AnyTrueEnables()
    {
        await WriteProjectSettingsAsync(
            "settings.local.json",
            """{ "enabledPlugins": { "roz-mcp@a": false, "roz-mcp@b": true } }""");

        ClaudePluginDetection detection = await DetectAsync();

        detection.IsPlugin.ShouldBeTrue();
        detection.MatchedKey.ShouldBe("roz-mcp@b");
    }

    [Fact]
    public async Task DetectAsync_UnparseableLocalSettings_SkippedNotDeciding()
    {
        await WriteProjectSettingsAsync("settings.local.json", "{ not json !");
        await WriteProjectSettingsAsync("settings.json", """{ "enabledPlugins": { "roz-mcp@roz-mcp": true } }""");

        ClaudePluginDetection detection = await DetectAsync();

        detection.IsPlugin.ShouldBeTrue();
        detection.SourceFile.ShouldBe(Path.Combine(_projectRoot, ".claude", "settings.json"));
    }

    [Fact]
    public async Task DetectAsync_NonBooleanValue_CountsAsDisabled()
    {
        await WriteProjectSettingsAsync("settings.local.json", """{ "enabledPlugins": { "roz-mcp@roz-mcp": "true" } }""");

        ClaudePluginDetection detection = await DetectAsync();

        detection.IsPlugin.ShouldBeFalse();
        detection.MatchedKey.ShouldBe("roz-mcp@roz-mcp");
    }

    [Fact]
    public async Task DetectAsync_SettingsFileWithArrayRoot_SkippedNotCrashing()
    {
        // Parseable but non-object: ReadJsonFileAsync must yield null (not throw, as the old
        // AsObject() call did), so the scan falls through to the next file.
        await WriteProjectSettingsAsync("settings.local.json", """[ "not", "an", "object" ]""");
        await WriteProjectSettingsAsync("settings.json", """{ "enabledPlugins": { "roz-mcp@roz-mcp": true } }""");

        ClaudePluginDetection detection = await DetectAsync();

        detection.IsPlugin.ShouldBeTrue();
        detection.SourceFile.ShouldBe(Path.Combine(_projectRoot, ".claude", "settings.json"));
    }

    [Fact]
    public async Task DetectAsync_EnabledPluginsNotAnObject_SkippedNotDeciding()
    {
        await WriteProjectSettingsAsync("settings.local.json", """{ "enabledPlugins": ["roz-mcp@roz-mcp"] }""");

        ClaudePluginDetection detection = await DetectAsync();

        detection.IsPlugin.ShouldBeFalse();
        detection.SourceFile.ShouldBeNull();
    }
}
