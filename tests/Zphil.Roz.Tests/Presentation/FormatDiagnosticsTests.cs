using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Models;
using Zphil.Roz.Presentation;
using Zphil.Roz.Services;

namespace Zphil.Roz.Tests.Presentation;

/// <summary>
///     Tests for <see cref="DiagnosticOutputFormatter.FormatDiagnostics" /> grouping, capping,
///     and metadata collapsing behavior.
/// </summary>
public class FormatDiagnosticsTests
{
    private const string SolutionDir = "/solution";

    /// <summary>
    ///     Enough lines to create source locations at various line numbers.
    /// </summary>
    private static readonly string DummySource = String.Join("\n", Enumerable.Range(0, 200).Select(_ => "// line"));

    private static Diagnostic CreateMetadataDiagnostic(string id, DiagnosticSeverity severity, string message)
    {
        var descriptor = new DiagnosticDescriptor(id, "title", message, "category", severity, true);
        return Diagnostic.Create(descriptor, Location.None);
    }

    private static Diagnostic CreateSourceDiagnostic(string id, DiagnosticSeverity severity, string message, string filePath, int line)
    {
        var descriptor = new DiagnosticDescriptor(id, "title", message, "category", severity, true);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(DummySource, path: filePath);
        SourceText text = tree.GetText();
        int position = text.Lines[line - 1].Start;
        var location = Location.Create(tree, new TextSpan(position, 1));
        return Diagnostic.Create(descriptor, location);
    }

    // ── Metadata grouping ────────────────────────────────────────────────────

    [Fact]
    public void FormatDiagnostics_MetadataDiagnostics_GroupedById()
    {
        // Arrange — 5 CS1705 metadata errors with different messages
        List<Diagnostic> diagnostics =
        [
            CreateMetadataDiagnostic("CS1705", DiagnosticSeverity.Error, "Assembly 'A' version mismatch"),
            CreateMetadataDiagnostic("CS1705", DiagnosticSeverity.Error, "Assembly 'B' version mismatch"),
            CreateMetadataDiagnostic("CS1705", DiagnosticSeverity.Error, "Assembly 'C' version mismatch"),
            CreateMetadataDiagnostic("CS1705", DiagnosticSeverity.Error, "Assembly 'D' version mismatch"),
            CreateMetadataDiagnostic("CS1705", DiagnosticSeverity.Error, "Assembly 'E' version mismatch")
        ];

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert — grouped into one entry with occurrence count
        result.ShouldContain("CS1705: 5 occurrence(s)");
        result.ShouldContain("Metadata diagnostics");
    }

    [Fact]
    public void FormatDiagnostics_MetadataGroupShowsMaxThreeSamples()
    {
        // Arrange — 5 distinct messages
        List<Diagnostic> diagnostics =
        [
            CreateMetadataDiagnostic("CS1705", DiagnosticSeverity.Error, "Assembly 'A' mismatch"),
            CreateMetadataDiagnostic("CS1705", DiagnosticSeverity.Error, "Assembly 'B' mismatch"),
            CreateMetadataDiagnostic("CS1705", DiagnosticSeverity.Error, "Assembly 'C' mismatch"),
            CreateMetadataDiagnostic("CS1705", DiagnosticSeverity.Error, "Assembly 'D' mismatch"),
            CreateMetadataDiagnostic("CS1705", DiagnosticSeverity.Error, "Assembly 'E' mismatch")
        ];

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert — only 3 sample messages shown, plus "and 2 more"
        result.ShouldContain("and 2 more distinct message(s)");
    }

    [Fact]
    public void FormatDiagnostics_DuplicateMetadataMessages_DeduplicatedInSamples()
    {
        // Arrange — same message repeated 10 times
        List<Diagnostic> diagnostics = Enumerable.Range(0, 10)
            .Select(_ => CreateMetadataDiagnostic("CS1705", DiagnosticSeverity.Error, "Same message"))
            .ToList();

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert — 10 occurrences but only 1 distinct message, no "and X more"
        result.ShouldContain("CS1705: 10 occurrence(s)");
        result.ShouldNotContain("more distinct message");
    }

