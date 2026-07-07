using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Tests that edit tools correctly resolve special member types by their internal
///     Roslyn symbol names: destructors (Finalize), operators (op_Addition), conversion
///     operators (op_Implicit), and indexers (this[]).
/// </summary>
public class SpecialMemberEditTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    // ── Destructor (Finalize) ───────────────────────────────────────────

    [Fact]
    public async Task RemoveSymbol_Destructor_ByFinalizeName_RemovesFromDisk()
    {
        // Arrange — ShapeCollection has: ~ShapeCollection() { Dispose(false); }
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = ShapeCollectionFile(Fixture);

        // Act
        string result = await tools.RemoveSymbol(file, "Finalize", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Removed");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldNotContain("~ShapeCollection");
        content.ShouldContain("operator +");
    }

    [Fact]
    public async Task InsertSymbol_After_Destructor_ContentAppearsAfterDestructor()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = ShapeCollectionFile(Fixture);

        // Act
        string result = await tools.InsertSymbol(file, "Finalize", "    // Destructor was here\n", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("// Destructor was here");
        int destructorPos = content.IndexOf("~ShapeCollection", StringComparison.Ordinal);
        int commentPos = content.IndexOf("// Destructor was here", StringComparison.Ordinal);
        commentPos.ShouldBeGreaterThan(destructorPos);
    }

    // ── Operator (op_Addition) ──────────────────────────────────────────

    [Fact]
    public async Task RemoveSymbol_Operator_ByOpAdditionName_RemovesFromDisk()
    {
        // Arrange — ShapeCollection has: operator +(ShapeCollection left, ShapeCollection right)
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = ShapeCollectionFile(Fixture);

        // Act
        string result = await tools.RemoveSymbol(file, "op_Addition", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Removed");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldNotContain("operator +");
        content.ShouldContain("operator int");
    }

    [Fact]
    public async Task ReplaceSymbol_Operator_ReplacesDeclaration()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = ShapeCollectionFile(Fixture);

        var newDeclaration = """
                             public static ShapeCollection operator +(ShapeCollection left, ShapeCollection right)
                             {
                                 var result = new ShapeCollection();
                                 result._shapes.AddRange(left._shapes);
                                 result._shapes.AddRange(right._shapes);
                                 // Replaced operator
                                 return result;
                             }
                             """;

        // Act
        await tools.ReplaceSymbol(file, "op_Addition", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("// Replaced operator");
    }

    // ── Conversion operator (op_Implicit) ───────────────────────────────

    [Fact]
    public async Task RemoveSymbol_ConversionOperator_ByOpImplicitName_RemovesFromDisk()
    {
        // Arrange — ShapeCollection has: implicit operator int(ShapeCollection collection)
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = ShapeCollectionFile(Fixture);

        // Act
        string result = await tools.RemoveSymbol(file, "op_Implicit", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Removed");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldNotContain("implicit operator int");
        content.ShouldContain("operator +");
    }

    // ── Indexer (this[]) ────────────────────────────────────────────────

    [Fact]
    public async Task RemoveSymbol_Indexer_RemovesFromDisk()
    {
        // Arrange — ShapeCollection has: public IShape this[int index] => _shapes[index];
        // Roslyn's IPropertySymbol.Name for indexers is "this[]" (MetadataName is "Item").
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = ShapeCollectionFile(Fixture);

        // Act
        string result = await tools.RemoveSymbol(file, "this[]", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Removed");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldNotContain("this[int index]");
        content.ShouldContain("operator +");
    }
}
