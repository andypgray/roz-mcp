using Zphil.Roz.Infrastructure;
using Zphil.Roz.Setup;
using Zphil.Roz.Tests.Setup.Fixtures;

namespace Zphil.Roz.Tests.Setup;

/// <summary>
///     <see cref="ProjectInstructionsConfigurator.WriteSnippetAsync" /> creates, appends, or
///     replaces the Roslyn project-instructions section in a per-client rules file
///     (<c>CLAUDE.md</c>, <c>AGENTS.md</c>). These tests guard the replace semantics — a stale
///     <c># roz-mcp</c> section is refreshed in place (hand edits inside it clobbered by design)
///     while user content outside the section survives byte-exactly — plus correct file creation
///     and the choice of target file per client.
/// </summary>
public class ProjectInstructionsConfiguratorTests : IDisposable
{
    private readonly TempProjectDirectory _projectRoot = new("roz-mcp-rules");

    public void Dispose() => _projectRoot.Dispose();

    [Theory]
    [InlineData("CLAUDE.md")]
    [InlineData("AGENTS.md")]
    public async Task WriteSnippetAsync_FileMissing_CreatesFileWithSnippet(string rulesFileName)
    {
        // Act
        await ProjectInstructionsConfigurator.WriteSnippetAsync(_projectRoot, rulesFileName);

        // Assert
        string path = Path.Combine(_projectRoot, rulesFileName);
        File.Exists(path).ShouldBeTrue();

        string content = await File.ReadAllTextAsync(path);
        content.ShouldBe(ProjectInstructionsSnippet.Text);
    }

    [Fact]
    public async Task WriteSnippetAsync_FileExistsWithoutSnippet_AppendsWithSeparator()
    {
        // Arrange
        string path = Path.Combine(_projectRoot, "AGENTS.md");
        await File.WriteAllTextAsync(path, "# Existing\n\nUnrelated content.\n", TestContext.Current.CancellationToken);

        // Act
        await ProjectInstructionsConfigurator.WriteSnippetAsync(_projectRoot, "AGENTS.md");

        // Assert
        string content = await File.ReadAllTextAsync(path);
        content.ShouldStartWith("# Existing");
        content.ShouldContain(ProjectInstructionsSnippet.SectionHeading);
        content.ShouldEndWith(ProjectInstructionsSnippet.Text);
    }

    [Fact]
    public async Task WriteSnippetAsync_StaleSection_ReplacedWithCurrentSnippet()
    {
        // Arrange — a section written by an older release, with an outdated body.
        string path = Path.Combine(_projectRoot, "AGENTS.md");
        await File.WriteAllTextAsync(path,
            "# roz-mcp\n\nStale rules from an older roz-mcp release.\n",
            TestContext.Current.CancellationToken);

        // Act
        await ProjectInstructionsConfigurator.WriteSnippetAsync(_projectRoot, "AGENTS.md");

        // Assert — the old body is gone, the current snippet is in, and the heading appears once.
        string content = await File.ReadAllTextAsync(path);
        content.ShouldNotContain("Stale rules");
        content.ShouldBe(ProjectInstructionsSnippet.Text);
        CountOccurrences(content, ProjectInstructionsSnippet.SectionHeading).ShouldBe(1);
    }

    [Fact]
    public async Task WriteSnippetAsync_UserContentBeforeAndAfterSection_PreservedByteExact()
    {
        // Arrange — user sections surround a stale roz section.
        const string before = "# My project\n\nHouse rules the user wrote.\n\n";
        const string after = "# Deployment\n\nUser epilogue.\n";
        string path = Path.Combine(_projectRoot, "AGENTS.md");
        await File.WriteAllTextAsync(path,
            before + "# roz-mcp\n\nOld roz body.\n\n" + after,
            TestContext.Current.CancellationToken);

        // Act
        await ProjectInstructionsConfigurator.WriteSnippetAsync(_projectRoot, "AGENTS.md");

        // Assert — only the roz section changed; the user's sections are untouched.
        string content = await File.ReadAllTextAsync(path);
        content.ShouldStartWith(before);
        content.ShouldEndWith(after);
        content.ShouldNotContain("Old roz body");
        content.ShouldContain(ProjectInstructionsSnippet.Text.TrimEnd('\n'));
    }

    [Fact]
    public async Task WriteSnippetAsync_HandEditedSection_Overwritten()
    {
        // Arrange — a current section with a hand-added line inside it.
        await ProjectInstructionsConfigurator.WriteSnippetAsync(_projectRoot, "AGENTS.md");
        string path = Path.Combine(_projectRoot, "AGENTS.md");
        string written = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path,
            written + "\nMy hand-added roz note.\n",
            TestContext.Current.CancellationToken);

        // Act — re-run setup.
        await ProjectInstructionsConfigurator.WriteSnippetAsync(_projectRoot, "AGENTS.md");

