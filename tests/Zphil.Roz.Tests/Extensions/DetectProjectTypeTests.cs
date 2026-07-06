using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Extensions;

public class DetectProjectTypeTests(WorkspaceFixture fixture)
{
    [Theory]
    [InlineData("TestFixture", "classlib")]
    [InlineData("TestFixture.Tests", "test")]
    [InlineData("TestFixture.Razor", "razor")]
    [InlineData("TestFixture.Minimal", "classlib")]
    public async Task DetectProjectType_KnownProject_ReturnsExpectedType(string projectName, string expected)
    {
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Project project = solution.Projects.Single(p => p.Name == projectName);

        string? result = project.DetectProjectType();

        result.ShouldBe(expected);
    }

    // ── SDK-type detection via fake csproj files ────────────────────────────

    [Theory]
    [InlineData("Microsoft.NET.Sdk.BlazorWebAssembly", "blazor-wasm")]
    [InlineData("Microsoft.NET.Sdk.Web", "web")]
    [InlineData("Microsoft.NET.Sdk.Worker", "worker")]
    [InlineData("Microsoft.NET.Sdk.Maui", "maui")]
    public async Task DetectProjectType_SpecialSdk_ReturnsExpectedType(string sdk, string expected)
    {
        var csproj = $"<Project Sdk=\"{sdk}\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>";

        string? result = await DetectTypeForCsprojAsync(csproj);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task DetectProjectType_UnknownSdk_ReturnsNull()
    {
        const string csproj = """
                              <Project Sdk="SomeUnknown.Sdk">
                                <PropertyGroup>
                                  <TargetFramework>net8.0</TargetFramework>
                                </PropertyGroup>
                              </Project>
                              """;

        string? result = await DetectTypeForCsprojAsync(csproj);

        result.ShouldBeNull();
    }

    [Theory]
    [InlineData("AzureFunctionsVersion", "v4", "azure-functions")]
    [InlineData("OutputType", "Exe", "console")]
    [InlineData("UseWPF", "true", "wpf")]
    [InlineData("UseWindowsForms", "true", "winforms")]
    [InlineData("UseMaui", "true", "maui")]
    public async Task DetectProjectType_PropertyGroupValues_DetectedCorrectly(string propertyName, string propertyValue, string expected)
    {
        var csproj = $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n    <{propertyName}>{propertyValue}</{propertyName}>\n  </PropertyGroup>\n</Project>";

        string? result = await DetectTypeForCsprojAsync(csproj);

        result.ShouldBe(expected);
    }

    // ── .NET Framework conventions: WinExe + <Reference> (no Use* property) ──

    [Theory]
    [InlineData("<OutputType>WinExe</OutputType>", "", "console")]
    [InlineData("<OutputType>WinExe</OutputType>", "<Reference Include=\"System.Windows.Forms\" />", "winforms")]
    [InlineData("", "<Reference Include=\"System.Windows.Forms, Version=4.0.0.0, Culture=neutral\" />", "winforms")]
    [InlineData("", "<Reference Include=\"PresentationFramework\" />", "wpf")]
    public async Task DetectProjectType_FrameworkConventions_DetectedCorrectly(
        string extraProperty, string referenceItem, string expected)
    {
        string itemGroup = referenceItem.Length > 0 ? $"<ItemGroup>{referenceItem}</ItemGroup>" : "";
        var csproj = $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n    {extraProperty}\n  </PropertyGroup>\n  {itemGroup}\n</Project>";

        // Act
        string? result = await DetectTypeForCsprojAsync(csproj);

        // Assert
        result.ShouldBe(expected);
    }

    // ── DetectUiFramework pure mapping ──────────────────────────────────────

    [Theory]
    [InlineData("System.Windows.Forms", "winforms")]
    [InlineData("PresentationFramework", "wpf")]
    [InlineData("presentationframework", "wpf")]
    [InlineData("SYSTEM.WINDOWS.FORMS", "winforms")]
    public void DetectUiFramework_KnownAssembly_MapsToTag(string assembly, string expected) => ProjectExtensions.DetectUiFramework([assembly]).ShouldBe(expected);

    [Fact]
    public void DetectUiFramework_BothPresent_WpfWins()
    {
        ProjectExtensions.DetectUiFramework(["System.Windows.Forms", "PresentationFramework"])
            .ShouldBe("wpf");
    }

    [Fact]
    public void DetectUiFramework_NoUiAssembly_ReturnsNull()
    {
        ProjectExtensions.DetectUiFramework(["System.Linq", "Newtonsoft.Json"]).ShouldBeNull();
        ProjectExtensions.DetectUiFramework([]).ShouldBeNull();
    }

    [Fact]
    public void DetectProjectType_NoFilePath_ReturnsNull()
    {
        // No csproj on disk and no UI metadata references → UI floor is null.
        using var workspace = new AdhocWorkspace();
        Project project = workspace.AddProject("InMemory", LanguageNames.CSharp);

        project.DetectProjectType().ShouldBeNull();
    }

    private static async Task<string?> DetectTypeForCsprojAsync(string csprojContent)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"roslyntest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string csprojPath = Path.Combine(tempDir, "App.csproj");
            await File.WriteAllTextAsync(csprojPath, csprojContent, TestContext.Current.CancellationToken);
            string slnPath = Path.Combine(tempDir, "App.sln");
            await File.WriteAllTextAsync(slnPath,
                "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"App\", \"App.csproj\", \"{{{Guid.NewGuid()}}}\"\n" +
                "EndProject\n", TestContext.Current.CancellationToken);

            var service = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance, slnPath);
            await using (service)
            {
                Solution solution = await service.GetSolutionAsync(TestContext.Current.CancellationToken);
                Project project = solution.Projects.Single();
                return project.DetectProjectType();
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
