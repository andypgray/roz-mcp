using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Tests the symbolName/position interaction on the edit path. For <c>edit_symbol</c> the
///     name is authoritative — a unique in-file name match wins and a mismatched/stale cursor is
///     ignored; a name that also matches the cursor still resolves. The strict position↔name
///     cross-check now lives only on <c>rename_symbol</c> (see
///     <see cref="RenameSymbolValidationTests" />), whose solution-wide blast radius needs it.
/// </summary>
public class SymbolNamePositionValidationTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task ReplaceSymbol_NameAuthoritative_IgnoresMismatchedCursor()
    {
        // Arrange — Circle.cs 5:19 is "Radius", but symbolName says "Area". On the edit path the
        // name is authoritative: "Area" is a unique in-file match, so the mismatched cursor is
        // ignored and Area — not Radius — is replaced. Deliberate inversion of the old
        // position↔name cross-check for edit_symbol; rename_symbol keeps the cross-check (see
        // RenameSymbolValidationTests.RenameSymbol_SymbolNameMismatchesPosition_Throws).
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — newDeclaration is structurally compatible with the resolved Area property.
        string result = await tools.ReplaceSymbol(
            circleFile, "Area", "public override double Area => 0;", 5, 19,
            ct: TestContext.Current.CancellationToken);

        // Assert — Area resolved by name and replaced; Radius (at the ignored 5:19 cursor) intact.
        string final = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        final.ShouldContain("Area => 0;");
        final.ShouldNotContain("Math.PI * Radius * Radius");
        final.ShouldContain("public double Radius { get; } = radius;");
    }

    [Fact]
    public async Task ReplaceSymbol_SymbolNameMatchesPosition_Succeeds()
    {
        // Arrange — Circle.cs line 5 col 19 is "Radius", symbolName matches
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act
        string result = await tools.ReplaceSymbol(circleFile, "Radius", "public double Radius { get; } = 0;", 5, 19, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Replaced");
    }

    [Fact]
    public async Task ReplaceSymbol_ConstructorWithContainingTypeName_Succeeds()
    {
        // Arrange — symbolName "Circle" is the unique in-file match (the class declaration),
        // so it resolves by name; the 3:14 cursor is consistent but not required.
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act
        string result = await tools.ReplaceSymbol(circleFile, "Circle", """
                                                                        public class Circle(double r) : Shape
                                                                        {
                                                                            public double Radius { get; } = r;
                                                                        }
                                                                        """, 3, 14, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Replaced");
    }
}
