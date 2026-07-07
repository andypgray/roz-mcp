using Zphil.Roz.Symbols;

namespace Zphil.Roz.Tests.Symbols;

/// <summary>
///     Tests for the <see cref="LocationFormat.Format" /> helper — including a round-trip
///     check against the typed <see cref="LocationParser" /> methods so the formatter's
///     output stays parseable.
/// </summary>
public class LocationFormatTests
{
    [Theory]
    [InlineData("Foo.cs", null, null, "Foo.cs")]
    [InlineData("Foo.cs", 42, null, "Foo.cs:42")]
    [InlineData("Foo.cs", 42, 18, "Foo.cs:42:18")]
    [InlineData("C:\\src\\Foo.cs", 42, 18, "C:\\src\\Foo.cs:42:18")]
    public void Format_ProducesExpectedString(string path, int? line, int? column, string expected)
    {
        // Act
        string formatted = LocationFormat.Format(path, line, column);

        // Assert
        formatted.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Foo.cs", null, null)]
    [InlineData("Foo.cs", 42, null)]
    [InlineData("Foo.cs", 42, 18)]
    [InlineData("C:\\src\\Foo.cs", 42, 18)]
    public void RoundTrip_FormatThenParse_PreservesParts(string path, int? line, int? column)
    {
        // Arrange
        string formatted = LocationFormat.Format(path, line, column);

        // Act / Assert — dispatch to the typed parser matching the formatted shape
        if (line is null)
        {
            FileLocation parsed = LocationParser.ParseFile(formatted, "format-roundtrip");
            parsed.FilePath.ShouldBe(path);
        }
        else if (column is null)
        {
            PositionLocation parsed = LocationParser.ParsePosition(formatted, "format-roundtrip");
            LineLocation lineLoc = parsed.ShouldBeOfType<LineLocation>();
            lineLoc.FilePath.ShouldBe(path);
            lineLoc.Line.ShouldBe(line.Value);
        }
        else
        {
            PositionLocation parsed = LocationParser.ParsePosition(formatted, "format-roundtrip");
            CursorLocation cursor = parsed.ShouldBeOfType<CursorLocation>();
            cursor.FilePath.ShouldBe(path);
            cursor.Line.ShouldBe(line.Value);
            cursor.Column.ShouldBe(column.Value);
        }
    }
}