        // Assert — hand edits inside the section do not survive a re-run; that is the design.
        string content = await File.ReadAllTextAsync(path);
        content.ShouldNotContain("My hand-added roz note");
        content.ShouldBe(ProjectInstructionsSnippet.Text);
    }

    [Fact]
    public async Task WriteSnippetAsync_SecondRun_IsByteIdentical()
    {
        // Arrange — first run creates the file.
        await ProjectInstructionsConfigurator.WriteSnippetAsync(_projectRoot, "AGENTS.md");
        string path = Path.Combine(_projectRoot, "AGENTS.md");
        byte[] firstWrite = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        // Act — replacing a current section with itself must be a no-op.
        await ProjectInstructionsConfigurator.WriteSnippetAsync(_projectRoot, "AGENTS.md");

        // Assert
        byte[] secondWrite = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        secondWrite.ShouldBe(firstWrite);
    }

    [Fact]
    public async Task WriteSnippetAsync_SectionAtEndOfFile_ReplacedToEof()
    {
        // Arrange — the roz section is last and its stale body has no trailing newline.
        const string before = "# Notes\n\nKeep these.\n\n";
        string path = Path.Combine(_projectRoot, "AGENTS.md");
        await File.WriteAllTextAsync(path,
            before + "# roz-mcp\n\nOld tail body without trailing newline",
            TestContext.Current.CancellationToken);

        // Act
        await ProjectInstructionsConfigurator.WriteSnippetAsync(_projectRoot, "AGENTS.md");

        // Assert — replaced through end-of-file, normalized to a single trailing newline.
        string content = await File.ReadAllTextAsync(path);
        content.ShouldBe(before + ProjectInstructionsSnippet.Text);
        content.ShouldNotContain("Old tail body");
    }

    [Fact]
    public async Task WriteSnippetAsync_SectionFollowedByAnotherH1_StopsAtH1()
    {
        // Arrange — the stale section contains a "## " subsection (must stay inside the section)
        // and is followed by a user H1 (must terminate it).
        const string after = "# After\n\nUser epilogue.\n";
        string path = Path.Combine(_projectRoot, "AGENTS.md");
        await File.WriteAllTextAsync(path,
            "# roz-mcp\n\nOld body.\n\n## Old subsection\n\nMore old body.\n\n" + after,
            TestContext.Current.CancellationToken);

        // Act
        await ProjectInstructionsConfigurator.WriteSnippetAsync(_projectRoot, "AGENTS.md");

        // Assert — the "## " line was replaced with the section; the next "# " line was not.
        string content = await File.ReadAllTextAsync(path);
        content.ShouldNotContain("Old body");
        content.ShouldNotContain("Old subsection");
        content.ShouldEndWith(after);
        content.ShouldContain("\n\n# After");
        CountOccurrences(content, ProjectInstructionsSnippet.SectionHeading).ShouldBe(1);
    }

    [Fact]
    public async Task WriteSnippetAsync_CrlfUserContent_PreservedOutsideSection()
    {
        // Arrange — a CRLF rules file; the splice must not rewrite the user's line endings.
        const string before = "# My project\r\n\r\nCRLF house rules.\r\n\r\n";
        const string after = "# Epilogue\r\n\r\nAlso CRLF.\r\n";
        string path = Path.Combine(_projectRoot, "AGENTS.md");
        await File.WriteAllTextAsync(path,
            before + "# roz-mcp\r\n\r\nOld CRLF body.\r\n\r\n" + after,
            TestContext.Current.CancellationToken);

        // Act
        await ProjectInstructionsConfigurator.WriteSnippetAsync(_projectRoot, "AGENTS.md");

        // Assert — user content keeps its CRLF endings byte-exactly; only the section was rewritten.
        string content = await File.ReadAllTextAsync(path);
        content.ShouldStartWith(before);
        content.ShouldEndWith(after);
        content.ShouldNotContain("Old CRLF body");
    }

    [Fact]
    public async Task WriteSnippetAsync_WritesToCorrectFilePerCall()
    {
        // Act — Claude path writes CLAUDE.md, the rest write AGENTS.md.
        await ProjectInstructionsConfigurator.WriteSnippetAsync(_projectRoot, "CLAUDE.md");
        await ProjectInstructionsConfigurator.WriteSnippetAsync(_projectRoot, "AGENTS.md");

        // Assert — both files exist independently.
        File.Exists(Path.Combine(_projectRoot, "CLAUDE.md")).ShouldBeTrue();
        File.Exists(Path.Combine(_projectRoot, "AGENTS.md")).ShouldBeTrue();
    }

    [Fact]
    public async Task WriteSnippetAsync_CreatedFile_HasNoByteOrderMark()
    {
        // CR-10: the atomic write must stay byte-identical to the prior plain File.WriteAllTextAsync,
        // which emits UTF-8 with no BOM. Utf8NoBom (not Encoding.UTF8) preserves that — a BOM would
        // be invisible to File.ReadAllText (it strips it) but would corrupt downstream byte-exact diffs.
        await ProjectInstructionsConfigurator.WriteSnippetAsync(
            _projectRoot, "AGENTS.md", TestContext.Current.CancellationToken);

        string path = Path.Combine(_projectRoot, "AGENTS.md");
        await path.ShouldNotHaveBomAsync();
    }

    [Fact]
    public async Task WriteSnippetAsync_AppendToExistingFile_LeavesNoTempFiles()
    {
        // Arrange — append branch (file already present, no section).
        string path = Path.Combine(_projectRoot, "AGENTS.md");
        await File.WriteAllTextAsync(path, "# Existing\n", TestContext.Current.CancellationToken);

        // Act
        await ProjectInstructionsConfigurator.WriteSnippetAsync(
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
