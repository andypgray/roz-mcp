using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Resolution behavior when a cursor is supplied. <c>rename_symbol</c> uses strict
///     position resolution — the cursor must be on the identifier token, no snap-to-nearest.
///     <c>edit_symbol</c> is name-authoritative: a unique in-file symbolName wins and a
///     non-identifier or stale cursor is ignored. Read-only tools remain forgiving.
/// </summary>
public class StrictResolutionTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    // ── replace_symbol ──────────────────────────────────────────────────

    [Fact]
    public async Task ReplaceSymbol_CursorOnKeyword_WithLineColumn_ResolvesByName()
    {
        // Arrange — Circle.cs line 5: "    public double Radius { get; } = radius;"
        // col 5 lands on 'p' of the 'public' keyword (not an identifier). On the edit path
        // symbolName is authoritative: "Radius" is the unique in-file match, so the keyword
        // cursor is ignored and the property is replaced (no strict-position rejection).
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        var newDeclaration = "public double Radius { get; } = 42;";

        // Act
        await tools.ReplaceSymbol(circleFile, "Radius", newDeclaration, 5, 5, ct: TestContext.Current.CancellationToken);

        // Assert — resolved by name despite the keyword cursor.
        (await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken)).ShouldContain("Radius { get; } = 42;");
    }

    [Fact]
    public async Task ReplaceSymbol_CursorOnIdentifier_WithLineColumn_Succeeds()
    {
        // Arrange — Circle.cs line 5: "    public double Radius { get; } = radius;"
        // col 19 lands on 'R' of 'Radius' identifier
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        var newDeclaration = "public double Radius { get; } = 42;";

        // Act
        string result = await tools.ReplaceSymbol(circleFile, "Radius", newDeclaration, 5, 19, ct: TestContext.Current.CancellationToken);

        // Assert — should succeed because cursor is on the identifier
        result.ShouldContain("Replaced");
    }

    [Fact]
    public async Task ReplaceSymbol_ByNameOnly_StillWorks()
    {
        // Arrange — name-based resolution (no line/column) is unaffected by strict mode
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        var newDeclaration = "public double Radius { get; } = 42;";

        // Act
        string result = await tools.ReplaceSymbol(circleFile, "Radius", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — name-based resolution still works
        result.ShouldContain("Replaced");
    }

    // ── insert_symbol (after) ──────────────────────────────────────────────────

    [Fact]
    public async Task InsertSymbol_After_CursorOnWhitespace_WithLineColumn_ResolvesByName()
    {
        // Arrange — Circle.cs line 2 is a blank line (cursor on no identifier). symbolName
        // "Circle" is the unique in-file match, so the whitespace cursor is ignored and the
        // comment is inserted after the class (name-authoritative edit path).
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act
        string result = await tools.InsertSymbol(circleFile, "Circle", "// inserted", line: 2, column: 1, ct: TestContext.Current.CancellationToken);

        // Assert — resolved by name despite the whitespace cursor.
        result.ShouldContain("Inserted");
        (await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken)).ShouldContain("// inserted");
    }

    // ── insert_symbol (before) ─────────────────────────────────────────────────

    [Fact]
    public async Task InsertSymbol_Before_CursorOnWhitespace_WithLineColumn_ResolvesByName()
    {
        // Arrange — Circle.cs line 6 is a blank line between Radius and Area. symbolName
        // "Area" is the unique in-file match, so the whitespace cursor is ignored and the
        // comment is inserted before Area (name-authoritative edit path).
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act
        string result = await tools.InsertSymbol(circleFile, "Area", "// inserted", InsertPosition.Before, 6, 1, ct: TestContext.Current.CancellationToken);

        // Assert — resolved by name despite the whitespace cursor.
        result.ShouldContain("Inserted");
        (await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken)).ShouldContain("// inserted");
    }

    // ── rename_symbol ────────────────────────────────────────────────────────

    [Fact]
    public async Task RenameSymbol_CursorOnModifier_ThrowsNoSymbolAtPosition()
    {
        // Arrange — Shape.cs line 13: "    public virtual string Describe() =>"
        // col 5 lands on 'p' of 'public'
        CodeEditTools tools = CreateEditTools(Fixture);
        string shapeFile = ShapeFile(Fixture);

        // Act & Assert
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.RenameSymbol(Loc(shapeFile, 13, 5), "Describe", "GetDescription"));
        ex.Message.ShouldContain("No symbol found at exact position");
    }
}
