using Zphil.Roz.Symbols;

namespace Zphil.Roz.Tests.Symbols;

/// <summary>
///     Edge-case coverage of the shape parser shared by every <see cref="LocationParser" />
///     entry point: MSBuild diagnostic format, quote/whitespace stripping, drive letters,
///     UNC paths, and malformed-input rejections. Routed through the typed parse methods
///     (the unconstrained entry point was removed once production code stopped using it).
///     Per-tool typed-discrimination behaviour lives in <see cref="LocationParserTypedTests" />.
/// </summary>
public class LocationParserTests
{
    private const string ToolName = "test_tool";

    // ── Path-only shapes (drive letters, UNC paths) ──────────────────────

    [Theory]
    [InlineData("Foo.cs")]
    [InlineData("src/Foo.cs")]
    [InlineData("C:\\src\\Foo.cs")]
    [InlineData("/usr/local/Foo.cs")]
    [InlineData("\\\\server\\share\\Foo.cs")]
    public void ParseFile_VariousPathStyles_ReturnsFileLocation(string raw)
    {
        // Act
        FileLocation file = LocationParser.ParseFile(raw, ToolName);

        // Assert
        file.FilePath.ShouldBe(raw);
    }

    // ── Path + line shapes (drive letters, UNC paths) ────────────────────

    [Theory]
    [InlineData("Foo.cs:42", "Foo.cs", 42, Label = "simple relative path")]
    [InlineData("src/Foo.cs:1", "src/Foo.cs", 1, Label = "subdirectory path")]
    [InlineData("C:\\src\\Foo.cs:42", "C:\\src\\Foo.cs", 42, Label = "Windows drive-letter path")]
    [InlineData("\\\\server\\share\\Foo.cs:7", "\\\\server\\share\\Foo.cs", 7, Label = "UNC path")]
    public void ParsePosition_PathAndLine_ReturnsLineLocation(string raw, string expectedPath, int expectedLine)
    {
        // Act
        PositionLocation loc = LocationParser.ParsePosition(raw, ToolName);

        // Assert
        LineLocation line = loc.ShouldBeOfType<LineLocation>();
        line.FilePath.ShouldBe(expectedPath);
        line.Line.ShouldBe(expectedLine);
    }

    // ── Path + line + column shapes ──────────────────────────────────────

    [Theory]
    [InlineData("Foo.cs:42:18", "Foo.cs", 42, 18, Label = "simple relative path")]
    [InlineData("C:\\src\\Foo.cs:42:18", "C:\\src\\Foo.cs", 42, 18, Label = "Windows drive-letter path")]
    [InlineData("\\\\server\\share\\Foo.cs:7:1", "\\\\server\\share\\Foo.cs", 7, 1, Label = "UNC path")]
    public void ParsePosition_FullPosition_ReturnsCursorLocation(string raw, string expectedPath, int expectedLine, int expectedColumn)
    {
        // Act
        PositionLocation loc = LocationParser.ParsePosition(raw, ToolName);

        // Assert
        CursorLocation cursor = loc.ShouldBeOfType<CursorLocation>();
        cursor.FilePath.ShouldBe(expectedPath);
        cursor.Line.ShouldBe(expectedLine);
        cursor.Column.ShouldBe(expectedColumn);
    }

    [Fact]
    public void ParseFile_DriveLetterAlone_KeepsColon()
    {
        // Act — bare "C:\Foo.cs" has no trailing :digit
        FileLocation file = LocationParser.ParseFile("C:\\Foo.cs", ToolName);

        // Assert
        file.FilePath.ShouldBe("C:\\Foo.cs");
    }

    // ── MSBuild diagnostic format (silently accepted) ────────────────────

    [Theory]
    [InlineData("Foo.cs(42,18)", "Foo.cs", 42, 18)]
    [InlineData("src/Foo.cs(1,1)", "src/Foo.cs", 1, 1)]
    [InlineData("C:\\src\\Foo.cs(42,18)", "C:\\src\\Foo.cs", 42, 18)]
    [InlineData("\\\\server\\share\\Foo.cs(7,1)", "\\\\server\\share\\Foo.cs", 7, 1)]
    public void ParsePosition_MsBuildLineCol_ReturnsCursorLocation(string raw, string expectedPath, int expectedLine, int expectedColumn)
    {
        // Act
        PositionLocation loc = LocationParser.ParsePosition(raw, ToolName);

        // Assert
        CursorLocation cursor = loc.ShouldBeOfType<CursorLocation>();
        cursor.FilePath.ShouldBe(expectedPath);
        cursor.Line.ShouldBe(expectedLine);
        cursor.Column.ShouldBe(expectedColumn);
    }

    [Theory]
    [InlineData("Foo.cs(42)", "Foo.cs", 42)]
    [InlineData("C:\\src\\Foo.cs(7)", "C:\\src\\Foo.cs", 7)]
    public void ParsePosition_MsBuildLineOnly_ReturnsLineLocation(string raw, string expectedPath, int expectedLine)
    {
        // Act
        PositionLocation loc = LocationParser.ParsePosition(raw, ToolName);

        // Assert
        LineLocation line = loc.ShouldBeOfType<LineLocation>();
        line.FilePath.ShouldBe(expectedPath);
        line.Line.ShouldBe(expectedLine);
    }

