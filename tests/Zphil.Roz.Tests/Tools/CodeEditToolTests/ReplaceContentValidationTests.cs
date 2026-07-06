using Zphil.Roz.Models;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Tests for replace_content safety guards: max match line span, max match count.
/// </summary>
public class ReplaceContentValidationTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task ReplaceContent_RegexMatchSpansTooManyLines_ReportsErrorPerOp()
    {
        // Arrange — write a file with 60+ lines so a cross-line match exceeds the 50-line limit
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        List<string> lines = new()
        {
            "namespace TestFixture.Shapes;",
            "",
            "public class Circle(double radius) : Shape",
            "{"
        };
        for (var i = 0; i < 55; i++)
        {
            lines.Add($"    // Line {i}");
        }

        lines.Add("    public double Radius { get; } = radius;");
        lines.Add("}");
        await RewriteFileAsync(Fixture, circleFile, _ => String.Join("\r\n", lines));

        // Act — singleline regex that spans the entire class body (>50 lines)
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, @"\{.*\}", "{ }", true, true)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Error");
        result.ShouldContain("lines (limit:");
    }

    [Fact]
    public async Task ReplaceContent_TooManyMatches_ReportsErrorPerOp()
    {
        // Arrange — write a file with 110 lines each containing "MATCH_ME"
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        List<string> lines = new()
        {
            "namespace TestFixture.Shapes;",
            "",
            "public class Circle(double radius) : Shape",
            "{"
        };
        for (var i = 0; i < 110; i++)
        {
            lines.Add($"    // MATCH_ME {i}");
        }

        lines.Add("    public double Radius { get; } = radius;");
        lines.Add("}");
        await RewriteFileAsync(Fixture, circleFile, _ => String.Join("\r\n", lines));

        // Act — regex matches >100 times
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "MATCH_ME", "REPLACED", true)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Error");
        result.ShouldContain("limit:");
    }

    [Fact]
    public async Task ReplaceContent_TooManyMatches_LiteralMode_ReportsErrorPerOp()
    {
        // Arrange — same setup but with literal (non-regex) mode
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        List<string> lines = new()
        {
            "namespace TestFixture.Shapes;",
            "",
            "public class Circle(double radius) : Shape",
            "{"
        };
        for (var i = 0; i < 110; i++)
        {
            lines.Add($"    // MATCH_ME {i}");
        }

        lines.Add("    public double Radius { get; } = radius;");
        lines.Add("}");
        await RewriteFileAsync(Fixture, circleFile, _ => String.Join("\r\n", lines));

        // Act — literal match >100 times
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "MATCH_ME", "REPLACED")], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Error");
        result.ShouldContain("limit:");
    }

    [Fact]
    public async Task ReplaceContent_ReasonableMatchCount_Succeeds()
    {
        // Arrange — Circle.cs has "Radius" 4 times (well under limit)
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "Radius", "R")], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("occurrence(s)");
    }

    [Fact]
    public async Task ReplaceContent_SingleLineRegexMatch_Succeeds()
    {
        // Arrange — single-line regex under all limits
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — match individual lines containing "Radius" (3 lines, each 1 line span)
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, @"Radius", "R", true)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("occurrence(s)");
    }

    [Fact]
    public async Task ReplaceContent_LiteralReplacementWouldEmptyFile_ReportsErrorWithLiteralHint()
    {
        // Arrange — write a file whose entire content is "MATCH_ME"
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, _ => "MATCH_ME");

        // Act — literal mode that empties the file gets a different hint than regex mode
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "MATCH_ME", "")], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Error");
        result.ShouldContain("empty");
        result.ShouldContain("matched the entire file content");
    }

    [Fact]
    public async Task ReplaceContent_RegexReplacementWouldEmptyFile_ReportsErrorWithRegexHint()
    {
        // Arrange — write a file whose entire content is "MATCH_ME"
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, _ => "MATCH_ME");

        // Act — regex mode that empties the file gets a regex-specific hint
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, ".*", "", true, true)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Error");
        result.ShouldContain("empty");
        result.ShouldContain("regex pattern");
    }

    [Fact]
    public async Task ReplaceContent_EmptySearch_ReportsErrorPerOp()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — empty search with a non-empty replacement must be rejected, not loop forever (literal mode)
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circleFile, "", "x")], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Error");
        result.ShouldContain("search must not be empty");
    }
}
