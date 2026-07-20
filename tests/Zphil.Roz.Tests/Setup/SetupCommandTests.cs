using Zphil.Roz.Setup;
using Zphil.Roz.Setup.Clients;
using Zphil.Roz.Tests.Setup.Fixtures;

namespace Zphil.Roz.Tests.Setup;

public class SetupCommandTests
{
    private static readonly ClaudePluginDetection DetectedPlugin =
        new(true, "roz-mcp@roz-mcp", ".claude/settings.local.json");

    private static readonly ClaudePluginDetection NoPluginFound = new(false, null, null);

    private static readonly string ProjectRoot = Path.Combine(Path.GetTempPath(), "roz-setup-proj");

    private static readonly string UserSettingsPath =
        Path.Combine(Path.GetTempPath(), "roz-someone-home", ".claude", "settings.json");

    [Fact]
    public void ResolveClientsFromArg_SingleKey_ReturnsOneConfigurator()
    {
        IReadOnlyList<IClientConfigurator> result = SetupCommand.ResolveClientsFromArg("cursor");

        result.Count.ShouldBe(1);
        result[0].ClientKey.ShouldBe("cursor");
    }

    [Fact]
    public void ResolveClientsFromArg_CommaList_ReturnsAllSpecified()
    {
        IReadOnlyList<IClientConfigurator> result = SetupCommand.ResolveClientsFromArg("claude,cursor,vscode");

        result.Select(c => c.ClientKey).ShouldBe(["claude", "cursor", "vscode"]);
    }

    [Fact]
    public void ResolveClientsFromArg_All_ReturnsAllFour()
    {
        IReadOnlyList<IClientConfigurator> result = SetupCommand.ResolveClientsFromArg("all");

        result.Select(c => c.ClientKey).ShouldBe(["claude", "cursor", "vscode", "codex"]);
    }

    [Fact]
    public void ResolveClientsFromArg_AllUppercase_ReturnsAllFour()
    {
        IReadOnlyList<IClientConfigurator> result = SetupCommand.ResolveClientsFromArg("ALL");

        result.Select(c => c.ClientKey).ShouldBe(["claude", "cursor", "vscode", "codex"]);
    }

    [Fact]
    public void ResolveClientsFromArg_MixedCaseKey_Matches()
    {
        IReadOnlyList<IClientConfigurator> result = SetupCommand.ResolveClientsFromArg("Cursor");

        result.Count.ShouldBe(1);
        result[0].ClientKey.ShouldBe("cursor");
    }

    [Fact]
    public void ResolveClientsFromArg_Unknown_Throws() => Should.Throw<UserErrorException>(() => SetupCommand.ResolveClientsFromArg("emacs"));

    [Fact]
    public void ResolveClientsFromArg_DuplicateKeys_DedupesPreservingOrder()
    {
        IReadOnlyList<IClientConfigurator> result = SetupCommand.ResolveClientsFromArg("cursor,cursor,claude");

        result.Select(c => c.ClientKey).ShouldBe(["cursor", "claude"]);
    }

    [Fact]
    public void AutoDetectClients_NoMarkerDirs_ReturnsEmpty()
    {
        using var projectRoot = new TempProjectDirectory("roz-mcp-detect");

        IReadOnlyList<IClientConfigurator> detected = SetupCommand.AutoDetectClients(projectRoot);

        detected.ShouldBeEmpty();
    }

