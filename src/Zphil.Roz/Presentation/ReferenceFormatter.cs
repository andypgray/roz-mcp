using System.Text;
using Microsoft.CodeAnalysis;
using Zphil.Roz.Extensions;
using Zphil.Roz.Models;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Formats reference locations and caller information into human-readable plain text.
/// </summary>
internal static class ReferenceFormatter
{
    private const int MaxFileDistributionEntries = 20;
    private const int PathDisplayWidth = 60;

    /// <summary>
    ///     Formats reference locations grouped by file, with inline source snippets.
    /// </summary>
    /// <example>
    ///     Finding references to <c>IShape</c> (contextLines=0). The line number lives in
    ///     the gutter, so there is no separate <c>Line N:</c> header duplicating it; the
    ///     <c>(N refs)</c>/<c>[implicit]</c> tags ride the matched line.
    ///     <code>
    ///     TestFixture/Shapes/Shape.cs:
    ///          3 | public abstract class Shape : IShape
    ///     TestFixture/Services/ShapeService.cs:
    ///          7 | public string ProcessShape(IShape shape)
    ///         10 | public IShape GetLargest(IEnumerable&lt;IShape&gt; shapes)  (2 refs)
    ///     </code>
    ///     With contextLines=1 the matched line is marked with <c>&gt;</c>:
    ///     <code>
    ///     TestFixture/Services/ShapeService.cs:
    ///            6 |   // pick the largest
    ///     &gt;    7 | public string ProcessShape(IShape shape)
    ///            8 |   {
    ///     </code>
    /// </example>
    public static string FormatReferenceLocations(
        IEnumerable<ReferenceLocationWithContext> locations, string solutionDir,
        bool includeSourceContext = true)
    {
        IOrderedEnumerable<IGrouping<string, ReferenceLocationWithContext>> grouped = locations
            .Where(l => l.Loc.Location.IsInSource)
            .GroupBy(l => l.Loc.Location.GetLineSpan().Path)
            .OrderBy(g => g.Key);

        var sb = new StringBuilder();
        foreach (IGrouping<string, ReferenceLocationWithContext> group in grouped)
        {
            string relPath = Path.GetRelativePath(solutionDir, group.Key);
            string? projectName = group.First().ProjectName;
            string projectTag = projectName is not null ? $" [{projectName}]" : "";
            sb.AppendLine($"{relPath}{projectTag}:");

            // Group by line number to deduplicate same-line references (common with generics)
            // Project once to avoid repeated GetLineSpan() calls
            IEnumerable<IGrouping<int, ReferenceLocationWithContext>> lineGroups = group
                .Select(l => (Ref: l, Line: l.Loc.Location.GetLineSpan().StartLinePosition.Line + 1))
                .OrderBy(x => x.Line)
                .GroupBy(x => x.Line, x => x.Ref);

            foreach (IGrouping<int, ReferenceLocationWithContext> lineGroup in lineGroups)
            {
                ReferenceLocationWithContext refLoc = lineGroup.First();
                int line = lineGroup.Key;
                int refCount = lineGroup.Count();

                bool anyImplicit = lineGroup.Any(l => l.Loc.IsImplicit);
                string implicitTag = anyImplicit ? " [implicit]" : "";
                string refsTag = refCount > 1 ? $" ({refCount} refs)" : "";
                var tags = $"{implicitTag}{refsTag}";

                // No snippet (DetailLevel suppressed context, or no source text): there is no
                // gutter for the line number to live in, so keep it in a header line.
                if (!includeSourceContext || refLoc.Lines.Length == 0)
                {
                    sb.AppendLine($"  Line {line}{tags}");
                    continue;
                }

                // With a snippet the line number is already in the gutter — don't repeat it
                // in a header. Tags ride the matched line.
                AppendSourceSnippet(sb, refLoc.Lines, refLoc.StartLineNumber, line, "  ", tags);
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Formats a per-file distribution summary showing top files by reference count.
    /// </summary>
    public static string FormatFileDistribution(IReadOnlyList<FileDistributionEntry> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine("  Top files:");

        int shown = Math.Min(files.Count, MaxFileDistributionEntries);
        for (var i = 0; i < shown; i++)
        {
            FileDistributionEntry file = files[i];
            string truncatedPath = file.RelativePath.Length > PathDisplayWidth
                ? "..." + file.RelativePath[^(PathDisplayWidth - 3)..]
                : file.RelativePath;
            sb.AppendLine($"    {truncatedPath}: {file.ReferenceCount}");
        }

        if (files.Count > MaxFileDistributionEntries)
        {
            sb.Append($"    ... and {files.Count - MaxFileDistributionEntries} more files");
        }
        else
        {
            sb.Length -= Environment.NewLine.Length; // trim trailing newline
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Formats a per-project distribution summary for truncated reference/caller results.
    /// </summary>
    public static string FormatDistributionSummary(
        IReadOnlyList<ProjectDistributionEntry> distribution)
    {
        var sb = new StringBuilder();
        sb.AppendLine("  Distribution:");
        foreach (ProjectDistributionEntry entry in distribution)
        {
            string fileInfo = entry.FileCount > 0 ? $" ({entry.FileCount} files)" : "";
            sb.AppendLine($"    {entry.ProjectName}: {entry.ReferenceCount}{fileInfo}");
        }

        int total = distribution.Sum(e => e.ReferenceCount);
        sb.Append($"  Total: {total} across {distribution.Count} projects");

        return sb.ToString();
    }

    /// <summary>
    ///     Formats callers grouped by file, with numbered call-site entries.
    /// </summary>
    /// <example>
    ///     Finding callers of <c>IShape.Describe()</c> with contextLines=0. The call-site
    ///     line is in the gutter, so the caller header drops the redundant <c>at line N</c>:
    ///     <code>
    ///     TestFixture/Services/ShapeService.cs [TestFixture]:
    ///       1. [method] string ShapeService.ProcessShape(IShape):
    ///             17 | return shape.Describe();
    ///     </code>
    /// </example>
    public static string FormatCallers(
        IEnumerable<CallerWithLineText> callers, string solutionDir,
        bool includeSourceContext = true)
    {
        // Flatten all caller entries into (file, caller, location) tuples for grouping by file
        List<(CallerWithLineText Entry, LocationWithContext Location)> flatEntries = callers
            .SelectMany(entry => entry.LocationsWithContext.Select(loc => (Entry: entry, Location: loc)))
            .ToList();

        IOrderedEnumerable<IGrouping<string, (CallerWithLineText Entry, LocationWithContext Location)>> grouped =
            flatEntries
                .GroupBy(x => x.Location.Loc.GetRelativePath(solutionDir))
                .OrderBy(g => g.Key);

        var sb = new StringBuilder();
        var index = 1;

        foreach (IGrouping<string, (CallerWithLineText Entry, LocationWithContext Location)> group in grouped)
        {
            string? projectName = group.First().Entry.ProjectName;
            string projectTag = projectName is not null ? $" [{projectName}]" : "";
            sb.AppendLine($"{group.Key}{projectTag}:");

            foreach ((CallerWithLineText entry, LocationWithContext locCtx) in
                     group.OrderBy(x => x.Location.StartLineNumber))
            {
                string callerKind = entry.CallingSymbol.GetKindString();
                string callerName = entry.CallingSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                int line = locCtx.Loc.GetLineSpan().StartLinePosition.Line + 1;

                // No snippet: no gutter for the call-site line, so keep it in the header.
                if (!includeSourceContext || locCtx.Lines.Length == 0)
                {
                    sb.AppendLine($"  {index}. [{callerKind}] {callerName} at line {line}");
                }
                else
                {
                    // The call-site line is in the gutter below — don't repeat it as
                    // "at line N".
                    sb.AppendLine($"  {index}. [{callerKind}] {callerName}:");
                    AppendSourceSnippet(sb, locCtx.Lines, locCtx.StartLineNumber, line, "      ");
                }

                index++;
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Formats DI registrations as a fallback section when find_references on .ctor
    ///     returns no results.
    /// </summary>
    public static string FormatDiRegistrationsFallback(
        IReadOnlyList<DiRegistration> registrations, string solutionDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"However, {registrations.Count} DI registration(s) found:");
        sb.AppendLine();

        for (var i = 0; i < registrations.Count; i++)
        {
            DiRegistration reg = registrations[i];
            sb.AppendLine($"  {i + 1}. {reg.LineText}  [{reg.ContainerName}, {reg.Lifetime.ToLowerInvariant()}]");
            sb.AppendLine($"     {FormatRegistrationLocation(reg, solutionDir)}");
        }

        sb.Append("(DI-registered types are instantiated by the container at runtime, not via direct constructor calls)");
        return sb.ToString();
    }

    /// <summary>
    ///     Formats a DI registrations summary section for find_implementations results
    ///     (both member and type dispatch).
    /// </summary>
    public static string FormatDiRegistrationsByType(
        IReadOnlyDictionary<string, IReadOnlyList<DiRegistration>> registrationsByType, string solutionDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DI registrations:");

        foreach ((string typeName, IReadOnlyList<DiRegistration> registrations) in
                 registrationsByType.OrderBy(kv => kv.Key))
        {
            foreach (DiRegistration reg in registrations)
            {
                sb.AppendLine($"  {typeName}: {reg.Lifetime.ToLowerInvariant()} ({reg.ContainerName}) — {FormatRegistrationLocation(reg, solutionDir)}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Renders a located source snippet — dedented lines with a <c>{lineNum,4} |</c>
    ///     gutter — into <paramref name="sb" />.
    /// </summary>
    /// <remarks>
    ///     The line number appears only in the gutter, never duplicated in a header. When
    ///     more than one line is shown (contextLines &gt; 0) the matched line is prefixed
    ///     with <c>&gt;</c> and a blank separator follows the record; a single line gets no
    ///     marker. Extracted so the reference and caller formatters emit identical snippets.
    /// </remarks>
    /// <param name="sb">Destination buffer.</param>
    /// <param name="lines">Raw source lines of the snippet window (pre-dedent).</param>
    /// <param name="startLineNumber">1-based source line number of <c>lines[0]</c>.</param>
    /// <param name="matchLine">1-based source line of the match (gets the <c>&gt;</c> marker).</param>
    /// <param name="indent">Leading indent before the marker (file-relative nesting).</param>
    /// <param name="matchedLineSuffix">Text appended after the matched line only (e.g. tags).</param>
    internal static void AppendSourceSnippet(
        StringBuilder sb, string[] lines, int startLineNumber, int matchLine,
        string indent, string matchedLineSuffix = "")
    {
        string[] dedentedLines = TextDedenter.Dedent(lines);
        bool hasContext = dedentedLines.Length > 1;
        for (var i = 0; i < dedentedLines.Length; i++)
        {
            int lineNum = startLineNumber + i;
            bool isHit = lineNum == matchLine;
            string marker = hasContext ? isHit ? "> " : "  " : "";
            string suffix = isHit ? matchedLineSuffix : "";
            sb.AppendLine($"{indent}{marker}{lineNum,4} | {dedentedLines[i]}{suffix}");
        }

        if (hasContext)
        {
            sb.AppendLine();
        }
    }

    private static string FormatRegistrationLocation(DiRegistration reg, string solutionDir)
    {
        string relPath = Path.GetRelativePath(solutionDir, reg.FilePath);
        string projectTag = reg.ProjectName is not null ? $" [{reg.ProjectName}]" : "";
        return $"{LocationFormat.Format(relPath, reg.Line)}{projectTag}";
    }
}
