> **Historical record.** Written 2026-03-30 against the tool as it existed then. The server was
> named `roslyn-mcp` before the 2026-07 rename to `roz-mcp`, so the tool names, tool counts, and
> preset sizes in the text below are period-correct for that date, not the current tool surface.
> Published July 2026 verbatim except for the redaction of internal file paths. A few links to
> internal working files (`../backlog/`, run-output `results/`, pre-rename source paths) do not
> resolve in this repository and are left as written rather than rewritten. Bugs identified
> here were subsequently fixed. See the evidence index at [README.md](README.md) for how this
> document maps to the project's current claims.

# Orleans Follow-up Evaluation — 2026-03-30

Follow-up to [orleans-evaluation-2026-03-28.md](orleans-evaluation-2026-03-28.md). Re-tested the same Orleans solution (~235 projects, net8.0 + net10.0) after recent fixes.

## Resolved since 2026-03-28

### P0: UnresolvedAnalyzerReference crash — FIXED

`find_implementations`, `find_derived_types`, and `rename_symbol` all work now. Tested `find_implementations(symbolName: "ISiloStatusOracle")` and `find_derived_types` by position — both returned correct results without crashing.

### P0: Multi-TFM duplication — FIXED

All tools now de-duplicate across TFMs:
- `find_symbol("ISiloStatusOracle")` returns 1 result, not 2
- `find_implementations` returns 1 `SiloStatusOracle`, not 2
- `find_callers` returns 4 unique call sites, not 8
- `find_references` shows per-project counts with TFM in the label (e.g. `Orleans.Runtime(net10.0)`) but no duplicate entries

### P1: get_workspace_info too large — IMPROVED

Output is now 51.5KB (down from 80KB). Still exceeds MCP response limits and gets dumped to a temp file, but the reduction is meaningful.

### excludeTests default — FIXED

All tools now default to `includeTests: false` and show a helpful footer: `(excluded 73 test project(s) — pass includeTests=true to include)`. This is exactly right — production code first, opt-in for tests.

### Generated partial class line numbers — IMPROVED

`find_implementations` for `ISiloStatusOracle` now shows both locations for `SiloStatusOracle` (user-authored + generated partial), but lists the user-authored file first. `get_symbols_overview` showed the correct user-authored line. The generated file location is informational rather than misleading now.

---

## New findings

### P2: `find_overloads` output is unnecessarily verbose

For `IGrainFactory.GetGrain` (15 overloads), the output has two sections:
1. A compact parameter-only summary (15 lines)
2. Full declaration details for all 15 overloads (~75 lines)

The summary alone is sufficient in most cases. The full details repeat information already visible in the summary. For a 15-overload method this consumes ~90 lines / ~1500 tokens.

**Suggestion**: Show only the compact summary by default. Add `includeBody: true` or a `verbose` flag for the full declarations. The summary format is already excellent — it just needs to be the default stopping point.

### P2: `get_symbols_overview` ignores maxTypes when using project filter

Called with `project: "Orleans.Core.Abstractions"`, `depth: 0`, `maxTypes: 10`. Got 100+ types across 70+ files — the `maxTypes` limit was not enforced. This produced an enormous response (~4000 tokens for what should have been a quick overview).

**Possible causes**: `maxTypes` may apply per-file rather than globally, or the project filter may bypass the limit. Either way, there's no effective way to get a concise project-level overview for large projects.

**Suggestion**: Enforce `maxTypes` globally (not per-file). When truncated, show a summary: `"Showing 10 of 142 types across 71 files. Use filePaths or kind filter to narrow."` Also consider adding a `maxFiles` equivalent for the project filter path.

### P2: `rename_symbol` partial application on file lock failure

Renamed `IsDeadSilo` -> `IsSiloDead` on `ISiloStatusOracle`. The declaration file was updated, but the operation failed mid-way with:

```
The process cannot access the file '...\RingTests_Standalone.cs' because it is being used by another process.
```

The result was an inconsistent codebase — the interface declaration was renamed but references in the locked file were not. Required `git checkout` to recover.

**Suggestion**: Either make rename atomic (rollback all changes on any failure) or return a structured result showing which files were updated vs which failed, so the consumer knows the exact scope of partial application and can decide whether to revert.

### P3: `find_symbol` wildcard search ranks fields above type declarations

