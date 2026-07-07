using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

public class ReplaceSymbolTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task ReplaceSymbol_ExistingMethod_UpdatesBodyOnDisk()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string shapeFile = ShapeFile(Fixture);
        var newDeclaration = """public virtual string Describe() => "Replaced description";""";

        // Act
        string result = await tools.ReplaceSymbol(shapeFile, "Describe", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Describe");
        string fileContent = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("Replaced description");
    }


    [Fact]
    public async Task ReplaceSymbol_NonExistentSymbol_ThrowsUserError()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.ReplaceSymbol(CircleFile(Fixture), "NonExistentSymbol", "void NonExistentSymbol() {}"));

        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task ReplaceSymbol_Method_PreservesBlankLinesBetweenMembers()
    {
        // Arrange — Shape.cs has a blank line between Perimeter and Describe
        CodeEditTools tools = CreateEditTools(Fixture);
        string shapeFile = ShapeFile(Fixture);

        // Act — replace Describe with a new implementation
        var newDeclaration = """public virtual string Describe() => "replaced";""";
        await tools.ReplaceSymbol(shapeFile, "Describe", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — blank line between Perimeter and Describe should still exist
        string content = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int perimeterLine = lines.ShouldContainLine(l => l.Contains("Perimeter"));
        int describeLine = lines.ShouldContainLine(l => l.Contains("Describe"));
        (describeLine - perimeterLine).ShouldBeGreaterThan(1,
            "There should be a blank line between Perimeter and Describe");
    }


    [Fact]
    public async Task ReplaceSymbol_Property_PreservesLeadingIndentation()
    {
        // Arrange — Circle.cs has "    public double Radius ..." (4-space indent)
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — replace Radius with body starting at 'public' (no leading whitespace)
        var newDeclaration = "public double Radius { get; } = 42;";
        await tools.ReplaceSymbol(circleFile, "Radius", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — indentation from the original trivia should be preserved
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("    public double Radius { get; } = 42;");
    }

    [Theory]
    [InlineData("public virtual string Describe()\n{\n    return \"replaced\";\n}", Label = "unindented input")]
    [InlineData("    public virtual string Describe()\n    {\n        return \"replaced\";\n    }", Label = "pre-indented input")]
    public async Task ReplaceSymbol_MultiLineBody_IndentsAllLinesCorrectly(string newDeclaration)
    {
        // Arrange — Describe in Shape.cs is indented 4 spaces (inside class body)
        CodeEditTools tools = CreateEditTools(Fixture);
        string shapeFile = ShapeFile(Fixture);

        // Act
        await tools.ReplaceSymbol(shapeFile, "Describe", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — output should be correctly indented regardless of input indentation
        string content = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int descIdx = lines.ShouldContainLine(l => l.Contains("Describe()"));
        lines[descIdx].ShouldBe("    public virtual string Describe()");
        lines[descIdx + 1].ShouldBe("    {");
        lines[descIdx + 2].ShouldBe("        return \"replaced\";");
        lines[descIdx + 3].ShouldBe("    }");
    }

    [Fact]
    public async Task ReplaceSymbol_BlockBodiedProperty_ReplacesCorrectly()
    {
        // Arrange — Triangle.cs Area property has a block-bodied getter
        CodeEditTools tools = CreateEditTools(Fixture);
        string triangleFile = TriangleFile(Fixture);

        // Act — replace with a simpler block-bodied property
        var newDeclaration = """
                             public override double Area
                             {
                                 get { return 42; }
                             }
                             """;
        await tools.ReplaceSymbol(triangleFile, "Area", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — new body should be correctly formatted with proper indentation
        string content = await File.ReadAllTextAsync(triangleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int areaIdx = lines.ShouldContainLine(l => l.Contains("override double Area"));
        lines[areaIdx].ShouldStartWith("    ");
        content.ShouldContain("return 42;");
        // Perimeter and Describe should still exist
        content.ShouldContain("Perimeter");
        content.ShouldContain("Describe");
    }


    [Fact]
    public async Task ReplaceSymbol_OverrideMethod_PreservesOverrideKeyword()
    {
        // Arrange — Triangle.cs Describe is an override method
        CodeEditTools tools = CreateEditTools(Fixture);
        string triangleFile = TriangleFile(Fixture);

        // Act — replace with a new override method
        var newDeclaration = """public override string Describe() => $"Triangle: {base.Describe()}";""";
        await tools.ReplaceSymbol(triangleFile, "Describe", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — override keyword is preserved, correct indentation
        string content = await File.ReadAllTextAsync(triangleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int describeLine = lines.ShouldContainLine(l => l.Contains("Describe"));
        lines[describeLine].ShouldStartWith("    ");
        lines[describeLine].ShouldContain("override");
    }

    [Fact]
    public async Task ReplaceSymbol_EmptyBody_ThrowsInvalidOperation()
    {
        // Arrange
        // NOTE: Roslyn's ParseMemberDeclaration accepts almost anything (returning a node with diagnostics),
        // so we test with an empty/whitespace body which will be trimmed to empty and fail to parse.
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act & Assert — provide whitespace-only body (gets trimmed to empty)
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.ReplaceSymbol(CircleFile(Fixture), "Radius", "   "));

        ex.Message.ShouldContain("empty");
    }

    [Fact]
    public async Task ReplaceSymbol_LocalFunctionWithInvalidBody_ThrowsUserError()
    {
        // Arrange — target is a local function; parsing the replacement as a statement
        // that is NOT a local function returns null from ParseDeclaration, hitting line 54-55.
        CodeEditTools tools = CreateEditTools(Fixture);
        string filePath = LocalFunctionExampleFile(Fixture);

        // Act & Assert — "return 42;" is a valid statement but not a local function statement
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.ReplaceSymbol(filePath, "GetArea", "return 42;", 16, 16));

        ex.Message.ShouldContain("Could not parse");
    }

    [Fact]
    public async Task ReplaceSymbol_EnumMemberSimpleRename_ReplacesCorrectly()
    {
        // Arrange — ShapeColor enum: Red, Green, Blue, Yellow
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = TypeKindExamplesFile(Fixture);

        // Act — rename Green to Lime
        await tools.ReplaceSymbol(file, "Green", "Lime", ct: TestContext.Current.CancellationToken);

        // Assert
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("Lime");
        content.ShouldNotContain("Green");
        content.ShouldContain("Red");
        content.ShouldContain("Blue");
        content.ShouldContain("Yellow");
        string[] lines = SplitLines(content);
        lines.ShouldNotContain(l => l.Trim() == ",", "Should not have an orphan comma line");
    }

    [Fact]
    public async Task ReplaceSymbol_EnumMemberWithValue_PreservesValue()
    {
        // Arrange — give Green a value, then replace it
        string file = TypeKindExamplesFile(Fixture);
        await RewriteFileAsync(Fixture, file, c => c.Replace("Green,", "Green = 2,"));
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act
        await tools.ReplaceSymbol(file, "Green", "Lime = 2", ct: TestContext.Current.CancellationToken);

        // Assert
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("Lime = 2");
        content.ShouldNotContain("Green");
    }

    [Fact]
    public async Task ReplaceSymbol_EnumMemberAddValue_AddsValueAssignment()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = TypeKindExamplesFile(Fixture);

        // Act — replace Green (no value) with Green = 42
        await tools.ReplaceSymbol(file, "Green", "Green = 42", ct: TestContext.Current.CancellationToken);

        // Assert
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("Green = 42");
    }

    [Fact]
    public async Task ReplaceSymbol_EnumMemberWithDocComment_PreservesExistingDocComment()
    {
        // Arrange — add doc comment to Green
        string file = TypeKindExamplesFile(Fixture);
        await RewriteFileAsync(Fixture, file, c =>
            c.Replace("    Green,", "    /// <summary>\n    /// The green color\n    /// </summary>\n    Green,"));
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace Green with Lime (leading trivia should be preserved)
        await tools.ReplaceSymbol(file, "Green", "Lime", ct: TestContext.Current.CancellationToken);

        // Assert — doc comment preserved, member renamed
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("Lime");
        content.ShouldContain("The green color");
        // Verify the enum member identifier was replaced (case-sensitive to avoid matching doc comment text)
        string[] lines = SplitLines(content);
        lines.ShouldNotContain(l => l.Trim().StartsWith("Green"), "Enum member 'Green' should have been replaced");
    }

    [Fact]
    public async Task ReplaceSymbol_LastEnumMemberNoTrailingComma_ReplacesCleanly()
    {
        // Arrange — Yellow is the last member with no trailing comma
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = TypeKindExamplesFile(Fixture);

        // Act
        await tools.ReplaceSymbol(file, "Yellow", "Orange", ct: TestContext.Current.CancellationToken);

        // Assert
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("Orange");
        content.ShouldNotContain("Yellow");
        content.ShouldContain("Red");
        content.ShouldContain("Blue");
        string[] lines = SplitLines(content);
        lines.ShouldNotContain(l => l.Trim() == ",", "Should not have an orphan comma line");
    }

    [Fact]
    public async Task ReplaceSymbol_LocalFunction_ReplacesBody()
    {
        // Arrange — LocalFunctionExample.cs line 16: `double GetArea(IShape s) => s.Area;`
        CodeEditTools tools = CreateEditTools(Fixture);
        string filePath = LocalFunctionExampleFile(Fixture);

        // Act — replace local function body, using line/column to target it
        var newDeclaration = "double GetArea(IShape s) => s.Area * 2;";
        await tools.ReplaceSymbol(filePath, "GetArea", newDeclaration, 16, 16, ct: TestContext.Current.CancellationToken);

        // Assert
        string content = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);
        content.ShouldContain("s.Area * 2");
    }

    [Fact]
    public async Task ReplaceSymbol_IdenticalDeclaration_ReturnsNoOp()
    {
        // Arrange — replace Radius property with identical declaration
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string originalContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);

        // Act — provide the exact same property declaration
        string result = await tools.ReplaceSymbol(circleFile, "Radius", "public double Radius { get; } = radius;", ct: TestContext.Current.CancellationToken);

        // Assert — should report no-op, file unchanged
        result.ShouldContain("No changes made");
        result.ShouldContain("identical");
        string afterContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        afterContent.ShouldBe(originalContent);
    }
}
