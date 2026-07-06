> **Historical record.** Written 2026-04-18 against the tool as it existed then. The server was
> named `roslyn-mcp` before the 2026-07 rename to `roz-mcp`, so the tool names, tool counts, and
> preset sizes in the text below are period-correct for that date, not the current tool surface.
> Published July 2026 verbatim except for the redaction of internal file paths. A few links to
> internal working files (`../backlog/`, run-output `results/`, pre-rename source paths) do not
> resolve in this repository and are left as written rather than rewritten. Bugs identified
> here were subsequently fixed. See the evidence index at [README.md](README.md) for how this
> document maps to the project's current claims.

# A/B test: does roslyn-mcp help Claude on realistic tasks?

**Date:** 2026-04-18
**Harness:** [scripts/ab-test/](../../scripts/ab-test/)
**Codebase:** nopCommerce @ `c91ad8a` (34 projects, ~300K LOC)
**Model:** `claude-opus-4-7`
**Arms:** `arm-a-with-mcp` (roslyn-mcp + production claude-md-snippet) vs. `arm-b-without-mcp` (baseline)

## TL;DR

- **Audit task (investigation):** MCP clearly wins. Arm A produced the same-quality report with **~40% fewer turns**, **~40% fewer output tokens**, and **~18% lower cost** — despite the MCP server's schema-advertisement overhead. The Roslyn `find_references` tool replaced ~29 Grep calls per Arm B run.
- **Feature-add task (editing):** MCP roughly breaks even on quality but **costs more and takes longer**. Both arms produced compilable, idiomatic nopCommerce code with identical file layout. Arm A was **2.1× slower wall-clock** and **17% more expensive**. MCP was used sparingly (mostly `get_diagnostics` and `find_symbol`), so most of the extra cost was the tool-schema overhead without offsetting savings.
- **Verdict: ship it.** The gain on investigation tasks is large and real; the tax on edit tasks is small. Deployed with the snippet, Claude uses MCP when it pays off and falls back to Grep/Read otherwise.

## Aggregate metrics

Pooled across reps (n=3 for three of the four cells, n=2 for one — see methodology).

| Task | Arm | n | Wall(s) | Turns | Out tokens | Cache rd | Cost ($) | Tool calls |
|---|---|---|---------|-------|-----------|----------|----------|------------|
| feature-add | arm-a-with-mcp | 2 | **367** | 38 | 10,397 | 1,171,276 | **1.19** | 38 |
| feature-add | arm-b-without-mcp | 3 | **171** | 41 | 10,765 | 879,133 | **1.02** | 40 |
| audit | arm-a-with-mcp | 3 | 418 | **35** | **13,651** | 773,491 | **2.08** | **33** |
| audit | arm-b-without-mcp | 3 | 407 | **56** | **24,134** | 1,388,352 | **2.55** | **54** |

Bold = larger delta within a task.

## Task 1 — Feature add (edit-heavy)

**Brief:** Add `OrderFeedback` entity + mapping + service interface/impl + DI registration. Compile must pass.

**Outcome:** Both arms produced exactly this file layout in both reps:
- `src/Libraries/Nop.Core/Domain/Orders/OrderFeedback.cs` (entity)
- `src/Libraries/Nop.Data/Mapping/Builders/Orders/OrderFeedbackBuilder.cs` (fluent mapping)
- `src/Libraries/Nop.Services/Orders/IOrderFeedbackService.cs`
- `src/Libraries/Nop.Services/Orders/OrderFeedbackService.cs`
- One-line edit to `NopStartup.cs`: `services.AddScoped<IOrderFeedbackService, OrderFeedbackService>()`

All 5 runs across both arms built cleanly (`dotnet build` exit 0). The file layout, entity shape (BaseEntity + IDs + Rating + Comment + CreatedOnUtc), and DI lifetime (`Scoped`) matched the nopCommerce convention in every case.

**Minor quality differences** (comparing Arm A rep 2 artifacts vs. Arm B rep 1 artifacts):

- **Service API surface:** Arm A wrote a single parameterised `SearchOrderFeedbacksAsync(orderId, customerId, createdFromUtc, createdToUtc, pageIndex, pageSize, getOnlyTotalCount)` returning `IPagedList<T>` — the pattern used by `ProductReviewService`. Arm B wrote three specific getters (`GetByIdAsync`, `GetByOrderIdAsync`, `GetByCustomerIdAsync`) returning unbuffered results. **Arm A's choice is more idiomatic for nopCommerce**; Arm B's is simpler but less aligned with sibling services.
- **Foreign-key mapping:** Arm A used `.ForeignKey<Customer>()` (default cascade). Arm B used `.ForeignKey<Customer>(onDelete: Rule.None)` (explicit, required `using System.Data;`). Both are defensible.
- **XML comment style:** Near-identical, trivial wording differences ("Order feedback" vs. "The order feedback").

