using Microsoft.CodeAnalysis;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.DiagnosticToolTests;

public class GetDiagnosticsGlobTests(WorkspaceFixture fixture)
{
    private readonly DiagnosticTools tools = TestFileHelper.CreateDiagnosticTools(fixture);

    [Fact]
    public async Task GetDiagnostics_WithGlobPattern_ScopesToMatchedFiles()
    {
        // Act — scope to Shapes/I*.cs via glob (IShape.cs compiles cleanly)
        string result = await tools.GetDiagnostics(["TestFixture/Shapes/I*.cs"], DiagnosticSeverity.Error, ct: TestContext.Current.CancellationToken);

        // Assert — IShape.cs has no errors
        result.ShouldContain("No diagnostics");
    }

    [Fact]
    public async Task GetDiagnostics_WithDoubleStarGlob_MatchesAcrossDirectories()
    {
        // Act — ** glob matches IShape.cs regardless of directory
        string result = await tools.GetDiagnostics(["**/IShape.cs"], DiagnosticSeverity.Error, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No diagnostics");
    }

    [Fact]
    public async Task GetDiagnostics_WithGlobMatchingNoFiles_Throws()
    {
        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.GetDiagnostics(["**/Nonexistent*.cs"], DiagnosticSeverity.Error));
        ex.Message.ShouldContain("matched no files");
    }

    [Fact]
    public async Task GetDiagnostics_WithMixedLiteralAndGlob_Works()
    {
        // Arrange
        string circlePath = fixture.ShapesFile("Circle.cs");

        // Act — one literal path + one glob
        string result = await tools.GetDiagnostics([circlePath, "TestFixture/Shapes/R*.cs"], DiagnosticSeverity.Error, ct: TestContext.Current.CancellationToken);

        // Assert — both resolve and compile cleanly
        result.ShouldContain("No diagnostics");
    }

    [Fact]
    public async Task GetDiagnostics_GlobExceedsMaxFiles_ErrorMentionsProjectHint()
    {
        // Arrange — "**/*.cs" matches all source files in the TestFixture solution (>50 total),
        // exceeding the diagnostics service's hardcoded maxFiles=50 cap.

        // Act & Assert — the per-tool recovery hint must mention the project= escape hatch.
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.GetDiagnostics(["**/*.cs"], DiagnosticSeverity.Error));

        ex.Message.ShouldContain("exceeding maxFiles=50");
        ex.Message.ShouldContain("Or use project=");
    }
}
