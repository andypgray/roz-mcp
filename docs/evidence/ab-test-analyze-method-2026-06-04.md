> **Historical record.** Written 2026-06-04 against the tool as it existed then. The server was
> named `roslyn-mcp` before the 2026-07 rename to `roz-mcp`, so the tool names, tool counts, and
> preset sizes in the text below are period-correct for that date, not the current tool surface.
> Published July 2026 verbatim except for the redaction of internal file paths. A few links to
> internal working files (`../backlog/`, run-output `results/`, pre-rename source paths) do not
> resolve in this repository and are left as written rather than rewritten. Bugs identified
> here were subsequently fixed. See the evidence index at [README.md](README.md) for how this
> document maps to the project's current claims.

# A/B test: does `analyze_method` earn promotion into the default tool preset?

**Date:** 2026-06-02 → 2026-06-04 (run across several days around a recurring quota cliff + an SDK-nesting hang)
**Status:** **COMPLETE — verdict HOLD** (re-tested 2026-06-06; HOLD stands — see the Re-test section). Adoption + a large tool-attributable turn-win are confirmed, but the canonical LLM judge shows a task-05 outbound-recall regression (routed 0.84 vs baseline 0.95) that fails the pre-registered correctness gate. The 2026-06-06 re-test (default `maxResults` 20→100) closed most of the gap (routed outbound 0.84→**0.888**, within ±0.05 of baseline 0.928) with the turn-win intact (**−40%**), but a confirmed residual — agents pass explicit low `maxResults`, re-capping the body-bounded outbound list — keeps it directionally short. The real cure — **decoupling outbound from `maxResults`** (`eb5cd1e`) — was re-tested 2026-06-06: it **fixes the recall regression outright** (proxy routed 0.906 ≥ baseline 0.895; the self-cap lever is gone), but the **turn-win did not reproduce** on that clean n=8 batch (routed +5% vs an anomalously fast 14.0 baseline), so **HOLD stands** — now on the *efficiency* case, not completeness. See the Fix-2 re-test subsection.
**Harness:** [scripts/ab-test/](../../scripts/ab-test/)
**Codebase:** nopCommerce @ `c91ad8a` (~35 projects, ~300K LOC)
**Model:** `claude-opus-4-7` (all arms + judge)

## TL;DR

**Verdict: HOLD `analyze_method` out of the `default` preset.** Unlike the sibling `analyze_change_impact` study (which failed on non-adoption), here the routing lever **works** — one snippet row flips adoption to a majority/all on 3 of 4 judged tasks — and the turn-win is **real and tool-attributable**: −54% turns on task 10 (7/7 winning reps used the tool), −30% on task 05. But the pre-registered **correctness gate fails on a primary**: on task 05 the routed agent **under-reports outbound collaborators by ~11%** (canonical judge: routed 0.84 vs baseline 0.95; replicated by an independent proxy at 0.80 vs 0.90). **Root-caused** (see Result 3): `analyze_method`'s default `maxResults=20` caps the 66-outbound god-method `SearchProductsAsync`'s list, so the tail collaborators are never shown — the tool even prints *"(66 total outbound — increase maxResults)"* and the agent doesn't act on it. So it's **HOLD-pending-a-targeted-fix** (raise the default / make the hint actionable), not a tool-capability flaw — the data is all retrievable at `maxResults≥66`, and the routed rep that skipped the tool scored at baseline level. Task 10 shows no cost (routed *more* complete). **No production changes.**

## The question

Does `analyze_method` — a compound navigation tool (method signature + inbound callers + outbound in-solution callees in one call), currently **held out of the `default` preset** — earn promotion into `default`? The sibling study on `analyze_change_impact` ([ab-test-analyze-change-impact-2026-06-01.md](ab-test-analyze-change-impact-2026-06-01.md)) returned **HOLD**, but the decisive cause was **non-adoption, not capability**: the tool was registered yet the injected project-instructions snippet never routed the agent to it, so it fell back to `find_references`. This experiment fixes that blind spot with a **3-arm design that tests the routing lever directly**.

## Arms

The three arms differ only in (a) whether `analyze_method` is registered and (b) whether the injected snippet routes to it. Everything else — base tools (`Read/Edit/Write/Grep/Glob/Bash`), model, fresh-per-run nopCommerce state, brief — is identical.

