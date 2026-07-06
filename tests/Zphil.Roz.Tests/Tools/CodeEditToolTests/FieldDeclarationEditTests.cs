using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Tests that edit tools (remove, insert, replace) correctly resolve field declarations
///     by name. Fields have a different syntax structure where the name lives on the
///     VariableDeclarator, not the FieldDeclarationSyntax.
/// </summary>
public class FieldDeclarationEditTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    private static string MutableStateFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Shapes", "MutableState.cs");

    [Fact]
    public async Task RemoveSymbol_OneDeclaratorOfMultiVariableField_RejectsAndPreservesSiblings()
    {
        // Arrange — MutableState has `private int _x, _y;`. The resolver returns the whole field
        // declaration for "_x", so Remove would delete the `_y` sibling with it (F3).
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = MutableStateFile(Fixture);

        // Act + Assert — friendly rejection naming the sibling and the remedy; file untouched.
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() =>
            tools.RemoveSymbol(file, "_x", ct: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("_y");
        ex.Message.ShouldContain("Split");

        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("_x, _y");
    }

    [Fact]
    public async Task ReplaceSymbol_OneDeclaratorOfMultiVariableField_Rejects()
    {
        // Arrange — Replace of one declarator rewrites the shared field node, dropping the sibling (F3).
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = MutableStateFile(Fixture);

        // Act + Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() =>
            tools.ReplaceSymbol(file, "_x", "private int _x => 0;", ct: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("_y");

        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("_x, _y");
    }

    [Fact]
    public async Task InsertSymbol_RelativeToMultiDeclaratorField_IsNotGuarded()
    {
        // Arrange — the guard is scoped to Remove/Replace; inserting near a multi-declarator field
        // doesn't touch the shared declaration and must still succeed (proves no over-guard).
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = MutableStateFile(Fixture);

        // Act — insert a comment after the _x declarator anchor.
        string result = await tools.InsertSymbol(
            file, "_x", "    // inserted near multi-declarator field\n", ct: TestContext.Current.CancellationToken);

        // Assert — insertion applied and both declarators survive.
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("// inserted near multi-declarator field");
        content.ShouldContain("_x, _y");
    }

    [Fact]
    public async Task RemoveSymbol_StaticReadonlyField_RemovesFromDisk()
    {
        // Arrange — ShapeCalculator has: private static readonly double DefaultRadius;
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = ShapeCalculatorFile(Fixture);

        // Act
        string result = await tools.RemoveSymbol(file, "DefaultRadius", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Removed");
        result.ShouldContain("DefaultRadius");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldNotContain("private static readonly double DefaultRadius");
        content.ShouldContain("_shape");
    }

    [Fact]
    public async Task RemoveSymbol_InstanceField_RemovesFromDisk()
    {
        // Arrange — ShapeCalculator has: private readonly IShape _shape;
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = ShapeCalculatorFile(Fixture);

        // Act
        string result = await tools.RemoveSymbol(file, "_shape", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Removed");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldNotContain("private readonly IShape _shape");
        content.ShouldContain("DefaultRadius");
    }

    [Fact]
    public async Task InsertSymbol_After_Field_ContentAppearsAfterField()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = ShapeCalculatorFile(Fixture);

        // Act — insert a new field after DefaultRadius
        string result = await tools.InsertSymbol(file, "DefaultRadius", "    private static readonly double MaxRadius = 100.0;", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("MaxRadius");
        int defaultRadiusPos = content.IndexOf("DefaultRadius", StringComparison.Ordinal);
        int maxRadiusPos = content.IndexOf("MaxRadius", StringComparison.Ordinal);
        maxRadiusPos.ShouldBeGreaterThan(defaultRadiusPos);
    }

    [Fact]
    public async Task InsertSymbol_Before_Field_ContentAppearsBeforeField()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = ShapeCalculatorFile(Fixture);

        // Act — insert a comment before _shape
        string result = await tools.InsertSymbol(file, "_shape", "    // The backing shape instance\n", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("// The backing shape instance");
        int commentPos = content.IndexOf("// The backing shape instance", StringComparison.Ordinal);
        int fieldPos = content.IndexOf("_shape", StringComparison.Ordinal);
        commentPos.ShouldBeLessThan(fieldPos);
    }

    [Fact]
    public async Task ReplaceSymbol_Field_ReplacesDeclaration()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = ShapeCalculatorFile(Fixture);

        // Act — replace DefaultRadius with a const
        string result = await tools.ReplaceSymbol(file, "DefaultRadius", "private const double DefaultRadius = 2.5;", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("DefaultRadius");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("private const double DefaultRadius = 2.5;");
        content.ShouldNotContain("static readonly");
    }
}
