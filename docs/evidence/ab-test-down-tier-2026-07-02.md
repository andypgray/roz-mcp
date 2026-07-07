> **Historical record.** Written 2026-07-02 against the tool as it existed then. The server was
> named `roslyn-mcp` before the 2026-07 rename to `roz-mcp`, so the tool names, tool counts, and
> preset sizes in the text below are period-correct for that date, not the current tool surface.
> Published July 2026 verbatim except for the redaction of internal file paths. A few links to
> internal working files (`../backlog/`, run-output `results/`, pre-rename source paths) do not
> resolve in this repository and are left as written rather than rewritten. Bugs identified
> here were subsequently fixed. See the evidence index at [README.md](README.md) for how this
> document maps to the project's current claims.

# Down-tier A/B: does roz-mcp's navigation value migrate to cheaper models? (2026-07-02/03)

**Question.** Opus 4.7 (April) showed roz-mcp winning on cost (−24.7% on 02-audit, −60.9% on
03-refactor-rename). Opus 4.8 (July 1) flipped every task to +26.7…+47.6% and audit adoption
collapsed (`find_references` calls 53 → 7): the stronger model trusts grep and mostly ignores
the routing snippet. **Hypothesis:** the navigation value didn't vanish, it migrated down-tier.
If Sonnet 5 / Haiku 4.5 show 4.7-shaped wins, roz's durable pitch includes "raises the floor of
the cheaper models most orgs actually run"; if they don't, navigation value is gone at every
tier and the product repositions around executors (rename), the verifier (get_diagnostics),
and the prompts.

**Answer (headline).** Neither, cleanly — the value *split*. The navigation-as-cost-saver story
is dead at every tier (audit cost delta positive on 4.8, Sonnet, and Haiku). But the
**executor** story migrated exactly as hypothesized: Haiku used `rename_symbol` in 3/3 reps and
delivered a 4.7-shaped **−57.5%** on the rename; Sonnet showed the only quality separation —
zero killed traps and half the churn while its baseline deleted live Razor/public-API code.
Reposition around executors + safety + report-accuracy, not navigation cost.

**Design.** Within-model A/B, 4 tasks × 2 arms × 3 reps per model (24 runs/model), sequential,
nopCommerce clone hard-reset per run. Arms: `arm-a-default` (roz-mcp, `ROZ_TOOLS=default`,
production routing snippet injected) vs `arm-b-baseline` (no MCP, no snippet). Never compare
absolute cost across models — prices differ; every verdict reads each model's own A−B delta.

| Tier | Model id | Results dir(s) |
|---|---|---|
| Opus 4.7 (Apr 20) | `claude-opus-4-7` | `20260420T213006Z` |
| Opus 4.8 (Jul 1)  | `claude-opus-4-8` | `20260701T191256Z` (01/02/03) + `20260702T121504Z` (14) |
| Sonnet 5 (Jul 2)  | `claude-sonnet-5` | `20260702T171038Z` |
| Haiku 4.5 (Jul 3) | `claude-haiku-4-5-20251001` | `20260703T075318Z` (22 sweep + 2 patch cells, see incidents) |

Judge: `claude-fable-5` on every tier's 02-audit reports against the Part A oracle
(`tasks/02-audit.reference.md`, Roslyn-generated rankings). Task 14 (planted dead code + five
trap symbols) is scored by mechanical token-residual verifiers, no LLM judge. Diff hygiene
(Sem LOC = semantic lines net of verbatim-resurrection churn; Churn% = fraction of raw ±lines
that are whole-file-rewrite noise) comes from Part A's `diffmetrics` backfill, applied to all
four tiers.

Pre-registered success criteria (fixed before any down-tier run):

- **H1 (cost):** on 02/03, arm-a within-model cost delta ≤ 0 down-tier.
- **H2 (quality):** arm-a Sem LOC/Churn% strictly better on 03; audit judge ≥ baseline on 02;
  on 14, arm-a preserves ≥ as many traps and removes ≥ as much dead code as baseline.
- **H3 (adoption):** roz calls well above 4.8's collapse (audit anchor: 4.7 = 53, 4.8 = 7
  `find_references` total; expect ≥ 20 down-tier).
- **Null result:** down-tier deltas look like 4.8's → navigation value is gone at every tier →
  reposition around executors / diagnostics verifier / prompts, not tiering.

## Results — four tiers, per task

