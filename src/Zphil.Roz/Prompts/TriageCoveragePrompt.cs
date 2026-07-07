using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Zphil.Roz.Prompts;

/// <summary>
///     MCP prompt that triages code-coverage gaps semantically. It runs Coverlet/ReportGenerator to get
///     uncovered line numbers, maps each range back to the symbol that owns it, then classifies every gap
///     as dead code, a genuine untested branch (with a concrete test to write), or a low-confidence
///     reflection/markup case — so the output is "which gaps are worth a test and what that test should
///     drive", not a raw line list. Read-mostly: it writes tests only on confirmation.
/// </summary>
[McpServerPromptType]
internal sealed class TriageCoveragePrompt
{
    /// <summary>
    ///     Emits a coverage-triage recipe scoped by <paramref name="scope" /> (or the files changed since
    ///     <paramref name="baseline" />), mapping uncovered lines to symbols and triaging each gap.
    /// </summary>
    [McpServerPrompt(Name = "triage_coverage", Title = "Triage coverage gaps")]
    [Description(
        "Run coverage and triage each gap: dead code, a genuine missing test (with a concrete test to "
        + "write), or a likely false alarm. Writes tests only on confirmation.")]
    public static string TriageCoverage(
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
             Triage the coverage gaps in {scopeClause} using Coverlet/ReportGenerator for the raw numbers and
             the roz-mcp tools for the semantics. The value here is NOT a line list — it's deciding, per
             gap, whether it's worth a test. Read-mostly: propose tests, write them only if I confirm.

             {PromptFragments.ToolPreflight()}

             1. **Preflight `reportgenerator`.** The Coverlet collector ships with the test project (the
                `coverlet.collector` package), so the raw cobertura XML always gets produced — but the
                readable per-method summary comes from ReportGenerator. Check it's on PATH: run
                `reportgenerator --help` (or `where reportgenerator` / `which reportgenerator`).
                - **Present** — continue to step 2.
                - **Absent** — don't install it silently. Raise it to me: {PromptFragments.AsMultipleChoice(false)}
                  — "install `dotnet-reportgenerator-globaltool` now, then continue" (you'd run
                  `dotnet tool install -g dotnet-reportgenerator-globaltool` and resume) versus "skip it — work
                  straight from the raw cobertura XML" (you'll read the `<line number=… hits="0">` elements
                  yourself in step 3). If I pick install, run it; if I pick skip, take the raw-XML path.

             2. **Run coverage** (inline these commands — do NOT call any project-local coverage skill):
                `dotnet test <test-project> --collect:"XPlat Code Coverage" --results-directory <tmp>`. **If
                any test fails, stop** — coverage from a red suite is unreliable; report the failures and don't
                triage. Then, when `reportgenerator` is available, summarize — but feed it the cobertura
                file's **explicit path**, not a `**` glob (the glob isn't reliably expanded): Coverlet writes
                the file under a per-run GUID dir, so locate it first (`find <tmp> -name coverage.cobertura.xml`)
                and pass that path: `reportgenerator -reports:<that-path> -targetdir:<tmp>/report -reporttypes:"JsonSummary;lcov"`.

             3. **Select the gaps in scope.** {scopeResolution} Narrow to production code — **skip
                deleted/renamed-away files and test files**, neither is a triage target. For each surviving file
                list the uncovered line ranges — from the LCOV `DA:` records (lines with hit count 0) plus
                `report/Summary.json` for the per-method overview, or straight from the cobertura
                `<line number=… hits="0">` elements if you skipped ReportGenerator. Collapse consecutive
                uncovered lines into ranges (e.g. 45-52).

             4. **Map ranges → symbols.** Attribute each uncovered range to the member that owns it from
                **cobertura's own per-method records** — every `<class>` carries a `<methods>` list whose
                `<method>` entries have a `name`/`signature` and per-line hit counts, so the owning member and
                its line span come straight from the coverage file (ReportGenerator's `Summary.json` per-method
                rows are the equivalent when you took that path). Cobertura is primary here. Cross-check the
                symbol identity with `get_symbols_overview filePaths=[…]` — its member rows carry a `:line` you
                can line up against the method records — or just `Read` the file. Group the ranges per symbol;
                that symbol, not the raw line, is the unit you triage.

             5. **Triage each symbol's gap — the value-add.** For each gapped member decide which of three
                buckets it's in, using `analyze_method` (its caller/callee map; if `analyze_method` isn't
                loaded on this server, use `find_references referenceKinds=invocations` directly) plus the reachability
                checks `cleanup_dead_code` uses:
                - **No callers, and not otherwise reachable** → likely **dead code**, not a test gap. Before
                  saying so, rule out invisible reachability exactly as cleanup does: text-search the markup
                  (`*.razor`, `*.cshtml`) for the name, check the DI annotations `find_references` adds, and
                  `find_implementations` for interface/override dispatch. If all come back empty, don't propose
                  a test — flag it for `cleanup_dead_code`.
                - **Callers exist, or the uncovered lines are an unexercised branch of a live method** → a
                  **genuine gap**. Name the caller path a test would drive and say what the branch does — read
                  it with `contextLines` around the range, and note the method's outbound callees so the
                  suggested test sets up the right preconditions.
                - **Reachable only via reflection / DI / markup** → **low confidence**: the collector may just
                  be undercounting execution it can't attribute. Say so; don't over-claim a gap.

             6. **Razor caveat.** {PromptFragments.RazorBlindSpot}. Coverage of `.razor`/`.cshtml` and other
                generated sources is unreliable for the same reason — execution through markup is hard to
                attribute — so flag any markup-heavy gap as low-confidence rather than a firm miss.

             7. **Report, then offer.** Lead with the headline — N gapped symbols: X genuine gaps, Y dead-code
                candidates, Z low-confidence. For each genuine gap give the symbol, what the uncovered branch
                does, and a concrete test suggestion (the caller path to drive, the precondition to set up).
                Then {PromptFragments.AsMultipleChoice(false)} — "write the suggested tests now" versus "report
                only, change nothing". If I pick write, add the tests, then re-run coverage (step 2) to confirm
                the ranges actually closed.

             Start with step 1.
             """;
    }
}
