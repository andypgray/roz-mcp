using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     CR-14: resolving a position inside a malformed field declaration must not crash.
///     <c>RoslynExtensions</c> and <c>RenameService</c> both read the field's first declarator via
///     <c>.Variables.First()</c>; the guarded <c>.FirstOrDefault()</c> (mirroring
///     <c>SymbolResolver.FindDeclaredSymbolOnLine</c>) handles a degenerate declarator list
///     gracefully. The guard is defensive: the C# parser never yields an *empty* Variables list
///     (<c>int ;</c> parses as an IncompleteMember; <c>const int ;</c> yields one missing-identifier
///     declarator), so these tests confirm both tools handle malformed field input without an
///     unhandled exception rather than reproducing a parser-reachable crash.
/// </summary>
public class FieldlessDeclarationResolutionTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    // Line 5 is "    const int ;" — a field declaration with no variable name. Column 12 is the
    // 'int' type token. The rest of Circle is preserved so cross-file references still resolve.
    private const string MalformedCircle =
        "namespace TestFixture.Shapes;\r\n\r\n"
        + "public class Circle(double radius) : Shape\r\n{\r\n"
        + "    const int ;\r\n"
        + "    public double Radius { get; } = radius;\r\n\r\n"
        + "    public override double Area => Math.PI * Radius * Radius;\r\n"
        + "    public override double Perimeter => 2 * Math.PI * Radius;\r\n}\r\n";

    [Fact]
    public async Task GoToDefinition_OnMalformedFieldType_DoesNotThrow()
    {
        // Arrange
        NavigationTools nav = CreateNavigationTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, _ => MalformedCircle);

        // Act — non-strict resolution snaps to the enclosing field declaration and reads its first
        // declarator; the guard keeps this from crashing on the degenerate declarator list.
        string result = await nav.GoToDefinition(Loc(circleFile, 5, 12), ct: TestContext.Current.CancellationToken);

        // Assert — a result is produced (the call completed without an unhandled exception).
        result.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task RenameSymbol_OnMalformedFieldType_SurfacesFriendlyError()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, _ => MalformedCircle);

        // Act & Assert — strict rename can't resolve a symbol at the 'int' token of a nameless
        // field. It surfaces a friendly rejection rather than crashing on the declarator list.
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            tools.RenameSymbol(Loc(circleFile, 5, 12), "Radius", "Renamed", ct: TestContext.Current.CancellationToken));
        ex.Message.ShouldNotContain("Sequence contains no elements");
    }
}
