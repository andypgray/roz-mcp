using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Post-rename stray-reference scan: surfaces .cs/.razor files outside the loaded
///     workspace that still textually reference the old name. Each test plants extra
///     files into the fixture tree; ResetAsync removes them between tests.
/// </summary>
public class RenameSymbolStrayReferenceTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task RenameSymbol_StrayOutsideAnyProject_IncludedInWarning()
    {
        // Arrange — plant a stray file at solution root, outside any csproj's compile globs
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circleFile = CircleFile(ws);
        await PlantStrayAsync(ws, "Strays/StrayConsumer.cs", "var Radius = 5.0;\n");

        // Act — rename the public Radius property
        string result = await tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "R", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("WARNING:");
        result.ShouldContain("StrayConsumer.cs");
        result.ShouldContain("'Radius'");
    }

    [Fact]
    public async Task RenameSymbol_InSolutionSameNamedSymbol_NotFlagged()
    {
        // Arrange — three independent `Count` symbols live in different loaded files:
        // ShapeCollection.Count (the target), MutableState.Count, and
        // OuterContainer.InnerProcessor.Count (in TypeKindExamples.cs). Renaming the first
        // leaves the other two files unchanged yet still textually matching \bCount\b. Because
        // they ARE in the loaded solution, they must not be reported as external strays.
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string shapeCollectionFile = ShapeCollectionFile(ws);
        // A genuinely-external stray (outside any project's compile globs) guards that the scan
        // still surfaces real external references after the fix.
        await PlantStrayAsync(ws, "Strays/ExternalCountUser.cs", "var Count = 1;\n");

        // Act — rename ShapeCollection.Count; cursor on the `Count` token at line 29, col 16
        string result = await tools.RenameSymbol(Loc(shapeCollectionFile, 29, 16), "Count", "ItemCount", ct: TestContext.Current.CancellationToken);

        // Assert — the genuinely-external stray is flagged ...
        result.ShouldContain("WARNING:");
        result.ShouldContain("ExternalCountUser.cs");
        // ... but the unchanged in-solution files holding same-named members are NOT.
        result.ShouldNotContain("MutableState.cs");
        result.ShouldNotContain("TypeKindExamples.cs");
    }

    [Fact]
    public async Task RenameSymbol_StrayUnderBin_NotInWarning()
    {
        // Arrange
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circleFile = CircleFile(ws);
        await PlantStrayAsync(ws, "bin/Debug/StrayConsumer.cs", "var Radius = 5.0;\n");

        // Act
        string result = await tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "R", ct: TestContext.Current.CancellationToken);

        // Assert — the bin/ directory tree is skipped wholesale
        result.ShouldNotContain("WARNING:");
    }

    [Fact]
    public async Task RenameSymbol_StrayUnderObj_NotInWarning()
    {
        // Arrange
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circleFile = CircleFile(ws);
        await PlantStrayAsync(ws, "obj/Stray/StrayConsumer.cs", "var Radius = 5.0;\n");

        // Act
        string result = await tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "R", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("WARNING:");
    }

    [Fact]
    public async Task RenameSymbol_GeneratedSuffix_NotInWarning()
    {
        // Arrange — *.g.cs files are excluded; they're regenerated on build
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circleFile = CircleFile(ws);
        await PlantStrayAsync(ws, "Strays/Generated.g.cs", "var Radius = 5.0;\n");

        // Act
        string result = await tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "R", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("WARNING:");
    }

    [Fact]
    public async Task RenameSymbol_PrivateField_StrayScanSkipped()
    {
        // Arrange — _disposed in ShapeCollection is a private field; stray text matches
        // are noise because no external code can semantically reference it.
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string shapeCollectionFile = ShapeCollectionFile(ws);
        await PlantStrayAsync(ws, "Strays/StrayConsumer.cs", "var _disposed = false;\n");

        // Act — rename _disposed → _isDisposed (private field, scan should be skipped)
        string result = await tools.RenameSymbol(shapeCollectionFile, "_disposed", "_isDisposed", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("WARNING:");
    }

    [Fact]
    public async Task RenameSymbol_LocalFunction_StrayScanSkipped()
    {
        // Arrange — GetArea is a local function inside CalculateTotal
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string localFile = LocalFunctionExampleFile(ws);
        await PlantStrayAsync(ws, "Strays/StrayConsumer.cs", "double GetArea() => 0;\n");

        // Act — rename the GetArea local function (line 16, col 16)
        string result = await tools.RenameSymbol(Loc(localFile, 16, 16), "GetArea", "ComputeArea", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("WARNING:");
    }

    [Fact]
    public async Task RenameSymbol_NoStrays_NoWarning()
    {
        // Arrange — no strays planted
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circleFile = CircleFile(ws);

        // Act
        string result = await tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "R", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("WARNING:");
    }

    [Fact]
    public async Task RenameSymbol_TwelveStrays_TruncatedToTen()
    {
        // Arrange — plant 12 stray files; output should list 10 and summarize the rest
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circleFile = CircleFile(ws);
        for (var i = 1; i <= 12; i++)
        {
            await PlantStrayAsync(ws, $"Strays/Stray{i:D2}.cs", "var Radius = 5.0;\n");
        }

        // Act
        string result = await tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "R", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("WARNING: 12 file(s)");
        result.ShouldContain("… and 2 more file(s).");
    }

    [Fact]
    public async Task RenameSymbol_SubstringNearMiss_NotInWarning()
    {
        // Arrange — `RadiusValue` contains `Radius` but is a different identifier
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circleFile = CircleFile(ws);
        await PlantStrayAsync(ws, "Strays/StrayConsumer.cs", "var RadiusValue = 5.0;\n");

        // Act
        string result = await tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "R", ct: TestContext.Current.CancellationToken);

        // Assert — word-boundary regex rejects substring-only matches
        result.ShouldNotContain("WARNING:");
    }

    [Fact]
    public async Task RenameSymbol_RazorStray_IncludedInWarning()
    {
        // Arrange — .razor files are scanned alongside .cs
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circleFile = CircleFile(ws);
        await PlantStrayAsync(ws, "Strays/StrayComponent.razor", "<p>@Radius</p>\n");

        // Act
        string result = await tools.RenameSymbol(Loc(circleFile, 5, 19), "Radius", "R", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("WARNING:");
        result.ShouldContain("StrayComponent.razor");
    }

    private static async Task PlantStrayAsync(ITestWorkspace ws, string relativePath, string content)
    {
        string absPath = Path.Combine(
            ws.WorkspaceManager.SolutionDirectory!,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
        await File.WriteAllTextAsync(absPath, content);
    }
}
