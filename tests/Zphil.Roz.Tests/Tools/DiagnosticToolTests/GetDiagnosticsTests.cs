using Microsoft.CodeAnalysis;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.DiagnosticToolTests;

public class GetDiagnosticsTests(WorkspaceFixture fixture)
{
    private readonly DiagnosticTools tools = TestFileHelper.CreateDiagnosticTools(fixture);

    [Fact]
    public async Task GetDiagnostics_ErrorSeverity_ReturnsOnlyHexagonErrors()
    {
        // Act
        string result = await tools.GetDiagnostics(severity: DiagnosticSeverity.Error, ct: TestContext.Current.CancellationToken);

        // Assert — only CS0246 errors from Hexagon.cs (intentional compilation error fixture)
        result.ShouldContain("CS0246");
        result.ShouldContain("Hexagon.cs");
    }

    [Fact]
    public async Task GetDiagnostics_DefaultSeverity_ReturnsExpectedDiagnostics()
    {
        // Act
        string result = await tools.GetDiagnostics(ct: TestContext.Current.CancellationToken);

        // Assert — CS0246 errors from Hexagon.cs + CS0219 warning in MultiTfmConsumer.cs
        result.ShouldContain("CS0246");
        result.ShouldContain("CS0219");
    }

    [Fact]
    public async Task GetDiagnostics_ScopedToFile_ReturnsFileSpecificResults()
    {
        // Arrange
        string iShapeFile = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.GetDiagnostics([iShapeFile], DiagnosticSeverity.Error, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No diagnostics");
    }

    [Fact]
    public async Task GetDiagnostics_NonExistentFile_ReturnsError()
    {
        // Act
        string result = await tools.GetDiagnostics(["NonExistent.cs"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Error");
        result.ShouldContain("not found");
        result.ShouldNotContain("No diagnostics");
    }

    [Fact]
    public async Task GetDiagnostics_DefaultExcludesTests_ExcludesTestProjectDiagnostics()
    {
        // Act — default excludes test projects (includeTests defaults to false)
        string result = await tools.GetDiagnostics(severity: DiagnosticSeverity.Error, ct: TestContext.Current.CancellationToken);

        // Assert — should still return clean results (no test project diagnostics either)
        result.ShouldNotContain("TestFixture.Tests");
    }

    [Fact]
    public async Task GetDiagnostics_CleanFixture_OmitsNuGetHint()
    {
        // Act — the fixture has a handful of intentional errors, well under the 20-error
        // floor, so the assembly-resolution hint must not fire.
        string result = await tools.GetDiagnostics(severity: DiagnosticSeverity.Error, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("assembly-resolution codes");
        result.ShouldNotContain("NuGet isn't restored");
    }

    [Fact]
    public async Task GetDiagnostics_OnlyCompilerErrors_OmitsFixerSummary()
    {
        // Act — CS0246 has no analyzer fixer in the catalog (built-in C# fixers aren't
        // loaded as AnalyzerReferences), so the fixer-summary block must not appear.
        string result = await tools.GetDiagnostics(severity: DiagnosticSeverity.Error, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("CS0246");
        result.ShouldNotContain("Available analyzer fixes");
    }
}
