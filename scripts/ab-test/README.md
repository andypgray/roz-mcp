# A/B test: does roz-mcp help Claude complete realistic tasks?

Orchestrates paired runs against the cached nopCommerce clone to quantify whether
advertising the roz-mcp server is net-positive, neutral, or net-negative.

## What it measures

For each `(task, arm, rep)` cell, the harness captures:

- Token usage (input, output, cache read, cache creation).
- Wall-clock time and turn count.
- Tool-call histogram (how often Claude reached for which tool).
- End-state `git diff` of the nopCommerce clone.
- Whether `dotnet build` on the clone exits 0.
- **Diff hygiene** (`src/roslyn_abtest/diffmetrics.py`): semantic LOC (raw ± lines minus
  verbatim-resurrected churn), churn ratio, and BOM-strip / whole-file-rewrite flags. A raw
  `diff_loc` count is gameable — a baseline that "wins" on cost by rewriting whole files (100%
  churn, ~0 real change) looks the same as a surgical edit until you net out the churn. These
  four fields (`diff_loc_semantic`, `diff_churn_ratio`, `diff_bom_stripped_files`,
  `diff_rewritten_files`) surface as the `Sem LOC` / `Churn%` columns in `summary.md` and back
  the `diff-hygiene` verifier.

Arms (see `configs/*.json` for the full set):

- **arm-a-default** — `roz-mcp` registered with the default tool subset, plus the
  production `project-instructions-snippet.md` injected into the system prompt. Mirrors
  how the server is deployed to real users.
- **arm-a-all** — same as `arm-a-default` but with the full Roslyn tool surface enabled,
  to measure how much the default trim costs.
- **arm-b-baseline** — no MCP servers, no injected snippet. Baseline Claude.

Both arms share the same base toolset: `Read`, `Edit`, `Write`, `Grep`, `Glob`, `Bash`,
the same model (`claude-opus-4-7`), the same fresh-per-run nopCommerce state, and the
same first-message brief.

## Tasks

- `tasks/00-smoke.md` — one-turn no-op task for verifying the harness plumbing works.
  Not part of the real experiment.
- `tasks/01-feature-add.md` — add an `OrderFeedback` entity + service following
  existing nopCommerce patterns. Edit-heavy; moderate MCP advantage.
- `tasks/02-audit.md` — produce a markdown audit of top fan-in services, most-referenced
  entities, and biggest god-classes. Read-only; strong MCP advantage.
- `tasks/03-refactor-rename.md` — solution-wide interface rename (`IAffiliateService` →
  `IAffiliateManagementService`) across 22 references in 14 files. Exercises `rename_symbol`
  on its home turf; the task's `token-residual` verifiers record grep-hit counts for the
  old and new names as completeness metrics (`iaffiliateservice_residual_count` and
  `iaffiliatemanagementservice_residual_count` in the per-run JSON).
- `tasks/14-dead-code-traps.md` — the **safety-tail control**. A plain tool-task (no `prompt:`,
  so the no-MCP baseline can attempt it) that plants the `P5-dead-code.patch` traps and asks the
  agent to remove *only* genuinely-dead code under `Nop.Core/AbTest/`. Three `Planted_Dead_*`
  symbols must go (`max_count: 0`); five `Planted_Trap_*` symbols reachable only via DI, Razor
  markup, interface dispatch, or reflection-scanned startup must survive (`min_count: 1`), with a
  green build and `loc-delta-max: 80`. Measures whether an arm's tooling lets it honor "don't
  delete reachable code" — the differential the trap grid (09/13/14) exists to expose.

To run the real experiment only (skipping the smoke task):

```bash
python scripts/ab-test/run.py --task 01-feature-add --reps 2
python scripts/ab-test/run.py --task 02-audit --reps 2
```

## Tool-promotion A/B experiments (CI / AM arm families)

Beyond the original MCP-vs-no-MCP comparison, `configs/` carries arm families that
test whether a specific tool **held out of the `default` preset** earns promotion.
Each isolates one tool by differing from a baseline arm by exactly that tool (server
`ROZ_TOOLS` env + client allow-list).

