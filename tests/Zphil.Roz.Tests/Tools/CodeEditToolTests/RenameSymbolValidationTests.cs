using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Tests for rename_symbol input validation: identifier validity checks
///     and rejection of non-renameable symbol kinds (destructors, operators, indexers).
/// </summary>
public class RenameSymbolValidationTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task RenameSymbol_InvalidIdentifier_ThrowsArgument()
    {
        // Arrange — "123bad" is not a valid C# identifier
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "123bad"));
        ex.Message.ShouldContain("not a valid C# identifier");
    }

    [Fact]
    public async Task RenameSymbol_LineOnlyLocation_Throws()
    {
        // Arrange — "path:5" (line, no column) is the ambiguous form that used to silently
        // snap to column 1 and produce a misleading "no symbol at exact position" error.
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.RenameSymbol(Loc(circleFile, 5), "Radius", "R"));
        ex.Message.ShouldContain("rename_symbol");
        ex.Message.ShouldContain("line:col");
    }

    [Fact]
    public async Task RenameSymbol_SymbolNameMismatchesPosition_Throws()
    {
        // Arrange — Circle.cs 5:19 is "Radius", but symbolName claims "Area". rename_symbol
        // deliberately keeps the strict position↔symbolName cross-check (preferName defaulted
        // false): a solution-wide rename must not silently retarget on a mismatched cursor.
        // Opt-out counterpart to the name-authoritative edit path (see
        // SymbolNamePositionValidationTests.ReplaceSymbol_NameAuthoritative_IgnoresMismatchedCursor).
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.RenameSymbol(Loc(circleFile, 5, 19), "Area", "Renamed"));
        ex.Message.ShouldContain("resolved to 'Radius'");
        ex.Message.ShouldContain("symbolName is 'Area'");
    }

    [Fact]
    public async Task RenameSymbol_EmptyName_ThrowsArgument()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", ""));
        ex.Message.ShouldContain("not a valid C# identifier");
    }

    [Fact]
    public async Task RenameSymbol_KeywordAsName_ThrowsArgument()
    {
        // Arrange — "class" is a C# keyword, not a valid identifier
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "class"));
        ex.Message.ShouldContain("not a valid C# identifier");
    }

    [Fact]
    public async Task RenameSymbol_VerbatimKeyword_Succeeds()
    {
        // Arrange — "@class" is a valid verbatim identifier in C#
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — rename Radius (line 5, col 19) to @class
        string result = await tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "@class", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Renamed");
    }

    [Fact]
    public async Task RenameSymbol_ValidIdentifier_Succeeds()
    {
        // Arrange — baseline positive test
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — rename Radius (line 5, col 19) to R
        string result = await tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "R", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Renamed");
        result.ShouldContain("'Radius'");
        result.ShouldContain("'R'");
    }

    // ── Non-renameable symbol kinds ────────────────────────────────────

    [Fact]
    public async Task RenameSymbol_Destructor_ThrowsUserError()
    {
        // Arrange — ShapeCollection has ~ShapeCollection() destructor
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = ShapeCollectionFile(Fixture);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.RenameSymbol(file, "Finalize", "Finalize2", kind: SymbolicKind.Destructor));
        ex.Message.ShouldContain("Destructors cannot be renamed");
    }

    [Fact]
    public async Task RenameSymbol_Operator_ThrowsUserError()
    {
        // Arrange — ShapeCollection has operator +(ShapeCollection, ShapeCollection)
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = ShapeCollectionFile(Fixture);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.RenameSymbol(file, "op_Addition", "op_Added"));
        ex.Message.ShouldContain("cannot be renamed");
        ex.Message.ShouldContain("operator");
    }

    [Fact]
    public async Task RenameSymbol_ConversionOperator_ThrowsUserError()
    {
        // Arrange — ShapeCollection has implicit operator int(ShapeCollection)
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = ShapeCollectionFile(Fixture);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.RenameSymbol(file, "op_Implicit", "op_Explicit"));
        ex.Message.ShouldContain("cannot be renamed");
        ex.Message.ShouldContain("operator");
    }

    [Fact]
    public async Task RenameSymbol_Indexer_ThrowsUserError()
    {
        // Arrange — ShapeCollection has this[int index] indexer
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = ShapeCollectionFile(Fixture);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.RenameSymbol(file, "this[]", "Item"));
        ex.Message.ShouldContain("Indexers cannot be renamed");
    }
}
