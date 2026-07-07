using Zphil.Roz.Pipeline;

namespace Zphil.Roz.Tests.Pipeline;

/// <summary>
///     Unit tests for <see cref="ResponseTruncator.TruncateIfNeeded" />: line-boundary truncation,
///     the char-count footer, and the per-tool narrowing hint.
/// </summary>
public class ResponseTruncatorTests
{
    [Fact]
    public void TruncateIfNeeded_ShortText_ReturnsUnchanged()
    {
        // Arrange
        var text = "Hello, world!";

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, "find_symbol", 100);

        // Assert
        result.ShouldBe(text);
    }

    [Fact]
    public void TruncateIfNeeded_ExactlyAtLimit_ReturnsUnchanged()
    {
        // Arrange
        var text = new string('x', 50);

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, "find_symbol", 50);

        // Assert
        result.ShouldBe(text);
    }

    [Fact]
    public void TruncateIfNeeded_OverLimit_TruncatesAtLineBoundary()
    {
        // Arrange — 3 lines of 10 chars each, with newlines at index 10 and index 21
        var text = "aaaaaaaaaa\nbbbbbbbbbb\ncccccccccc";

        // Act — limit 25; the last \n at/before index 25 is at index 21, so lines 1-2 survive
        string result = ResponseTruncator.TruncateIfNeeded(text, null, 25);

        // Assert
        result.ShouldStartWith("aaaaaaaaaa\nbbbbbbbbbb");
        result.ShouldNotContain("cccccccccc");
    }

    [Fact]
    public void TruncateIfNeeded_OverLimit_AppendsFooterWithCharCounts()
    {
        // Arrange
        string text = "line1\nline2\nline3\nline4\nline5\n" + new string('x', 100);

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, null, 30);

        // Assert
        result.ShouldContain("--- RESPONSE TRUNCATED ---");
        result.ShouldContain($"Output was {text.Length:N0} characters");
        result.ShouldContain("limit is 30");
        result.ShouldContain("The results above are incomplete.");
    }

    [Fact]
    public void TruncateIfNeeded_KnownTool_IncludesNarrowingHint()
    {
        // Arrange
        var text = new string('a', 200);

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, "find_symbol", 50);

        // Assert
        result.ShouldContain("containingType");
    }

    [Theory]
    [InlineData("some_unknown_tool")]
    [InlineData(null)]
    public void TruncateIfNeeded_UnknownOrNullTool_NoHint(string? toolName)
    {
        // Arrange
        var text = new string('a', 200);

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, toolName, 50);

        // Assert
        result.ShouldContain("--- RESPONSE TRUNCATED ---");
        result.ShouldEndWith("The results above are incomplete.");
    }

    [Fact]
    public void TruncateIfNeeded_NoNewlines_HardCutsAtLimit()
    {
        // Arrange
        var text = new string('x', 200);

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, null, 50);

        // Assert — exactly 50 x's, then the footer
        result.ShouldStartWith(new string('x', 50));
        result.ShouldContain("--- RESPONSE TRUNCATED ---");
    }
}
