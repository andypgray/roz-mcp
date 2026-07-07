using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Zphil.Roz.Pipeline;

namespace Zphil.Roz.Tests.Pipeline;

/// <summary>
///     Tests for <see cref="UnknownParameterGuard" />: a JSON argument key matching no
///     declared parameter is surfaced as an actionable, self-correcting error instead of
///     being silently dropped by the SDK's <c>UnmappedMemberHandling = Skip</c>.
/// </summary>
public class UnknownParameterGuardTests
{
    // Value is never inspected by the guard (only keys are), so a single shared dummy
    // suffices; the document is intentionally kept alive for the class lifetime.
    private static readonly JsonElement DummyValue = JsonDocument.Parse("null").RootElement;

    [Fact]
    public void Validate_UnknownKeyOnRealTool_NamesBadKeyToolAndValidList()
    {
        // Act
        string? message = UnknownParameterGuard.Validate(
            "get_symbols_overview",
            new Dictionary<string, JsonElement> { ["relativePath"] = DummyValue });

        // Assert — names the bad key (quoted), the tool, and the real scope params.
        message.ShouldNotBeNull();
        message.ShouldContain("\"relativePath\"");
        message.ShouldContain("get_symbols_overview");
        message.ShouldContain("filePaths");
        message.ShouldContain("project");
    }

    [Fact]
    public void Validate_EveryDeclaredParameter_ReturnsNull()
    {
        // Arrange — independently reflect every tool's JSON parameter names. Services
        // arrive via primary constructors, so the only context-bound *method*
        // parameters are CancellationToken (every tool) and
        // IProgress<ProgressNotificationValue> (the diagnostics/workspace tools);
        // IsJsonBound encodes exactly that, independently of the guard's predicate. A
        // newly introduced context-bound parameter type will (correctly) trip this
        // test, forcing a matching update here and in UnknownParameterGuard.
        List<string> failures = [];

        foreach (MethodInfo method in ToolAttributeDiscovery.GetToolMethods())
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>()?.Name is not { } toolName)
            {
                continue;
            }

            Dictionary<string, JsonElement> arguments = method.GetParameters()
                .Where(IsJsonBound)
                .ToDictionary(p => p.Name!, _ => DummyValue);

            string? message = UnknownParameterGuard.Validate(toolName, arguments);
            if (message is not null)
            {
                failures.Add($"{toolName}: {message}");
            }
        }

        // Assert — every real parameter name is accepted; any failure is schema drift.
        failures.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_CaseInsensitiveKey_ReturnsNull()
    {
        // Act — a casing slip binds anyway under Web defaults, so it must not be flagged.
        string? message = UnknownParameterGuard.Validate(
            "get_symbols_overview",
            new Dictionary<string, JsonElement> { ["FilePaths"] = DummyValue });

        // Assert
        message.ShouldBeNull();
    }

    [Fact]
    public void Validate_UnknownToolName_ReturnsNull()
    {
        // Act — unknown-tool dispatch is the SDK's concern; the guard never blocks it.
        string? message = UnknownParameterGuard.Validate(
            "no_such_tool",
            new Dictionary<string, JsonElement> { ["whatever"] = DummyValue });

        // Assert
        message.ShouldBeNull();
    }

    [Fact]
    public void Validate_NullArguments_ReturnsNull() => UnknownParameterGuard.Validate("get_symbols_overview", null).ShouldBeNull();

    [Fact]
    public void Validate_EmptyArguments_ReturnsNull()
    {
        UnknownParameterGuard.Validate(
            "get_symbols_overview",
            new Dictionary<string, JsonElement>()).ShouldBeNull();
    }

    [Theory]
    [InlineData("relativePath")]
    [InlineData("location")]
    [InlineData("symbolNames")]
    public void Validate_HallucinatedKeyOnGetSymbolsOverview_ReturnsError(string hallucinatedKey)
    {
        // Act — the keys the model guesses because the description names no parameter.
        string? message = UnknownParameterGuard.Validate(
            "get_symbols_overview",
            new Dictionary<string, JsonElement> { [hallucinatedKey] = DummyValue });

        // Assert
        message.ShouldNotBeNull();
        message.ShouldContain($"\"{hallucinatedKey}\"");
        message.ShouldContain("get_symbols_overview");
    }

    [Fact]
    public void Validate_EmptyObjectOnGetSymbolsOverview_ReturnsNull()
    {
        // The NEW-C "{}" case: no keys at all is not a typo — let the downstream
        // "Either filePaths or project must be specified" message handle it.
        UnknownParameterGuard.Validate(
            "get_symbols_overview",
            new Dictionary<string, JsonElement>()).ShouldBeNull();
    }

    [Fact]
    public void Validate_FindSymbolWithSymbolNames_ReturnsNull()
    {
        // Rec #4: find_symbol's batch parameter is now `symbolNames`, unified with
        // every other symbol-name tool. The name the model already reaches for is
        // valid — no naming-tax round-trip.
        UnknownParameterGuard.Validate(
            "find_symbol",
            new Dictionary<string, JsonElement> { ["symbolNames"] = DummyValue }).ShouldBeNull();
    }

    [Theory]
    [InlineData("find_symbol")]
    [InlineData("find_references")]
    [InlineData("find_overloads")]
    [InlineData("find_implementations")]
    [InlineData("get_type_hierarchy")]
    public void SymbolNameBatchTool_ExposesSymbolNamesAndNotNames(string toolName)
    {
        // Rec #4 unification pin: every symbol-name batch tool must spell its array
        // parameter `symbolNames`. A future revert to `names` (or any other spelling)
        // silently re-introduces the cross-tool naming tax this fix removed.
        MethodInfo method = ToolAttributeDiscovery.GetToolMethods()
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);

        string[] parameterNames = method.GetParameters().Select(p => p.Name!).ToArray();

        parameterNames.ShouldContain("symbolNames");
        parameterNames.ShouldNotContain("names");
    }

    [Fact]
    public void AddUsings_ExposesFilePathNotLocation()
    {
        // `add_usings` takes a plain file path, not a cursor. The `location` name was
        // collateral from the blanket filePath→location sweep (commit d45fdcd): it
        // falsely advertised `:line:col` support (LocationParser.ParseFile rejects it) and
        // split the file-target slot from the `filePath`/`filePaths` convention every other
        // file-target tool uses. A future re-sweep silently reintroduces that naming tax.
        MethodInfo method = ToolAttributeDiscovery.GetToolMethods()
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == "add_usings");

        string[] parameterNames = method.GetParameters().Select(p => p.Name!).ToArray();

        parameterNames.ShouldContain("filePath");
        parameterNames.ShouldNotContain("location");
    }

    // Independent oracle for "is this a JSON-bound parameter": the only context-bound
    // method-parameter types in the codebase are CancellationToken and
    // IProgress<ProgressNotificationValue>. Deliberately NOT calling the guard's own
    // predicate, so a divergence is observable.
    private static bool IsJsonBound(ParameterInfo p)
    {
        if (p.Name is null || p.ParameterType == typeof(CancellationToken))
        {
            return false;
        }

        return !(p.ParameterType.IsGenericType
                 && p.ParameterType.GetGenericTypeDefinition() == typeof(IProgress<>));
    }
}