Searching for `*Oracle*` returned fields (`_oracle`, `_siloStatusOracle`) before the `ISiloStatusOracle` interface declaration. When pattern-searching, the type declaration is almost always the primary interest.

**Suggestion**: Sort results by symbol kind priority: types > methods > properties > fields > parameters. This matches how developers think — "what is Oracle?" before "where is Oracle stored?". The `kind` filter works as a workaround, but better default ranking would reduce the need for it.

### P3: `find_derived_types` rejects `containingType` with confusing error

Called `find_derived_types(symbolName: "Grain", containingType: "Grain")` to disambiguate between `Grain` and `Grain<TGrainState>`. Got:

```
No symbol found with name 'Grain' in type 'Grain'. Did you mean: GrainId?
```

The `containingType` parameter semantically means "the type that contains this member" — but `find_derived_types` targets types, not members. Passing `containingType` on a type-targeting tool is a category error, but the error message doesn't explain this. The correct approach was `filePath + line`.

**Suggestion**: Either reject `containingType` on `find_derived_types` and `get_type_hierarchy` with a clear error ("containingType is for members, not types — use filePath+line to disambiguate between types with the same name"), or reinterpret `containingType` on type-targeting tools as "the parent/enclosing type" for nested type disambiguation.

Also: the "Cannot combine symbolName with line/column" error when passing both `symbolName` + `filePath` + `line` was unexpected. `filePath` alone (without line/column) works with `symbolName` as a scoping mechanism. The error triggers only when `line` is added. **Suggestion**: Allow `symbolName` + `filePath` + `line` as "find this named symbol near this line" for disambiguation. Currently the only name-based disambiguation is `containingType`, which doesn't work for top-level types.

### P3: `get_diagnostics` duplicates across TFMs

The initial error-level diagnostics call returned 12 errors — 6 unique diagnostics each appearing twice (once per TFM). All were in `TestFSharpGrainInterfaces` which targets both `net8.0` and `net10.0`.

**Suggestion**: Deduplicate by `(filePath, line, diagnosticId)` and annotate with TFM count: `(2 TFMs)`. Or add a `deduplicateTfms: true` default. The consumer cares about "which lines have errors", not "which compilation instances produced errors".

---

## Documentation feedback

### What's helpful and should stay

- **"Which tool to use" quick-reference** — I consult this on nearly every navigation decision. Saves a round-trip of picking the wrong tool.
- **Special Symbol Names table** (`.ctor`, `op_*`, `this[]`) — Essential. I used `.ctor` in this session and it worked exactly as documented.
- **`containingType` vs `filePath+line+column` guidance** — Clear and accurate for the member-level tools where it applies.

### What should be added

- **`find_overloads` verbosity note**: Document that the output includes both a compact summary AND full details, and that this can be very large for heavily-overloaded methods. Mention `maxResults` as a way to limit.
- **`rename_symbol` is not atomic**: Document that a file lock or other I/O failure can leave the rename partially applied. Recommend `git diff` / `git checkout` as recovery steps.
- **`find_derived_types` / `get_type_hierarchy` and `containingType`**: Document that `containingType` is only meaningful for member-level symbols and will not help disambiguate between top-level types sharing a name. State that `filePath+line` is the correct disambiguation for types.

### What could be trimmed

- **Repeated parameter descriptions**: The `containingType` description is copied verbatim across 10+ tool schemas (~40 tokens each, ~400 tokens total per conversation). Same for `symbolName` (~60 tokens x 10 tools = ~600 tokens). These are loaded into context on every conversation. Consider:
  - A single shared description block referenced by name, or
  - Shorter per-tool descriptions that reference the shared definition: `"See find_symbol docs for symbolName conventions."`
  - This would save ~800-1000 tokens per conversation load.

- **Server instructions block**: The instructions are excellent but repeated between the MCP server's own instructions and the CLAUDE.md Roslyn section (noted in prior evaluation). One authoritative source would reduce context load.

---

## Wishlist (carried forward + new)

- **Type usage by role** (from prior eval): "What methods accept `ISiloStatusOracle` as a parameter?" — filtering references by semantic role (parameter type, return type, field type, base type). Would be invaluable for understanding DI patterns.
- **Project dependency graph** (from prior eval): Which projects reference a given project.
- **Atomic rename with rollback**: On any file write failure, undo all changes made so far.
- **`find_symbol` result ranking by kind**: Types first, then methods, then fields/properties.
