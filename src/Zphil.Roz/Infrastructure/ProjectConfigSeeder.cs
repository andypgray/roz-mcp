using System.Text.Json;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Infrastructure;

/// <summary>
///     Seeds process environment variables from an optional per-project <c>.roz.json</c> file, so a
///     globally-configured launcher (e.g. a Claude Code plugin whose launch command carries no
///     per-project <c>env</c> block) still gets per-project configuration. Keys are the exact
///     <c>ROZ_*</c> variable names from <see cref="RozEnvVars" />; an environment variable that is
///     already set always wins over the file.
/// </summary>
/// <remarks>
///     <para>
///         Runs as step 0 of server startup — before Serilog is initialized and while stdout is
///         reserved for the MCP protocol — so this class must never write to Console or
///         <c>Serilog.Log</c>. Outcomes (applied keys, env overrides, warnings) ride the
///         <see cref="ProjectConfigSeedResult" /> stored in <see cref="Current" />; callers log or
///         print them once their output channel is up.
///     </para>
///     <para>
///         Precedence intentionally matches consumers byte-for-byte: a variable counts as "set" iff
///         <see cref="EnvParse.RawString" /> returns non-null (unset and whitespace-only both count
///         as unset), because that is the same test every <c>ROZ_*</c> consumer applies.
///     </para>
/// </remarks>
internal static class ProjectConfigSeeder
{
    public const string FileName = ".roz.json";

    /// <summary>
    ///     Every variable name the config file may set: the <c>ROZ_</c>-prefixed subset of
    ///     <see cref="RozEnvVars.All" />. Derived programmatically so future registry entries are
    ///     covered automatically, while client-owned names (<c>MAX_MCP_OUTPUT_TOKENS</c>,
    ///     <c>CLAUDE_CODE_SESSION_ID</c>) are excluded by construction. The prefix filter also means
    ///     a repo-committed file can never become a generic env injector (<c>PATH</c>,
    ///     <c>DOTNET_*</c>, …).
    /// </summary>
    internal static IReadOnlySet<string> SeedableNames { get; } = RozEnvVars.All
        .Select(v => v.Name)
        .Where(n => n.StartsWith("ROZ_", StringComparison.Ordinal))
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    ///     Result of the one <see cref="Seed" /> call made at process start; <c>null</c> until then.
    ///     Read by <c>get_workspace_info</c> (config provenance line) and the setup environment check.
    /// </summary>
    public static ProjectConfigSeedResult? Current { get; private set; }

    internal static void ResetForTests() => Current = null;

    /// <summary>
    ///     Discovers the nearest <c>.roz.json</c>, plans the seedable settings via
    ///     <see cref="Compute" />, applies them with <see cref="Environment.SetEnvironmentVariable(string, string)" />,
    ///     and stores the outcome in <see cref="Current" />.
    /// </summary>
    /// <remarks>
    ///     Never throws: seeding an optional config file must not block startup, so any unexpected
    ///     failure degrades to a warning in the result.
    /// </remarks>
    public static ProjectConfigSeedResult Seed(string? workingDirectory = null)
    {
        ProjectConfigSeedResult result;
        try
        {
            string cwd = workingDirectory ?? Directory.GetCurrentDirectory();
            string? configFilePath = DiscoverConfigFile(cwd);
            if (configFilePath is null)
            {
                result = new ProjectConfigSeedResult(null, [], [], []);
            }
            else
            {
                string json = File.ReadAllText(configFilePath);
                result = Compute(configFilePath, json, EnvParse.RawString);
                foreach (AppliedSetting setting in result.Applied)
                {
                    Environment.SetEnvironmentVariable(setting.Name, setting.Value);
                }
            }
        }
        // Deliberate whole-body catch-all (documented exception to the no-catch-all rule, same
        // class as SetupCommand.ValidateWorkspaceAsync): this runs before any output channel
        // exists, so the only non-blocking way to surface an unexpected failure is as a warning.
        catch (Exception ex)
        {
            result = new ProjectConfigSeedResult(
                null, [], [], [$"{FileName} seeding failed and was skipped: {ex.Message}"]);
        }

        Current = result;
        return result;
    }

