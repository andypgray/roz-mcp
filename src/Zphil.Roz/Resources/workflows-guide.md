# roz tool routing and packaged workflows

This guide is the on-demand companion to the `# roz-mcp` section that `roz-mcp setup` writes into a
project's rules file. Read it when choosing between roz tools for a C# symbol question, or when a
request matches one of the packaged workflows below.

## Which tool answers which question

- Who calls this / where is it used → `find_references` (`referenceKinds=invocations` = callers only)
- Understand a method end-to-end — signature, inbound callers, outbound callees grouped by target →
  `analyze_method` (one call instead of find_references + reading each callee; `includeExternalCalls=true`
  promotes the collapsed BCL/NuGet summary to rows)
- What implements / overrides / derives from this → `find_implementations` (also reports DI registrations)
- What breaks if I change this (type, signature, accessibility, removal) → `analyze_change_impact`
  (per-site Compatible / RequiresUpdate / Unsafe verdicts)
- Base/derived/interface shape of a type → `get_type_hierarchy`; a type's members → `get_symbols_overview`
- Where is this defined → `find_symbol` (name or FQN; resolves BCL/NuGet types too) or `go_to_definition` (cursor)
- Compile/analyzer state after editing → `get_diagnostics` (`incremental=true` = only new since baseline)
- Citing numbers in a report (caller counts, fan-in) → count `find_references` results; grep-estimated counts drift
- Several symbols to look up → one batched call (`symbolNames=["A","B","C"]`), not N calls

## Packaged workflows (MCP prompts)

User-invoked slash commands — `/mcp__roz__<name>` in a direct Claude Code install;
plugin installs prefix the name differently. Not tools; you cannot call them. When a
request matches one, suggest it, or follow the same safety steps yourself:

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
