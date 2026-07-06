using System.Text.Json.Nodes;
using Zphil.Roz.Setup.Clients;
using Zphil.Roz.Tests.Setup.Fixtures;

namespace Zphil.Roz.Tests.Setup;

/// <summary>
///     Cursor uses <c>.cursor/mcp.json</c> with the same <c>mcpServers</c> JSON shape as Claude.
///     These tests guard the merge-into-existing behaviour and ensure sibling entries and
///     user-added env vars survive across re-runs.
/// </summary>
public class CursorConfiguratorTests : IDisposable
{
    private readonly TempProjectDirectory _projectRoot = new("roz-mcp-cursor");

    public void Dispose() => _projectRoot.Dispose();

    [Fact]
    public async Task ConfigureMcpJsonAsync_FreshProject_CreatesCursorMcpJson()
    {
        // Act
        await CursorConfigurator.ConfigureMcpJsonAsync(_projectRoot);

        // Assert
        JsonObject server = await ReadServerEntryAsync();
        server["command"]!.GetValue<string>().ShouldBe("roz-mcp");
        server["args"].ShouldBeOfType<JsonArray>();
    }

    [Fact]
    public async Task ConfigureMcpJsonAsync_WithToolsValue_SetsRoslynToolsEnv()
    {
        // Act
        await CursorConfigurator.ConfigureMcpJsonAsync(_projectRoot, "read");

        // Assert
        JsonObject server = await ReadServerEntryAsync();
        JsonObject env = server["env"]!.AsObject();
        env["ROZ_TOOLS"]!.GetValue<string>().ShouldBe("read");
    }

    [Fact]
    public async Task ConfigureMcpJsonAsync_PreservesSiblingMcpServers()
    {
        // Arrange
        string cursorMcpJsonPath = Path.Combine(_projectRoot, ".cursor", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cursorMcpJsonPath)!);
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
        await File.WriteAllTextAsync(cursorMcpJsonPath, initial.ToJsonString(), TestContext.Current.CancellationToken);

        // Act
        await CursorConfigurator.ConfigureMcpJsonAsync(_projectRoot);

        // Assert
        JsonObject root = await ReadRootAsync();
        JsonObject servers = root["mcpServers"]!.AsObject();
        servers["other-server"]!.AsObject()["command"]!.GetValue<string>().ShouldBe("other-tool");
        servers.ContainsKey("roz").ShouldBeTrue();
    }

    [Fact]
    public async Task ConfigureMcpJsonAsync_PreservesUserAddedEnvVarsOnRerun()
    {
        // Arrange
        string cursorMcpJsonPath = Path.Combine(_projectRoot, ".cursor", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cursorMcpJsonPath)!);
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
                        ["ROZ_SOLUTION_PATH"] = "/custom/path/to/solution.sln"
                    }
                }
            }
        };
        await File.WriteAllTextAsync(cursorMcpJsonPath, initial.ToJsonString(), TestContext.Current.CancellationToken);

        // Act
        await CursorConfigurator.ConfigureMcpJsonAsync(_projectRoot, "navigate");

        // Assert
        JsonObject server = await ReadServerEntryAsync();
        JsonObject env = server["env"]!.AsObject();
        env["ROZ_SOLUTION_PATH"]!.GetValue<string>().ShouldBe("/custom/path/to/solution.sln");
        env["ROZ_TOOLS"]!.GetValue<string>().ShouldBe("navigate");
    }

    [Fact]
    public async Task ConfigureMcpJsonAsync_RerunIsIdempotent()
    {
        // Act
        await CursorConfigurator.ConfigureMcpJsonAsync(_projectRoot, "read");
        await CursorConfigurator.ConfigureMcpJsonAsync(_projectRoot, "read");

        // Assert
        JsonObject root = await ReadRootAsync();
        JsonObject servers = root["mcpServers"]!.AsObject();
        servers.Count.ShouldBe(1);
        servers["roz"]!.AsObject()["env"]!.AsObject()["ROZ_TOOLS"]!.GetValue<string>().ShouldBe("read");
    }

    private async Task<JsonObject> ReadRootAsync()
    {
        string path = Path.Combine(_projectRoot, ".cursor", "mcp.json");
        string content = await File.ReadAllTextAsync(path);
        return JsonNode.Parse(content)!.AsObject();
    }

    private async Task<JsonObject> ReadServerEntryAsync()
    {
        JsonObject root = await ReadRootAsync();
        return root["mcpServers"]!.AsObject()["roz"]!.AsObject();
    }
}
