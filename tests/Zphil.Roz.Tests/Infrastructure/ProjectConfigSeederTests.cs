using System.Text.Json;
using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Tests.Infrastructure;

/// <summary>
///     Pure-path tests for <see cref="ProjectConfigSeeder" />: discovery walks a temp directory tree
///     and <see cref="ProjectConfigSeeder.Compute" /> takes an injected environment, so nothing here
///     mutates process env vars and the class is parallel-safe (the
///     <see cref="Zphil.Roz.Tests.Utility.SolutionDiscoveryTests" /> idiom). Env-mutating coverage of
///     <see cref="ProjectConfigSeeder.Seed" /> lives in <see cref="ProjectConfigSeederEnvTests" />.
///     Fixtures use temp dirs only — a real <c>.roz.json</c> in this repo would reconfigure the
///     dogfooding server.
/// </summary>
public class ProjectConfigSeederTests : IDisposable
{
    private static readonly Func<string, string?> EmptyEnv = EnvWith();
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "roz-config-seeder-tests", Guid.NewGuid().ToString("N"));

    public ProjectConfigSeederTests()
    {
        Directory.CreateDirectory(tempRoot);
    }

    private string ConfigPath => Path.Combine(tempRoot, ProjectConfigSeeder.FileName);

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private string CreateDir(params string[] segments)
    {
        string dir = Path.Combine([tempRoot, .. segments]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string CreateConfigFile(string directory, string json)
    {
        string path = Path.Combine(directory, ProjectConfigSeeder.FileName);
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>
    ///     Injected environment mirroring <see cref="EnvParse.RawString" /> semantics: unset and
    ///     whitespace-only both read as null, so Compute's precedence check matches production exactly.
    /// </summary>
    private static Func<string, string?> EnvWith(params (string Name, string? Value)[] vars)
    {
        Dictionary<string, string?> map = vars.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out string? value) && !String.IsNullOrWhiteSpace(value) ? value : null;
    }

    // --- Discovery ---

    [Fact]
    public void DiscoverConfigFile_InWorkingDirectory_FindsIt()
    {
        string cwd = CreateDir("in-cwd");
        string configPath = CreateConfigFile(cwd, "{}");

        string? result = ProjectConfigSeeder.DiscoverConfigFile(cwd);

        result.ShouldBe(configPath);
    }

    [Fact]
    public void DiscoverConfigFile_InParentDirectory_FindsIt()
    {
        string parent = CreateDir("parent-walk");
        string configPath = CreateConfigFile(parent, "{}");
        string cwd = CreateDir("parent-walk", "src", "app");

        string? result = ProjectConfigSeeder.DiscoverConfigFile(cwd);

        result.ShouldBe(configPath);
    }

    [Fact]
    public void DiscoverConfigFile_InChildAndParent_ChildWinsAndStopsWalk()
    {
        string parent = CreateDir("child-beats-parent");
        CreateConfigFile(parent, "{ \"ROZ_TOOLS\": \"all\" }");
        string child = CreateDir("child-beats-parent", "nested");
        string childConfig = CreateConfigFile(child, "not even json");

        string? result = ProjectConfigSeeder.DiscoverConfigFile(child);

        // The first directory containing a .roz.json wins even when its file is unparseable —
        // that is what lets a nested project shield itself from a parent's config.
        result.ShouldBe(childConfig);
    }

    [Fact]
    public void DiscoverConfigFile_NoFileAnywhere_ReturnsNull()
    {
        // Arrange — a leaf directory with no .roz.json anywhere up the chain.
        string cwd = CreateDir("empty-root", "deep", "nested");

        // Guard: the walk continues to the drive root, so a stray .roz.json in ANY ancestor of the
        // temp dir would make discovery find one. Fail loudly on a polluted environment instead.
        for (DirectoryInfo? dir = new(cwd); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ProjectConfigSeeder.FileName);
            bool exists;
            try
            {
                exists = File.Exists(candidate);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            exists.ShouldBeFalse($"Stray {candidate} makes this test meaningless — clean it.");
        }

        // Act & Assert
        ProjectConfigSeeder.DiscoverConfigFile(cwd).ShouldBeNull();
    }

    // --- Compute: precedence ---

    [Fact]
    public void Compute_EnvVarUnset_AppliesKey()
    {
        var json = "{ \"ROZ_TOOLS\": \"read\" }";

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        result.Applied.ShouldBe([new AppliedSetting("ROZ_TOOLS", "read")]);
        result.OverriddenByEnv.ShouldBeEmpty();
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Compute_EnvVarSet_ReportsOverriddenByEnv()
    {
        var json = "{ \"ROZ_TOOLS\": \"read\" }";
        Func<string, string?> env = EnvWith(("ROZ_TOOLS", "all"));

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, env);

        result.Applied.ShouldBeEmpty();
        result.OverriddenByEnv.ShouldBe(["ROZ_TOOLS"]);
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Compute_EnvVarWhitespace_AppliesKey()
    {
        // Whitespace reads as unset through EnvParse.RawString, so the file value applies —
        // exactly the precedence every ROZ_* consumer uses.
        var json = "{ \"ROZ_LOG_LEVEL\": \"Information\" }";
        Func<string, string?> env = EnvWith(("ROZ_LOG_LEVEL", "   "));

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, env);

        result.Applied.ShouldBe([new AppliedSetting("ROZ_LOG_LEVEL", "Information")]);
        result.OverriddenByEnv.ShouldBeEmpty();
    }

    // --- Compute: key filtering ---

    [Theory]
    [InlineData("PATH")]
    [InlineData("DOTNET_ROOT")]
    [InlineData("MAX_MCP_OUTPUT_TOKENS")]
    [InlineData("CLAUDE_CODE_SESSION_ID")]
    public void Compute_NonRozKey_WarnsAndSkips(string key)
    {
        // MAX_MCP_OUTPUT_TOKENS and CLAUDE_CODE_SESSION_ID are registry entries but client-owned:
        // the ROZ_ prefix rule excludes them by construction, same as any generic env var.
        var json = $"{{ \"{key}\": \"anything\" }}";

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        result.Applied.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldContain(key);
    }

    [Fact]
    public void Compute_UnknownRozKey_WarnsAndSkips()
    {
        var json = "{ \"ROZ_TOOL\": \"read\" }";

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        result.Applied.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldContain("ROZ_TOOL");
    }

    [Fact]
    public void Compute_MixedKnownAndUnknownKeys_AppliesKnownAndWarnsUnknown()
    {
        var json = "{ \"ROZ_TOOLS\": \"read\", \"PATH\": \"C:\\\\evil\" }";

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        result.Applied.ShouldBe([new AppliedSetting("ROZ_TOOLS", "read")]);
        result.Warnings.ShouldHaveSingleItem().ShouldContain("PATH");
    }

    // --- Compute: value coercion ---

    [Fact]
    public void Compute_NumberValue_CoercesToInvariantString()
    {
        var json = "{ \"ROZ_IDLE_TIMEOUT_MINUTES\": 45 }";

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        result.Applied.ShouldBe([new AppliedSetting("ROZ_IDLE_TIMEOUT_MINUTES", "45")]);
    }

    [Fact]
    public void Compute_TrueValue_CoercesToLowercaseTrue()
    {
        // "true" (not "True") so the coerced value matches EnvParse.BoolTrue.
        var json = "{ \"ROZ_DISABLE_ANALYZERS\": true }";

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        result.Applied.ShouldBe([new AppliedSetting("ROZ_DISABLE_ANALYZERS", "true")]);
    }

    [Theory]
    [InlineData("{ \"ROZ_TOOLS\": {} }", "Object")]
    [InlineData("{ \"ROZ_TOOLS\": [\"read\"] }", "Array")]
    [InlineData("{ \"ROZ_TOOLS\": null }", "Null")]
    public void Compute_StructuredOrNullValue_WarnsAndSkips(string json, string expectedKindInWarning)
    {
        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        result.Applied.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldContain(expectedKindInWarning);
    }

    [Fact]
    public void Compute_BlankStringValue_WarnsAndSkips()
    {
        var json = "{ \"ROZ_TOOLS\": \"   \" }";

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        result.Applied.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldContain("blank");
    }

    // --- Compute: ROZ_SOLUTION_PATH resolution ---

    [Fact]
    public void Compute_RelativeSolutionPath_ResolvesAgainstConfigDirectory()
    {
        var json = "{ \"ROZ_SOLUTION_PATH\": \"src/My.sln\" }";

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        string expected = Path.GetFullPath(Path.Combine(tempRoot, "src", "My.sln"));
        result.Applied.ShouldBe([new AppliedSetting("ROZ_SOLUTION_PATH", expected)]);
    }

    [Fact]
    public void Compute_AbsoluteSolutionPath_PassesThroughUnchanged()
    {
        string absolute = Path.Combine(tempRoot, "Elsewhere.sln");
        var json = $"{{ \"ROZ_SOLUTION_PATH\": {JsonSerializer.Serialize(absolute)} }}";

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        result.Applied.ShouldBe([new AppliedSetting("ROZ_SOLUTION_PATH", absolute)]);
    }

    [Fact]
    public void Compute_SolutionPathUnresolvable_WarnsAndSkips()
    {
        // An embedded NUL is the one character Path.GetFullPath always rejects; JSON carries it
        // as an escaped control character, so the file parses fine and only path resolution fails.
        var json = $"{{ \"ROZ_SOLUTION_PATH\": {JsonSerializer.Serialize("src" + '\0' + "bad.sln")} }}";

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        result.Applied.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldContain("could not be resolved");
    }

    [Fact]
    public void Compute_ConfigPathWithoutDirectory_SolutionPathPassesThrough()
    {
        // A bare file name has no directory to resolve against; production never produces one
        // (discovery returns rooted paths), so the value passes through untouched.
        var json = "{ \"ROZ_SOLUTION_PATH\": \"src/My.sln\" }";

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ProjectConfigSeeder.FileName, json, EmptyEnv);

        result.Applied.ShouldBe([new AppliedSetting("ROZ_SOLUTION_PATH", "src/My.sln")]);
    }

    // --- Compute: parse behavior ---

    [Fact]
    public void Compute_UnparseableJson_WarnsAndAppliesNothing()
    {
        var json = "this is not json";

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        result.Applied.ShouldBeEmpty();
        result.OverriddenByEnv.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldContain("not valid JSON");
    }

    [Fact]
    public void Compute_DuplicateKeys_DoesNotThrow_TreatedAsUnparseable()
    {
        var json = "{ \"ROZ_TOOLS\": \"read\", \"ROZ_TOOLS\": \"all\" }";

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        result.Applied.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldContain("not valid JSON");
    }

    [Fact]
    public void Compute_NonObjectRoot_WarnsAndAppliesNothing()
    {
        var json = "[\"ROZ_TOOLS\"]";

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        result.Applied.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldContain("JSON object");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void Compute_WhitespaceFile_EmptyResultWithoutWarning(string json)
    {
        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        result.ConfigFilePath.ShouldBe(ConfigPath);
        result.Applied.ShouldBeEmpty();
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Compute_CommentsAndTrailingCommas_Parse()
    {
        const string json = """
                            {
                                // Tool surface for this repo
                                "ROZ_TOOLS": "default,edit_symbol",
                                /* block comment */
                                "ROZ_LOG_LEVEL": "Information",
                            }
                            """;

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        result.Applied.ShouldBe([
            new AppliedSetting("ROZ_TOOLS", "default,edit_symbol"),
            new AppliedSetting("ROZ_LOG_LEVEL", "Information")
        ]);
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Compute_LeadingBom_StrippedAndParses()
    {
        string json = (char)0xFEFF + "{ \"ROZ_TOOLS\": \"read\" }";

        ProjectConfigSeedResult result = ProjectConfigSeeder.Compute(ConfigPath, json, EmptyEnv);

        result.Applied.ShouldBe([new AppliedSetting("ROZ_TOOLS", "read")]);
        result.Warnings.ShouldBeEmpty();
    }

    // --- Allowlist drift guard ---

    [Fact]
    public void SeedableNames_DeriveFromRegistry()
    {
        // The allowlist must track RozEnvVars.All programmatically: every ROZ_-prefixed registry
        // name is seedable, and the client-owned names are excluded by construction. A future
        // registry entry that fails this test means the derivation broke, not that a list is stale.
        HashSet<string> expected = RozEnvVars.All
            .Select(v => v.Name)
            .Where(n => n.StartsWith("ROZ_", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        // ReSharper disable once ArgumentsStyleLiteral — keep `ignoreOrder:` self-documenting.
        ProjectConfigSeeder.SeedableNames.ShouldBe(expected, ignoreOrder: true);
        ProjectConfigSeeder.SeedableNames.ShouldNotContain(RozEnvVars.MaxMcpOutputTokens.Name);
        ProjectConfigSeeder.SeedableNames.ShouldNotContain(RozEnvVars.ClaudeSessionId.Name);
        ProjectConfigSeeder.SeedableNames.ShouldContain(RozEnvVars.SessionId.Name);
        ProjectConfigSeeder.SeedableNames.ShouldContain(RozEnvVars.VsInstallPath.Name);
    }

    // --- Summary (the canonical outcome line shared by workspace info, setup, and the startup log) ---

    [Fact]
    public void Summary_AppliedOverriddenAndWarnings_JoinsAllParts()
    {
        // Arrange
        ProjectConfigSeedResult result = new(
            "C:\\x\\.roz.json",
            [new AppliedSetting("ROZ_TOOLS", "read")],
            ["ROZ_LOG_LEVEL"],
            ["Unknown key 'PATH' skipped — only ROZ_-prefixed variables from the registry are honored."]);

        // Act + Assert
        result.Summary().ShouldBe(
            "applied: ROZ_TOOLS; overridden by env: ROZ_LOG_LEVEL; warnings: Unknown key 'PATH' skipped — only ROZ_-prefixed variables from the registry are honored.");
        result.IsIgnored.ShouldBeFalse();
    }

    [Fact]
    public void Summary_WithValues_ListsNameValuePairs()
    {
        // Arrange
        ProjectConfigSeedResult result = new(
            "C:\\x\\.roz.json",
            [new AppliedSetting("ROZ_TOOLS", "read"), new AppliedSetting("ROZ_LOG_LEVEL", "Debug")],
            [], []);

        // Act + Assert
        // ReSharper disable once ArgumentsStyleLiteral — keep `withValues:` self-documenting.
        result.Summary(withValues: true).ShouldBe("applied: ROZ_TOOLS=read, ROZ_LOG_LEVEL=Debug");
    }

    [Fact]
    public void Summary_IgnoredFile_RendersIgnoredWithWarnings()
    {
        // Arrange — found but contributed nothing except warnings: must not read as an
        // environment-precedence outcome.
        ProjectConfigSeedResult result = new(
            "C:\\x\\.roz.json", [], [], [".roz.json is not valid JSON and was ignored: bad token"]);

        // Act + Assert
        result.IsIgnored.ShouldBeTrue();
        result.Summary().ShouldBe("ignored: .roz.json is not valid JSON and was ignored: bad token");
    }

    [Fact]
    public void Summary_EmptyOptOutFile_AppliedNoneOnly()
    {
        // Arrange — a `{}` opt-out: found, nothing applied, nothing overridden, no warnings.
        ProjectConfigSeedResult result = new("C:\\x\\.roz.json", [], [], []);

        // Act + Assert
        result.IsIgnored.ShouldBeFalse();
        result.Summary().ShouldBe("applied: none");
    }

    [Fact]
    public void Summary_WithoutWarnings_OmitsWarningTexts()
    {
        // Arrange — the startup log's flavor: warnings ride separate Warning-level lines, so the
        // summary must omit them in both the normal and the ignored state.
        ProjectConfigSeedResult applied = new(
            "C:\\x\\.roz.json", [new AppliedSetting("ROZ_TOOLS", "read")], [], ["Unknown key 'PATH' skipped."]);
        ProjectConfigSeedResult ignored = new(
            "C:\\x\\.roz.json", [], [], [".roz.json is not valid JSON and was ignored: bad token"]);

        // Act + Assert
        applied.Summary(includeWarnings: false).ShouldBe("applied: ROZ_TOOLS");
        ignored.Summary(includeWarnings: false).ShouldBe("ignored");
    }
}

[CollectionDefinition("ProjectConfigSeeder", DisableParallelization = true)]
public class ProjectConfigSeederCollection;

/// <summary>
///     The two tests that exercise the real <see cref="ProjectConfigSeeder.Seed" /> path, which
///     mutates process env vars and the <see cref="ProjectConfigSeeder.Current" /> static. Serialized
///     against all other collections and restoring both in <see cref="Dispose" />, mirroring
///     <see cref="Zphil.Roz.Tests.Utility.SolutionDiscoveryEnvVarTests" />.
/// </summary>
[Collection("ProjectConfigSeeder")]
public class ProjectConfigSeederEnvTests : IDisposable
{
    private readonly string? originalLogLevel;
    private readonly string? originalTestNamespaces;
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "roz-config-seeder-env-tests", Guid.NewGuid().ToString("N"));

    public ProjectConfigSeederEnvTests()
    {
        Directory.CreateDirectory(tempRoot);
        originalTestNamespaces = Environment.GetEnvironmentVariable(RozEnvVars.TestNamespaces.Name);
        originalLogLevel = Environment.GetEnvironmentVariable(RozEnvVars.LogLevel.Name);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RozEnvVars.TestNamespaces.Name, originalTestNamespaces);
        Environment.SetEnvironmentVariable(RozEnvVars.LogLevel.Name, originalLogLevel);
        ProjectConfigSeeder.ResetForTests();

        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Seed_SetsOnlyUnsetVars_AndStoresCurrent()
    {
        // Arrange — one var pre-set in the environment (must win), one unset (file fills it).
        Environment.SetEnvironmentVariable(RozEnvVars.LogLevel.Name, "Error");
        Environment.SetEnvironmentVariable(RozEnvVars.TestNamespaces.Name, null);
        File.WriteAllText(
            Path.Combine(tempRoot, ProjectConfigSeeder.FileName),
            "{ \"ROZ_LOG_LEVEL\": \"Debug\", \"ROZ_TEST_NAMESPACES\": \"My.Tests\" }");

        // Act
        ProjectConfigSeedResult result = ProjectConfigSeeder.Seed(tempRoot);

        // Assert — environment reflects the precedence, and Current carries the same result.
        Environment.GetEnvironmentVariable(RozEnvVars.LogLevel.Name).ShouldBe("Error");
        Environment.GetEnvironmentVariable(RozEnvVars.TestNamespaces.Name).ShouldBe("My.Tests");
        result.Applied.ShouldBe([new AppliedSetting(RozEnvVars.TestNamespaces.Name, "My.Tests")]);
        result.OverriddenByEnv.ShouldBe([RozEnvVars.LogLevel.Name]);
        ProjectConfigSeeder.Current.ShouldBe(result);
    }

    [Fact]
    public void Seed_UnexpectedFailure_NeverThrows_ReturnsWarningResult()
    {
        // Arrange — a working directory Path.GetFullPath rejects (embedded NUL), forcing the
        // discovery step to throw inside Seed. The whole-body catch-all must degrade it to a
        // warning: seeding an optional file may never block startup.
        string invalidWorkingDirectory = "bad" + '\0' + "dir";

        // Act
        ProjectConfigSeedResult result = ProjectConfigSeeder.Seed(invalidWorkingDirectory);

        // Assert
        result.ConfigFilePath.ShouldBeNull();
        result.Applied.ShouldBeEmpty();
        result.Warnings.ShouldHaveSingleItem().ShouldContain("seeding failed");
        ProjectConfigSeeder.Current.ShouldBe(result);
    }

    [Fact]
    public void Seed_NoConfigFile_ReturnsEmptyResult_NoEnvChanges()
    {
        // Arrange — a leaf dir with no .roz.json up the chain (guarded like the discovery test).
        string cwd = Path.Combine(tempRoot, "no-config");
        Directory.CreateDirectory(cwd);
        for (DirectoryInfo? dir = new(cwd); dir is not null; dir = dir.Parent)
        {
            File.Exists(Path.Combine(dir.FullName, ProjectConfigSeeder.FileName))
                .ShouldBeFalse($"Stray {ProjectConfigSeeder.FileName} under '{dir.FullName}' makes this test meaningless — clean it.");
        }

        Environment.SetEnvironmentVariable(RozEnvVars.TestNamespaces.Name, null);

        // Act
        ProjectConfigSeedResult result = ProjectConfigSeeder.Seed(cwd);

        // Assert
        result.ConfigFilePath.ShouldBeNull();
        result.Applied.ShouldBeEmpty();
        result.OverriddenByEnv.ShouldBeEmpty();
        result.Warnings.ShouldBeEmpty();
        Environment.GetEnvironmentVariable(RozEnvVars.TestNamespaces.Name).ShouldBeNull();
        ProjectConfigSeeder.Current.ShouldBe(result);
    }
}
