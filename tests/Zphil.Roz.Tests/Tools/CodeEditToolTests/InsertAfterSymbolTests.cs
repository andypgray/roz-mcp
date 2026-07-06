using System.Text;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

public class InsertAfterSymbolTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task InsertSymbol_After_AfterClass_ContentAppearsInFile()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert a comment after the Circle class closing brace
        string result = await tools.InsertSymbol(circleFile, "Circle", "\n// Appended after Circle\n", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Inserted");
        string fileContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("// Appended after Circle");
    }

    [Fact]
    public async Task InsertSymbol_After_AfterLastMember_ClosingBraceOnOwnLine()
    {
        // Arrange — Perimeter is the last property in Circle, followed by closing brace
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert after Perimeter WITHOUT trailing newline in content
        await tools.InsertSymbol(circleFile, "Perimeter", "    public double Diameter => Radius * 2;", ct: TestContext.Current.CancellationToken);

        // Assert — closing brace should be on its own line, not merged with inserted content
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int diameterLine = lines.ShouldContainLine(l => l.Contains("Diameter"));
        int closingBraceLine = Array.FindIndex(lines, diameterLine + 1, l => l.TrimStart().StartsWith("}"));
        closingBraceLine.ShouldBeGreaterThan(diameterLine,
            "Closing brace should be on a separate line after the inserted property");
    }


    [Fact]
    public async Task InsertSymbol_After_AfterTopLevelSymbol_NewContentStartsOnOwnLine()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert after Circle class WITHOUT leading newline in content
        await tools.InsertSymbol(circleFile, "Circle", "public static class CircleExtensions { }", ct: TestContext.Current.CancellationToken);

        // Assert — closing brace of Circle and the new class should be on separate lines
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int closingBraceLine = lines.ShouldContainLine(l => l.TrimStart() == "}");
        int extensionsLine = lines.ShouldContainLine(l => l.Contains("CircleExtensions"));
        extensionsLine.ShouldBeGreaterThan(closingBraceLine,
            "CircleExtensions should be on a separate line after Circle's closing brace");
    }


    [Fact]
    public async Task InsertSymbol_After_BetweenSiblings_NextSiblingOnOwnLine()
    {
        // Arrange — Area and Perimeter are adjacent (no blank line) in Circle.cs
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert between Area and Perimeter WITHOUT trailing newline
        await tools.InsertSymbol(circleFile, "Area", "    public double Diameter => Radius * 2;", ct: TestContext.Current.CancellationToken);

        // Assert — Perimeter should still start on its own line
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int diameterLine = lines.ShouldContainLine(l => l.Contains("Diameter"));
        int perimeterLine = lines.ShouldContainLine(l => l.Contains("Perimeter"));
        perimeterLine.ShouldBeGreaterThan(diameterLine,
            "Perimeter should be on a separate line after the inserted Diameter property");
    }

    [Fact]
    public async Task InsertSymbol_After_LfContent_NormalizesToCrlfFile()
    {
        // Arrange — Circle.cs uses CRLF
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await EnsureCrlfAsync(Fixture, circleFile);
        string original = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        original.ShouldContain("\r\n");

        // Act — insert content with LF-only newlines
        await tools.InsertSymbol(circleFile, "Radius", "\npublic double Diameter => Radius * 2;\n", ct: TestContext.Current.CancellationToken);

        // Assert — no bare LF should remain (every \n should be part of \r\n)
        string result = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        result.ShouldHaveNoBareLineFeed();
    }

    [Fact]
    public async Task InsertSymbol_After_LfOnlyFile_PreservesLfLineEndings()
    {
        // Arrange — convert Circle.cs to LF-only
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, content => content.Replace("\r\n", "\n"));

        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert content (also with LF)
        await tools.InsertSymbol(circleFile, "Radius", "\n    public double Diameter => Radius * 2;\n", ct: TestContext.Current.CancellationToken);

        // Assert — file should remain LF-only
        string result = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        result.ShouldHaveNoCrLf();
        result.ShouldContain("Diameter");
    }

    [Fact]
    public async Task InsertSymbol_After_FileWithBom_PreservesBom()
    {
        // Arrange — re-write Circle.cs with a UTF-8 BOM
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(circleFile, content, new UTF8Encoding(true), TestContext.Current.CancellationToken);
        await Fixture.WorkspaceManager.ReloadAsync(ct: TestContext.Current.CancellationToken);

        // Act — insert after Radius
        await tools.InsertSymbol(circleFile, "Radius", "    public double Diameter => Radius * 2;\n", ct: TestContext.Current.CancellationToken);

        // Assert — file should still start with BOM bytes
        await circleFile.ShouldHaveBomAsync();
        string result = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        result.ShouldContain("Diameter");
    }

    [Fact]
    public async Task InsertSymbol_After_TabContentIntoSpaceFile_InsertedContentPresent()
    {
        // Arrange — Circle.cs uses spaces
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert content with tab indentation into space-indented file
        await tools.InsertSymbol(circleFile, "Radius", "\tpublic double Diameter => Radius * 2;\n", ct: TestContext.Current.CancellationToken);

        // Assert — content should be present in the file
        string result = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        result.ShouldContain("public double Diameter");
        // The inserted content is present; note that insert tools use SourceText.WithChanges
        // which preserves the exact content (including tabs) as provided
    }

    [Fact]
    public async Task InsertSymbol_After_CrlfContentIntoLfFile_NormalizesToLf()
    {
        // Arrange — convert Circle.cs to LF-only
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, content => content.Replace("\r\n", "\n"));

        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert content with CRLF newlines into LF file
        await tools.InsertSymbol(circleFile, "Radius", "\r\npublic double Diameter => Radius * 2;\r\n", ct: TestContext.Current.CancellationToken);

        // Assert — NormalizeLineEndings should strip \r, keeping file LF-only
        string result = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        result.ShouldHaveNoCrLf();
        result.ShouldContain("Diameter");
    }

    [Fact]
    public async Task InsertSymbol_After_EmptyContent_ThrowsArgumentException()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.InsertSymbol(CircleFile(Fixture), "Area", ""));

        ex.Message.ShouldContain("empty");
    }

    [Fact]
    public async Task InsertSymbol_After_TypeInFileScopedNamespace_MatchesTargetIndentation()
    {
        // Arrange — Circle.cs uses file-scoped namespace, so Circle class is at column 0
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert a new class after the Circle class
        string result = await tools.InsertSymbol(circleFile, "Circle", "public static class CircleHelper { }", ct: TestContext.Current.CancellationToken);

        // Assert — inserted class should be at column 0, matching the Circle class
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int helperLine = lines.ShouldContainLine(l => l.Contains("public static class CircleHelper"));
        lines[helperLine].ShouldStartWith("public static class CircleHelper");
    }

    [Fact]
    public async Task InsertSymbol_After_MemberInTraditionalNamespace_NormalizationIsNoOp()
    {
        // Arrange — LegacyClass.cs uses traditional namespace { }; members at 8-space indent.
        // When Roslyn's formatter produces the correct indentation, NormalizeInsertedIndentationAsync
        // detects insertedIndent == targetIndent and returns early without any changes.
        CodeEditTools tools = CreateEditTools(Fixture);
        string legacyFile = LegacyClassFile(Fixture);

        // Act — insert a property after Name (which is at 8-space indent); Roslyn should
        // format the new property at the same 8-space indent without any normalization needed
        string result = await tools.InsertSymbol(legacyFile, "Name", "public int Score { get; set; }", ct: TestContext.Current.CancellationToken);

        // Assert — inserted property at correct 8-space indent and DoWork still follows
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(legacyFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);

        int scoreLine = lines.ShouldContainLine(l => l.Contains("public int Score"));
        lines[scoreLine].ShouldStartWith("        public int Score");

        int doWorkLine = lines.ShouldContainLine(l => l.Contains("public void DoWork()"));
        doWorkLine.ShouldBeGreaterThan(scoreLine, "DoWork should follow the inserted Score property");
    }

    [Fact]
    public async Task InsertSymbol_After_MultiLineMember_AllLinesReindented()
    {
        // Arrange — Circle.cs uses file-scoped namespace; Circle class is at column 0.
        // Insert a multi-line class after Circle. Roslyn formats it with 4-space indent;
        // NormalizeInsertedIndentationAsync must re-indent ALL lines including body lines to column 0.
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert a multi-line class after Circle
        string result = await tools.InsertSymbol(circleFile, "Circle", "public class CircleFactory\r\n{\r\n    public static Circle Create(double r) => new(r);\r\n\r\n    public static Circle Unit() => new(1.0);\r\n}", ct: TestContext.Current.CancellationToken);

        // Assert — every top-level line of the inserted class should be at column 0;
        // members inside the class should preserve their relative (4-space) indentation
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);

        int factoryLine = lines.ShouldContainLine(l => l.Contains("public class CircleFactory"));
        lines[factoryLine].ShouldStartWith("public class CircleFactory");

        int createLine = lines.ShouldContainLine(l => l.Contains("public static Circle Create"));
        lines[createLine].ShouldStartWith("    public static Circle Create");

        int unitLine = lines.ShouldContainLine(l => l.Contains("public static Circle Unit()"));
        lines[unitLine].ShouldStartWith("    public static Circle Unit()");
    }

    [Fact]
    public async Task InsertSymbol_After_EnumMemberAfterLast_InsertsWithComma()
    {
        // Arrange — ShapeColor enum: Red, Green, Blue, Yellow (no trailing comma on Yellow)
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = TypeKindExamplesFile(Fixture);

        // Act — insert Purple after Yellow (the last member)
        string result = await tools.InsertSymbol(file, "Yellow", "Purple", ct: TestContext.Current.CancellationToken);

        // Assert — Yellow should now have a trailing comma, Purple appears after it
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("Yellow,");
        string[] lines = SplitLines(content);
        int yellowLine = lines.ShouldContainLine(l => l.Trim() == "Yellow,");
        int purpleLine = lines.ShouldContainLine(l => l.Trim().StartsWith("Purple"));
        purpleLine.ShouldBeGreaterThan(yellowLine);
        // Enum should still be valid (closing brace present after Purple)
        int closingBrace = Array.FindIndex(lines, purpleLine, l => l.Trim() == "}");
        closingBrace.ShouldBeGreaterThan(purpleLine);
    }

    [Fact]
    public async Task InsertSymbol_After_EnumMemberAfterMiddle_InsertsCorrectly()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = TypeKindExamplesFile(Fixture);

        // Act — insert Orange after Green (middle member)
        string result = await tools.InsertSymbol(file, "Green", "Orange", ct: TestContext.Current.CancellationToken);

        // Assert — Orange appears between Green and Blue
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int greenLine = lines.ShouldContainLine(l => l.Trim().StartsWith("Green"));
        int orangeLine = lines.ShouldContainLine(l => l.Trim().StartsWith("Orange"));
        int blueLine = lines.ShouldContainLine(l => l.Trim().StartsWith("Blue"));
        orangeLine.ShouldBeGreaterThan(greenLine);
        orangeLine.ShouldBeLessThan(blueLine);
    }

    [Fact]
    public async Task InsertSymbol_After_EnumMemberWithValue_PreservesValueAssignment()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = TypeKindExamplesFile(Fixture);

        // Act — insert enum member with value assignment
        string result = await tools.InsertSymbol(file, "Blue", "Orange = 15", ct: TestContext.Current.CancellationToken);

        // Assert — value assignment is preserved in the output
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("Orange = 15");
    }

    [Fact]
    public async Task InsertSymbol_After_EnumMemberWithDocComment_InsertsCorrectly()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = TypeKindExamplesFile(Fixture);

        // Act — insert enum member with doc comment
        string result = await tools.InsertSymbol(file, "Yellow", "    /// <summary>\n    /// Purple color\n    /// </summary>\n    Purple", ct: TestContext.Current.CancellationToken);

        // Assert — both doc comment and member are present
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("Purple color");
        content.ShouldContain("Purple");
        string[] lines = SplitLines(content);
        int docLine = lines.ShouldContainLine(l => l.Contains("Purple color"));
        int purpleLine = lines.ShouldContainLine(l => l.Trim().StartsWith("Purple"));
        purpleLine.ShouldBeGreaterThan(docLine);
    }

    [Fact]
    public async Task InsertSymbol_After_MultiLineWithBlankLines_BlankLinesPreserved()
    {
        // Arrange — insert a class with blank lines between members, testing that
        // NormalizeInsertedIndentationAsync skips whitespace-only lines during re-indentation
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert a class with multiple blank lines between members
        string result = await tools.InsertSymbol(circleFile, "Circle", "public class Stats\r\n{\r\n    public int Count { get; set; }\r\n\r\n\r\n    public double Average { get; set; }\r\n}", ct: TestContext.Current.CancellationToken);

        // Assert — blank lines between members should be preserved
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);

        int countLine = lines.ShouldContainLine(l => l.Contains("public int Count"));
        int averageLine = lines.ShouldContainLine(l => l.Contains("public double Average"));
        (averageLine - countLine).ShouldBeGreaterThan(2,
            "Blank lines between members should be preserved during re-indentation");
    }

    [Fact]
    public async Task InsertSymbol_After_FirstMember_PreservesCopyrightHeader()
    {
        // Arrange — prepend a copyright header to Circle.cs
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, c =>
            "// Copyright (c) Test Corp. All rights reserved.\r\n// Licensed under the MIT License.\r\n\r\n" + c);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert after Radius (first member in the class)
        await tools.InsertSymbol(circleFile, "Radius", "public double Diameter => Radius * 2;", ct: TestContext.Current.CancellationToken);

        // Assert — copyright header survives
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Copyright (c) Test Corp");
        content.ShouldContain("Licensed under the MIT License");
        content.ShouldContain("Diameter");
        content.ShouldContain("Radius");
        content.ShouldContain("Area");
    }

    [Fact]
    public async Task InsertSymbol_After_FirstMemberWithUsings_PreservesCopyrightHeader()
    {
        // Arrange — exact ILSpy file structure: tab-indented, K&R braces, doc comments
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, _ => CustomLanguageCs);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert after FileExtension (matching the ILSpy bug sequence)
        var inserted = "\t\tpublic string ProjectFileExtension {\r\n\t\t\tget {\r\n\t\t\t\treturn \".proj\";\r\n\t\t\t}\r\n\t\t}";
        await tools.InsertSymbol(circleFile, "FileExtension", inserted, ct: TestContext.Current.CancellationToken);

        // Assert — copyright header survives
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("AlphaSierraPapa");
        content.ShouldContain("MIT X11 license");
        content.ShouldContain("using System;");
        content.ShouldContain("ProjectFileExtension");
        content.ShouldContain("FileExtension");
        content.ShouldContain("Name");
    }

    [Fact]
    public async Task InsertSymbol_After_MemberAfterMember_HasBlankLineSeparator()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert a method after the Perimeter property
        await tools.InsertSymbol(circleFile, "Perimeter", "public double Diameter => Radius * 2;", ct: TestContext.Current.CancellationToken);

        // Assert — there should be a blank line between Perimeter and Diameter
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int perimeterLine = lines.ShouldContainLine(l => l.Contains("Perimeter"));
        int diameterLine = lines.ShouldContainLine(l => l.Contains("Diameter"));

        // At least one blank line between them (perimeterLine + 1 should be blank)
        bool hasBlankSeparator = Enumerable.Range(perimeterLine + 1, diameterLine - perimeterLine - 1)
            .Any(i => String.IsNullOrWhiteSpace(lines[i]));
        hasBlankSeparator.ShouldBeTrue(
            "A blank line should separate the existing member from the inserted member");
    }

    [Fact]
    public async Task InsertSymbol_After_TrailingLineComment_PreservesComment()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert a property with a trailing line comment
        await tools.InsertSymbol(circleFile, "Radius", "public double Diameter => Radius * 2; // twice the radius", ct: TestContext.Current.CancellationToken);

        // Assert — trailing comment should be preserved
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("// twice the radius");
    }

    [Fact]
    public async Task InsertSymbol_After_TrailingBlockComment_PreservesComment()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert a property with a trailing block comment
        await tools.InsertSymbol(circleFile, "Radius", "public double Diameter => Radius * 2; /* diameter */", ct: TestContext.Current.CancellationToken);

        // Assert — trailing block comment should be preserved
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("/* diameter */");
    }
}
