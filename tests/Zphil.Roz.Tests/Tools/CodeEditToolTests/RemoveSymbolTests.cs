using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

public class RemoveSymbolTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task RemoveSymbol_Property_RemovesFromDisk()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act
        string result = await tools.RemoveSymbol(circleFile, "Radius", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Removed");
        result.ShouldContain("Radius");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("Radius { get; }");
        content.ShouldContain("Area");
        content.ShouldContain("Perimeter");
    }

    [Fact]
    public async Task RemoveSymbol_Method_RemovesFromDisk()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        // Act
        string result = await tools.RemoveSymbol(serviceFile, "ProcessShape", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Removed");
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("ProcessShape");
        content.ShouldContain("GetLargest");
        content.ShouldContain("Format");
    }

    [Fact]
    public async Task RemoveSymbol_WithDocComments_RemovesDocsToo()
    {
        // Arrange — ProcessShape has a multi-line XML doc comment
        CodeEditTools tools = CreateEditTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        // Act
        await tools.RemoveSymbol(serviceFile, "ProcessShape", ct: TestContext.Current.CancellationToken);

        // Assert — doc comments should be gone too
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("Processes a shape");
        content.ShouldNotContain("ProcessShape");
        // GetLargest doc comment should still be there
        content.ShouldContain("Returns the shape with the largest area");
    }

    [Fact]
    public async Task RemoveSymbol_MiddleMember_NoDoubleBlankLines()
    {
        // Arrange — Circle: Radius, blank, Area, Perimeter. Remove Area (middle).
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act
        await tools.RemoveSymbol(circleFile, "Area", ct: TestContext.Current.CancellationToken);

        // Assert — no triple-blank-line runs (double blank lines)
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Radius");
        content.ShouldContain("Perimeter");
        content.ShouldNotContain("Area");
        string[] lines = SplitLines(content);
        for (var i = 0; i < lines.Length - 2; i++)
        {
            bool tripleBlank = String.IsNullOrWhiteSpace(lines[i])
                               && String.IsNullOrWhiteSpace(lines[i + 1])
                               && String.IsNullOrWhiteSpace(lines[i + 2]);
            tripleBlank.ShouldBeFalse($"Found triple blank line at line {i + 1}");
        }
    }

    [Fact]
    public async Task RemoveSymbol_ByName_OverloadedMethod_RemovesFirstOverload()
    {
        // Arrange — ShapeService has 3 Format overloads; name-based targets the first
        CodeEditTools tools = CreateEditTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        // Act — name-based resolution removes the first overload
        string result = await tools.RemoveSymbol(serviceFile, "Format", ct: TestContext.Current.CancellationToken);

        // Assert — first overload (1-param) gone, other two remain
        result.ShouldContain("Removed");
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("Format(IShape shape) =>");
        content.ShouldContain("includePerimeter");
        content.ShouldContain("prefix");
    }

    [Fact]
    public async Task RemoveSymbol_LfOnlyFile_RemovesSymbolWithoutLeavingBlankLine()
    {
        // Arrange — convert Circle.cs to LF-only; the removal path for LF line endings
        // increments endPos by 1 (ch == '\n' branch) instead of 2 (\r\n branch)
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, content => content.Replace("\r\n", "\n"));

        CodeEditTools tools = CreateEditTools(Fixture);

        // Act
        string result = await tools.RemoveSymbol(circleFile, "Radius", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Removed");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("Radius { get; }");
        content.ShouldNotContain("\r\n");
    }

    [Fact]
    public async Task RemoveSymbol_NonExistentSymbol_Throws()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.RemoveSymbol(CircleFile(Fixture), "NonExistentSymbol"));

        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task RemoveSymbol_LastEnumMember_NoTrailingCommaArtifact()
    {
        // Arrange — ShapeColor enum: Red, Green, Blue, Yellow
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = TypeKindExamplesFile(Fixture);

        // Act — remove Yellow (last member)
        string result = await tools.RemoveSymbol(file, "Yellow", ct: TestContext.Current.CancellationToken);

        // Assert — Yellow gone, no stray comma, enum is valid
        result.ShouldContain("Removed");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldNotContain("Yellow");
        content.ShouldContain("Red");
        content.ShouldContain("Green");
        content.ShouldContain("Blue");
        // No orphan comma line
        string[] lines = SplitLines(content);
        lines.ShouldNotContain(l => l.Trim() == ",", "Should not have an orphan comma line");
    }

    [Fact]
    public async Task RemoveSymbol_MiddleEnumMember_NoStrayComma()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = TypeKindExamplesFile(Fixture);

        // Act — remove Green (middle member)
        string result = await tools.RemoveSymbol(file, "Green", ct: TestContext.Current.CancellationToken);

        // Assert — Green gone, remaining members properly separated
        result.ShouldContain("Removed");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldNotContain("Green");
        content.ShouldContain("Red");
        content.ShouldContain("Blue");
        content.ShouldContain("Yellow");
        string[] lines = SplitLines(content);
        lines.ShouldNotContain(l => l.Trim() == ",", "Should not have an orphan comma line");
    }

    [Fact]
    public async Task RemoveSymbol_EnumMemberWithValue_RemovesCleanly()
    {
        // Arrange — give Blue a value assignment
        string file = TypeKindExamplesFile(Fixture);
        await RewriteFileAsync(Fixture, file, c => c.Replace("Blue,", "Blue = 5,"));
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act
        string result = await tools.RemoveSymbol(file, "Blue", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Removed");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldNotContain("Blue");
        content.ShouldContain("Red");
        content.ShouldContain("Yellow");
        string[] lines = SplitLines(content);
        lines.ShouldNotContain(l => l.Trim() == ",", "Should not have an orphan comma line");
    }

    [Fact]
    public async Task RemoveSymbol_LastEnumMemberWithTrailingComma_PreservesTrailingComma()
    {
        // Arrange — mutate ShapeColor so Yellow has a trailing comma (ToCrlf first — Linux checks out LF)
        string file = TypeKindExamplesFile(Fixture);
        await RewriteFileAsync(Fixture, file, c => ToCrlf(c).Replace("    Yellow\r\n", "    Yellow,\r\n"));
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — remove the last member
        string result = await tools.RemoveSymbol(file, "Yellow", ct: TestContext.Current.CancellationToken);

        // Assert — Blue should now be the last member AND still have a trailing comma
        result.ShouldContain("Removed");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldNotContain("Yellow");
        content.ShouldContain("Blue,");
    }

    [Fact]
    public async Task RemoveSymbol_InsertRemoveRoundTrip_EnumPreservesOriginalFormat()
    {
        // Arrange — add a trailing comma to match the Justify.cs pattern from the bug report
        // (ToCrlf first — Linux checks out LF, so the CRLF-keyed Replace would otherwise silently no-op)
        string file = TypeKindExamplesFile(Fixture);
        await RewriteFileAsync(Fixture, file, c => ToCrlf(c).Replace("    Yellow\r\n", "    Yellow,\r\n"));
        string original = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert a new last member, then remove it
        await tools.InsertSymbol(file, "Yellow", "    Purple,", ct: TestContext.Current.CancellationToken);
        await tools.RemoveSymbol(file, "Purple", ct: TestContext.Current.CancellationToken);

        // Assert — byte-for-byte round trip
        string afterRoundTrip = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        afterRoundTrip.ShouldBe(original);
    }

    [Fact]
    public async Task RemoveSymbol_EnumMemberWithDocComment_RemovesDocCommentToo()
    {
        // Arrange — add doc comment to Green
        string file = TypeKindExamplesFile(Fixture);
        await RewriteFileAsync(Fixture, file, c =>
            c.Replace("    Green,", "    /// <summary>\n    /// The green color\n    /// </summary>\n    Green,"));
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act
        string result = await tools.RemoveSymbol(file, "Green", ct: TestContext.Current.CancellationToken);

        // Assert — both doc comment and member gone
        result.ShouldContain("Removed");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldNotContain("Green");
        content.ShouldNotContain("The green color");
        content.ShouldContain("Red");
        content.ShouldContain("Blue");
    }

    [Fact]
    public async Task RemoveSymbol_WithDocComment_LineCountIncludesDocComment()
    {
        // Arrange — add a 3-line doc comment to Radius property
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, c =>
            c.Replace("    public double Radius",
                "    /// <summary>\r\n    /// The radius of the circle.\r\n    /// </summary>\r\n    public double Radius"));
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act
        string result = await tools.RemoveSymbol(circleFile, "Radius", ct: TestContext.Current.CancellationToken);

        // Assert — line count should include the 3 doc comment lines + 1 property line
        // The blank separator line between { and the doc comment may also be counted
        result.ShouldSatisfyAllConditions(
            () => result.ShouldContain("Removed"),
            () => result.ShouldContain("lines)"),
            // At minimum 4 lines (3 doc + 1 property), may include separator
            () => result.ShouldNotContain("(1 lines)"),
            () => result.ShouldNotContain("(2 lines)"),
            () => result.ShouldNotContain("(3 lines)"));
    }

    [Fact]
    public async Task RemoveSymbol_ResultFormat_ContainsExpectedFields()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act
        string result = await tools.RemoveSymbol(circleFile, "Radius", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Removed");
        result.ShouldContain("Radius");
        result.ShouldContain("lines");
        result.ShouldContain("Circle.cs");
    }

    [Fact]
    public async Task RemoveSymbol_FirstMember_PreservesCopyrightHeader()
    {
        // Arrange — prepend a copyright header to Circle.cs
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, c => CopyrightHeader + c);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — remove Radius (first member)
        string result = await tools.RemoveSymbol(circleFile, "Radius", ct: TestContext.Current.CancellationToken);

        // Assert — copyright header survives, member is gone
        result.ShouldContain("Removed");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Copyright (c) Test Corp");
        content.ShouldContain("Licensed under the MIT License");
        content.ShouldNotContain("Radius { get; }");
        content.ShouldContain("Area");
        content.ShouldContain("Perimeter");
    }

    [Fact]
    public async Task RemoveSymbol_FirstMemberWithDocComment_PreservesCopyrightAndRemovesDocComment()
    {
        // Arrange — add copyright header and doc comment on first member
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, c => CopyrightHeader + c);
        await RewriteFileAsync(Fixture, circleFile, c =>
            c.Replace("    public double Radius", "    /// <summary>\r\n    /// The radius of the circle.\r\n    /// </summary>\r\n    public double Radius"));
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — remove Radius (first member, now has doc comment)
        string result = await tools.RemoveSymbol(circleFile, "Radius", ct: TestContext.Current.CancellationToken);

        // Assert — copyright preserved, doc comment removed with member
        result.ShouldContain("Removed");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Copyright (c) Test Corp");
        content.ShouldNotContain("The radius of the circle");
        content.ShouldNotContain("Radius { get; }");
        content.ShouldContain("Area");
    }

    [Fact]
    public async Task RemoveSymbol_InsertThenRemoveRoundTrip_PreservesCopyrightHeader()
    {
        // Arrange — the original reproduction: insert after first member, then remove inserted member
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, c => CopyrightHeader + c);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert a new member, then remove it
        await tools.InsertSymbol(circleFile, "Radius", "\n    public string TestProperty => \"hello\";\n", ct: TestContext.Current.CancellationToken);
        await tools.RemoveSymbol(circleFile, "TestProperty", ct: TestContext.Current.CancellationToken);

        // Assert — copyright header survives the round-trip
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Copyright (c) Test Corp");
        content.ShouldContain("Licensed under the MIT License");
        content.ShouldNotContain("TestProperty");
        content.ShouldContain("Radius");
        content.ShouldContain("Area");
    }

    [Fact]
    public async Task RemoveSymbol_InsertAfterThenRemove_RestoresOriginalContent()
    {
        // Arrange — read baseline before any edits
        string circleFile = CircleFile(Fixture);
        string baseline = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert between Radius and Area (next sibling has blank line), then remove
        await tools.InsertSymbol(circleFile, "Radius", "public double Diameter => Radius * 2;", ct: TestContext.Current.CancellationToken);
        await tools.RemoveSymbol(circleFile, "Diameter", ct: TestContext.Current.CancellationToken);

        // Assert — file should be byte-for-byte identical to baseline
        string afterRoundTrip = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        afterRoundTrip.ShouldBe(baseline, "insert_after + remove round-trip should restore original content");
    }

    [Fact]
    public async Task RemoveSymbol_InsertAfterLastMemberThenRemove_RestoresOriginalContent()
    {
        // Arrange — read baseline before any edits
        string circleFile = CircleFile(Fixture);
        string baseline = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert after Perimeter (last member, no next sibling), then remove
        await tools.InsertSymbol(circleFile, "Perimeter", "public double Diameter => Radius * 2;", ct: TestContext.Current.CancellationToken);
        await tools.RemoveSymbol(circleFile, "Diameter", ct: TestContext.Current.CancellationToken);

        // Assert — file should be byte-for-byte identical to baseline
        string afterRoundTrip = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        afterRoundTrip.ShouldBe(baseline, "insert_after last member + remove round-trip should restore original content");
    }

    [Fact]
    public async Task RemoveSymbol_InsertAfterDocCommentedMemberThenRemove_RestoresOriginalContent()
    {
        // Arrange — Shape.cs members have /// <inheritdoc /> doc comments
        string shapeFile = ShapeFile(Fixture);
        string baseline = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert after Area (which has doc comment + blank line before Perimeter), then remove
        await tools.InsertSymbol(shapeFile, "Area", "public double Diameter { get; }", ct: TestContext.Current.CancellationToken);
        await tools.RemoveSymbol(shapeFile, "Diameter", ct: TestContext.Current.CancellationToken);

        // Assert — file should be byte-for-byte identical to baseline
        string afterRoundTrip = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        afterRoundTrip.ShouldBe(baseline, "insert_after doc-commented member + remove round-trip should restore original content");
    }

    [Fact]
    public async Task RemoveSymbol_InsertReplaceRemoveSequence_PreservesCopyrightHeader()
    {
        // Arrange — exact reproduction from bug report: insert → replace → remove sequence
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, c => CopyrightHeader + c);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert after first member, replace the inserted member, then remove it
        await tools.InsertSymbol(circleFile, "Radius", "public string TestProp => \"hello\";", ct: TestContext.Current.CancellationToken);
        await tools.ReplaceSymbol(circleFile, "TestProp", "public string TestProp => \"replaced\";", ct: TestContext.Current.CancellationToken);
        await tools.RemoveSymbol(circleFile, "TestProp", ct: TestContext.Current.CancellationToken);

        // Assert — copyright header survives the full insert → replace → remove sequence
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Copyright (c) Test Corp");
        content.ShouldContain("Licensed under the MIT License");
        content.ShouldNotContain("TestProp");
        content.ShouldContain("Radius");
        content.ShouldContain("Area");
    }

    [Fact]
    public async Task RemoveSymbol_PreservesBlankLineSeparator()
    {
        // Arrange — Circle has: Radius (line 5), blank (line 6), Area (line 7), Perimeter (line 8).
        // Removing Area should preserve the blank line between Radius and the remaining Perimeter.
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act
        await tools.RemoveSymbol(circleFile, "Area", ct: TestContext.Current.CancellationToken);

        // Assert — there should still be a blank line between Radius and Perimeter
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("Area");
        content.ShouldContain("Radius");
        content.ShouldContain("Perimeter");

        // Verify structure: Radius line, then blank line, then Perimeter line
        string[] lines = SplitLines(content);
        int radiusLine = Array.FindIndex(lines, l => l.Contains("Radius"));
        int perimeterLine = Array.FindIndex(lines, l => l.Contains("Perimeter"));
        radiusLine.ShouldBeGreaterThan(-1);
        perimeterLine.ShouldBeGreaterThan(-1);
        (perimeterLine - radiusLine).ShouldBeGreaterThanOrEqualTo(2,
            "There should be at least one blank line between Radius and Perimeter");
    }

    [Fact]
    public async Task RemoveSymbol_InsertReplaceRemoveWithUsings_PreservesCopyrightHeader()
    {
        // Arrange — exact ILSpy reproduction: tab-indented, K&R braces, doc comments,
        // copyright header + usings + block namespace, insert→replace→remove on first member
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, _ => CustomLanguageCs);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — mirror the ILSpy sequence: insert after FileExtension, replace, remove
        var inserted = "\t\tpublic string ProjectFileExtension {\r\n\t\t\tget {\r\n\t\t\t\treturn \".proj\";\r\n\t\t\t}\r\n\t\t}";
        await tools.InsertSymbol(circleFile, "FileExtension", inserted, ct: TestContext.Current.CancellationToken);
        var replaced = "\t\tpublic string ProjectFileExtension {\r\n\t\t\tget {\r\n\t\t\t\treturn \".csproj\";\r\n\t\t\t}\r\n\t\t}";
        await tools.ReplaceSymbol(circleFile, "ProjectFileExtension", replaced, ct: TestContext.Current.CancellationToken);
        await tools.RemoveSymbol(circleFile, "ProjectFileExtension", ct: TestContext.Current.CancellationToken);

        // Assert — copyright header survives the full sequence
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("AlphaSierraPapa");
        content.ShouldContain("MIT X11 license");
        content.ShouldContain("using System;");
        content.ShouldNotContain("ProjectFileExtension");
        content.ShouldContain("Name");
        content.ShouldContain("FileExtension");
        content.ShouldContain("Describe");
    }

    [Fact]
    public async Task RemoveSymbol_InsertReplaceRemoveNonFirstMemberWithUsings_PreservesCopyrightHeader()
    {
        // Arrange — same file, but target a non-first member (FileExtension is 2nd property)
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, _ => CustomLanguageCs);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert after FileExtension (2nd property), replace, then remove
        var inserted = "\t\tpublic string TestProp {\r\n\t\t\tget {\r\n\t\t\t\treturn \"test\";\r\n\t\t\t}\r\n\t\t}";
        await tools.InsertSymbol(circleFile, "FileExtension", inserted, ct: TestContext.Current.CancellationToken);
        var replaced = "\t\tpublic string TestProp {\r\n\t\t\tget {\r\n\t\t\t\treturn \"replaced\";\r\n\t\t\t}\r\n\t\t}";
        await tools.ReplaceSymbol(circleFile, "TestProp", replaced, ct: TestContext.Current.CancellationToken);
        await tools.RemoveSymbol(circleFile, "TestProp", ct: TestContext.Current.CancellationToken);

        // Assert — copyright header survives
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("AlphaSierraPapa");
        content.ShouldContain("MIT X11 license");
        content.ShouldContain("using System;");
        content.ShouldNotContain("TestProp");
        content.ShouldContain("Name");
        content.ShouldContain("FileExtension");
        content.ShouldContain("Describe");
    }

    [Fact]
    public async Task RemoveSymbol_NonFirstMemberWithUsings_PreservesCopyrightHeader()
    {
        // Arrange — isolate remove_symbol alone on the ILSpy-style file
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, _ => CustomLanguageCs);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — remove a non-first member
        await tools.RemoveSymbol(circleFile, "FileExtension", ct: TestContext.Current.CancellationToken);

        // Assert — copyright header survives
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("AlphaSierraPapa");
        content.ShouldContain("MIT X11 license");
        content.ShouldContain("using System;");
        content.ShouldNotContain("FileExtension");
        content.ShouldContain("Name");
        content.ShouldContain("Describe");
    }
}
