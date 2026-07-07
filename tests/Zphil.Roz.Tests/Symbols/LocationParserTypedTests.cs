using Zphil.Roz.Symbols;

namespace Zphil.Roz.Tests.Symbols;

/// <summary>
///     Tests for the per-tool typed parse entry points
///     (<see cref="LocationParser.ParseFile" />, <see cref="LocationParser.ParsePosition" />,
///     <see cref="LocationParser.ParseFileOrCursor" />): success returns the requested
///     subtype, rejection produces a tool-name-tagged error message.
/// </summary>
public class LocationParserTypedTests
{
    // ── ParseFile ────────────────────────────────────────────────────────

    [Fact]
    public void ParseFile_PathOnly_ReturnsFileLocation()
    {
        // Act
        FileLocation loc = LocationParser.ParseFile("Foo.cs", "add_usings");

        // Assert
        loc.FilePath.ShouldBe("Foo.cs");
    }

    [Theory]
    [InlineData("Foo.cs:42")]
    [InlineData("Foo.cs:42:18")]
    public void ParseFile_HasLineOrColumn_Throws(string raw)
    {
        // Act / Assert
        UserErrorException ex = Should.Throw<UserErrorException>(() => LocationParser.ParseFile(raw, "add_usings"));
        ex.Message.ShouldContain("add_usings", Case.Sensitive);
    }

    // ── ParsePosition ────────────────────────────────────────────────────

    [Fact]
    public void ParsePosition_PathOnly_Throws()
    {
        // Act / Assert
        UserErrorException ex = Should.Throw<UserErrorException>(() => LocationParser.ParsePosition("Foo.cs", "find_references"));
        ex.Message.ShouldContain(":line", Case.Sensitive);
        ex.Message.ShouldContain("find_references", Case.Sensitive);
    }

    [Fact]
    public void ParsePosition_LineOnly_ReturnsLineLocation()
    {
        // Act
        PositionLocation loc = LocationParser.ParsePosition("Foo.cs:42", "find_references");

        // Assert
        LineLocation line = loc.ShouldBeOfType<LineLocation>();
        line.FilePath.ShouldBe("Foo.cs");
        line.Line.ShouldBe(42);
    }

    [Fact]
    public void ParsePosition_FullPosition_ReturnsCursorLocation()
    {
        // Act
        PositionLocation loc = LocationParser.ParsePosition("Foo.cs:42:18", "find_references");

        // Assert
        CursorLocation cursor = loc.ShouldBeOfType<CursorLocation>();
        cursor.FilePath.ShouldBe("Foo.cs");
        cursor.Line.ShouldBe(42);
        cursor.Column.ShouldBe(18);
    }

    // ── ParseFileOrCursor (treatLineOnlyAsFile=false) ────────────────────

    [Fact]
    public void ParseFileOrCursor_Strict_PathOnly_ReturnsFileLocation()
    {
        // Act
        LocationArg loc = LocationParser.ParseFileOrCursor("Foo.cs", "rename_symbol", false);

        // Assert
        FileLocation file = loc.ShouldBeOfType<FileLocation>();
        file.FilePath.ShouldBe("Foo.cs");
    }

    [Fact]
    public void ParseFileOrCursor_Strict_FullPosition_ReturnsCursorLocation()
    {
        // Act
        LocationArg loc = LocationParser.ParseFileOrCursor("Foo.cs:42:18", "rename_symbol", false);

        // Assert
        CursorLocation cursor = loc.ShouldBeOfType<CursorLocation>();
        cursor.FilePath.ShouldBe("Foo.cs");
        cursor.Line.ShouldBe(42);
        cursor.Column.ShouldBe(18);
    }

    [Fact]
    public void ParseFileOrCursor_Strict_LineOnly_Throws()
    {
        // Act / Assert — strict rejects the ambiguous half-cursor
        UserErrorException ex = Should.Throw<UserErrorException>(() => LocationParser.ParseFileOrCursor("Foo.cs:42", "rename_symbol", false));
        ex.Message.ShouldContain("rename_symbol", Case.Sensitive);
        ex.Message.ShouldContain("path:line", Case.Sensitive);
    }

    // ── ParseFileOrCursor (treatLineOnlyAsFile=true) ─────────────────────

    [Fact]
    public void ParseFileOrCursor_Lenient_LineOnly_NormalizesToFileLocation()
    {
        // Act — edit_symbol with symbolName supplied: bare path:line silently drops the line.
        LocationArg loc = LocationParser.ParseFileOrCursor("Foo.cs:42", "edit_symbol", true);

        // Assert
        FileLocation file = loc.ShouldBeOfType<FileLocation>();
        file.FilePath.ShouldBe("Foo.cs");
    }

    [Fact]
    public void ParseFileOrCursor_Lenient_PathOnly_ReturnsFileLocation()
    {
        // Act
        LocationArg loc = LocationParser.ParseFileOrCursor("Foo.cs", "edit_symbol", true);

        // Assert
        loc.ShouldBeOfType<FileLocation>().FilePath.ShouldBe("Foo.cs");
    }

    [Fact]
    public void ParseFileOrCursor_Lenient_FullPosition_ReturnsCursorLocation()
    {
        // Act
        LocationArg loc = LocationParser.ParseFileOrCursor("Foo.cs:42:18", "edit_symbol", true);

        // Assert
        CursorLocation cursor = loc.ShouldBeOfType<CursorLocation>();
        cursor.Line.ShouldBe(42);
        cursor.Column.ShouldBe(18);
    }
}
