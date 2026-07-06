using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Zphil.Roz.Prompts;

/// <summary>
///     MCP prompt that previews the blast radius of a proposed change to one symbol — a read-only
///     "what breaks if I change this?" report built on <c>analyze_change_impact</c>, with a free-text
///     change description the recipe maps to a concrete <c>changeKind</c>. Report-first: it mutates
///     only on explicit confirmation.
/// </summary>
[McpServerPromptType]
internal sealed class AssessImpactPrompt
{
    /// <summary>
    ///     Emits a blast-radius recipe for <paramref name="change" /> applied to
    ///     <paramref name="target" />, optionally scoped by <paramref name="project" />.
    /// </summary>
    [McpServerPrompt(Name = "assess_impact", Title = "Assess change impact")]
    [Description(
        "Preview the blast radius of a proposed change to a symbol before making it: every affected site "
        + "reported as compatible, needs-update, or unsafe. Report-first — nothing changes unless you "
        + "confirm.")]
    public static string AssessImpact(
        [Description("The symbol to assess — a name (e.g. 'Order.Total') or a cursor 'path:line:col'.")]
        string target,
        [Description(
            "What you're thinking of changing, in plain English — e.g. 'make it a long', "
            + "'remove this method', 'make it internal', 'add a CancellationToken parameter'.")]
        string change,
        [Description("Optional project-name substring to disambiguate a symbol that exists in several projects.")]
        string? project = null)
    {
        string projectNote = String.IsNullOrWhiteSpace(project)
            ? ""
            : $" Scope every lookup to `project={project}`.";

        string verifyStep = PromptFragments.GetVerifyStep(
            "the change",
            "If something that should still compile doesn't, stop and show me.",
            "the baseline you just captured");

        return
            $"""
             Assess the blast radius of a proposed change to `{target}` *before* making it — a read-only
             "what breaks if…" preview using the roz-mcp tools. Report first; touch code only if I confirm.

             {PromptFragments.ToolPreflight()}

             1. **Resolve the target.** Resolve `{target}` (a symbol name or `path:line:col`) with
                `find_symbol`/`go_to_definition`. If it's ambiguous (overloads, several matches), list the
                candidates and ask which I mean — {PromptFragments.AsMultipleChoice(false)}, one option per
                candidate (labelled with signature + declaring type/project). Echo back what you resolved —
                kind, signature, declaring type and project — so we're aligned before analyzing.{projectNote}

             2. **Map my change to a `changeKind`.** Translate "{change}" into exactly one
                `analyze_change_impact` change:
                {PromptFragments.ChangeKindMapping}
                Echo the mapping back in one line (e.g. "reading 'make it a long' as TypeChange newType=long
                on `Order.Total`"). If my wording is ambiguous, ask which I mean —
                {PromptFragments.AsMultipleChoice(false)} among the four change kinds (TypeChange,
                RemoveSymbol, AccessibilityNarrow, SignatureChange). Two changes fall outside those four —
                handle each before running the tool rather than forcing a bad fit:
                - **A behavioral / unrepresentable change** (same signature, different semantics — a
                  null-handling, thrown-exception, or ordering change; anything the four kinds don't capture):
                  say plainly that `analyze_change_impact` **can't model** it, then assess by hand —
                  `find_references` the symbol and reason per-site about what the new behavior does to each
                  caller. Skip the step-3 `analyze_change_impact` call and fold that manual census into steps 5–6.
                - **A rename**: route to `rename_symbol`, not `analyze_change_impact`. It's an atomic
                  solution-wide rewrite, and the blast radius is already known without running anything —
                  every reference `RequiresUpdate`, all of them mechanical, and `rename_symbol` performs each
                  one for you. Report that and offer to run it in step 7 instead of the edit path.

             3. **Run the analysis.** Call `analyze_change_impact` with the resolved target, that `changeKind`
                (plus `newType`/`newAccessibility`/`newSignature` where required), `includeTests=true`, `contextLines=1`,
                and a generous `maxResults`.

             4. **Cover the Razor blind spot.** The analysis is reference-based, and {PromptFragments.RazorBlindSpot}.
                Text-search the markup (`*.razor`, `*.cshtml`) for the symbol name; fold any hits into the
                report as extra impacted sites tagged "markup — not classified by the analyzer", so the blast
                radius isn't undercounted.

             5. **Present the blast radius**, mirroring the tool's own output: lead with the headline
                (N sites — X compatible, Y require updates, Z unsafe), then the per-project distribution, then
                the per-site list with each site's verdict and reason. Append the markup sites from step 4.

             6. **Interpret.** Tell me whether the change is **safe** (all compatible), **mechanical**
                (requires-update — e.g. add casts / update call sites), or **breaking** (any unsafe, or a
                removal). Note what the analyzer can't see: one-hop data flow only — reflection, `dynamic`,
                and source-generated callers are out of scope (the same blind-spot class as the markup caveat).

             7. **Offer to apply — don't apply yet.** This is a preview; nothing has changed. Ask whether to
                proceed — {PromptFragments.AsMultipleChoice(false)} between "apply now (declaration plus the
                mechanical requires-update fixes)" and "keep as preview — change nothing". If I pick apply,
                capture the baseline **before** touching anything — {PromptFragments.BaselineCapture} The
                explicit `resetBaseline=true`, run strictly ahead of the edits, is deliberate: the edit tools'
                own auto-capture no-ops against a stale pre-existing baseline, so leaning on it would leave the
                verify below diffing against the wrong snapshot. Then make the edits at the declaration plus
                those fixes (`edit_symbol`/`replace_content`) and verify: {verifyStep} If I pick keep as
                preview, stop and change nothing.

             Start with step 1.
             """;
    }
}
