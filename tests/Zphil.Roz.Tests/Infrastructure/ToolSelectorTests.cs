using System.Reflection;
using ModelContextProtocol.Server;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Pipeline;

namespace Zphil.Roz.Tests.Infrastructure;

public class ToolSelectorTests : IDisposable
{
    private const int TotalToolCount = 19;

    // 19 total − 7 risky − 1 held (analyze_method; pending A/B validation) = 11.
    // analyze_change_impact was promoted to default 2026-06-16 (backs the impact/tighten prompts).
    private const int DefaultPresetToolCount = 11;

    private static readonly string[] RiskyTools =
        ["edit_symbol", "replace_content", "apply_code_fix", "change_signature", "add_usings", "remove_unused_usings", "get_unused_references"];

    public void Dispose() => Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, null);

    [Fact]
    public void GetEnabledTools_UnsetEnvVar_ReturnsDefaultPresetWithoutRiskyTools()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, null);

        // Act
        IReadOnlyList<MethodInfo> tools = ToolSelector.GetEnabledTools();

        // Assert
        tools.Count.ShouldBe(DefaultPresetToolCount);
        HashSet<string> names = GetToolNames(tools);
        foreach (string risky in RiskyTools)
        {
            names.ShouldNotContain(risky);
        }

        names.ShouldContain("rename_symbol");
    }

    [Fact]
    public void GetEnabledTools_EmptyString_AppliesDefaultPreset()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "");

        // Act
        IReadOnlyList<MethodInfo> tools = ToolSelector.GetEnabledTools();

        // Assert
        tools.Count.ShouldBe(DefaultPresetToolCount);
    }

    [Fact]
    public void GetEnabledTools_Whitespace_AppliesDefaultPreset()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "   ");

        // Act
        IReadOnlyList<MethodInfo> tools = ToolSelector.GetEnabledTools();

        // Assert
        tools.Count.ShouldBe(DefaultPresetToolCount);
    }

    [Fact]
    public void GetEnabledTools_AllPreset_ReturnsAllTools()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "all");

        // Act
        IReadOnlyList<MethodInfo> tools = ToolSelector.GetEnabledTools();

        // Assert
        tools.Count.ShouldBe(TotalToolCount);
    }

    [Fact]
    public void GetEnabledTools_ReadPreset_ExcludesEditingAndUsings()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "read");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert
        names.ShouldContain("find_symbol");
        names.ShouldContain("find_references");
        names.ShouldContain("get_type_hierarchy");
        names.ShouldContain("get_diagnostics");
        names.ShouldContain("get_workspace_info");
        names.ShouldNotContain("edit_symbol");
        names.ShouldNotContain("rename_symbol");
        names.ShouldNotContain("add_usings");
    }

    [Fact]
    public void GetEnabledTools_EditPreset_IncludesEditingAndUsings()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "edit");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert
        names.ShouldContain("edit_symbol");
        names.ShouldContain("rename_symbol");
        names.ShouldContain("replace_content");
        names.ShouldContain("add_usings");
        names.ShouldContain("remove_unused_usings");
        names.ShouldContain("get_diagnostics");
        names.ShouldContain("get_workspace_info");
        names.ShouldNotContain("find_symbol");
    }

    [Fact]
    public void GetEnabledTools_SingleCategory_ReturnsCategoryTools()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "navigation");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert
        names.ShouldBe(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "find_symbol",
            "get_symbols_overview",
            "find_overloads",
            "analyze_method",
            "go_to_definition"
        });
    }

    [Fact]
    public void GetEnabledTools_CommaDelimited_ReturnsRequestedCategories()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "navigation,references");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert — navigation (5) + references (find_references, find_implementations, analyze_change_impact)
        names.Count.ShouldBe(8);
        names.ShouldContain("find_symbol");
        names.ShouldContain("analyze_method");
        names.ShouldContain("find_references");
        names.ShouldContain("find_implementations");
        names.ShouldContain("analyze_change_impact");
    }

    [Fact]
    public void GetEnabledTools_SemicolonDelimited_ReturnsRequestedCategories()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "navigation;references");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert
        names.ShouldContain("find_symbol");
        names.ShouldContain("find_references");
    }

    [Fact]
    public void GetEnabledTools_UpperCase_ResolvesCaseInsensitively()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "NAVIGATION,READ");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert
        names.ShouldContain("find_symbol");
        names.ShouldContain("get_diagnostics");
    }

    [Fact]
    public void GetEnabledTools_UnknownTokenAmongValid_DropsUnknownAndKeepsValid()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "navigation,bogus");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert
        names.ShouldContain("find_symbol");
        names.Count.ShouldBe(5);
    }

    [Fact]
    public void GetEnabledTools_AllTokensUnknown_Throws()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "bogus,alsobogus");

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => ToolSelector.GetEnabledTools())
            .Message.ShouldContain(RozEnvVars.Tools.Name);
    }

    [Fact]
    public void GetEnabledTools_DuplicateToken_ReturnsSingleInstance()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "navigation,navigation,navigation");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert
        names.Count.ShouldBe(5);
    }

    [Fact]
    public void GetEnabledTools_ToolNameToken_IncludesOnlyThatTool()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "edit_symbol");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert
        names.ShouldBe(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "edit_symbol" });
    }

    [Fact]
    public void GetEnabledTools_ExclusionOfToolName_RemovesJustThatTool()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "all,-edit_symbol");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert
        names.Count.ShouldBe(TotalToolCount - 1);
        names.ShouldNotContain("edit_symbol");
        names.ShouldContain("rename_symbol");
        names.ShouldContain("replace_content");
    }

    [Fact]
    public void GetEnabledTools_ExclusionOfCategory_RemovesAllToolsInCategory()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "all,-usings");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert
        names.Count.ShouldBe(TotalToolCount - 2);
        names.ShouldNotContain("add_usings");
        names.ShouldNotContain("remove_unused_usings");
    }

    [Fact]
    public void GetEnabledTools_ExclusionOfPreset_RemovesAllPresetTools()
    {
        // Arrange — subtract the read preset from "all"
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "all,-read");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert — only editing + usings remain
        names.ShouldContain("edit_symbol");
        names.ShouldContain("rename_symbol");
        names.ShouldContain("replace_content");
        names.ShouldContain("add_usings");
        names.ShouldContain("remove_unused_usings");
        names.ShouldNotContain("find_symbol");
        names.ShouldNotContain("get_workspace_info");
    }

    [Fact]
    public void GetEnabledTools_DefaultPreset_HasExpectedCountAndExcludesRiskyTools()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "default");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert
        names.Count.ShouldBe(DefaultPresetToolCount);
        foreach (string risky in RiskyTools)
        {
            names.ShouldNotContain(risky);
        }

        names.ShouldContain("rename_symbol");
    }

    [Fact]
    public void GetEnabledTools_AdditionThenExclusion_AppliesLeftToRight()
    {
        // Arrange — re-add add_usings after subtracting the usings category
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "all,-usings,add_usings");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert
        names.Count.ShouldBe(TotalToolCount - 1);
        names.ShouldContain("add_usings");
        names.ShouldNotContain("remove_unused_usings");
    }

    [Fact]
    public void GetEnabledTools_ExclusionOnly_StartsFromEmptySetAndReturnsNoTools()
    {
        // Arrange — exclusion-only input. The plan calls this legal: zero tools registered.
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "-edit_symbol");

        // Act
        IReadOnlyList<MethodInfo> tools = ToolSelector.GetEnabledTools();

        // Assert
        tools.Count.ShouldBe(0);
    }

    [Fact]
    public void GetEnabledTools_DefaultAndExclusion_WorksTogether()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "default,-rename_symbol");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert
        names.Count.ShouldBe(DefaultPresetToolCount - 1);
        names.ShouldNotContain("rename_symbol");
    }

    [Fact]
    public void GetEnabledTools_PresetPlusExplicit_ReturnsUnion()
    {
        // Arrange
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "navigate,editing");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert
        names.ShouldContain("find_symbol");
        names.ShouldContain("find_references");
        names.ShouldContain("get_type_hierarchy");
        names.ShouldContain("get_workspace_info");
        names.ShouldContain("edit_symbol");
        names.ShouldContain("rename_symbol");
        names.ShouldNotContain("add_usings");
    }

    [Fact]
    public void GetEnabledTools_AnalyzeMethod_HeldOutOfDefaultButPresentInAll()
    {
        // Arrange — analyze_method is held out of `default` pending A/B validation (not risky).
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "all");
        HashSet<string> all = GetToolNames(ToolSelector.GetEnabledTools());

        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "default");
        HashSet<string> def = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert — reachable via all, withheld from default.
        all.ShouldContain("analyze_method");
        def.ShouldNotContain("analyze_method");
    }

    [Fact]
    public void GetEnabledTools_DefaultPlusAnalyzeMethod_OptsHeldToolBackIn()
    {
        // Arrange — explicit opt-in re-adds the held tool on top of the default preset.
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "default,analyze_method");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert
        names.ShouldContain("analyze_method");
        names.Count.ShouldBe(DefaultPresetToolCount + 1);
    }

    [Fact]
    public void GetEnabledTools_AnalyzeChangeImpact_PromotedIntoDefaultPreset()
    {
        // Arrange — analyze_change_impact was promoted to `default` 2026-06-16 to back the
        // assess_impact / tighten_accessibility prompts (superseding its A/B HOLD).
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "all");
        HashSet<string> all = GetToolNames(ToolSelector.GetEnabledTools());

        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "default");
        HashSet<string> def = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert — now present in both presets.
        all.ShouldContain("analyze_change_impact");
        def.ShouldContain("analyze_change_impact");
    }

    [Fact]
    public void GetEnabledTools_DefaultPlusAnalyzeChangeImpact_IsIdempotent()
    {
        // Arrange — analyze_change_impact is already in `default`, so naming it again adds nothing.
        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "default,analyze_change_impact");

        // Act
        HashSet<string> names = GetToolNames(ToolSelector.GetEnabledTools());

        // Assert
        names.ShouldContain("analyze_change_impact");
        names.Count.ShouldBe(DefaultPresetToolCount);
    }

    [Fact]
    public void IsValid_RecognisedPreset_ReturnsTrueAndNullError()
    {
        // Act
        bool valid = ToolSelector.IsValid("read", out string? error);

        // Assert
        valid.ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public void IsValid_RecognisedCategory_ReturnsTrue()
    {
        // Act
        bool valid = ToolSelector.IsValid("navigation", out string? error);

        // Assert
        valid.ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public void IsValid_RecognisedToolName_ReturnsTrue()
    {
        // Act
        bool valid = ToolSelector.IsValid("edit_symbol", out string? error);

        // Assert
        valid.ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public void IsValid_ExclusionToken_ReturnsTrue()
    {
        // Act
        bool valid = ToolSelector.IsValid("-edit_symbol", out string? error);

        // Assert
        valid.ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public void IsValid_PartiallyValid_ReturnsTrue()
    {
        // Act — at least one recognised token means IsValid passes (matches runtime's tolerant behaviour)
        bool valid = ToolSelector.IsValid("navigation,bogus", out string? error);

        // Assert
        valid.ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public void IsValid_AllTokensUnknown_ReturnsFalseWithHint()
    {
        // Act
        bool valid = ToolSelector.IsValid("reed,alsobogus", out string? error);

        // Assert
        valid.ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain(RozEnvVars.Tools.Name);
        error.ShouldContain("navigation");
        error.ShouldContain("read");
    }

    [Fact]
    public void IsValid_CaseInsensitive()
    {
        // Act
        bool valid = ToolSelector.IsValid("NAVIGATION", out string? error);

        // Assert
        valid.ShouldBeTrue();
        error.ShouldBeNull();
    }

    /// <summary>
    ///     Regression guard: the <c>all</c> preset must cover every <see cref="McpServerToolAttribute" />
    ///     method in the assembly. If someone adds a new tool but forgets to map its declaring type in
    ///     <see cref="ToolSelector" />'s <c>TypeToCategory</c> dictionary, discovery throws at startup
    ///     — but this test catches missing registrations in CI before the server tries to boot.
    /// </summary>
    [Fact]
    public void AllPreset_CoversEveryMcpServerToolMethodInAssembly()
    {
        // Arrange
        MethodInfo[] attributedMethods = ToolAttributeDiscovery
            .GetToolMethods()
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name is not null)
            .ToArray();

        Environment.SetEnvironmentVariable(RozEnvVars.Tools.Name, "all");

        // Act
        IReadOnlyList<MethodInfo> viaAllPreset = ToolSelector.GetEnabledTools();

        // Assert
        viaAllPreset.Count.ShouldBe(attributedMethods.Length);
        HashSet<string> enabledNames = GetToolNames(viaAllPreset);
        foreach (MethodInfo method in attributedMethods)
        {
            string? name = method.GetCustomAttribute<McpServerToolAttribute>()?.Name;
            name.ShouldNotBeNull();
            enabledNames.ShouldContain(name,
                $"{nameof(ToolSelector)} missing name-index entry for {method.DeclaringType?.Name}.{method.Name}");
        }
    }

    private static HashSet<string> GetToolNames(IEnumerable<MethodInfo> methods)
    {
        return new HashSet<string>(
            methods
                .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name!)
                .Where(n => n is not null),
            StringComparer.OrdinalIgnoreCase);
    }
}
