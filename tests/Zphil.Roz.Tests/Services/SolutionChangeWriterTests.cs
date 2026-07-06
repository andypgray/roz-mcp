using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Extensions;
using Zphil.Roz.Services;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Services;

/// <summary>
///     Unit coverage for <see cref="SolutionChangeWriter.CollectFileChangesAsync" />'s line-ending
///     normalization — the persist path shared by <c>rename_symbol</c> / <c>apply_code_fix</c> /
///     <c>change_signature</c>, where Roslyn's FixAll/Formatter synthesize <see cref="Environment.NewLine" />
///     and would otherwise leave a CRLF user file with mixed endings on Linux. Read-only: the collect
///     step forks the loaded solution in memory (<see cref="Solution.WithDocumentText(DocumentId, SourceText)" />)
///     and touches no disk, so the shared <see cref="WorkspaceFixture" /> is safe.
/// </summary>
/// <remarks>
///     CRLF/LF contents are built from escaped <c>"\r\n"</c> concatenation, never raw string literals:
///     a raw literal inherits the checkout's endings (LF on Linux), which would defeat the test.
/// </remarks>
public class SolutionChangeWriterTests(WorkspaceFixture fixture)
{
    private const string CrlfBody =
        "namespace Fixture;\r\n\r\npublic sealed class Sample\r\n{\r\n    public int Value { get; set; }\r\n}\r\n";

    private const string LfBody =
        "namespace Fixture;\n\npublic sealed class Sample\n{\n    public int Value { get; set; }\n}\n";

    [Fact]
    public async Task CollectFileChangesAsync_LfInsertionIntoCrlfDocument_NormalizesToCrlf()
    {
        // Arrange — old document is uniformly CRLF; the new document appends an LF-ended line
        // (mirrors Roslyn synthesizing Environment.NewLine == LF on Linux into a CRLF user file).
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Solution oldSolution, Solution newSolution) = await ForkDocumentAsync(CrlfBody, CrlfBody + "// appended\n", ct);

        // Act
        List<(string FilePath, string Content, Encoding Encoding)> changes =
            await SolutionChangeWriter.CollectFileChangesAsync(oldSolution, newSolution, ct);

        // Assert — the appended LF is normalized to CRLF, leaving no bare line feed.
        changes.ShouldHaveSingleItem().Content.ShouldHaveNoBareLineFeed();
    }

    [Fact]
    public async Task CollectFileChangesAsync_CrlfInsertionIntoLfDocument_StaysLf()
    {
        // Arrange — mirror case: old document is uniformly LF; the new document appends a CRLF-ended line.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Solution oldSolution, Solution newSolution) = await ForkDocumentAsync(LfBody, LfBody + "// appended\r\n", ct);

        // Act
        List<(string FilePath, string Content, Encoding Encoding)> changes =
            await SolutionChangeWriter.CollectFileChangesAsync(oldSolution, newSolution, ct);

        // Assert — the appended CRLF is normalized down to LF, so no CRLF survives.
        changes.ShouldHaveSingleItem().Content.ShouldHaveNoCrLf();
    }

    [Fact]
    public async Task CollectFileChangesAsync_NoChanges_ReturnsEmpty()
    {
        // Arrange — diffing a snapshot against itself models a no-op edit.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(ct);

        // Act
        List<(string FilePath, string Content, Encoding Encoding)> changes =
            await SolutionChangeWriter.CollectFileChangesAsync(solution, solution, ct);

        // Assert
        changes.ShouldBeEmpty();
    }

    /// <summary>
    ///     Forks the loaded solution twice over the same document (Circle.cs): once with
    ///     <paramref name="oldContent" /> and once with <paramref name="newContent" />, so the pair
    ///     differs only in that one document's text.
    /// </summary>
    private async Task<(Solution Old, Solution New)> ForkDocumentAsync(string oldContent, string newContent, CancellationToken ct)
    {
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(ct);
        Document doc = solution.GetDocumentByPath(fixture.ShapesFile("Circle.cs"))!;
        SourceText original = await doc.GetTextAsync(ct);

        Solution oldSolution = solution.WithDocumentText(doc.Id, SourceText.From(oldContent, original.Encoding));
        Solution newSolution = oldSolution.WithDocumentText(doc.Id, SourceText.From(newContent, original.Encoding));
        return (oldSolution, newSolution);
    }
}
