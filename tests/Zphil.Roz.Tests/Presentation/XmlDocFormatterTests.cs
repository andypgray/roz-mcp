using Zphil.Roz.Presentation;

namespace Zphil.Roz.Tests.Presentation;

/// <summary>
///     Tests for <see cref="XmlDocFormatter" /> XML documentation parsing and formatting.
/// </summary>
public class XmlDocFormatterTests
{
    // ── Format (full) ────────────────────────────────────────────────────

    [Fact]
    public void Format_WithSummary_ReturnsSummaryText()
    {
        // Arrange
        var xml = """
                  <member name="T:MyNamespace.MyClass">
                      <summary>This is a summary.</summary>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("This is a summary.");
    }

    [Fact]
    public void Format_WithParams_FormatsParameterList()
    {
        // Arrange
        var xml = """
                  <member name="M:MyClass.MyMethod(System.String,System.Int32)">
                      <summary>Does something.</summary>
                      <param name="name">The name.</param>
                      <param name="count">The count.</param>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("Parameters:");
        result.ShouldContain("name \u2014 The name.");
        result.ShouldContain("count \u2014 The count.");
    }

    [Fact]
    public void Format_WithReturns_FormatsReturnDescription()
    {
        // Arrange
        var xml = """
                  <member name="M:MyClass.GetValue">
                      <summary>Gets a value.</summary>
                      <returns>The computed value.</returns>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("Returns: The computed value.");
    }

    [Fact]
    public void Format_WithException_FormatsExceptionInfo()
    {
        // Arrange
        var xml = """
                  <member name="M:MyClass.Validate">
                      <summary>Validates input.</summary>
                      <exception cref="T:System.ArgumentNullException">Thrown when input is null.</exception>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("Exceptions:");
        result.ShouldContain("ArgumentNullException \u2014 Thrown when input is null.");
    }

    [Fact]
    public void Format_WithRemarks_FormatsRemarks()
    {
        // Arrange
        var xml = """
                  <member name="T:MyClass">
                      <summary>A class.</summary>
                      <remarks>This class is thread-safe.</remarks>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("Remarks: This class is thread-safe.");
    }

    [Fact]
    public void Format_WithTypeParams_FormatsTypeParameters()
    {
        // Arrange
        var xml = """
                  <member name="T:MyClass`1">
                      <summary>A generic class.</summary>
                      <typeparam name="T">The element type.</typeparam>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("Type parameters:");
        result.ShouldContain("T \u2014 The element type.");
    }

    [Fact]
    public void Format_WithSeeAlso_FormatsSeeAlsoReferences()
    {
        // Arrange
        var xml = """
                  <member name="T:MyClass">
                      <summary>A class.</summary>
                      <seealso cref="T:System.String"/>
                      <seealso cref="T:MyNamespace.OtherClass"/>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("See also: String, OtherClass");
    }

    // ── Inner tag handling ───────────────────────────────────────────────

    [Fact]
    public void Format_WithSeeCref_ExtractsTypeName()
    {
        // Arrange
        var xml = """
                  <member name="M:MyClass.Get">
                      <summary>Returns a <see cref="T:System.String"/> value.</summary>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("Returns a String value.");
    }

    [Fact]
    public void Format_WithSeeLangword_ExtractsKeyword()
    {
        // Arrange
        var xml = """
                  <member name="M:MyClass.Get">
                      <summary>Returns <see langword="null"/> if not found.</summary>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("Returns null if not found.");
    }

    [Fact]
    public void Format_WithParamRef_ExtractsParamName()
    {
        // Arrange
        var xml = """
                  <member name="M:MyClass.Process(System.String)">
                      <summary>Processes the given input.</summary>
                      <param name="input">The input value.</param>
                      <exception cref="T:System.ArgumentNullException">
                          Thrown when <paramref name="input"/> is null.
                      </exception>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("Thrown when input is null.");
    }

    [Fact]
    public void Format_WithCodeTag_PreservesContent()
    {
        // Arrange
        var xml = """
                  <member name="M:MyClass.Parse">
                      <summary>Parses a value like <c>int.Parse("42")</c>.</summary>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("int.Parse(\"42\")");
    }

    // ── Edge cases ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<not valid xml")]
    public void Format_InvalidInput_ReturnsNull(string? xml) => XmlDocFormatter.Format(xml).ShouldBeNull();

    [Fact]
    public void Format_EmptyMember_ReturnsNull()
    {
        var xml = """<member name="T:MyClass"></member>""";
        XmlDocFormatter.Format(xml).ShouldBeNull();
    }

    [Fact]
    public void Format_NormalizesWhitespace()
    {
        // Arrange — typical indented XML doc comment with lots of whitespace
        var xml = """
                  <member name="T:MyClass">
                      <summary>
                          This is a
                          multi-line summary
                          with extra whitespace.
                      </summary>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("This is a multi-line summary with extra whitespace.");
    }

    // ── FormatSummaryOnly ────────────────────────────────────────────────

