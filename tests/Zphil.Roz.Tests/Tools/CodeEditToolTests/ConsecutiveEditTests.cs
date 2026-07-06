using Zphil.Roz.Models;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Tests that consecutive edit operations persist correctly — regression tests for
///     silent data loss when replace_symbol is called after a prior edit.
/// </summary>
public class ConsecutiveEditTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task ReplaceSymbol_TwiceOnSameFile_BothEditsPersist()
    {
        // Arrange — ShapeCalculator.cs has two methods: Calculate and GetDefaultRadius
        CodeEditTools tools = CreateEditTools(Fixture);
        string calculatorFile = ShapeCalculatorFile(Fixture);

        // Act — first edit: replace Calculate method
        string result1 = await tools.ReplaceSymbol(calculatorFile, "Calculate", """public string Calculate() => "FIRST_EDIT_MARKER";""", ct: TestContext.Current.CancellationToken);
        result1.ShouldContain("Replaced");

        // Verify first edit persisted to disk
        string afterFirst = await File.ReadAllTextAsync(calculatorFile, TestContext.Current.CancellationToken);
        afterFirst.ShouldContain("FIRST_EDIT_MARKER");

        // Act — second edit: replace GetDefaultRadius method
        string result2 = await tools.ReplaceSymbol(calculatorFile, "GetDefaultRadius", """public static double GetDefaultRadius() => 42.0;""", ct: TestContext.Current.CancellationToken);
        result2.ShouldContain("Replaced");

        // Assert — both edits should be on disk
        string afterSecond = await File.ReadAllTextAsync(calculatorFile, TestContext.Current.CancellationToken);
        afterSecond.ShouldContain("FIRST_EDIT_MARKER");
        afterSecond.ShouldContain("42.0");
    }

    [Fact]
    public async Task ReplaceContent_ThenReplaceSymbol_OnDifferentFiles_BothPersist()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string shapeServiceFile = ShapeServiceFile(Fixture);
        string calculatorFile = ShapeCalculatorFile(Fixture);

        // Act — first edit: replace_content on ShapeService.cs
        await tools.ReplaceContent([new ReplaceContentRequest(shapeServiceFile, "Processing:", "REPLACED:")], ct: TestContext.Current.CancellationToken);

        // Verify first edit persisted
        string afterFirst = await File.ReadAllTextAsync(shapeServiceFile, TestContext.Current.CancellationToken);
        afterFirst.ShouldContain("REPLACED:");

        // Act — second edit: replace_symbol on ShapeCalculator.cs
        string result2 = await tools.ReplaceSymbol(calculatorFile, "Calculate", """public string Calculate() => "CROSS_FILE_EDIT";""", ct: TestContext.Current.CancellationToken);
        result2.ShouldContain("Replaced");

        // Assert — both files should have their edits
        string serviceContent = await File.ReadAllTextAsync(shapeServiceFile, TestContext.Current.CancellationToken);
        serviceContent.ShouldContain("REPLACED:");
        string calcContent = await File.ReadAllTextAsync(calculatorFile, TestContext.Current.CancellationToken);
        calcContent.ShouldContain("CROSS_FILE_EDIT");
    }

    [Fact]
    public async Task ReplaceSymbol_ThenReplaceSymbol_OnDifferentFiles_BothPersist()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string shapeFile = ShapeFile(Fixture);
        string calculatorFile = ShapeCalculatorFile(Fixture);

        // Act — first edit on Shape.cs
        await tools.ReplaceSymbol(shapeFile, "Describe", """public virtual string Describe() => "SHAPE_EDIT";""", ct: TestContext.Current.CancellationToken);

        // Verify first edit persisted
        string afterFirst = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        afterFirst.ShouldContain("SHAPE_EDIT");

        // Act — second edit on ShapeCalculator.cs
        string result2 = await tools.ReplaceSymbol(calculatorFile, "Calculate", """public string Calculate() => "CALC_EDIT";""", ct: TestContext.Current.CancellationToken);
        result2.ShouldContain("Replaced");

        // Assert — both files should have their edits
        string shapeContent = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        shapeContent.ShouldContain("SHAPE_EDIT");
        string calcContent = await File.ReadAllTextAsync(calculatorFile, TestContext.Current.CancellationToken);
        calcContent.ShouldContain("CALC_EDIT");
    }
}
