using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Zphil.Roz.Prompts;

/// <summary>
///     MCP prompt that triages code-complexity / quality-debt hotspots semantically. It does NOT compute
///     complexity itself — it sources the raw metric from whatever provider the user already has (CRAP via
///     Coverlet/ReportGenerator, an NDepend / <c>Microsoft.CodeAnalysis.Metrics</c> report, CA15xx
///     diagnostics, or in-server size/coupling proxies, requiring none) and then layers the triage no
///     linter or metrics engine can: reachability (is this hotspot dead or test-only?), caller fan-out
///     across projects, refactor blast radius, DI lifecycle, and the complexity × coverage fusion. Each
///     hotspot is bucketed and routed to the specialist prompt (<c>cleanup_dead_code</c> /
///     <c>assess_impact</c> / <c>tighten_accessibility</c> / <c>fix_diagnostics</c>). Sibling to
///     <c>triage_coverage</c>: same "take a raw numeric signal, add semantic triage" shape. Report-first —
///     it prioritises and routes rather than refactoring, and adds no new MCP tool.
/// </summary>
[McpServerPromptType]
internal sealed class TriageComplexityPrompt
{
    /// <summary>
    ///     Emits a complexity-triage recipe scoped by <paramref name="scope" /> (or the files changed since
    ///     <paramref name="baseline" />), ranking hotspots by an existing complexity metric and triaging
    ///     each into a bucket with a recommended specialist prompt.
    /// </summary>
    [McpServerPrompt(Name = "triage_complexity", Title = "Triage complexity hotspots")]
    [Description(
        "Rank the worst complexity / quality-debt hotspots in a scope from an existing complexity metric, "
        + "then triage each: dead code, a safe local refactor, a risky high-fan-out change, over-exposed "
        + "API, or a mechanical analyzer fix — and route each to the right action. Report-first; changes "
        + "code only on confirmation.")]
    public static string TriageComplexity(
        [Description(
            "What to triage: a project, file, type, or namespace substring. Omit to triage only the files "
            + "changed versus `baseline` on this branch.")]
        string? scope = null,
        [Description("Git ref the changed-files default diffs against (used only when `scope` is omitted). Default 'main'.")]
        string baseline = "main")
    {
        string scopeClause = String.IsNullOrWhiteSpace(scope)
            ? $"the files changed on this branch since `{baseline}`"
            : $"files matching `{scope}`";
        string scopeResolution = String.IsNullOrWhiteSpace(scope)
            ? $"List the changed `.cs` files with `git diff {baseline}...HEAD --name-only -- '*.cs'` — if "
              + $"`{baseline}` doesn't resolve as a git ref, fall back to the repo's default branch "
              + "(`git symbolic-ref --short refs/remotes/origin/HEAD`, e.g. `origin/master`)."
            : $"List the files matching `{scope}`.";

        return
            $"""
             Triage the complexity / quality-debt hotspots in {scopeClause} using whatever complexity metric is
             already to hand for the raw numbers and the roz-mcp tools for the semantics. The value here is
             NOT a metrics table — linters, ReSharper and metrics engines already produce that; it's deciding,
             per hotspot, *which ones matter and what fixing them costs*. Read-first: prioritise and route to
             the specialist prompts; change code only if I confirm.

             {PromptFragments.ToolPreflight()}

             1. **Acquire a complexity metric — use what's already here, require nothing, never mutate
                silently.** Don't compute complexity / maintainability / coupling yourself — existing engines do
                it better, so *consume* a number, don't re-invent one — and don't add a provider behind my back.
                Detect what's available in this order and stop at the first hit:
                - **CRAP / cyclomatic from Coverlet + ReportGenerator** — the same path `triage_coverage`
                  drives. Coverlet's cobertura emits a per-method `complexity`; ReportGenerator's
                  **risk-hotspots** table (in its `MarkdownSummary`) surfaces cyclomatic complexity **and the
                  CRAP score** — complexity × coverage, already the fusion we want. Zero new dependency, and
                  `coverlet.collector` ships in the test project, so prefer this when a coverage run is cheap or
                  already done: run coverage as `triage_coverage` does, then
                  `reportgenerator -reports:<cobertura> -targetdir:<tmp>/report -reporttypes:MarkdownSummary` and
                  read the risk-hotspots table.
                - **A metrics report I already use** — NDepend output, `Microsoft.CodeAnalysis.Metrics`
                  (`dotnet build /t:Metrics`), or a cached `Metrics.exe` run → parse its XML for
                  MaintainabilityIndex / CyclomaticComplexity / ClassCoupling / DepthOfInheritance per
                  type/member.
                - **CA15xx already enabled** (CA1501/1502/1505/1506) → read it straight from `get_diagnostics`
                  — no run, no dependency; the threshold message embeds the number (e.g. "complexity of 28").
                - **None of the above** → rank from in-server proxies, always available: type size and member
                  count via `get_symbols_overview`, method length plus outbound-callee count (a coupling proxy)
                  via `analyze_method`. Say plainly you're ranking on proxies, not a true metric.
                A provider that runs but yields **no per-method rows is a miss, not an answer** — e.g.
                `Microsoft.CodeAnalysis.Metrics` emits empty reports on the .NET 10 SDK — so fall through to the
                next provider in this list instead of triaging an empty table.
                Only if I want a richer provider I don't yet have, offer — {PromptFragments.AsMultipleChoice(false)}
                — to add it transiently (the `Microsoft.CodeAnalysis.Metrics` dev-dependency, or CA15xx in
                `.editorconfig`) and **revert it after**. Never add a package or edit `.editorconfig` without
                that pick; if you do add one, step 8 removes it.

             2. **Select the scope.** {scopeResolution} Narrow to production code — **skip test and generated
                files**; neither is a refactor target.

             3. **Rank the hotspots.** Order the in-scope symbols by the metric you acquired — highest
                cyclomatic/cognitive complexity, lowest maintainability index, highest coupling. Cross-reference
                analyzer **smells** on the same symbols from `get_diagnostics` (including the "Available analyzer
                fixes" hints): a hotspot that also trips CA / StyleCop rules is a stronger signal and may carry a
                ready point-fix.

             4. **Pull the semantic context — the value-add.** This is the part no linter or metrics engine can
                do. Per top hotspot: `analyze_method` for its caller/callee map (fan-out), `find_references`
                (`includeTests=true`) for fan-in across projects plus the DI annotations it tags, and
                `find_implementations` for interface/override dispatch. In an interface/impl-heavy solution a
                bare method name is ambiguous — target `analyze_method` by `location` (`path:line`, a real path
                not a glob) or by `symbolName`+`containingType`+`kind` so it resolves to the one method you mean.

             5. **Triage each hotspot into a bucket with a recommended route** — routing to the specialist
                prompt, not refactoring here (the server has no extract/move tool, so the actual refactor is
                yours):
                - **Dead / unreachable** — no callers and not reachable via DI / dispatch / markup (the same
                  checks `cleanup_dead_code` runs; {PromptFragments.RazorBlindSpot}, so text-search the markup
                  (`*.razor`, `*.cshtml`) for the name, and check the `find_references` DI tags and
                  `find_implementations` dispatch before concluding). A complex method nobody calls is a
                  **deletion, not a refactor** → route to `cleanup_dead_code`.
                - **High-impact, high fan-out** — genuine debt but risky to touch. **Run `analyze_change_impact`
                  now — do not defer it to `assess_impact`** — on the symbol, with the `changeKind` that matches
                  the fix you'd make (`SignatureChange` for a param-heavy method you'd fold into a parameter
                  object, `AccessibilityNarrow` for an over-exposed member, `TypeChange`/`RemoveSymbol` where they
                  fit), and report its Compatible / RequiresUpdate / Unsafe split **in this triage**. That
                  quantified blast radius is the value-add over a linter; `assess_impact` is the route for
                  actually applying the change. (If the fix is purely internal — no signature/visibility change —
                  there's no external blast radius, so it belongs in *safe-localised*, not here.)
                - **Low-impact, localised** — a safe, high-value refactor; name the concrete move (extract the
                  validation block, split the responsibilities, collapse the nested conditionals). No
                  extract/move tool exists, so the edit itself is yours via `edit_symbol` / the editor.
                - **Over-exposed** — `public`/`protected` with few or no external callers → route to
                  `tighten_accessibility`.
                - **Mechanical analyzer fix** — a CA15xx (or similar smell) a point-fix clears → route to
                  `fix_diagnostics`.

             6. **Complex × untested = highest risk.** When the metric is CRAP (or coverage is already to
                hand), this fusion is baked into the score — surface the high-CRAP hotspots first. With a
                complexity-only provider, optionally borrow a `triage_coverage` pass to add the coverage axis so
                the "complex *and* untested" items rise to the top.

             7. **Razor caveat.** {PromptFragments.RazorBlindSpot} — a markup-heavy type's complexity and its
                references are both under-counted, so flag any such hotspot as low-confidence rather than a firm
                verdict.

             8. **Report, then offer.** Lead with the headline — N hotspots: A dead, B safe-localised, C
                high-impact (each with its step-5 `analyze_change_impact` Compatible/RequiresUpdate/Unsafe
                verdict — don't claim a blast radius you didn't run), D over-exposed, E point-fixes — ordered by leverage
                (impact × fixability, untested-first). Per item give the symbol, its metric, its semantic shape
                (fan-in / fan-out, reachability), and the recommended action + target prompt. Then
                {PromptFragments.AsMultipleChoice(true)} — one option per bucket, tick which to act on now versus
                report-only. **If step 1 added a package or an `.editorconfig` entry, remove it now** before you
                finish.

             Start with step 1.
             """;
    }
}
