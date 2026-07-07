using Zphil.Roz.Models;
using Zphil.Roz.Presentation;

namespace Zphil.Roz.Tests.Presentation;

public class FormatBatchWithErrorsTests
{
    [Fact]
    public void SingleSuccess_RendersWithoutHeader()
    {
        // Arrange — single success mirrors existing FormatBatch behavior (no "=== header ===")
        IReadOnlyList<BatchItem<string>> items = [new BatchItemSuccess<string>("Circle", "Found Circle")];

        // Act
        string result = FormattingHelpers.FormatBatchWithErrors(items, _ => "Search: Circle", value => value);

        // Assert
        result.ShouldBe("Found Circle");
        result.ShouldNotContain("===");
    }

    [Fact]
    public void SingleError_RendersAsOneLineErrorMessage()
    {
        // Arrange — single error case
        IReadOnlyList<BatchItem<string>> items = [new BatchItemError<string>("Missing", "No symbol found.")];

        // Act
        string result = FormattingHelpers.FormatBatchWithErrors(items, _ => "never called", _ => "never called");

        // Assert
        result.ShouldBe("Error looking up 'Missing': No symbol found.");
    }

    [Fact]
    public void MixedBatch_PreservesInputOrder_AndMarksErrorBlocks()
    {
        // Arrange — order: success, error, success
        IReadOnlyList<BatchItem<string>> items =
        [
            new BatchItemSuccess<string>("Circle", "Circle body"),
            new BatchItemError<string>("Missing", "No symbol found."),
            new BatchItemSuccess<string>("Shape", "Shape body")
        ];

        // Act
        string result = FormattingHelpers.FormatBatchWithErrors(items, r => $"Search: \"{r}\"", value => value);

        // Assert — order preserved; error block marked
        int circleIdx = result.IndexOf("=== Search: \"Circle body\" ===", StringComparison.Ordinal);
        int errorIdx = result.IndexOf("=== Error: Missing ===", StringComparison.Ordinal);
        int shapeIdx = result.IndexOf("=== Search: \"Shape body\" ===", StringComparison.Ordinal);
        circleIdx.ShouldBeGreaterThanOrEqualTo(0);
        errorIdx.ShouldBeGreaterThan(circleIdx);
        shapeIdx.ShouldBeGreaterThan(errorIdx);
        result.ShouldContain("No symbol found.");
    }

    [Fact]
    public void AllErrorBatch_DoesNotThrow_RendersEveryErrorBlock()
    {
        // Arrange — two errors, no successes
        IReadOnlyList<BatchItem<string>> items =
        [
            new BatchItemError<string>("X", "first error"),
            new BatchItemError<string>("Y", "second error")
        ];

        // Act — must not throw; returns a normal string
        string result = FormattingHelpers.FormatBatchWithErrors(items, _ => "never called", _ => "never called");

        // Assert
        result.ShouldContain("=== Error: X ===");
        result.ShouldContain("first error");
        result.ShouldContain("=== Error: Y ===");
        result.ShouldContain("second error");
    }
}
