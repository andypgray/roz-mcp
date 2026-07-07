using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.Presentation;

namespace Zphil.Roz.Tests.Presentation;

/// <summary>
///     Unit tests for <see cref="UnusedReferenceFormatter" />, focused on rendering of
///     <see cref="ProjectUnusedReferences.AnalysisError" /> blocks alongside normal hit output.
/// </summary>
public class UnusedReferenceFormatterTests
{
    [Fact]
    public void Format_ProjectWithAnalysisError_RendersErrorBlock()
    {
        // Arrange — single project that failed analysis (e.g. broken compilation).
        var result = new UnusedReferencesResult(
            UnusedReferencesKind.Projects,
            null,
            [
                new ProjectUnusedReferences(
                    "MyProject", [], [],
                    "Compilation has errors. Cannot determine used references.")
            ]);

        // Act
        string output = UnusedReferenceFormatter.Format(result);

        // Assert — error block present, no hits-style heading or line.
        output.ShouldContain("=== Error in MyProject ===");
        output.ShouldContain("Compilation has errors. Cannot determine used references.");
        output.ShouldNotContain("Unused references in MyProject:");
        output.ShouldNotContain("unused project:");
    }

    [Fact]
    public void Format_MixedErrorAndHits_RendersBoth()
    {
        // Arrange — one project with hits, one with an analysis error.
        var result = new UnusedReferencesResult(
            UnusedReferencesKind.Projects,
            null,
            [
                new ProjectUnusedReferences("GoodProject", ["UnusedRef"], []),
                new ProjectUnusedReferences(
                    "BrokenProject", [], [],
                    "boom")
            ]);

        // Act
        string output = UnusedReferenceFormatter.Format(result);

        // Assert — both blocks rendered.
        output.ShouldContain("Unused references in GoodProject:");
        output.ShouldContain("unused project: UnusedRef");
        output.ShouldContain("=== Error in BrokenProject ===");
        output.ShouldContain("boom");

        // Assert — footer counts only the hits from the non-erroring project. Singular
        // reference count ("1 unused project reference") and self-contained "across N
        // projects" clause (no shared/elided noun between the counts and project total).
        output.ShouldContain("1 unused project reference, across 2 projects.");
    }
}