    // ── Source diagnostics capping ───────────────────────────────────────────

    [Fact]
    public void FormatDiagnostics_SourceDiagnosticsUnderCap_AllShown()
    {
        // Arrange
        List<Diagnostic> diagnostics =
        [
            CreateSourceDiagnostic("CS0219", DiagnosticSeverity.Warning, "unused var", "/solution/file.cs", 10),
            CreateSourceDiagnostic("CS0246", DiagnosticSeverity.Error, "type not found", "/solution/file.cs", 20)
        ];

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert
        result.ShouldContain("CS0219");
        result.ShouldContain("CS0246");
        result.ShouldNotContain("more source diagnostics");
    }

    [Fact]
    public void FormatDiagnostics_SourceDiagnosticsOverCap_ShowsGroupedFormat()
    {
        // Arrange — 105 source diagnostics (over 100 threshold)
        List<Diagnostic> diagnostics = Enumerable.Range(1, 105)
            .Select(i => CreateSourceDiagnostic("CS0219", DiagnosticSeverity.Warning, $"unused var {i}", $"/solution/file{i}.cs", 1))
            .ToList();

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert — should show grouped format, not individual listing
        result.ShouldContain("Source diagnostics (105 total across 105 files):");
        result.ShouldContain("By error code");
        result.ShouldContain("CS0219");
        result.ShouldContain("Top affected files");
    }

    // ── Mixed source + metadata ──────────────────────────────────────────────

    [Fact]
    public void FormatDiagnostics_MixedSourceAndMetadata_BothSectionsPresent()
    {
        // Arrange
        List<Diagnostic> diagnostics =
        [
            CreateSourceDiagnostic("CS0219", DiagnosticSeverity.Warning, "unused var", "/solution/file.cs", 10),
            CreateMetadataDiagnostic("CS1705", DiagnosticSeverity.Error, "Assembly mismatch")
        ];

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert — source shown as individual lines
        result.ShouldContain("file.cs:10: warning CS0219");

        // Assert — metadata shown in grouped section
        result.ShouldContain("Metadata diagnostics");
        result.ShouldContain("CS1705: 1 occurrence(s)");
    }

    // ── Summary line always accurate ─────────────────────────────────────────

    [Fact]
    public void FormatDiagnostics_SummaryCounts_ReflectAllDiagnosticsNotJustShown()
    {
        // Arrange — 105 warnings (over the cap)
        List<Diagnostic> diagnostics = Enumerable.Range(1, 105)
            .Select(i => CreateSourceDiagnostic("CS0219", DiagnosticSeverity.Warning, $"unused var {i}", $"/solution/file{i}.cs", 1))
            .ToList();

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert — summary reflects total count, not just shown
        result.ShouldContain("Summary: 0 error(s), 105 warning(s), 0 info");
    }

    [Fact]
    public void FormatDiagnostics_MetadataOnlySummary_CountsAllOccurrences()
    {
        // Arrange — 50 metadata errors
        List<Diagnostic> diagnostics = Enumerable.Range(1, 50)
            .Select(i => CreateMetadataDiagnostic("CS1705", DiagnosticSeverity.Error, $"Assembly '{i}' mismatch"))
            .ToList();

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert
        result.ShouldContain("Summary: 50 error(s), 0 warning(s), 0 info");
    }

    // ── Grouped source diagnostics ──────────────────────────────────────────

    [Fact]
    public void FormatDiagnostics_OverThreshold_ShowsGroupedByErrorCode()
    {
        // Arrange — 200 diagnostics across 2 error codes
        List<Diagnostic> diagnostics =
        [
            ..Enumerable.Range(1, 150)
                .Select(i => CreateSourceDiagnostic("CS0246", DiagnosticSeverity.Error, $"type not found {i}", $"/solution/file{i}.cs", 1)),
            ..Enumerable.Range(1, 50)
                .Select(i => CreateSourceDiagnostic("CS0234", DiagnosticSeverity.Error, $"namespace not found {i}", $"/solution/ns{i}.cs", 1))
        ];

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert
        result.ShouldContain("By error code");
        result.ShouldContain("CS0246");
        result.ShouldContain("150 occurrences");
        result.ShouldContain("CS0234");
        result.ShouldContain("50 occurrences");
    }