    [Fact]
    public void ParsePosition_MsBuildWithSpaceAfterComma_AcceptsBoth()
    {
        // Act — VS Error List sometimes formats as "(42, 18)"
        PositionLocation loc = LocationParser.ParsePosition("Foo.cs(42, 18)", ToolName);

        // Assert
        CursorLocation cursor = loc.ShouldBeOfType<CursorLocation>();
        cursor.FilePath.ShouldBe("Foo.cs");
        cursor.Line.ShouldBe(42);
        cursor.Column.ShouldBe(18);
    }

    [Theory]
    [InlineData("Foo.cs(0,1)")]
    [InlineData("Foo.cs(1,0)")]
    [InlineData("Foo.cs(0)")]
    public void ParsePosition_MsBuildNonPositive_Throws(string raw)
    {
        // Act / Assert
        Should.Throw<UserErrorException>(() => LocationParser.ParsePosition(raw, ToolName));
    }

    [Theory]
    [InlineData("Foo.cs(abc)")]
    [InlineData("Foo.cs(42,)")]
    [InlineData("Foo.cs(,42)")]
    [InlineData("Foo.cs()")]
    public void ParseFile_MsBuildMalformedParens_TreatedAsPath(string raw)
    {
        // Act — non-numeric/empty contents fall through to colon parser, which keeps the
        // string intact as a path. The downstream FilePathResolver will reject it.
        FileLocation file = LocationParser.ParseFile(raw, ToolName);

        // Assert
        file.FilePath.ShouldBe(raw);
    }

    [Fact]
    public void ParseFile_PathContainingParens_NotMistakenForMsBuildFormat()
    {
        // Act — "(Project)" contents are non-numeric, so the parser leaves the path alone.
        FileLocation file = LocationParser.ParseFile("C:\\Some(Project)\\Foo.cs", ToolName);

        // Assert
        file.FilePath.ShouldBe("C:\\Some(Project)\\Foo.cs");
    }

    // ── Trimming and quote stripping ─────────────────────────────────────

    [Fact]
    public void ParsePosition_SurroundingQuotes_AreStripped()
    {
        // Act
        PositionLocation loc = LocationParser.ParsePosition("\"Foo.cs:42:18\"", ToolName);

        // Assert
        CursorLocation cursor = loc.ShouldBeOfType<CursorLocation>();
        cursor.FilePath.ShouldBe("Foo.cs");
        cursor.Line.ShouldBe(42);
        cursor.Column.ShouldBe(18);
    }

    [Fact]
    public void ParsePosition_LeadingTrailingWhitespace_IsTrimmed()
    {
        // Act
        PositionLocation loc = LocationParser.ParsePosition("  Foo.cs:42  ", ToolName);

        // Assert
        LineLocation line = loc.ShouldBeOfType<LineLocation>();
        line.FilePath.ShouldBe("Foo.cs");
        line.Line.ShouldBe(42);
    }

    // ── Rejection cases ──────────────────────────────────────────────────

    [Fact]
    public void ParseFile_Empty_Throws()
    {
        // Act / Assert
        Should.Throw<UserErrorException>(() => LocationParser.ParseFile("", ToolName));
    }

    [Fact]
    public void ParseFile_Whitespace_Throws()
    {
        // Act / Assert
        Should.Throw<UserErrorException>(() => LocationParser.ParseFile("   ", ToolName));
    }

    [Fact]
    public void ParseFile_OnlyColons_TreatedAsPath()
    {
        // Act — "::" has no trailing :digit suffix; we don't attempt to validate path content.
        // The downstream FilePathResolver will reject the missing/invalid file.
        FileLocation file = LocationParser.ParseFile("::", ToolName);

        // Assert
        file.FilePath.ShouldBe("::");
    }

    [Theory]
    [InlineData("Foo.cs:0")]
    [InlineData("Foo.cs:0:1")]
    [InlineData("Foo.cs:1:0")]
    public void ParsePosition_NonPositiveLineOrColumn_Throws(string raw)
    {
        // Act / Assert
        Should.Throw<UserErrorException>(() => LocationParser.ParsePosition(raw, ToolName));
    }

    [Fact]
    public void ParsePosition_NumericOverflow_Throws()
    {
        // Act / Assert — 99999999999 exceeds int range
        Should.Throw<UserErrorException>(() => LocationParser.ParsePosition("Foo.cs:99999999999", ToolName));
    }

    [Fact]
    public void ParseFile_TrailingNonInt_TreatedAsPath()
    {
        // Act — "Foo.cs:abc" — 'abc' is not all digits, so the whole thing is the path
        FileLocation file = LocationParser.ParseFile("Foo.cs:abc", ToolName);

        // Assert
        file.FilePath.ShouldBe("Foo.cs:abc");
    }

    [Fact]
    public void ParseFile_MixedTrailingSegment_TreatedAsPath()
    {
        // Act — "Foo.cs:42abc" — '42abc' is not all digits → entire string is path
        FileLocation file = LocationParser.ParseFile("Foo.cs:42abc", ToolName);

        // Assert
        file.FilePath.ShouldBe("Foo.cs:42abc");
    }

    [Fact]
    public void ParseFile_TrailingColon_IsNotALineMarker()
    {
        // Act — "Foo.cs:" has empty suffix; treat as path (not line)
        FileLocation file = LocationParser.ParseFile("Foo.cs:", ToolName);

        // Assert
        file.FilePath.ShouldBe("Foo.cs:");
    }
}
