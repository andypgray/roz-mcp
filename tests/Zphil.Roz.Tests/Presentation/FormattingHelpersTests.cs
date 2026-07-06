using Zphil.Roz.Presentation;

namespace Zphil.Roz.Tests.Presentation;

public class FormattingHelpersTests
{
    [Fact]
    public void FormatTruncationHint_WithIncludedTests_ShowsSourceTestSplit()
    {
        // Arrange + Act — mixed source and test results
        string hint = FormattingHelpers.FormatTruncationHint(8, "increase maxResults", 3);

        // Assert
        hint.ShouldBe("(8 total: 5 source, 3 tests — increase maxResults)");
    }

    [Fact]
    public void FormatTruncationHint_WithoutIncludedTests_ShowsTotalOnly()
    {
        // Arrange + Act — no test results (or filter applied upstream)
        string hint = FormattingHelpers.FormatTruncationHint(8, "increase maxResults");

        // Assert
        hint.ShouldBe("(8 total — increase maxResults)");
    }

    [Fact]
    public void FormatTruncationHint_AllTests_ShowsZeroSource()
    {
        // Arrange + Act — every result lives in a test project
        string hint = FormattingHelpers.FormatTruncationHint(8, "increase maxResults", 8);

        // Assert
        hint.ShouldBe("(8 total: 0 source, 8 tests — increase maxResults)");
    }
}