Cells are means over 3 reps. Δ = arm-a vs arm-b within the same model. Adoption = mcp roz
calls per arm-a run (April used the pre-rebrand `mcp__roslyn-mcp__*` prefix; counted the same).
n=3 per cell — too small for Wilcoxon to clear 0.05; sign-consistency across reps is reported
where it holds.

### 01-feature-add (build-verified feature; diff is 1 tracked line + untracked new files)

| Tier | Cost Δ | Wall Δ | Adoption (roz calls/run) | Build |
|---|---|---|---|---|
| Opus 4.7 | +1.5% | +32.8% | 6.7 | n/r |
| Opus 4.8 | +26.7% | +35.8% | 1.0 | 100/100 |
| Sonnet 5 | +5.2% | +0.5% | **0.0** | 100/100 |
| Haiku 4.5 | +23.6% | +12.4% | **0.0** | 100/100 |

Neither down-tier model called a single roz tool on this task; their deltas are snippet/
tool-catalog overhead (plus normal rep variance). Pattern-following feature work never needed
navigation at any tier.

### 02-audit (report-only; judged by Fable 5 against the Roslyn oracle)

| Tier | Cost Δ | Wall Δ | Adoption (roz/run · Σfind_refs) | Judge composite A vs B |
|---|---|---|---|---|
| Opus 4.7 | **−24.7%** | −29.0% | 24.3 · **53** | 0.75 vs 0.69 |
| Opus 4.8 | +47.6% | +11.3% | 4.0 · **7** | 0.86 vs 0.81 |
| Sonnet 5 | +34.0% | **−24.2%** | 11.7 · **18** | **0.89 vs 0.80** |
| Haiku 4.5 | +19.7% | **−57.3%** | 8.7 · **13** | 0.60 vs 0.60 |

Judge composite = mean of (fan-in recall, entity recall, god-class recall, count plausibility).
April was judged 2026-07-02 with the same Fable + oracle as the new tiers; anchor-era reports
score lower than today's in both arms — read judge rows within-tier only.

The consistent down-tier shape: **arm-a audits are much faster** (Haiku: 202s vs 472s) **and
their numbers are real** (count plausibility — Sonnet 1.00 vs 0.77, Haiku 0.97 vs 0.62; the
baseline's cited caller counts drift or are fabricated), **but they cost more** —
`find_references` on god-class fan-in returns large token-priced outputs while the baseline's
grep grind is time-priced, not token-priced. At Haiku the speed came with shallowness: arm-a's
god-class section was weak (recall 0.11 vs 0.33) while fan-in recall also lagged (0.53 vs
0.80) — adoption without depth. Sonnet arm-a swept fan-in recall 1.00 vs 0.87. Arm-a still
mixed 2–20 Bash calls per rep alongside roz at both tiers; adoption is real but partial.

### 03-refactor-rename (IAffiliateService → IAffiliateManagementService, 22 refs + file rename)

| Tier | Cost Δ | Wall Δ | Churn% A/B | rename_symbol used | Residual |
|---|---|---|---|---|---|
| Opus 4.7 | **−60.9%** | +15.2% | 0 / 0 | (n/r) | n/r |
| Opus 4.8 | +43.4% | +76.9% | 0 / 66 | — | 0 / 0 |
| Sonnet 5 | +143.1% | +80.5% | 33 / 66 | **1 of 3 reps** | 0 / 0 |
| Haiku 4.5 | **−57.5%** | −6.6% | **0 / 0** | **3 of 3 reps** | 0 / 0 |

Every rep at every tier lands the identical semantic rename (44 sem LOC of reference edits;
file renamed in 24/24 runs — the 44-vs-147 Sem LOC spread across tiers is purely git
rename-detection representation, so it is omitted above). The tiers differ in **how**:

- **Haiku is the hypothesis confirmed.** `rename_symbol` in 3/3 arm-a reps ($0.115/$0.191/
  $0.121) vs a careful 43–49-tool baseline grind ($0.314/$0.358/$0.333). Arm-a cheaper in
  3/3 rep pairs, −57.5% mean — statistically indistinguishable from 4.7's −60.9%. Haiku's
  baseline never sed-rewrites (zero churn) — the win is pure executor efficiency.
- **Sonnet is the adoption cautionary tale.** Arm-a used `rename_symbol` once ($0.68, clean);
  once hand-edited 23 files via Grep+Edit ($1.18 — the most expensive run of the cell); once
  ignored roz and Bash-rewrote whole files ($0.48, 99.7% churn). The baseline's cheapest
  strategy (2 of 3 reps) is a 13,660-line whole-file rewrite at 99.7% churn for $0.21 — raw
  diff-LOC metrics used to reward exactly this. Churn% is the honest column: 33 vs 66.
- Opus 4.8 pays +43.4% for hand-editing discipline its baseline doesn't need (0% churn arm-a
  vs 66% baseline).

