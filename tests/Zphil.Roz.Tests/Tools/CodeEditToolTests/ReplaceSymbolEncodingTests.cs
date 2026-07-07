using System.Text;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

public class ReplaceSymbolEncodingTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task ReplaceSymbol_FileWithBom_PreservesBom()
    {
        // Arrange — re-write Circle.cs with a UTF-8 BOM
        CodeEditTools tools = CreateEditTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(circleFile, content, new UTF8Encoding(true), TestContext.Current.CancellationToken);
        await Fixture.WorkspaceManager.ReloadAsync(ct: TestContext.Current.CancellationToken);

        // Act
        await tools.ReplaceSymbol(circleFile, "Radius", "public double Radius { get; } = 42;", ct: TestContext.Current.CancellationToken);

        // Assert — file should still start with BOM bytes
        await circleFile.ShouldHaveBomAsync();
    }

    [Fact]
    public async Task ReplaceSymbol_FileWithoutBom_PreservesNoBom()
    {
        // Arrange — explicitly write Circle.cs as raw UTF-8 bytes without BOM
        string circleFile = CircleFile(Fixture);
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        byte[] noBomBytes = new UTF8Encoding(false).GetBytes(content);
        await File.WriteAllBytesAsync(circleFile, noBomBytes, TestContext.Current.CancellationToken);
        await Fixture.WorkspaceManager.ReloadAsync(ct: TestContext.Current.CancellationToken);

        // Verify precondition — no BOM before acting
        await circleFile.ShouldNotHaveBomAsync();

        CodeEditTools tools = CreateEditTools(Fixture);

        // Act
        await tools.ReplaceSymbol(circleFile, "Radius", "public double Radius { get; } = 42;", ct: TestContext.Current.CancellationToken);

        // Assert — BOM should NOT be introduced on files that didn't have one
        await circleFile.ShouldNotHaveBomAsync();
    }

    [Fact]
    public async Task ReplaceSymbol_LfContent_NormalizesToCrlfFile()
    {
        // Arrange — Shape.cs uses CRLF
        CodeEditTools tools = CreateEditTools(Fixture);
        string shapeFile = ShapeFile(Fixture);
        await EnsureCrlfAsync(Fixture, shapeFile);
        string original = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        original.ShouldContain("\r\n");

        // Act — replace with LF-only newlines in a multi-line body
        var newDeclaration = "public virtual string Describe()\n{\n    return \"replaced\";\n}";
        await tools.ReplaceSymbol(shapeFile, "Describe", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — no bare LF should remain
        string result = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        result.ShouldHaveNoBareLineFeed();
    }

    [Fact]
    public async Task ReplaceSymbol_LfOnlyFile_PreservesLfLineEndings()
    {
        // Arrange — convert Circle.cs to LF-only
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, content => content.Replace("\r\n", "\n"));

        CodeEditTools tools = CreateEditTools(Fixture);

        // Act
        await tools.ReplaceSymbol(circleFile, "Radius", "public double Radius { get; } = 42;", ct: TestContext.Current.CancellationToken);

        // Assert — file should remain LF-only, no CRLF introduced
        string result = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        result.ShouldHaveNoCrLf();
        result.ShouldContain("42");
    }

    [Fact]
    public async Task ReplaceSymbol_TabIndentedFile_PreservesTabIndentation()
    {
        // Arrange — convert Circle.cs to use tabs for indentation
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, content => content.Replace("    ", "\t"));

        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace Radius
        await tools.ReplaceSymbol(circleFile, "Radius", "public double Radius { get; } = 42;", ct: TestContext.Current.CancellationToken);

        // Assert — the edit preserves the file's existing tab indentation; it does NOT normalize to
        // spaces. (The 4-space sibling is ReplaceSymbolTests.ReplaceSymbol_Property_PreservesLeadingIndentation.)
        string result = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        result.ShouldContain("\tpublic double Radius { get; } = 42;");
        result.ShouldNotContain("    public double Radius { get; } = 42;");
    }

    [Fact]
    public async Task ReplaceSymbol_StringLiteralWithEscapes_PreservesLiteral()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string shapeFile = ShapeFile(Fixture);

        // Act — replace Describe with a body containing verbatim and escape sequences
        var newDeclaration = """
                             public virtual string Describe() =>
                                 $@"Shape: {GetType().Name}\tArea={Area:F2}";
                             """;
        await tools.ReplaceSymbol(shapeFile, "Describe", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — verbatim string with escape should be preserved
        string content = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        content.ShouldContain(@"\tArea=");
    }
}
