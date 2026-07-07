namespace Zphil.Roz.Constants;

/// <summary>
///     Shared parameter description constants for MCP tool attributes.
///     Centralizes descriptions to prevent drift across tool classes.
/// </summary>
internal static class ToolDescriptions
{
    // ── Name-based resolution parameters ─────────────────────────────────

    internal const string BatchSymbolNames =
        "Names: ['Foo','Bar',...] (simple/FQN). BATCH MANY per call. See server instructions.";

    internal const string ContainingType =
        "Containing type.";

    // ── Position parameters ──────────────────────────────────────────────

    internal const string BatchLocations =
        "Cursors: ['src/Foo.cs:42:15',...] (path:line[:col]). BATCH MANY per call. See server instructions.";

    // ── Filtering parameters ─────────────────────────────────────────────

    internal const string Kind =
        "Symbol kind filter. See server instructions.";

    internal const string MatchMode =
        "Match mode. See server instructions.";

    internal const string DiagnosticSeverity =
        "Min severity. See server instructions.";

    internal const string IncludeOverloads = "Search all overloads.";

    internal const string IncludeExternalCalls = "Show BCL/NuGet callees too.";

    internal const string ReferenceKinds = "Reference kind filter. See server instructions.";

    internal const string ExcludeBaseCalls = "Drop base.Method() sites.";

    internal const string ChangeKind = "Proposed change to assess. See server instructions.";

    internal const string NewType = "New type for changeKind=TypeChange (e.g. long, IReadOnlyList<Order>).";

    internal const string NewAccessibility = "New accessibility for changeKind=AccessibilityNarrow. See server instructions.";

    internal const string NewSignature =
        "New parameter list for changeKind=SignatureChange, e.g. (string name, int count = 5). See server instructions.";

    internal const string IncludeTests = "Include test projects.";

    internal const string MaxResults = "Max results.";

    internal const string ContextLines =
        "Lines of context (like grep -C).";

    internal const string MemberKinds =
        "Member kind filter. See server instructions.";

    internal const string MaxMembers =
        "Max members per type.";

    internal const string MaxTypes =
        "Max types to show.";

    internal const int MaxContextLines = 50;

    // ── Display parameters ────────────────────────────────────────────────

    internal const string IncludeDocs = "Include XML doc comments.";

    internal const string IncludeBody = "Include full source body.";

    internal const string MaxBodyLines =
        "Max body lines.";

    internal const string IncludeBodyFindSymbol =
        "Include source body (hides members).";

    internal const string FilePathsFilter =
        "File paths (glob OK).";

    // ── Generated code parameters ─────────────────────────────────────────

    internal const string Project =
        "Project filter (substring, ci).";

    internal const string IncludeGenerated = "Include generated files.";

    internal const string IncludeMetadata =
        "Include BCL/NuGet results.";

    // ── Using parameters ─────────────────────────────────────────────────

    internal const string SortUsings = "Sort usings after operation.";

    // ── Verified writes ──────────────────────────────────────────────────

    internal const string Verify =
        "Post-edit verification (None/Delta/DryRun). See server instructions.";

    // ── Apply code fix ────────────────────────────────────────────────────

    internal const string ApplyCodeFixDiagnosticId =
        "Diagnostic ID to fix, e.g. 'CA1822', 'IDE0052', 'xUnit2004'.";

    internal const string ApplyCodeFixEquivalenceKey =
        "Fix-flavor key; needed only when the fixer offers several (the tool lists them).";
}
