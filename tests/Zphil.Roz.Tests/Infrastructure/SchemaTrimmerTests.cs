using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit.Sdk;
using Zphil.Roz.Pipeline;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Infrastructure;

/// <summary>
///     Guards the token-saving behaviour of <see cref="SchemaTrimmer" />: every MCP tool's
///     <c>inputSchema</c> must be emitted without inline <c>enum</c> arrays, nullable type unions
///     must collapse, redundant <c>default:null</c> must be dropped, and the trimmed
///     <c>tools/list</c> payload must be meaningfully smaller than the default.
/// </summary>
public class SchemaTrimmerTests(ITestOutputHelper output)
{
    private static readonly Type[] AllToolTypes =
    [
        typeof(NavigationTools),
        typeof(ReferenceTools),
        typeof(TypeHierarchyTools),
        typeof(CodeEditTools),
        typeof(UsingDirectiveTools),
        typeof(DiagnosticTools),
        typeof(WorkspaceTools)
    ];

    [Fact]
    public void WithTrimmedTools_EmitsNoEnumKeyInAnyInputSchema()
    {
        // Arrange
        McpServerTool[] tools = BuildTools(true);

        // Act / Assert
        foreach (McpServerTool tool in tools)
        {
            JsonElement schema = tool.ProtocolTool.InputSchema;
            string toolName = tool.ProtocolTool.Name;

            AssertNoEnumKey(schema, toolName, "$");
        }
    }

    [Fact]
    public void WithTrimmedTools_PreservesStringTypeForEnumParameters()
    {
        // Arrange — find_symbol has both a scalar enum (`kind`) and array-of-enum (`memberKinds`)
        McpServerTool findSymbol = BuildTools(true)
            .Single(t => t.ProtocolTool.Name == "find_symbol");

        // Act
        JsonElement schema = findSymbol.ProtocolTool.InputSchema;
        JsonElement properties = schema.GetProperty("properties");
        JsonElement kindNode = properties.GetProperty("kind");
        JsonElement memberKindsNode = properties.GetProperty("memberKinds");

        // Assert — scalar enum parameter reduces to {"type":"string", "description":...}
        GetTypeString(kindNode).ShouldBe("string");
        kindNode.TryGetProperty("enum", out _).ShouldBeFalse("kind schema still has an enum array");

        // Array-of-enum keeps its "array" type but its items must also be bare strings
        GetTypeString(memberKindsNode).ShouldBe("array");
        JsonElement items = memberKindsNode.GetProperty("items");
        GetTypeString(items).ShouldBe("string");
        items.TryGetProperty("enum", out _).ShouldBeFalse("memberKinds.items still has an enum array");
    }

    [Fact]
    public void WithTrimmedTools_CollapsesNullableTypeUnion()
    {
        // Arrange — find_symbol's `kind` parameter is a nullable enum (declared as SymbolicKind?),
        // which the schema generator emits as {"type":["string","null"],"default":null}. Both the
        // union and the redundant null default should be trimmed.
        McpServerTool findSymbol = BuildTools(true)
            .Single(t => t.ProtocolTool.Name == "find_symbol");

        // Act
        JsonElement kindNode = findSymbol.ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("kind");

        // Assert — type is a bare string, no null default lingering.
        kindNode.GetProperty("type").ValueKind.ShouldBe(JsonValueKind.String);
        kindNode.GetProperty("type").GetString().ShouldBe("string");
        kindNode.TryGetProperty("default", out _).ShouldBeFalse("kind retained a redundant default:null");
    }

    [Fact]
    public void WithTrimmedTools_PreservesMeaningfulDefaults()
    {
        // Arrange — find_symbol.maxResults is int? with a real default of 10. The union collapses
        // but the default value must survive so clients still know the server's fallback.
        McpServerTool findSymbol = BuildTools(true)
            .Single(t => t.ProtocolTool.Name == "find_symbol");

        // Act
        JsonElement maxResults = findSymbol.ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("maxResults");

        // Assert
        maxResults.GetProperty("type").GetString().ShouldBe("integer");
        maxResults.GetProperty("default").GetInt32().ShouldBe(10);
    }

    [Fact]
    public void WithTrimmedTools_PreservesBooleanTypeOnBoolParameters()
    {
        // Arrange — find_symbol declares several bool parameters (includeTests, includeBody,
        // includeDocs, includeGenerated). BoolCoercerFactory registration causes the exporter
        // to emit {} for bool nodes, exactly like enums and string[]. SchemaTrimmer.TrimSchemaNode
        // re-injects type:boolean from the CLR type; this test locks that injection.
        McpServerTool findSymbol = BuildTools(true)
            .Single(t => t.ProtocolTool.Name == "find_symbol");

        // Act
        JsonElement properties = findSymbol.ProtocolTool.InputSchema.GetProperty("properties");

        // Assert — every bool param keeps "type":"boolean" after BoolCoercerFactory registration.
        foreach (string boolParam in new[] { "includeTests", "includeBody", "includeDocs", "includeGenerated" })
        {
            JsonElement node = properties.GetProperty(boolParam);
            GetTypeString(node).ShouldBe("boolean", $"{boolParam} schema should advertise type:boolean");
        }
    }

    [Fact]
    public void WithTrimmedTools_CollapsesNullableItemsInsideArrays()
    {
        // Arrange — filePaths is string[]? declared as nullable-array-of-nullable-strings;
        // neither the outer `type` nor the items' `type` should advertise null.
        McpServerTool findSymbol = BuildTools(true)
            .Single(t => t.ProtocolTool.Name == "find_symbol");

        // Act
        JsonElement filePaths = findSymbol.ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("filePaths");

        // Assert
        filePaths.GetProperty("type").GetString().ShouldBe("array");
        filePaths.GetProperty("items").GetProperty("type").GetString().ShouldBe("string");
    }

