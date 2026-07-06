using System.Text.Json.Nodes;
using Zphil.Roz.Setup.Clients;
using Zphil.Roz.Tests.Setup.Fixtures;

namespace Zphil.Roz.Tests.Setup;

/// <summary>
///     Guards <see cref="ClaudeCodeConfigurator.ConfigureMcpJsonAsync" />'s merge-into-existing
///     behaviour. Re-running <c>roz-mcp setup</c> must not clobber user-added env vars on the
///     roz-mcp server entry (e.g. <c>ROZ_SOLUTION_PATH</c>, <c>ROZ_TEST_PATHS</c>).
/// </summary>
public class ClaudeCodeConfiguratorMcpJsonTests : IDisposable
{
    private readonly TempProjectDirectory _projectRoot = new("roz-mcp-cfg");

    public void Dispose() => _projectRoot.Dispose();

    [Fact]
    public async Task ConfigureMcpJsonAsync_FreshProject_CreatesServerEntry()
    {
        // Act
        await ClaudeCodeConfigurator.ConfigureMcpJsonAsync(_projectRoot);

        // Assert
        JsonObject root = await ReadMcpJsonAsync();
        JsonObject server = GetServerEntry(root);
        server["command"]!.GetValue<string>().ShouldBe("roz-mcp");
        server["args"].ShouldBeOfType<JsonArray>();
    }

    [Fact]
    public async Task ConfigureMcpJsonAsync_WithToolsValue_SetsRoslynToolsEnv()
    {
        // Act
        await ClaudeCodeConfigurator.ConfigureMcpJsonAsync(_projectRoot, "read");

        // Assert
        JsonObject root = await ReadMcpJsonAsync();
        JsonObject env = GetServerEntry(root)["env"]!.AsObject();
        env["ROZ_TOOLS"]!.GetValue<string>().ShouldBe("read");
    }

    [Fact]
    public async Task ConfigureMcpJsonAsync_PreservesUserAddedEnvVarsOnRerun()
    {
        // Arrange — user has customised the server entry with an extra env var
        string mcpJsonPath = Path.Combine(_projectRoot, ".mcp.json");
        JsonObject initial = new()
        {
            ["mcpServers"] = new JsonObject
            {
                ["roz"] = new JsonObject
                {
                    ["command"] = "roz-mcp",
                    ["args"] = new JsonArray(),
                    ["env"] = new JsonObject
                    {
                        ["ROZ_SOLUTION_PATH"] = "/custom/path/to/solution.sln",
                        ["ROZ_TEST_PATHS"] = "tests/;specs/"
                    }
                }
            }
        };
        await File.WriteAllTextAsync(mcpJsonPath, initial.ToJsonString(), TestContext.Current.CancellationToken);

        // Act — re-run setup with a new tools value
        await ClaudeCodeConfigurator.ConfigureMcpJsonAsync(_projectRoot, "navigate");

        // Assert — user env vars survive, ROZ_TOOLS added alongside
        JsonObject root = await ReadMcpJsonAsync();
        JsonObject env = GetServerEntry(root)["env"]!.AsObject();
        env["ROZ_SOLUTION_PATH"]!.GetValue<string>().ShouldBe("/custom/path/to/solution.sln");
        env["ROZ_TEST_PATHS"]!.GetValue<string>().ShouldBe("tests/;specs/");
        env["ROZ_TOOLS"]!.GetValue<string>().ShouldBe("navigate");
    }

    [Fact]
    public async Task ConfigureMcpJsonAsync_WithoutToolsValue_LeavesExistingEnvIntact()
    {
        // Arrange — existing config with user env vars only
        string mcpJsonPath = Path.Combine(_projectRoot, ".mcp.json");
        JsonObject initial = new()
        {
            ["mcpServers"] = new JsonObject
            {
                ["roz"] = new JsonObject
                {
                    ["command"] = "roz-mcp",
                    ["args"] = new JsonArray(),
                    ["env"] = new JsonObject
                    {
                        ["ROZ_SOLUTION_PATH"] = "/custom/path.sln"
                    }
                }
            }
        };
        await File.WriteAllTextAsync(mcpJsonPath, initial.ToJsonString(), TestContext.Current.CancellationToken);

        // Act — re-run without passing tools
        await ClaudeCodeConfigurator.ConfigureMcpJsonAsync(_projectRoot);

        // Assert — env preserved, no ROZ_TOOLS added
        JsonObject root = await ReadMcpJsonAsync();
        JsonObject env = GetServerEntry(root)["env"]!.AsObject();
        env["ROZ_SOLUTION_PATH"]!.GetValue<string>().ShouldBe("/custom/path.sln");
        env.ContainsKey("ROZ_TOOLS").ShouldBeFalse();
    }

    [Fact]
    public async Task ConfigureMcpJsonAsync_PreservesOtherMcpServers()
    {
        // Arrange — user has another MCP server configured
        string mcpJsonPath = Path.Combine(_projectRoot, ".mcp.json");
        JsonObject initial = new()
        {
            ["mcpServers"] = new JsonObject
            {
                ["other-server"] = new JsonObject
                {
                    ["command"] = "other-tool",
                    ["args"] = new JsonArray("--flag")
                }
            }
        };
        await File.WriteAllTextAsync(mcpJsonPath, initial.ToJsonString(), TestContext.Current.CancellationToken);

        // Act
        await ClaudeCodeConfigurator.ConfigureMcpJsonAsync(_projectRoot);

        // Assert — sibling server untouched, roslyn added
        JsonObject root = await ReadMcpJsonAsync();
        JsonObject servers = root["mcpServers"]!.AsObject();
        servers["other-server"]!.AsObject()["command"]!.GetValue<string>().ShouldBe("other-tool");
        servers.ContainsKey("roz").ShouldBeTrue();
    }

    private async Task<JsonObject> ReadMcpJsonAsync()
    {
        string mcpJsonPath = Path.Combine(_projectRoot, ".mcp.json");
        string content = await File.ReadAllTextAsync(mcpJsonPath);
        return JsonNode.Parse(content)!.AsObject();
    }

    private static JsonObject GetServerEntry(JsonObject root) =>
        root["mcpServers"]!.AsObject()["roz"]!.AsObject();
}
