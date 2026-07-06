using System.Text;
using Zphil.Roz.Models;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

public class ReplaceContentEncodingTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task ReplaceContent_FileWithBom_PreservesBom()
    {
        // Arrange — re-write Circle.cs with a UTF-8 BOM
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(circleFile, content, new UTF8Encoding(true), TestContext.Current.CancellationToken);

        // Act
        await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "Radius", "R")], ct: TestContext.Current.CancellationToken);

        // Assert — file should still start with BOM bytes
        await circleFile.ShouldHaveBomAsync();
    }

    [Fact]
    public async Task ReplaceContent_SequentialEditsOnBomFile_DoesNotAccumulateBoms()
    {
        // Arrange — write Circle.cs with a UTF-8 BOM
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(circleFile, content, new UTF8Encoding(true), TestContext.Current.CancellationToken);

        // Act — perform multiple sequential edits on the same BOM file
        await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "Radius", "Rad")], ct: TestContext.Current.CancellationToken);
        await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "Rad", "Radius")], ct: TestContext.Current.CancellationToken);
        await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "Radius", "R")], ct: TestContext.Current.CancellationToken);

        // Assert — file should still have exactly one BOM, not three
        await circleFile.ShouldHaveExactlyOneBomAsync();
    }

    [Fact]
    public async Task ReplaceContent_MixedLineEndings_NormalizesToCrlf()
    {
        // Arrange — create a file with mixed CRLF and LF endings
        // NormalizeLineEndings: if reference Contains("\r\n"), converts all to CRLF
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await EnsureCrlfAsync(Fixture, circleFile);
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        // Create mixed endings by making the 3rd line LF-only
        string[] crlfLines = content.Split("\r\n");
        crlfLines.Length.ShouldBeGreaterThan(3, "Fixture file must have >3 lines for mixed-ending test");
        string mixed = String.Join("\r\n", crlfLines[..2]) + "\n"
                                                           + String.Join("\r\n", crlfLines[2..]);
        await File.WriteAllTextAsync(circleFile, mixed, new UTF8Encoding(false), TestContext.Current.CancellationToken);

        // Act
        await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "Radius", "R")], ct: TestContext.Current.CancellationToken);

        // Assert — after replace_content, file should be uniformly CRLF
        // (because the mixed file contains at least one \r\n)
        string result = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        result.ShouldHaveNoBareLineFeed();
    }

    [Fact]
    public async Task ReplaceContent_FileWithoutFinalNewline_PreservesNoFinalNewline()
    {
        // Arrange — remove trailing newline from Circle.cs
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content = content.TrimEnd('\r', '\n');
        await File.WriteAllTextAsync(circleFile, content, new UTF8Encoding(false), TestContext.Current.CancellationToken);

        // Act
        await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "Radius", "R")], ct: TestContext.Current.CancellationToken);

        // Assert — file should still NOT end with a newline
        string result = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        result[^1].ShouldNotBe('\n');
        result[^1].ShouldNotBe('\r');
    }

    [Fact]
    public async Task ReplaceContent_UnicodeInSearchAndReplace_HandlesCorrectly()
    {
        // Arrange — add a string literal with Unicode to Circle.cs
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content = content.Replace("Circle(double radius)", "Circle(double radius) // \u2603 snowman");
        await File.WriteAllTextAsync(circleFile, content, new UTF8Encoding(false), TestContext.Current.CancellationToken);

        // Act — replace the Unicode character
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "\u2603 snowman", "\u2764 heart")], ct: TestContext.Current.CancellationToken);

        // Assert — Unicode replacement should work
        result.ShouldContain("occurrence(s)");
        string newContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        newContent.ShouldContain("\u2764 heart");
        newContent.ShouldNotContain("\u2603 snowman");
    }

    [Fact]
    public async Task ReplaceContent_ContentInsideComment_ReplacesInCommentToo()
    {
        // Arrange — add a comment containing "Radius" to Circle.cs
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content = content.Replace(
            "public class Circle",
            "// Circle uses Radius for calculations\npublic class Circle");
        await File.WriteAllTextAsync(circleFile, content, new UTF8Encoding(false), TestContext.Current.CancellationToken);

        // Act — replace all "Radius" occurrences (should include the one in the comment)
        await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "Radius", "R")], ct: TestContext.Current.CancellationToken);

        // Assert — replace_content is a text operation, it replaces in comments too
        string newContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        newContent.ShouldContain("// Circle uses R for calculations");
    }

    [Fact]
    public async Task ReplaceContent_FileWithMultipleTrailingNewlines_PreservesTrailingNewlines()
    {
        // Arrange — append extra newlines to Circle.cs
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content += "\r\n\r\n\r\n";
        await File.WriteAllTextAsync(circleFile, content, new UTF8Encoding(false), TestContext.Current.CancellationToken);

        // Act
        await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "Radius", "R")], ct: TestContext.Current.CancellationToken);

        // Assert — multiple trailing newlines should not be collapsed
        string result = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        result.ShouldEndWith("\r\n\r\n\r\n");
    }
}