    [Fact]
    public void FormatSummaryOnly_ReturnsSummaryText()
    {
        // Arrange
        var xml = """
                  <member name="M:MyClass.DoWork">
                      <summary>Does important work.</summary>
                      <param name="x">Not included.</param>
                      <returns>Not included either.</returns>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.FormatSummaryOnly(xml);

        // Assert
        result.ShouldBe("Does important work.");
    }

    [Fact]
    public void FormatSummaryOnly_NullInput_ReturnsNull() => XmlDocFormatter.FormatSummaryOnly((string?)null).ShouldBeNull();

    [Fact]
    public void FormatSummaryOnly_NoSummaryElement_ReturnsNull()
    {
        var xml = """
                  <member name="M:MyClass.DoWork">
                      <returns>Something.</returns>
                  </member>
                  """;
        XmlDocFormatter.FormatSummaryOnly(xml).ShouldBeNull();
    }

    [Fact]
    public void FormatSummaryOnly_EmptySummary_ReturnsNull()
    {
        var xml = """
                  <member name="M:MyClass.DoWork">
                      <summary>   </summary>
                  </member>
                  """;
        XmlDocFormatter.FormatSummaryOnly(xml).ShouldBeNull();
    }

    // ── InheritDoc (string-level) ────────────────────────────────────────

    [Fact]
    public void Format_InheritDocOnlyXml_ReturnsNull()
    {
        // When the raw XML contains only <inheritdoc/>, Format(string) can't resolve it
        var xml = """
                  <member name="M:MyClass.DoWork">
                      <inheritdoc/>
                  </member>
                  """;
        XmlDocFormatter.Format(xml).ShouldBeNull();
    }

    // ── Empty text elements ──────────────────────────────────────────────

    [Fact]
    public void Format_EmptyReturnsElement_OmitsReturnsSection()
    {
        // Arrange
        var xml = """
                  <member name="M:MyClass.Get">
                      <summary>Gets a value.</summary>
                      <returns>   </returns>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldNotContain("Returns:");
    }

    [Fact]
    public void Format_EmptyRemarksElement_OmitsRemarksSection()
    {
        // Arrange
        var xml = """
                  <member name="M:MyClass.Get">
                      <summary>Gets a value.</summary>
                      <remarks>
                      </remarks>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldNotContain("Remarks:");
    }

    // ── Value and Example elements ───────────────────────────────────────

    [Fact]
    public void Format_WithValueElement_FormatsValueSection()
    {
        // Arrange
        var xml = """
                  <member name="P:MyClass.Name">
                      <summary>Gets the name.</summary>
                      <value>A non-null string representing the name.</value>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("Value: A non-null string representing the name.");
    }

    [Fact]
    public void Format_WithExampleElement_FormatsExampleSection()
    {
        // Arrange
        var xml = """
                  <member name="M:MyClass.Parse">
                      <summary>Parses input.</summary>
                      <example>MyClass.Parse("hello")</example>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("Example: MyClass.Parse(\"hello\")");
    }

    // ── Cref extraction ─────────────────────────────────────────────────

    [Fact]
    public void Format_MethodCref_ExtractsMethodName()
    {
        // Arrange
        var xml = """
                  <member name="T:MyClass">
                      <summary>See <see cref="M:System.String.Format(System.String,System.Object)"/>.</summary>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("See Format.");
    }

    [Fact]
    public void Format_AllSections_FormatsInOrder()
    {
        // Arrange
        var xml = """
                  <member name="M:MyClass.DoWork(System.String)">
                      <summary>Does work.</summary>
                      <typeparam name="T">The type.</typeparam>
                      <param name="input">The input.</param>
                      <returns>The result.</returns>
                      <exception cref="T:System.InvalidOperationException">When invalid.</exception>
                      <remarks>Be careful.</remarks>
                      <seealso cref="T:MyNamespace.Helper"/>
                  </member>
                  """;

        // Act
        string? result = XmlDocFormatter.Format(xml);

        // Assert
        result.ShouldNotBeNull();
        int summaryPos = result.IndexOf("Does work.", StringComparison.Ordinal);
        int typeParamPos = result.IndexOf("Type parameters:", StringComparison.Ordinal);
        int paramPos = result.IndexOf("Parameters:", StringComparison.Ordinal);
        int returnsPos = result.IndexOf("Returns:", StringComparison.Ordinal);
        int exceptionsPos = result.IndexOf("Exceptions:", StringComparison.Ordinal);
        int remarksPos = result.IndexOf("Remarks:", StringComparison.Ordinal);
        int seeAlsoPos = result.IndexOf("See also:", StringComparison.Ordinal);

        // All sections present and in order
        summaryPos.ShouldBeGreaterThanOrEqualTo(0);
        typeParamPos.ShouldBeGreaterThan(summaryPos);
        paramPos.ShouldBeGreaterThan(typeParamPos);
        returnsPos.ShouldBeGreaterThan(paramPos);
        exceptionsPos.ShouldBeGreaterThan(returnsPos);
        remarksPos.ShouldBeGreaterThan(exceptionsPos);
        seeAlsoPos.ShouldBeGreaterThan(remarksPos);
    }
}
