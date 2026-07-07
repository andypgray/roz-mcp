using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.Presentation;

namespace Zphil.Roz.Tests.Presentation;

/// <summary>
///     Pure formatting tests for the verification block — the headline, the scope collapsing, the
///     dry-run banner, the resolved count, the uncovered-files note, and the byte-identical
///     <c>Prepend(null, …)</c> path. The introduced-error rendering is covered by the integration tests
///     (it needs real <see cref="Diagnostic" /> objects from a compilation).
/// </summary>
public class VerificationFormatterTests
{
    private static EditVerification Clean(VerifyMode mode, int resolved, params string[] scope) =>
        new(mode, new DiagnosticsDelta([], resolved, scope, "/sln"), mode == VerifyMode.Delta);

    [Fact]
    public void Format_Skipped_ReportsReason()
    {
        var verification = new EditVerification(VerifyMode.DryRun, null, false, "no content changed");

        VerificationFormatter.Format(verification)
            .ShouldBe("Verification skipped — no content changed.");
    }

    [Fact]
    public void Format_Clean_SingleProject_NoResolved()
    {
        VerificationFormatter.Format(Clean(VerifyMode.Delta, 0, "TestFixture"))
            .ShouldBe("Verification: no new errors | scope: 1 project (TestFixture)");
    }

    [Fact]
    public void Format_Clean_WithResolved_AppendsResolvedCount()
    {
        VerificationFormatter.Format(Clean(VerifyMode.Delta, 2, "TestFixture"))
            .ShouldBe("Verification: no new errors, -2 resolved | scope: 1 project (TestFixture)");
    }

    [Fact]
    public void Format_DryRun_PrependsBanner()
    {
        string output = VerificationFormatter.Format(Clean(VerifyMode.DryRun, 0, "TestFixture"));

        output.ShouldStartWith("DRY RUN — no files written. Op results below show what a commit would produce.");
        output.ShouldContain("Verification: no new errors");
    }

    [Fact]
    public void Format_ScopeOverTwoProjects_CollapsesRemainder()
    {
        string output = VerificationFormatter.Format(Clean(VerifyMode.Delta, 0, "Alpha", "Bravo", "Charlie"));

        output.ShouldContain("scope: 3 projects (Alpha, Bravo, +1 more)");
    }

    [Fact]
    public void Format_UncoveredFiles_AppendsNoCoverageNote()
    {
        var verification = new EditVerification(
            VerifyMode.Delta,
            new DiagnosticsDelta([], 0, ["TestFixture"], "/sln", ["Scratch.cs", "Other.cs"]),
            true);

        string output = VerificationFormatter.Format(verification);

        output.ShouldContain("no delta coverage: Scratch.cs, Other.cs");
    }

    [Fact]
    public void Prepend_NullVerification_ReturnsBodyUnchanged()
    {
        const string body = "Replaced 'Foo' (1 -> 1 lines)";

        VerificationFormatter.Prepend(null, body).ShouldBe(body);
    }

    [Fact]
    public void Prepend_NonNull_PrefixesBlockThenBlankLineThenBody()
    {
        const string body = "Replaced 'Foo' (1 -> 1 lines)";

        string output = VerificationFormatter.Prepend(Clean(VerifyMode.Delta, 0, "TestFixture"), body);

        output.ShouldBe($"Verification: no new errors | scope: 1 project (TestFixture)\n\n{body}");
    }
}
