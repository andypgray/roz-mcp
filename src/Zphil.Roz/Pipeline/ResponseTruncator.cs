using System.Collections.Frozen;
using System.Reflection;
using ModelContextProtocol.Server;
using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Pipeline;

/// <summary>
///     Truncates tool response text that exceeds a character limit to prevent
///     context window exhaustion in LLM consumers.
/// </summary>
internal static class ResponseTruncator
{
    private const int DefaultMaxResponseChars = 25_000;
    private const double CharsPerToken = 2.5;

    private static readonly FrozenDictionary<string, string> s_narrowingHints = DiscoverNarrowingHints();

    /// <summary>
    ///     The configured maximum response size in characters.
    /// </summary>
    internal static int MaxChars { get; } = ReadMaxCharsFromEnv();

    public static string TruncateIfNeeded(string text, string? toolName) => TruncateIfNeeded(text, toolName, MaxChars);

    internal static string TruncateIfNeeded(string text, string? toolName, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        int cutPoint = text.LastIndexOf('\n', maxChars - 1);
        if (cutPoint <= 0)
        {
            cutPoint = maxChars;
        }

        string truncated = text[..cutPoint];

        int droppedChars = text.Length - cutPoint;
        string hint = toolName is not null && s_narrowingHints.TryGetValue(toolName, out string? h)
            ? $" {h}"
            : "";

        return $"{truncated}\n\n--- RESPONSE TRUNCATED ---\nOutput was {text.Length:N0} characters, limit is {maxChars:N0} ({droppedChars:N0} characters omitted).\nThe results above are incomplete.{hint}";
    }

    private static FrozenDictionary<string, string> DiscoverNarrowingHints()
    {
        Dictionary<string, string> hints = new(StringComparer.Ordinal);

        foreach (MethodInfo method in ToolAttributeDiscovery.GetToolMethods())
        {
            McpServerToolAttribute? toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
            NarrowingHintAttribute? hintAttr = method.GetCustomAttribute<NarrowingHintAttribute>();
            if (toolAttr?.Name is not null && hintAttr is not null)
            {
                hints[toolAttr.Name] = hintAttr.Hint;
            }
        }

        return hints.ToFrozenDictionary();
    }

    private static int ReadMaxCharsFromEnv()
    {
        int? explicitChars = EnvParse.PositiveInt(RozEnvVars.MaxResponseChars.Name);
        if (explicitChars is { } chars)
        {
            return chars;
        }

        // Fall back to the MCP client's token limit, converted to characters.
        int? tokenLimit = EnvParse.PositiveInt(RozEnvVars.MaxMcpOutputTokens.Name);
        if (tokenLimit is { } tokens)
        {
            return (int)(tokens * CharsPerToken);
        }

        return DefaultMaxResponseChars;
    }
}
