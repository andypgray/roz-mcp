using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Tests.Infrastructure;

[CollectionDefinition("MsBuildBootstrapEnvVar", DisableParallelization = true)]
public class MsBuildBootstrapEnvVarCollection;

[Collection("MsBuildBootstrapEnvVar")]
public sealed class MsBuildBootstrapTests
{
    [Fact]
    public void SelectBestInstance_EmptyList_ReturnsNull()
    {
        VsInstance? result = MsBuildBootstrap.SelectBestInstance([]);
        result.ShouldBeNull();
    }

    [Fact]
    public void SelectBestInstance_PrefersStableOverPreview()
    {
        // Arrange — VS 2022 (17.x) and a preview VS 18 both installed.
        VsInstance vs2022 = new(@"C:\VS\2022", new Version(17, 14, 100), "VS 2022");
        VsInstance vs18Preview = new(@"C:\VS\18", new Version(18, 4, 200), "VS 18");

        // Act — preview has higher version but stable should still win.
        VsInstance? selected = MsBuildBootstrap.SelectBestInstance([vs18Preview, vs2022]);

        // Assert
        selected.ShouldBe(vs2022);
    }

    [Fact]
    public void SelectBestInstance_AmongStable_PicksHighestVersion()
    {
        VsInstance vs2019 = new(@"C:\VS\2019", new Version(16, 11, 500), "VS 2019");
        VsInstance vs2022Old = new(@"C:\VS\2022old", new Version(17, 0, 0), "VS 2022");
        VsInstance vs2022New = new(@"C:\VS\2022new", new Version(17, 14, 100), "VS 2022");

        VsInstance? selected = MsBuildBootstrap.SelectBestInstance([vs2019, vs2022Old, vs2022New]);

        selected.ShouldBe(vs2022New);
    }

    [Fact]
    public void SelectBestInstance_OnlyPreview_FallsBackToHighest()
    {
        // No 16/17 installed; pick highest among newer.
        VsInstance vs18 = new(@"C:\VS\18", new Version(18, 4, 200), "VS 18");
        VsInstance vs26 = new(@"C:\VS\26", new Version(26, 0, 100), "VS 26");

        VsInstance? selected = MsBuildBootstrap.SelectBestInstance([vs18, vs26]);

        selected.ShouldBe(vs26);
    }

    [Fact]
    public void Initialize_OverrideEnvVar_NonExistentDir_Throws()
    {
        string? original = Environment.GetEnvironmentVariable(RozEnvVars.VsInstallPath.Name);
        try
        {
            Environment.SetEnvironmentVariable(RozEnvVars.VsInstallPath.Name, @"C:\__does_not_exist_for_test__");

            Action act = () => MsBuildBootstrap.Initialize();

            InvalidOperationException ex = act.ShouldThrow<InvalidOperationException>();
            ex.Message.ShouldContain(RozEnvVars.VsInstallPath.Name);
            ex.Message.ShouldContain("not an existing directory");
        }
        finally
        {
            Environment.SetEnvironmentVariable(RozEnvVars.VsInstallPath.Name, original);
        }
    }

    [Fact]
    public void Initialize_OverrideEnvVar_DirWithoutMSBuild_Throws()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MsBuildBootstrapTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string? original = Environment.GetEnvironmentVariable(RozEnvVars.VsInstallPath.Name);
        try
        {
            Environment.SetEnvironmentVariable(RozEnvVars.VsInstallPath.Name, tempDir);

            Action act = () => MsBuildBootstrap.Initialize();

            InvalidOperationException ex = act.ShouldThrow<InvalidOperationException>();
            ex.Message.ShouldContain("MSBuild.exe not found");
        }
        finally
        {
            Environment.SetEnvironmentVariable(RozEnvVars.VsInstallPath.Name, original);
            Directory.Delete(tempDir, true);
        }
    }
}
