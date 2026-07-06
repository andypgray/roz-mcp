using Zphil.Roz.Extensions;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Extensions;

public class FilePathResolverTests(WorkspaceFixture fixture)
{
    [Fact]
    public async Task ResolveAgainstSolutionAsync_AbsolutePath_ReturnsInputUnchanged()
    {
        // Arrange
        string absolute = Path.GetFullPath(fixture.ShapesFile("Circle.cs"));

        // Act
        string result = await FilePathResolver.ResolveAgainstSolutionAsync(absolute, fixture.WorkspaceManager, TestContext.Current.CancellationToken);

        // Assert
        result.ShouldBe(absolute);
    }

    [Fact]
    public async Task ResolveAgainstSolutionAsync_SolutionRelativePath_ResolvesToDocument()
    {
        // Arrange — "TestFixture/Shapes/Circle.cs" is the normal relative form
        const string input = "TestFixture/Shapes/Circle.cs";

        // Act
        string result = await FilePathResolver.ResolveAgainstSolutionAsync(input, fixture.WorkspaceManager, TestContext.Current.CancellationToken);

        // Assert
        result.ShouldBe(fixture.ShapesFile("Circle.cs"));
    }

    [Fact]
    public async Task ResolveAgainstSolutionAsync_RedundantProjectPrefix_ResolvesToRealDocument()
    {
        // Arrange — agent typed the project name twice: "TestFixture/TestFixture/Shapes/Circle.cs"
        // instead of the true "TestFixture/Shapes/Circle.cs".
        const string input = "TestFixture/TestFixture/Shapes/Circle.cs";

        // Act
        string result = await FilePathResolver.ResolveAgainstSolutionAsync(input, fixture.WorkspaceManager, TestContext.Current.CancellationToken);

        // Assert
        result.ShouldBe(fixture.ShapesFile("Circle.cs"));
    }

    [Fact]
    public async Task ResolveAgainstSolutionAsync_RedundantProjectPrefixMixedSeparators_ResolvesToRealDocument()
    {
        // Arrange — redundant prefix with interleaved back- and forward-slashes (distinct from the
        // all-forward-slash and all-backslash siblings)
        const string input = @"TestFixture\TestFixture/Shapes\Circle.cs";

        // Act
        string result = await FilePathResolver.ResolveAgainstSolutionAsync(input, fixture.WorkspaceManager, TestContext.Current.CancellationToken);

        // Assert
        result.ShouldBe(fixture.ShapesFile("Circle.cs"));
    }

    [Fact]
    public async Task ResolveAgainstSolutionAsync_RedundantProjectPrefixBackslash_ResolvesToRealDocument()
    {
        // Arrange — backslash variant (Windows path style)
        const string input = @"TestFixture\TestFixture\Shapes\Circle.cs";

        // Act
        string result = await FilePathResolver.ResolveAgainstSolutionAsync(input, fixture.WorkspaceManager, TestContext.Current.CancellationToken);

        // Assert
        result.ShouldBe(fixture.ShapesFile("Circle.cs"));
    }

    [Fact]
    public async Task ResolveAgainstSolutionAsync_CaseMismatchOnWindows_ResolvesToRealDocument()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        const string input = "testfixture/SHAPES/circle.cs";

        // Act
        string result = await FilePathResolver.ResolveAgainstSolutionAsync(input, fixture.WorkspaceManager, TestContext.Current.CancellationToken);

        // Assert — Windows NTFS is case-insensitive; the resolved path points to the same file
        String.Equals(result, fixture.ShapesFile("Circle.cs"), StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAgainstSolutionAsync_AmbiguousSuffix_ThrowsWithCandidates()
    {
        // Arrange — SharedHelper.cs exists in both TestFixture.Legacy and TestFixture.Minimal
        const string input = "SharedHelper.cs";

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => FilePathResolver.ResolveAgainstSolutionAsync(input, fixture.WorkspaceManager));

        ex.Message.ShouldContain("ambiguous");
        ex.Message.ShouldContain("SharedHelper.cs");
        ex.Message.ShouldContain("TestFixture.Legacy/SharedHelper.cs");
        ex.Message.ShouldContain("TestFixture.Minimal/SharedHelper.cs");
    }

    [Fact]
    public async Task ResolveAgainstSolutionAsync_NoMatch_ThrowsWithLiteralPath()
    {
        // Arrange
        const string input = "Foo/DoesNotExist.cs";

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => FilePathResolver.ResolveAgainstSolutionAsync(input, fixture.WorkspaceManager));

        ex.Message.ShouldContain("File not found in solution");
        ex.Message.ShouldContain(input);
        ex.Message.ShouldContain("DoesNotExist.cs");
    }

    [Fact]
    public async Task ResolveAgainstSolutionAsync_PartialSegmentSuffix_ThrowsNoMatch()
    {
        // Arrange — "ircle.cs" is a character-level (not segment-level) suffix of "Circle.cs"
        const string input = "ircle.cs";

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => FilePathResolver.ResolveAgainstSolutionAsync(input, fixture.WorkspaceManager));

        ex.Message.ShouldContain("File not found in solution");
    }

    [Fact]
    public async Task ResolveAgainstSolutionAsync_FileOnDiskButNotInSolution_ReturnsLiteralPath()
    {
        // Arrange — write a temp .txt next to the fixture that isn't part of any project
        string solutionDir = fixture.WorkspaceManager.SolutionDirectory!;
        string tempFile = Path.Combine(solutionDir, $"temp-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempFile, "not a source file", TestContext.Current.CancellationToken);

        try
        {
            // Act
            string result = await FilePathResolver.ResolveAgainstSolutionAsync(Path.GetFileName(tempFile), fixture.WorkspaceManager, TestContext.Current.CancellationToken);

            // Assert — literal path is returned since File.Exists says so
            result.ShouldBe(tempFile);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveAgainstSolutionAsync_NullOrWhitespace_ThrowsArgumentException(string? input)
    {
        // Act & Assert
        ArgumentException ex = await Should.ThrowAsync<ArgumentException>(() => FilePathResolver.ResolveAgainstSolutionAsync(input!, fixture.WorkspaceManager));

        ex.ParamName.ShouldBe("filePath");
    }

    [Fact]
    public async Task ResolveAgainstSolutionAsync_ShortTailMatch_ResolvesToUniqueDocument()
    {
        // Arrange — "Shapes/Circle.cs" is a tail of the real "TestFixture/Shapes/Circle.cs"
        // (user typed only the last two segments).
        const string input = "Shapes/Circle.cs";

        // Act
        string result = await FilePathResolver.ResolveAgainstSolutionAsync(input, fixture.WorkspaceManager, TestContext.Current.CancellationToken);

        // Assert
        result.ShouldBe(fixture.ShapesFile("Circle.cs"));
    }

    [Fact]
    public async Task ResolveAgainstSolutionAsync_DotSlashPrefixWithRedundantPrefix_NormalizesAndResolves()
    {
        // Arrange — "./" prefix must be stripped by NormalizeInput, and the redundant
        // project prefix must be suffix-matched. Combined, this exercises both normalization paths.
        const string input = "./TestFixture/TestFixture/Shapes/Circle.cs";

        // Act
        string result = await FilePathResolver.ResolveAgainstSolutionAsync(input, fixture.WorkspaceManager, TestContext.Current.CancellationToken);

        // Assert
        result.ShouldBe(fixture.ShapesFile("Circle.cs"));
    }
}