    [Fact]
    public void FormatDiagnostics_Grouped_OrderedByCountDescending()
    {
        // Arrange — CS0246 has more occurrences than CS0234
        List<Diagnostic> diagnostics =
        [
            ..Enumerable.Range(1, 80)
                .Select(i => CreateSourceDiagnostic("CS0234", DiagnosticSeverity.Error, $"ns not found {i}", $"/solution/a{i}.cs", 1)),
            ..Enumerable.Range(1, 120)
                .Select(i => CreateSourceDiagnostic("CS0246", DiagnosticSeverity.Error, $"type not found {i}", $"/solution/b{i}.cs", 1))
        ];

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert — CS0246 (120) should appear before CS0234 (80)
        int cs0246Pos = result.IndexOf("CS0246", StringComparison.Ordinal);
        int cs0234Pos = result.IndexOf("CS0234", StringComparison.Ordinal);
        cs0246Pos.ShouldBeLessThan(cs0234Pos);
    }

    [Fact]
    public void FormatDiagnostics_Grouped_ShowsTopAffectedFiles()
    {
        // Arrange — 120 diagnostics, some files have more than others
        List<Diagnostic> diagnostics =
        [
            ..Enumerable.Range(1, 50)
                .Select(i => CreateSourceDiagnostic("CS0246", DiagnosticSeverity.Error, "type not found", "/solution/HotFile.cs", i)),
            ..Enumerable.Range(1, 70)
                .Select(i => CreateSourceDiagnostic("CS0246", DiagnosticSeverity.Error, "type not found", $"/solution/other{i}.cs", 1))
        ];

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert
        result.ShouldContain("Top affected files");
        result.ShouldContain("HotFile.cs: 50 errors");
    }

    [Fact]
    public void FormatDiagnostics_Grouped_TopAffectedFiles_SingularCountsNotPluralized()
    {
        // Arrange — 100 errors in one file (triggers grouping) + a second file with exactly
        // 1 error and 1 warning, to pin the singular "1 error"/"1 warning" wording.
        List<Diagnostic> diagnostics =
        [
            ..Enumerable.Range(1, 100)
                .Select(i => CreateSourceDiagnostic("CS0246", DiagnosticSeverity.Error, "type not found", "/solution/BigFile.cs", 1)),
            CreateSourceDiagnostic("CS0246", DiagnosticSeverity.Error, "type not found", "/solution/SingleHits.cs", 1),
            CreateSourceDiagnostic("CS0219", DiagnosticSeverity.Warning, "unused var", "/solution/SingleHits.cs", 2)
        ];

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert — singular wording, not "1 errors"/"1 warnings"
        result.ShouldContain("SingleHits.cs: 1 error, 1 warning");
        result.ShouldNotContain("1 errors");
        result.ShouldNotContain("1 warnings");
    }

    [Fact]
    public void FormatDiagnostics_Grouped_WithMaxCount25_FewerGroupsNoFiles()
    {
        // Arrange — 30 diagnostics across 6 error codes, maxCount=25 triggers grouping
        List<Diagnostic> diagnostics =
        [
            ..Enumerable.Range(1, 10)
                .Select(i => CreateSourceDiagnostic("CS0001", DiagnosticSeverity.Error, $"err {i}", $"/solution/a{i}.cs", 1)),
            ..Enumerable.Range(1, 5)
                .Select(i => CreateSourceDiagnostic("CS0002", DiagnosticSeverity.Error, $"err {i}", $"/solution/b{i}.cs", 1)),
            ..Enumerable.Range(1, 5)
                .Select(i => CreateSourceDiagnostic("CS0003", DiagnosticSeverity.Warning, $"warn {i}", $"/solution/c{i}.cs", 1)),
            ..Enumerable.Range(1, 4)
                .Select(i => CreateSourceDiagnostic("CS0004", DiagnosticSeverity.Warning, $"warn {i}", $"/solution/d{i}.cs", 1)),
            ..Enumerable.Range(1, 3)
                .Select(i => CreateSourceDiagnostic("CS0005", DiagnosticSeverity.Info, $"info {i}", $"/solution/e{i}.cs", 1)),
            ..Enumerable.Range(1, 3)
                .Select(i => CreateSourceDiagnostic("CS0006", DiagnosticSeverity.Info, $"info {i}", $"/solution/f{i}.cs", 1))
        ];

        // Act — maxCount=25 means grouping triggers at >25
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir, 25);

