using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using Zphil.Roz.Extensions;
using Zphil.Roz.Services;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Services;

public class WorkspaceServiceTests(WorkspaceFixture fixture)
{
    [Fact]
    public async Task GetTargetFramework_CSharpProject_ReturnsTfm()
    {
        // Arrange
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Project project = solution.Projects.First(p => p.Name == "TestFixture");

        // Act
        string? result = WorkspaceService.GetTargetFramework(project);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldMatch(@"^net\d+\.\d+$");
    }

    [Fact]
    public void GetTargetFramework_NonCSharpParseOptions_ReturnsNull()
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        Project project = workspace.AddProject("VbProject", LanguageNames.VisualBasic)
            .WithParseOptions(new VisualBasicParseOptions());

        // Act
        string? result = WorkspaceService.GetTargetFramework(project);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetLanguageVersion_CSharpProject_ReturnsVersion()
    {
        // Arrange
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Project project = solution.Projects.First(p => p.Name == "TestFixture");

        // Act
        string? result = WorkspaceService.GetLanguageVersion(project);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldMatch(@"^\d+\.\d+$");
    }

    [Fact]
    public void GetLanguageVersion_NonCSharpParseOptions_ReturnsNull()
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        Project project = workspace.AddProject("VbProject", LanguageNames.VisualBasic)
            .WithParseOptions(new VisualBasicParseOptions());

        // Act
        string? result = WorkspaceService.GetLanguageVersion(project);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetNullableContext_CSharpProjectWithEnable_ReturnsEnable()
    {
        // Arrange
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Project project = solution.Projects.First(p => p.Name == "TestFixture");

        // Act
        string? result = WorkspaceService.GetNullableContext(project);

        // Assert
        result.ShouldBe("enable");
    }

    [Fact]
    public void GetNullableContext_NonCSharpCompilationOptions_ReturnsNull()
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        Project project = workspace.AddProject("VbProject", LanguageNames.VisualBasic)
            .WithCompilationOptions(new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Act
        string? result = WorkspaceService.GetNullableContext(project);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public void GetNullableContext_Warnings_ReturnsWarnings()
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Warnings);
        Project project = workspace.AddProject("TestProject", LanguageNames.CSharp)
            .WithCompilationOptions(options);

        // Act
        string? result = WorkspaceService.GetNullableContext(project);

