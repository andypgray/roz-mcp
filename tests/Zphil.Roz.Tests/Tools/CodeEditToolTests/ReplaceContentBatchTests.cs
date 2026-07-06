using Zphil.Roz.Models;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Tests for replace_content batch semantics — sectioning, partial failure, sequential same-file ops.
/// </summary>
public class ReplaceContentBatchTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    private static string RectangleFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Shapes", "Rectangle.cs");

    [Fact]
    public async Task ReplaceContent_Batch_MultipleFiles_AppliesAll()
    {
        // Arrange
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circle = CircleFile(ws);
        string rect = RectangleFile(ws);

        // Act
        string result = await tools.ReplaceContent([
            new ReplaceContentRequest(circle, "Area", "ComputeArea"),
            new ReplaceContentRequest(rect, "Area", "ComputeArea")
        ], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("=== ");
        result.ShouldContain("Circle.cs");
        result.ShouldContain("Rectangle.cs");
        (await File.ReadAllTextAsync(circle, TestContext.Current.CancellationToken)).ShouldContain("ComputeArea");
        (await File.ReadAllTextAsync(rect, TestContext.Current.CancellationToken)).ShouldContain("ComputeArea");
    }

    [Fact]
    public async Task ReplaceContent_Batch_SingleEntry_OmitsHeaderWrapper()
    {
        // Arrange
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circle = CircleFile(ws);

        // Act — a single-entry batch should bypass FormatBatch headers (N=1 short-circuit).
        string result = await tools.ReplaceContent([new ReplaceContentRequest(circle, "Area", "ComputeArea")], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("=== ");
        result.ShouldContain("Replaced 1 occurrence(s)");
    }

    [Fact]
    public async Task ReplaceContent_Batch_MiddleOpFails_OthersSucceed()
    {
        // Arrange
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circle = CircleFile(ws);
        string rect = RectangleFile(ws);
        string fake = Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "DoesNotExist.cs");

        // Act
        string result = await tools.ReplaceContent([
            new ReplaceContentRequest(circle, "Area", "ComputeArea"),
            new ReplaceContentRequest(fake, "x", "y"),
            new ReplaceContentRequest(rect, "Area", "ComputeArea")
        ], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Circle.cs");
        result.ShouldContain("Rectangle.cs");
        result.ShouldContain("DoesNotExist.cs");
        result.ShouldContain("Error in");
        result.ShouldContain("File not found");

        // Outer files DID apply — partial success, not atomic.
        (await File.ReadAllTextAsync(circle, TestContext.Current.CancellationToken)).ShouldContain("ComputeArea");
        (await File.ReadAllTextAsync(rect, TestContext.Current.CancellationToken)).ShouldContain("ComputeArea");
    }

    [Fact]
    public async Task ReplaceContent_Batch_SameFileTwice_SequentialWrites()
    {
        // Arrange
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circle = CircleFile(ws);

        // Act — second op should see the bytes written by the first op.
        await tools.ReplaceContent([
            new ReplaceContentRequest(circle, "Radius", "Step1"),
            new ReplaceContentRequest(circle, "Step1", "Step2")
        ], ct: TestContext.Current.CancellationToken);

        // Assert
        string final = await File.ReadAllTextAsync(circle, TestContext.Current.CancellationToken);
        final.ShouldContain("Step2");
        final.ShouldNotContain("Step1");
        final.ShouldNotContain("Radius");
    }

    [Fact]
    public async Task ReplaceContent_Batch_RejectsEmptyArray()
    {
        // Arrange
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() =>
            tools.ReplaceContent([]));
        ex.Message.ShouldContain("must not be empty");
    }

    [Fact]
    public async Task ReplaceContent_Batch_MixedLiteralAndRegex_AllApply()
    {
        // Arrange
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circle = CircleFile(ws);
        string rect = RectangleFile(ws);
        string triangle = TriangleFile(ws);

        // Act — flags must not leak between entries
        string result = await tools.ReplaceContent([
            new ReplaceContentRequest(circle, "Math.PI", "PI_LITERAL"),
            new ReplaceContentRequest(rect, @"Width\s*\*\s*Height", "Width_Times_Height", true),
            new ReplaceContentRequest(triangle, "Area.*Perimeter", "AREA_AND_PERIMETER", true, true)
        ], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Circle.cs");
        result.ShouldContain("Rectangle.cs");
        result.ShouldContain("Triangle.cs");
        (await File.ReadAllTextAsync(circle, TestContext.Current.CancellationToken)).ShouldContain("PI_LITERAL");
        (await File.ReadAllTextAsync(rect, TestContext.Current.CancellationToken)).ShouldContain("Width_Times_Height");
        (await File.ReadAllTextAsync(triangle, TestContext.Current.CancellationToken)).ShouldContain("AREA_AND_PERIMETER");
    }

    [Fact]
    public async Task ReplaceContent_Batch_HeaderShowsSearchPreview()
    {
        // Arrange — headers should disambiguate same-file ops by including the search text.
        ITestWorkspace ws = Fixture;
        CodeEditTools tools = CreateEditTools(ws);
        string circle = CircleFile(ws);

        // Act — two ops on the same file with different search terms.
        string result = await tools.ReplaceContent([
            new ReplaceContentRequest(circle, "Radius", "R"),
            new ReplaceContentRequest(circle, "Math.PI", "PI")
        ], ct: TestContext.Current.CancellationToken);

        // Assert — both search terms appear in headers.
        result.ShouldContain("\"Radius\"");
        result.ShouldContain("\"Math.PI\"");
    }
}
