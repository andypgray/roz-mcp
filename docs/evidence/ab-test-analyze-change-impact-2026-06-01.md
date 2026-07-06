> **Historical record.** Written 2026-06-01 against the tool as it existed then. The server was
> named `roslyn-mcp` before the 2026-07 rename to `roz-mcp`, so the tool names, tool counts, and
> preset sizes in the text below are period-correct for that date, not the current tool surface.
> Published July 2026 verbatim except for the redaction of internal file paths. A few links to
> internal working files (`../backlog/`, run-output `results/`, pre-rename source paths) do not
> resolve in this repository and are left as written rather than rewritten. Bugs identified
> here were subsequently fixed. See the evidence index at [README.md](README.md) for how this
> document maps to the project's current claims.

# A/B test: does `analyze_change_impact` earn promotion into the default tool preset?

**Date:** 2026-06-01 (launched) → 2026-06-02 (completed after an overnight quota pause)
**Harness:** [scripts/ab-test/](../../scripts/ab-test/)
**Codebase:** nopCommerce @ `c91ad8a` (~35 projects, ~300K LOC)
**Model:** `claude-opus-4-7` (both arms)
**Arms:** `arm-ci-on` (11 tools — the 10-tool default preset **plus** `analyze_change_impact`) vs. `arm-ci-baseline` (the 10-tool default preset). The arms differ by **exactly one tool**.

**Tasks** — five impact scenarios, one per `changeKind` plus a rename "trap":

| Task | changeKind | symbol | oracle |
|---|---|---|---|
| 04-impact-analysis | TypeChange | `IRepository<T>.GetByIdAsync` → `Task<BaseEntity>` | 116 sites |
| 06-impact-remove | RemoveSymbol | `IStaticCacheManager.RemoveByPrefixAsync` | 183 sites, all Unsafe |
| 07-impact-accessibility | AccessibilityNarrow | `ILocalizationService.GetLocalizedAsync` (public→internal) | 375 sites (102 Compatible / 273 Unsafe) |
| 08-impact-signature | SignatureChange | `IProductService.GetProductByIdAsync` | 239 sites, all RequiresUpdate |
| 09-impact-trap | rename (no impact verdicts) | — | histogram-only |

5 reps × 2 arms × 5 tasks = 50 runs.

## TL;DR

- **Verdict: HOLD `analyze_change_impact` out of the default preset.** It fails the promotion bar decisively.
- **The blocker is discoverability, not capability.** With the tool loaded on perfect-fit tasks, the agent invoked it on **only 1 of 4 applicable tasks** (TypeChange), and **never** on RemoveSymbol, AccessibilityNarrow, or SignatureChange — it reached for `find_references` + manual reasoning every time.
- **The aggregate "wins" are not tool-attributable.** arm-ci-on looks cheaper on 07 (−31% cost) and 04 (−14% cost), but on 07 the tool was *never called*, and on 04 the cheap reps were the ones that *skipped* it. Crediting the tool for those deltas is precisely the error the tool-use guard exists to prevent.
- **No functional risk either way:** 100% build-pass on every valid run; the tool produced no regressions when used.
- Real promotion would also require rewriting the injected snippet routing table to list the tool — a production system-prompt change deliberately out of scope for this availability-only A/B.

## Decision rule (pre-registered)

Promote only if arm-ci-on shows an efficiency win (turns / tool-calls / cost) on **≥2 of the 3 primary tasks (04, 06, 07)** *with the tool actually invoked in those runs*. The "with the tool invoked" clause is load-bearing: a win in runs that never called the tool measures arm-to-arm variance, not the tool.

## Result 1 — Tool adoption (the headline)

`analyze_change_impact` invocations in arm-ci-on, from per-run tool histograms:

| Task | changeKind | reps invoking the tool | total calls |
|---|---|---|---|
| 04 | TypeChange | **3 / 5** (reps 1–3; rep 1 errored → 2 valid) | 4 |
| 06 | RemoveSymbol | **0 / 5** | 0 |
| 07 | AccessibilityNarrow | **0 / 5** | 0 |
| 08 | SignatureChange | **0 / 5** | 0 |

The tool was reached for **only** where the manual alternative is genuinely hard — TypeChange, which needs C# conversion rules applied by hand at each site. For Remove / Accessibility / Signature, the agent judged (reasonably) that `find_references` + per-site reasoning sufficed, and never invoked the tool — even though it was loaded and its `[Description]` had been rewritten to be action-oriented ("What breaks if you remove a symbol, change its type, narrow its access…"). This reproduces the pre-matrix probe (06: 0/3, 07: 0/1, 04: 1/1) at 5× the scale.

## Result 2 — Efficiency (arm-ci-on vs arm-ci-baseline, per task)

| Task | tool used? | Cost Δ | Wall Δ | Turns Δ | Tool-calls Δ | n |
|---|---|---|---|---|---|---|
| 04 TypeChange | yes (2–3/5) | −14.1% | +0.6% | +2.9% | +2.2% | 5 v 5 |
| 06 RemoveSymbol | **no (0/5)** | −0.8% | −7.2% | −8.4% | −9.0% | 5 v 5 |
| 07 AccessibilityNarrow | **no (0/5)** | −30.6% | −26.1% | −35.8% | −38.7% | 5 v 5 |
| 08 SignatureChange | no (0/5) | *quota-truncated* | — | — | — | 2 v 1 valid |
| 09 trap | — | *all runs failed* | — | — | — | 0 |

