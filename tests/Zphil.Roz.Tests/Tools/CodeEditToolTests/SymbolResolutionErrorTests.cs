using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Tests for symbol resolution error paths: mismatched line/column args and file not in solution.
/// </summary>
public class SymbolResolutionErrorTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task ReplaceSymbol_LineWithoutColumn_WithSymbolName_ResolvesByName()
    {
        // Arrange — EDIT-1: a half-specified `path:line` form (no column) plus a symbolName is
        // no longer rejected. The redundant line is dropped and the op resolves by the
        // authoritative name. (A bare `path:line` with no symbolName still errors — see
        // EditSymbolBatchTests.EditSymbol_Batch_LineOnlyLocation_PerOpError.)
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — `, 5` is the line arg (column omitted) → "path:5".
        await tools.ReplaceSymbol(circleFile, "Radius", "public double Radius { get; }", 5, ct: TestContext.Current.CancellationToken);

        // Assert — resolved by name; the `= radius;` initializer is gone.
        (await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken)).ShouldNotContain("Radius { get; } = radius;");
    }

    [Fact]
    public async Task ReplaceSymbol_MisspelledSymbolName_ShowsSuggestions()
    {
        // Arrange — "Radiu" is close to "Radius" in Circle.cs
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act & Assert
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.ReplaceSymbol(circleFile, "Radiu", "public double Radiu { get; }"));

        ex.Message.ShouldContain("Did you mean");
        ex.Message.ShouldContain("Radius");
    }

    [Fact]
    public async Task ReplaceSymbol_CompletelyUnrelatedName_NoSuggestions()
    {
        // Arrange — "Xyzzy123" has no close matches in Circle.cs
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act & Assert
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.ReplaceSymbol(circleFile, "Xyzzy123", "void Xyzzy123() {}"));

        ex.Message.ShouldContain("not found");
        ex.Message.ShouldNotContain("Did you mean");
    }

    [Fact]
    public async Task ReplaceSymbol_FileNotInSolution_ThrowsInvalidOperation()
    {
        // Arrange — use a path that doesn't exist in the solution
        CodeEditTools tools = CreateEditTools(Fixture);
        string fakePath = Path.Combine(Fixture.WorkspaceManager.SolutionDirectory!, "NotInSolution.cs");

        // Act & Assert
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.ReplaceSymbol(fakePath, "Foo", "void Foo() { }"));
        ex.Message.ShouldContain("not found in solution");
    }
}