        // Assert
        result.ShouldBe("warnings");
    }

    [Fact]
    public void GetNullableContext_Annotations_ReturnsAnnotations()
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Annotations);
        Project project = workspace.AddProject("TestProject", LanguageNames.CSharp)
            .WithCompilationOptions(options);

        // Act
        string? result = WorkspaceService.GetNullableContext(project);

        // Assert
        result.ShouldBe("annotations");
    }

    [Fact]
    public void GetNullableContext_Disable_ReturnsDisable()
    {
        // Arrange
        using var workspace = new AdhocWorkspace();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Disable);
        Project project = workspace.AddProject("TestProject", LanguageNames.CSharp)
            .WithCompilationOptions(options);

        // Act
        string? result = WorkspaceService.GetNullableContext(project);

        // Assert
        result.ShouldBe("disable");
    }

    [Fact]
    public async Task CountDocuments_ExcludesGeneratedFiles()
    {
        // Arrange
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        List<Project> projects = solution.Projects.OrderBy(p => p.Name).ToList();

        // Act
        int count = WorkspaceService.CountDocuments(projects);

        // Assert
        count.ShouldBeGreaterThan(0);
        int totalIncludingGenerated = projects.Sum(p => p.Documents.Count());
        count.ShouldBeLessThanOrEqualTo(totalIncludingGenerated);
    }

    // ── TFM preprocessor-symbol seam ────────────────────────────────────────

    [Theory]
    [InlineData("NET48", "net48")]
    [InlineData("NET472", "net472")]
    [InlineData("NET462", "net462")]
    [InlineData("NET481", "net481")]
    [InlineData("NET20", "net20")]
    [InlineData("NET8_0", "net8.0")]
    [InlineData("NET10_0", "net10.0")]
    [InlineData("NETSTANDARD2_0", "netstandard2.0")]
    [InlineData("NETCOREAPP3_1", "netcoreapp3.1")]
    public void TfmFromPreprocessorSymbols_MatchingSymbol_ReturnsMoniker(string symbol, string expected) => WorkspaceService.TfmFromPreprocessorSymbols([symbol]).ShouldBe(expected);

    [Theory]
    [InlineData("NET48_OR_GREATER")]
    [InlineData("NETFRAMEWORK")]
    [InlineData("NET")]
    [InlineData("NETCOREAPP")]
    [InlineData("NETSTANDARD")]
    [InlineData("DEBUG")]
    [InlineData("TRACE")]
    public void TfmFromPreprocessorSymbols_NonTfmSymbol_ReturnsNull(string symbol) => WorkspaceService.TfmFromPreprocessorSymbols([symbol]).ShouldBeNull();

    [Fact]
    public void TfmFromPreprocessorSymbols_PicksTfmAmongNoise()
    {
        string[] symbols = ["DEBUG", "TRACE", "NETFRAMEWORK", "NET48_OR_GREATER", "NET48"];
        WorkspaceService.TfmFromPreprocessorSymbols(symbols).ShouldBe("net48");
    }

    [Fact]
    public void TfmFromPreprocessorSymbols_NoSymbols_ReturnsNull() => WorkspaceService.TfmFromPreprocessorSymbols([]).ShouldBeNull();

    // ── Legacy <TargetFrameworkVersion> normalization ───────────────────────

    [Theory]
    [InlineData("v4.8", "net48")]
    [InlineData("v4.7.2", "net472")]
    [InlineData("v4.6.1", "net461")]
    [InlineData("v3.5", "net35")]
    [InlineData("v2.0", "net20")]
    [InlineData("V4.8", "net48")]
    [InlineData(" v4.8 ", "net48")]
    [InlineData("4.8", "net48")]
    public void NormalizeFrameworkVersion_LegacyValue_ConvertsToMoniker(string value, string expected) => ProjectExtensions.NormalizeFrameworkVersion(value).ShouldBe(expected);

    // ── csproj TFM read (priority + namespace-agnostic) ─────────────────────

    [Theory]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>", "net8.0")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>NET8.0</TargetFramework></PropertyGroup></Project>", "net8.0")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup></Project>", "net8.0")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>", null)]
    [InlineData("<Project></Project>", null)]
    public void GetTargetFrameworkFromCsproj_VariousShapes_ReadsTfm(string xml, string? expected)
    {
        var doc = XDocument.Parse(xml);
        ProjectExtensions.GetTargetFrameworkFromCsproj(doc).ShouldBe(expected);
    }

    [Fact]
    public void GetTargetFrameworkFromCsproj_LegacyNonSdkNamespace_ReadsFrameworkVersion()
    {
        var doc = XDocument.Parse(
            """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
              </PropertyGroup>
            </Project>
            """);

        ProjectExtensions.GetTargetFrameworkFromCsproj(doc).ShouldBe("net48");
    }

    [Fact]
    public void GetTargetFrameworkFromCsproj_TargetFrameworkWinsOverLegacyVersion()
    {
        var doc = XDocument.Parse(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        ProjectExtensions.GetTargetFrameworkFromCsproj(doc).ShouldBe("net8.0");
    }

    // ── GetTargetFramework csproj fallback (non-SDK legacy) + TryLoadCsproj ──

    [Fact]
    public async Task GetTargetFramework_NonSdkLegacyProject_FallsBackToCsproj()
    {
        // Arrange — a legacy non-SDK project: C# parse options with no NETxx preprocessor
        // symbols, but a csproj on disk declaring <TargetFrameworkVersion>.
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslyntest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string csprojPath = Path.Combine(tempDir, "Legacy.csproj");
            await File.WriteAllTextAsync(csprojPath,
                """
                <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <PropertyGroup>
                    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
                  </PropertyGroup>
                </Project>
                """, TestContext.Current.CancellationToken);

            ProjectInfo info = ProjectInfo.Create(
                    ProjectId.CreateNewId(), VersionStamp.Default,
                    "Legacy", "Legacy", LanguageNames.CSharp, csprojPath)
                .WithParseOptions(new CSharpParseOptions());

            using var workspace = new AdhocWorkspace();
            Project project = workspace.AddProject(info);

            // Act
            string? result = WorkspaceService.GetTargetFramework(project);

            // Assert
            result.ShouldBe("net48");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TryLoadCsproj_NoFilePath_ReturnsNull()
    {
        using var workspace = new AdhocWorkspace();
        Project project = workspace.AddProject("NoPath", LanguageNames.CSharp);

        ProjectExtensions.TryLoadCsproj(project).ShouldBeNull();
    }

    [Fact]
    public async Task TryLoadCsproj_MalformedCsproj_ReturnsNull()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslyntest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string csprojPath = Path.Combine(tempDir, "Broken.csproj");
            await File.WriteAllTextAsync(csprojPath, "<Project><not-closed>", TestContext.Current.CancellationToken);

            var info = ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Default,
                "Broken", "Broken", LanguageNames.CSharp, csprojPath);
            using var workspace = new AdhocWorkspace();
            Project project = workspace.AddProject(info);

            ProjectExtensions.TryLoadCsproj(project).ShouldBeNull();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