    /// <summary>
    ///     Walks up from <paramref name="workingDirectory" /> and returns the first directory's
    ///     <c>.roz.json</c>, or <c>null</c> when no ancestor has one.
    /// </summary>
    /// <remarks>
    ///     The first file found stops the walk even if it is unparseable — an empty <c>{}</c> is the
    ///     documented opt-out that shields a nested project from a parent's config.
    /// </remarks>
    internal static string? DiscoverConfigFile(string workingDirectory)
    {
        foreach (string directory in FileUtility.EnumerateSelfAndAncestors(Path.GetFullPath(workingDirectory)))
        {
            string candidate = Path.Combine(directory, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    ///     Pure planner: parses <paramref name="json" /> leniently and decides, per key, whether it
    ///     applies, is overridden by the environment (<paramref name="getEnv" /> returned non-null),
    ///     or is skipped with a warning.
    /// </summary>
    /// <remarks>
    ///     Performs no I/O and no env mutation, so tests can drive it with an injected environment.
    /// </remarks>
    internal static ProjectConfigSeedResult Compute(string configFilePath, string json, Func<string, string?> getEnv)
    {
        // Whitespace-only (including empty) is the documented opt-out for nested projects: an
        // empty config, not a malformed one — no warning.
        if (String.IsNullOrWhiteSpace(json))
        {
            return new ProjectConfigSeedResult(configFilePath, [], [], []);
        }

        JsonDocumentOptions parseOptions = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            AllowDuplicateProperties = false
        };

        JsonDocument document;
        try
        {
            // Strip a leading BOM: File.ReadAllText usually consumes it, but a caller-supplied
            // string (or an unusual encoding) may still carry U+FEFF, which the JSON parser rejects.
            document = JsonDocument.Parse(json.TrimStart((char)0xFEFF), parseOptions);
        }
        catch (JsonException ex)
        {
            return new ProjectConfigSeedResult(
                configFilePath, [], [],
                [$"{FileName} is not valid JSON and was ignored: {ex.Message}"]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ProjectConfigSeedResult(
                    configFilePath, [], [],
                    [$"{FileName} must contain a JSON object of \"ROZ_*\": \"value\" pairs; found {document.RootElement.ValueKind}. File ignored."]);
            }

            List<AppliedSetting> applied = [];
            List<string> overriddenByEnv = [];
            List<string> warnings = [];

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                PlanProperty(property, configFilePath, getEnv, applied, overriddenByEnv, warnings);
            }

            return new ProjectConfigSeedResult(configFilePath, applied, overriddenByEnv, warnings);
        }
    }

    private static void PlanProperty(
        JsonProperty property,
        string configFilePath,
        Func<string, string?> getEnv,
        List<AppliedSetting> applied,
        List<string> overriddenByEnv,
        List<string> warnings)
    {
        string name = property.Name;
        if (!SeedableNames.Contains(name))
        {
            warnings.Add($"Unknown key '{name}' skipped — only ROZ_-prefixed variables from the registry are honored.");
            return;
        }

        string? value = CoerceValue(property.Value, name, warnings);
        if (value is null)
        {
            return;
        }

        if (getEnv(name) is not null)
        {
            overriddenByEnv.Add(name);
            return;
        }

        if (name == RozEnvVars.SolutionPath.Name)
        {
            value = ResolveSolutionPath(value, configFilePath, warnings);
            if (value is null)
            {
                return;
            }
        }

        applied.Add(new AppliedSetting(name, value));
    }

    /// <summary>
    ///     Coerces a JSON value to the environment-variable string: strings pass through, numbers and
    ///     booleans become their invariant JSON text (<c>true</c> → <c>"true"</c>, matching
    ///     <see cref="EnvParse.BoolTrue" />). Blank strings and structured values return <c>null</c>
    ///     with a warning — seeding a whitespace value would read as unset to every consumer.
    /// </summary>
    private static string? CoerceValue(JsonElement element, string name, List<string> warnings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                string text = element.GetString()!;
                if (String.IsNullOrWhiteSpace(text))
                {
                    warnings.Add($"Key '{name}' skipped — its value is blank.");
                    return null;
                }

                return text;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                // Raw JSON text is already invariant ("30", "1.5", "true", "false").
                return element.GetRawText();

            default:
                warnings.Add($"Key '{name}' skipped — value must be a string, number, or boolean; found {element.ValueKind}.");
                return null;
        }
    }

