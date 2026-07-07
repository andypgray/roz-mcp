using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Tests verifying that name-based edit tools correctly document the first-match behavior
///     for overloaded symbols, and that position-based resolution can target a specific overload.
/// </summary>
public class OverloadedSymbolTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    // ── replace_symbol — overloaded methods ───────────────────────────────

    [Fact]
    public async Task ReplaceSymbol_ByName_OverloadedMethod_TargetsFirstOverload()
    {
        // Arrange — ShapeService has three Format overloads; name-based targets the first
        CodeEditTools tools = CreateEditTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        var newDeclaration = """
                             public string Format(IShape shape) =>
                                 $"REPLACED: {shape.Area:F2}";
                             """;

        // Act
        string result = await tools.ReplaceSymbol(serviceFile, "Format", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — first overload replaced, second overload unchanged
        string fileContent = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("REPLACED:");
        fileContent.ShouldContain("includePerimeter"); // second overload untouched
    }

    [Fact]
    public async Task ReplaceSymbol_ByPosition_OverloadedMethod_TargetsSecondOverload()
    {
        // Arrange — target the second Format overload at line 30, col 19 (ShapeService.cs)
        CodeEditTools tools = CreateEditTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        var newDeclaration = """
                             public string Format(IShape shape, bool includePerimeter) =>
                                 $"REPLACED_SECOND: {shape.Area:F2}";
                             """;

        // Act — position-based resolution targets the second overload
        string result = await tools.ReplaceSymbol(serviceFile, "Format", newDeclaration, 30, 19, ct: TestContext.Current.CancellationToken);

        // Assert — second overload replaced, first overload unchanged
        string fileContent = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("REPLACED_SECOND:");
        // The old 2-arg body had `Perimeter: {shape.Perimeter...}` in its ternary; the replacement
        // ($"REPLACED_SECOND:...") removes it. "includePerimeter" still appears (param + 3-arg overload),
        // so assert the distinctive old-body fragment instead of relying on line-layout.
        fileContent.ShouldNotContain("Perimeter: {shape.Perimeter"); // old 2-arg body was replaced
        // First overload still has original body
        fileContent.ShouldContain("shape.Describe()} (Area:");
    }

    // ── replace_symbol — overloaded constructors ──────────────────────────

    [Fact]
    public async Task ReplaceSymbol_ByName_OverloadedConstructor_TargetsFirstConstructor()
    {
        // Arrange — ShapeCalculator has two instance constructors (.ctor);
        // name-based targets the first (IShape param, line 15)
        CodeEditTools tools = CreateEditTools(Fixture);
        string calculatorFile = ShapeCalculatorFile(Fixture);

        var newDeclaration = """
                             public ShapeCalculator(IShape shape)
                             {
                                 _shape = shape ?? throw new ArgumentNullException(nameof(shape));
                             }
                             """;

        // Act
        string result = await tools.ReplaceSymbol(calculatorFile, ".ctor", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — first constructor replaced, second untouched
        string fileContent = await File.ReadAllTextAsync(calculatorFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("ArgumentNullException");
        fileContent.ShouldContain("new Circle(radius)"); // second ctor still has original body
    }

    [Fact]
    public async Task ReplaceSymbol_ByPosition_OverloadedConstructor_TargetsSecondConstructor()
    {
        // Arrange — target the second constructor (double radius, line 20, col 12)
        CodeEditTools tools = CreateEditTools(Fixture);
        string calculatorFile = ShapeCalculatorFile(Fixture);

        var newDeclaration = """
                             public ShapeCalculator(double radius)
                             {
                                 _shape = new Circle(radius * 2);
                             }
                             """;

        // Act — position targets second .ctor
        string result = await tools.ReplaceSymbol(calculatorFile, ".ctor", newDeclaration, 20, 12, ct: TestContext.Current.CancellationToken);

        // Assert — second constructor replaced, first untouched
        string fileContent = await File.ReadAllTextAsync(calculatorFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("radius * 2"); // second ctor was replaced
        fileContent.ShouldContain("_shape = shape;"); // first ctor untouched
    }

    // ── insert_symbol (after) — overloaded methods ───────────────────────────────

    [Fact]
    public async Task InsertSymbol_After_ByPosition_OverloadedMethod_TargetsSecondOverload()
    {
        // Arrange — insert after the second Format overload (line 30)
        CodeEditTools tools = CreateEditTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        // Act
        string result = await tools.InsertSymbol(serviceFile, "Format", "\n    // Inserted after second Format\n", line: 30, column: 19, ct: TestContext.Current.CancellationToken);

        // Assert — content should appear after the second Format, not the first
        result.ShouldContain("Inserted");
        string fileContent = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("// Inserted after second Format");

        // Verify ordering: the comment appears after the second Format's body
        int secondFormatIdx = fileContent.IndexOf("includePerimeter", StringComparison.Ordinal);
        int insertedIdx = fileContent.IndexOf("// Inserted after second Format", StringComparison.Ordinal);
        insertedIdx.ShouldBeGreaterThan(secondFormatIdx);
    }

    // ── insert_symbol (before) — overloaded methods ──────────────────────────────

    [Fact]
    public async Task InsertSymbol_Before_ByPosition_OverloadedMethod_TargetsSecondOverload()
    {
        // Arrange — insert before the second Format overload (line 30)
        CodeEditTools tools = CreateEditTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        // Act
        string result = await tools.InsertSymbol(serviceFile, "Format", "    // Inserted before second Format\n", InsertPosition.Before, 30, 19, ct: TestContext.Current.CancellationToken);

        // Assert — content should appear before the second Format, not the first
        result.ShouldContain("Inserted");
        string fileContent = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("// Inserted before second Format");

        // Verify ordering: the comment appears after the first Format but before the second
        int firstFormatIdx = fileContent.IndexOf("public string Format(IShape shape) =>", StringComparison.Ordinal);
        int insertedIdx = fileContent.IndexOf("// Inserted before second Format", StringComparison.Ordinal);
        int secondFormatIdx = fileContent.IndexOf("public string Format(IShape shape, bool", StringComparison.Ordinal);
        insertedIdx.ShouldBeGreaterThan(firstFormatIdx);
        insertedIdx.ShouldBeLessThan(secondFormatIdx);
    }

    // ── ambiguity warning — correct overload count ────────────────────────────

    [Fact]
    public async Task ReplaceSymbol_ByName_ThreeOverloads_WarningShowsCorrectCount()
    {
        // Arrange — ShapeService.Format has 3 overloads
        CodeEditTools tools = CreateEditTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        var newDeclaration = """
                             public string Format(IShape shape) =>
                                 $"REPLACED: {shape.Area:F2}";
                             """;

        // Act
        string result = await tools.ReplaceSymbol(serviceFile, "Format", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — warning should show the actual count (3), not 2
        result.ShouldContain("3 total");
    }

    // ── edge case: position on keyword ─────────────────────────────────────────

    [Fact]
    public async Task ReplaceSymbol_ByPosition_OnKeyword_AmbiguousOverloads_Throws()
    {
        // Arrange — symbolName "Format" has 3 overloads and the cursor is on the 'p' of
        // 'public' of the second Format (line 30, col 5), not on a name token. With >1 name
        // match the cursor only tie-breaks overloads (against the identifier-token span); a
        // cursor that hits none must throw an actionable ambiguity error, never silently edit
        // the wrong overload.
        CodeEditTools tools = CreateEditTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        var newDeclaration = """
                             public string Format(IShape shape, bool includePerimeter) =>
                                 $"KEYWORD_RESOLVED: {shape.Area:F2}";
                             """;

        // Act & Assert — ambiguity error, not a wrong-overload edit.
        string before = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.ReplaceSymbol(serviceFile, "Format", newDeclaration, 30, 5));
        ex.Message.ShouldContain("ambiguous");
        ex.Message.ShouldContain("Format");

        // Assert the invariant the test name claims: no overload was silently edited. The file is
        // byte-for-byte unchanged and the replacement marker never reached disk.
        string after = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        after.ShouldBe(before);
        after.ShouldNotContain("KEYWORD_RESOLVED:");
    }
}
