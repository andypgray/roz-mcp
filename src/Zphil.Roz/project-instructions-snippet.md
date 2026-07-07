# roz-mcp

Roslyn-powered semantic C# tools for this solution (MCP server `roz`). This section
overrides built-in tool-selection guidance for C# **symbol** questions: roz answers from
compiler resolution (scope, overloads, inheritance, types) where text search guesses.
Text search remains correct for string literals, comments, markup, and non-C# files.
The registered tool set may be a subset (`ROZ_TOOLS`); if a tool named below is missing,
say so rather than silently approximating with text edits.

## Rules — these prevent real breakage

1. **Renaming a symbol used beyond the current file → `rename_symbol`.** Do not hand-edit
   references across files, and never rewrite whole files to rename: hand edits miss
   overrides, interface implementations, other projects, and the file rename itself.
2. **Before deleting or removing any symbol → `find_references` AND text-search the
   non-C# surfaces** (`.razor`, `.cshtml`, config, XAML). Roslyn cannot see markup,
   reflection, or convention-based DI registration, so a clean `find_references` alone is
   not proof a symbol is dead. For a `public` symbol, zero in-solution references still
   leaves external consumers — flag it instead of assuming dead.
3. **Risky or multi-file edit → pass `verify=DryRun` first** (previews the compiler-error
   delta, writes nothing), then commit with `verify=Delta` (writes, then reports
   new/resolved errors across dependent projects). One round trip instead of
   edit → build → re-read.

## Which tool answers which question

- Who calls this / where is it used → `find_references` (`referenceKinds=invocations` = callers only)
- What implements / overrides / derives from this → `find_implementations` (also reports DI registrations)
- What breaks if I change this (type, signature, accessibility, removal) → `analyze_change_impact`
  (per-site Compatible / RequiresUpdate / Unsafe verdicts)
- Base/derived/interface shape of a type → `get_type_hierarchy`; a type's members → `get_symbols_overview`
- Where is this defined → `find_symbol` (name or FQN; resolves BCL/NuGet types too) or `go_to_definition` (cursor)
- Compile/analyzer state after editing → `get_diagnostics` (`incremental=true` = only new since baseline)
- Citing numbers in a report (caller counts, fan-in) → count `find_references` results; grep-estimated counts drift
- Several symbols to look up → one batched call (`symbolNames=["A","B","C"]`), not N calls

Razor caveat, both directions: roz does not see `.razor`/`.cshtml`/`@code` (hence rule 2),
and `get_diagnostics` may report phantom errors near Razor — `dotnet build` is the backstop there.

## Packaged workflows (MCP prompts)

User-invoked slash commands (Claude Code: `/mcp__roz__<name>`) — not tools; you cannot call
them. When a request matches one, suggest it, or follow the same safety steps yourself:

- `cleanup_dead_code` — find + safely remove unused symbols (blind-spot checks, public-API gate)
- `assess_impact` — blast radius of a proposed change; report-first
- `tighten_accessibility` — narrow over-broad accessibility, each step verified safe
- `fix_diagnostics` — fix compiler/analyzer diagnostics at root cause, no blanket suppression
- `check_breaking_changes` — diff public surface vs a git ref; classify source/binary/behavioral breaks (read-only)
- `decompile_symbol` — explain a BCL/NuGet symbol from real source or decompile (read-only)
- `triage_coverage` — map uncovered lines to symbols; dead code vs genuine test gaps
- `triage_complexity` — rank complexity/debt hotspots; route each to the right remedy
- `trim_dependencies` — find + remove unused project/package references, build-verified
- `assess_upgrade` — NuGet upgrade risk mapped onto actual call sites; report-first

`cleanup_dead_code`, `tighten_accessibility`, and `fix_diagnostics` apply edits through the
editing tools (`--tools=all` or `edit`); without them they hand back a verified change plan
instead. `trim_dependencies` and `assess_upgrade` edit via the dotnet CLI, confirmation-gated.
