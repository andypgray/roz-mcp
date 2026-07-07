using Tomlyn;
using Tomlyn.Model;
using Zphil.Roz.Setup.Clients;
using Zphil.Roz.Tests.Setup.Fixtures;

namespace Zphil.Roz.Tests.Setup;

/// <summary>
///     Codex CLI uses <c>.codex/config.toml</c> with a <c>[mcp_servers.&lt;name&gt;]</c>
///     sub-table. Tests guard merge semantics: sibling <c>[mcp_servers.*]</c> tables and
///     unrelated top-level sections must survive across re-runs, the <c>--tools=</c> value
///     must land in env, and round-trip must be idempotent.
/// </summary>
public class CodexConfiguratorTests : IDisposable
{
    private readonly TempProjectDirectory _projectRoot = new("roz-mcp-codex");

    public void Dispose() => _projectRoot.Dispose();

    [Fact]
    public async Task ConfigureConfigTomlAsync_FreshProject_CreatesMcpServersRoslynTable()
    {
        // Act
        await CodexConfigurator.ConfigureConfigTomlAsync(_projectRoot);

        // Assert
        TomlTable roslyn = await ReadRoslynTableAsync();
        ((string)roslyn["command"]).ShouldBe("roz-mcp");
        roslyn.ContainsKey("args").ShouldBeTrue();
    }

    [Fact]
    public async Task ConfigureConfigTomlAsync_WithToolsValue_SetsRoslynToolsEnv()
    {
        // Act
        await CodexConfigurator.ConfigureConfigTomlAsync(_projectRoot, "read");

        // Assert
        TomlTable roslyn = await ReadRoslynTableAsync();
        var env = (TomlTable)roslyn["env"];
        ((string)env["ROZ_TOOLS"]).ShouldBe("read");
    }

    [Fact]
    public async Task ConfigureConfigTomlAsync_PreservesSiblingMcpServers()
    {
        // Arrange — pre-populate config.toml with an unrelated MCP server.
        string configPath = Path.Combine(_projectRoot, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(
            configPath,
            """
            [mcp_servers.other]
            command = "other-tool"
            args = ["--flag"]
            """,
            TestContext.Current.CancellationToken);

        // Act
        await CodexConfigurator.ConfigureConfigTomlAsync(_projectRoot);

        // Assert
        TomlTable mcpServers = await ReadMcpServersAsync();
        mcpServers.ContainsKey("other").ShouldBeTrue();
        mcpServers.ContainsKey("roz").ShouldBeTrue();
        ((string)((TomlTable)mcpServers["other"])["command"]).ShouldBe("other-tool");
    }

    [Fact]
    public async Task ConfigureConfigTomlAsync_PreservesUnrelatedTopLevelSections()
    {
        // Arrange — pre-populate with an unrelated section.
        string configPath = Path.Combine(_projectRoot, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(
            configPath,
            """
            [unrelated]
            some_key = "some_value"
            """,
            TestContext.Current.CancellationToken);

        // Act
        await CodexConfigurator.ConfigureConfigTomlAsync(_projectRoot);

        // Assert
        TomlTable root = await ReadRootAsync();
        var unrelated = (TomlTable)root["unrelated"];
        ((string)unrelated["some_key"]).ShouldBe("some_value");

        ((TomlTable)((TomlTable)root["mcp_servers"])["roz"]).ContainsKey("command").ShouldBeTrue();
    }

    [Fact]
    public async Task ConfigureConfigTomlAsync_RerunIsIdempotent()
    {
        // Act
        await CodexConfigurator.ConfigureConfigTomlAsync(_projectRoot, "read");
        await CodexConfigurator.ConfigureConfigTomlAsync(_projectRoot, "read");

        // Assert
        TomlTable mcpServers = await ReadMcpServersAsync();
        mcpServers.Count.ShouldBe(1);

        var roslyn = (TomlTable)mcpServers["roz"];
        ((string)roslyn["command"]).ShouldBe("roz-mcp");
        ((string)((TomlTable)roslyn["env"])["ROZ_TOOLS"]).ShouldBe("read");
    }

    [Fact]
    public async Task ConfigureConfigTomlAsync_PreservesExistingRozEnvVars()
    {
        // Arrange — user has added ROZ_SOLUTION_PATH to the roslyn entry.
        string configPath = Path.Combine(_projectRoot, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(
            configPath,
            """
            [mcp_servers.roz]
            command = "roz-mcp"
            args = []

            [mcp_servers.roz.env]
            ROZ_SOLUTION_PATH = "/custom/path.sln"
            """,
            TestContext.Current.CancellationToken);

        // Act
        await CodexConfigurator.ConfigureConfigTomlAsync(_projectRoot, "navigate");

        // Assert
        TomlTable roslyn = await ReadRoslynTableAsync();
        var env = (TomlTable)roslyn["env"];
        ((string)env["ROZ_SOLUTION_PATH"]).ShouldBe("/custom/path.sln");
        ((string)env["ROZ_TOOLS"]).ShouldBe("navigate");
    }

    private async Task<TomlTable> ReadRootAsync()
    {
        string path = Path.Combine(_projectRoot, ".codex", "config.toml");
        string content = await File.ReadAllTextAsync(path);
        return TomlSerializer.Deserialize<TomlTable>(content)!;
    }

    private async Task<TomlTable> ReadMcpServersAsync()
    {
        TomlTable root = await ReadRootAsync();
        return (TomlTable)root["mcp_servers"];
    }

    private async Task<TomlTable> ReadRoslynTableAsync()
    {
        TomlTable mcpServers = await ReadMcpServersAsync();
        return (TomlTable)mcpServers["roz"];
    }
}
