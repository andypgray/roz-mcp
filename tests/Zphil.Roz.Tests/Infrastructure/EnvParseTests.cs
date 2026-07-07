using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Tests.Infrastructure;

[CollectionDefinition("EnvParseTests", DisableParallelization = true)]
public class EnvParseTestsCollection;

/// <summary>
///     Exercises the <see cref="EnvParse" /> helpers against a per-test scratch env var so the
///     production names in <see cref="RozEnvVars" /> are never mutated. The collection is
///     non-parallel because each test mutates process-wide state.
/// </summary>
[Collection("EnvParseTests")]
public sealed class EnvParseTests : IDisposable
{
    private const string TestVar = "ROZ_ENVPARSE_TEST_VAR";

    public EnvParseTests()
    {
        Environment.SetEnvironmentVariable(TestVar, null);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(TestVar, null);

    // ── BoolTrue ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("tRuE")]
    public void BoolTrue_TrueAnyCasing_ReturnsTrue(string value)
    {
        // Arrange
        Environment.SetEnvironmentVariable(TestVar, value);

        // Act / Assert
        EnvParse.BoolTrue(TestVar).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("anything-else")]
    public void BoolTrue_NotExactlyTrue_ReturnsFalse(string? value)
    {
        // Arrange
        Environment.SetEnvironmentVariable(TestVar, value);

        // Act / Assert
        EnvParse.BoolTrue(TestVar).ShouldBeFalse();
    }

    // ── RawString ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void RawString_UnsetOrBlank_ReturnsNull(string? value)
    {
        // Arrange
        Environment.SetEnvironmentVariable(TestVar, value);

        // Act / Assert
        EnvParse.RawString(TestVar).ShouldBeNull();
    }

    [Fact]
    public void RawString_NonBlankValue_ReturnsValueVerbatim()
    {
        // Arrange — preserve surrounding whitespace; only fully-blank input collapses to null.
        Environment.SetEnvironmentVariable(TestVar, "  /tmp/some path  ");

        // Act
        string? value = EnvParse.RawString(TestVar);

        // Assert
        value.ShouldBe("  /tmp/some path  ");
    }

    // ── DelimitedList ───────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DelimitedList_UnsetOrBlank_ReturnsEmpty(string? value)
    {
        // Arrange
        Environment.SetEnvironmentVariable(TestVar, value);

        // Act
        IReadOnlyList<string> list = EnvParse.DelimitedList(TestVar);

        // Assert
        list.ShouldBeEmpty();
    }

    [Fact]
    public void DelimitedList_Comma_SplitsAndTrims()
    {
        Environment.SetEnvironmentVariable(TestVar, "a, b , c");
        IReadOnlyList<string> list = EnvParse.DelimitedList(TestVar);
        list.ShouldBe(["a", "b", "c"]);
    }

    [Fact]
    public void DelimitedList_Semicolon_SplitsAndTrims()
    {
        Environment.SetEnvironmentVariable(TestVar, "a;b ;c");
        IReadOnlyList<string> list = EnvParse.DelimitedList(TestVar);
        list.ShouldBe(["a", "b", "c"]);
    }

    [Fact]
    public void DelimitedList_MixedDelimiters_BothAccepted()
    {
        Environment.SetEnvironmentVariable(TestVar, "a, b;c, d");
        IReadOnlyList<string> list = EnvParse.DelimitedList(TestVar);
        list.ShouldBe(["a", "b", "c", "d"]);
    }

    [Fact]
    public void DelimitedList_EmptyEntries_AreDropped()
    {
        Environment.SetEnvironmentVariable(TestVar, "a,,b;;c, ,d");
        IReadOnlyList<string> list = EnvParse.DelimitedList(TestVar);
        list.ShouldBe(["a", "b", "c", "d"]);
    }

    // ── PositiveInt ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("1.5")]
    [InlineData("0")]
    [InlineData("-3")]
    public void PositiveInt_UnsetBlankOrNonPositive_ReturnsNull(string? value)
    {
        // Arrange
        Environment.SetEnvironmentVariable(TestVar, value);

        // Act / Assert
        EnvParse.PositiveInt(TestVar).ShouldBeNull();
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("25000", 25000)]
    [InlineData("2147483647", Int32.MaxValue)]
    public void PositiveInt_PositiveValue_ReturnsParsed(string value, int expected)
    {
        // Arrange
        Environment.SetEnvironmentVariable(TestVar, value);

        // Act / Assert
        EnvParse.PositiveInt(TestVar).ShouldBe(expected);
    }
}
