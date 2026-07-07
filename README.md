# roz-mcp

[![CI](https://github.com/andypgray/roz-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/andypgray/roz-mcp/actions/workflows/ci.yml)

roz is an MCP server that gives C# coding agents the compiler's view of a solution. It finds every real reference to a symbol, including the edges text search misses: overrides, interface dispatch, DI registrations. It renames a symbol across the whole solution in one call, previews the blast radius of a proposed change, and makes edits that verify themselves against the compiler in the same round trip. roz runs as a .NET global tool over stdio; one `roz-mcp setup` command configures Claude Code, Cursor, VS Code Copilot Chat, or Codex CLI.

roz was developed for and dogfooded on large legacy financial codebases, and it is for that sort of work that it shines. It is most probably *not* worth the (inherent to the MCP protocol) token overhead for clean greenfield work; [When it helps (and when it doesn't)](#when-it-helps-and-when-it-doesnt) has the measurements.

## Quickstart

roz requires the .NET 10 SDK.

```bash
dotnet tool install -g Zphil.Roz
cd path/to/your/solution
roz-mcp setup
```

Restart the AI client, then ask it something that needs the compiler:

> *"Find all implementations of `IRepository` in this solution and show me each class's `Save` method."*

`setup` auto-detects which AI clients the project uses (via the `.claude/`, `.cursor/`, `.vscode/`, and `.codex/` marker directories), writes the matching MCP config file(s), and appends a short usage snippet to the corresponding project rules file. If several markers are present, choose explicitly with `--client=claude,cursor`. Setup is idempotent: re-running it updates roz's entries without disturbing sibling MCP servers or user-added environment variables.

| Client | Config file(s) written | Rules file |
|---|---|---|
| Claude Code | `.mcp.json`, `.claude/settings.local.json` | `CLAUDE.md` |
| Cursor | `.cursor/mcp.json` | `AGENTS.md` |
| VS Code Copilot Chat | `.vscode/mcp.json` | `AGENTS.md` |
| Codex CLI | `.codex/config.toml` | `AGENTS.md` |

Two defaults are worth knowing before your first session:

- **Solution discovery** walks up from the working directory; pin a specific `.sln`/`.slnx` with `ROZ_SOLUTION_PATH` in the MCP config's `env` block.
- **Tool surface**: the default set is 11 of the 19 tools; most write tools are opt-in. Seed a different surface at onboarding with `roz-mcp setup --tools=all` (everything) or `--tools=read` (read-only), and see [Configuration](#configuration) for finer control.

### From source

```bash
git clone https://github.com/andypgray/roz-mcp
cd roz-mcp
dotnet pack src/Zphil.Roz/Zphil.Roz.csproj -c Release
dotnet tool install -g --add-source src/Zphil.Roz/bin/Release Zphil.Roz
```

roz installs as `roz-mcp` on your `PATH`.

## Tools

roz ships 19 tools across 7 categories. Read tools are forgiving: a cursor near a declaration snaps to the nearest enclosing symbol, and the worst case is information about the wrong symbol. Write tools are conservative: symbol names are authoritative, and an ambiguous target fails with a disambiguation error.

Read tools accept `includeDocs=true` and (where applicable) `includeBody=true`; locations are either cursors (`path:line:col`, or `path:line` for member-on-line snap) or symbol names (with optional FQN, `containingType`, `kind`, or `project` substring filter).

| Tool | Category | Description |
|---|---|---|
| `find_symbol` | navigation | Find a symbol by name, FQN, or arity-disambiguated name (e.g. `Processor<,>`). |
| `get_symbols_overview` | navigation | List a type's members. |
| `go_to_definition` | navigation | Resolve a cursor to its definition. |
| `find_overloads` | navigation | List all overloads of a method (batchable by name or location). |
| `analyze_method` | navigation | Method signature plus inbound callers and outbound in-solution callees grouped by target (held out of the default set). |
| `find_references` | references | Find usages with `referenceKinds=all\|invocations\|reads\|writes`, plus `contextLines`, base-call filtering, and a DI-registration fallback for constructors. |
| `find_implementations` | references | Interface/abstract members → overrides; classes/interfaces → derived/implementing types. |
| `analyze_change_impact` | references | Blast radius of a proposed change (`TypeChange`/`RemoveSymbol`/`AccessibilityNarrow`/`SignatureChange`); each site tagged compatible/requires-update/unsafe; `newSignature` upgrades `SignatureChange` to per-argument classification via overload resolution. |
| `get_type_hierarchy` | types | Base types, derived types, implemented interfaces (batchable). |
| `get_diagnostics` | diagnostics | Compiler + analyzer diagnostics with code-fix hints. `incremental=true` surfaces only new issues vs a captured baseline. |
| `get_workspace_info` | workspace | Solution and project metadata; `reload=true` refreshes from disk. |
| `get_unused_references` | workspace | Detect unused `ProjectReference` (confident) or `PackageReference` (weak signal). |
| `rename_symbol` | editing | Solution-wide semantic rename that keeps overrides and references coherent. |
| `edit_symbol` | editing | Batched insert/replace/remove of members; targets resolved by location or name. |
| `replace_content` | editing | Batched literal or regex text replacement; multi-line literal mode supported. |
| `apply_code_fix` | editing | Apply a registered analyzer-pack code fix (FixAll) for one diagnostic ID across a scope (held out of the default set). |
| `change_signature` | editing | Add-optional / remove-unused / reorder a method's parameters across its declaration family and all call sites; analysis-gated (held out of the default set). |
| `add_usings` | usings | Add `using` directives, sorted and deduplicated. |
| `remove_unused_usings` | usings | Remove unused `using` directives. |

The default set is 11 tools. The write tools (`edit_symbol`, `replace_content`, `apply_code_fix`, `change_signature`, `add_usings`, `remove_unused_usings`) and `get_unused_references` are opt-in via `ROZ_TOOLS`, and `analyze_method` is held out pending A/B validation (see [Configuration](#configuration)).

The five `editing` tools accept a `verify` mode that collapses the edit → build → re-read loop into one round trip: `verify=DryRun` applies the batch to an in-memory fork and reports the new-and-resolved compiler-error delta without writing anything, and `verify=Delta` commits first, then reports. Verified writes exist because an agent's own success report is not evidence: silently asserting completion while the state says otherwise is a pattern reported across the agent literature ([Advani 2026](https://arxiv.org/abs/2606.09863), agentic benchmarks rather than code audits specifically).

Batched read tools (`find_references`, `analyze_change_impact`, `find_implementations`, `find_overloads`, `get_type_hierarchy`) accept many items per call; one round trip with `symbolNames=["A","B","C"]` is almost always cheaper than three separate calls.

## Prompts

roz registers 10 user-invoked slash commands (`/mcp__roz__*`). Each packages a multi-step Roslyn workflow into one recipe.

| Prompt | Purpose |
|---|---|
| `cleanup_dead_code` | Find and remove dead code, checking the DI / dispatch / markup blind spots and confirming before touching public API. |
| `tighten_accessibility` | Narrow over-broad accessibility (e.g. a public member only used internally) where it is provably safe. |
| `assess_impact` | Preview the blast radius of a proposed change: every affected site tagged compatible, needs-update, or unsafe. Report-first. |
| `check_breaking_changes` | Report what your branch would break for consumers of your public API versus a baseline git ref: source, binary, behavioral. Read-only. |
| `fix_diagnostics` | Drive compiler and analyzer diagnostics to clean with minimal root-cause fixes; suppression is a last resort. |
| `decompile_symbol` | Explain an external (BCL/NuGet) symbol from its source or a decompiled body. Read-only. |
| `trim_dependencies` | Find and remove unused project/package references, confirming each and verifying the build still passes. |
| `assess_upgrade` | Gauge how risky a NuGet upgrade is: how exposed your code is and which call sites the breaking changes would hit. |
| `triage_coverage` | Run coverage and triage each gap: dead code, a genuine missing test, or a likely false alarm. |
| `triage_complexity` | Rank the worst complexity hotspots in a scope, then route each to the right fix. |

## When it helps (and when it doesn't)

roz was A/B-measured against no-MCP baselines across four model tiers before 1.0; the write-ups are linked from each paragraph below and indexed in [Evidence and methodology](#evidence-and-methodology).

Mechanical refactors are the clearest win, at every tier measured. A 22-reference solution-wide rename via `rename_symbol` cost −57.5% versus a careful text-editing baseline (Haiku 4.5, 2026-07-03; the tool adopted in 3 of 3 reps, zero churn, zero residual) and −60.9% at the earlier frontier anchor (Opus 4.7, 2026-04). Both arms landed the identical semantic change. [Write-up.](https://github.com/andypgray/roz-mcp/blob/main/docs/evidence/ab-test-down-tier-2026-07-02.md)

roz preserves live code that text search would delete. A deletion task planted five traps reachable only through Razor markup, DI, dispatch, startup, or the public API, alongside three genuinely dead symbols. The roz arm preserved all five traps at every tier tested (Opus 4.8 / Sonnet 5 / Haiku 4.5, 2026-07); only the no-MCP baseline deleted live code (Sonnet 5, the Razor-only and public-API traps, 1 of 3 reps). The counterweight: the roz arm over-corrected once at Sonnet and removed nothing that rep. The `cleanup_dead_code` and `tighten_accessibility` prompts drive this workflow with the blind-spot checks built in. [Write-up.](https://github.com/andypgray/roz-mcp/blob/main/docs/evidence/ab-test-down-tier-2026-07-02.md)

Audit reports cite real numbers and finish faster, but they cost more. roz-armed audits scored count plausibility 0.97–1.00 versus 0.62–0.77 for a grep baseline whose cited caller counts drift or are fabricated. They ran −24.2% to −57.3% in wall-clock (Sonnet 5 / Haiku 4.5, 2026-07). They also cost +19.7% to +47.6% *more* on every current tier: MCP tool results are billed as input tokens ([tool-use pricing](https://platform.claude.com/docs/en/docs/tool-use-pricing-and-tokens)), while a locally-run grep never enters the context window. At the budget tier the speed came with shallower coverage. Buy this for trustworthy-and-fast, not for cheap. [Write-up.](https://github.com/andypgray/roz-mcp/blob/main/docs/evidence/ab-test-down-tier-2026-07-02.md)

Greenfield, pattern-following feature work does not pay. At the frontier it cost +17% and 2.1× the wall-clock for comparable code (Opus 4.7, 2026-04-18). Down-tier, neither Sonnet 5 nor Haiku 4.5 called a single roz tool on the task, so their +5.2% / +23.6% deltas were pure snippet and tool-schema overhead. Don't load roz for this. [Write-up.](https://github.com/andypgray/roz-mcp/blob/main/docs/evidence/ab-test-mcp-vs-no-mcp-2026-04-18.md)

Pick the tool surface for the work (`ROZ_TOOLS`):

| Your task | `ROZ_TOOLS` | Also use |
|---|---|---|
| Rename / mechanical refactor | `default` | `rename_symbol`; try `verify=DryRun` first |
| Dead-code removal / API tightening | `all` (or `default,editing`) | `cleanup_dead_code`, `tighten_accessibility` prompts |
| Audit / understand a codebase | `default` | at the budget tier, add depth with `default,analyze_method` |
| Greenfield / pattern-following feature work | *don't load roz* (measured cost, no benefit) | or `read` at most |
| Diagnostics cleanup loop | `edit` | `fix_diagnostics` prompt |
| API-change planning | `default` | `assess_impact`, `check_breaking_changes` prompts |
| Dependency trim | `default,get_unused_references` | `trim_dependencies` prompt |

Tier matters too (Opus 4.8 / Sonnet 5 / Haiku 4.5, 2026-07). A frontier model trusts grep for navigation and mostly ignores routing guidance, so the read side saves nothing; the value sits in the write path and the prompts. The mid tier is strong enough to act boldly and not always right; the one baseline deletion of live code above happened at this tier. The budget tier gets the best price per outcome on renames, and its fast audits run shallow.

| Tier | How to load roz |
|---|---|
| **Frontier** (Opus 4.8-class) | write path (`rename_symbol`, verified edits) + prompts; no read-side savings |
| **Mid** (Sonnet 5-class) | invoke executors explicitly; watch the over-correction failure mode |
| **Budget** (Haiku 4.5-class) | `default` + depth-forcing prompts; best rename $/outcome (−57.5%) |

## Configuration

All configuration is via environment variables, typically set in the MCP client's config `env` block.

| Variable | Purpose |
|---|---|
| `ROZ_SOLUTION_PATH` | Explicit `.sln`/`.slnx` path. Otherwise discovered from the working directory. |
| `ROZ_TOOLS` | Comma- or semicolon-separated list of presets (`all`/`default`/`read`/`navigate`/`edit`), category keys, or tool names. Items prefixed with `-` exclude. Example: `read,rename_symbol` or `all,-edit_symbol`. |
| `ROZ_DISABLE_ANALYZERS` | Set to `true` to skip analyzer execution; `get_diagnostics` returns compiler-only output. |
| `ROZ_IDLE_TIMEOUT_MINUTES` | Minutes of no tool activity before roz self-exits (avoids orphaned processes). Default `30`; `0` disables. |
| `ROZ_VS_INSTALL_PATH` | Override MSBuild auto-selection. Useful if your only Visual Studio install is a preview build that breaks legacy projects. |
| `ROZ_TEST_PATHS` | Semicolon-separated path prefixes that classify projects as tests (for `includeTests` filtering). |
| `ROZ_TEST_NAMESPACES` | Semicolon-separated namespace prefixes that classify projects as tests. |

`roz-mcp setup --tools=<value>` seeds `ROZ_TOOLS` into the generated MCP config so subsequent sessions pick it up automatically.

## Works well with

roz is a C# specialist and pairs with, rather than replaces, adjacent tools:

- [Serena](https://github.com/oraios/serena): when your work spans multiple languages or a polyglot repo, a cross-language LSP server covers ground a Roslyn-only tool cannot.
- `ilspycmd`: reads the body of a compiled BCL/NuGet symbol; the `decompile_symbol` prompt drives it.
- [ReSharper CLI](https://www.jetbrains.com/help/resharper/ReSharper_Command_Line_Tools.html) / `dotnet format`: whole-file style and formatting passes, which roz leaves alone.

## Evidence and methodology

Every quantitative claim on this page resolves to a primary-source evaluation in [`docs/evidence/`](https://github.com/andypgray/roz-mcp/blob/main/docs/evidence/README.md): the A/B studies that drove ship/hold decisions and the stress tests that shaped the design, published verbatim as dated historical records.

The harness that produced the A/B numbers is public: [`scripts/ab-test/`](https://github.com/andypgray/roz-mcp/tree/main/scripts/ab-test). Run it on your own codebase before you trust ours.

### Stress tests

roz was stress-tested against seven open-source C# codebases before 1.0.0, and each surfaced a distinct bug class that was then fixed. The reports keep roz's former name (`roslyn-mcp`) and period-correct tool counts, and some clones were deliberately broken to stress error handling.

| Codebase | Scale | Distinct bug class surfaced (since fixed) | Evidence |
|---|---|---|---|
| nopCommerce | 37 proj, 3,489 files | No-op-rename false positive ("Changed 394 files" while `git diff` is empty) + `remove_symbol` eating adjacent blank lines | [doc](https://github.com/andypgray/roz-mcp/blob/main/docs/evidence/nopcommerce-evaluation-2026-03-22.md) |
| ILSpy | 13 proj, 1,265 files, WPF | `find_overloads` returned no result for every special symbol name (`.ctor`, `.cctor`, operators) | [doc](https://github.com/andypgray/roz-mcp/blob/main/docs/evidence/ilspy-stress-test-2026-03-25.md) |
| Orleans | ~160–235 proj, multi-TFM | `UnresolvedAnalyzerReference` crash (100% on cross-project traversal) + every symbol duplicated per TFM; non-atomic rename on a file-lock ([followup](https://github.com/andypgray/roz-mcp/blob/main/docs/evidence/orleans-followup-2026-03-30.md)) | [doc](https://github.com/andypgray/roz-mcp/blob/main/docs/evidence/orleans-evaluation-2026-03-28.md) |
| ImageSharp | 1 proj, 1,271 files (broken clone, 7,379 errors) | `find_symbol` invisible for types with compile errors while syntax-based tools saw them (a semantic-vs-syntax consistency gap) | [doc](https://github.com/andypgray/roz-mcp/blob/main/docs/evidence/imagesharp-stress-test-2026-04-04.md) |
| Spectre.Console | 8 proj, 29 TFM variants, 503 files | Test-project reference count inflated 12–15× under multi-TFM (counted per variant pair, not deduplicated) | [doc](https://github.com/andypgray/roz-mcp/blob/main/docs/evidence/roslyn-stress-test-spectre-console.md) |
| Wolverine | 138 proj, 8,155 docs | `remove_unused_usings` deleted *required* usings when type resolution was incomplete (silent data loss) | [doc](https://github.com/andypgray/roz-mcp/blob/main/docs/evidence/wolverine-stress-test-2026-04-06.md) |
| MathNet.Numerics | 10 proj, 2,794 docs | Position-over-name rename renamed `Complex32` across 139 files (3,913 refs); the origin of the write-strict design | [doc](https://github.com/andypgray/roz-mcp/blob/main/docs/evidence/mathnet-stress-test-2026-04-06.md) |

## Contributing

Contributions are welcome. Bug reports reproduced on public codebases, new conservative executors, client-compatibility fixes, and evaluation or harness improvements land best. See [CONTRIBUTING.md](https://github.com/andypgray/roz-mcp/blob/main/CONTRIBUTING.md) for dev setup (.NET 10 SDK, Windows or Linux) and the test architecture. Default-preset changes want an A/B run, and HOLD verdicts are normal here (two of our own tools have them). The deep dive on internals (error-handling conventions, location/FQN resolution, DI-container detection, verified writes, and precision limits) is in [ARCHITECTURE.md](https://github.com/andypgray/roz-mcp/blob/main/ARCHITECTURE.md).

## License

MIT; see [LICENSE](https://github.com/andypgray/roz-mcp/blob/main/LICENSE).