        // Assert — at most 5 groups, no file breakdown
        result.ShouldContain("By error code");
        result.ShouldContain("CS0001");
        result.ShouldNotContain("Top affected files");
        // Should show overflow for the 6th group
        result.ShouldContain("... and 1 more error codes");
    }

    [Fact]
    public void FormatDiagnostics_Grouped_SummaryCounts_StillAccurate()
    {
        // Arrange — mixed severities
        List<Diagnostic> diagnostics =
        [
            ..Enumerable.Range(1, 80)
                .Select(i => CreateSourceDiagnostic("CS0246", DiagnosticSeverity.Error, $"err {i}", $"/solution/a{i}.cs", 1)),
            ..Enumerable.Range(1, 60)
                .Select(i => CreateSourceDiagnostic("CS0219", DiagnosticSeverity.Warning, $"warn {i}", $"/solution/b{i}.cs", 1))
        ];

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert — summary reflects total counts
        result.ShouldContain("Summary: 80 error(s), 60 warning(s), 0 info");
    }

    [Fact]
    public void FormatDiagnostics_Grouped_ShowsDescriptorTitle()
    {
        // Arrange — diagnostics with a known descriptor title
        var descriptor = new DiagnosticDescriptor("CS0246", "The type or namespace name could not be found",
            "type not found", "category", DiagnosticSeverity.Error, true);
        List<Diagnostic> diagnostics = Enumerable.Range(1, 110)
            .Select(i =>
            {
                SyntaxTree tree = CSharpSyntaxTree.ParseText(DummySource, path: $"/solution/file{i}.cs");
                SourceText text = tree.GetText();
                int position = text.Lines[0].Start;
                var location = Location.Create(tree, new TextSpan(position, 1));
                return Diagnostic.Create(descriptor, location);
            })
            .ToList();

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert — descriptor title appears after the em dash
        result.ShouldContain("\u2014 The type or namespace name could not be found");
    }

    [Fact]
    public void FormatDiagnostics_AtThreshold_StillIndividual()
    {
        // Arrange — exactly 100 diagnostics (not over threshold)
        List<Diagnostic> diagnostics = Enumerable.Range(1, 100)
            .Select(i => CreateSourceDiagnostic("CS0219", DiagnosticSeverity.Warning, $"unused var {i}", $"/solution/file{i}.cs", 1))
            .ToList();

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert — individual flat list, not grouped
        result.ShouldNotContain("By error code");
        result.ShouldNotContain("Source diagnostics (");
        result.ShouldContain("file1.cs:1: warning CS0219");
    }

    [Fact]
    public void FormatDiagnostics_Grouped_OverflowMessages()
    {
        // Arrange — many error codes to trigger overflow
        List<Diagnostic> diagnostics = Enumerable.Range(1, 25)
            .SelectMany(code => Enumerable.Range(1, 5)
                .Select(i => CreateSourceDiagnostic($"CS{code:D4}", DiagnosticSeverity.Error, $"err {i}",
                    $"/solution/code{code}_file{i}.cs", 1)))
            .ToList(); // 125 total across 25 error codes

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert — 20 shown, 5 overflow
        result.ShouldContain("... and 5 more error codes");
        // 125 files, 10 shown, 115 overflow
        result.ShouldContain("more files");
    }

    // ── NuGet-unrestored workspace hint ──────────────────────────────────────

    [Fact]
    public void BuildWorkspaceHint_AboveCountAndRatio_EmitsBanner()
    {
        // Arrange — 25 CS0246 errors, purely assembly-resolution signature.
        List<Diagnostic> diagnostics = Enumerable.Range(1, 25)
            .Select(i => CreateSourceDiagnostic("CS0246", DiagnosticSeverity.Error, $"type not found {i}",
                $"/solution/file{i}.cs", 1))
            .ToList();

        // Act
        string? hint = DiagnosticService.BuildWorkspaceHint(diagnostics);

        // Assert
        hint.ShouldNotBeNull();
        hint.ShouldContain("25/25 errors are assembly-resolution codes");
        hint.ShouldContain("dotnet build");
    }

    [Fact]
    public void BuildWorkspaceHint_BelowCountFloor_ReturnsNull()
    {
        // Arrange — 15 CS0246 errors, below the 20-error floor.
        List<Diagnostic> diagnostics = Enumerable.Range(1, 15)
            .Select(i => CreateSourceDiagnostic("CS0246", DiagnosticSeverity.Error, $"type not found {i}",
                $"/solution/file{i}.cs", 1))
            .ToList();

        // Act
        string? hint = DiagnosticService.BuildWorkspaceHint(diagnostics);

        // Assert
        hint.ShouldBeNull();
    }

    [Fact]
    public void BuildWorkspaceHint_BelowRatio_ReturnsNull()
    {
        // Arrange — 10 CS0246 (assembly-resolution) + 10 CS0103 (typo-ish): 50% ratio.
        List<Diagnostic> diagnostics =
        [
            ..Enumerable.Range(1, 10)
                .Select(i => CreateSourceDiagnostic("CS0246", DiagnosticSeverity.Error, $"type not found {i}",
                    $"/solution/file{i}.cs", 1)),
            ..Enumerable.Range(1, 10)
                .Select(i => CreateSourceDiagnostic("CS0103", DiagnosticSeverity.Error, $"name missing {i}",
                    $"/solution/name{i}.cs", 1))
        ];

        // Act
        string? hint = DiagnosticService.BuildWorkspaceHint(diagnostics);

        // Assert
        hint.ShouldBeNull();
    }

    // ── DiagnosticResultFormatter — WorkspaceHint prepending ─────────────────

    [Fact]
    public void Format_DiagnosticsResultWithHintAndErrors_PrependsBothSeparatedByBlankLine()
    {
        // Arrange — hint AND path-resolution error should both appear before the diagnostics body.
        List<Diagnostic> diagnostics = Enumerable.Range(1, 25)
            .Select(i => CreateSourceDiagnostic("CS0246", DiagnosticSeverity.Error, $"type not found {i}",
                $"/solution/file{i}.cs", 1))
            .ToList();
        string? hint = DiagnosticService.BuildWorkspaceHint(diagnostics);
        var result = new DiagnosticsResult(
            diagnostics, SolutionDir, Errors: ["Path 'foo' not found"], WorkspaceHint: hint);

        // Act
        string formatted = DiagnosticResultFormatter.Format(result);

        // Assert — hint first, then error, then diagnostics.
        formatted.ShouldContain("assembly-resolution codes");
        formatted.ShouldContain("Error: Path 'foo' not found");
        int hintPos = formatted.IndexOf("assembly-resolution", StringComparison.Ordinal);
        int errorPos = formatted.IndexOf("Error: Path", StringComparison.Ordinal);
        hintPos.ShouldBeLessThan(errorPos);
    }

    [Fact]
    public void Format_IncrementalDiagnosticsResultWithHint_PrependsHintBeforeHeader()
    {
        // Arrange — incremental path must surface the hint too, not only the scalar overload.
        List<Diagnostic> diagnostics = Enumerable.Range(1, 25)
            .Select(i => CreateSourceDiagnostic("CS0246", DiagnosticSeverity.Error, $"type not found {i}",
                $"/solution/file{i}.cs", 1))
            .ToList();
        string? hint = DiagnosticService.BuildWorkspaceHint(diagnostics);
        var result = new IncrementalDiagnosticsResult(
            diagnostics, 0, 0,
            DateTime.UtcNow, SolutionDir, WorkspaceHint: hint);

        // Act
        string formatted = DiagnosticResultFormatter.Format(result);

        // Assert — hint appears before the "Incremental diagnostics" header.
        int hintPos = formatted.IndexOf("assembly-resolution", StringComparison.Ordinal);
        int headerPos = formatted.IndexOf("Incremental diagnostics", StringComparison.Ordinal);
        hintPos.ShouldBeGreaterThanOrEqualTo(0);
        hintPos.ShouldBeLessThan(headerPos);
    }

    // ── Fixer summary block ──────────────────────────────────────────────────

    [Fact]
    public void FormatDiagnostics_WithFixerSummary_AppendsAvailableFixesBlock()
    {
        // Arrange
        List<Diagnostic> diagnostics =
        [
            CreateSourceDiagnostic("xUnit1004", DiagnosticSeverity.Warning, "skipped test", "/solution/T.cs", 10)
        ];
        IReadOnlyList<FixerSummaryEntry> summary = [new("xUnit1004", 1)];

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(
            diagnostics, SolutionDir, fixerSummary: summary);

        // Assert — block appears after the Summary line.
        result.ShouldContain("Available analyzer fixes");
        result.ShouldContain("xUnit1004: 1");
        int summaryPos = result.IndexOf("Summary:", StringComparison.Ordinal);
        int fixerPos = result.IndexOf("Available analyzer fixes", StringComparison.Ordinal);
        summaryPos.ShouldBeLessThan(fixerPos);
    }

    [Fact]
    public void FormatDiagnostics_WithoutFixerSummary_OmitsAvailableFixesBlock()
    {
        // Arrange — null summary (the common path for results with no fixable IDs).
        List<Diagnostic> diagnostics =
        [
            CreateSourceDiagnostic("CS0246", DiagnosticSeverity.Error, "type not found", "/solution/T.cs", 1)
        ];

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir);

        // Assert
        result.ShouldNotContain("Available analyzer fixes");
    }

    [Fact]
    public void FormatDiagnostics_FixerSummary_UsesTerseIdColonCountFormat()
    {
        // Arrange
        IReadOnlyList<FixerSummaryEntry> summary =
        [
            new("xUnit1004", 5),
            new("CA1822", 1)
        ];
        List<Diagnostic> diagnostics =
        [
            CreateSourceDiagnostic("xUnit1004", DiagnosticSeverity.Warning, "msg", "/solution/T.cs", 1)
        ];

        // Act
        string result = DiagnosticOutputFormatter.FormatDiagnostics(diagnostics, SolutionDir, fixerSummary: summary);

        // Assert — terse "id: count" rows, no "occurrence(s)" wording (matches
        // DiagnosticResultFormatter's ResetBaselineResult breakdown style).
        result.ShouldContain("xUnit1004: 5");
        result.ShouldContain("CA1822: 1");
        result.ShouldNotContain("occurrence");
    }

    [Fact]
    public void Format_IncrementalDiagnosticsResultWithFixerSummary_AppendsBlock()
    {
        // Arrange — incremental path also needs to surface the summary.
        List<Diagnostic> diagnostics =
        [
            CreateSourceDiagnostic("xUnit1004", DiagnosticSeverity.Warning, "skipped test", "/solution/T.cs", 1)
        ];
        IReadOnlyList<FixerSummaryEntry> summary = [new("xUnit1004", 1)];
        var result = new IncrementalDiagnosticsResult(
            diagnostics, 0, 0, DateTime.UtcNow, SolutionDir, FixerSummary: summary);

        // Act
        string formatted = DiagnosticResultFormatter.Format(result);

        // Assert — block appears after the new-diagnostics summary.
        formatted.ShouldContain("Available analyzer fixes");
        formatted.ShouldContain("xUnit1004: 1");
    }

    [Fact]
    public void Format_DiagnosticsResultWithFixerSummary_AppendsBlock()
    {
        // Arrange
        List<Diagnostic> diagnostics =
        [
            CreateSourceDiagnostic("xUnit1004", DiagnosticSeverity.Warning, "skipped test", "/solution/T.cs", 1)
        ];
        IReadOnlyList<FixerSummaryEntry> summary = [new("xUnit1004", 1)];
        var result = new DiagnosticsResult(diagnostics, SolutionDir, FixerSummary: summary);

        // Act
        string formatted = DiagnosticResultFormatter.Format(result);

        // Assert
        formatted.ShouldContain("Available analyzer fixes");
        formatted.ShouldContain("xUnit1004");
    }
}
