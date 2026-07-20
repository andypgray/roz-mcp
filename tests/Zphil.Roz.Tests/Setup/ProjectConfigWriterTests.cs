using System.Text.Json.Nodes;
using Zphil.Roz.Setup;
using Zphil.Roz.Tests.Setup.Fixtures;

namespace Zphil.Roz.Tests.Setup;

/// <summary>
///     Guards <see cref="ProjectConfigWriter" />'s <c>.roz.json</c> merge: siblings survive, the
///     file is created when missing, and anything that cannot be rewritten losslessly (invalid
///     JSON, comments a rewrite would strip, a non-object root) aborts without touching the file.
///     Temp dirs only — a real <c>.roz.json</c> in this repo would reconfigure the dogfooding server.
/// </summary>
public class ProjectConfigWriterTests : IDisposable
{
    private readonly TempProjectDirectory _projectRoot = new("roz-mcp-config-writer");

    private string ConfigPath => Path.Combine(_projectRoot, ".roz.json");

    public void Dispose() => _projectRoot.Dispose();

    private Task MergeAsync(string key = "ROZ_TOOLS", string value = "all") =>
        ProjectConfigWriter.MergeKeyAsync(_projectRoot, key, value, TestContext.Current.CancellationToken);

    private async Task<JsonObject> ReadConfigAsync()
    {
        string json = await File.ReadAllTextAsync(ConfigPath, TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!.AsObject();
    }

    [Fact]
    public async Task MergeKeyAsync_NoFile_CreatesFileWithKey()
    {
        await MergeAsync(value: "read");

        JsonObject root = await ReadConfigAsync();
        root["ROZ_TOOLS"]!.GetValue<string>().ShouldBe("read");
    }

    [Fact]
    public async Task MergeKeyAsync_ExistingFile_PreservesSiblingKeys()
    {
        await File.WriteAllTextAsync(
            ConfigPath,
            """{ "ROZ_LOG_LEVEL": "Information", "ROZ_TOOLS": "default" }""",
            TestContext.Current.CancellationToken);

        await MergeAsync(value: "all");

        JsonObject root = await ReadConfigAsync();
        root["ROZ_LOG_LEVEL"]!.GetValue<string>().ShouldBe("Information");
        root["ROZ_TOOLS"]!.GetValue<string>().ShouldBe("all");
    }

    [Fact]
    public async Task MergeKeyAsync_UnparseableFile_AbortsWithoutOverwrite()
    {
        const string Broken = "{ not json !";
        await File.WriteAllTextAsync(ConfigPath, Broken, TestContext.Current.CancellationToken);

        await MergeAsync();

        string after = await File.ReadAllTextAsync(ConfigPath, TestContext.Current.CancellationToken);
        after.ShouldBe(Broken);
    }

    [Fact]
    public async Task MergeKeyAsync_FileWithComments_AbortsWithoutStrippingThem()
    {
        // The seeder reads comments leniently, but a rewrite would silently drop them — so the
        // writer refuses and asks for a hand edit instead.
        const string Commented = """
                                 {
                                   // keep the surface small
                                   "ROZ_TOOLS": "read"
                                 }
                                 """;
        await File.WriteAllTextAsync(ConfigPath, Commented, TestContext.Current.CancellationToken);

        await MergeAsync(value: "all");

        string after = await File.ReadAllTextAsync(ConfigPath, TestContext.Current.CancellationToken);
        after.ShouldBe(Commented);
    }

    [Fact]
    public async Task MergeKeyAsync_WhitespaceFile_TreatedAsEmptyConfig()
    {
        await File.WriteAllTextAsync(ConfigPath, "   \r\n", TestContext.Current.CancellationToken);

        await MergeAsync(value: "read");

        JsonObject root = await ReadConfigAsync();
        root["ROZ_TOOLS"]!.GetValue<string>().ShouldBe("read");
    }

    [Fact]
    public async Task MergeKeyAsync_NonObjectRoot_AbortsWithoutOverwrite()
    {
        const string ArrayRoot = """["ROZ_TOOLS"]""";
        await File.WriteAllTextAsync(ConfigPath, ArrayRoot, TestContext.Current.CancellationToken);

        await MergeAsync();

        string after = await File.ReadAllTextAsync(ConfigPath, TestContext.Current.CancellationToken);
        after.ShouldBe(ArrayRoot);
    }
}
