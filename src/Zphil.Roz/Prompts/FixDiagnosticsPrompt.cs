using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Server;

namespace Zphil.Roz.Prompts;

/// <summary>
///     MCP prompt that drives compiler/analyzer diagnostics to clean — baseline, triage, minimal
///     root-cause fixes, incremental verify, loop. Its value over a generic agent is the server-specific
///     discipline: the Razor phantom-error blind spot, the "Available analyzer fixes" hints,
///     fix-don't-suppress, and incremental verification against a captured baseline. Applies edits.
/// </summary>
[McpServerPromptType]
internal sealed class FixDiagnosticsPrompt
{
    /// <summary>
    ///     Emits a fix-the-diagnostics recipe scoped by <paramref name="scope" /> and filtered by
    ///     <paramref name="severity" /> / <paramref name="diagnosticIds" />.
    /// </summary>
    [McpServerPrompt(Name = "fix_diagnostics", Title = "Fix diagnostics")]
    [Description(
        "Drive compiler and analyzer diagnostics to clean with minimal, root-cause fixes rather than "
        + "blanket suppressions. Applies edits.")]
    public static string FixDiagnostics(
        [Description("Optional project-name substring to scope the work to one project; omit for the whole solution.")]
        string? scope = null,
        [Description(
            "Which diagnostics to fix: 'error' (default, errors only), 'warning' (warnings and errors), "
            + "or 'all' (also info-level analyzer diagnostics).")]
        [AllowedValues("error", "warning", "all")]
        string severity = "error",
        [Description("Optional comma-separated diagnostic codes to restrict to (e.g. 'CS8618,CA2007'). Omit for everything in the severity band.")]
        string? diagnosticIds = null)
    {
        string scopeClause = String.IsNullOrWhiteSpace(scope)
            ? ""
            : $" Scope every `get_diagnostics` call to `project={scope}`.";

        string severityClause = severity.Trim().ToLowerInvariant() switch
        {
            "warning" => "`severity=Warning` (warnings and errors)",
            "all" => "`severity=Info` (errors, warnings, and info-level analyzer diagnostics)",
            _ => "`severity=Error` (errors only)"
        };

        string idsClause = String.IsNullOrWhiteSpace(diagnosticIds)
            ? ""
            : $" Restrict to `diagnosticIds=[{diagnosticIds}]`.";

        string verifyStep = PromptFragments.GetVerifyStep(
            "your fix",
            "Revert any \"fix\" that introduced a new error and rethink it.",
            "the step-1 baseline");

        return
            $"""
             Drive diagnostics to clean in this solution using the roz-mcp tools — baseline, fix minimally,
             verify incrementally, loop. Fix root causes, not symptoms; never suppress just to silence.

             {PromptFragments.ToolPreflight()}

             1. **Baseline, then pull the work-list.** {PromptFragments.BaselineCapture} Then list what to fix:
                call `get_diagnostics` with {severityClause}.{idsClause}{scopeClause} One caveat before you
                trust that list — {PromptFragments.RazorBlindSpot}, and `get_diagnostics` additionally
                over-reports source-generator *phantom* errors, so cross-check any error in a Razor-adjacent
                file against `dotnet build` before treating it as real.

             2. **Triage.** Group the diagnostics by id and severity. Flag the ones whose output carries the
                server's "Available analyzer fixes" hint — those have a known code fix and are the cheap wins.
                Fix errors before warnings.

             3. **Fix.** For each diagnostic apply the minimal correct fix with `edit_symbol` /
                `replace_content` (fall back to your normal file-editing tools if the `editing` category isn't
                loaded on this server). Prefer the root-cause fix over a suppression; reach for
                `#pragma warning disable` or `<NoWarn>` only when the diagnostic is genuinely a false positive,
                and only with an explicit one-line reason. Never mass-suppress to drive the count down.

             4. **Verify incrementally.** After each batch of fixes, {verifyStep} Confirm the ids you targeted
                actually cleared and that no new diagnostics appeared.

             5. **Loop.** Re-pull the work-list (`get_diagnostics` again — NOT `resetBaseline`, so the
                step-1 baseline stays intact) and repeat steps 2–4 until the scope is clean or only
                deliberately-left items remain. Finish with a summary: what you fixed, and what you left and
                why (intentional, suppressed-with-reason, or a phantom Razor/generator error).

             Start with step 1.
             """;
    }
}
