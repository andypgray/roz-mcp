using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Tests for the associated-file rename logic in rename_symbol (RenameAssociatedFiles).
///     ResetAsync triggers a full ReloadAsync when files are renamed/deleted, so the shared
///     fixture is safe here.
/// </summary>
public class RenameAssociatedFilesTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task RenameSymbol_RenameFile_AlsoRenamesDesignerFile()
    {
        // Arrange — create a Circle.Designer.cs file alongside Circle.cs to simulate WinForms
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circleFile = CircleFile(ws);
        string circleDir = Path.GetDirectoryName(circleFile)!;
        string designerFile = Path.Combine(circleDir, "Circle.Designer.cs");
        await File.WriteAllTextAsync(designerFile, "// Designer file for Circle\n", TestContext.Current.CancellationToken);

        // Act — rename Circle class with renameFile: true
        string result = await tools.RenameSymbol(Loc(circleFile, 3, 14), "Circle", "Ellipse", renameFile: true, ct: TestContext.Current.CancellationToken);

        // Assert — primary file renamed
        result.ShouldContain("Renamed");
        File.Exists(circleFile).ShouldBeFalse("Circle.cs should be gone");
        File.Exists(Path.Combine(circleDir, "Ellipse.cs")).ShouldBeTrue("Ellipse.cs should exist");

        // Assert — associated Designer.cs also renamed
        File.Exists(designerFile).ShouldBeFalse("Circle.Designer.cs should be gone");
        File.Exists(Path.Combine(circleDir, "Ellipse.Designer.cs")).ShouldBeTrue("Ellipse.Designer.cs should exist");

        // Assert — result mentions the designer file
        result.ShouldContain("Ellipse.Designer.cs");
    }

    [Fact]
    public async Task RenameSymbol_RenameFile_AlsoRenamesResxFiles()
    {
        // Arrange — create .resx and .Designer.resx files alongside Circle.cs
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circleFile = CircleFile(ws);
        string circleDir = Path.GetDirectoryName(circleFile)!;
        string resxFile = Path.Combine(circleDir, "Circle.resx");
        string designerResxFile = Path.Combine(circleDir, "Circle.Designer.resx");
        await File.WriteAllTextAsync(resxFile, "<root></root>\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(designerResxFile, "<root></root>\n", TestContext.Current.CancellationToken);

        // Act
        string result = await tools.RenameSymbol(Loc(circleFile, 3, 14), "Circle", "Ellipse", renameFile: true, ct: TestContext.Current.CancellationToken);

        // Assert — both .resx files renamed
        result.ShouldContain("Renamed");
        File.Exists(resxFile).ShouldBeFalse("Circle.resx should be gone");
        File.Exists(Path.Combine(circleDir, "Ellipse.resx")).ShouldBeTrue("Ellipse.resx should exist");
        File.Exists(designerResxFile).ShouldBeFalse("Circle.Designer.resx should be gone");
        File.Exists(Path.Combine(circleDir, "Ellipse.Designer.resx")).ShouldBeTrue("Ellipse.Designer.resx should exist");
    }

    [Fact]
    public async Task RenameSymbol_RenameFile_NoAssociatedFiles_SkipsGracefully()
    {
        // Arrange — no associated files exist; the FileNotFoundException catch should trigger
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circleFile = CircleFile(ws);
        string circleDir = Path.GetDirectoryName(circleFile)!;

        // Act — rename with renameFile but no .Designer.cs, .resx, or .Designer.resx exist
        string result = await tools.RenameSymbol(Loc(circleFile, 3, 14), "Circle", "Ellipse", renameFile: true, ct: TestContext.Current.CancellationToken);

        // Assert — rename succeeds without errors; only the primary file is renamed
        result.ShouldContain("Renamed");
        File.Exists(circleFile).ShouldBeFalse("Circle.cs should be gone");
        File.Exists(Path.Combine(circleDir, "Ellipse.cs")).ShouldBeTrue("Ellipse.cs should exist");
        File.Exists(Path.Combine(circleDir, "Ellipse.Designer.cs")).ShouldBeFalse("No Designer.cs should be created");
        File.Exists(Path.Combine(circleDir, "Ellipse.resx")).ShouldBeFalse("No .resx should be created");
    }
}
