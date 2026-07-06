using Zphil.Roz.Models;
using Zphil.Roz.Presentation;

namespace Zphil.Roz.Tests.Presentation;

public class FormatDistributionSummaryTests
{
    [Fact]
    public void SingleProject_ShowsProjectNameAndCount()
    {
        // Arrange
        List<ProjectDistributionEntry> distribution =
        [
            new("MyProject", 7, 0)
        ];

        // Act
        string result = ReferenceFormatter.FormatDistributionSummary(distribution);

        // Assert
        result.ShouldContain("Distribution:");
        result.ShouldContain("MyProject: 7");
        result.ShouldContain("Total: 7 across 1 projects");
    }

    [Fact]
    public void MultipleProjects_SortedByCount_ShowsTotalAcrossAll()
    {
        // Arrange
        List<ProjectDistributionEntry> distribution =
        [
            new("Orleans.Runtime", 11, 0),
            new("Orleans.Core", 5, 0)
        ];

        // Act
        string result = ReferenceFormatter.FormatDistributionSummary(distribution);

        // Assert
        result.ShouldContain("Orleans.Runtime: 11");
        result.ShouldContain("Orleans.Core: 5");
        result.ShouldContain("Total: 16 across 2 projects");
    }

    [Fact]
    public void FileCountGreaterThanZero_ShowsFileInfo()
    {
        // Arrange
        List<ProjectDistributionEntry> distribution =
        [
            new("MyProject", 10, 3)
        ];

        // Act
        string result = ReferenceFormatter.FormatDistributionSummary(distribution);

        // Assert
        result.ShouldContain("MyProject: 10 (3 files)");
    }

    [Fact]
    public void FileCountZero_SuppressesFileInfo()
    {
        // Arrange
        List<ProjectDistributionEntry> distribution =
        [
            new("MyProject", 10, 0)
        ];

        // Act
        string result = ReferenceFormatter.FormatDistributionSummary(distribution);

        // Assert
        result.ShouldNotContain("files");
    }
}
