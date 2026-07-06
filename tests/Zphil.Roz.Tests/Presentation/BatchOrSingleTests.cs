using Zphil.Roz.Models;
using Zphil.Roz.Presentation;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Tests.Presentation;

public class BatchOrSingleTests
{
    [Fact]
    public async Task RunAllAsync_RawLocations_HappyPath_ReturnsTwoSuccesses()
    {
        // Arrange — two valid cursors (path:line:col)
        string[] rawLocations = ["src/Foo.cs:10:5", "src/Bar.cs:20:8"];

        // Act — perLocation echoes back the parsed location
        BatchItem<string>[] items = await BatchOrSingle.RunAllAsync(
            rawLocations, loc => Task.FromResult($"{loc.FilePath}@{loc.Line}:{(loc as CursorLocation)?.Column}"), "test");

        // Assert — both items succeed; BatchItem.Name keys on the original raw string
        items.Length.ShouldBe(2);
        items[0].ShouldBeOfType<BatchItemSuccess<string>>();
        ((BatchItemSuccess<string>)items[0]).Name.ShouldBe("src/Foo.cs:10:5");
        ((BatchItemSuccess<string>)items[0]).Value.ShouldBe("src/Foo.cs@10:5");
        items[1].ShouldBeOfType<BatchItemSuccess<string>>();
        ((BatchItemSuccess<string>)items[1]).Value.ShouldBe("src/Bar.cs@20:8");
    }

    [Fact]
    public async Task RunAllAsync_RawLocations_PathOnlyItem_BecomesInlineError()
    {
        // Arrange — one valid cursor + one path-only string (rejected by ParsePosition)
        string[] rawLocations = ["src/Foo.cs:10:5", "src/Bar.cs"];

        // Act
        BatchItem<string>[] items = await BatchOrSingle.RunAllAsync(
            rawLocations, loc => Task.FromResult($"OK:{loc.FilePath}"), "test");

        // Assert — first is success, second is captured error keyed on raw string
        items.Length.ShouldBe(2);
        items[0].ShouldBeOfType<BatchItemSuccess<string>>();
        items[1].ShouldBeOfType<BatchItemError<string>>();
        BatchItemError<string> err = (BatchItemError<string>)items[1];
        err.Name.ShouldBe("src/Bar.cs");
        err.Error.ShouldContain(":line");
    }

    [Fact]
    public async Task RunAllAsync_RawLocations_UserErrorFromHandler_BecomesInlineError()
    {
        // Arrange — handler throws UserErrorException for a specific path
        string[] rawLocations = ["src/Good.cs:1:1", "src/Bad.cs:1:1"];

        // Act
        BatchItem<string>[] items = await BatchOrSingle.RunAllAsync(rawLocations, loc =>
        {
            if (loc.FilePath.Contains("Bad", StringComparison.Ordinal))
            {
                throw new UserErrorException("Symbol not found at this position.");
            }

            return Task.FromResult("ok");
        }, "test");

        // Assert
        items[0].ShouldBeOfType<BatchItemSuccess<string>>();
        items[1].ShouldBeOfType<BatchItemError<string>>();
        ((BatchItemError<string>)items[1]).Error.ShouldContain("Symbol not found");
    }

    [Fact]
    public async Task RunAllAsync_RawLocations_UnexpectedException_PropagatesOutOfBatch()
    {
        // Arrange — non-UserErrorException must NOT be captured (preserves crash signal)
        string[] rawLocations = ["src/Foo.cs:1:1"];

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() =>
            BatchOrSingle.RunAllAsync<string>(rawLocations, _ => throw new InvalidOperationException("bug"), "test"));
    }
}
