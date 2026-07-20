using System.Text.RegularExpressions;
using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Times the <c>verify</c> delta on nopCommerce: a leaf-project Delta (small cone, expect seconds)
///     and a Nop.Core DryRun (the whole-solution cone whose number decides whether a
///     <c>MaxDeltaDependentProjects</c> scope cap is worth adding).
/// </summary>
[Trait("Category", "Stress")]
public class NopVerifiedEditStressTests(NopTempWorkspaceFixture fixture, ITestOutputHelper output) : IClassFixture<NopTempWorkspaceFixture>
{
    [Fact]
    public async Task Delta_LeafProjectEdit_RecompilesSmallCone()
    {
        // Arrange — ProductController is in Nop.Web (Presentation), a leaf: nothing depends on it, so a
        // Delta's dependent-cone recompile is just this one project. Expect single-digit seconds.
        CodeEditTools codeEdit = CreateEditTools(fixture);
        string controllerFile = ProductControllerFile(fixture.WorkspaceManager);
        (string search, string replace) = await BenignNamespaceEditAsync(controllerFile, TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

        // Act — commit + verify in one round trip.
        string result = await TimingHelper.TimeAsync("Nop_VerifiedEdit_Delta_LeafProject",
            () => codeEdit.ReplaceContent([new ReplaceContentRequest(controllerFile, search, replace)], VerifyMode.Delta, ct: cts.Token), output);

        // Assert — committed and verified; record the scope line.
        result.ShouldContain("Verification:");
        result.ShouldContain("scope:");
        output.WriteLine("Leaf Delta: " + VerificationLine(result));
        (await File.ReadAllTextAsync(controllerFile, TestContext.Current.CancellationToken)).ShouldContain("verify-stress");
    }

    [Fact]
    public async Task DryRun_NopCoreEdit_RecompilesWholeCone_WritesNothing()
    {
        // Arrange — Product is in Nop.Core, the base library ~everything depends on, so the DryRun fork's
        // dependent cone is effectively the whole solution. This timing is the go/no-go for the scope-cap
        // lever. DryRun writes nothing.
        CodeEditTools codeEdit = CreateEditTools(fixture);
        string productFile = ProductFile(fixture.WorkspaceManager);
        byte[] before = await File.ReadAllBytesAsync(productFile, TestContext.Current.CancellationToken);
        (string search, string replace) = await BenignNamespaceEditAsync(productFile, TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));

        // Act — preview the blast radius across the whole cone; write nothing.
        string result = await TimingHelper.TimeAsync("Nop_VerifiedEdit_DryRun_NopCore_Cone",
            () => codeEdit.ReplaceContent([new ReplaceContentRequest(productFile, search, replace)], VerifyMode.DryRun, ct: cts.Token), output);

        // Assert — dry run wrote nothing; the cone timing is captured by TimingHelper + the scope line.
        result.ShouldContain("DRY RUN — no files written.");
        result.ShouldContain("scope:");
        output.WriteLine("Nop.Core DryRun: " + VerificationLine(result));
        (await File.ReadAllBytesAsync(productFile, TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    [Fact]
    public async Task DryRun_ChangeSignatureAddOptional_CrossProject_WritesNothing()
    {
        // Arrange — GetProductByIdAsync (IProductService + ProductService) is consumed across many
        // projects; adding a trailing optional CancellationToken keeps every existing call site Compatible
        // (Phase-1's sibling analyze stress classified all 239). change_signature forks the whole slot
        // family, classifies every site, and previews the dependent-cone delta — the apply analog of the
        // Nop.Core DryRun. DryRun writes nothing.
        CodeEditTools codeEdit = CreateEditTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);
        byte[] before = await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));

        // Act — preview the family + call-site rewrite across the cone; write nothing.
        string result = await TimingHelper.TimeAsync("Nop_ChangeSignature_DryRun_AddOptional_GetProductByIdAsync",
            () => codeEdit.ChangeSignature(productServiceFile, "GetProductByIdAsync",
                "(int productId, System.Threading.CancellationToken cancellationToken = default)",
                "ProductService", verify: VerifyMode.DryRun, ct: cts.Token), output);

        // Assert — dry run wrote nothing; the classification + cone timing is captured by TimingHelper.
        result.ShouldContain("DRY RUN — no files written.");
        result.ShouldContain("Would change signature of 'GetProductByIdAsync'");
        output.WriteLine("change_signature DryRun: " + VerificationLine(result));
        (await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    [Fact]
    public async Task ChangeSignature_RemoveUsedParameter_RefusesListingBlockers_WritesNothing()
    {
        // Arrange — productId is used in GetProductByIdAsync's body and supplied at every call site,
        // so the apply gate must refuse with concrete per-site blockers and write nothing. Deliberately
        // the default verify=None: under DryRun the byte-identity assertion would be vacuous (DryRun
        // never writes), whereas here it pins the refusal gate itself.
        CodeEditTools codeEdit = CreateEditTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);
        byte[] before = await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));

        // Act + Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => codeEdit.ChangeSignature(productServiceFile, "GetProductByIdAsync", "()",
            "ProductService", ct: cts.Token));

        output.WriteLine(ex.Message[..Math.Min(2000, ex.Message.Length)]);
        ex.Message.ShouldContain("productId");
        (await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    /// <summary>
    ///     Appends a harmless comment to a file's <c>namespace</c> declaration — a benign, syntactically
    ///     valid edit that needs no knowledge of the file's members. Returns the (search, replace) pair
    ///     for <c>replace_content</c>; "namespace X" occurs exactly once (usings say "using", not
    ///     "namespace"), so it is an unambiguous single-site literal replace.
    /// </summary>
    private static async Task<(string Search, string Replace)> BenignNamespaceEditAsync(string filePath, CancellationToken ct)
    {
        string content = await File.ReadAllTextAsync(filePath, ct);
        Match match = Regex.Match(content, @"namespace\s+[\w.]+");
        match.Success.ShouldBeTrue($"expected a namespace declaration in {filePath}");
        return (match.Value, match.Value + " /* verify-stress */");
    }

    private static string VerificationLine(string result) =>
        result.Replace("\r\n", "\n").Split('\n')
            .FirstOrDefault(l => l.StartsWith("Verification:", StringComparison.Ordinal)) ?? "(no verification line)";
}
