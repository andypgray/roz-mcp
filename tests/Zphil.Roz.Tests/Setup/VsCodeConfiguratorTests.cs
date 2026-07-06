using System.Text.Json.Nodes;
using Zphil.Roz.Setup.Clients;
using Zphil.Roz.Tests.Setup.Fixtures;

namespace Zphil.Roz.Tests.Setup;

/// <summary>
///     VS Code Copilot Chat uses <c>.vscode/mcp.json</c> with a <c>servers</c> key (not
///     <c>mcpServers</c>) and a per-entry <c>"type": "stdio"</c> field. Tests guard the merge
///     semantics and ensure that user-added top-level fields like <c>inputs</c> survive.
/// </summary>
public class VsCodeConfiguratorTests : IDisposable
{
    private readonly TempProjectDirectory _projectRoot = new("roz-mcp-vscode");

    public void Dispose() => _projectRoot.Dispose();

    [Fact]
    public async Task ConfigureMcpJsonAsync_FreshProject_UsesServersKeyAndStdioType()
    {
        // Act
        await VsCodeConfigurator.ConfigureMcpJsonAsync(_projectRoot);

        // Assert
        JsonObject root = await ReadRootAsync();
        root.ContainsKey("servers").ShouldBeTrue();
        root.ContainsKey("mcpServers").ShouldBeFalse();

        JsonObject server = root["servers"]!.AsObject()["roz"]!.AsObject();
        server["command"]!.GetValue<string>().ShouldBe("roz-mcp");
        server["type"]!.GetValue<string>().ShouldBe("stdio");
        server["args"].ShouldBeOfType<JsonArray>();
    }

    [Fact]
    public async Task ConfigureMcpJsonAsync_WithToolsValue_SetsRoslynToolsEnv()
    {
        // Act
        await VsCodeConfigurator.ConfigureMcpJsonAsync(_projectRoot, "edit");

        // Assert
        JsonObject server = await ReadServerEntryAsync();
        server["env"]!.AsObject()["ROZ_TOOLS"]!.GetValue<string>().ShouldBe("edit");
    }

    [Fact]
    public async Task ConfigureMcpJsonAsync_PreservesUserAddedInputsArray()
    {
        // Arrange — VS Code mcp.json supports a top-level "inputs" array for prompts.
        string vsCodeMcpJsonPath = Path.Combine(_projectRoot, ".vscode", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(vsCodeMcpJsonPath)!);
        JsonObject initial = new()
        {
            ["inputs"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "promptString",
                    ["id"] = "api-key",
                    ["description"] = "API key"
                }),
            ["servers"] = new JsonObject()
        };
        await File.WriteAllTextAsync(vsCodeMcpJsonPath, initial.ToJsonString(), TestContext.Current.CancellationToken);

        // Act
        await VsCodeConfigurator.ConfigureMcpJsonAsync(_projectRoot);

        // Assert
        JsonObject root = await ReadRootAsync();
        JsonArray inputs = root["inputs"]!.AsArray();
        inputs.Count.ShouldBe(1);
        inputs[0]!.AsObject()["id"]!.GetValue<string>().ShouldBe("api-key");

        root["servers"]!.AsObject().ContainsKey("roz").ShouldBeTrue();
    }

    [Fact]
    public async Task ConfigureMcpJsonAsync_PreservesSiblingServerEntries()
    {
        // Arrange
        string vsCodeMcpJsonPath = Path.Combine(_projectRoot, ".vscode", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(vsCodeMcpJsonPath)!);
        JsonObject initial = new()
        {
            ["servers"] = new JsonObject
            {
                ["other-server"] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = "other-tool"
                }
            }
        };
        await File.WriteAllTextAsync(vsCodeMcpJsonPath, initial.ToJsonString(), TestContext.Current.CancellationToken);

        // Act
        await VsCodeConfigurator.ConfigureMcpJsonAsync(_projectRoot);

        // Assert
        JsonObject root = await ReadRootAsync();
        JsonObject servers = root["servers"]!.AsObject();
        servers["other-server"]!.AsObject()["command"]!.GetValue<string>().ShouldBe("other-tool");
        servers.ContainsKey("roz").ShouldBeTrue();
    }

    [Fact]
    public async Task ConfigureMcpJsonAsync_RerunIsIdempotent()
    {
        // Act
        await VsCodeConfigurator.ConfigureMcpJsonAsync(_projectRoot, "read");
        await VsCodeConfigurator.ConfigureMcpJsonAsync(_projectRoot, "read");

        // Assert
        JsonObject root = await ReadRootAsync();
        JsonObject servers = root["servers"]!.AsObject();
        servers.Count.ShouldBe(1);

        JsonObject roslyn = servers["roz"]!.AsObject();
        roslyn["type"]!.GetValue<string>().ShouldBe("stdio");
        roslyn["env"]!.AsObject()["ROZ_TOOLS"]!.GetValue<string>().ShouldBe("read");
    }

    private async Task<JsonObject> ReadRootAsync()
    {
        string path = Path.Combine(_projectRoot, ".vscode", "mcp.json");
        string content = await File.ReadAllTextAsync(path);
        return JsonNode.Parse(content)!.AsObject();
    }

    private async Task<JsonObject> ReadServerEntryAsync()
    {
        JsonObject root = await ReadRootAsync();
        return root["servers"]!.AsObject()["roz"]!.AsObject();
    }
}
