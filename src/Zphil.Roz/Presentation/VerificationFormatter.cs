using System.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Models;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Formats the <see cref="EditVerification" /> block that a verified edit prepends to its normal
///     output. Kept token-lean: a single headline (error deltas + scope), then only the introduced
///     errors grouped by code. The block is prepended, not appended, because
///     <see cref="Pipeline.ResponseTruncator" /> cuts from the end and the delta is the highest-signal
///     content.
/// </summary>
internal static class VerificationFormatter
{
    private const int MaxNamedProjects = 2;

    /// <summary>
    ///     Prepends the verification block to <paramref name="body" />. Returns <paramref name="body" />
    ///     unchanged when there is nothing to report (<c>verify=None</c>), so that path stays byte-identical.
    /// </summary>
    public static string Prepend(EditVerification? verification, string body) =>
        verification is null ? body : $"{Format(verification)}\n\n{body}";

    public static string Format(EditVerification verification)
    {
        if (verification.SkippedReason is not null)
        {
            return $"Verification skipped — {verification.SkippedReason}.";
        }

        DiagnosticsDelta delta = verification.Delta!;
        var sb = new StringBuilder();

        if (verification.Mode == VerifyMode.DryRun)
        {
            sb.AppendLine("DRY RUN — no files written. Op results below show what a commit would produce.");
        }

        int introducedCount = delta.Introduced.Count;
        sb.Append(introducedCount == 0 ? "Verification: no new errors" : $"Verification: +{introducedCount} new error(s)");
        if (delta.ResolvedCount > 0)
        {
            sb.Append($", -{delta.ResolvedCount} resolved");
        }

        sb.Append($" | scope: {FormatScope(delta.ScopeProjects)}");

        if (introducedCount > 0)
        {
            sb.AppendLine();
            sb.Append(DiagnosticOutputFormatter.FormatIncrementalDiagnostics(delta.Introduced, delta.SolutionDir));
        }

        if (delta.UncoveredFiles is { Count: > 0 } uncovered)
        {
            sb.AppendLine();
            sb.Append(
                $"Note: {uncovered.Count} changed file(s) are outside the loaded workspace — no delta coverage: " +
                $"{String.Join(", ", uncovered)}.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Renders the recompiled cone as "N project(s) (name1, name2, +K more)", naming up to
    ///     <see cref="MaxNamedProjects" /> before collapsing the rest to a count.
    /// </summary>
    private static string FormatScope(IReadOnlyList<string> projects)
    {
        int count = projects.Count;
        string noun = count == 1 ? "project" : "projects";

        if (count == 0)
        {
            return "0 projects";
        }

        if (count <= MaxNamedProjects)
        {
            return $"{count} {noun} ({String.Join(", ", projects)})";
        }

        var named = String.Join(", ", projects.Take(MaxNamedProjects));
        return $"{count} {noun} ({named}, +{count - MaxNamedProjects} more)";
    }
}
