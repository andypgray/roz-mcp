using System.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

public class RenameSymbolTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task RenameSymbol_Property_RenamesAcrossFile()
    {
        // Arrange — "    public double Radius { get; } = radius;" — Radius at line 5, col 19
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act
        string result = await tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "R", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Renamed");
        result.ShouldContain("'Radius'");
        result.ShouldContain("'R'");
    }

    [Fact]
    public async Task RenameSymbol_RedundantPrefix_Resolves()
    {
        // Arrange — agent-typed path with a redundant project prefix.
        // Real relative path is "TestFixture/Shapes/Circle.cs"; this form has an extra "TestFixture/" up front.
        CodeEditTools tools = CreateEditTools(Fixture);
        const string redundantPath = "TestFixture/TestFixture/Shapes/Circle.cs";

        // Act — rename should succeed rather than failing the literal path lookup
        string result = await tools.RenameSymbol(Loc(redundantPath, 5, 19), "Radius", "R", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Renamed");
        result.ShouldContain("'Radius'");
        result.ShouldContain("'R'");
    }


    [Fact]
    public async Task RenameSymbol_OnPublicKeyword_ThrowsNoSymbolAtPosition()
    {
        // Arrange — "    public double Radius { get; } = radius;" — col 5 is the 'p' of 'public'
        // rename_symbol uses strict resolution: cursor must be on the identifier token, not keywords.
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act & Assert — cursor on 'public' keyword should NOT snap to Radius
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.RenameSymbol(Loc(circleFile, 5, 5), "Radius", "R"));
        ex.Message.ShouldContain("No symbol found at exact position");
    }

    [Fact]
    public async Task RenameSymbol_FileWithBom_PreservesBom()
    {
        // Arrange — re-write Circle.cs with a UTF-8 BOM
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(circleFile, content, new UTF8Encoding(true), TestContext.Current.CancellationToken);
        await Fixture.WorkspaceManager.ReloadAsync(ct: TestContext.Current.CancellationToken);

        // Act — rename Radius at line 5, col 19
        await tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "R", ct: TestContext.Current.CancellationToken);

        // Assert — file should still start with BOM bytes
        await circleFile.ShouldHaveBomAsync();
    }

    [Fact]
    public async Task RenameSymbol_CrossFileReferences_RenamesInAllFiles()
    {
        // Arrange — Describe is defined in Shape.cs, overridden in Triangle.cs,
        // and called in ShapeService.cs
        CodeEditTools tools = CreateEditTools(Fixture);
        string shapeFile = ShapeFile(Fixture);

        // Describe() is at line 13, col 27 in Shape.cs: "    public virtual string Describe() =>"
        // Act
        string result = await tools.RenameSymbol(Loc(shapeFile, 13, 27), "Describe", "GetDescription", ct: TestContext.Current.CancellationToken);

        // Assert — multiple files should be changed
        result.ShouldContain("Renamed");
        result.ShouldContain("'Describe'");
        result.ShouldContain("'GetDescription'");
        // The rename spans the declaration (Shape.cs) and the override (Triangle.cs).
        result.ShouldContain("Shape.cs");
        result.ShouldContain("Triangle.cs");
    }


    [Fact]
    public async Task RenameSymbol_CrlfFile_PreservesCrlfLineEndings()
    {
        // Arrange — Circle.cs uses CRLF
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await EnsureCrlfAsync(Fixture, circleFile);
        string original = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        original.ShouldContain("\r\n");

        // Act — rename Radius at line 5, col 19
        await tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "R", ct: TestContext.Current.CancellationToken);

        // Assert — file should still use CRLF
        string result = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        result.ShouldContain("\r\n");
        result.ShouldContain("public double R {");
    }

    [Fact]
    public async Task RenameSymbol_InvalidPosition_ThrowsArgumentOutOfRange()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act & Assert — line 999 does not exist
        await Should.ThrowAsync<UserErrorException>(() => tools.RenameSymbol(Loc(circleFile, 999, 1), "Radius", "NewName"));
    }

    [Fact]
    public async Task RenameSymbol_RenameFile_RenamesContainingFile()
    {
        // Arrange — rename the Circle class with renameFile: true
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string circleDir = Path.GetDirectoryName(circleFile)!;

        // Act — "public class Circle(double radius) : Shape" — Circle at line 3, col 14
        string result = await tools.RenameSymbol(Loc(circleFile, 3, 14), "Circle", "Ellipse", renameFile: true, ct: TestContext.Current.CancellationToken);

        // Assert — original file gone, new file exists
        result.ShouldContain("Renamed");
        result.ShouldContain("'Circle'");
        result.ShouldContain("'Ellipse'");
        File.Exists(circleFile).ShouldBeFalse("Circle.cs should no longer exist");
        File.Exists(Path.Combine(circleDir, "Ellipse.cs")).ShouldBeTrue("Ellipse.cs should exist");
    }

    [Fact]
    public async Task RenameSymbol_RenameFile_TargetExists_ThrowsUserError()
    {
        // Arrange — a file already occupies the rename target path (Ellipse.cs)
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string targetFile = Path.Combine(Path.GetDirectoryName(circleFile)!, "Ellipse.cs");
        await File.WriteAllTextAsync(targetFile, "// existing\n", TestContext.Current.CancellationToken);

        // Act & Assert — the collision must fail fast with a friendly, actionable error rather
        // than a crash-classified raw IOException retried for ~350 ms first.
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.RenameSymbol(Loc(circleFile, 3, 14), "Circle", "Ellipse", renameFile: true, ct: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("Ellipse.cs");
        ex.Message.ShouldContain("already exists");
    }

    [Fact]
    public async Task RenameSymbol_RenameFileDefault_DoesNotRenameContainingFile()
    {
        // Arrange — rename the Circle class without renameFile (defaults to false)
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — "public class Circle(double radius) : Shape" — Circle at line 3, col 14
        string result = await tools.RenameSymbol(Loc(circleFile, 3, 14), "Circle", "Ellipse", ct: TestContext.Current.CancellationToken);

        // Assert — file should still be named Circle.cs
        result.ShouldContain("Renamed");
        File.Exists(circleFile).ShouldBeTrue("Circle.cs should still exist when renameFile is false");
    }

    [Fact]
    public async Task RenameSymbol_RenameOverloads_RenamesBothOverloads()
    {
        // Arrange — ShapeService has Format(IShape) at line 27 and Format(IShape, bool) at line 30
        CodeEditTools tools = CreateEditTools(Fixture);
        string shapeServiceFile = ShapeServiceFile(Fixture);

        // Act — rename Format at line 27, col 19 with renameOverloads: true
        string result = await tools.RenameSymbol(Loc(shapeServiceFile, 27, 19), "Format", "FormatShape", renameOverloads: true, ct: TestContext.Current.CancellationToken);

        // Assert — both overloads should be renamed
        result.ShouldContain("Renamed");
        string content = await File.ReadAllTextAsync(shapeServiceFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("public string Format(");
        content.ShouldContain("public string FormatShape(IShape shape)");
        content.ShouldContain("public string FormatShape(IShape shape, bool includePerimeter)");
    }

    [Fact]
    public async Task RenameSymbol_RenameOverloadsFalse_RenamesOnlyTargetOverload()
    {
        // Arrange — ShapeService has Format(IShape) at line 27 and Format(IShape, bool) at line 30
        CodeEditTools tools = CreateEditTools(Fixture);
        string shapeServiceFile = ShapeServiceFile(Fixture);

        // Act — rename Format at line 27, col 19 without renameOverloads (defaults to false)
        string result = await tools.RenameSymbol(Loc(shapeServiceFile, 27, 19), "Format", "FormatShape", ct: TestContext.Current.CancellationToken);

        // Assert — only the first overload should be renamed
        result.ShouldContain("Renamed");
        string content = await File.ReadAllTextAsync(shapeServiceFile, TestContext.Current.CancellationToken);
        content.ShouldContain("public string FormatShape(IShape shape) =>");
        content.ShouldContain("public string Format(IShape shape, bool includePerimeter)");
    }

    [Fact]
    public async Task RenameSymbol_RenameInStrings_RenamesIdentifiersInStringLiterals()
    {
        // Arrange — inject a string literal containing "Radius" into Circle.cs
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, content =>
            content.Replace(
                "public override double Perimeter => 2 * Math.PI * Radius;",
                "public override double Perimeter => 2 * Math.PI * Radius;\n" +
                "    public string Label => \"Radius\";"));

        // Act — rename Radius at line 5, col 19 with renameInStrings: true
        string result = await tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "R", renameInStrings: true, ct: TestContext.Current.CancellationToken);

        // Assert — the string literal should also be updated
        result.ShouldContain("Renamed");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("\"R\"");
    }

    [Fact]
    public async Task RenameSymbol_RenameInStringsFalse_LeavesStringLiteralsAlone()
    {
        // Arrange — inject a string literal containing "Radius" into Circle.cs
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, content =>
            content.Replace(
                "public override double Perimeter => 2 * Math.PI * Radius;",
                "public override double Perimeter => 2 * Math.PI * Radius;\n" +
                "    public string Label => \"Radius\";"));

        // Act — rename Radius without renameInStrings (defaults to false)
        string result = await tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "R", ct: TestContext.Current.CancellationToken);

        // Assert — the string literal should NOT be updated
        result.ShouldContain("Renamed");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("\"Radius\"");
    }

    [Fact]
    public async Task RenameSymbol_RenameInComments_RenamesIdentifiersInComments()
    {
        // Arrange — inject a comment containing "Radius" into Circle.cs
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, content =>
            content.Replace(
                "    public double Radius { get; } = radius;",
                "    // The Radius property stores the circle's radius\n" +
                "    public double Radius { get; } = radius;"));

        // Act — rename Radius at line 6 (shifted by 1 due to added comment), col 19 with renameInComments: true
        string result = await tools.RenameSymbol(Loc(circleFile, 6, 19), "Radius", "R", renameInComments: true, ct: TestContext.Current.CancellationToken);

        // Assert — the comment should also be updated
        result.ShouldContain("Renamed");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("// The R property");
    }

    [Fact]
    public async Task RenameSymbol_BlankLine_ThrowsNoSymbolAtPosition()
    {
        // Arrange — line 2 in Circle.cs is a blank line between the namespace and the class.
        // Edit tools use strict resolution: blank lines have no identifier token.
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act & Assert — cursor on blank line should be rejected outright
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.RenameSymbol(Loc(circleFile, 2, 1), "Circle", "Foo"));
        ex.Message.ShouldContain("No symbol found at exact position");
    }

    [Fact]
    public async Task RenameSymbol_OnNamespaceIdentifier_ThrowsCannotRenameNamespace()
    {
        // Arrange — line 1 in Circle.cs: "namespace TestFixture.Shapes;"
        // Column 25 lands on the 'S' of 'Shapes' identifier
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act & Assert — namespace renames should be explicitly refused
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.RenameSymbol(Loc(circleFile, 1, 25), "Shapes", "Foo"));
        ex.Message.ShouldContain("Cannot rename namespace");
    }

    [Fact]
    public async Task RenameSymbol_RenameInCommentsFalse_LeavesCommentsAlone()
    {
        // Arrange — inject a comment containing "Radius" into Circle.cs
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, content =>
            content.Replace(
                "    public double Radius { get; } = radius;",
                "    // The Radius property stores the circle's radius\n" +
                "    public double Radius { get; } = radius;"));

        // Act — rename Radius at line 6 (shifted by 1) without renameInComments (defaults to false)
        string result = await tools.RenameSymbol(Loc(circleFile, 6, 19), "Radius", "R", ct: TestContext.Current.CancellationToken);

        // Assert — the comment should NOT be updated
        result.ShouldContain("Renamed");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("// The Radius property");
    }

    [Fact]
    public async Task RenameSymbol_SameName_ReturnsNoChange()
    {
        // Arrange — rename Radius to Radius (no-op)
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — "    public double Radius { get; } = radius;" — Radius at line 5, col 19
        string result = await tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "Radius", ct: TestContext.Current.CancellationToken);

        // Assert — should detect no-op and not report any changes
        result.ShouldContain("already named");
        result.ShouldContain("No changes made");
    }

    [Fact]
    public async Task RenameSymbol_BySymbolName_RenamesSuccessfully()
    {
        // Arrange — rename by name only, no line/column
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act
        string result = await tools.RenameSymbol(circleFile, "Radius", "R", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Renamed");
        result.ShouldContain("'Radius'");
        result.ShouldContain("'R'");
    }

    [Fact]
    public async Task RenameSymbol_BySymbolName_NotFound_ThrowsDescriptiveError()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act & Assert — "NonExistent" is not in Circle.cs
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.RenameSymbol(circleFile, "NonExistent", "NewName"));
        ex.Message.ShouldContain("NonExistent");
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task RenameSymbol_KindFilterExcludesAllMatches_ReportsActualKinds()
    {
        // Arrange — Radius is a property in Circle.cs; kind=Method filters it out.
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act & Assert — error should hint that Radius exists as a Property
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.RenameSymbol(circleFile, "Radius", "R", kind: SymbolicKind.Method));
        ex.Message.ShouldContain("Radius");
        ex.Message.ShouldContain("with kind 'Method' not found");
        ex.Message.ShouldContain("\"Radius\" exists as Property");
        ex.Message.ShouldContain("drop the kind filter or use a different kind");
    }

    [Fact]
    public async Task RenameSymbol_BySymbolName_CrossFile_RenamesAllReferences()
    {
        // Arrange — rename Describe method by name, no position needed
        CodeEditTools tools = CreateEditTools(Fixture);
        string shapeFile = ShapeFile(Fixture);

        // Act
        string result = await tools.RenameSymbol(shapeFile, "Describe", "GetDescription", ct: TestContext.Current.CancellationToken);

        // Assert — multiple files should be changed
        result.ShouldContain("Renamed");
        result.ShouldContain("'Describe'");
        result.ShouldContain("'GetDescription'");
        // The rename spans the declaration (Shape.cs) and the override (Triangle.cs).
        result.ShouldContain("Shape.cs");
        result.ShouldContain("Triangle.cs");
    }

    [Fact]
    public async Task RenameSymbol_FieldByName_RenamesSuccessfully()
    {
        // Arrange — _disposed is a field in ShapeCollection.cs; GetDeclaredSymbol
        // on FieldDeclarationSyntax returns null so this exercises the field-handling path.
        CodeEditTools tools = CreateEditTools(Fixture);
        string shapeCollectionFile = ShapeCollectionFile(Fixture);

        // Act — rename the _disposed field by name only
        string result = await tools.RenameSymbol(shapeCollectionFile, "_disposed", "_isDisposed", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Renamed");
        result.ShouldContain("'_disposed'");
        result.ShouldContain("'_isDisposed'");
    }

    [Fact]
    public async Task RenameSymbol_InactivePreprocessorBranch_RenamesBothBranches()
    {
        // Arrange — ConditionalField.cs has lockObj declared in both #if and #else branches.
        // The active TFM (net10.0) sees the Lock branch; the #else (object) branch is disabled text.
        CodeEditTools tools = CreateEditTools(Fixture);
        string conditionalFile = MultiTfmFile(Fixture, "ConditionalField.cs");

        // Act — rename lockObj → syncRoot by name
        string result = await tools.RenameSymbol(conditionalFile, "lockObj", "syncRoot", "ConditionalField", ct: TestContext.Current.CancellationToken);

        // Assert — both branches should contain the new name
        result.ShouldContain("Renamed");
        result.ShouldContain("'lockObj'");
        result.ShouldContain("'syncRoot'");

        string content = await File.ReadAllTextAsync(conditionalFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("lockObj");
        content.ShouldContain("Lock syncRoot");
        content.ShouldContain("object syncRoot");
        content.ShouldContain("lock (syncRoot)");

        // The disabled-branch fixup note should appear
        result.ShouldContain("inactive preprocessor branches");
    }
}