- **CI family — `analyze_change_impact`.** `arm-ci-baseline` (default 10 tools) vs
  `arm-ci-on` (default + `analyze_change_impact`). Verdict: **HOLD** — see
  [docs/evidence/ab-test-analyze-change-impact-2026-06-01.md](../../docs/evidence/ab-test-analyze-change-impact-2026-06-01.md).
  The decisive lesson: that was an *availability-only* test — the tool was registered
  but the injected snippet never routed the agent to it, so adoption (not capability)
  drove the HOLD.

- **AM family — `analyze_method`.** A 3-arm design that fixes the adoption blind spot:
  - `arm-ci-baseline` — anchor (default 10, production snippet).
  - `arm-am-on` (**N**, bare-add) — default + `analyze_method`, production snippet
    **unchanged**. Replicates the availability-only design; the diagnostic for "does a
    bare default-add move anything?" (expected ≈ no).
  - `arm-am-routed` (**R**, routed) — default + `analyze_method`, **variant** snippet
    with one method-comprehension routing row. The promotion-deciding arm (default-add
    **plus** routing). **R vs baseline** decides promotion; **N vs baseline** is the
    bare-add diagnostic.

  Method-comprehension tasks (graded by the `analyze_method` judge rubric, except the
  trap):
  - `tasks/05-explain-service.md` — explain `ProductService`'s 8 key methods (batch
    comprehension + inbound/outbound). **Primary**, report `SERVICE_EXPLAINED.md`.
  - `tasks/10-method-callgraph.md` — map `OrderProcessingService.PlaceOrderAsync` +
    `UpdateOrderTotalsAsync` (the **outbound** call-graph differentiator). **Primary**,
    report `CALL_GRAPH.md`.
  - `tasks/11-method-interface.md` — enumerate inbound callers of
    `IProductService.GetProductByIdAsync` (inbound recall at scale + interface tip).
    Secondary, report `INBOUND.md`.
  - `tasks/12-method-overloads.md` — the `UpdateProductWarehouseInventoryAsync` overload
    pair (`includeOverloads` aggregation). Secondary, report `OVERLOADS.md`.
  - `tasks/13-method-trap.md` — a leaf method definition lookup. Histogram-only
    over-reach control (does routing wrongly trigger `analyze_method` where
    `go_to_definition` suffices?). **No** oracle, report `LEAF.md`.

### `claude_md_snippet_path` (variant-snippet override)

An arm config may set `"claude_md_snippet_path": "<repo-relative path>"` to inject a
variant of the system-prompt snippet instead of the production
`src/Zphil.Roz/project-instructions-snippet.md`. Absent → the production snippet
(so `arm-ci-baseline` / `arm-am-on` stay byte-identical to before the override existed —
A/B integrity preserved). `arm-am-routed` points it at
`project-instructions-snippet.analyze-method.md` (the production snippet plus one routing
row). Editing the **production** snippet is out of scope until a tool earns promotion;
on PROMOTE the routing row is merged into production.

### Oracles & grading

`generate_references.py` drives the installed `roz-mcp` against the pinned clone to
emit `tasks/<task>.reference.md` oracles for both tool families (per-spec `tool` field).
`roslyn-abtest judge` grades each arm's report against its oracle: impact tasks score
site-recall + verdict-accuracy; method tasks score inbound/outbound caller-graph recall +
hallucinations. `results/` stays gitignored — the canonical write-ups live under
`docs/evidence/`.

The `02-audit` task is LLM-judged too (frontmatter `rubric: audit`, `report: AUDIT_REPORT.md`).
Its oracle (`_generate_audit_reference` in `generate_references.py`, run via
`generate_references.py --task 02-audit`) is bespoke and multi-tool: three ranked ground-truth
tables — top service interfaces by caller fan-in (`find_references`), entities by project spread
(`find_implementations` on `Nop.Core.BaseEntity` → per-entity `find_references` by cursor), and
biggest god-classes by LOC (Python line-count + `get_symbols_overview` member counts). The `audit`
rubric scores `fanin_recall` / `entity_recall` / `godclass_recall` / `count_plausibility` and counts
`hallucinated_items`. Rankings are `maxResults=1` header totals (which carry the true count + project
spread even when truncated), so the oracle stays small; entity refs are looked up by cursor because
common entity names (`Order` matches 54 symbols) are too ambiguous for name-based resolution.

