using Zphil.Roz.Models;
using Zphil.Roz.Presentation;

namespace Zphil.Roz.Tests.Presentation;

public class FormatFileDistributionTests
{
    [Fact]
    public void SingleFile_ShowsFileAndCount()
    {
        // Arrange
        List<FileDistributionEntry> files =
        [
            new("src/Services/ShapeService.cs", "TestFixture", 7)
        ];

        // Act
        string result = ReferenceFormatter.FormatFileDistribution(files);

        // Assert
        result.ShouldContain("Top files:");
        result.ShouldContain("src/Services/ShapeService.cs");
        result.ShouldContain("7");
    }

    [Fact]
    public void MultipleFiles_SortedByCount()
    {
        // Arrange
        List<FileDistributionEntry> files =
        [
            new("src/A.cs", "ProjectA", 42),
            new("src/B.cs", "ProjectB", 28),
            new("src/C.cs", "ProjectA", 5)
        ];

        // Act
        string result = ReferenceFormatter.FormatFileDistribution(files);

        // Assert
        int indexA = result.IndexOf("src/A.cs");
        int indexB = result.IndexOf("src/B.cs");
        int indexC = result.IndexOf("src/C.cs");
        indexA.ShouldBeLessThan(indexB);
        indexB.ShouldBeLessThan(indexC);
    }

    [Fact]
    public void MoreThan20Files_ShowsTopAndOverflowMessage()
    {
        // Arrange
        List<FileDistributionEntry> files = Enumerable.Range(1, 25)
            .Select(i => new FileDistributionEntry($"src/File{i:D2}.cs", "Project", 100 - i))
            .ToList();

        // Act
        string result = ReferenceFormatter.FormatFileDistribution(files);

        // Assert
        result.ShouldContain("src/File01.cs");
        result.ShouldContain("src/File20.cs");
        result.ShouldNotContain("src/File21.cs");
        result.ShouldContain("... and 5 more files");
    }

    [Fact]
    public void Exactly20Files_NoOverflowMessage()
    {
        // Arrange
        List<FileDistributionEntry> files = Enumerable.Range(1, 20)
            .Select(i => new FileDistributionEntry($"src/File{i:D2}.cs", "Project", 100 - i))
            .ToList();

        // Act
        string result = ReferenceFormatter.FormatFileDistribution(files);

        // Assert
        result.ShouldContain("src/File20.cs");
        result.ShouldNotContain("more files");
    }

    [Fact]
    public void LongPath_TruncatesWithEllipsis()
    {
        // Arrange — path longer than 60 characters
        var longPath = "src/Very/Deep/Nested/Directory/Structure/With/Many/Levels/SomeReallyLongFileName.cs";
        List<FileDistributionEntry> files =
        [
            new(longPath, "Project", 10)
        ];

        // Act
        string result = ReferenceFormatter.FormatFileDistribution(files);

        // Assert
        result.ShouldContain("...");
        result.ShouldNotContain(longPath);
    }

    [Fact]
    public void PathAndCount_RenderedAsColonSeparatedPair()
    {
        // Arrange
        List<FileDistributionEntry> files =
        [
            new("src/A.cs", "ProjectA", 1234),
            new("src/B.cs", "ProjectB", 5)
        ];

        // Act
        string result = ReferenceFormatter.FormatFileDistribution(files);

        // Assert — no fixed-width padding; just "path: count"
        result.ShouldContain("src/A.cs: 1234");
        result.ShouldContain("src/B.cs: 5");
    }
}
