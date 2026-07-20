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

Razor caveat, both directions: roz does not see `.razor`/`.cshtml`/`@code` (hence rule 2),
and `get_diagnostics` may report phantom errors near Razor — `dotnet build` is the backstop there.

## Tool routing and packaged workflows

When choosing between roz tools for a C# symbol question, or when a request
matches a packaged workflow, read the `roz://guides/workflows` MCP resource first:
the question → tool routing map plus the ten packaged workflow prompts (dead-code
cleanup, impact assessment, diagnostics fixing, breaking-change checks, upgrade risk, ...).