**Tool-use patterns** (per-run histograms):

| Tool | Arm A rep1 | Arm A rep2 | Arm B rep1 | Arm B rep2 |
|------|------------|------------|------------|------------|
| Read | 11 | 7 | 15 | 11 |
| Glob | 11 | 1 | 8 | 12 |
| Bash | 6 | 7 | 8 | 6 |
| Write | 4 | 4 | 4 | 4 |
| Grep | 2 | 3 | 0 | 3 |
| Edit | 1 | 2 | 1 | 1 |
| mcp__roslyn-mcp__find_symbol | 1 | 5 | — | — |
| mcp__roslyn-mcp__get_diagnostics | 2 | 3 | — | — |
| mcp__roslyn-mcp__get_workspace_info | 2 | 2 | — | — |
| mcp__roslyn-mcp__find_references | 1 | — | — | — |

MCP tools were **supplementary**, not central: `find_symbol` located similar entities (`ProductReview`, `NopServicesDefaults`); `get_diagnostics` validated the new files before running `dotnet build`. Most of the work was still Read/Write/Glob.

**Cost analysis:**
- Arm A's wall-clock was 2.1× Arm B's (367s vs 171s). Per-turn time: Arm A ~9.7s, Arm B ~4.2s. The per-turn gap is consistent with MCP tool-schema overhead on every turn plus slower MCP calls vs. native Grep.
- Input cache reads: Arm A 1.17M vs. Arm B 0.88M — ~33% higher. The delta is the MCP schema + snippet in every turn's system prompt. Cache reads are cheap so this is a small cost driver.
- Output tokens were nearly identical (~10.5K). Both arms wrote similar code of similar length.
- Total cost: Arm A $1.19 vs. Arm B $1.02, a 17% premium for slightly more idiomatic code.

## Task 2 — Audit (read-only, investigation)

**Brief:** Produce `AUDIT_REPORT.md` ranking top-5 fan-in services, top-3 most-referenced entities, and top-3 god-classes.

**Outcome:** All 6 runs produced a well-structured report. Both arms identified the same god-class leaders (ImportManager, OrderProcessingService). The rankings differed in defensible ways:

| Rank | Arm A services | Arm A refs | Arm B services | Arm B file-refs |
|------|----------------|-----------|----------------|-----------------|
| 1 | ILocalizationService | 468 (Roslyn) | ILocalizationService | 419 (files) |
| 2 | ICustomerService | 216 | IWorkContext | 196 |
| 3 | IPermissionService | 136 | ICustomerService | 163 |
| 4 | IProductService | 134 | IWebHelper | 135 |
| 5 | IStoreMappingService | 113 | IStoreContext | 120 |

Arm A used Roslyn `find_references` for exact counts and explicitly excluded `IWorkContext`/`IStoreContext` as "ambient context providers". Arm B used word-boundary Grep counts (`file refs`, minus 2 for interface+impl files) and included the context interfaces. Both methods produce a defensible answer; Arm A's numbers are precise, Arm B's are approximate but within the same order of magnitude.

For god classes:
- **Both arms** flagged `ImportManager.cs` (3,585 LOC) and `OrderProcessingService.cs` (3,498 LOC) at #1–2.
- **Arm A** also found `Admin/ProductController.cs` (4,119 LOC) — the actual #1 god-class — by looking across the whole solution.
- **Arm B** stuck to `Nop.Services/` and picked `WorkflowMessageService.cs` (2,944 LOC) as #3.

Arm A's wider scope was a small win; Arm B's narrower service-focused scan is also defensible.

**Tool-use patterns (striking):**

| Tool | Arm A rep1 | Arm A rep2 | Arm A rep3 | Arm B rep1 | Arm B rep2 | Arm B rep3 |
|------|------------|------------|------------|------------|------------|------------|
| mcp__roslyn-mcp__find_references | 15 | 9 | 11 | — | — | — |
| mcp__roslyn-mcp__find_symbol | 0 | 3 | 4 | — | — | — |
| mcp__roslyn-mcp__get_symbols_overview | 0 | 2 | 1 | — | — | — |
| mcp__roslyn-mcp__get_workspace_info | 1 | 1 | 1 | — | — | — |
| Grep | 5 | 6 | 4 | 29 | 29 | 40 |
| Bash | 5 | 5 | 5 | 16 | 9 | 21 |
| Read | 1 | 0 | 1 | 2 | 0 | 1 |
| Glob | 1 | 0 | 1 | 1 | 3 | 3 |
| Write | 1 | 1 | 1 | 1 | 1 | 1 |

