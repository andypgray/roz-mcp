using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

public class ConstructorEditTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    // ── replace_symbol ──────────────────────────────────────────────────

    [Fact]
    public async Task ReplaceSymbol_Constructor_UpdatesBodyOnDisk()
    {
        // Arrange — ShapeCalculator has two instance constructors; .ctor matches the first one
        CodeEditTools tools = CreateEditTools(Fixture);
        string calculatorFile = ShapeCalculatorFile(Fixture);

        var newDeclaration = """
                             public ShapeCalculator(IShape shape)
                             {
                                 _shape = shape ?? throw new ArgumentNullException(nameof(shape));
                             }
                             """;

        // Act
        await tools.ReplaceSymbol(calculatorFile, ".ctor", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert
        string fileContent = await File.ReadAllTextAsync(calculatorFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("ArgumentNullException");
    }

    // ── insert_symbol (after) ──────────────────────────────────────────────────

    [Fact]
    public async Task InsertSymbol_After_Constructor_ContentAppearsInFile()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string calculatorFile = ShapeCalculatorFile(Fixture);

        // Act — insert after the first .ctor
        string result = await tools.InsertSymbol(calculatorFile, ".ctor", "\n    // Inserted after constructor\n", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Inserted");
        string fileContent = await File.ReadAllTextAsync(calculatorFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("// Inserted after constructor");
    }

    // ── insert_symbol (before) ─────────────────────────────────────────────────

    [Fact]
    public async Task InsertSymbol_Before_Constructor_ContentAppearsInFile()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string calculatorFile = ShapeCalculatorFile(Fixture);

        // Act — insert before the first .ctor
        string result = await tools.InsertSymbol(calculatorFile, ".ctor", "    // Inserted before constructor\n", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Inserted");
        string fileContent = await File.ReadAllTextAsync(calculatorFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("// Inserted before constructor");
    }

    // ── edge case: static constructor ────────────────────────────────────────

    [Fact]
    public async Task ReplaceSymbol_StaticConstructor_UpdatesBodyOnDisk()
    {
        // Arrange — static constructor Name is ".cctor"
        CodeEditTools tools = CreateEditTools(Fixture);
        string calculatorFile = ShapeCalculatorFile(Fixture);

        var newDeclaration = """
                             static ShapeCalculator()
                             {
                                 DefaultRadius = 2.0;
                             }
                             """;

        // Act
        await tools.ReplaceSymbol(calculatorFile, ".cctor", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert
        string fileContent = await File.ReadAllTextAsync(calculatorFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("2.0");
    }

    // ── ambiguity warning ──────────────────────────────────────────────────

    [Fact]
    public async Task ReplaceSymbol_Constructor_MultipleOverloads_IncludesWarning()
    {
        // Arrange — ShapeCalculator has two instance constructors
        CodeEditTools tools = CreateEditTools(Fixture);
        string calculatorFile = ShapeCalculatorFile(Fixture);

        // Use a different body to avoid no-op detection
        var newDeclaration = """
                             public ShapeCalculator(IShape shape)
                             {
                                 ArgumentNullException.ThrowIfNull(shape);
                                 _shape = shape;
                             }
                             """;

        // Act
        string result = await tools.ReplaceSymbol(calculatorFile, ".ctor", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — warning should mention multiple symbols and disambiguation
        result.ShouldContain("Warning");
        result.ShouldContain("2 total");
        result.ShouldContain("disambiguate");
    }

    [Fact]
    public async Task ReplaceSymbol_Constructor_WithLineColumn_NoWarning()
    {
        // Arrange — use line/column to target a specific constructor (no ambiguity)
        CodeEditTools tools = CreateEditTools(Fixture);
        string calculatorFile = ShapeCalculatorFile(Fixture);

        // Use a different body to avoid no-op detection
        var newDeclaration = """
                             public ShapeCalculator(IShape shape)
                             {
                                 ArgumentNullException.ThrowIfNull(shape);
                                 _shape = shape;
                             }
                             """;

        // Act — line 15 is where the first instance constructor is in ShapeCalculator.cs
        string result = await tools.ReplaceSymbol(calculatorFile, ".ctor", newDeclaration, 15, 12, ct: TestContext.Current.CancellationToken);

        // Assert — no ambiguity warning when position is explicit
        result.ShouldNotContain("Warning");
        result.ShouldContain("Replaced");
    }

    [Fact]
    public async Task ReplaceSymbol_ClassNameWithConstructorBody_ThrowsWithCtorHint()
    {
        // Arrange — pass the class name "ShapeCalculator" but provide constructor body
        CodeEditTools tools = CreateEditTools(Fixture);
        string calculatorFile = ShapeCalculatorFile(Fixture);

        var constructorBody = """
                              public ShapeCalculator(IShape shape)
                              {
                                  _shape = shape ?? throw new ArgumentNullException(nameof(shape));
                              }
                              """;

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.ReplaceSymbol(calculatorFile, "ShapeCalculator", constructorBody));

        ex.Message.ShouldContain(".ctor");
    }

    [Fact]
    public async Task ReplaceSymbol_ClassNameWithConstructorBody_PreservesOriginalFile()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string calculatorFile = ShapeCalculatorFile(Fixture);
        string originalContent = await File.ReadAllTextAsync(calculatorFile, TestContext.Current.CancellationToken);

        var constructorBody = """
                              public ShapeCalculator(double radius)
                              {
                                  _shape = new Circle(radius * 2);
                              }
                              """;

        // Act — should throw, not corrupt the file
        await Should.ThrowAsync<UserErrorException>(() => tools.ReplaceSymbol(calculatorFile, "ShapeCalculator", constructorBody));

        // Assert — file must be unchanged
        string afterContent = await File.ReadAllTextAsync(calculatorFile, TestContext.Current.CancellationToken);
        afterContent.ShouldBe(originalContent);
    }
}
