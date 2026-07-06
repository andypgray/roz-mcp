> **Historical record.** Written 2026-06-16 against the tool as it existed then. The server was
> named `roslyn-mcp` before the 2026-07 rename to `roz-mcp`, so the tool names, tool counts, and
> preset sizes in the text below are period-correct for that date, not the current tool surface.
> Published July 2026 verbatim except for the redaction of internal file paths. A few links to
> internal working files (`../backlog/`, run-output `results/`, pre-rename source paths) do not
> resolve in this repository and are left as written rather than rewritten. Bugs identified
> here were subsequently fixed. See the evidence index at [README.md](README.md) for how this
> document maps to the project's current claims.

# MCP-prompt efficacy testing — Phase 1 (plumbing + pilot)

**Status: COMPLETE — all six MCP prompts SHIP.** Phase 1: `assess_impact` + `fix_diagnostics`. Phase 2
(`20260617T150535Z` + re-run `20260617T190252Z`): `check_breaking_changes` + `decompile_symbol` SHIP on
the first sweep; `cleanup_dead_code` SHIP after a public-API-gate reword (first sweep was safe but 1/3
reps over-deferred and removed nothing); `tighten_accessibility` SHIP after fixing an `accessibility-is`
harness bug (slugged keys + a parser matching real `find_symbol` output). Both fixes deployed/live and
confirmed by the re-run (P5 recall 1.0, P6 all checks pass, 3/3 reps each).

This file is the canonical write-up for the prompt-efficacy experiment, mirroring the CI / analyze_method
precedent ([ab-test-mcp-vs-no-mcp-2026-04-18.md](ab-test-mcp-vs-no-mcp-2026-04-18.md)). `docs/` is gitignored,
so this lives on disk only.

## What this experiment is (and is NOT)

The server gained six user-invoked MCP prompts (`src/RoslynMcpServer/Prompts/*Prompt.cs`). Prompts have **zero
standing cost** — unlike tools they are not loaded into context for normal sessions — so the A/B cost/benefit
question (PROMOTE/HOLD into the `default` preset) **does not apply**. The only thing worth testing is
**efficacy / correctness / safety**: when invoked, does the recipe produce a correct, complete, safe outcome,
and does it avoid the failure modes it exists to prevent (Razor blind spot, public-API gate, fix-don't-suppress,
in-solution-vs-external split)?

Turn/token counts are recorded but **informational only** — a recipe spending extra turns on a Razor
cross-check is the recipe *working*.

