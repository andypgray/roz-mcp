using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.WorkspaceToolTests;

/// <summary>
///     Integration tests for <see cref="WorkspaceTools.GetUnusedReferences" />.
///     Anchored on <c>TestFixture.Minimal</c> — its only source file is <c>public class Marker;</c>,
///     so its <c>&lt;ProjectReference&gt;</c> to <c>TestFixture</c> is guaranteed unused by source.
/// </summary>
public class GetUnusedReferencesTests(WorkspaceFixture fixture)
{
    private readonly WorkspaceTools tools = TestFileHelper.CreateWorkspaceTools(fixture);

    [Fact]
    public async Task GetUnusedReferences_DefaultKind_FlagsUnusedProjectReferenceInMinimal()
    {
        // Act — default kind=Projects, scoped to TestFixture.Minimal which has an unused ref to TestFixture.
        string result = await tools.GetUnusedReferences(project: "TestFixture.Minimal", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Unused references in TestFixture.Minimal");
        result.ShouldContain("unused project: TestFixture");
    }

    [Fact]
    public async Task GetUnusedReferences_DefaultKind_OmitsPackageEntries()
    {
        // Act — default kind=Projects must never report packages.
        string result = await tools.GetUnusedReferences(ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("unused package");
    }

    [Fact]
    public async Task GetUnusedReferences_KindPackages_ExcludesProjectEntries()
    {
        // Act
        string result = await tools.GetUnusedReferences(UnusedReferencesKind.Packages, ct: TestContext.Current.CancellationToken);

        // Assert — kind=Packages must never report project references.
        result.ShouldNotContain("unused project:");
    }

    [Fact]
    public async Task GetUnusedReferences_KindAll_PackagesEntriesIncludeWeakSignalNote()
    {
        // Act
        string result = await tools.GetUnusedReferences(UnusedReferencesKind.All, ct: TestContext.Current.CancellationToken);

        // Assert — when packages are flagged the weak-signal footer must accompany them.
        if (result.Contains("unused package"))
        {
            result.ShouldContain("weak signal");
        }
    }

    [Fact]
    public async Task GetUnusedReferences_ProjectFilter_NoMatch_Throws()
    {
        // Act & Assert — same pattern as every other tool that uses FilterByProjectName.
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            tools.GetUnusedReferences(project: "ThisProjectDoesNotExist"));

        ex.Message.ShouldContain("ThisProjectDoesNotExist");
    }

    [Fact]
    public async Task GetUnusedReferences_KindAll_NeverFlagsFrameworkReferences()
    {
        // Act
        string result = await tools.GetUnusedReferences(UnusedReferencesKind.All, ct: TestContext.Current.CancellationToken);

        // Assert — shared-framework refs must never appear in the package report.
        result.ShouldNotContain("Microsoft.NETCore.App.Ref");
        result.ShouldNotContain("Microsoft.AspNetCore.App.Ref");
        result.ShouldNotContain("System.Runtime");
    }

    [Fact]
    public async Task GetUnusedReferences_TestFixtureTests_DoesNotFlagAnyProjectReference()
    {
        // Arrange — TestFixture.Tests references TestFixture and uses IShape/Circle in its tests,
        // so the ProjectReference is genuinely used. With default kind=Projects, the report should
        // be empty (no projects with hits) and packages should not be analyzed.

        // Act
        string result = await tools.GetUnusedReferences(project: "TestFixture.Tests", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("unused project:");
    }

    [Fact]
    public async Task GetUnusedReferences_DefaultKind_SolutionWide_FormatsFooter()
    {
        // Act — solution-wide default scan; TestFixture.Minimal contributes one unused project.
        string result = await tools.GetUnusedReferences(ct: TestContext.Current.CancellationToken);

        // Assert — at least one hit, footer line present. "reference" matches both the
        // singular and plural wording since the live-solution hit count isn't fixed here.
        result.ShouldContain("unused project");
        result.ShouldContain("unused project reference");
        result.ShouldContain(", across");
    }
}