### 14-dead-code-traps (remove 3 planted dead symbols; preserve 5 traps reachable only via Razor/DI/dispatch/startup/public-API)

| Tier | Cost Δ | Dead removed A/B (of 3) | Traps kept A/B (of 5) | Build |
|---|---|---|---|---|
| Opus 4.7 | — (task added in Part A) | — | — | — |
| Opus 4.8 | +56.7% | 3.0 / 3.0 | 5.0 / 5.0 | 100/100 |
| Sonnet 5 | **−11.8%** | 2.0 / 3.0 | **5.0 / 4.3** | 100/100 |
| Haiku 4.5 | +0.6% | 3.0 / 3.0 | 5.0 / 5.0 | 100/100 |

Opus 4.8 and Haiku ace the task in both arms — no separation (Haiku is uniformly careful; 4.8
is uniformly capable). Sonnet is where the designed trade appears: baseline rep 2 **deleted two
live traps** (`RazorOnly` and `PublicUnused` — exactly the two with no direct C# reference;
text search says "dead", the build stays green, production breaks). Arm-a never killed a trap
at any tier, and on Sonnet was *cheaper* too — but over-corrected once (rep 2 removed nothing,
the safe-but-idle failure mode; its patched Haiku sibling and both other Sonnet reps removed
cleanly). The build verifier is blind to both trap kills (Razor compiles lazily; public API
has no in-solution callers) — only Part A's planted-token verifiers catch them.

## Verdicts (pre-registered)

- **H1 (cost ≤ 0 on 02/03 down-tier): fails on Sonnet, splits on Haiku.** Sonnet: audit
  +34.0%, rename +143.1%. Haiku: audit +19.7%, rename **−57.5%** (sign-consistent 3/3). The
  one surviving cost win is the executor, not navigation.
- **H2 (quality): passes 3/4 on Sonnet, 2/4 on Haiku.** Sonnet — churn 33 vs 66 ✓, judge 0.89
  vs 0.80 ✓, traps 5.0 vs 4.3 ✓, dead-removal 2.0 vs 3.0 ✗ (over-caution). Haiku — churn equal
  (0/0, not strictly better) ✗, judge 0.597 vs 0.605 ✗ (a tie within judge noise, but not ≥),
  traps 5.0 vs 5.0 ✓, dead-removal 3.0 vs 3.0 ✓.
- **H3 (adoption ≥ 20 Σfind_references on audit): not met.** Sonnet 18, Haiku 13 — 1.9–2.6×
  above 4.8's collapse (7) but under the pre-registered 20, and far from 4.7's 53. Adoption
  *reshaped* rather than recovered: navigation tools are consulted selectively; the executor
  is adopted reliably at Haiku (3/3 reps) where the snippet's replacement rule got followed.
- **Null-result check: rejected in its strong form.** Down-tier deltas do not simply look like
  4.8's — Haiku's rename is 4.7-shaped, Sonnet uniquely separates on safety, and both tiers
  flip audit wall-time hard in roz's favor. What *is* dead at every tier: navigation as a
  cost-saver.

## Positioning conclusion

1. **Retire the navigation-cost pitch.** "roz makes exploration cheaper" was true for exactly
   one model generation (Opus 4.7). On 4.8, Sonnet 5, and Haiku 4.5 the audit premium is
   +20–48%: MCP tool results are token-priced while grep is subsidised by local execution.
   No tier is coming back for it.
2. **The down-tier pitch is the executor.** Haiku + `rename_symbol`: −57.5% cost, 3/3
   adoption, zero churn, zero residual — it turns the cheapest model into the cheapest
   *correct* refactorer (Haiku arm-a renames at ~$0.14 vs Sonnet baseline $0.32 / Opus 4.8
   baseline $1.05, all landing the same 44-line semantic change). "Raises the floor" is true
   specifically for write-path executors, and that is where the roadmap should double down
   (more executors; the existing conservative-writes design is the moat).