**This is the clearest win for MCP in the study.** Arm B substituted for `find_references` by running `grep -c "IFoo"` across the codebase, spawning ~29-40 Grep calls per run. Arm A replaced those with ~11 `find_references` calls — getting exact counts that grep can't (it double-counts type definitions, comments, and string mentions). The reports end up similar, but Arm A's are **evidence-based**, Arm B's are **evidence-approximate**.

**Cost analysis:**
- Wall-clock nearly identical (Arm A 418s, Arm B 407s).
- Output tokens: Arm A 13.7K vs Arm B 24.1K — Arm B wrote ~76% more output because each Grep result had to be read through, interpreted, and summarised inline. Arm A got structured results from Roslyn.
- Turns: Arm A 35 vs Arm B 56 — Arm A converged faster.
- Total cost: Arm A $2.08 vs Arm B $2.55 — **Arm A was 18% cheaper**.

## Verdict

Production setup (MCP registered + claude-md-snippet as project CLAUDE.md):

- **Audit / investigation tasks** — net positive. Fewer turns, lower cost, more defensible evidence in the output. This is where the MCP server justifies itself.
- **Feature-add / editing tasks** — mild negative (slightly slower, slightly more expensive, comparable quality). MCP tools were used as supplementary validators (`get_diagnostics`) rather than primary drivers. The overhead of advertising the schema every turn was not fully offset.
- **Overall** — the positive on read-heavy tasks outweighs the tax on edit-heavy tasks. The alternative ("install MCP only when doing investigation") is impractical. Keep it installed.

## Caveats

- **n is small** (2-3 per cell). Variance across reps was substantial — the Arm B audit runs ranged 305s to 506s, and the Arm A feature-add runs 330s to 404s. The directional signal is consistent but magnitudes are noisy.
- **Two tasks are not the full task space.** This study picked one edit-heavy task and one read-heavy task. A bug-fix task or a cross-project refactor could behave differently.
- **Cache effects vary.** Arm A's system prompt is larger (base + snippet), so its cache_creation cost is higher on cold sessions but amortises across turns. In real multi-turn sessions the amortisation is better than this study captures (each harness run is a fresh session).
- **Arm A's snippet shapes behaviour.** Without the snippet, Arm A ignored the MCP tools in informal testing. The snippet is not free — it's a token tax on every session — but it's what makes the MCP tools actually get used.
- **No ReSharper cleanup in either arm** (by design, to preserve raw model output). Production has `resharper_cleanup` running after edits, which would normalise formatting and hide some stylistic choices.

## Artifacts

All raw data lives under [scripts/ab-test/results/](../../scripts/ab-test/results/):

- `20260418T140806Z/` — main 8-run experiment (2 tasks × 2 arms × 2 reps).
- `20260418T151224Z/` — audit artifact-capture reruns (1 rep per arm; `.artifacts/` dirs have the AUDIT_REPORT.md files).
- `20260418T153002Z/` — Arm B feature-add artifact-capture rerun (`.artifacts/` has the new .cs files).

Each run has:
- `<task>-<arm>-<rep>.json` — usage, cost, tool histogram, build result.
- `<task>-<arm>-<rep>.transcript.json` — tool calls with input previews + assistant/user text.
- `<task>-<arm>-<rep>.diff` — `git diff` of tracked changes only.
- `<task>-<arm>-<rep>.artifacts/` — new (untracked) files created during the run (added to harness after the initial 8-run experiment).

## Harness limitations spotted during analysis

- `diff_loc` computed from `git diff` undercounts because new untracked files don't appear in `git diff`. Post-hoc the `untracked_files` list + artifacts dir gives the right picture, but the single-number metric is misleading on its own.
- `NuGet.config` (clone-local override to suppress JFrog NU1900 warnings) appears in every run's `untracked_files`. It's filtered out when copying artifacts but still shows up in the JSON count.
- Tool call `input_preview` is capped at 300 chars, so full Write contents aren't captured in transcripts — the `.artifacts/` dir is the source of truth for what Claude wrote.
