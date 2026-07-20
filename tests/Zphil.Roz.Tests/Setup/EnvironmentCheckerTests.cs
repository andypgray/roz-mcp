using Zphil.Roz.Infrastructure;
using Zphil.Roz.Setup;

namespace Zphil.Roz.Tests.Setup;

/// <summary>
///     Tests for the pure <see cref="EnvironmentChecker.CheckProjectConfig" /> check. The other
///     checks (SDK, MSBuild, solution file) shell out or probe the machine, so they stay covered by
///     running <c>roz-mcp setup</c> itself; the project-config check is a pure formatting decision
///     over a <see cref="ProjectConfigSeedResult" /> and is pinned here.
/// </summary>
public class EnvironmentCheckerTests
{
    private const string WorkingDirectory = "C:\\repo\\src";
    private const string ConfigPath = "C:\\repo\\.roz.json";

    [Fact]
    public void CheckProjectConfig_NoFile_PassesWithOptionalNote()
    {
        // Arrange
        ProjectConfigSeedResult seed = new(null, [], [], []);

        // Act
        EnvironmentChecker.CheckResult result = EnvironmentChecker.CheckProjectConfig(WorkingDirectory, seed);

        // Assert
        result.Passed.ShouldBeTrue();
        result.Detail.ShouldContain("none found");
        result.Detail.ShouldContain("optional");
    }

    [Fact]
    public void CheckProjectConfig_SeedingFailed_PassesAndSurfacesWarning()
    {
        // Arrange — the seeder's whole-body catch-all yields a null path plus one warning.
        ProjectConfigSeedResult seed = new(null, [], [], [".roz.json seeding failed and was skipped: boom"]);

        // Act
        EnvironmentChecker.CheckResult result = EnvironmentChecker.CheckProjectConfig(WorkingDirectory, seed);

        // Assert
        result.Passed.ShouldBeTrue();
        result.Detail.ShouldContain("seeding failed");
    }

    [Fact]
    public void CheckProjectConfig_AppliedAndOverridden_PassesListingBoth()
    {
        // Arrange
        ProjectConfigSeedResult seed = new(
            ConfigPath,
            [new AppliedSetting("ROZ_TOOLS", "read"), new AppliedSetting("ROZ_LOG_LEVEL", "Debug")],
            ["ROZ_SESSION_ID"],
            []);

        // Act
        EnvironmentChecker.CheckResult result = EnvironmentChecker.CheckProjectConfig(WorkingDirectory, seed);

        // Assert — path is rendered relative to the working directory, like the solution check.
        result.Passed.ShouldBeTrue();
        result.Detail.ShouldContain("Found: ..\\.roz.json");
        result.Detail.ShouldContain("applied: ROZ_TOOLS, ROZ_LOG_LEVEL");
        result.Detail.ShouldContain("overridden by env: ROZ_SESSION_ID");
    }

    [Fact]
    public void CheckProjectConfig_AppliedWithWarnings_PassesListingWarnings()
    {
        // Arrange
        ProjectConfigSeedResult seed = new(
            ConfigPath,
            [new AppliedSetting("ROZ_TOOLS", "read")],
            [],
            ["Unknown key 'PATH' skipped — only ROZ_-prefixed variables from the registry are honored."]);

        // Act
        EnvironmentChecker.CheckResult result = EnvironmentChecker.CheckProjectConfig(WorkingDirectory, seed);

        // Assert
        result.Passed.ShouldBeTrue();
        result.Detail.ShouldContain("applied: ROZ_TOOLS");
        result.Detail.ShouldContain("warnings: Unknown key 'PATH'");
    }

    [Fact]
    public void CheckProjectConfig_FileEntirelyIgnored_PassesWithIgnoredNote()
    {
        // Arrange — found a file, applied nothing, warned (the unparseable case).
        ProjectConfigSeedResult seed = new(
            ConfigPath, [], [], [".roz.json is not valid JSON and was ignored: bad token"]);

        // Act
        EnvironmentChecker.CheckResult result = EnvironmentChecker.CheckProjectConfig(WorkingDirectory, seed);

        // Assert
        result.Passed.ShouldBeTrue();
        result.Detail.ShouldContain("Found ..\\.roz.json but ignored");
        result.Detail.ShouldContain("not valid JSON");
    }

    [Fact]
    public void CheckProjectConfig_EmptyConfigFile_PassesWithAppliedNone()
    {
        // Arrange — a `{}` opt-out file: found, nothing applied, nothing overridden, no warnings.
        ProjectConfigSeedResult seed = new(ConfigPath, [], [], []);

        // Act
        EnvironmentChecker.CheckResult result = EnvironmentChecker.CheckProjectConfig(WorkingDirectory, seed);

        // Assert
        result.Passed.ShouldBeTrue();
        result.Detail.ShouldContain("applied: none");
    }
}
