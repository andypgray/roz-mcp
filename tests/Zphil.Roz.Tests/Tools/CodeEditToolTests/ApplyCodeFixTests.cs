using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;
using Zphil.Roz.Services;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     End-to-end tests for <c>apply_code_fix</c> against a real analyzer-shipped fixer: the
///     xUnit2004 fixer (<c>Assert.Equal(true, x)</c> → <c>Assert.True(x)</c>), which is FixAll-capable
///     with a single equivalence key. Exercised via <see cref="EditWorkspaceFixture" />, whose
///     <c>EditFixture.slnf</c> now includes <c>TestFixture.Tests</c> (the only fixture project carrying a
///     fixer). Covers the apply/repair path, DryRun/Delta verification, scope narrowing, the
///     no-registered-fixer error, and the informative no-match skip.
/// </summary>
public class ApplyCodeFixTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    private const string Xunit2004 = "xUnit2004";

    private static string CodeFixSampleFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture.Tests", "CodeFixSample.cs");

    private static string CodeFixSampleMoreFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture.Tests", "CodeFixSampleMore.cs");

    /// <summary>
    ///     Counts the live xUnit2004 diagnostics in the test project — used to assert the fixer actually
    ///     repaired the diagnostic, not just changed bytes.
    /// </summary>
    private async Task<int> CountXunit2004Async(CancellationToken ct)
    {
        Solution solution = await Fixture.WorkspaceManager.GetSolutionAsync(ct);
        List<Project> projects = solution.Projects
            .Where(p => p.Name.Contains("TestFixture.Tests", StringComparison.Ordinal))
            .ToList();
        List<Diagnostic> diagnostics = await DiagnosticService.GetSolutionDiagnosticsAsync(projects, null, ct);
        return diagnostics.Count(d => d.Id == Xunit2004 && d.Location.IsInSource);
    }

    [Fact]
    public async Task ApplyCodeFix_Xunit2004_FixesOccurrences_AndRepairsDiagnostic()
    {
        // Arrange
        CancellationToken ct = TestContext.Current.CancellationToken;
        CodeEditTools tools = CreateEditTools(Fixture);
        string sampleFile = CodeFixSampleFile(Fixture);
        byte[] before = await File.ReadAllBytesAsync(sampleFile, ct);

        // Precondition: the fixture actually raises the analyzer diagnostic (guards the whole approach —
        // if the test project's xunit.analyzers didn't load, this is 0 and the failure is unambiguous).
        (await CountXunit2004Async(ct)).ShouldBeGreaterThan(0);

        // Act — fix every xUnit2004 across the test project (single equivalence key ⇒ no flavor needed).
        string result = await tools.ApplyCodeFix(Xunit2004, includeTests: true, ct: ct);

        // Assert — reported as applied, the code was rewritten, and the diagnostic is gone.
        result.ShouldContain("Fixed");
        result.ShouldContain(Xunit2004);
        result.ShouldContain("CodeFixSample.cs");

        string after = await File.ReadAllTextAsync(sampleFile, ct);
        after.ShouldContain("Assert.True(flag)");
        after.ShouldContain("Assert.False(flag)");
        after.ShouldNotContain("Assert.Equal(true");
        after.ShouldNotContain("Assert.Equal(false");
        (await File.ReadAllBytesAsync(sampleFile, ct)).ShouldNotBe(before);

        (await CountXunit2004Async(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task ApplyCodeFix_DryRun_WritesNothing_ReportsCleanDelta()
    {
        // Arrange
        CancellationToken ct = TestContext.Current.CancellationToken;
        CodeEditTools tools = CreateEditTools(Fixture);
        string sampleFile = CodeFixSampleFile(Fixture);
        byte[] before = await File.ReadAllBytesAsync(sampleFile, ct);

        // Act
        string result = await tools.ApplyCodeFix(Xunit2004, includeTests: true, verify: VerifyMode.DryRun, ct: ct);

        // Assert — worded as a preview, banner present, delta clean (the fix compiles).
        result.ShouldContain("DRY RUN — no files written.");
        result.ShouldContain("Would fix");
        result.ShouldContain("no new errors");

        // Disk untouched (byte-for-byte) and the diagnostic still present.
        (await File.ReadAllBytesAsync(sampleFile, ct)).ShouldBe(before);
        (await CountXunit2004Async(ct)).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ApplyCodeFix_Delta_Commits_AndReportsCleanDelta()
    {
        // Arrange
        CancellationToken ct = TestContext.Current.CancellationToken;
        CodeEditTools tools = CreateEditTools(Fixture);
        string sampleFile = CodeFixSampleFile(Fixture);

        // Act — Delta commits then verifies.
        string result = await tools.ApplyCodeFix(Xunit2004, includeTests: true, verify: VerifyMode.Delta, ct: ct);

        // Assert — committed (disk changed) and reported clean.
        result.ShouldContain("Fixed");
        result.ShouldContain("Verification: no new errors");
        result.ShouldNotContain("DRY RUN");
        (await File.ReadAllTextAsync(sampleFile, ct)).ShouldContain("Assert.True(flag)");
        (await CountXunit2004Async(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task ApplyCodeFix_FilePathsScope_FixesOnlyScopedFile()
    {
        // Arrange
        CancellationToken ct = TestContext.Current.CancellationToken;
        CodeEditTools tools = CreateEditTools(Fixture);
        string sampleFile = CodeFixSampleFile(Fixture);
        string moreFile = CodeFixSampleMoreFile(Fixture);
        byte[] moreBefore = await File.ReadAllBytesAsync(moreFile, ct);

        // Act — scope to CodeFixSample.cs (2 sites); CodeFixSampleMore.cs (1 site) must be untouched.
        string result = await tools.ApplyCodeFix(Xunit2004, filePaths: [sampleFile], includeTests: true, ct: ct);

        // Assert — exactly the two in-scope sites fixed, the out-of-scope file byte-identical.
        result.ShouldContain("Fixed 2 occurrence(s)");
        (await File.ReadAllTextAsync(sampleFile, ct)).ShouldContain("Assert.True(flag)");
        (await File.ReadAllBytesAsync(moreFile, ct)).ShouldBe(moreBefore);
    }

    [Fact]
    public async Task ApplyCodeFix_UnknownDiagnosticId_Throws()
    {
        // Arrange
        CancellationToken ct = TestContext.Current.CancellationToken;
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act / Assert — no analyzer pack ships a fixer for this ID.
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.ApplyCodeFix("XYZ9999", includeTests: true, ct: ct));
        ex.Message.ShouldContain("No registered code fix");
    }

    [Fact]
    public async Task ApplyCodeFix_NoMatchingDiagnostics_SkipsWithoutError()
    {
        // Arrange — xUnit1004 (skipped-fact) has a registered, FixAll-capable fixer, but the fixture has
        // no [Fact(Skip=...)], so there is nothing to fix: an informative skip, not an error.
        CancellationToken ct = TestContext.Current.CancellationToken;
        CodeEditTools tools = CreateEditTools(Fixture);
        string sampleFile = CodeFixSampleFile(Fixture);
        byte[] before = await File.ReadAllBytesAsync(sampleFile, ct);

        // Act
        string result = await tools.ApplyCodeFix("xUnit1004", includeTests: true, ct: ct);

        // Assert — skip message, no write.
        result.ShouldContain("nothing changed");
        (await File.ReadAllBytesAsync(sampleFile, ct)).ShouldBe(before);
    }

    [Fact]
    public async Task ApplyCodeFix_WithoutIncludeTests_SkipsTestOnlyDiagnostic()
    {
        // Arrange — the only xUnit2004 sites live in the test project, which is out of scope unless
        // includeTests=true (deliberate symmetry with get_diagnostics).
        CancellationToken ct = TestContext.Current.CancellationToken;
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — includeTests defaults false.
        string result = await tools.ApplyCodeFix(Xunit2004, ct: ct);

        // Assert
        result.ShouldContain("nothing changed");
    }
}
