using Zphil.Roz.Infrastructure;
using Zphil.Roz.Setup;
using Zphil.Roz.Tests.Setup.Fixtures;

namespace Zphil.Roz.Tests.Setup;

/// <summary>
///     <see cref="ProjectInstructionsConfigurator.AppendSnippetAsync" /> writes or appends the
///     Roslyn project-instructions snippet to a per-client rules file (<c>CLAUDE.md</c>,
///     <c>AGENTS.md</c>). These tests guard idempotency (no duplicate appends), correct file
///     creation, and the choice of target file per client.
/// </summary>
public class ProjectInstructionsConfiguratorTests : IDisposable
{
    private readonly TempProjectDirectory _projectRoot = new("roz-mcp-rules");

    public void Dispose() => _projectRoot.Dispose();

    [Theory]
    [InlineData("CLAUDE.md")]
    [InlineData("AGENTS.md")]
    public async Task AppendSnippetAsync_FileMissing_CreatesFileWithSnippet(string rulesFileName)
    {
        // Act
        await ProjectInstructionsConfigurator.AppendSnippetAsync(_projectRoot, rulesFileName);

        // Assert
        string path = Path.Combine(_projectRoot, rulesFileName);
        File.Exists(path).ShouldBeTrue();

        string content = await File.ReadAllTextAsync(path);
        content.ShouldBe(ProjectInstructionsSnippet.Text);
    }

    [Fact]
    public async Task AppendSnippetAsync_FileExistsWithoutSnippet_AppendsWithSeparator()
    {
        // Arrange
        string path = Path.Combine(_projectRoot, "AGENTS.md");
        await File.WriteAllTextAsync(path, "# Existing\n\nUnrelated content.\n", TestContext.Current.CancellationToken);

        // Act
        await ProjectInstructionsConfigurator.AppendSnippetAsync(_projectRoot, "AGENTS.md");

        // Assert
        string content = await File.ReadAllTextAsync(path);
        content.ShouldStartWith("# Existing");
        content.ShouldContain(ProjectInstructionsSnippet.SectionHeading);
        content.ShouldEndWith(ProjectInstructionsSnippet.Text);
    }

    [Fact]
    public async Task AppendSnippetAsync_SnippetAlreadyPresent_DoesNotDuplicate()
    {
        // Arrange — first append.
        await ProjectInstructionsConfigurator.AppendSnippetAsync(_projectRoot, "AGENTS.md");
        string path = Path.Combine(_projectRoot, "AGENTS.md");
        string firstWrite = await File.ReadAllTextAsync(path);

        // Act — second append should detect the heading and skip.
        await ProjectInstructionsConfigurator.AppendSnippetAsync(_projectRoot, "AGENTS.md");

        // Assert
        string secondWrite = await File.ReadAllTextAsync(path);
        secondWrite.ShouldBe(firstWrite);

        int headingCount = CountOccurrences(secondWrite, ProjectInstructionsSnippet.SectionHeading);
        headingCount.ShouldBe(1);
    }

    [Fact]
    public async Task AppendSnippetAsync_WritesToCorrectFilePerCall()
    {
        // Act — Claude path writes CLAUDE.md, the rest write AGENTS.md.
        await ProjectInstructionsConfigurator.AppendSnippetAsync(_projectRoot, "CLAUDE.md");
        await ProjectInstructionsConfigurator.AppendSnippetAsync(_projectRoot, "AGENTS.md");

        // Assert — both files exist independently.
        File.Exists(Path.Combine(_projectRoot, "CLAUDE.md")).ShouldBeTrue();
        File.Exists(Path.Combine(_projectRoot, "AGENTS.md")).ShouldBeTrue();
    }

    [Fact]
    public async Task AppendSnippetAsync_CreatedFile_HasNoByteOrderMark()
    {
        // CR-10: the atomic write must stay byte-identical to the prior plain File.WriteAllTextAsync,
        // which emits UTF-8 with no BOM. Utf8NoBom (not Encoding.UTF8) preserves that — a BOM would
        // be invisible to File.ReadAllText (it strips it) but would corrupt downstream byte-exact diffs.
        await ProjectInstructionsConfigurator.AppendSnippetAsync(
            _projectRoot, "AGENTS.md", TestContext.Current.CancellationToken);

        string path = Path.Combine(_projectRoot, "AGENTS.md");
        await path.ShouldNotHaveBomAsync();
    }

    [Fact]
    public async Task AppendSnippetAsync_AppendToExistingFile_LeavesNoTempFiles()
    {
        // Arrange — append branch (file already present).
        string path = Path.Combine(_projectRoot, "AGENTS.md");
        await File.WriteAllTextAsync(path, "# Existing\n", TestContext.Current.CancellationToken);

        // Act
        await ProjectInstructionsConfigurator.AppendSnippetAsync(
            _projectRoot, "AGENTS.md", TestContext.Current.CancellationToken);

        // Assert — CR-10: the temp-then-move write completed and left no temp sibling, so the
        // project directory holds exactly the rules file. Content was appended, not clobbered.
        string content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        content.ShouldStartWith("# Existing");
        content.ShouldContain(ProjectInstructionsSnippet.SectionHeading);

        string[] files = Directory.GetFiles(_projectRoot);
        files.ShouldHaveSingleItem();
        Path.GetFileName(files[0]).ShouldBe("AGENTS.md");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
