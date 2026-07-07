namespace Zphil.Roz.Infrastructure;

/// <summary>
///     Minimal env-var parsing helpers shared across the server.
/// </summary>
/// <remarks>
///     <para>
///         Each helper covers one common shape (bool flag, raw string, delimited list, positive int)
///         and reads directly from <see cref="Environment.GetEnvironmentVariable(string)" />. They
///         are intentionally small: bespoke parsers with their own logging or domain semantics
///         (e.g. <c>ROZ_IDLE_TIMEOUT_MINUTES</c>'s "0 disables / warn on invalid" rule, or
///         <c>ROZ_LOG_LEVEL</c>'s dual Serilog/Microsoft enum acceptance) stay at their original
///         call sites rather than being squeezed into a generic helper.
///     </para>
///     <para>
///         <see cref="RozEnvVars" /> is the source-of-truth for which variable names exist —
///         call these helpers with <c>RozEnvVars.Xxx.Name</c>, not literal strings.
///     </para>
/// </remarks>
internal static class EnvParse
{
    /// <summary>
    ///     Returns <c>true</c> when the env var's value equals <c>"true"</c> (case-insensitive).
    ///     Any other value — including unset, blank, <c>"1"</c>, <c>"yes"</c> — returns <c>false</c>.
    /// </summary>
    public static bool BoolTrue(string name) =>
        String.Equals(
            Environment.GetEnvironmentVariable(name),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Returns the raw env-var value, or <c>null</c> when the variable is unset or contains only whitespace.
    /// </summary>
    public static string? RawString(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return String.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    ///     Splits the env-var value on <c>,</c> and <c>;</c>, trims each entry, and drops empties.
    ///     Returns an empty list when the variable is unset or contains only whitespace.
    /// </summary>
    /// <remarks>
    ///     Domain-specific normalisation (path canonicalisation, case folding, etc.) is the
    ///     caller's responsibility — this helper deliberately stays generic.
    /// </remarks>
    public static IReadOnlyList<string> DelimitedList(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (String.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>
    ///     Parses the env-var value as a positive <see cref="Int32" />. Returns <c>null</c> when
    ///     the variable is unset, unparseable, or non-positive.
    /// </summary>
    public static int? PositiveInt(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (value is null || !Int32.TryParse(value, out int parsed) || parsed <= 0)
        {
            return null;
        }

        return parsed;
    }
}