    [Fact]
    public void AutoDetectClients_ClaudeMarkerOnly_DetectsClaude()
    {
        using var projectRoot = new TempProjectDirectory("roz-mcp-detect");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".claude"));

        IReadOnlyList<IClientConfigurator> detected = SetupCommand.AutoDetectClients(projectRoot);

        detected.Count.ShouldBe(1);
        detected[0].ClientKey.ShouldBe("claude");
    }

    [Fact]
    public void AutoDetectClients_CursorMarkerOnly_DetectsCursor()
    {
        using var projectRoot = new TempProjectDirectory("roz-mcp-detect");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".cursor"));

        IReadOnlyList<IClientConfigurator> detected = SetupCommand.AutoDetectClients(projectRoot);

        detected.Count.ShouldBe(1);
        detected[0].ClientKey.ShouldBe("cursor");
    }

    [Fact]
    public void AutoDetectClients_MultipleMarkers_ReturnsAllInRegistrationOrder()
    {
        using var projectRoot = new TempProjectDirectory("roz-mcp-detect");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".claude"));
        Directory.CreateDirectory(Path.Combine(projectRoot, ".codex"));

        IReadOnlyList<IClientConfigurator> detected = SetupCommand.AutoDetectClients(projectRoot);

        detected.Select(c => c.ClientKey).ShouldBe(["claude", "codex"]);
    }

    [Fact]
    public void ResolvePluginMode_BothFlags_Throws() =>
        Should.Throw<UserErrorException>(() => SetupCommand.ResolvePluginMode(true, true, NoPluginFound));

    [Fact]
    public void ResolvePluginMode_PluginFlag_WinsOverNegativeDetection() =>
        SetupCommand.ResolvePluginMode(true, false, NoPluginFound).ShouldBe(ClaudePluginMode.Plugin);

    [Fact]
    public void ResolvePluginMode_NoPluginFlag_WinsOverPositiveDetection() =>
        SetupCommand.ResolvePluginMode(false, true, DetectedPlugin).ShouldBe(ClaudePluginMode.Classic);

    [Fact]
    public void ResolvePluginMode_NoFlags_FollowsDetectionPositive() =>
        SetupCommand.ResolvePluginMode(false, false, DetectedPlugin).ShouldBe(ClaudePluginMode.Plugin);

    [Fact]
    public void ResolvePluginMode_NoFlags_FollowsDetectionNegative() =>
        SetupCommand.ResolvePluginMode(false, false, NoPluginFound).ShouldBe(ClaudePluginMode.Classic);

    [Fact]
    public void BuildPluginVerdict_PluginFlag_NamesTheFlagAsBasis()
    {
        string verdict = SetupCommand.BuildPluginVerdict(
            ClaudePluginMode.Plugin, NoPluginFound, true, false, ProjectRoot);

        verdict.ShouldContain("plugin mode (--plugin)");
        verdict.ShouldContain("Re-run with --no-plugin");
    }

    [Fact]
    public void BuildPluginVerdict_NoPluginFlag_NamesTheFlagAsBasis()
    {
        string verdict = SetupCommand.BuildPluginVerdict(
            ClaudePluginMode.Classic, DetectedPlugin, false, true, ProjectRoot);

        verdict.ShouldContain("classic mode (--no-plugin)");
        verdict.ShouldContain("Re-run with --plugin");
    }

    [Fact]
    public void BuildPluginVerdict_DetectedEnabled_NamesKeyAndRelativeSourceFile()
    {
        ClaudePluginDetection detection = new(
            true, "roz-mcp@roz-mcp", Path.Combine(ProjectRoot, ".claude", "settings.local.json"));

        string verdict = SetupCommand.BuildPluginVerdict(
            ClaudePluginMode.Plugin, detection, false, false, ProjectRoot);

        verdict.ShouldContain($"'roz-mcp@roz-mcp' enabled in {Path.Combine(".claude", "settings.local.json")}");
    }

    [Fact]
    public void BuildPluginVerdict_ExplicitlyDisabled_NamesKeyAsDisabled()
    {
        ClaudePluginDetection detection = new(
            false, "roz-mcp@roz-mcp", Path.Combine(ProjectRoot, ".claude", "settings.json"));

        string verdict = SetupCommand.BuildPluginVerdict(
            ClaudePluginMode.Classic, detection, false, false, ProjectRoot);

        verdict.ShouldContain($"'roz-mcp@roz-mcp' disabled in {Path.Combine(".claude", "settings.json")}");
    }

    [Fact]
    public void BuildPluginVerdict_UserScopeSource_KeepsAbsolutePath()
    {
        // A user-level settings file lives outside the project root, so the relative form would
        // be a ..\..\ chain; the absolute path is clearer.
        ClaudePluginDetection detection = new(true, "roz-mcp@roz-mcp", UserSettingsPath);

        string verdict = SetupCommand.BuildPluginVerdict(
            ClaudePluginMode.Plugin, detection, false, false, ProjectRoot);

        verdict.ShouldContain($"enabled in {UserSettingsPath}");
    }

    [Fact]
    public void BuildPluginVerdict_NothingFound_SaysSo()
    {
        string verdict = SetupCommand.BuildPluginVerdict(
            ClaudePluginMode.Classic, NoPluginFound, false, false, ProjectRoot);

        verdict.ShouldContain("no roz-mcp plugin enablement found");
    }
}
