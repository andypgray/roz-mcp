using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

public class ReplaceSymbolTriviaTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task ReplaceSymbol_SymbolWithXmlDocComment_PreservesDocComment()
    {
        // Arrange — add XML doc comment above Describe() in Shape.cs
        string shapeFile = ShapeFile(Fixture);
        await RewriteFileAsync(Fixture, shapeFile, content =>
            content.Replace(
                "    public virtual string Describe()",
                "    /// <summary>Describes the shape.</summary>\n    public virtual string Describe()"));

        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace Describe body
        var newDeclaration = """public virtual string Describe() => "replaced";""";
        await tools.ReplaceSymbol(shapeFile, "Describe", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — doc comment should be preserved above the new declaration
        string result = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(result);
        int docLine = lines.ShouldContainLine(
            l => l.Contains("<summary>Describes the shape.</summary>"),
            "XML doc comment should be preserved");
        int describeLine = lines.ShouldContainLine(l => l.Contains("Describe()"));
        describeLine.ShouldBe(docLine + 1, "Doc comment should be immediately before the declaration");
    }

    [Fact]
    public async Task ReplaceSymbol_SymbolWithAttribute_NewBodyWithoutAttribute_AttributeIsDropped()
    {
        // Arrange — add [Obsolete] attribute above Describe() in Shape.cs
        // NOTE: documents current behaviour. Attributes are syntax children, not trivia,
        // so WithLeadingTrivia() cannot transfer them. They are dropped if omitted from new body.
        string shapeFile = ShapeFile(Fixture);
        await RewriteFileAsync(Fixture, shapeFile, content =>
            content.Replace(
                "    public virtual string Describe()",
                "    [System.Obsolete]\n    public virtual string Describe()"));

        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace Describe WITHOUT the attribute in new body
        var newDeclaration = """public virtual string Describe() => "replaced";""";
        await tools.ReplaceSymbol(shapeFile, "Describe", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — attribute should be dropped (current expected behaviour)
        string result = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        // NOTE: Attributes are syntax children, not trivia — they are dropped when not included in new body
        result.ShouldNotContain("[System.Obsolete]");
        result.ShouldContain("replaced");
    }


    [Fact]
    public async Task ReplaceSymbol_SymbolWithAttribute_NewBodyIncludesAttribute_AttributeIsPreserved()
    {
        // Arrange — add [Obsolete] attribute above Describe() in Shape.cs
        string shapeFile = ShapeFile(Fixture);
        await RewriteFileAsync(Fixture, shapeFile, content =>
            content.Replace(
                "    public virtual string Describe()",
                "    [System.Obsolete]\n    public virtual string Describe()"));

        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace Describe WITH the attribute in new body
        var newDeclaration = """
                             [System.Obsolete]
                             public virtual string Describe() => "replaced";
                             """;
        await tools.ReplaceSymbol(shapeFile, "Describe", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — attribute should be preserved when included in new body
        string result = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        result.ShouldContain("[System.Obsolete]");
        result.ShouldContain("replaced");
    }

    [Fact]
    public async Task ReplaceSymbol_NewBodyWithInlineComments_PreservesComments()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string shapeFile = ShapeFile(Fixture);

        // Act — replace Describe with a body containing inline comments
        var newDeclaration = """
                             public virtual string Describe()
                             {
                                 // Build the description string
                                 var name = GetType().Name; // type name
                                 return $"{name}: Area={Area:F2}";
                             }
                             """;
        await tools.ReplaceSymbol(shapeFile, "Describe", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — inline comments should be preserved
        string content = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        content.ShouldContain("// Build the description string");
        content.ShouldContain("// type name");
    }

    [Fact]
    public async Task ReplaceSymbol_SymbolWithPragmaDirective_PreservesPragma()
    {
        // Arrange — add #pragma warning disable above Describe() in Shape.cs
        string shapeFile = ShapeFile(Fixture);
        await RewriteFileAsync(Fixture, shapeFile, content =>
            content.Replace(
                "    public virtual string Describe()",
                "#pragma warning disable CS0618\n    public virtual string Describe()"));

        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace Describe body
        var newDeclaration = """public virtual string Describe() => "replaced";""";
        await tools.ReplaceSymbol(shapeFile, "Describe", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — #pragma should be preserved (it's leading trivia)
        string result = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        result.ShouldContain("#pragma warning disable CS0618");
        result.ShouldContain("replaced");
    }


    [Fact]
    public async Task ReplaceSymbol_FirstMember_PreservesCopyrightHeader()
    {
        // Arrange — prepend a copyright header to Circle.cs
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, c =>
            "// Copyright (c) Test Corp. All rights reserved.\r\n// Licensed under the MIT License.\r\n\r\n" + c);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace Radius (first member) with modified declaration
        await tools.ReplaceSymbol(circleFile, "Radius", "public double Radius { get; } = radius * 2;", ct: TestContext.Current.CancellationToken);

        // Assert — copyright header survives
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Copyright (c) Test Corp");
        content.ShouldContain("Licensed under the MIT License");
        content.ShouldContain("radius * 2");
        content.ShouldContain("Area");
    }

    [Fact]
    public async Task ReplaceSymbol_FirstMemberWithUsings_PreservesCopyrightHeader()
    {
        // Arrange — exact ILSpy file structure
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, _ => CustomLanguageCs);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace Name (first member) with modified declaration
        var replaced = "\t\tpublic string Name {\r\n\t\t\tget {\r\n\t\t\t\treturn \"Modified\";\r\n\t\t\t}\r\n\t\t}";
        await tools.ReplaceSymbol(circleFile, "Name", replaced, ct: TestContext.Current.CancellationToken);

        // Assert — copyright header survives
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("AlphaSierraPapa");
        content.ShouldContain("MIT X11 license");
        content.ShouldContain("using System;");
        content.ShouldContain("Modified");
        content.ShouldContain("FileExtension");
    }

    [Fact]
    public async Task ReplaceSymbol_InsertThenReplace_PreservesCopyrightHeader()
    {
        // Arrange — multi-operation sequence without removal
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, c =>
            "// Copyright (c) Test Corp. All rights reserved.\r\n// Licensed under the MIT License.\r\n\r\n" + c);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert a new member, then replace the original first member
        await tools.InsertSymbol(circleFile, "Radius", "public double Diameter => Radius * 2;", ct: TestContext.Current.CancellationToken);
        await tools.ReplaceSymbol(circleFile, "Radius", "public double Radius { get; } = radius * 2;", ct: TestContext.Current.CancellationToken);

        // Assert — copyright header survives two mutations
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Copyright (c) Test Corp");
        content.ShouldContain("Licensed under the MIT License");
        content.ShouldContain("radius * 2");
        content.ShouldContain("Diameter");
        content.ShouldContain("Area");
    }

    [Fact]
    public async Task ReplaceSymbol_TrailingLineComment_PreservesComment()
    {
        // Arrange
        string circleFile = CircleFile(Fixture);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace Radius with same value + trailing line comment
        await tools.ReplaceSymbol(circleFile, "Radius", "public double Radius { get; } = radius; // radius value", ct: TestContext.Current.CancellationToken);

        // Assert — trailing comment should be preserved
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("// radius value");
    }

    [Fact]
    public async Task ReplaceSymbol_TrailingBlockComment_PreservesComment()
    {
        // Arrange
        string circleFile = CircleFile(Fixture);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace Radius with same value + trailing block comment
        await tools.ReplaceSymbol(circleFile, "Radius", "public double Radius { get; } = radius; /* block */", ct: TestContext.Current.CancellationToken);

        // Assert — trailing block comment should be preserved
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("/* block */");
    }

    [Fact]
    public async Task ReplaceSymbol_IdenticalExceptTrailingComment_IsNotNoOp()
    {
        // Arrange
        string circleFile = CircleFile(Fixture);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace Radius with identical code + trailing comment
        string result = await tools.ReplaceSymbol(circleFile, "Radius", "public double Radius { get; } = radius; // added comment", ct: TestContext.Current.CancellationToken);

        // Assert — should NOT be a no-op; the comment makes it different
        result.ShouldNotContain("No changes made");
        result.ShouldContain("Replaced");
    }

    [Fact]
    public async Task ReplaceSymbol_SymbolInsideRegion_PreservesRegion()
    {
        // Arrange — wrap Describe() in #region/#endregion in Shape.cs
        string shapeFile = ShapeFile(Fixture);
        await RewriteFileAsync(Fixture, shapeFile, content =>
        {
            content = content.Replace(
                "    public virtual string Describe()",
                "    #region Description\n    public virtual string Describe()");
            // Add #endregion after the Describe method's closing (which is the expression body line)
            content = content.Replace(
                "Perimeter={Perimeter:F2}\";",
                "Perimeter={Perimeter:F2}\";\n    #endregion");
            return content;
        });

        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace Describe body
        var newDeclaration = """public virtual string Describe() => "replaced";""";
        await tools.ReplaceSymbol(shapeFile, "Describe", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — #region and #endregion should be preserved
        string result = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        result.ShouldContain("#region Description");
        result.ShouldContain("replaced");
    }
}
