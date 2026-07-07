using Zphil.Roz.Models;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

public class ReplaceContentTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task ReplaceContent_LiteralSearch_ReplacesAllOccurrences()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — "Radius" appears 4 times in Circle.cs
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "Radius", "R")], ct: TestContext.Current.CancellationToken);

        // Assert — "Radius" appears 4 times in Circle.cs (lines 5, 7×2, 8)
        result.ShouldContain("4 occurrence(s)");
        string newContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        // "Radius" property is replaced; constructor param "radius" (lowercase) stays untouched
        newContent.ShouldContain("public double R {");
    }

    [Fact]
    public async Task ReplaceContent_RedundantPrefix_Resolves()
    {
        // Arrange — agent-typed path with a redundant project prefix.
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        const string redundantPath = "TestFixture/TestFixture/Shapes/Circle.cs";

        // Act
        string result = await tools.ReplaceContent([new ReplaceContentRequest(redundantPath, "Radius", "R")], ct: TestContext.Current.CancellationToken);

        // Assert — resolver found Circle.cs despite the redundant prefix
        result.ShouldContain("occurrence(s)");
        string newContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        newContent.ShouldContain("public double R {");
    }

    [Fact]
    public async Task ReplaceContent_RegexSearch_ReplacesMatches()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — regex replaces "Math.PI" with a constant name
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, @"Math\.PI", "Pi", true)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("occurrence(s)");
        string newContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        newContent.ShouldNotContain("Math.PI");
        newContent.ShouldContain("Pi");
    }

    [Fact]
    public async Task ReplaceContent_InvalidRegex_ReportsErrorPerOp()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act
        string result = await tools.ReplaceContent([new ReplaceContentRequest(CircleFile(Fixture), "[invalid", "x", true)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Error");
        result.ShouldContain("Invalid regex");
    }

    [Fact]
    public async Task ReplaceContent_NoMatch_ReportsErrorPerOp()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act
        string result = await tools.ReplaceContent([new ReplaceContentRequest(CircleFile(Fixture), "XyzNonExistentText999", "replacement")], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Error");
        result.ShouldContain("No matches found");
        result.ShouldContain("line endings are normalized automatically");
    }

    [Fact]
    public async Task ReplaceContent_EmptyReplacement_DeletesMatchedContent()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await EnsureCrlfAsync(Fixture, circleFile);

        // Act — replace "Math.PI" with empty string (deletion)
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "Math.PI", "")], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("occurrence(s)");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("Math.PI");
        // Line endings should still be consistent
        content.ShouldHaveNoBareLineFeed();
    }

    [Fact]
    public async Task ReplaceContent_RegexWithBackreferences_SubstitutesGroups()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — capture "Math" and "PI" separately, swap order
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, @"(Math)\.(PI)", "$2_from_$1", true)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("occurrence(s)");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("PI_from_Math");
        content.ShouldNotContain("Math.PI");
    }

    [Fact]
    public async Task ReplaceContent_RegexReplacementNewlineEscape_ProducesActualNewline()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await EnsureCrlfAsync(Fixture, circleFile);

        // Act — replace "Math.PI" with two lines using \n in the replacement string
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, @"Math\.PI", "Math\n    .PI", true)], ct: TestContext.Current.CancellationToken);

        // Assert — \n in replacement should produce an actual newline, not literal \n
        result.ShouldContain("occurrence(s)");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain(@"\n"); // no literal backslash-n
        content.ShouldContain("Math\r\n    .PI"); // actual newline (CRLF on Windows)
    }

    [Fact]
    public async Task ReplaceContent_RegexReplacementTabEscape_ProducesActualTab()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — replace "Math.PI" with tab-separated text using \t
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, @"Math\.PI", "Math\t.PI", true)], ct: TestContext.Current.CancellationToken);

        // Assert — \t in replacement should produce an actual tab character
        result.ShouldContain("occurrence(s)");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Math\t.PI");
    }

    [Fact]
    public async Task ReplaceContent_RegexReplacementEscapedBackslashThenN_PreservesLiteralBackslashN()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — @"Math\\n" = M a t h \ \ n ; escaped-backslash + n → literal backslash-then-n, NOT a newline
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, @"Math\.PI", @"Math\\n", true)], ct: TestContext.Current.CancellationToken);

        // Assert — \\ is honored, so output is a literal backslash + n, not an actual newline
        result.ShouldContain("occurrence(s)");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain(@"Math\n"); // literal backslash + n survived
        content.ShouldNotContain("Math\r\n"); // did NOT become an actual newline
    }

    [Fact]
    public async Task ReplaceContent_RegexAnchors_MatchLineStartEnd()
    {
        // Arrange — Circle.cs has multiple lines starting with "    public"
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — use ^ anchor to match lines starting with "    public override"
        string result = await tools.ReplaceContent([
            new ReplaceContentRequest(circleFile, @"^    public override", "    public new override", true)
        ], ct: TestContext.Current.CancellationToken);

        // Assert — should match Area and Perimeter lines (both start with "    public override")
        result.ShouldContain("2 occurrence(s)");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("public new override double Area");
        content.ShouldContain("public new override double Perimeter");
    }

    [Fact]
    public async Task ReplaceContent_LiteralModeWithRegexSpecialChars_TreatsAsLiteral()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — search for "Math.PI" in literal mode (the . is NOT a regex wildcard)
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "Math.PI", "MY_PI")], ct: TestContext.Current.CancellationToken);

        // Assert — only exact "Math.PI" matches, not "MathXPI" or similar
        result.ShouldContain("occurrence(s)");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("MY_PI");
        content.ShouldNotContain("Math.PI");
    }

    [Fact]
    public async Task ReplaceContent_RegexDotMatchesNewline_CrossLineMatch()
    {
        // Arrange — Area and Perimeter are on adjacent lines in Circle.cs
        // RegexOptions.Singleline makes . match \n, enabling cross-line patterns
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — .* spans across the newline between Area and Perimeter (requires singleline)
        string result = await tools.ReplaceContent([
            new ReplaceContentRequest(circleFile, "Area.*Perimeter", "MATCHED", true, true)
        ], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("1 occurrence(s)");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("MATCHED");
    }

    [Fact]
    public async Task ReplaceContent_RegexDotStarPattern_MatchesSingleLineOnly()
    {
        // Arrange — Circle.cs has "Radius" on 3 lines (property, Area, Perimeter).
        // The pattern ^.*Radius.*\n should match each line individually,
        // NOT greedily span the entire file due to Singleline flag.
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string originalContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        int originalLineCount = SplitLines(originalContent).Length;

        // Act — delete lines containing "Radius"
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, @"^.*Radius.*\n", "", true)], ct: TestContext.Current.CancellationToken);

        // Assert — should remove 3 lines, not the entire file
        result.ShouldContain("3 occurrence(s)");
        string newContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        newContent.ShouldNotContain("Radius");
        newContent.ShouldContain("namespace"); // file still has content
        int newLineCount = SplitLines(newContent).Length;
        newLineCount.ShouldBe(originalLineCount - 3);
    }

    [Fact]
    public async Task ReplaceContent_ReplacementWouldEmptyFile_ReportsErrorPerOp()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — a pattern that matches the entire file should be rejected
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, @"[\s\S]+", "", true)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Error");
        result.ShouldContain("empty");
    }

    [Fact]
    public async Task ReplaceContent_SinglelineWithoutRegex_ReportsErrorPerOp()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — singleline requires isRegex=true
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "Radius", "R", false, true)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Error");
        result.ShouldContain("isRegex");
    }

    [Fact]
    public async Task ReplaceContent_FileNotFound_ReportsErrorPerOp()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string nonExistentFile = Path.Combine(Fixture.WorkspaceManager.SolutionDirectory!, "TestFixture", "Shapes", "DoesNotExist.cs");

        // Act
        string result = await tools.ReplaceContent([new ReplaceContentRequest(nonExistentFile, "anything", "replacement")], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Error");
        result.ShouldContain("File not found");
    }

    // ── No-op detection ──────────────────────────────────────────────────

    [Fact]
    public async Task ReplaceContent_IdenticalSearchAndReplace_ReturnsNoOp()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string originalContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);

        // Act — search and replace are the same string
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "Radius", "Radius")], ct: TestContext.Current.CancellationToken);

        // Assert — should report no-op, file unchanged
        result.ShouldContain("No changes made");
        result.ShouldContain("identical");
        string afterContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        afterContent.ShouldBe(originalContent);
    }

    [Fact]
    public async Task ReplaceContent_RegexNoOp_ReturnsNoOp()
    {
        // Arrange — regex that captures and replaces with identical content
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string originalContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);

        // Act — capture "Radius" and replace with "$0" (same content)
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "Radius", "$0", true)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No changes made");
        string afterContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        afterContent.ShouldBe(originalContent);
    }

    // ── Line number reporting ────────────────────────────────────────────

    [Fact]
    public async Task ReplaceContent_ReportsLineNumbers()
    {
        // Arrange — "Radius" appears 4 times: lines 5, 7 (twice), 8 in Circle.cs
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "Radius", "R")], ct: TestContext.Current.CancellationToken);

        // Assert — line numbers are reported per occurrence (Radius is on 5, 7 twice, 8)
        result.ShouldContain("At line(s): 5, 7, 7, 8");
    }
}