| Arm | Tools | Injected snippet | Role |
|---|---|---|---|
| `arm-ci-baseline` | default 10 | production | anchor |
| `arm-am-on` (**N**) | default + `analyze_method` (11) | production (**unchanged**) | bare-add adoption diagnostic |
| `arm-am-routed` (**R**) | default + `analyze_method` (11) | **variant** (production + one routing row) | the promotion-deciding arm |

The variant snippet adds exactly one row to the routing table: *"Understand a method end-to-end (signature + who calls it + what it calls) → `analyze_method`"*. **R vs baseline decides promotion; N vs baseline is the bare-add diagnostic.**

## Tasks (method-comprehension)

| Task | What it exercises | Report | Judged? |
|---|---|---|---|
| 05-explain-service | `ProductService`'s 8 key methods (batch comprehension, inbound+outbound) | `SERVICE_EXPLAINED.md` | yes — **PRIMARY** |
| 10-method-callgraph | `OrderProcessingService.PlaceOrderAsync` + `UpdateOrderTotalsAsync` (the outbound differentiator) | `CALL_GRAPH.md` | yes — **PRIMARY** |
| 11-method-interface | inbound callers of `IProductService.GetProductByIdAsync` (inbound at scale + interface dispatch) | `INBOUND.md` | yes |
| 12-method-overloads | the `UpdateProductWarehouseInventoryAsync` overload pair (`includeOverloads`) | `OVERLOADS.md` | yes |
| 13-method-trap | a leaf-method definition lookup | `LEAF.md` | **no** — histogram-only over-reach control |

