using Zphil.Roz.Setup;
using Zphil.Roz.Setup.Clients;
using Zphil.Roz.Tests.Setup.Fixtures;

namespace Zphil.Roz.Tests.Setup;

public class SetupCommandTests
{
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
}