    [Fact]
    public void WithTrimmedTools_DropsRedundantToolMetadata()
    {
        // Arrange — every tool declares Title via [McpServerTool(Title = ...)], which the SDK
        // copies into BOTH Tool.Title and Tool.Annotations.Title. The SDK also auto-populates
        // Tool.Execution with {"taskSupport":"optional"} for every async method. Both are wire
        // noise for clients that don't consume them.
        McpServerTool[] tools = BuildTools(true);

        // Act / Assert
        foreach (McpServerTool tool in tools)
        {
            tool.ProtocolTool.Title.ShouldBeNull($"{tool.ProtocolTool.Name} still has a top-level Title");
#pragma warning disable MCPEXP001 // ToolExecution is experimental.
            tool.ProtocolTool.Execution.ShouldBeNull($"{tool.ProtocolTool.Name} still has Execution metadata");
#pragma warning restore MCPEXP001
        }
    }

    [Fact]
    public void WithTrimmedTools_ShrinksSerializedSchemaPayload()
    {
        // Arrange
        McpServerTool[] original = BuildTools(false);
        McpServerTool[] stripped = BuildTools(true);

        // Act — measure the actual wire cost using the same options the MCP SDK uses to serialize
        // tools/list responses. Plain JsonSerializer defaults would double-count null properties that
        // WhenWritingNull strips on the wire, producing a misleading baseline.
        long originalBytes = MeasureSerializedSize(original);
        long strippedBytes = MeasureSerializedSize(stripped);
        long savedBytes = originalBytes - strippedBytes;
        double savedPercent = savedBytes * 100d / originalBytes;

        output.WriteLine($"tools/list payload: original={originalBytes} B, stripped={strippedBytes} B, saved={savedBytes} B ({savedPercent:F1}%)");

        foreach (KeyValuePair<string, long> row in MeasurePerTool(stripped).OrderByDescending(p => p.Value))
        {
            output.WriteLine($"  {row.Key,-28} {row.Value,6} B");
        }

        // Assert — aim for ≥15% wire savings across enum stripping + union collapse + default:null stripping.
        savedBytes.ShouldBeGreaterThan(0,
            $"stripped payload ({strippedBytes} bytes) should be smaller than original ({originalBytes} bytes)");
        savedPercent.ShouldBeGreaterThan(15d,
            $"expected >15% savings, got {savedPercent:F1}% (original={originalBytes}, stripped={strippedBytes})");
    }

    private static McpServerTool[] BuildTools(bool useStripper)
    {
        ServiceCollection services = new();
        IMcpServerBuilder builder = new StandaloneMcpServerBuilder(services);

        if (useStripper)
        {
            builder.WithTrimmedTools(GetAllToolMethods());
        }
        else
        {
            // Explicit IEnumerable<Type> cast forces the non-generic overload; otherwise C#
            // picks WithTools<T>(T target) with T=Type[] and iterates the array's own methods.
            builder.WithTools((IEnumerable<Type>)AllToolTypes);
        }

        ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetServices<McpServerTool>().ToArray();
    }

    private static IEnumerable<MethodInfo> GetAllToolMethods() =>
        AllToolTypes.SelectMany(t => t.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    private static long MeasureSerializedSize(IEnumerable<McpServerTool> tools)
    {
        // Serialize a real ListToolsResult with the SDK's own JsonSerializerOptions so that
        // WhenWritingNull correctly drops null Tool.Title/Annotations/OutputSchema etc. — this
        // is what the MCP server actually transmits for every tools/list response.
        ListToolsResult payload = new()
        {
            Tools = tools.Select(t => t.ProtocolTool).ToList()
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, McpJsonUtilities.DefaultOptions).LongLength;
    }

    private static Dictionary<string, long> MeasurePerTool(IEnumerable<McpServerTool> tools)
    {
        Dictionary<string, long> sizes = new();
        foreach (McpServerTool tool in tools)
        {
            sizes[tool.ProtocolTool.Name] = JsonSerializer
                .SerializeToUtf8Bytes(tool.ProtocolTool, McpJsonUtilities.DefaultOptions)
                .LongLength;
        }

        return sizes;
    }

    private static void AssertNoEnumKey(JsonElement element, string toolName, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    if (prop.Name == "enum")
                    {
                        throw new XunitException(
                            $"Tool '{toolName}' still exposes an 'enum' array at {path}.enum");
                    }

                    AssertNoEnumKey(prop.Value, toolName, $"{path}.{prop.Name}");
                }

                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    AssertNoEnumKey(item, toolName, $"{path}[{i++}]");
                }

                break;
        }
    }

    private static string GetTypeString(JsonElement node)
    {
        // The type field is "string" for a scalar enum parameter; for nullable enums some schemas
        // emit ["string", "null"]. This helper normalises to the leading type when it's an array.
        JsonElement typeNode = node.GetProperty("type");
        return typeNode.ValueKind == JsonValueKind.Array
            ? typeNode.EnumerateArray().First().GetString()!
            : typeNode.GetString()!;
    }

    /// <summary>
    ///     Minimal <see cref="IMcpServerBuilder" /> implementation for tests — avoids booting the
    ///     full MCP host just to inspect the emitted tool schemas.
    /// </summary>
    private sealed class StandaloneMcpServerBuilder(IServiceCollection services) : IMcpServerBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
