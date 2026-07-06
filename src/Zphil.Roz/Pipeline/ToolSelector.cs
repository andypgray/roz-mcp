using System.Reflection;
using ModelContextProtocol.Server;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Pipeline;

/// <summary>
///     Resolves which <see cref="McpServerToolAttribute" />-annotated tool methods get registered,
///     based on the <c>ROZ_TOOLS</c> environment variable. Shrinks per-session schema overhead
///     for users who only need a subset of the server's capabilities.
/// </summary>
/// <remarks>
///     Tokens (comma- or semicolon-delimited, case-insensitive, processed left-to-right) may be:
///     <list type="bullet">
///         <item>
///             <description>
///                 Presets: <c>all</c>, <c>default</c>, <c>read</c>, <c>navigate</c>, <c>edit</c>.
///             </description>
///         </item>
///         <item>
///             <description>
///                 Categories: <c>navigation</c>, <c>references</c>, <c>types</c>, <c>editing</c>,
///                 <c>usings</c>, <c>diagnostics</c>, <c>workspace</c>.
///             </description>
///         </item>
///         <item>
///             <description>Individual tool names: <c>edit_symbol</c>, <c>rename_symbol</c>, etc.</description>
///         </item>
///         <item>
///             <description>
///                 Exclusions: a <c>-</c> prefix removes the matched preset/category/tool from
///                 the set built so far (e.g. <c>all,-usings,-edit_symbol</c>).
///             </description>
///         </item>
///     </list>
///     Unset or empty value applies the <c>default</c> preset (all tools except the destructive
///     <c>edit_symbol</c>, <c>replace_content</c>, <c>apply_code_fix</c>, <c>change_signature</c>,
///     <c>add_usings</c>, <c>remove_unused_usings</c>, the niche <c>get_unused_references</c>, and
///     <c>analyze_method</c>, which is held out pending A/B validation). Explicit
///     <c>all</c> remains available as an opt-in escape hatch. Unknown tokens are dropped with a
///     stderr warning. If every token is invalid, startup throws. Recognised tokens that net to
///     an empty set (e.g. exclusion-only input) start the server with no tools and log a stderr
///     warning.
/// </remarks>
internal static class ToolSelector
{
    private const string AllPreset = "all";
    private const string DefaultPreset = "default";

    private static readonly IReadOnlyDictionary<Type, string> TypeToCategory =
        new Dictionary<Type, string>
        {
            [typeof(NavigationTools)] = "navigation",
            [typeof(ReferenceTools)] = "references",
            [typeof(TypeHierarchyTools)] = "types",
            [typeof(CodeEditTools)] = "editing",
            [typeof(UsingDirectiveTools)] = "usings",
            [typeof(DiagnosticTools)] = "diagnostics",
            [typeof(WorkspaceTools)] = "workspace"
        };

    private static readonly IReadOnlyList<ToolEntry> AllToolsCache = DiscoverTools();