    /// <summary>
    ///     Resolves a relative <c>ROZ_SOLUTION_PATH</c> against the config file's directory (an
    ///     absolute path passes through unchanged apart from normalization). No existence check here:
    ///     <c>FileUtility.DiscoverSolution</c> stays the single validator of the path.
    /// </summary>
    private static string? ResolveSolutionPath(string value, string configFilePath, List<string> warnings)
    {
        string? configDir = Path.GetDirectoryName(configFilePath);
        if (String.IsNullOrEmpty(configDir))
        {
            return value;
        }

        try
        {
            return Path.GetFullPath(value, Path.GetFullPath(configDir));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            warnings.Add($"Key '{RozEnvVars.SolutionPath.Name}' skipped — its value could not be resolved as a path: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
///     Outcome of one <see cref="ProjectConfigSeeder.Seed" /> pass. <see cref="ConfigFilePath" /> is
///     <c>null</c> when no file was found (or seeding itself failed); <see cref="Applied" /> holds the
///     settings written into the process environment; <see cref="OverriddenByEnv" /> the file keys a
///     live environment variable beat; <see cref="Warnings" /> everything skipped or unparseable.
/// </summary>
internal sealed record ProjectConfigSeedResult(
    string? ConfigFilePath,
    IReadOnlyList<AppliedSetting> Applied,
    IReadOnlyList<string> OverriddenByEnv,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Gets the names of the applied settings, in file order.</summary>
    public IReadOnlyList<string> AppliedNames => Applied.Select(a => a.Name).ToList();

    /// <summary>
    ///     The file was found but contributed nothing except warnings — unparseable, or every key
    ///     skipped. Distinct from "applied: none" states where the environment legitimately won.
    /// </summary>
    public bool IsIgnored =>
        ConfigFilePath is not null && Applied.Count == 0 && OverriddenByEnv.Count == 0 && Warnings.Count > 0;

    /// <summary>
    ///     Canonical one-line outcome summary shared by every surface that reports the seed
    ///     (<c>get_workspace_info</c>, the setup check, the startup log): <c>applied: A, B</c> or
    ///     <c>applied: none</c>, then <c>overridden by env: …</c> and <c>warnings: …</c> when present,
    ///     joined with <c>"; "</c>. The <see cref="IsIgnored" /> state renders as <c>ignored: …</c> so
    ///     an unparseable file is never misreported as an environment-precedence outcome.
    /// </summary>
    /// <param name="withValues">Render applied settings as <c>NAME=value</c> instead of names only.</param>
    /// <param name="includeWarnings">
    ///     Include the warning texts. The startup log passes <c>false</c>: its per-warning
    ///     Warning-level lines already carry them, and embedding them in the Information summary too
    ///     would log every warning twice.
    /// </param>
    public string Summary(bool withValues = false, bool includeWarnings = true)
    {
        if (IsIgnored)
        {
            return includeWarnings ? $"ignored: {String.Join(" | ", Warnings)}" : "ignored";
        }

        List<string> parts = [];
        if (Applied.Count > 0)
        {
            IEnumerable<string> applied = withValues
                ? Applied.Select(a => $"{a.Name}={a.Value}")
                : AppliedNames;
            parts.Add($"applied: {String.Join(", ", applied)}");
        }
        else
        {
            parts.Add("applied: none");
        }

        if (OverriddenByEnv.Count > 0)
        {
            parts.Add($"overridden by env: {String.Join(", ", OverriddenByEnv)}");
        }

        if (includeWarnings && Warnings.Count > 0)
        {
            parts.Add($"warnings: {String.Join(" | ", Warnings)}");
        }

        return String.Join("; ", parts);
    }
}

/// <summary>A single <c>NAME=VALUE</c> pair the seeder wrote into the process environment.</summary>
internal sealed record AppliedSetting(string Name, string Value);
