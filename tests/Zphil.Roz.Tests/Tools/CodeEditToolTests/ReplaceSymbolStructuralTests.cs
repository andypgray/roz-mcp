using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Tests for replace_symbol and rename_symbol structural validation:
///     type-to-constructor mismatch, type-to-member mismatch, and namespace rename rejection.
/// </summary>
public class ReplaceSymbolStructuralTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task ReplaceSymbol_ClassWithConstructorBody_ThrowsStructuralMismatch()
    {
        // Arrange — try to replace the Circle class declaration with just a constructor
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.ReplaceSymbol(circleFile, "Circle", "public Circle(double r) { }"));
        ex.Message.ShouldContain("type declaration");
        ex.Message.ShouldContain("constructor");
    }

    [Fact]
    public async Task ReplaceSymbol_ClassWithMethodBody_ThrowsStructuralMismatch()
    {
        // Arrange — try to replace the Circle class declaration with just a method
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.ReplaceSymbol(circleFile, "Circle", "public string Describe() => \"hello\";"));
        ex.Message.ShouldContain("type declaration");
        ex.Message.ShouldContain("member declaration");
    }

    [Fact]
    public async Task RenameSymbol_TargetingNamespace_ThrowsNamespaceRejection()
    {
        // Arrange — position on namespace name in Circle.cs line 1: "namespace TestFixture.Shapes;"
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.RenameSymbol(Loc(circleFile, 1, 11), "TestFixture", "NewNamespace"));
        ex.Message.ShouldContain("namespace");
    }
}
