> **Historical record.** Written 2026-03-28 against the tool as it existed then. The server was
> named `roslyn-mcp` before the 2026-07 rename to `roz-mcp`, so the tool names, tool counts, and
> preset sizes in the text below are period-correct for that date, not the current tool surface.
> Published July 2026 verbatim except for the redaction of internal file paths. A few links to
> internal working files (`../backlog/`, run-output `results/`, pre-rename source paths) do not
> resolve in this repository and are left as written rather than rewritten. Bugs identified
> here were subsequently fixed. See the evidence index at [README.md](README.md) for how this
> document maps to the project's current claims.

# Orleans Evaluation — 2026-03-28

Stress-tested the Roslyn MCP server against [Microsoft Orleans](https://github.com/dotnet/orleans) — a large, multi-TFM open-source solution (~160 projects, net8.0 + net10.0).

## Test matrix

| Tool | Name-based | Position-based | Notes |
|------|-----------|---------------|-------|
| `get_workspace_info` | n/a | n/a | Output too large (80K chars, 30K tokens) |
| `find_symbol` | Works (with TFM duplicates) | n/a | Batch search, kind filter, body inclusion all work |
| `get_symbols_overview` | n/a | n/a | Works well; generated partial line numbers confusing |
| `go_to_definition` | n/a | Works | Good output format, member listing useful |
| `find_references` | Ambiguous (68 matches for "SiloAddress") | Works | Distribution summary is excellent |
| `find_callers` | Ambiguous (TFM dupes) | Works (with TFM dupe results) | contextLines output format is great |
| `find_implementations` | Ambiguous (TFM dupes) | **Crashes** | UnresolvedAnalyzerReference |
| `find_derived_types` | Ambiguous (TFM dupes) | **Crashes** | UnresolvedAnalyzerReference |
| `find_overloads` | Works (de-dupes TFMs!) | n/a | Inconsistently handles TFMs better than other tools |
| `get_type_hierarchy` | Ambiguous (TFM dupes) | Works | Clean output |
| `rename_symbol` | **Crashes** | n/a | UnresolvedAnalyzerReference |
| `replace_symbol` | Works | n/a | Correctly preserves doc comments |
| `remove_symbol` | Works | n/a | Correctly removes doc comments + blank lines |
| `insert_after_symbol` | Works | n/a | Clean |
| `insert_before_symbol` | Not tested | n/a | — |
| `replace_content` | Works | n/a | Literal mode, workspace sync confirmed |
| `add_usings` | Not tested | n/a | — |
| `remove_unused_usings` | Not tested | n/a | — |
| `get_diagnostics` | Works | n/a | Incremental mode works well |
| `reload_workspace` | Not tested | n/a | — |
| `reset_baseline` | Not tested | n/a | — |

## Critical findings

### P0: UnresolvedAnalyzerReference crash

`find_derived_types`, `find_implementations`, and `rename_symbol` crash 100% of the time with:

```
Unexpected value 'Microsoft.CodeAnalysis.Diagnostics.UnresolvedAnalyzerReference'
of type 'Microsoft.CodeAnalysis.Diagnostics.UnresolvedAnalyzerReference'
```

Likely an unhandled case in a switch/pattern match during cross-project traversal. These are among the highest-value semantic tools.

**Backlog item**: [unresolved-analyzer-reference-crash.md](../backlog/unresolved-analyzer-reference-crash.md)

### P0: Multi-TFM duplication

Every symbol appears 2x (one per TFM). Effects:
- `find_symbol` returns identical duplicate entries
- Name-based tools fail with "Ambiguous: 2 symbols match" showing the same file:line twice — no way to disambiguate
- Position-based `find_callers` returns duplicate call sites
- `find_overloads` de-duplicates correctly — inconsistent with all other tools

**Backlog item**: [multi-tfm-deduplication.md](../backlog/multi-tfm-deduplication.md)

### P1: get_workspace_info too large

80K chars for this solution. Exceeds MCP response limits, dumped to temp file. Needs a summary mode.

**Backlog item**: [workspace-info-too-large.md](../backlog/workspace-info-too-large.md)

## UX friction

### Position resolution snapping to return types

`find_implementations(filePath, line: 52, column: 5)` on `Task<MembershipTableData> ReadAll()` resolved to `Task` instead of `ReadAll`. Omitting column (line-only) correctly resolves to the declared member. This means providing a column gives *worse* behavior than omitting it.

**Existing backlog item**: [line-level-position-resolution.md](../backlog/line-level-position-resolution.md) — already tracked, confirmed by this evaluation.

### Common names cause ambiguity in find_references

`find_references(symbolName: "SiloAddress")` matched 68 symbols (the class + properties named SiloAddress). No `kind` filter available to narrow to "the type, not properties."

**Backlog item**: [find-references-kind-filter.md](../backlog/find-references-kind-filter.md)

### Generated partial class line numbers

`get_symbols_overview` showed `Silo` at `:6268` (from `LoggerMessage.g.cs`) instead of `:25` (from the user-authored file).

**Backlog item**: [generated-partial-line-numbers.md](../backlog/generated-partial-line-numbers.md)

### excludeTests default

`find_symbol("IPlacement*", kind: Interface)` returned all test interfaces first. Production code is almost always the primary interest.

**Backlog item**: [exclude-tests-default.md](../backlog/exclude-tests-default.md)

## What works well

### find_references distribution summary

```
Distribution:
  Orleans.Runtime(net10.0)    224  (49 files)
  Orleans.Core(net10.0)        87  (26 files)
  ...
Total: 748 across 33 projects
```

Genuinely useful information that grep cannot provide. Shows blast radius at a glance.

### find_callers with contextLines

Shows the calling method signature + surrounding code — gives intent, not just location. Exactly what's needed when understanding how a method is used.

### find_overloads summary format

Compact parameter-only listing at top, full details below. Lets me scan the overload set quickly then drill in.

### find_symbol with includeBody + maxBodyLines

Good control over verbosity. Truncation note with total line count is helpful.

### Edit tools (replace_symbol, remove_symbol, insert_after_symbol, replace_content)

All worked correctly. `remove_symbol` cleaned up doc comments properly. `replace_symbol` preserved them. "Workspace updated" confirmation is the right amount of feedback.

### get_diagnostics with incremental mode

"0 new, 16 resolved, 0 unchanged" after an edit — exactly what's needed to validate a change.

### find_symbol batch search

Passing multiple names in one call with independent kind/containingType filters works well for getting a quick overview of related symbols.

## Wishlist

- **Project dependency graph**: Understanding which projects reference a given project. Low complexity (Roslyn provides this via `Solution.GetProjectDependencyGraph()`). Backlog item: [project-dependency-graph.md](../backlog/project-dependency-graph.md)
- **Effective interfaces**: Flattened view of all interfaces a class satisfies (including inherited). `get_type_hierarchy` only shows direct bases/interfaces.
- **Type usage by role**: "What methods accept `GrainId` as a parameter?" — semantic queries that grep can't do.

## Documentation notes

- Server instructions are well-written. The "Which tool to use" guide and "Special Symbol Names" table are valuable.
- The CLAUDE.md Roslyn section duplicates the server instructions with minor differences — consider having one authoritative source.
- The `find_symbol` `matchMode` interaction with wildcard `*` in names is unclear — searched `IPlacement*` with default Contains mode and got unexpected results (matches for `IActivationCountBasedPlacementTestGrain` which doesn't contain "IPlacement" as a substring). Existing backlog item: [document-wildcard-behavior.md](../backlog/document-wildcard-behavior.md).
