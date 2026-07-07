# Evidence & methodology

Every quantitative claim in the project's README traces to one of the documents collected here.
These are the primary-source evaluations behind the numbers: the A/B studies that drove ship/hold
decisions and the stress tests that shaped the design. They are published verbatim as historical
records. Each opens with a dated note saying it was written against the tool as it existed at the
time, under its former name `roslyn-mcp`, so era-specific tool names, tool counts, and preset sizes
are period-correct. Bugs identified in these documents were subsequently
fixed; the visible before/after is deliberate.

## The documents

| Doc | Date | Model(s) | Codebase | What it established | Headline numbers |
|---|---|---|---|---|---|
| [nopcommerce-evaluation-2026-03-22.md](nopcommerce-evaluation-2026-03-22.md) | 2026-03-22 | Opus 4.6 | nopCommerce (37 proj, 3,489 files, C# 13/.NET 9) | First full-tool eval; extreme-scale validation; surfaced the no-op-rename false positive and `remove_symbol` blank-line eating | Rating 9/10; 4,478 refs, 150-type hierarchy, 394-file rename; 0 crashes |
| [ilspy-stress-test-2026-03-25.md](ilspy-stress-test-2026-03-25.md) | 2026-03-25 | Opus 4.6 | ILSpy (13 proj, 1,265 files, C# 14/.NET 10, WPF) | Special-symbol / WPF-pattern stress; surfaced `find_overloads` 0-result on all special names [HIGH] | ~60 tool calls |
| [orleans-evaluation-2026-03-28.md](orleans-evaluation-2026-03-28.md) | 2026-03-28 | Claude (model unstated) | Orleans (~160 proj, net8.0+net10.0) | Large multi-project / multi-TFM stress; surfaced the `UnresolvedAnalyzerReference` crash and multi-TFM duplication [P0] | 3 tools crash 100%; every symbol duplicated ×2; 748 refs across 33 proj |
| [orleans-followup-2026-03-30.md](orleans-followup-2026-03-30.md) | 2026-03-30 | Claude (model unstated) | Orleans (~235 proj, net8.0+net10.0) | Re-test confirming the P0 fixes; surfaced non-atomic rename on a file-lock | Both P0s FIXED; `get_workspace_info` 80KB → 51.5KB |
| [imagesharp-stress-test-2026-04-04.md](imagesharp-stress-test-2026-04-04.md) | 2026-04-04 | Claude (model unstated) | ImageSharp (1 proj, 1,271 files, C# 12/net8.0; broken clone, 7,379 errors) | Operator / self-referential-generic / unsafe stress on an errored workspace; surfaced the semantic-vs-syntax `find_symbol` gap | 586 refs for `Image<>`; `find_symbol` 25 vs `find_implementations` 29 |
| [roslyn-stress-test-spectre-console.md](roslyn-stress-test-spectre-console.md) | 2026-04-05 | Claude (model unstated) | Spectre.Console (8 proj, 29 TFM variants, 503 files, C# 14, 4-TFM) | Multi-TFM record / fluent-API stress; surfaced 12–15× test-project reference inflation [HIGH] | ~80 calls, MD5 round-trip verified; +864 reported vs 56 actual |
| [wolverine-stress-test-2026-04-06.md](wolverine-stress-test-2026-04-06.md) | 2026-04-06 | Claude (model unstated) | Wolverine (138 proj, 8,155 docs, net8/9/10) | Cross-project / handler-dispatch / DI-at-scale stress; surfaced `remove_unused_usings` silent data loss | 72 calls, 59 pass / 13 issue, 4 bugs; 126K+ baseline diagnostics |
| [mathnet-stress-test-2026-04-06.md](mathnet-stress-test-2026-04-06.md) | 2026-04-06 | Claude (model unstated) | MathNet.Numerics (10 proj, 33 TFM-expanded, 2,794 docs, net6/8/netstandard2.0) | Operator / generic / multi-TFM stress (the write-strict origin); surfaced the CRITICAL position-over-name rename bug | ~62 calls (18/19 tools), 5 bugs (1 CRITICAL); 139-file / 3,913-ref rename |
| [ab-test-mcp-vs-no-mcp-2026-04-18.md](ab-test-mcp-vs-no-mcp-2026-04-18.md) | 2026-04-18 | Opus 4.7 | nopCommerce @ c91ad8a (34 proj, ~300K LOC) | First MCP-vs-baseline A/B; the original ship decision | Audit −18% cost / ~40% fewer turns / ~29 greps → ~11 `find_references`; feature-add +17% cost / 2.1× wall |
| [ab-test-analyze-change-impact-2026-06-01.md](ab-test-analyze-change-impact-2026-06-01.md) | 2026-06-01/02 | Opus 4.7 | nopCommerce @ c91ad8a (~35 proj) | Promotion A/B → **HOLD** (a discoverability failure) | Adopted on 1 of 4 applicable tasks; 0 of 3 primary tasks meet the decision rule |
| [ab-test-analyze-method-2026-06-04.md](ab-test-analyze-method-2026-06-04.md) | 2026-06-04 (→06-06) | Opus 4.7 (+ pinned judge) | nopCommerce @ c91ad8a (~35 proj) | Promotion A/B → **HOLD** (recall, then efficiency) | Task-10 −54% turns (7/7 used the tool); task-05 recall 0.84 vs 0.95 → fixed; turn-win −30 / −40 / +5 (washed out) |
| [ab-test-prompts-2026-06-16.md](ab-test-prompts-2026-06-16.md) | 2026-06-16/17 | Model unstated (arm `ROSLYN_TOOLS=all`) | nopCommerce | Prompt efficacy (SHIP / FIX-the-recipe, not cost) | 6/6 SHIP (2 after a fix-and-rerun); `assess_impact` recall 0.879 / verdict 1.0; `check_breaking_changes` recall 1.0; `cleanup_dead_code` 0.67 → 1.0 |
| [ab-test-down-tier-2026-07-02.md](ab-test-down-tier-2026-07-02.md) | 2026-07-02/03 | Opus 4.7 & 4.8, Sonnet 5, Haiku 4.5 (judge Fable 5) | nopCommerce (hard-reset per run) | Down-tier value-split; retires the navigation-cost pitch | Haiku rename −57.5% (3/3); audit cost +19.7…+47.6%, wall −24.2…−57.3%; traps 5.0 vs 4.3 (Sonnet baseline) |

## Reading order for skeptics

To pressure-test the claims, read in this order (newest and most
adversarial first, foundational stress work last):

1. [ab-test-down-tier-2026-07-02.md](ab-test-down-tier-2026-07-02.md): the current value model.
2. [ab-test-mcp-vs-no-mcp-2026-04-18.md](ab-test-mcp-vs-no-mcp-2026-04-18.md): the original April A/B, whose
   audit win the down-tier study shows did *not* survive to the next model generation.
3. The two **HOLD** docs, tools of ours that failed their own promotion bar:
   [ab-test-analyze-change-impact-2026-06-01.md](ab-test-analyze-change-impact-2026-06-01.md) and
   [ab-test-analyze-method-2026-06-04.md](ab-test-analyze-method-2026-06-04.md).
4. [ab-test-prompts-2026-06-16.md](ab-test-prompts-2026-06-16.md): prompt efficacy.
5. The eight stress tests, in date order:
   [nopcommerce](nopcommerce-evaluation-2026-03-22.md) ·
   [ilspy](ilspy-stress-test-2026-03-25.md) ·
   [orleans](orleans-evaluation-2026-03-28.md) ·
   [orleans followup](orleans-followup-2026-03-30.md) ·
   [imagesharp](imagesharp-stress-test-2026-04-04.md) ·
   [spectre.console](roslyn-stress-test-spectre-console.md) ·
   [wolverine](wolverine-stress-test-2026-04-06.md) ·
   [mathnet](mathnet-stress-test-2026-04-06.md).

## Methods and limitations

Read the numbers with these constraints in mind; they are stated here once and repeated in the individual
docs:

- n = 3 per cell on the down-tier grid (and n = 5–10 per cell on the HOLD studies). This is too small for
  a significance test at 0.05; the docs report sign-consistency (e.g. "cheaper in 3/3 rep pairs"), not
  p-values.
- Within-model deltas only. Every A vs B figure compares the two arms *of the same model on the same
  task*; a Haiku delta and an Opus delta are not on the same scale.
- LLM-judged rubrics back the quality scores, with the judge model named in each document (the
  down-tier grid pins Fable 5 as the judge against a hand-checked Roslyn oracle). Judge composites are read
  within-tier only.
- Raw `results/` run directories are not published. The transcripts carry machine paths and quota
  messages, and the dated write-ups here are the canonical record. What *is* public is the harness that
  produced them (see [`../../scripts/ab-test/`](../../scripts/ab-test/)), so you can reproduce the shape of
  these experiments on your own codebase.
- Usage-limit incidents during runs are disclosed inside the affected docs (several long grids hit a
  rolling quota cliff and lost individual runs).

## External context & further reading

- **SWE-Master** (Song et al., [arXiv:2602.03411](https://arxiv.org/abs/2602.03411), §5.3): the
  closest controlled analog to our premise: same model, same scaffold, an LSP semantic-navigation
  tool toggled against a grep/find baseline on SWE-bench Verified. Accuracy up (+1.4–2.0 pts),
  turns down, on two open frontier models. Python + pyright; preprint; small deltas — direction,
  not magnitude.
- **LocAgent** (Chen et al., [arXiv:2503.09089](https://arxiv.org/abs/2503.09089)): parsing a
  codebase into a dependency graph (imports, invocations, inheritance) lets an agent localize code
  by multi-hop reasoning, up to 92.7% file-level accuracy. Retrieval only, Python only, but it is
  the same "follow the edges text search can't see" idea this server is built on.
- **Beyond Resolution Rates** (Mehtiyev & Assunção,
  [arXiv:2604.02547](https://arxiv.org/abs/2604.02547)): "Framework prompts do influence agent
  tactics, but this influence diminishes with stronger LLMs." Independent corroboration of what our
  down-tier grid found about routing snippets. Counterpoint:
  [arXiv:2512.10398](https://arxiv.org/abs/2512.10398) reports a strong scaffold overturning a
  model-capability gap at the frontier tier (scaffold *quality* still matters even when scaffold
  *instructions* are ignored); see also [arXiv:2605.05716](https://arxiv.org/abs/2605.05716) on the
  over-equipping penalty shrinking as capability rises (QA/math domain).
- **CrossCodeEval** (Ding et al., [arXiv:2310.11248](https://arxiv.org/abs/2310.11248)): cross-file
  completion benchmark (includes C#): models fail without cross-file context and improve clearly
  with it. **RepoBench** ([arXiv:2306.03091](https://arxiv.org/abs/2306.03091)) and **RepoCoder**
  ([EMNLP 2023](https://aclanthology.org/2023.emnlp-main.151/)) show the same for repo-level
  retrieval. Two caveats: RepoCoder's own retriever is lexical, and RepoBench found
  even *randomly selected* cross-file context helps, which caps how much smart retrieval alone can
  claim.
- **SWE-agent** (Yang et al., [arXiv:2405.15793](https://arxiv.org/abs/2405.15793)): the
  agent-computer-interface paper: a purpose-built tool interface improves an agent's
  editing, navigation, and testing versus using the machine as-is. Interface-vs-no-interface, not
  semantic-vs-grep.
- **False success** (Advani, [arXiv:2606.09863](https://arxiv.org/abs/2606.09863)): "LLM agents can
  fail silently by asserting task completion when the environment state shows otherwise." The
  external analog of the report-fabrication drift our audit rubric measures, and part of why the
  write path here verifies against the compiler instead of trusting the narrative.
- **Anthropic documentation**: the token economics behind the README's audit-cost-premium finding.
  [tool-use pricing](https://platform.claude.com/docs/en/docs/tool-use-pricing-and-tokens) bills tool
  schemas, `tool_use`, and `tool_result` blocks all as input tokens.
  [prompt caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching) prices cache
  reads at 0.1× base input and writes at 1.25–2×; tool definitions and results are cacheable.
  [code execution with MCP](https://www.anthropic.com/engineering/code-execution-with-mcp) states
  "Every intermediate result must pass through the model": the structural reason a locally-executed
  grep is context-free while an MCP result is context-priced.