Paired Wilcoxon on cost (the primary metric): 04 p=0.44 (d=−0.28), 06 p=1.00 (d=−0.01), 07 p=0.06 (d=−0.68). **None reaches significance at α=0.05**; 07 is closest — but used the tool zero times.

## Result 3 — Why the aggregate wins are not the tool

- **Task 07 (the biggest delta):** arm-ci-on spent −31% cost / −36% turns — yet its histogram shows **zero** `analyze_change_impact` calls. It leaned almost entirely on `find_references` (19 calls, little else); baseline mixed `find_references` (16) with Grep (19), Read (6), Bash (9). The arm-ci-on agent simply converged faster on `find_references` alone. That is run-to-run strategy variance, and the held-out baseline *already has* `find_references`. The new tool earns no credit.
- **Task 04:** the −14% aggregate cost is carried by reps 4 and 5 ($0.86, $1.46) — the two reps that **did not** invoke the tool. The reps that did (2, 3) cost $2.82 and $1.20 (avg $2.01 vs baseline $1.78). Within task 04, *using* the tool correlated with *higher*, not lower, cost (n=2, high variance — rep 2 was a 52-turn outlier).

Net: on the only task where the tool was used (04), the tool-using runs were not more efficient; on the tasks where arm-ci-on was more efficient (06, 07), the tool was not used. **0 of 3 primary tasks satisfy the decision rule → HOLD.**

## Quota truncation (08, 09) — real limitation, immaterial to the verdict

The experiment was paused overnight for usage quota and auto-resumed the next morning. The resumed run (06→07→08→09 sequentially) exhausted the replenished quota at ~08:40: every subsequent agent run failed instantly (`Command failed with exit code 1`, 1 turn, $0) in **both arms identically** — the textbook account-level cliff (06/07 clean → 08 partial → 09 fully dead).

- **08-impact-signature:** 3/10 runs valid (2 baseline, 1 arm-ci-on); rest errored. Too thin for a comparison. Its aggregate "−18.7% cost" is an artifact of the $0 errored runs and should be ignored.
- **09-impact-trap:** 0/10 valid — entirely lost.

Both are **secondary** tasks (08 fed correctness grading; 09 is a histogram-only "does the agent wrongly invoke the tool on a pure rename?" trap). The 3 primary tasks completed *before* the cliff and are intact, so the decision rule is unaffected.

## Correctness (deferred)

The `judge` step (LLM grading of each arm's impact report against the Roslyn-generated oracle, for site-recall + verdict-accuracy on tasks 04/06/07/08) was **not run** — it makes opus-4-7 calls and quota was exhausted. It is a *supporting* dimension (does the tool make the *reports* more accurate), not part of the promotion rule, and cannot flip a HOLD driven by zero adoption. Append it later with `roslyn-abtest judge --timestamp merged-impact-2026-06-02` once quota is available.

## Secondary decision — `[Description]` rewrite: KEPT

The `analyze_change_impact` `[Description]` was rewritten mid-study from *"Blast radius of a proposed change; pass locations[] or symbolNames[]."* to the action-oriented *"What breaks if you remove a symbol, change its type, narrow its access, or change its signature — tags every reference Compatible/RequiresUpdate/Unsafe (the impact, not just the sites)…"*. It did **not** move adoption (still 0/5 on task 06 after the change, rebuilt + redeployed). It is **kept anyway** as a standalone clarity improvement — more accurate and more useful to any client browsing the tool, independent of the promotion decision.

## What real promotion would require

Adoption is gated by the injected `project-instructions-snippet.md` routing table ("find references → `find_references`"), which never lists `analyze_change_impact`, plus the agent's strong `find_references` prior. A bare default-add (the Phase-5 mechanic) would *register* the tool without changing behaviour. Real adoption would need the snippet routing table to route impact questions to the tool — a change to production system-prompt semantics, deliberately out of scope for this availability-only A/B.

## Reproduction

Raw data under [scripts/ab-test/results/](../../scripts/ab-test/results/):

- Primary task dirs (clean): `20260601T192458Z/` (04), `20260602T060914Z/` (06), `20260602T071511Z/` (07).
- Quota-truncated: `20260602T080932Z/` (08), `20260602T084324Z/` (09).
- Merged for analysis: `merged-impact-2026-06-02/` (all 50 runs + `summary.md`).

```
# regenerate the aggregate (free, no model calls)
roslyn-abtest analyze --timestamp merged-impact-2026-06-02
# correctness grading (needs quota)
roslyn-abtest judge --timestamp merged-impact-2026-06-02
```

Oracles: `scripts/ab-test/tasks/0{4,6,7,8}-impact-*.reference.md`, generated by `scripts/ab-test/generate_references.py` against the pinned clone.

## Caveats

- **n = 5 / cell, high variance.** Cost CIs are wide (04 arm-ci-on [$1.05, $2.22]). Directional only.
- **One symbol per changeKind.** A harder-to-reason symbol might lift adoption on the non-04 tasks.
- **Availability-only.** Measures what the agent does with the tool merely *registered*; it does not test the snippet-routing change that real adoption would need.
- **Quota truncation** lost 08/09 (see above).
- **Correctness not yet graded** (judge deferred).
- **Task-04 has one flagged run** (`arm-ci-on-1`, `is_error=True`/`stop_sequence`, but 31 turns + clean build) excluded from the efficiency comparison.
