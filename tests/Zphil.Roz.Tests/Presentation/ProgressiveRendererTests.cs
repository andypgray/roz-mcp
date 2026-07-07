using Zphil.Roz.Enums;
using Zphil.Roz.Presentation;

namespace Zphil.Roz.Tests.Presentation;

public class ProgressiveRendererTests
{
    [Fact]
    public void Render_FitsAtFull_NoReductionNote()
    {
        // Arrange
        var output = new string('x', 50);

        // Act
        string result = ProgressiveRenderer.Render("input",
            (_, _) => output, 100);

        // Assert
        result.ShouldBe(output);
        result.ShouldNotContain("DETAIL REDUCED");
    }

    [Fact]
    public void Render_FitsAtDropBodies_AppendsReductionNote()
    {
        // Arrange — Full is too large, DropBodies fits
        List<DetailLevel> callLog = new();

        // Act
        string result = ProgressiveRenderer.Render("input", (_, level) =>
        {
            callLog.Add(level);
            return level == DetailLevel.Full
                ? new string('x', 200)
                : new string('y', 50);
        }, 100);

        // Assert
        result.ShouldContain("--- DETAIL REDUCED ---");
        result.ShouldContain("High");
        result.ShouldContain("Source bodies removed");
        callLog.ShouldContain(DetailLevel.Full);
        callLog.ShouldContain(DetailLevel.High);
    }

    [Fact]
    public void Render_FitsAtSignaturesOnly_AppendsCorrectNote()
    {
        // Arrange — Full, DropBodies, DropDocs all too large, SignaturesOnly fits
        string result = ProgressiveRenderer.Render("input", (_, level) =>
                level < DetailLevel.Low
                    ? new string('x', 200)
                    : new string('y', 50),
            100);

        // Assert
        result.ShouldContain("Low");
        result.ShouldContain("Only signatures and locations shown");
    }

    [Fact]
    public void Render_AllLevelsExceed_ReturnsNamesOnlyForFailsafe()
    {
        // Arrange — every level exceeds the limit
        string result = ProgressiveRenderer.Render("input",
            (_, _) => new string('x', 200), 100);

        // Assert — reduction note is appended but output still exceeds limit
        // (the hard truncation failsafe in ResponseTruncator handles the rest)
        result.ShouldContain("--- DETAIL REDUCED ---");
        result.ShouldContain("Minimal");
    }

    [Fact]
    public void Render_SkipsIdenticalLevels_ReportsCorrectLevel()
    {
        // Arrange — Full and DropBodies produce same output (no bodies requested),
        // DropDocs also same, SignaturesOnly is smaller and fits
        var largeOutput = new string('x', 200);
        var smallOutput = new string('y', 50);

        string result = ProgressiveRenderer.Render("input", (_, level) =>
                level >= DetailLevel.Low ? smallOutput : largeOutput,
            100);

        // Assert — should report SignaturesOnly, not DropBodies or DropDocs
        result.ShouldContain("Low");
        result.ShouldNotContain("High");
        result.ShouldNotContain("Medium");
    }

    [Fact]
    public void Render_TriesLevelsInOrder()
    {
        // Arrange
        List<DetailLevel> callLog = new();

        // Act — all levels too large, so all get tried
        ProgressiveRenderer.Render("input", (_, level) =>
        {
            callLog.Add(level);
            return new string('x', 200);
        }, 100);

        // Assert
        callLog.ShouldBe(new[]
        {
            DetailLevel.Full,
            DetailLevel.High,
            DetailLevel.Medium,
            DetailLevel.Low,
            DetailLevel.Minimal
        });
    }

    [Fact]
    public void Render_ExactlyAtLimit_NoReductionNote()
    {
        // Arrange — output is exactly at the limit
        var output = new string('x', 100);

        // Act
        string result = ProgressiveRenderer.Render("input",
            (_, _) => output, 100);

        // Assert
        result.ShouldBe(output);
        result.ShouldNotContain("DETAIL REDUCED");
    }

    [Fact]
    public void Render_ReductionNoteIncludesCharLimit()
    {
        // Arrange
        string result = ProgressiveRenderer.Render("input", (_, level) =>
                level == DetailLevel.Full
                    ? new string('x', 200)
                    : new string('y', 50),
            100);

        // Assert
        result.ShouldContain("100 character limit");
    }

    [Fact]
    public void Render_EqualLengthDistinctLevels_ReturnsLowestLevelNotStaleEarlierLevel()
    {
        // Arrange — every level produces distinct 20-char content; none fits the 10-char
        // limit. Lengths are identical across levels, so using output length as a
        // content-equality proxy wrongly treats each level as a duplicate of the previous
        // and skips it. The failsafe then returns a *stale earlier* level's content while
        // the note labels it Minimal (the last level tried) — a content/label mismatch.
        string result = ProgressiveRenderer.Render("input", (_, level) => level switch
        {
            DetailLevel.Full => new string('F', 20),
            DetailLevel.High => new string('H', 20),
            DetailLevel.Medium => new string('M', 20),
            DetailLevel.Low => new string('L', 20),
            _ => new string('N', 20)
        }, 10);

        // Assert — the returned content must be the Minimal level (matching the note's label),
        // not the Full level left stale by a length-collision skip.
        result.ShouldContain("Minimal");
        result.ShouldContain(new string('N', 20));
        result.ShouldNotContain(new string('F', 20));
    }
}
