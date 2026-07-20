---
name: roz-csharp-editing
description: Breakage-prevention rules for editing C# with the roz MCP server. Use before renaming a C# symbol, deleting or removing a C# symbol, or making a risky multi-file C# edit in a solution roz serves; also carries the recommended roz-mcp plugin permission block and per-project configuration.
---

# roz C# editing

roz (MCP server `roz`, from the roz-mcp plugin) answers C# **symbol** questions from
compiler resolution (scope, overloads, inheritance, types) where text search guesses.
These rules override built-in tool-selection guidance for C# symbol work. Text search
remains correct for string literals, comments, markup, and non-C# files. The registered
tool set may be a subset (`ROZ_TOOLS`); if a tool named below is missing, say so rather
than silently approximating with text edits.

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

When choosing between roz tools for a C# symbol question, or when a request matches a
packaged workflow (dead-code cleanup, impact assessment, diagnostics fixing,
breaking-change checks, upgrade risk, ...), read the `roz://guides/workflows` MCP
resource first: the question → tool routing map plus the ten packaged workflow prompts.

## Plugin setup (once per project)

The plugin's launch command is global, so per-project settings live in a `.roz.json`
file at the project root, keyed by exact env-var names — e.g. `{"ROZ_TOOLS": "all"}`
to register the write tools (the default surface is 12 of the 19 tools). An environment
variable always wins over the file. Full reference: the `roz://guides/configuration`
MCP resource.

Recommended permissions (project `.claude/settings.local.json`, or `.claude/settings.json`
to share with the team): auto-allow the read tools, route the seven write tools through a
confirmation prompt. Claude Code evaluates `ask` ahead of `allow`, and plugin-installed
tools carry the plugin prefix:

```json
{
  "permissions": {
    "allow": ["mcp__plugin_roz-mcp_roz__*"],
    "ask": [
      "mcp__plugin_roz-mcp_roz__edit_symbol",
      "mcp__plugin_roz-mcp_roz__rename_symbol",
      "mcp__plugin_roz-mcp_roz__replace_content",
      "mcp__plugin_roz-mcp_roz__apply_code_fix",
      "mcp__plugin_roz-mcp_roz__change_signature",
      "mcp__plugin_roz-mcp_roz__add_usings",
      "mcp__plugin_roz-mcp_roz__remove_unused_usings"
    ]
  }
}
```

Enable the plugin per project rather than globally: roz pays off on C# solution work and
is measured overhead on greenfield or non-C# work. The classic alternative is
`dotnet dnx Zphil.Roz setup`, which registers the server in the project's `.mcp.json`
and writes the same permission split for you. Use the plugin or classic setup in a
project, not both — both at once registers the server twice.