The decision axis is **SHIP / FIX-THE-RECIPE**, never HOLD-for-cost (see [Decision rule](#decision-rule)).

## Phase 1 scope (this session)

Built the reusable plumbing and proved it end-to-end on the two pilot prompts:

- **assess_impact** (primary, near-free) — reuses task 04's TypeChange scenario + the existing
  `04-impact-analysis.reference.md` oracle + the impact judge rubric.
- **fix_diagnostics** (primary EDIT pilot) — a planted patch + the new `diagnostics-delta` and suppression-
  detector verifiers; no LLM judge (`rubric: none`).

Phase 2 prompts (`check_breaking_changes`, `decompile_symbol`, `cleanup_dead_code`, `tighten_accessibility`)
have their **rubrics/verifiers in place** but their tasks/patches/references are deferred to the next session.

## Harness additions

### The render bridge (the key enabler)

A prompt's recipe is only obtainable by a real MCP `prompts/get`. `scripts/ab-test/src/roslyn_abtest/mcp_client.py`
(lifted from `generate_references.py`'s hand-rolled client) adds `get_prompt` and a one-shot `render_prompt`.
`runner.py` branches at the brief: a task with a `prompt:` key has its recipe rendered (memoized per
(prompt, args) across reps) and used as the brief; **every existing 00–13 tool task is byte-identical** (the
A/B-integrity invariant). When the task declares a `report:` artifact, a one-line "write your report to X"
directive is appended (the grading hook — analogous to the naive tasks that name their report file in the body).

`$FIXTURE_SHA` in `prompt_args` is expanded to the pinned commit at render time (for
`check_breaking_changes`'s `baseline`, which the manifest owns).

### New task frontmatter (discriminated by `prompt:`)

```yaml
prompt: fix_diagnostics                       # render this prompt's recipe as the brief
prompt_args: {severity: "warning", scope: "Nop.Core", diagnosticIds: "CS0168,CS0219"}
setup_patch: patches/P2-planted-warnings.patch  # git apply before the run
setup_commit: true                            # commit the patch so the run diff = agent edits only
report: IMPACT_ASSESS.md                       # report artifact (judged prompts)
rubric: impact | method | breaking | decompile | none
reference: 04-impact-analysis                  # reuse another task's .reference.md (defaults to self)
```

`parse_task` is unchanged (extra keys are ignored by the runner); for a prompt task the markdown body is
documentation only and is **not** sent to the model.

### New verifiers (`verification.py`)

| Verifier | Purpose |
|---|---|
| `diff-absent` | Fail if any **added** diff line matches a regex (suppression / silent-IVT / must-not-edit detector). The highest-value new verifier — it's how "did the agent cheat by suppressing?" is caught. |
| `diff-contains` | Pass only if every regex matches an added line. |
| `diagnostics-delta` | Count remaining targeted diagnostics (`ids`/`severity`/`scope`); assert `<= max_remaining`. Runs its OWN `--no-incremental` build — an incremental build skips up-to-date projects without re-emitting their warnings, so reusing the exit-code build would under-count whatever the agent already compiled (a false pass). Verifier builds are off the measured wall-clock. |
| `accessibility-is` | Re-resolve a member via a live `find_symbol` and assert its `[access …]` tag equals `expected` (for `tighten_accessibility`, Phase 2). |

`VerifyContext` now threads `diff_full` (the run diff, agent edits only) and a stashed full `build_output`.

### Judge rubrics (`judge.py`)

Dispatch is now frontmatter-driven (`rubric:`/`report:`/`reference:`) with the original eight tasks falling
back to a hard-coded legacy registry. Two new rubrics added alongside `impact`/`method`:
- **`breaking`** — break-class accuracy + recall over the planted set + in-solution/external split.
- **`decompile`** — factual accuracy + grounding + hallucination count + source-first.

### Other

- `configs/arm-prompt-recipe.json` — the single acceptance arm (`ROSLYN_TOOLS=all`, production snippet,
  full editing tool set). Modeled on `arm-a-all`.
- `cli.py` pre-flight — for any `setup_patch`, checks the patch file exists and (when the clone is cached)
  `git apply --check`s it against the pinned SHA, so a stale patch fails the sweep before burning budget.
- Pilot tasks: `tasks/P1-assess-impact.md`, `tasks/P2-fix-diagnostics.md`; patch `patches/P2-planted-warnings.patch`.

## Pipeline-proven offline (this session)

- **Unit gates green:** `ruff check .` ✓, `mypy src/roslyn_abtest` ✓, `pytest` ✓ (118 passed / 19 integration
  deselected; the 16 SDK-stubbed judge tests pass under `-m integration`). New coverage: the four verifiers,
  `get_prompt` parsing, `render_task_brief` memoization + `$FIXTURE_SHA` + report directive, `_resolve_judged`
  dispatch + legacy fallback, the breaking/decompile judge functions.
- **Patch validated:** `patches/P2-planted-warnings.patch` applies cleanly to the pinned SHA
  (`git apply --check`), confirming the cli pre-flight path.
- **Render smoke green** against a freshly-built server binary (`bin/Debug/net10.0/RoslynMcpServer.exe`):
  - `fix_diagnostics` → `severity=Warning`, `project=Nop.Core`, `CS0168,CS0219`, `resetBaseline=true`, Razor
    blind-spot all present and correctly bound. (P2's `diagnostics-delta` CS0168/CS0219 must reach 0.)
  - `assess_impact` → `target`/`change` injected, `analyze_change_impact` mapping, report-first, Razor present.

This proves render → (run → verify → judge): the render bridge does a real `prompts/get` round-trip with correct
argument binding; the run/verify/judge stages are unit-tested. Only the **live model sweep** remains, and it is
the user's billed step.

## Prerequisite (DONE): the global tool now serves the prompts

The arm config drives the **installed** `~/.dotnet/tools/roslyn-mcp` for both the agent's MCP server and the
render bridge. That tool was stale (it predated the prompts — `prompts/get` → `Unknown prompt`), so it was
**redeployed via `/deploy` (2026-06-17, version 0.1.0)**, and the render smoke now passes against the installed
binary. Re-deploy again only if the prompts change before the next sweep:

```bash
# from a plain terminal (kills running roslyn-mcp processes) — or just /deploy
dotnet pack src/RoslynMcpServer/RoslynMcpServer.csproj -c Release
dotnet tool update -g --add-source src/RoslynMcpServer/bin/Release RoslynMcpServer
```

## Running the pilot (billed; from a PLAIN terminal)

```bash
roslyn-abtest run --task P1-assess-impact --task P2-fix-diagnostics --arms arm-prompt-recipe --reps 3
roslyn-abtest judge   # judges P1 (impact rubric); P2 is mechanical-only (rubric: none)
```

Then inspect each run under `results/<ts>/`: `.json` (verifier passes), `.diff` (trap checks), `.artifacts/`
(the report). **Run `roslyn-abtest judge` from a plain terminal, not inside a Claude session** (the nested-CLI
hang). Keep `--reps ≥ 3` — the zero-trap-violation gate is sensitive to a single bad rep and must absorb
casualties. Keep a watchdog on the sweep (the render path adds a process-spawn hang surface).

### Judge robustness fixes (2026-06-17, after the first pilot judge crashed)

The first `roslyn-abtest judge` crashed. Three fixes landed:

1. **Wrong-dir selection.** `_resolve_timestamp_dir` defaulted to `max(by name)`, and a manually-named
   `merged-impact-2026-06-02` sorts above every `YYYYMMDDTHHMMSSZ` dir (`'m' > '2'`) — so a no-`--timestamp`
   judge hit that *old* impact dir instead of the latest run. Now it restricts the auto-default to the
   timestamp-named dirs (explicit `--timestamp <name>` still selects a custom one). A mechanical-only run
   (all `rubric: none`, like a P2-only sweep) now judges to a clean **no-op**, no model call.
2. **cwd insulation.** The judge query set no `cwd`, so the headless CLI — launched from the repo — loaded
   the repo's project `.claude/` settings/skills (the run path is insulated by `cwd=<clone>`, outside the
   repo). The judge now runs its CLI in a scratch tempdir, matching the runs (only *user* settings load).
3. **Stderr capture + per-judgment resilience.** The SDK reported a bare "Command failed with exit code 1"
   and discarded the CLI's stderr. `_call_judge` now attaches a `stderr` callback and re-raises with the
   captured tail, and `run_judge` records a null-scored judgment (with the reason) and continues instead of
   aborting — so one bad call can't lose the whole run.

Fix 1 is confirmed (no-arg judge resolves to the latest run; the P2 sweep judges cleanly). Fixes 2–3 are
sound but unconfirmed on a live impact judge (couldn't run a model call from inside a session) — running P1
from a plain terminal will confirm, and if it still fails the captured stderr now names the cause.

## Decision rule

**SHIP / FIX-THE-RECIPE** — never HOLD-for-cost. SHIP a prompt when, across all reps:

1. **Zero trap violations** (a gate, not an average): no editing on a read-only prompt; no added suppression;
   no deleting a Razor-live/DI/dispatch/public-gate symbol; no narrowing a CS0053/Blazor/Razor/by-design member.
2. **Build green on every edit run.**
3. **Completeness recall ≥ bar:** targeted diagnostics cleared = 100%; `assess_impact` `site_recall` ≥ the
   existing impact-task threshold; (Phase 2) dead-symbol removal ≥ 0.9, should-narrow ≥ 0.9, breaking recall ≥ 0.9.

Otherwise **fix the recipe** (reword/strengthen the failing step) and re-run.

## Verdicts

### Pilot (Phase 1)

| Prompt | Pipeline | SHIP / FIX | Notes |
|---|---|---|---|
| `assess_impact` | **live, 3/3 reps** | **SHIP** | 2026-06-17 (`20260617T131424Z`): report-only trap held 3/3 (diff_loc=0, build green — never edited), site-recall **0.879**, verdict-accuracy **1.000**. Judge ran clean (confirms the cwd/stderr fixes). Recall ≈ the impact-task band; verdict accuracy perfect. |
| `fix_diagnostics` | **live, 3/3 reps** | **SHIP (mechanical gate)** | 2026-06-17 (`20260617T113722Z`): build green, `diagnostics-delta` CS0168/CS0219→0, `diff-absent` 0 suppression violations, diff_loc=3 (the three dead locals, no collateral) — all 3 reps. No LLM judge (rubric: none). |

**Harness nit fixed (2026-06-17):** P1 rep 1 wrote the report to `src/IMPACT_ASSESS.md` (it read the appended
"solution root" directive as the `.sln` folder), so the `file-exists` check false-negatived (the judge still
graded it via rglob, so recall is unaffected). The report directive now says "the TOP of your current working
directory, not a `src/` subfolder" — which is exactly what `file-exists` checks.

### Phase 2 (authored + pipeline-proven offline; live verdicts PENDING)

All four remaining prompts now have tasks/patches/references. The rubrics and verifiers were already
in place from Phase 1; Phase 2 added the scenarios, the planted patches, the hand-authored decompile
references, and the breaking-changes oracle mode in `generate_references.py`. Offline-validated below;
the live billed sweep + judge is the only remaining step.

| Prompt | Tasks | Scenario / planting | Grading |
|---|---|---|---|
| `check_breaking_changes` | `P3-check-breaking-changes` | `patches/P3-breaking-changes.patch` modifies `CommonHelper` (Nop.Core): **remove** `AreNullOrEmpty` (source break, 0 in-solution callers → external-surface), **sig-change** `GenerateRandomDigitCode` (+optional param → binary break, 6 sites), **behavioral-change** `IsValidIpAddress` (IPv4-only, same sig), plus an `internal` `EncryptionKeyMetadata` decoy that must be ignored. `setup_commit: true` so `git diff <SHA>...HEAD` = the planted changes. | `breaking` rubric vs `P3-…reference.md` (generated by the new oracle mode — `analyze_change_impact` forward-modeled per change on the clean clone, 0/6/5 sites, break classes hand-annotated). `loc-delta-max: 0` = read-only trap. |
| `decompile_symbol` | `P4a-decompile-path` (BCL `Path.Combine`), `P4b-decompile-pascalize` (Humanizer `Pascalize`), `P4c-decompile-markdig` (Markdig `Markdown.ToHtml`) | No patch — three real metadata symbols forcing the source / decompile branches. References hand-authored from `ilspycmd`-captured bodies + a false-claim checklist each (rooted-second-arg gotcha; "lowercases the rest" / `ToUpper` vs invariant; "sanitizes HTML by default" / advanced-extensions-on). | `decompile` rubric: factual_accuracy + grounding + hallucinations (checklist) + source_first. `loc-delta-max: 0` = read-only. |
| `cleanup_dead_code` | `P5-cleanup-dead-code` | `patches/P5-dead-code.patch` adds `Nop.Core/AbTest/PlantedCleanupTargets.cs` + a `.cshtml`: 3 `internal` `Planted_Dead_*` (must remove) and traps that must survive — markup-only, DI-registered, a real `INopStartup`, an interface impl, and one **public** member behind the ask gate. Reachable traps are `internal` so the gate can't trivially save them. | `rubric: none` (mechanical). `build` green + `token-residual` (`max_count: 0` for dead, `min_count: 1` for each trap) + `loc-delta-max: 80`. |
| `tighten_accessibility` | `P6-tighten-accessibility` | `patches/P6-accessibility.patch` adds files in Nop.Core/Nop.Services/Nop.Web: two over-broad `Planted_Narrow_*` (→internal, →private) and four must-not-narrow traps — cross-assembly use (Unsafe), pure CS0053 (tool says Compatible, build is the backstop), interface impl, and Razor-markup. `publicApiHandling: include` so narrowings actually happen. | `rubric: none`. `build` green (backstop for the cross-assembly/CS0053/interface traps) + `accessibility-is` (the two narrowings + the Razor trap, the cases with no build signal) + `diff-absent: InternalsVisibleTo` (no silent IVT). |

**Offline validation (this session):**
- **Gates green:** `ruff check .` ✓, `mypy src/roslyn_abtest` ✓, `pytest` ✓ (145 passed / 20 integration
  deselected). New coverage: `tests/test_breaking_oracle.py` (the oracle mode combines table + decoy +
  per-change sections) and `tests/test_phase2_tasks.py` (every P-task parses, uses known verifiers in a
  valid order, and has its patch + reference present).
- **Patches apply** to the pinned SHA (`git apply --check`): P3, P5, P6 all clean.
- **Breaking oracle generated** (`generate_references.py --task P3-check-breaking-changes`, 84 s): the
  forward-modeled `analyze_change_impact` returns **0** sites (AreNullOrEmpty removal — external only),
  **6** (GenerateRandomDigitCode), **5** (IsValidIpAddress + tests) — matching the planted reality.
- **Render smoke green** for all four prompts against the installed `roslyn-mcp`: each renders with the
  task's args correctly bound and contains the expected steps (analyze_change_impact mapping +
  source/binary classification + Razor cross-check; `ilspycmd` + prefer-source; the STOP-and-ask gate;
  CS0053 + analyze_change_impact). Judge dispatch resolves P3→`breaking`, P4a/b/c→`decompile`,
  P5/P6→`none`.

This proves render → (run → verify → judge) for Phase 2 short of the model call. Only the **live sweep**
remains.

#### Running the Phase 2 sweep (billed; from a PLAIN terminal)

```bash
# Decompile/breaking prompts are LLM-judged; cleanup/tighten are mechanical (rubric: none).
roslyn-abtest run --task P3-check-breaking-changes P4a-decompile-path P4b-decompile-pascalize P4c-decompile-markdig P5-cleanup-dead-code P6-tighten-accessibility --arms arm-prompt-recipe --reps 3
roslyn-abtest judge   # judges P3 (breaking) + P4a/b/c (decompile); P5/P6 are mechanical-only
```

Inspect each run under `results/<ts>/`: `.json` (verifier passes — `build_passed`, `*_residual_*`,
`accessibility_is_*`, `diff_absent_*`, `diagnostics_delta_*`), `.diff` (read-only / no-IVT trap checks),
`.artifacts/` (the judged report). **Run `roslyn-abtest judge` from a plain terminal, not inside a
Claude session** (the nested-CLI hang). Keep `--reps ≥ 3` (the zero-trap-violation gate is sensitive to
a single bad rep) and a watchdog on the sweep. Notes: `tighten_accessibility` runs ~3 short-lived
`find_symbol` servers per rep (one per `accessibility-is`), each a cold workspace load (~80 s) — budget
for it; the cleanup/tighten edit runs build the clone for the `build` verifier.

### Phase 2 verdicts — first live sweep (`20260617T150535Z`, `--reps 3`)

One run was a hang casualty (P4c rep 3 — model stall, killed to free the sequential sweep; absorbed
as `is_error`, which is exactly why `--reps 3`).

| Prompt | n | SHIP / FIX | Evidence |
|---|---|---|---|
| `check_breaking_changes` | 3 | **SHIP** | recall **1.0** / class-acc **1.0** / split 0.85–1.0 all reps; no edits (`loc-delta-max: 0` held); internal decoy never over-reported. |
| `decompile_symbol` (×3) | 8 (+1 casualty) | **SHIP** | factual 0.95–1.0 on real reps (P4b 0.97–1.0, P4c reps 1–2 = 1.0/0.98, P4a 0.95–0.97); **zero planted-checklist hallucinations** (the one P4a flag was a non-checklist over-claim about `JoinInternal` internals); no edits. **Recipe gap (non-blocking): source-first ≈ 0.2** — agents decompiled instead of reading real GitHub/runtime source, even where it exists. Accurate either way, but the "prefer source" step under-fires. |
| `cleanup_dead_code` | 3 + 3 re-run | **SHIP** (after gate fix) | First sweep: zero trap violations, build green — but rep 2 over-deferred at the public-API gate and removed nothing (recall 0.67). Gate reworded + deployed; **re-run `20260617T190252Z`: 3/3 reps removed all three `Planted_Dead_*` (recall 1.0), kept every trap, build green, loc=20 each.** |
| `tighten_accessibility` | 3 + 3 re-run | **SHIP** (after harness fix) | First sweep blocked by an `accessibility-is` harness bug — (a) three checks wrote the same result keys (only the last survived) and (b) the parser matched a bracketed `[public method]` tag while `find_symbol` emits unbracketed `1. public … method …`, so every check returned `None`. Both fixed (slugged keys + collision guard; parser handles both forms). **Re-run `20260617T190252Z`: 3/3 reps — `Planted_Narrow_AssemblyOnly`→internal, `Planted_Narrow_TypeOnly`→private, `Planted_Trap_RazorOnly` stayed public, build green, no silent IVT.** |

### Harness bug found + fixed (this run)

`accessibility-is` had never been exercised live (Phase 1 only stubbed it with bracketed text). The P6
run exposed two defects in `verification.py`, both now fixed with tests:
1. **Key collision** — every `accessibility-is` wrote fixed keys (`accessibility_is_pass`, …), so three
   checks in one task overwrote each other; only the last (the Razor trap) was recorded. Fixed by
   slugging keys per symbol (`accessibility_is_<slug>_pass`), mirroring `token-residual`, plus a
   `validate_verification_order` collision guard.
2. **Parser mismatch** — `_parse_access_tag` matched only a bracketed `[public method]` tag, but a
   single-symbol `find_symbol` returns `N. public static method …` (unbracketed). It returned `None`
   for every real result. Fixed to accept both forms; verified against the captured live output
   (un-narrowed → `public`; narrowed → `internal`/`private`).

### Recipe fix applied (2026-06-17, post-sweep)

The public-API gate's `ask` branch (`PromptFragments.GetPublicApiGate`) was the cause of P5 rep 2's
no-op: the old wording (`STOP and ask me. … Internal/private symbols don't need this confirmation.`)
let the agent read "STOP" as blocking *all* work. Reworded to scope the stop to externally-visible
members and to push the rest forward: *"STOP and ask me before {deleting/narrowing} any
externally-visible member … **Do not block the rest of the work on that answer:** {delete/narrow} the
`internal`/`private`/`file`-scoped symbols now — they don't need confirmation — and hold only the
externally-visible ones pending my reply."* Built clean, ReSharper-cleaned, and **deployed**
(`/deploy`, v0.1.0); render smoke confirms the new wording is live in both `cleanup_dead_code` and
`tighten_accessibility` (`ask`). This benefits `tighten_accessibility`'s `ask` mode too (though P6 runs
under `include`).

### Re-run done (`20260617T190252Z`) — both confirmed

`roslyn-abtest run --task P5-cleanup-dead-code P6-tighten-accessibility --arms arm-prompt-recipe --reps 3`,
3/3 reps each, no casualties:
- **P5:** every rep removed all three `Planted_Dead_*` (recall **1.0**), kept all five traps, build green,
  `loc=20` — the gate reword fixed the rep-2 over-deferral.
- **P6:** every rep narrowed `Planted_Narrow_AssemblyOnly`→**internal** and `Planted_Narrow_TypeOnly`→**private**,
  kept `Planted_Trap_RazorOnly` **public**, build green (cross-asm/CS0053/interface traps held), no silent IVT.

**All six prompts SHIP.** Only open item is optional: `decompile_symbol` source-first ≈ 0.2 (agents
decompile instead of reading real source) — accurate either way; reword step 2 only if that metric matters.
