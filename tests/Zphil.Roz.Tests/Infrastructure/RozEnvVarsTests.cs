using System.Reflection;
using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Tests.Infrastructure;

/// <summary>
///     Reflection-based drift guards for <see cref="RozEnvVars" />. None of these tests mutate
///     env-var state, so the class is safe to run in parallel with the rest of the suite.
/// </summary>
public sealed class RozEnvVarsTests
{
    /// <summary>Env-var names allowed not to start with <c>ROZ_</c>.</summary>
    private static readonly HashSet<string> NonRoslynPrefixAllowlist =
        new(StringComparer.Ordinal) { "MAX_MCP_OUTPUT_TOKENS", "CLAUDE_CODE_SESSION_ID" };

    [Fact]
    public void All_ContainsEveryDeclaredEnvVarClass()
    {
        // Arrange
        IReadOnlyList<string> namesFromNestedClasses = GetNamesFromNestedClasses();
        HashSet<string> namesInAll = RozEnvVars.All.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);

        // Assert — every nested env-var class must have a matching All entry.
        foreach (string name in namesFromNestedClasses)
        {
            namesInAll.ShouldContain(name,
                $"RozEnvVars.All is missing an entry for the {name} nested class.");
        }

        // And vice versa — no orphan entries in All for vars that have lost their nested class.
        RozEnvVars.All.Count.ShouldBe(namesFromNestedClasses.Count,
            "RozEnvVars.All has entries with no matching nested class (or vice versa).");
    }

    [Fact]
    public void Names_AreUnique()
    {
        // Arrange
        IReadOnlyList<string> names = GetNamesFromNestedClasses();

        // Act
        IEnumerable<IGrouping<string, string>> duplicates = names
            .GroupBy(n => n, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);

        // Assert
        duplicates.ShouldBeEmpty();
    }

    [Fact]
    public void Names_RoslynPrefix_OrOnAllowlist()
    {
        // Arrange
        IReadOnlyList<string> names = GetNamesFromNestedClasses();

        // Act / Assert
        foreach (string name in names)
        {
            if (NonRoslynPrefixAllowlist.Contains(name))
            {
                continue;
            }

            name.ShouldStartWith("ROZ_",
                customMessage: $"Env var '{name}' must start with ROZ_ or be added to the explicit allowlist (currently: {String.Join(", ", NonRoslynPrefixAllowlist)}).");
        }
    }

    [Fact]
    public void EnvVarInfo_CurrentValue_ReadsLiveEnvironment()
    {
        // Arrange
        var info = new EnvVarInfo("ROZ_ENVVARINFO_TEST", "test default");

        try
        {
            // Act + Assert — unset reads null.
            Environment.SetEnvironmentVariable(info.Name, null);
            info.CurrentValue.ShouldBeNull();

            // Act + Assert — set reads the live value.
            Environment.SetEnvironmentVariable(info.Name, "hello");
            info.CurrentValue.ShouldBe("hello");
        }
        finally
        {
            Environment.SetEnvironmentVariable(info.Name, null);
        }
    }

    private static IReadOnlyList<string> GetNamesFromNestedClasses()
    {
        List<string> names = [];
        foreach (Type nested in typeof(RozEnvVars).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
        {
            FieldInfo? nameField = nested.GetField("Name", BindingFlags.Public | BindingFlags.Static);
            if (nameField is null || nameField.FieldType != typeof(string))
            {
                continue;
            }

            var value = (string?)nameField.GetRawConstantValue();
            value.ShouldNotBeNull($"Nested class {nested.Name} must expose a non-null const string Name.");
            names.Add(value);
        }

        return names;
    }
}
