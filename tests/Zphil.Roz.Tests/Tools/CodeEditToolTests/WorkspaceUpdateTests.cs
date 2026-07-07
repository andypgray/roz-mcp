using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Services;
using Zphil.Roz.Symbols;
using Zphil.Roz.Tests.Fixtures;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Verifies that workspace mutations (ScheduleFileChanged and ReplaceSymbolAsync) update the
///     in-memory solution so that subsequent GetSolutionAsync calls observe the changes.
/// </summary>
public class WorkspaceUpdateTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task ScheduleFileChanged_ThenGetSolution_ReflectsChanges()
    {
        // Arrange
        WorkspaceManager ws = Fixture.WorkspaceManager;
        string circleFile = CircleFile(Fixture);

        // Modify the file on disk and notify the workspace
        string originalContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string modifiedContent = originalContent.Replace("Math.PI", "3.14159");
        await File.WriteAllTextAsync(circleFile, modifiedContent, TestContext.Current.CancellationToken);
        ws.ScheduleFileChanged(circleFile, modifiedContent, Encoding.UTF8);

        // Act — get solution (should drain the pending update)
        Solution solution = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Assert — the in-memory document should reflect the change
        Document? doc = solution.GetDocumentByPath(circleFile);
        doc.ShouldNotBeNull();
        SourceText text = await doc.GetTextAsync(TestContext.Current.CancellationToken);
        text.ToString().ShouldContain("3.14159");
        text.ToString().ShouldNotContain("Math.PI");
    }

    [Fact]
    public async Task ReplaceSymbol_ThenCheckSolution_DocumentUpdated()
    {
        // Arrange
        WorkspaceManager ws = Fixture.WorkspaceManager;
        var editService = new SymbolEditService(ws, Fixture.BaselineManager, new EditSymbolResolver(ws), new EditVerificationService(ws), NullLogger<SymbolEditService>.Instance);
        string shapeFile = ShapeFile(Fixture);

        // Act — perform one replace_symbol
        await editService.ReplaceSymbolAsync(shapeFile, "Describe", """public virtual string Describe() => "edited";""", ct: TestContext.Current.CancellationToken);

        // Assert — solution should reflect the change
        Solution solution = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
        Document? doc = solution.GetDocumentByPath(shapeFile);
        doc.ShouldNotBeNull();
        SourceText text = await doc.GetTextAsync(TestContext.Current.CancellationToken);
        text.ToString().ShouldContain("edited");
    }

    [Fact]
    public async Task ReplaceSymbol_Twice_SecondCallUsesUpdatedDocument()
    {
        // Arrange
        WorkspaceManager ws = Fixture.WorkspaceManager;
        var editService = new SymbolEditService(ws, Fixture.BaselineManager, new EditSymbolResolver(ws), new EditVerificationService(ws), NullLogger<SymbolEditService>.Instance);
        string shapeFile = ShapeFile(Fixture);

        // Act — first replace_symbol
        await editService.ReplaceSymbolAsync(shapeFile, "Describe", """public virtual string Describe() => "first-edit";""", ct: TestContext.Current.CancellationToken);

        // Verify file on disk has first edit
        string afterFirst = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        afterFirst.ShouldContain("first-edit");

        // Verify solution has first edit
        Solution solutionAfterFirst = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
        Document? docAfterFirst = solutionAfterFirst.GetDocumentByPath(shapeFile);
        SourceText textAfterFirst = await docAfterFirst!.GetTextAsync(TestContext.Current.CancellationToken);
        textAfterFirst.ToString().ShouldContain("first-edit");

        // Act — second replace_symbol on same file, different symbol
        await editService.ReplaceSymbolAsync(shapeFile, "Area", "public abstract double SecondEditArea { get; }", ct: TestContext.Current.CancellationToken);

        // Assert — file should have both edits
        string afterSecond = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        afterSecond.ShouldContain("first-edit");
        afterSecond.ShouldContain("SecondEditArea");

        // Assert — the drained in-memory solution reflects both edits too (black-box, no re-implementation)
        Solution solutionAfterSecond = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
        SourceText textAfterSecond = await solutionAfterSecond.GetDocumentByPath(shapeFile)!.GetTextAsync(TestContext.Current.CancellationToken);
        textAfterSecond.ToString().ShouldContain("first-edit");
        textAfterSecond.ToString().ShouldContain("SecondEditArea");
    }

    [Fact]
    public async Task TwoScheduleFileChanged_ThenGetSolution_BothReflected()
    {
        // Arrange
        WorkspaceManager ws = Fixture.WorkspaceManager;
        string circleFile = CircleFile(Fixture);
        string shapeFile = ShapeFile(Fixture);

        // First change: modify Circle.cs
        string circleContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string modifiedCircle = circleContent.Replace("Math.PI", "3.14159");
        await File.WriteAllTextAsync(circleFile, modifiedCircle, TestContext.Current.CancellationToken);
        ws.ScheduleFileChanged(circleFile, modifiedCircle, Encoding.UTF8);

        // Second change: modify Shape.cs
        string shapeContent = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        string modifiedShape = shapeContent.Replace("GetType().Name", "\"Shape\"");
        await File.WriteAllTextAsync(shapeFile, modifiedShape, TestContext.Current.CancellationToken);
        ws.ScheduleFileChanged(shapeFile, modifiedShape, Encoding.UTF8);

        // Act — get solution (should drain both pending updates)
        Solution solution = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Assert — both changes should be reflected
        Document? circleDoc = solution.GetDocumentByPath(circleFile);
        circleDoc.ShouldNotBeNull();
        SourceText circleText = await circleDoc.GetTextAsync(TestContext.Current.CancellationToken);
        circleText.ToString().ShouldContain("3.14159");

        Document? shapeDoc = solution.GetDocumentByPath(shapeFile);
        shapeDoc.ShouldNotBeNull();
        SourceText shapeText = await shapeDoc.GetTextAsync(TestContext.Current.CancellationToken);
        shapeText.ToString().ShouldContain("\"Shape\"");
    }
}
