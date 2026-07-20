using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Validation coverage for <see cref="CodeEditTools.ApplyCodeFix" /> against nopCommerce: fixer
///     discovery must reflect the target solution's real analyzer surface (roz ships no fixers of its
///     own), the unknown-ID path must reject cleanly, and a hinted ID must FixAll under
///     <c>verify=DryRun</c> without writing — all on an out-of-repo ~300K-LOC solution.
/// </summary>
[Trait("Category", "Stress")]
public class NopCodeFixStressTests(NopTempWorkspaceFixture fixture, ITestOutputHelper output) : IClassFixture<NopTempWorkspaceFixture>
{
    [Fact]
    public async Task ApplyCodeFix_UnknownDiagnosticId_RejectsNamingTheId()
    {
        // Arrange — an ID no analyzer pack registers a fixer for must be a UserErrorException naming
        // the ID, never a crash or a silent no-op.
        CodeEditTools codeEdit = CreateEditTools(fixture);

        // Act + Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => codeEdit.ApplyCodeFix("ZZ9999", project: "Nop.Core", ct: TestContext.Current.CancellationToken));
        output.WriteLine(ex.Message);
        ex.Message.ShouldContain("ZZ9999");
    }

    [Fact]
    public async Task ApplyCodeFix_HintedAnalyzerId_DryRunAcrossNopCore_WritesNothing()
    {
        // Arrange — ask get_diagnostics (Info floor, where nop's CA diagnostics live) which fixable
        // IDs the solution's analyzer set actually offers. This validates FixerCatalog discovery
        // against a real out-of-repo analyzer surface rather than a hand-picked ID.
        DiagnosticTools diag = CreateDiagnosticTools(fixture);
        CodeEditTools codeEdit = CreateEditTools(fixture);
        using var diagCts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        string diagResult = await TimingHelper.TimeAsync("Nop_CodeFix_DiagnosticsDiscovery_NopCore",
            () => diag.GetDiagnostics(project: "Nop.Core", severity: DiagnosticSeverity.Info, ct: diagCts.Token), output);

        List<string> hintedIds = ParseHintedFixIds(diagResult);
        output.WriteLine($"Hinted fixable IDs in Nop.Core: {(hintedIds.Count > 0 ? String.Join(", ", hintedIds) : "(none)")}");

        if (hintedIds.Count == 0)
        {
            // No fixer-backed diagnostics in scope — the no-match path must be an informative skip,
            // not an error (ROZ_DISABLE_ANALYZERS or a lean analyzer set legitimately produces this).
            string skip = await codeEdit.ApplyCodeFix("CA1822", project: "Nop.Core",
                verify: VerifyMode.DryRun, ct: TestContext.Current.CancellationToken);
            output.WriteLine(skip);
            skip.ShouldContain("CA1822");
            return;
        }

        // Act — FixAll-preview hinted IDs until one previews. A multi-flavour fixer refusing with an
        // equivalence listing is a legitimate conservative-writes outcome to skip past, but when the
        // surface hints any ID, at least one must actually produce the DryRun — otherwise the test
        // green-passes without exercising the FixAll path it exists to prove.
        foreach (string id in hintedIds)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
            try
            {
                string result = await TimingHelper.TimeAsync("Nop_ApplyCodeFix_DryRun_NopCore",
                    () => codeEdit.ApplyCodeFix(id, project: "Nop.Core", verify: VerifyMode.DryRun, ct: cts.Token), output);

                // Assert — previewed, nothing written.
                output.WriteLine(result[..Math.Min(2500, result.Length)]);
                result.ShouldContain("DRY RUN");
                result.ShouldContain(id);
                return;
            }
            catch (UserErrorException ex)
            {
                // Both refusal wordings spell it differently ("equivalence key" / "equivalenceKey").
                ex.Message.ShouldContain("equivalence");
                output.WriteLine($"Equivalence refusal for {id}: {ex.Message}");
            }
        }

        Assert.Fail("Every hinted ID refused on equivalence keys; no FixAll DryRun was exercised.");
    }

    /// <summary>
    ///     Pulls the diagnostic IDs out of the "Available analyzer fixes" block: the contiguous run
    ///     of <c>"  CA1822: 40"</c> lines after the header. Stops at the first non-entry line so a
    ///     later section that happens to share the shape is never misread as a fixable ID.
    /// </summary>
    private static List<string> ParseHintedFixIds(string diagnosticsResult)
    {
        string[] lines = diagnosticsResult.Replace("\r\n", "\n").Split('\n');
        int header = Array.FindIndex(lines, l => l.StartsWith("Available analyzer fixes", StringComparison.Ordinal));
        if (header < 0)
        {
            return [];
        }

        List<string> ids = [];
        for (int i = header + 1; i < lines.Length; i++)
        {
            Match match = Regex.Match(lines[i], @"^\s{2}([A-Za-z0-9]+): \d+");
            if (!match.Success)
            {
                break;
            }

            ids.Add(match.Groups[1].Value);
        }

        return ids;
    }
}