## Prompt-efficacy experiment (P-task family)

A separate experiment tests the six user-invoked **MCP prompts** (`src/Zphil.Roz/Prompts/*Prompt.cs`).
Prompts have **zero standing cost** (they aren't loaded into normal sessions), so PROMOTE/HOLD-into-`default`
doesn't apply — the axis is **SHIP / FIX-THE-RECIPE**: when invoked, does the recipe produce a correct,
complete, safe outcome and avoid the failure modes it exists to prevent? Turn/token counts are recorded but
never decision-driving. The single acceptance arm is `arm-prompt-recipe`. Canonical write-up:
[docs/evidence/ab-test-prompts-2026-06-16.md](../../docs/evidence/ab-test-prompts-2026-06-16.md).

**Render bridge.** A prompt task names a prompt; the runner renders its recipe via a real `prompts/get`
round-trip (`mcp_client.render_prompt`, memoized per (prompt, args)) and uses it as the brief. Tool tasks
(00–13) are byte-identical to before — the brief is rendered only when a `prompt:` key is present.

**Prompt-task frontmatter:**

```yaml
prompt: fix_diagnostics                            # render this prompt's recipe as the brief
prompt_args: {severity: "warning", scope: "Nop.Core", diagnosticIds: "CS0168,CS0219"}
setup_patch: patches/P2-planted-warnings.patch     # git apply before the run (cli pre-flight --checks it)
setup_commit: true                                 # commit the patch so the run diff = agent edits only
report: IMPACT_ASSESS.md                            # graded report artifact (judged prompts only)
rubric: impact | method | breaking | decompile | none
reference: 04-impact-analysis                       # reuse another task's .reference.md (defaults to self)
```

`$FIXTURE_SHA` in `prompt_args` expands to the pinned commit at render time (for `check_breaking_changes`'s
`baseline`). The markdown body of a prompt task is documentation only — it is **not** sent to the model.

**New verifiers** (registry in `verification.py`):

- `diff-absent` / `diff-contains` — grep the run's **added** diff lines against regexes. `diff-absent` is the
  suppression / silent-IVT / must-not-edit detector (e.g. `["#pragma warning disable", "<NoWarn", "SuppressMessage"]`).
- `diagnostics-delta` — `{ids, severity, scope, max_remaining}`; counts remaining targeted diagnostics in a
  fresh `--no-incremental` build (a skipped up-to-date compile won't re-emit warnings, so an incremental build
  would under-count). Verifier builds run after the measured turn, so the extra compile is off the wall-clock.
- `accessibility-is` — `{symbolName, containingType?, expected}`; re-resolves the member via a live `find_symbol`
  and asserts its `[access …]` tag.
- `diff-hygiene` — `{max_churn_ratio?, forbid_bom_strip?}`; fails a run that games diff-size cost metrics
  via whole-file rewrites (churn above `max_churn_ratio`) or stripping UTF-8 BOMs. Used on
  `03-refactor-rename` (`max_churn_ratio: 0.5, forbid_bom_strip: true`), where a surgical rename churns ~0%
  and a whole-file-rewrite baseline churns ~100%.

**Pilot** (Phase 1, proven offline; live sweep pending a tool redeploy — see the write-up):

```bash
roslyn-abtest run --task P1-assess-impact --task P2-fix-diagnostics --arms arm-prompt-recipe --reps 3
roslyn-abtest judge   # P1 uses the impact rubric; P2 is mechanical-only (rubric: none)
```

## Running

```bash
# One-time setup
pip install claude-agent-sdk

# All tasks, both arms, 2 reps each -> 8 runs
python scripts/ab-test/run.py --reps 2

# Single task
python scripts/ab-test/run.py --task 01-feature-add --reps 2

# Single arm (useful for smoke-testing)
python scripts/ab-test/run.py --task 02-audit --arms arm-b-baseline --reps 1

# Re-aggregate existing results without re-running
python scripts/ab-test/analyze.py --timestamp 20260418T140000Z

# Pick the model for a sweep (default $ROZ_ABTEST_MODEL or claude-opus-4-7). Pass MULTIPLE
# tasks as space-separated values after ONE --task (nargs='+'); repeating --task keeps only
# the last. The trap grid (over-reach controls 09/13 + the 14 safety differential):
roslyn-abtest run --task 09-impact-trap 13-method-trap 14-dead-code-traps \
  --arms arm-a-default arm-b-baseline --reps 3 --model claude-opus-4-8

# Backfill diff-hygiene fields into existing run JSONs and rewrite their summaries (idempotent).
# No --timestamp -> every timestamped results dir; --timestamp for one (incl. a merged-* dir).
roslyn-abtest backfill
roslyn-abtest backfill --timestamp 20260701T191256Z

# LLM-judge a run's report artifacts against their oracles (Fable keeps judge spend low)
roslyn-abtest judge --timestamp 20260701T191256Z --model claude-fable-5
```

Flags:

- `--task <stem|all>` — which task brief to run (default: `all`).
- `--arms <name> [<name> ...]` — which arms to run (default: all configs).
- `--reps N` — reps per `(task, arm)` (default 2).
- `--max-turns N` — kill a stuck run after N turns (default 80).
- `--max-budget-usd X` — per-run spend cap (default 10).
- `--seed N` — shuffle seed for run ordering (default 42).
- `--model <id>` — model for the sweep (`run`) or grader (`judge`); overrides `$ROZ_ABTEST_MODEL` /
  `$ROZ_ABTEST_JUDGE_MODEL`, else `claude-opus-4-7`.

**`judge` is not idempotent** — it re-bills every report and overwrites `<task>-<arm>-<rep>.judgment.json`.
It skips its own prior judgments (they carry a `judge_model` key), so a re-run doesn't judge judgments,
but it *will* re-judge and re-bill the run reports. Invoke it once per dir, only on dirs with un-judged
report tasks. `backfill`, by contrast, is safe to re-run — it recomputes the same hygiene fields in place.

## Output

Each run writes three files under `results/<timestamp>/`:

- `<task>-<arm>-<rep>.json` — structured metrics.
- `<task>-<arm>-<rep>.transcript.json` — tool-call log and assistant/user text.
- `<task>-<arm>-<rep>.diff` — `git diff` of the clone at end of run.

After all runs, a `summary.md` is written with per-run and per-cell aggregate tables,
tool histograms, and a qualitative-notes stub to fill in by hand.

## Reading the results honestly

The plan's verification step asks: **would I be better off not installing this MCP server?**
The numbers are only one input — inspect the diffs and transcripts before concluding:

- `summary.md` tables answer delta-tokens / delta-time / delta-completion.
- Tool histograms show whether Arm A actually used the MCP tools or fell back to grep.
  If Arm A didn't call any `mcp__roz__*` tools, the run is uninformative — the
  snippet wasn't persuasive enough and the comparison collapsed to Arm B.
- The `.diff` files answer idiomaticity by eye — did the feature code look native?
- The `.transcript.json` files show where each arm spent its turns.

Re-run with `--reps 3` (or higher) if n=2 variance looks too noisy to conclude.

## Explicitly NOT tested here

- **Automatic CLAUDE.md from this repo** — the SDK doesn't walk parent dirs for
  project CLAUDE.md by default (`setting_sources=None`), and we don't override.
- **User-memory / global preferences** — same reason.

## Implementation notes

- The clone is reset via `git reset --hard <sha> && git clean -fdx` before each run,
  so prior runs can't pollute state.
- Run order is shuffled (seeded) to dampen any time-of-day drift.
- `ROZ_SOLUTION_PATH` is injected into the Arm A MCP server env so the MCP workspace
  loads the correct solution.
- `permission_mode="bypassPermissions"` skips permission prompts — the harness is
  non-interactive by design.
