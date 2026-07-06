using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Tests.Infrastructure;

public sealed class VsWhereLocatorTests
{
    [Fact]
    public void ParseInstances_RealVswhereOutput_ExtractsKeyFields()
    {
        // Sampled from `vswhere -all -prerelease -format json`. Trimmed to two entries
        // covering both the stable VS 2022 and a higher-version preview that the BuildHost
        // would otherwise pick by default.
        const string json = """
                            [
                              {
                                "instanceId": "2d7df52c",
                                "installationPath": "C:\\Program Files\\Microsoft Visual Studio\\2022\\Community",
                                "installationVersion": "17.14.36915.13",
                                "displayName": "Visual Studio Community 2022",
                                "isPrerelease": false
                              },
                              {
                                "instanceId": "e6562aad",
                                "installationPath": "C:\\Program Files\\Microsoft Visual Studio\\18\\Community",
                                "installationVersion": "18.4.11605.240",
                                "displayName": "Visual Studio Community 18"
                              }
                            ]
                            """;

        IReadOnlyList<VsInstance> instances = VsWhereLocator.ParseInstances(json);

        instances.Count.ShouldBe(2);
        instances[0].InstallationPath.ShouldBe(@"C:\Program Files\Microsoft Visual Studio\2022\Community");
        instances[0].Version.ShouldBe(new Version(17, 14, 36915, 13));
        instances[0].Name.ShouldBe("Visual Studio Community 2022");
        instances[1].Version.ShouldBe(new Version(18, 4, 11605, 240));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("{}")]
    public void ParseInstances_EmptyOrNonArray_ReturnsEmpty(string json) =>
        VsWhereLocator.ParseInstances(json).ShouldBeEmpty();

    [Fact]
    public void ParseInstances_EntryMissingPath_IsSkipped()
    {
        const string json = """
                            [
                              { "installationVersion": "17.0.0" },
                              {
                                "installationPath": "C:\\VS\\Good",
                                "installationVersion": "17.14.0"
                              }
                            ]
                            """;

        IReadOnlyList<VsInstance> instances = VsWhereLocator.ParseInstances(json);

        instances.Count.ShouldBe(1);
        instances[0].InstallationPath.ShouldBe(@"C:\VS\Good");
    }

    [Fact]
    public void ParseInstances_UnparseableVersion_IsSkipped()
    {
        const string json = """
                            [
                              {
                                "installationPath": "C:\\VS\\Bad",
                                "installationVersion": "not-a-version"
                              }
                            ]
                            """;

        VsWhereLocator.ParseInstances(json).ShouldBeEmpty();
    }

    [Fact]
    public void ParseInstances_MissingDisplayName_DefaultsToVisualStudio()
    {
        const string json = """
                            [
                              {
                                "installationPath": "C:\\VS\\NoName",
                                "installationVersion": "17.0.0"
                              }
                            ]
                            """;

        IReadOnlyList<VsInstance> instances = VsWhereLocator.ParseInstances(json);

        instances[0].Name.ShouldBe("Visual Studio");
    }
}