    private static readonly IReadOnlyDictionary<string, MethodInfo> ToolNameToMethod =
        AllToolsCache.ToDictionary(e => e.Name, e => e.Method, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string[]> CategoryToToolNames =
        AllToolsCache
            .GroupBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Name).ToArray(), StringComparer.OrdinalIgnoreCase);

    private static readonly string[] RiskyToolsExcludedFromDefault =
        ["edit_symbol", "replace_content", "apply_code_fix", "change_signature", "add_usings", "remove_unused_usings", "get_unused_references"];

    // Not risky — just unvalidated. Held out of the default preset until the A/B arm confirms the
    // turn-count win; still reachable via all/read/navigate/navigation or by explicit name. Removing
    // an entry here is the one-line "promote to default" follow-up. See ARCHITECTURE.md Selective Tool Loading.
    // analyze_change_impact was promoted to default 2026-06-16 to back the assess_impact /
    // tighten_accessibility prompts (user-invoked value), superseding its A/B HOLD; analyze_method stays held.
    private static readonly string[] HeldFromDefaultPendingValidation =
        ["analyze_method"];

    private static readonly IReadOnlyDictionary<string, string[]> Presets = BuildPresets();

    /// <summary>
    ///     Returns the tool <see cref="MethodInfo" /> entries to register for this process.
    /// </summary>
    internal static IReadOnlyList<MethodInfo> GetEnabledTools()
    {
        string? rawValue = EnvParse.RawString(RozEnvVars.Tools.Name);

        if (rawValue is null)
        {
            return ResolvePresetToMethods(DefaultPreset);
        }

        string[] tokens = rawValue.Split(
            [',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        HashSet<string> enabled = new(StringComparer.OrdinalIgnoreCase);
        List<string> invalidTokens = [];
        var anyRecognised = false;

        foreach (string token in tokens)
        {
            bool isExclusion = token.StartsWith('-');
            string body = isExclusion ? token[1..].Trim() : token;

            IReadOnlyCollection<string>? names = body.Length == 0 ? null : ResolveToken(body);
            if (names is null)
            {
                invalidTokens.Add(token);
                continue;
            }

            anyRecognised = true;

            if (isExclusion)
            {
                foreach (string n in names)
                {
                    enabled.Remove(n);
                }
            }
            else
            {
                foreach (string n in names)
                {
                    enabled.Add(n);
                }
            }
        }

        if (invalidTokens.Count > 0)
        {
            Console.Error.WriteLine(
                $"[{RozEnvVars.Tools.Name}] Ignoring unknown token(s): {String.Join(", ", invalidTokens)}. Valid values: {FormatValidKeys()}.");
        }

        if (!anyRecognised)
        {
            throw new InvalidOperationException(
                $"{RozEnvVars.Tools.Name}='{rawValue}' resolved to zero valid tool tokens. Valid values: {FormatValidKeys()}.");
        }

        if (enabled.Count == 0)
        {
            // Recognised tokens that net to nothing (exclusion-only input, or every included
            // tool later excluded) leave the server with no tools — legal but easy to do by
            // accident. Warn so it doesn't look like the server silently failed to start.
            Console.Error.WriteLine(
                $"[{RozEnvVars.Tools.Name}] '{rawValue}' resolved to zero tools; the server will start with no tools registered. " +
                "Exclusions subtract from an empty set — prefix a preset or category to include tools first (e.g. 'all,-edit_symbol').");
        }

        return enabled
            .Select(n => ToolNameToMethod[n])
            .ToArray();
    }

    /// <summary>
    ///     Fast pre-flight check for <c>roz-mcp setup --tools=</c>: returns <c>true</c> when
    ///     <paramref name="value" /> contains at least one recognised preset, category, or tool name
    ///     (possibly prefixed with <c>-</c>). When <c>false</c>, <paramref name="error" /> carries a
    ///     user-facing message listing valid keys.
    /// </summary>
    internal static bool IsValid(string value, out string? error)
    {
        string[] tokens = value.Split(
            [',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        bool anyRecognised = tokens.Any(t =>
        {
            string body = t.StartsWith('-') ? t[1..].Trim() : t;
            return body.Length > 0 && ResolveToken(body) is not null;
        });

        if (anyRecognised)
        {
            error = null;
            return true;
        }

        error = $"{RozEnvVars.Tools.Name}='{value}' contains no valid tool tokens. Valid values: {FormatValidKeys()}.";
        return false;
    }

    private static IReadOnlyCollection<string>? ResolveToken(string token)
    {
        if (Presets.TryGetValue(token, out string[]? presetNames))
        {
            return presetNames;
        }

        if (CategoryToToolNames.TryGetValue(token, out string[]? categoryNames))
        {
            return categoryNames;
        }

        if (ToolNameToMethod.ContainsKey(token))
        {
            return [token];
        }

        return null;
    }

    private static IReadOnlyList<MethodInfo> ResolvePresetToMethods(string presetName)
    {
        string[] names = Presets[presetName];
        return names.Select(n => ToolNameToMethod[n]).ToArray();
    }

    private static ToolEntry[] DiscoverTools()
    {
        List<ToolEntry> entries = [];

        foreach (MethodInfo method in ToolAttributeDiscovery.GetToolMethods())
        {
            McpServerToolAttribute? attr = method.GetCustomAttribute<McpServerToolAttribute>();
            if (attr?.Name is null)
            {
                continue;
            }

            if (!TypeToCategory.TryGetValue(method.DeclaringType!, out string? category))
            {
                throw new InvalidOperationException(
                    $"Tool '{attr.Name}' on {method.DeclaringType?.Name} has no category mapping in {nameof(ToolSelector)}.{nameof(TypeToCategory)}.");
            }

            entries.Add(new ToolEntry(attr.Name, category, method));
        }

        return entries.ToArray();
    }

    private static IReadOnlyDictionary<string, string[]> BuildPresets()
    {
        string[] allNames = AllToolsCache.Select(e => e.Name).ToArray();
        string[] defaultNames = allNames
            .Except(RiskyToolsExcludedFromDefault, StringComparer.OrdinalIgnoreCase)
            .Except(HeldFromDefaultPendingValidation, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [AllPreset] = allNames,
            [DefaultPreset] = defaultNames,
            ["read"] = ExpandCategories("navigation", "references", "types", "diagnostics", "workspace"),
            ["navigate"] = ExpandCategories("navigation", "references", "types", "workspace"),
            ["edit"] = ExpandCategories("editing", "usings", "diagnostics", "workspace")
        };
    }

    private static string[] ExpandCategories(params string[] categories) =>
        categories.SelectMany(c => CategoryToToolNames[c]).ToArray();

    private static string FormatValidKeys()
    {
        IEnumerable<string> keys = Presets.Keys
            .Concat(CategoryToToolNames.Keys)
            .Concat(ToolNameToMethod.Keys);
        return String.Join(", ", keys);
    }

    private sealed record ToolEntry(string Name, string Category, MethodInfo Method);
}