Oracles (the tool's own output, eyeballed once) committed at `scripts/ab-test/tasks/{05,10,11,12}-*.reference.md`.

## Decision rule (pre-registered)

- **Primary metrics:** `num_turns`, `tool_call_count` (the documented turn-count win); secondary `total_cost_usd`.
- **Tool-use guard (load-bearing):** a win counts **only** on reps where `analyze_method` appears in that run's `tool_histogram`. Wins in reps that never called the tool measure arm variance — the exact error that confounded the sibling study.
- **Correctness gate:** R mean `inbound_recall` **and** `outbound_recall` ≥ baseline (within ~0.05 judge noise) **and** hallucinations not worse.
- **PROMOTE iff** R vs baseline shows a turn/tool-call reduction **with the tool invoked in those reps** AND passes the correctness gate, on **both primaries (05, 10)** OR ≥3 of the 4 judged tasks. Else HOLD.
- **Trap (13):** materially more `analyze_method` calls in R than N/baseline = over-trigger debit (qualitative).

## Data provenance & the quota cliff

A reproducible **failure hits ~2.5 h into any sustained sweep** and guillotines the tail (clean prefix → instant-fail/hang tail). This is not a one-off (the sibling study's was overnight); it recurred on every sweep here. Root cause is **not definitively established** — it presents as rate-limit-style instant-fails on the sweeps and as the intermittent nested-CLI hang on the judge (the harness spawns the Claude Code CLI nested inside the operator's session), and it coincided with the operator's own "session limit" hits. For this study the cause is immaterial: the affected runs failed cleanly (1 turn, $0, or a tree-killed hang) in both arms symmetrically and are dropped.

| Sweep | dir | clean / total | cliff |
|---|---|---|---|
| Pilot (n=3, 5 tasks) | `20260602T160906Z` | 39 / 45 | last 6 (positions 40–45) |
| Scale primaries (n=8, 05/10) | `20260603T124453Z` | 29 / 48 | positions 30–48 |
| Scale secondaries (n=5, 11/12) | `20260603T191919Z` | 22 / 30 | **hung** at run 23 (SDK deadlock) |

Merged into `results/merged-analyze-method-2026-06-04/` = **90 clean runs** (25 cliff casualties dropped; reps re-sequenced 1..k per cell to avoid collisions). Clean n per cell: 05 (b7/N5/R7), 10 (b6/N10/R7), 11 (b8/N5/R6), 12 (b8/N8/R5), 13 (b3/N3/R2). Cliff casualties are infrastructure failures (quota), not task outcomes, and are excluded — standard practice; both arms lose reps roughly symmetrically (the cliff hits by completion-time across a shuffled plan).

## Result 1 — Adoption (the headline: does routing close the gap?)

`analyze_method` invocations among **clean** reps, from per-run tool histograms:

| Task | N = am-on (bare-add) | R = am-routed | routing effect |
|---|---|---|---|
| 05-explain-service (PRIMARY) | 1/5 | **5/7** | routing fires |
| 10-method-callgraph (PRIMARY) | 4/10 | **7/7** | routing fires (100%) |
| 12-method-overloads | 2/8 | **5/5** | routing fires (100%) |
| 11-method-interface | 0/5 | **0/6** | **routing does NOT fire** |
| 13-method-trap (control) | 0/3 | 0/2 | no over-reach ✓ |

- **Routing closes the gap on the primaries and 12** — R reaches `analyze_method` on 5/7, 7/7, 5/5, versus the bare-add arm's sparse 1/5, 4/10, 2/8. This is the direct contrast the sibling study lacked: there, no snippet row → ~0 adoption; here, one snippet row flips adoption to a majority/all on 3 of 4 judged tasks.
- **Bonus finding:** unlike `analyze_change_impact`, the bare-add arm (N) adopts *somewhat* even without the routing row (4/10 on task 10) — `analyze_method` is more discoverable on a plain default-add. This narrows the N↔R gap but does not change that R-vs-baseline decides.
- **Task 11 is a genuine routing gap, not noise.** On pure inbound enumeration, *neither* arm escalates past `find_references` (R = 0/6). The agent (reasonably) judges that `find_references` answers "just list the callers"; `analyze_method`'s outbound half is wasted there. Forcing 11 via snippet wording would risk the over-reach the trap guards against.

## Result 2 — Turn / tool-call win (guarded)

Per-task means over clean reps; Δ = R vs baseline (negative = fewer turns = better):

| Task | baseline turns (n) | R turns (n) | Δ turns | Δ tools | Δ cost | tool-use guard |
|---|---|---|---|---|---|---|
| 05 (PRIMARY) | 17.3 (7) | 12.1 (7) | **−29.8%** | −31.6% | −19.7% | R AM-used subset → 10.8 turns (**−38%**), 5/7 invoked |
| 10 (PRIMARY) | 16.7 (6) | 7.7 (7) | **−53.7%** | −57.1% | −30.9% | **7/7** invoked → win is fully tool-attributable |
| 11 | 17.6 (8) | 23.5 (6) | +33.3% | +35.3% | +10.7% | 0/6 invoked — routing thrashed without the tool |
| 12 | 12.2 (8) | 13.0 (5) | +6.1% | +6.7% | −3.6% | 5/5 invoked but no turn benefit |
| 13 trap | 4.0 (3) | 4.0 (2) | +0.0% | +0.0% | +5.9% | 0 invoked |

- **On both primaries, R shows a large turn/tool-call reduction with the tool actually invoked** (05: −30%, 5/7 used it, AM-used subset −38%; 10: −54%, **7/7** used it). This is the precise signal the sibling study never produced: there, the apparent wins came from reps that *skipped* the tool. **Here the faster reps are the tool-using reps.** Mechanistically sensible — one `analyze_method` call replaces several `find_references` + read cycles when reconstructing a call graph.
- **The win is confined to the primaries.** On 11 routing didn't adopt and ran +33% (manual thrash on inbound-at-scale); on 12 it adopted fully yet stayed flat (+6%) — the overload-aggregation view didn't save turns over the baseline approach.
- **The PROMOTE rule's turn/tool-call clause is satisfied via the "both primaries" branch** (it fails the "≥3 of 4" branch — only 2 of 4 judged tasks win).
- **No functional risk:** build-pass = **100%** in every cell, all arms.

## Result 3 — Correctness (canonical LLM judge — the decider)

The pre-registered grader is a pinned-model LLM judge: per-report inbound/outbound caller-graph recall + hallucinations vs the Roslyn oracle, **scoring only methods shared by candidate and reference** (so an agent's choice of which methods to document does not by itself tank recall). Running it was a saga — its `claude_agent_sdk` calls spawn the Claude Code CLI **nested inside the running Claude Code session** and hang **intermittently** (at first every call; a `2.1.162` CLI update eased but did not eliminate it). It was completed with a **resumable, process-isolated orchestrator**: each report judged in its own subprocess with an OS timeout, hung calls tree-killed and retried; the big-oracle task-05 calls needed a 200 s timeout to stop false-killing slow-but-valid replies. **28 reports judged — full coverage on both primaries.**

| Task | arm | inbound recall | outbound recall | halluc. callers | gate |
|---|---|---|---|---|---|
| **10** (PRIMARY) | baseline (n=2) | 1.00 | 0.85 | 2.5/rep | |
| | **routed (n=5)** | **1.00** (=) | **0.979 (+0.13)** | 1/rep (≤ base) | **PASS** |
| **05** (PRIMARY) | baseline (n=5) | 0.967 | **0.948** | 0 | |
| | **routed (n=4)** | 0.958 (−0.01) | **0.840 (−0.108)** | 14 total (vs 0) | **FAIL** |

- **Task 10 passes cleanly.** Routed inbound = baseline (1.0), routed outbound *exceeds* baseline (0.979 vs 0.85), routed hallucinated callers ≤ baseline. On the focused 2-method call-graph the tool makes the report *more* complete — the turn-win comes with **no** completeness cost.
- **Task 05 fails the correctness gate.** With the baseline firmed up to n=5, routed outbound recall is **0.840 vs 0.948 — a −0.108 gap, > 2× the 0.05 tolerance.** It is **not an outlier** (per-rep routed outbound 0.80/0.83/0.83/0.90 — every routed rep at or below the *lowest* baseline rep, 0.90) and **not** a method-choice artifact (the judge scores *shared* methods only and still finds the gap). Routed also hallucinates callers on 2/4 reps (7 each) vs baseline's 0 — "hallucinations not worse" also fails. The misses concentrate in the huge `SearchProductsAsync` (66 outbound entries); the mechanism is root-caused immediately below (a `maxResults` default cap, not response truncation or bad data).
- **The independent proxy agrees, and corrects my earlier reading.** A quota-free outbound-collaborator proxy (oracle `[method]`/`[constructor]` names word-boundary-matched in each report, full n) put routed-05 at **0.804 vs baseline 0.902 (−0.098)** — the same ~0.10 gap. I had earlier dismissed that proxy gap as a pure method-choice artifact (the arms overlap 7/8 on which methods they document); the **shared-method canonical judge shows the regression is real on top of that overlap.** On the fair tasks both agree routed ≥ baseline (proxy 10: +0.045, 12: +0.029; judge 10: +0.13).
- **Tasks 11/12** (secondary, partial judge coverage): no baseline-vs-routed correctness concern surfaced; both moot for the verdict since neither has a turn-win.

**Root cause — a default-parameter cap, not bad data or response truncation.** Re-running `analyze_method` on the agent's 8 methods over MCP stdio settles the mechanism:

- The judge's `missed_callees` for routed-05 concentrate almost entirely in **one method, `SearchProductsAsync`** — a ~300-line god-method with a giant LINQ query. *Both* arms miss its hard LINQ-extension calls (`ToPagedListAsync`, a custom `OrderBy`, `GetWorkingLanguageAsync`); routed just misses ~3 more.
- **It is tool-attributable.** Within the routed arm, the rep that *skipped* `analyze_method` and reconstructed manually scored baseline-level; the three that *called* it (one batched call, 8 methods, `maxResults=20`) all scored below every baseline rep:

  | routed-05 rep | used `analyze_method`? | outbound recall |
  |---|---|---|
  | rep1 / rep3 | yes — 1 batched call | 0.83 / 0.83 |
  | rep2 | yes — 1 batched call (`maxResults=20`) | 0.80 |
  | **rep5** | **no — manual** | **0.90 (≈ baseline)** |

- **The cause is `analyze_method`'s default `maxResults=20`** ([NavigationTools.cs:164](../../src/RoslynMcpServer/Tools/NavigationTools.cs#L164)). `SearchProductsAsync` has **66 outbound entries**; the default cap shows the first 20 and appends `(66 total outbound — increase maxResults)`. The tail collaborators (source lines 1135–1138) are **confirmed absent** from the 25.6 KB output the agent received — capped off, *not* response-truncated (`SearchProductsAsync` sits 2nd/mid-output, far from any char cap; the 25 KB-floor response cap would only clip the *last* method, `GetTotalStockQuantityAsync`). The tool signposts the fix; the agent, mid-batched-call, doesn't re-call with a higher `maxResults`. A secondary sliver is genuine condensation (2 shown-but-unreported collaborators).
- **Why task 10 escapes it:** its large method `UpdateOrderTotalsAsync` (62 outbound) keeps its distinct `[method]` collaborators inside the first-20 window (its bulk is `[property]` reads, which don't count as collaborators); `SearchProductsAsync` scatters key collaborators past entry 20.

**Net: the gate PASSES on task 10 but FAILS on task 05** (outbound −0.11, more hallucinations) — but the failure traces to a **fixable default (`maxResults=20`) plus an unheeded hint**, not a tool-capability gap. The full call graph is retrievable at `maxResults≥66`.

## Result 4 — Trap / over-reach (13-method-trap)

**Clean.** `analyze_method` calls on the leaf-definition lookup: baseline 0, N 0, **R 0**. The routing row did **not** drag the agent to the compound tool where `go_to_definition` suffices; all arms ran the identical 4 turns. No over-reach debit.

## Attribution argument

The sibling study's central error was crediting efficiency deltas to a tool that wasn't invoked. This study's guard rules that out on the primaries:

- On **task 10**, **7/7** clean routed reps invoked `analyze_method`, and routed ran **−54% turns**. There is no "the win came from reps that skipped the tool" escape hatch — every winning rep used it.
- On **task 05**, 5/7 routed reps invoked it; the invoked subset is *faster* (−38%) than the cell mean (−30%), i.e. using the tool correlates with *lower* turns within the cell (the opposite of the sibling study's task-04 pattern).
- The bare-add arm (N) lands between baseline and R on the primaries (05: −14%, 10: −38%), tracking its partial adoption — a clean dose-response: more adoption → bigger turn reduction.

The remaining threat to validity was **correctness** — does the turn saving come at the cost of completeness? The judge answered **yes on task 05**: the turn-win there is partly bought by dropped collaborators (−0.11 outbound recall), which is what tips the verdict to HOLD. On task 10 the answer is no (routed is *more* complete) — so this is a task-shape-dependent tradeoff, not a blanket one.

## Verdict — HOLD

Applying the pre-registered rule to the completed data:

- **Turn/tool-call win:** met on both primaries, tool-attributable (10: −54%, 7/7 invoked; 05: −30%, AM-used subset −38%). ✓
- **Correctness gate:** PASS on task 10 (routed ≥ baseline on both axes), **FAIL on task 05** (outbound recall −0.108; hallucinated callers 14 vs 0). ✗
- **"Both primaries (05, 10)" branch:** fails — task 05 fails the correctness gate.
- **"≥3 of 4 judged tasks" branch:** fails — only task 10 has *both* a turn-win and a correctness pass (11 has no adoption/win; 12 adopts but no win).

**→ Neither branch is satisfied → HOLD `analyze_method` out of the `default` preset.**

This is a *different* HOLD from the sibling study. There, the tool was never adopted and produced no tool-attributable win — a discoverability failure. **Here adoption works and the turn-win is real and large.** What kills promotion is the thing the efficiency numbers alone would have hidden, and that the correctness gate + pre-registration existed to catch: on broad batch comprehension (task 05), routing the agent to `analyze_method` makes it **summarize the tool's large batched output and under-report collaborators by ~11%**, with more hallucinated sites. On focused call-graph work (task 10) there is no such cost — routed is *more* complete. A real efficiency win does not justify promoting a tool that degrades completeness on a primary comprehension task.

**No production changes.** `analyze_method` stays in `HeldFromDefaultPendingValidation`; the production snippet is unchanged. The tool remains available (reachable via `all`/`read`/`navigate`/`navigation` or by explicit name) for the focused call-graph use where it clearly helps.

**Improvement path — concrete and cheap (root-caused in Result 3).** The task-05 regression is `analyze_method`'s default `maxResults=20` hiding a god-method's tail collaborators, plus the agent not heeding the tool's own *"(66 total outbound — increase maxResults)"* hint. Three orthogonal fixes, any of which is a clean re-test on this same harness: (1) **raise `analyze_method`'s default `maxResults`** — 20 is too low for god-classes, and this also fixes silent under-reporting in *direct* use, independent of the A/B; (2) make the `(N total outbound — increase maxResults)` footer **actually drive a re-call** (or have the routing snippet instruct "pass a high `maxResults` on large methods"); (3) failing those, a narrower routing row (single-method call-graph only) sidesteps the batched-god-method case. The lever is the default/hint, **not** the tool's core capability — so this is a HOLD-pending-a-targeted-fix, not a rejection.

## Re-test (2026-06-06): `maxResults` 20→100 closed most of the gap — but HOLD stands; the real cure is decoupling outbound

The Result-3 root cause named two orthogonal fixes. Both were carried out; the first was A/B-re-tested against the same harness (task 05 only, 3 arms, n=8 → **21 clean: b7/N6/R8**, full judge coverage b7/N6/R7).

**Fix 1 — raise the default `maxResults` 20→100** (`4803ba3`, [NavigationTools.cs](../../src/RoslynMcpServer/Tools/NavigationTools.cs)). Deployed + verified live (`SearchProductsAsync` lists all 66 in-solution outbound, no cap footer).

Result — **the outbound-recall regression is largely repaired, but not cleanly:**

| metric | original | re-test (maxResults=100) |
|---|---|---|
| routed outbound recall | 0.840 (gap **−0.108**, >2× tol) | **0.888 (gap −0.040, within ±0.05 tol)** |
| routed inbound recall | 0.958 | 0.929 (**+0.024** vs baseline 0.905) |
| turn-win (routed vs baseline) | −30% | **−40%** (routed[AM-used] −42%, 6/8 invoked) |

- **The turn-win is intact and larger** — the bigger output did *not* cost turns (refuting the main risk the re-test was run to check).
- **Outbound recall landed inside the pre-registered ±0.05 band** (−0.040) but is still *directionally* below baseline, and the cause is **mechanistic, not noise.** Splitting routed reps by whether the tool actually showed the full 66 outbound:

  | routed reps | what they did | outbound recall |
  |---|---|---|
  | rep1, rep8, rep3 | **full data** (explicit 200 / default 100) | **0.92 ≈ baseline 0.928** |
  | rep2, rep7 | skipped tool (manual) | 0.90 |
  | rep4, rep6 | **explicit low `maxResults` (50, 20)** | **0.83** |

  **When the tool shows the full outbound list, routed recall equals baseline.** The entire residual gap is the reps where the *agent itself* passed an explicit low `maxResults` (50, 20) — re-clamping the body-bounded outbound list exactly as the old default did. Raising the default cannot stop the agent from overriding it. (Routed rep5, the heaviest self-capper at `maxResults=10`, timed out in the judge across all 8 passes and is excluded; its absence if anything *inflates* the routed mean.)
- The formal gate's **"hallucinations not worse"** clause also trips (routed 13.7 vs baseline 12.6 hallucinated *callers*/rep), but it is discounted: both arms are ~13/rep (the original's clean 0-vs-14 did not replicate — baseline alone swung 0→12.6 between runs), it is a reference artifact (interface-dispatched callers absent from the analyze_method oracle), and on the tool-relevant *callee/outbound* axis it is **0 for both arms**.

**Verdict: HOLD stands.** Recall recovered only to a tolerance-pass riding on a confirmed, reproducible residual defect — not the clean recovery a default-preset promotion warrants.

**Fix 2 — decouple the outbound list from `maxResults`** (`eb5cd1e`). This is the cure the residual points to: outbound is body-bounded (≤66 here), `maxResults` is an inbound concept (callers run to 188+); sharing one knob let an agent truncate collaborators by dialing `maxResults` down for caller volume. `analyze_method` now renders outbound **in full regardless of `maxResults`** (ResponseTruncator is the backstop); `maxResults` caps only the inbound caller list. The `(N total outbound — increase maxResults)` footer and its `OutboundTotalCount` field are removed. Build + full unit suite green (2268). This is a standalone tool-quality fix — it also ends silent under-reporting in *direct* use — so it is committed on its own merits; **promotion remains HOLD pending a re-test of Fix 2**, which should lift the explicit-`maxResults` reps to full-data recall (~0.92) and is expected to produce an outright recovery → a clean PROMOTE. (A Fix-2 re-test no longer needs any "pass a high `maxResults`" routing hint — the lever is gone.)

### Fix-2 (decouple) re-test (2026-06-06): recall recovered cleanly — but the turn-win did not reproduce → HOLD stands

Fix 2 (`eb5cd1e`) was deployed (`/deploy`; installed `roslyn-mcp` confirmed at `eb5cd1e`) and **verified live**: probing `analyze_method` on `SearchProductsAsync` at **`maxResults=20`** renders all **66 in-solution outbound** calls with **no `increase maxResults` footer** (footer + `OutboundTotalCount` gone), response un-truncated. Re-ran **task 05 only, 3 arms × n=8** under the stall-aware watchdog — **24/24 clean (b8/N8/R8), no cliff** (merged → `results/merged-am-decouple-2026-06-06/`). Per the operator's call the canonical LLM judge was **skipped this batch**: the free proxy already shows recall recovered, and the (judge-independent) turn data already pins the gate to HOLD.

**Recall — recovered (the Fix-2 goal achieved).** Quota-free outbound-collaborator proxy:

| run | baseline | routed | gap |
|---|---|---|---|
| Fix-1 (maxResults=100) | 0.888 | 0.820 | −0.068 |
| **Fix-2 (decouple)** | 0.895 | **0.906** | **+0.012** |

Routed per-rep `[0.84, 0.91, 0.84, 0.97, 0.94, 0.91, 0.94]` (n=7; routed rep7 produced no report) — the low cluster (0.80/0.83 in Fix-1) is **gone**, lowest now 0.84 = baseline's own floor. The mechanism confirms cleanly: **all 5 routed AM-users called with default `maxResults`** (Fix-1 had reps at 50/20/10) — with the footer/lever removed, agents stopped dialing `maxResults` down, so the body-bounded outbound list always rendered in full. **Fix 2 did exactly what it was designed to do; the completeness blocker that drove the original HOLD is resolved.**

**Turn-win — did not reproduce.** The robust half of the original verdict washed out on this batch:

| run | baseline turns | routed turns | Δ | routed[AM-used] |
|---|---|---|---|---|
| Original | 17.3 | 12.1 | −30% | −38% |
| Fix-1 | 18.6 | 11.1 | −40% | −42% |
| **Fix-2** | **14.0** | **14.8** | **+5%** | **14.4 (+3%)** |

Per-rep turns: baseline `[11,13,13,14,14,14,15,18]` (mean 14.0 — the **fastest baseline of all three runs**; a ~6-SEM departure from the prior 17.3/18.6, i.e. between-batch population drift, not within-batch sampling); routed `[9,12,14,15,15,16,18,19]` (mean 14.8 — the slowest routed). Adoption held (R 5/8). Among the 5 AM-users only one rep (9 turns) shows the classic one-call speedup; the rest (14–18) are baseline-or-slower, so the AM-used subset (14.4) ≈ baseline (14.0). The cross-batch delta is **−30% / −40% / +5%** — the task-05 turn-win is real but **baseline-variance-fragile**: a fast baseline erases it. The pre-registered `turn_ok` (routed < baseline, tool-use guarded) is **FALSE** this batch.

**Verdict: HOLD stands — but the reason has changed.** The original HOLD was a *completeness* failure (recall −0.11); Fix 1 narrowed it to a tolerance-pass riding on a confirmed self-cap residual; **Fix 2 fixes recall outright** (proxy routed ≥ baseline; self-capping lever removed). What now blocks promotion is the **efficiency** case: the turn-win — load-bearing for a default-preset promotion — did not survive a clean n=8 re-test and is too baseline-sensitive (−30/−40/+5 across three batches) to call reliable. **No production changes**: `analyze_method` stays in `HeldFromDefaultPendingValidation`; the production snippet is unchanged. The `maxResults` bump (`4803ba3`) and the decouple (`eb5cd1e`) are kept as standalone tool-quality fixes — they end silent outbound under-reporting in *direct* use, independent of the A/B. A future promotion bid would need the turn-win to reproduce on a controlled batch (or a decision to promote on the recall-fix + task-10's robust −54% / 7-of-7 win alone, accepting task-05's efficiency as a wash).

## Reproduction

```
# efficiency (free, no model calls) — regenerate the merged aggregate
python scripts/ab-test/run.py analyze --timestamp merged-analyze-method-2026-06-04

# correctness proxy (free, no model calls) — objective outbound-collaborator recall per arm
python <tmp>/ab_recall_proxy.py scripts/ab-test/results/merged-analyze-method-2026-06-04 10-method-callgraph

# canonical correctness (LLM judge) — resumable, hang-proof orchestrator (28/82 judged in-session,
# FULL coverage on both primaries; run from a PLAIN TERMINAL for the remaining secondary reps)
python <tmp>/judge_orchestrate.py scripts/ab-test/results/merged-analyze-method-2026-06-04 8 200
python <tmp>/ab_judge_agg.py    scripts/ab-test/results/merged-analyze-method-2026-06-04  # routed-vs-baseline recall
```

Raw data (gitignored) under `scripts/ab-test/results/`: pilot `20260602T160906Z`, scale-primaries `20260603T124453Z`, scale-secondaries `20260603T191919Z`, merged `merged-analyze-method-2026-06-04`. Oracles: `scripts/ab-test/tasks/{05,10,11,12}-*.reference.md`.

## Caveats

- **Correctness: full canonical-judge coverage on both primaries, partial on secondaries.** 28 reports judged (05 b5/N3/R4, 10 b2/N4/R5, 11 N3/R2). The judge hangs intermittently when run nested inside a Claude Code session (a CLI-version interaction; `2.1.162` eased but didn't eliminate it), so the secondary tasks aren't fully judged — but they don't bear on the verdict (no turn-win). Both **primaries** are fully covered, so the HOLD is firm. The proxy independently corroborates the task-05 regression. For complete 82-report coverage, run the orchestrator from a plain terminal.
- **Task-05 cell n is modest** (baseline 5, routed 4). The −0.108 outbound gap is ~2× tolerance, every routed rep is at/below the lowest baseline rep, and the proxy replicates it at full n — but a larger judged sample would tighten the estimate.
- **Recurring ~2.5 h account quota cliff** cost 25 of 115 runs and shares the operator's session quota. Clean n per cell is uneven (5–10), below the planned uniform n=8 on every cell; directional, not tight.
- **Run-to-run adoption variance:** R adoption on task 05 was 3/3 in the pilot but 2/4 in the scale batch (combined 5/7) — single-rep adoption is not deterministic.
- **Task 11 routing gap** is unresolved by design (chasing it risks trap over-reach); it is reported as a known limitation, not a failure.
- **One symbol/scenario per task.** A different method might shift the secondary-task results.

## Routed re-test (2026-07-19, judged 2026-07-20): PROMOTE — adoption + cost/turn win + recall parity

One-window grid banked before a quota reset: task 05 only, `arm-a-default` (n=4) vs `arm-am-routed`
(n=4), shuffled run order, zero errors, all builds green (`results/20260719T210126Z`, gitignored;
headline numbers reproduced here).

Mechanical (routed vs default): cost **−19.1%** ($0.967 vs $1.195), wall −21.3% (249.5 s vs
316.9 s), turns **−26.9%** (12.2 vs 16.8), tool calls −28.6%, cache read −34.0%. Wilcoxon p=0.375
on cost at n=4 — direction consistent (three of four routed reps ran 8–10 turns vs the default
arm's 12–20) but underpowered alone; merge with this doc's June cells for power.

Adoption held this time: 5 `analyze_method` calls pooled across the routed arm's tool-call
histogram, with `Read` collapsing 23 → 7 — the routing cue did what the June bare-add arm could not.

Judged (opus-4-7 judge, task-05 oracle, one judgment per rep; the judge's parse-retry fix landed
first as `38ed151` and fired once, rescuing a default-arm reply that would previously have
null-scored):

| Arm | n | Inbound recall | Outbound recall | Σ hallucinated callees |
|-----|---|----------------|-----------------|------------------------|
| `arm-a-default` | 4 | 0.958 | 0.958 | 0 |
| `arm-am-routed` | 4 | **1.000** | **0.967** | 0 |

Verdict: the June HOLD's two failure modes — non-adoption, and no turn-win with a correctness pass
— are both cleared by the routed arm on the flagship task. **`analyze_method` was promoted into the
`default` preset on 2026-07-20** (`HeldFromDefaultPendingValidation` emptied), with the routing cue
shipped in the `roz://guides/workflows` routing map that the setup snippet points at. Caveats: one
task, n=4 per arm, single-judge scoring. Scale-correctness is separately pinned by
`NopAnalyzeMethodStressTests` (nopCommerce: god-method, interface dispatch, batch, external-call
promotion).
