using System.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

public class InsertBeforeSymbolTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task InsertSymbol_Before_BeforeProperty_ContentAppearsInFile()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert a comment before the Area property
        string result = await tools.InsertSymbol(circleFile, "Area", "    // Area property override\n", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Inserted");
        string fileContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("// Area property override");
    }


    [Fact]
    public async Task InsertSymbol_Before_ContentWithoutTrailingNewline_SymbolOnOwnLine()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert before Area WITHOUT trailing newline
        await tools.InsertSymbol(circleFile, "Area", "    // Area override", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — Area declaration should still start on its own line
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int commentLine = lines.ShouldContainLine(l => l.Contains("// Area override"));
        int areaLine = lines.ShouldContainLine(l => l.Contains("override double Area"));
        areaLine.ShouldBe(commentLine + 1,
            "Area declaration should be on the line after the inserted comment");
    }

    [Fact]
    public async Task InsertSymbol_Before_BeforeClass_PreservesBlankLineSeparation()
    {
        // Arrange — Shape.cs has a blank line between namespace and class declaration
        CodeEditTools tools = CreateEditTools(Fixture);
        string shapeFile = ShapeFile(Fixture);

        // Act — insert doc comment before Shape class
        await tools.InsertSymbol(shapeFile, "Shape", "/// <summary>A shape.</summary>\n", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — blank line between namespace and inserted content should be preserved,
        // and inserted content should appear immediately before the class declaration
        string content = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int namespaceLine = lines.ShouldContainLine(l => l.StartsWith("namespace"));
        int docCommentLine = lines.ShouldContainLine(l => l.Contains("<summary>"));
        int classLine = lines.ShouldContainLine(l => l.Contains("public abstract class Shape"));
        (docCommentLine - namespaceLine).ShouldBeGreaterThan(1,
            "Blank line between namespace and inserted content should be preserved");
        classLine.ShouldBe(docCommentLine + 2,
            "Inserted content should appear before the existing doc comment and class declaration");
    }


    [Fact]
    public async Task InsertSymbol_Before_BeforeMember_PreservesBlankLineSeparation()
    {
        // Arrange — Circle.cs has a blank line between Radius and Area
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert a comment before the Area property
        await tools.InsertSymbol(circleFile, "Area", "    // Area override\n", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — blank line between Radius and the inserted comment should be preserved,
        // and inserted content should appear immediately before the Area property
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int radiusLine = lines.ShouldContainLine(l => l.Contains("Radius"));
        int commentLine = lines.ShouldContainLine(l => l.Contains("// Area override"));
        int areaLine = lines.ShouldContainLine(l => l.Contains("override double Area"));
        (commentLine - radiusLine).ShouldBeGreaterThan(1,
            "Blank line between Radius and inserted content should be preserved");
        areaLine.ShouldBe(commentLine + 1,
            "Inserted content should appear immediately before the Area property");
    }

    [Fact]
    public async Task InsertSymbol_Before_SymbolWithDocComment_InsertsBeforeDocComment()
    {
        // Arrange — add XML doc comment above Describe() in Shape.cs
        string shapeFile = ShapeFile(Fixture);
        await RewriteFileAsync(Fixture, shapeFile, content =>
            content.Replace(
                "    public virtual string Describe()",
                "    /// <summary>Describes the shape.</summary>\n    public virtual string Describe()"));

        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert a comment before Describe (which has a doc comment)
        await tools.InsertSymbol(shapeFile, "Describe", "    // Custom comment\n", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — insertion should be before the doc comment, not between it and the declaration
        string result = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(result);
        int customLine = lines.ShouldContainLine(l => l.Contains("// Custom comment"));
        int docLine = lines.ShouldContainLine(l => l.Contains("<summary>Describes the shape.</summary>"));
        int describeLine = lines.ShouldContainLine(l => l.Contains("Describe()"));
        customLine.ShouldBeLessThan(docLine,
            "Inserted content should appear before the doc comment");
        describeLine.ShouldBe(docLine + 1,
            "Doc comment should remain immediately before the declaration");
    }

    [Fact]
    public async Task InsertSymbol_Before_SymbolWithAttribute_InsertsBeforeAttribute()
    {
        // Arrange — add [Obsolete] attribute above ProcessShape in ShapeService.cs
        string serviceFile = ShapeServiceFile(Fixture);
        await RewriteFileAsync(Fixture, serviceFile, content =>
            content.Replace(
                "    public string ProcessShape",
                "    [System.Obsolete(\"Use NewMethod\")]\n    public string ProcessShape"));

        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert a comment before ProcessShape (which now has an attribute)
        await tools.InsertSymbol(serviceFile, "ProcessShape", "    // TODO: replace\n", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — insertion should be before the [Obsolete] attribute
        string result = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(result);
        int commentLine = lines.ShouldContainLine(l => l.Contains("// TODO: replace"));
        int attrLine = lines.ShouldContainLine(l => l.Contains("[System.Obsolete"));
        commentLine.ShouldBeLessThan(attrLine,
            "Inserted content should appear before the attribute");
    }

    [Fact]
    public async Task InsertSymbol_Before_LfOnlyFile_PreservesLfLineEndings()
    {
        // Arrange — convert Circle.cs to LF-only
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, content => content.Replace("\r\n", "\n"));

        CodeEditTools tools = CreateEditTools(Fixture);

        // Act
        await tools.InsertSymbol(circleFile, "Area", "    // LF-only comment\n", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — file should remain LF-only
        string result = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        result.ShouldHaveNoCrLf();
        result.ShouldContain("// LF-only comment");
    }

    [Fact]
    public async Task InsertSymbol_Before_FileWithBom_PreservesBom()
    {
        // Arrange — re-write Circle.cs with a UTF-8 BOM
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(circleFile, content, new UTF8Encoding(true), TestContext.Current.CancellationToken);
        await Fixture.WorkspaceManager.ReloadAsync(ct: TestContext.Current.CancellationToken);

        // Act — insert before Area
        await tools.InsertSymbol(circleFile, "Area", "    // BOM test\n", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — file should still start with BOM bytes
        await circleFile.ShouldHaveBomAsync();
        string result = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        result.ShouldContain("// BOM test");
    }

    [Fact]
    public async Task InsertSymbol_Before_EmptyContent_ThrowsArgumentException()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.InsertSymbol(CircleFile(Fixture), "Area", "", InsertPosition.Before));

        ex.Message.ShouldContain("empty");
    }

    [Fact]
    public async Task InsertSymbol_Before_MemberBeforePropertyWithBlankLine_PreservesBlankLineOnNewMember()
    {
        // Arrange — Area has a blank line above it (between Radius and Area)
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert a valid property declaration before Area
        string result = await tools.InsertSymbol(circleFile, "Area", "public double Diameter => Radius * 2;", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — blank line should transfer to the new member, not remain on Area
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int radiusLine = lines.ShouldContainLine(l => l.Contains("Radius { get; }"));
        int diameterLine = lines.ShouldContainLine(l => l.Contains("Diameter"));
        int areaLine = lines.ShouldContainLine(l => l.Contains("override double Area"));
        diameterLine.ShouldBeLessThan(areaLine,
            "Diameter should appear before Area");
        (diameterLine - radiusLine).ShouldBeGreaterThan(1,
            "Blank line between Radius and new member should be preserved");
        areaLine.ShouldBe(diameterLine + 1,
            "Area should immediately follow Diameter with no blank line");
    }

    [Fact]
    public async Task InsertSymbol_Before_MemberBeforePropertyWithoutBlankLine_InsertsDirectlyBefore()
    {
        // Arrange — Perimeter has no blank line above it (immediately follows Area)
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert a valid property before Perimeter
        string result = await tools.InsertSymbol(circleFile, "Perimeter", "public double Diameter => Radius * 2;", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — Diameter appears between Area and Perimeter
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int areaLine = lines.ShouldContainLine(l => l.Contains("override double Area"));
        int diameterLine = lines.ShouldContainLine(l => l.Contains("Diameter"));
        int perimeterLine = lines.ShouldContainLine(l => l.Contains("override double Perimeter"));
        diameterLine.ShouldBeGreaterThan(areaLine,
            "Diameter should appear after Area");
        diameterLine.ShouldBeLessThan(perimeterLine,
            "Diameter should appear before Perimeter");
    }

    [Fact]
    public async Task InsertSymbol_Before_MethodBeforeMemberWithBlankLine_MovesBlankLineToNewMethod()
    {
        // Arrange — Area has a blank line above it (between Radius and Area)
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert a valid method declaration before Area
        string result = await tools.InsertSymbol(circleFile, "Area", "public string GetName() { return \"Circle\"; }", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — blank line should transfer to the new method
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int radiusLine = lines.ShouldContainLine(l => l.Contains("Radius { get; }"));
        int getNameLine = lines.ShouldContainLine(l => l.Contains("GetName"));
        int areaLine = lines.ShouldContainLine(l => l.Contains("override double Area"));
        getNameLine.ShouldBeLessThan(areaLine,
            "GetName should appear before Area");
        (getNameLine - radiusLine).ShouldBeGreaterThan(1,
            "Blank line between Radius and inserted method should be preserved");
    }

    [Fact]
    public async Task InsertSymbol_Before_TypeInFileScopedNamespace_MatchesTargetIndentation()
    {
        // Arrange — Circle.cs uses file-scoped namespace, so Circle class is at column 0
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert a new enum before the Circle class
        string result = await tools.InsertSymbol(circleFile, "Circle", "public enum CircleType { Small, Medium, Large }", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — inserted enum should be at column 0, matching the Circle class
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int enumLine = lines.ShouldContainLine(l => l.Contains("public enum CircleType"));
        lines[enumLine].ShouldStartWith("public enum CircleType");
    }

    [Fact]
    public async Task InsertSymbol_Before_MemberInTraditionalNamespace_MatchesTargetIndentation()
    {
        // Arrange — LegacyClass.cs uses traditional namespace { }, members are at 8-space indent
        CodeEditTools tools = CreateEditTools(Fixture);
        string legacyFile = LegacyClassFile(Fixture);

        // Act — insert a property before DoWork method (which is at 8-space indent)
        string result = await tools.InsertSymbol(legacyFile, "DoWork", "public int Age { get; set; }", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — inserted property should match DoWork's indentation (8 spaces)
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(legacyFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int ageLine = lines.ShouldContainLine(l => l.Contains("public int Age"));
        lines[ageLine].ShouldStartWith("        public int Age");
    }

    [Fact]
    public async Task InsertSymbol_Before_EnumMemberBeforeFirst_InsertsCorrectly()
    {
        // Arrange — ShapeColor enum: Red, Green, Blue, Yellow
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = TypeKindExamplesFile(Fixture);

        // Act — insert None before Red (first member)
        string result = await tools.InsertSymbol(file, "Red", "None", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — None is the first member, Red follows
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int noneLine = lines.ShouldContainLine(l => l.Trim().StartsWith("None"));
        int redLine = lines.ShouldContainLine(l => l.Trim().StartsWith("Red"));
        noneLine.ShouldBeLessThan(redLine);
    }

    [Fact]
    public async Task InsertSymbol_Before_EnumMemberBeforeMiddle_InsertsCorrectly()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = TypeKindExamplesFile(Fixture);

        // Act — insert Orange before Blue (middle member)
        string result = await tools.InsertSymbol(file, "Blue", "Orange", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

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
    public async Task InsertSymbol_Before_EnumMemberWithDocComment_InsertsCorrectly()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = TypeKindExamplesFile(Fixture);

        // Act — insert enum member with doc comment before Red
        string result = await tools.InsertSymbol(file, "Red", "    /// <summary>\n    /// No color\n    /// </summary>\n    None", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — doc comment and None appear before Red, both fully intact
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("No color");
        content.ShouldContain("None");
        string[] lines = SplitLines(content);
        int docLine = lines.ShouldContainLine(l => l.Contains("No color"));
        int noneLine = lines.ShouldContainLine(l => l.Trim().StartsWith("None"));
        int redLine = lines.ShouldContainLine(l => l.Trim().StartsWith("Red"));
        noneLine.ShouldBeGreaterThan(docLine);
        noneLine.ShouldBeLessThan(redLine);
    }

    [Fact]
    public async Task InsertSymbol_Before_MemberInDeeplyNestedClass_MatchesNestingIndentation()
    {
        // Arrange — add a nested class inside LegacyClass so members are at 12-space indent
        // (namespace braces = 4, outer class = 8, inner class members = 12)
        CodeEditTools tools = CreateEditTools(Fixture);
        string legacyFile = LegacyClassFile(Fixture);
        await RewriteFileAsync(Fixture, legacyFile, content =>
            content.Replace(
                "        public void DoWork()",
                "        public class LegacyInner\r\n        {\r\n            public string Tag { get; set; }\r\n\r\n            public void Process() { }\r\n        }\r\n\r\n        public void DoWork()"));

        // Act — insert a property before Process (which is at 12-space indent)
        string result = await tools.InsertSymbol(legacyFile, "Process", "public bool IsActive { get; set; }", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — inserted property should be at 12-space indent matching the nesting level
        result.ShouldContain("Inserted");
        string fileContent = await File.ReadAllTextAsync(legacyFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(fileContent);
        int isActiveLine = lines.ShouldContainLine(l => l.Contains("public bool IsActive"));
        lines[isActiveLine].ShouldStartWith("            public bool IsActive");
    }

    [Fact]
    public async Task InsertSymbol_Before_TargetNodeHasNoLeadingTrivia_InsertsAtColumnZero()
    {
        // Arrange — rewrite LegacyClass.cs so the class declaration is the very first token
        // in the file (no namespace, no usings, no blank lines) — testing GetNodeIndentation
        // with a node that has no leading trivia at all
        CodeEditTools tools = CreateEditTools(Fixture);
        string legacyFile = LegacyClassFile(Fixture);
        await RewriteFileAsync(Fixture, legacyFile, _ =>
            "public class LegacyClass\r\n{\r\n    public string Name { get; set; }\r\n\r\n    public void DoWork() { }\r\n}\r\n");

        // Act — insert a class before LegacyClass (the first token, no trivia before it)
        string result = await tools.InsertSymbol(legacyFile, "LegacyClass", "public class NewHelper { }", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — inserted class should be at column 0 (no indentation)
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(legacyFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int helperLine = lines.ShouldContainLine(l => l.Contains("public class NewHelper"));
        lines[helperLine].ShouldStartWith("public class NewHelper");
    }

    [Fact]
    public async Task InsertSymbol_Before_FirstMember_PreservesCopyrightHeader()
    {
        // Arrange — prepend a copyright header to Circle.cs
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, c =>
            "// Copyright (c) Test Corp. All rights reserved.\r\n// Licensed under the MIT License.\r\n\r\n" + c);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert before Radius (first member in the class)
        await tools.InsertSymbol(circleFile, "Radius", "public double Diameter => 0;", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — copyright header survives, new member present, existing members intact
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Copyright (c) Test Corp");
        content.ShouldContain("Licensed under the MIT License");
        content.ShouldContain("Diameter");
        content.ShouldContain("Radius");
        content.ShouldContain("Area");
    }

    [Fact]
    public async Task InsertSymbol_Before_FirstMemberWithUsings_PreservesCopyrightHeader()
    {
        // Arrange — exact ILSpy file structure. Tests the SplitLeadingTrivia path.
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, _ => CustomLanguageCs);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert before Name (first member in the class)
        var inserted = "\t\tpublic string TestProp => \"test\";";
        await tools.InsertSymbol(circleFile, "Name", inserted, InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — copyright header and usings survive
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("AlphaSierraPapa");
        content.ShouldContain("MIT X11 license");
        content.ShouldContain("using System;");
        content.ShouldContain("TestProp");
        content.ShouldContain("Name");
        content.ShouldContain("FileExtension");
    }

    [Fact]
    public async Task InsertSymbol_Before_MultiLineMember_AllLinesReindented()
    {
        // Arrange — Circle.cs uses file-scoped namespace; Circle class is at column 0.
        // Insert a multi-line class before Circle. Roslyn formats it with 4-space indent;
        // NormalizeInsertedIndentationAsync must re-indent ALL lines to column 0.
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert a multi-line class before Circle
        string result = await tools.InsertSymbol(circleFile, "Circle", "public class CircleConfig\r\n{\r\n    public int MaxCount { get; set; }\r\n\r\n    public void Reset() { MaxCount = 0; }\r\n}", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — every line of the inserted class should be at column 0
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);

        int classLine = lines.ShouldContainLine(l => l.Contains("public class CircleConfig"));
        lines[classLine].ShouldStartWith("public class CircleConfig");

        int maxCountLine = lines.ShouldContainLine(l => l.Contains("MaxCount { get; set; }"));
        lines[maxCountLine].ShouldStartWith("    public int MaxCount");

        int resetLine = lines.ShouldContainLine(l => l.Contains("public void Reset()"));
        lines[resetLine].ShouldStartWith("    public void Reset()");
    }

    [Fact]
    public async Task InsertSymbol_Before_TrailingLineComment_PreservesComment()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — insert a property with a trailing line comment before Area
        await tools.InsertSymbol(circleFile, "Area", "public double Diameter => Radius * 2; // twice the radius", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — trailing comment should be preserved
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("// twice the radius");
    }
}