3. **The mid-tier pitch is safety.** Only Sonnet's baseline killed live code (Razor-only +
   public-API traps, 1 of 3 reps) — the exact blind spots text search cannot see. Roz arms
   never killed a trap at any tier, and the Sonnet trap run was cheaper too. The honest
   framing: semantic verification prevents rare-but-catastrophic deletions, and its value
   peaks where the model is strong enough to act boldly but not strong enough to be right
   (today: Sonnet-class). Pair with the known over-caution failure mode (one frozen rep).
4. **The audit pitch is "fast reports with real numbers", not cheaper ones.** Wall −24%
   (Sonnet) to −57% (Haiku) with count plausibility 0.97–1.00 vs the baseline's 0.62–0.77
   fabrication drift — but expect +20–35% cost and, at Haiku, shallower coverage unless the
   recipe forces depth (the `analyze_method`/prompt recipes exist for exactly this).
5. **Rework the routing snippet from substitution table to condition-scoped triggers.** The
   "mandatory replacements" framing is ignored by strong models (4.8), followed 0–33% of the
   time by Sonnet, and followed reliably by Haiku only for the executor. The two rules the
   data actually supports: "for a solution-wide rename, call `rename_symbol`" (followed when
   cheap-model, honored 3/3) and "before deleting a symbol, `find_references` + markup grep"
   (would have saved Sonnet's baseline). Condition-scoped, verb-anchored lines; drop the
   tool-for-tool table.

## Incidents & caveats (methods honesty)

- **Usage-limit deaths (three).** `roslyn-abtest run` bills nested runs against the
  interactive Claude subscription window. Sonnet sweep 1 died at run 6/24
  (`20260702T141017Z`, quarantined); Haiku sweep 1 died at run 5/24 (`20260702T194638Z`,
  quarantined); Haiku sweep 2 died at run 23/24. Failed cells: `is_error=true`, `cost=0`,
  `stop=stop_sequence`, transcript "You've hit your limit · resets <time>". The two cells
  lost from Haiku sweep 2 were re-run individually in the next window
  (`20260703T132944Z`, `20260703T133804Z`) and copied into `20260703T075318Z`; the 14-traps
  patch run is an independent draw relabeled rep 1 → rep 3 (rep indices are arbitrary labels
  over independent clone-reset runs; the JSON keeps the patch run's own timestamp as
  provenance). Quarantined dirs are excluded from every table; their healthy early runs
  (5 Sonnet, 4 Haiku) reproduce the fresh sweeps' same-cell results.
- **Judge truncation (two).** One April and one Haiku judgment came back truncated mid-JSON
  (long `notes` field → unparseable); each was surgically re-judged with the same pinned
  Fable model via `judge_audit_report` and the dir's judge-summary rebuilt. The April repair
  matched the truncated reply's visible score prefix. Harness follow-up: cap/retry long judge
  notes.
- **April arm definition drift.** April's `arm-a-default` predates the "align with shipping
  default" change (10 → 11 tools; `analyze_change_impact` promotion) and the `roz` rebrand
  (tool prefix `mcp__roslyn-mcp__*`). Treated as the same arm for anchor purposes.
- **April gaps.** No build verifier on 01/02/03, no rename residual, no task 14 (didn't
  exist). Cells marked n/r or —.
- **Haiku `num_turns` quirk.** Several Haiku runs report `num_turns=1` despite dozens of tool
  calls and multi-minute walls; turn counts are unreliable for Haiku — `tool_call_count` is
  the effort proxy in Haiku rows.
- **Judge variance.** Each (arm, rep) judged once by pinned Fable; the oracle is Roslyn tool
  output eyeballed once; a wrong oracle biases both arms equally. The Haiku judge composite
  (0.597 vs 0.605) is inside single-judgment noise — read it as a tie, and as a strict-≥
  criterion miss.
- **Fixture pin.** All tiers run the same nopCommerce SHA clone, reset per run; April anchors
  ran the identical task files (14 excepted).

## Raw sources

- Summaries: `scripts/ab-test/results/<dir>/summary.md` (per-run tables, paired stats),
  `judge-summary.md` (per-rubric judge tables) — results/ is gitignored; this doc is canonical.
- Anchors quoted from the plan doc and reproduced independently by re-extraction from the run
  JSONs before writing this doc (April `find_references` total = 53 and 4.8 = 7 both confirmed).
